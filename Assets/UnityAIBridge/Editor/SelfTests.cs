using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    /// <summary>
    /// In-editor self tests. They run against the real project and clean up after
    /// themselves; results are written to Library/UnityAIBridge/test-results.json.
    /// </summary>
    public static partial class SelfTests
    {
        public const string TestFolder = "Assets/__UAIBTests__";

        static List<JsonValue> _results;
        static int _passed;
        static int _failed;
        static IEnumerator _routine;
        static float _startedAt;
        static int _savedAccessLevel;
        static bool _savedAutoApply;
        static readonly List<string> _testChangeIds = new List<string>();

        public static bool Running { get; private set; }

        public static void Start()
        {
            if (Running)
            {
                BridgeLog.Warn("tests", "Self tests are already running.");
                return;
            }

            Running = true;
            _results = new List<JsonValue>();
            _passed = 0;
            _failed = 0;
            _testChangeIds.Clear();
            _startedAt = Time.realtimeSinceStartup;

            ChatService.SuspendBackgroundWork();
            _savedAccessLevel = BridgeSettings.Current.AccessLevel;
            _savedAutoApply = BridgeSettings.Current.AutoApplyWithWriteAccess;
            AssetDatabase.DisallowAutoRefresh();

            BridgeSettings.Current.AccessLevel = (int)BridgeSettings.AccessMode.ProjectWrite;
            BridgeSettings.Current.AutoApplyWithWriteAccess = false;

            SafeDeleteFolder();
            EnsureFolder();

            BridgeLog.Info("tests", "Self tests started (auto-refresh paused).");
            _routine = RunAll();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        /// <summary>
        /// Entry point for Unity batch mode (-executeMethod UnityAIBridge.SelfTests.RunBatch).
        /// Drives the coroutine manually because EditorApplication.update does not tick in batch mode.
        /// </summary>
        public static void RunBatch()
        {
            Start();
            DateTime deadline = DateTime.UtcNow.AddMinutes(4);
            while (Running && DateTime.UtcNow < deadline)
            {
                if (_routine == null) break;
                bool more;
                try { more = _routine.MoveNext(); }
                catch (Exception e) { Check("runner", false, "Unhandled exception: " + e); Finish(); break; }
                if (!more) { Finish(); break; }
                MainThread.PumpNow();
                System.Threading.Thread.Sleep(10);
            }
            if (Running)
            {
                Check("runner", false, "Batch self-test run exceeded the deadline.");
                Finish();
            }
            Debug.Log("[Unity AI Bridge] Batch self tests complete: " + _passed + " passed, " + _failed + " failed.");
        }

        static void Tick()
        {
            if (!Running) return;
            if (Time.realtimeSinceStartup - _startedAt > 240f)
            {
                Check("runner", false, "Self-test suite exceeded the 240 second safety limit.");
                Finish();
                return;
            }

            try
            {
                if (_routine == null || !_routine.MoveNext())
                {
                    Finish();
                }
            }
            catch (Exception e)
            {
                Check("runner", false, "Unhandled exception: " + e);
                Finish();
            }
        }

        static void Check(string name, bool ok, string detail = null)
        {
            if (ok) _passed++; else _failed++;
            _results.Add(JsonValue.Obj("name", name, "ok", ok, "detail", detail ?? string.Empty));
            if (ok) BridgeLog.Success("tests", "PASS " + name);
            else BridgeLog.Error("tests", "FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail));
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.CreateFolder("Assets", "__UAIBTests__");
        }

        static void SafeDeleteFolder()
        {
            try
            {
                if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
                string full = BridgePaths.ToAbsolute(TestFolder);
                if (Directory.Exists(full)) Directory.Delete(full, true);
                if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
            }
            catch (Exception e)
            {
                BridgeLog.Warn("tests", "Could not clean the test folder: " + e.Message);
            }
        }

        static IEnumerator RunAll()
        {
            yield return null;

            // 1. JSON
            try
            {
                var parsed = JsonValue.Parse("{\"a\":[1,true,\"x\\n\"],\"b\":null}");
                bool ok = parsed["a"].Count == 3 && Math.Abs(parsed["a"][0].AsNumber() - 1) < 0.0001 &&
                          parsed["a"][1].AsBool() && parsed["a"][2].AsString() == "x\n" && parsed["b"].IsNull &&
                          JsonValue.Parse(parsed.ToJson(true))["a"][2].AsString() == "x\n";
                Check("json_roundtrip", ok, ok ? null : "JSON parse/serialize mismatch");
            }
            catch (Exception e) { Check("json_roundtrip", false, e.Message); }

            // 2. Diff
            try
            {
                string diff = DiffUtil.Unified("one\ntwo\nthree\n", "one\nTWO\nthree\n", "a/x", "b/x");
                bool ok = diff.IndexOf("-two", StringComparison.Ordinal) >= 0 && diff.IndexOf("+TWO", StringComparison.Ordinal) >= 0;
                Check("diff_unified", ok, ok ? DiffUtil.Summary(diff) : diff);
            }
            catch (Exception e) { Check("diff_unified", false, e.Message); }

            // 3. Read an existing script
            try
            {
                string error;
                string text = FileService.ReadText("Assets/UnityAIBridge/Editor/Ai/AgentRunner.cs", out error);
                Check("read_existing_script", text != null && text.IndexOf("class AgentRunner", StringComparison.Ordinal) >= 0,
                    text == null ? error : null);
            }
            catch (Exception e) { Check("read_existing_script", false, e.Message); }

            // 4. Path traversal / sensitive files
            try
            {
                string full;
                bool rejectedOutside = BridgePaths.TryResolve("../outside.txt", out full, out _) == false;
                bool rejectedAbsolute = BridgePaths.TryResolve("/etc/passwd", out full, out _) == false;
                bool rejectedGit = BridgePaths.TryResolve(".git/config", out full, out _) == false;
                bool allowedReadme = BridgePaths.TryResolve("Assets/TutorialInfo/Editor/ReadmeEditor.cs", out full, out _);
                Check("path_traversal_rejected", rejectedOutside && rejectedAbsolute && rejectedGit && allowedReadme,
                    "outside=" + rejectedOutside + " absolute=" + rejectedAbsolute + " git=" + rejectedGit + " allowed=" + allowedReadme);
            }
            catch (Exception e) { Check("path_traversal_rejected", false, e.Message); }

            // 5. Symlink escape (Linux/macOS)
#if !UNITY_EDITOR_WIN
            try
            {
                EnsureFolder();
                string link = Path.Combine(BridgePaths.ToAbsolute(TestFolder), "escape");
                if (Directory.Exists(link) || File.Exists(link)) File.Delete(link);
                int result = symlink("/etc", link);
                string detail;
                if (result == 0)
                {
                    string full;
                    bool rejected = BridgePaths.TryResolve(TestFolder + "/escape/passwd", out full, out detail) == false;
                    Check("symlink_escape_rejected", rejected, rejected ? null : "Resolved to " + full);
                    try { File.Delete(link); } catch { }
                }
                else
                {
                    Check("symlink_escape_rejected", true, "Skipped: could not create a symlink in this environment.");
                }
            }
            catch (Exception e) { Check("symlink_escape_rejected", false, e.Message); }
#else
            Check("symlink_escape_rejected", true, "Skipped on Windows.");
#endif

            // 6. Read Only blocks writes
            try
            {
                BridgeSettings.Current.AccessLevel = (int)BridgeSettings.AccessMode.ReadOnly;
                bool rejected = false;
                try { ChangeService.StageFile(ChangeKinds.CreateFile, TestFolder + "/blocked.txt", "nope", "selftest"); }
                catch (BridgeException) { rejected = true; }
                Check("readonly_rejects_write", rejected);
            }
            catch (Exception e) { Check("readonly_rejects_write", false, e.Message); }
            finally { BridgeSettings.Current.AccessLevel = (int)BridgeSettings.AccessMode.ProjectWrite; }

            // 7. Create / modify / backup a text file
            try
            {
                string path = TestFolder + "/hello.txt";
                var create = ChangeService.StageFile(ChangeKinds.CreateFile, path, "hello bridge\n", "selftest");
                _testChangeIds.Add(create.Id);
                string approveError = ChangeService.Approve(create.Id);
                string applyError = approveError ?? ChangeService.Apply(create.Id);
                string readError;
                string text = FileService.ReadText(path, out readError);
                Check("file_create", string.IsNullOrEmpty(approveError) && string.IsNullOrEmpty(applyError) && text == "hello bridge\n",
                    applyError ?? readError ?? text);

                var modify = ChangeService.StageFile(ChangeKinds.ModifyFile, path, "hello bridge\nsecond line\n", "selftest");
                _testChangeIds.Add(modify.Id);
                bool hasDiff = !string.IsNullOrEmpty(modify.Diff) && modify.Diff.IndexOf("+second line", StringComparison.Ordinal) >= 0;
                ChangeService.Approve(modify.Id);
                string modifyError = ChangeService.Apply(modify.Id);
                string modified = FileService.ReadText(path, out readError);
                string backup = File.Exists(BridgePaths.GetFullPathSafe(path)) ? FileService.BackupFile(BridgePaths.GetFullPathSafe(path), "selftest") : null;
                bool modifyOk = hasDiff && string.IsNullOrEmpty(modifyError) && modified != null &&
                                modified.IndexOf("second line", StringComparison.Ordinal) >= 0 && !string.IsNullOrEmpty(backup);
                Check("file_modify_and_diff", modifyOk, modifyOk ? DiffUtil.Summary(modify.Diff) : (modifyError ?? readError ?? "diff/backup missing"));
            }
            catch (Exception e) { Check("file_create", false, e.Message); }

            // 8. Unapproved change cannot be applied
            try
            {
                string path = TestFolder + "/unapproved.txt";
                var change = ChangeService.StageFile(ChangeKinds.CreateFile, path, "should not exist", "selftest");
                _testChangeIds.Add(change.Id);
                string error = ChangeService.Apply(change.Id);
                bool blocked = !string.IsNullOrEmpty(error) && !File.Exists(BridgePaths.GetFullPathSafe(path));
                ChangeService.Reject(change.Id);
                Check("unauthorized_edit_rejected", blocked, error);
            }
            catch (Exception e) { Check("unauthorized_edit_rejected", false, e.Message); }

            // 9. Script create/modify (file level, no recompile during tests)
            try
            {
                EnsureFolder();
                string path = TestFolder + "/SampleBehaviour.cs";
                string content = "using UnityEngine;\n\npublic class SampleBehaviour : MonoBehaviour\n{\n    public int value = 1;\n}\n";
                var change = ChangeService.StageFile(ChangeKinds.CreateFile, path, content, "selftest");
                _testChangeIds.Add(change.Id);
                bool diffOk = !string.IsNullOrEmpty(change.Diff) && change.Diff.IndexOf("+public class SampleBehaviour", StringComparison.Ordinal) >= 0;
                string error;
                string written = FileService.WriteTextAtomic(path, content, out error);
                string readBack = FileService.ReadText(path, out error);
                string modified = content.Replace("value = 1", "value = 250");
                FileService.WriteTextAtomic(path, modified, out error);
                string modifiedBack = FileService.ReadText(path, out error);
                if (File.Exists(written)) File.Delete(written);
                string meta = written + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
                ChangeService.Reject(change.Id);
                bool scriptOk = diffOk && written != null && readBack != null && readBack.IndexOf("SampleBehaviour", StringComparison.Ordinal) >= 0 &&
                                modifiedBack != null && modifiedBack.IndexOf("250", StringComparison.Ordinal) >= 0;
                Check("script_create_modify", scriptOk, scriptOk ? "created, modified and removed without a domain reload" : (error ?? "script diff/readback failed"));
            }
            catch (Exception e) { Check("script_create_modify", false, e.Message); }

            // 10. Import a generated PNG as a sprite
            try
            {
                EnsureFolder();
                string png = Path.Combine(BridgePaths.TempDir, "test_texture.png");
                WriteTestPng(png, 64, 64);
                var parameters = JsonValue.Obj(
                    "incoming_path", png,
                    "sprite_mode", "Single",
                    "pixels_per_unit", 64,
                    "filter", "Bilinear",
                    "compression", "Compressed",
                    "max_size", 128);
                string assetPath = TestFolder + "/test_texture.png";
                var change = ChangeService.Stage(ChangeKinds.ImportSprite, "Self test sprite", assetPath, null, parameters, "selftest", false);
                _testChangeIds.Add(change.Id);
                ChangeService.Approve(change.Id);
                string error = ChangeService.Apply(change.Id);
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                bool ok = string.IsNullOrEmpty(error) && importer != null && importer.textureType == TextureImporterType.Sprite &&
                          Math.Abs(importer.spritePixelsPerUnit - 64f) < 0.01f && sprite != null &&
                          Math.Abs(sprite.rect.width - 64f) < 0.01f;
                Check("sprite_import", ok, error ?? "importer=" + (importer != null ? importer.textureType + " ppu=" + importer.spritePixelsPerUnit : "null"));

                if (ok)
                {
                    var sliceChange = ChangeService.Stage(ChangeKinds.SliceSprite, "Self test slice", assetPath, null, JsonValue.Obj("rows", 4, "columns", 4), "selftest", false);
                    _testChangeIds.Add(sliceChange.Id);
                    ChangeService.Approve(sliceChange.Id);
                    string sliceError = ChangeService.Apply(sliceChange.Id);
                    var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                    int sprites = 0;
                    foreach (var item in all) if (item is Sprite) sprites++;
                    bool sliceOk = string.IsNullOrEmpty(sliceError) && sprites == 16;
                    Check("sprite_slicing", sliceOk, sliceError ?? ("sprites=" + sprites));
                }
                else Check("sprite_slicing", false, "skipped because the base import failed");

                if (ok)
                {
                    string guidBefore = AssetDatabase.AssetPathToGUID(assetPath);
                    string movedPath = TestFolder + "/test_texture_moved.png";
                    var moveChange = ChangeService.Stage(ChangeKinds.MoveFile, "Self test move", movedPath, null, JsonValue.Obj("from", assetPath), "selftest", true);
                    _testChangeIds.Add(moveChange.Id);
                    ChangeService.Approve(moveChange.Id, true);
                    string moveError = ChangeService.Apply(moveChange.Id);
                    string guidAfter = AssetDatabase.AssetPathToGUID(movedPath);
                    bool moveOk = string.IsNullOrEmpty(moveError) && !string.IsNullOrEmpty(guidAfter) && guidAfter == guidBefore;
                    Check("sprite_move_preserves_guid", moveOk, moveError ?? (guidBefore == guidAfter ? "guid preserved" : "GUID changed"));
                }
                else Check("sprite_move_preserves_guid", false, "skipped because the base import failed");
            }
            catch (Exception e) { Check("sprite_import", false, e.Message); }

            // 11. Material creation
            try
            {
                EnsureFolder();
                string assetPath = TestFolder + "/TestMaterial.mat";
                var parameters = JsonValue.Obj("color", "#FF0000");
                var change = ChangeService.Stage(ChangeKinds.CreateMaterial, "Self test material", assetPath, null, parameters, "selftest", false);
                _testChangeIds.Add(change.Id);
                ChangeService.Approve(change.Id);
                string error = ChangeService.Apply(change.Id);
                var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                bool ok = string.IsNullOrEmpty(error) && material != null && material.shader != null;
                if (ok && material.HasProperty("_BaseColor"))
                {
                    var color = material.GetColor("_BaseColor");
                    ok = color.r > 0.9f && color.g < 0.1f && color.b < 0.1f;
                }
                Check("material_create", ok, error ?? (material == null ? "material missing" : material.shader.name));
            }
            catch (Exception e) { Check("material_create", false, e.Message); }

            // 12. GameObject + Undo
            try
            {
                var parameters = JsonValue.Obj("name", "__UAIBTestGO", "primitive", "cube", "position", JsonValue.Arr(1, 2, 3));
                var change = ChangeService.Stage(ChangeKinds.CreateGameObject, "Self test object", string.Empty, null, parameters, "selftest", false);
                _testChangeIds.Add(change.Id);
                ChangeService.Approve(change.Id);
                string error = ChangeService.Apply(change.Id);
                var go = SceneService.FindByPath("__UAIBTestGO");
                bool created = go != null && Math.Abs(go.transform.localPosition.x - 1f) < 0.01f;
                if (go != null)
                {
                    Undo.PerformUndo();
                    var afterUndo = SceneService.FindByPath("__UAIBTestGO");
                    Check("gameobject_create_and_undo", string.IsNullOrEmpty(error) && created && afterUndo == null,
                        error ?? ("created=" + created + " undone=" + (afterUndo == null)));
                    if (afterUndo != null) UnityEngine.Object.DestroyImmediate(afterUndo);
                }
                else Check("gameobject_create_and_undo", false, error ?? "object not created");
            }
            catch (Exception e) { Check("gameobject_create_and_undo", false, e.Message); }

            // 13. Component add + property update
            try
            {
                var createParams = JsonValue.Obj("name", "__UAIBCompGO", "primitive", "empty");
                var createChange = ChangeService.Stage(ChangeKinds.CreateGameObject, "Self test component object", string.Empty, null, createParams, "selftest", false);
                _testChangeIds.Add(createChange.Id);
                ChangeService.Approve(createChange.Id);
                string error = ChangeService.Apply(createChange.Id);

                var addParams = JsonValue.Obj("target", "__UAIBCompGO", "component", "SpriteRenderer");
                var addChange = ChangeService.Stage(ChangeKinds.AddComponent, "Add SpriteRenderer", "__UAIBCompGO", null, addParams, "selftest", false);
                _testChangeIds.Add(addChange.Id);
                ChangeService.Approve(addChange.Id);
                error = error ?? ChangeService.Apply(addChange.Id);

                var updateParams = JsonValue.Obj("target", "__UAIBCompGO", "component", "SpriteRenderer", "property", "m_SortingOrder", "value", 7);
                var updateChange = ChangeService.Stage(ChangeKinds.SetComponentProperty, "Set sorting order", "__UAIBCompGO", null, updateParams, "selftest", false);
                _testChangeIds.Add(updateChange.Id);
                ChangeService.Approve(updateChange.Id);
                error = error ?? ChangeService.Apply(updateChange.Id);

                var go = SceneService.FindByPath("__UAIBCompGO");
                var renderer = go != null ? go.GetComponent<SpriteRenderer>() : null;
                bool ok = string.IsNullOrEmpty(error) && renderer != null && renderer.sortingOrder == 7;
                Check("component_add_and_update", ok, error ?? (renderer == null ? "component missing" : "order=" + renderer.sortingOrder));
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            catch (Exception e) { Check("component_add_and_update", false, e.Message); }

            // 14. Prefab creation
            try
            {
                var go = new GameObject("__UAIBPrefabSrc");
                Undo.RegisterCreatedObjectUndo(go, "self test prefab source");
                string prefabPath = TestFolder + "/TestPrefab.prefab";
                var parameters = JsonValue.Obj("target", "__UAIBPrefabSrc", "keep_in_scene", true);
                var change = ChangeService.Stage(ChangeKinds.CreatePrefab, "Self test prefab", prefabPath, null, parameters, "selftest", false);
                _testChangeIds.Add(change.Id);
                ChangeService.Approve(change.Id);
                string error = ChangeService.Apply(change.Id);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Check("prefab_create", string.IsNullOrEmpty(error) && prefab != null, error ?? (prefab == null ? "prefab asset missing" : prefab.name));
                UnityEngine.Object.DestroyImmediate(go);
            }
            catch (Exception e) { Check("prefab_create", false, e.Message); }

            // 15. Console diagnostics reader
            try
            {
                var errors = ConsoleService.GetErrors(10, false);
                string prompt = ConsoleService.ForPrompt(5);
                Check("console_errors_readable", errors != null && !string.IsNullOrEmpty(prompt));
            }
            catch (Exception e) { Check("console_errors_readable", false, e.Message); }

            // 16. Invalid sprite source is rejected
            try
            {
                bool rejected = false;
                try
                {
                    var parameters = JsonValue.Obj("incoming_path", "/etc/hosts");
                    var change = ChangeService.Stage(ChangeKinds.ImportSprite, "Bad import", TestFolder + "/bad.png", null, parameters, "selftest", false);
                    _testChangeIds.Add(change.Id);
                    ChangeService.Approve(change.Id);
                    string error = ChangeService.Apply(change.Id);
                    rejected = !string.IsNullOrEmpty(error);
                }
                catch (BridgeException) { rejected = true; }
                Check("invalid_sprite_source_rejected", rejected);
            }
            catch (Exception e) { Check("invalid_sprite_source_rejected", false, e.Message); }

            // 17. MCP HTTP round trip (initialize, tools/list, tools/call, auth rejection)
            var mcpTest = RunMcpTest();
            while (mcpTest.MoveNext()) yield return mcpTest.Current;

            // 18. Provider connection failure is handled gracefully
            var providerTest = RunProviderFailureTest();
            while (providerTest.MoveNext()) yield return providerTest.Current;

            // 19. Provider factory / ids
            try
            {
                bool ok = true;
                foreach (var id in new[] { BridgeSettings.ProviderChatGpt, BridgeSettings.ProviderDeepSeek, BridgeSettings.ProviderOpenCode, BridgeSettings.ProviderOpenAi, BridgeSettings.ProviderOllama })
                {
                    var provider = ProviderFactory.Create(id);
                    if (provider == null || provider.Id != id) ok = false;
                }
                Check("provider_factory", ok);
            }
            catch (Exception e) { Check("provider_factory", false, e.Message); }

            // 20. PKCE (RFC 7636 appendix B vector)
            try
            {
                string challenge = ChatGptAuth.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
                Check("chatgpt_pkce", challenge == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
            }
            catch (Exception e) { Check("chatgpt_pkce", false, e.Message); }

            // 21. Authorization URL contains the required OAuth parameters
            try
            {
                string savedAuthBase = ChatGptAuth.AuthBaseUrl;
                ChatGptAuth.AuthBaseUrl = ChatGptAuth.DefaultAuthBaseUrl;
                string url = ChatGptAuth.BuildAuthorizationUrl("oaiapp_test", "http://127.0.0.1:1455/auth/callback", "state1", "nonce1", "challenge1", "urn:uuid:host1", null, null, false, true);
                bool ok = url.IndexOf("client_id=oaiapp_test", StringComparison.Ordinal) >= 0 &&
                          url.IndexOf("resource=https%3A%2F%2Fapi.openai.com%2Fv1", StringComparison.Ordinal) >= 0 &&
                          url.IndexOf("chatgpt.tokens.use.direct", StringComparison.Ordinal) >= 0 &&
                          url.IndexOf("code_challenge_method=S256", StringComparison.Ordinal) >= 0 &&
                          url.IndexOf("ext_agent_host_id=urn%3Auuid%3Ahost1", StringComparison.Ordinal) >= 0 &&
                          url.IndexOf("agent_name_hint=Unity%20AI%20Bridge", StringComparison.Ordinal) >= 0;
                ChatGptAuth.AuthBaseUrl = savedAuthBase;
                Check("chatgpt_authorize_url", ok, url.Length > 600 ? url.Substring(0, 600) : url);
            }
            catch (Exception e) { Check("chatgpt_authorize_url", false, e.Message); }

            // 22. ID token signature/nonce/audience validation
            try
            {
                using (var rsa = RSA.Create())
                {
                    try { rsa.KeySize = 2048; } catch { }
                    var parameters = rsa.ExportParameters(false);
                    string header = JsonValue.Obj("alg", "RS256", "kid", "testkid", "typ", "JWT").ToJson(false);
                    string payload = JsonValue.Obj(
                        "iss", ChatGptAuth.DefaultAuthBaseUrl,
                        "aud", "oaiapp_test",
                        "nonce", "nonce1",
                        "sub", "subject1",
                        "email", "user@example.com",
                        "exp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600).ToJson(false);
                    string signingInput = ChatGptAuth.Base64UrlEncode(Encoding.UTF8.GetBytes(header)) + "." + ChatGptAuth.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
                    byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    string jwt = signingInput + "." + ChatGptAuth.Base64UrlEncode(signature);
                    string jwks = JsonValue.Obj("keys", JsonValue.Arr(JsonValue.Obj(
                        "kty", "RSA",
                        "kid", "testkid",
                        "n", ChatGptAuth.Base64UrlEncode(parameters.Modulus),
                        "e", ChatGptAuth.Base64UrlEncode(parameters.Exponent)))).ToJson(false);

                    string error;
                    string email;
                    string subject;
                    bool valid = ChatGptAuth.ValidateIdToken(jwt, jwks, "nonce1", "oaiapp_test", ChatGptAuth.DefaultAuthBaseUrl, out error, out email, out subject);
                    string validEmail = email;
                    string validSubject = subject;
                    string ignoredError, ignoredEmail, ignoredSubject;
                    bool badNonce = ChatGptAuth.ValidateIdToken(jwt, jwks, "wrong", "oaiapp_test", ChatGptAuth.DefaultAuthBaseUrl, out ignoredError, out ignoredEmail, out ignoredSubject);
                    string tampered = jwt.Substring(0, jwt.Length - 2) + (jwt.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
                    bool badSignature = ChatGptAuth.ValidateIdToken(tampered, jwks, "nonce1", "oaiapp_test", ChatGptAuth.DefaultAuthBaseUrl, out ignoredError, out ignoredEmail, out ignoredSubject);
                    Check("chatgpt_id_token_validation", valid && !badNonce && !badSignature && validEmail == "user@example.com" && validSubject == "subject1",
                        "valid=" + valid + " badNonce=" + badNonce + " badSignature=" + badSignature + " email=" + validEmail + " subject=" + validSubject + " error=" + error);
                }
            }
            catch (Exception e) { Check("chatgpt_id_token_validation", false, e.Message); }

            var prefabRegressions = RunPrefabRegressionTests();
            while (prefabRegressions.MoveNext()) yield return prefabRegressions.Current;

            var approvals = RunApprovalTests();
            while (approvals.MoveNext()) yield return approvals.Current;
            var autoAccept = RunConversationAutoAcceptTests();
            while (autoAccept.MoveNext()) yield return autoAccept.Current;

            var reliability = RunChatReliabilityTests();
            while (reliability.MoveNext()) yield return reliability.Current;
            var streamTimeouts = RunStreamTimeoutTests();
            while (streamTimeouts.MoveNext()) yield return streamTimeouts.Current;
            RunActivityTests();
            RunAnimationTests();
            RunAdvancedAnimationTests();
            var images = RunImageGenerationTests();
            while (images.MoveNext()) yield return images.Current;
            var activityTransport = RunActivityTransportTests();
            while (activityTransport.MoveNext()) yield return activityTransport.Current;

            // 23. DeepSeek streaming, tool calls, usage and cost
            var deepSeekTest = RunDeepSeekStreamTest();
            while (deepSeekTest.MoveNext()) yield return deepSeekTest.Current;

            // 24. OpenAI Responses API streaming + tool call
            var openAiTest = RunOpenAiStreamTest(false);
            while (openAiTest.MoveNext()) yield return openAiTest.Current;

            // 25. ChatGPT OAuth route uses additional_tools and no unsupported fields
            var chatGptTest = RunOpenAiStreamTest(true);
            while (chatGptTest.MoveNext()) yield return chatGptTest.Current;

            // 26. ChatGPT refresh-token exchange
            var refreshTest = RunChatGptRefreshTest();
            while (refreshTest.MoveNext()) yield return refreshTest.Current;

            // 27. OpenCode Go streaming (models resolution, chat completions, tools, usage)
            var openCodeTest = RunOpenCodeStreamTest();
            while (openCodeTest.MoveNext()) yield return openCodeTest.Current;

            // 28. MCP installer: JSON merge (existing servers preserved)
            try
            {
                string path = Path.Combine(BridgePaths.TempDir, "mcp-install-json.json");
                File.WriteAllText(path, "{\"mcpServers\":{\"existing\":{\"url\":\"https://example.invalid/mcp\"}}}\n");
                var client = new McpClient { Id = "testjson", Name = "Test JSON", Kind = McpClientKind.JsonMcpServers, Path = path };
                string installError = McpInstaller.Install(client);
                var json = JsonValue.Parse(File.ReadAllText(path));
                string detail;
                var state = McpInstaller.GetState(client, out detail);
                bool ok = string.IsNullOrEmpty(installError) &&
                          json["mcpServers"]["existing"]["url"].AsString() == "https://example.invalid/mcp" &&
                          json["mcpServers"][McpInstaller.ServerName]["url"].AsString() == McpInstaller.ServerUrl() &&
                          state == McpInstallState.Installed;
                string removeError = McpInstaller.Uninstall(client);
                var after = JsonValue.Parse(File.ReadAllText(path));
                bool removed = string.IsNullOrEmpty(removeError) &&
                               after["mcpServers"][McpInstaller.ServerName].IsObject == false &&
                               after["mcpServers"]["existing"].IsObject;
                Check("mcp_installer_json_merge", ok && removed, installError ?? removeError ?? "merge/remove failed");
                File.Delete(path);
            }
            catch (Exception e) { Check("mcp_installer_json_merge", false, e.Message); }

            // 29. MCP installer: Codex TOML section merge
            try
            {
                string path = Path.Combine(BridgePaths.TempDir, "mcp-install-codex.toml");
                File.WriteAllText(path, "model = \"test\"\n\n[other]\nvalue = 1\n");
                var client = new McpClient { Id = "testcodex", Name = "Test Codex", Kind = McpClientKind.CodexToml, Path = path };
                string installError = McpInstaller.Install(client);
                string text = File.ReadAllText(path);
                string detail;
                var state = McpInstaller.GetState(client, out detail);
                bool ok = string.IsNullOrEmpty(installError) &&
                          text.IndexOf("[mcp_servers.unity-ai-bridge]", StringComparison.Ordinal) >= 0 &&
                          text.IndexOf("[other]", StringComparison.Ordinal) >= 0 &&
                          text.IndexOf("http_headers", StringComparison.Ordinal) >= 0 &&
                          state == McpInstallState.Installed;
                string removeError = McpInstaller.Uninstall(client);
                text = File.ReadAllText(path);
                bool removed = string.IsNullOrEmpty(removeError) &&
                               text.IndexOf("[mcp_servers.unity-ai-bridge]", StringComparison.Ordinal) < 0 &&
                               text.IndexOf("[other]", StringComparison.Ordinal) >= 0;
                Check("mcp_installer_codex_toml", ok && removed, installError ?? removeError ?? "toml merge/remove failed");
                File.Delete(path);
            }
            catch (Exception e) { Check("mcp_installer_codex_toml", false, e.Message); }

            // 30. MCP installer: OpenCode v2 mcp.servers layout
            try
            {
                string path = Path.Combine(BridgePaths.TempDir, "mcp-install-opencode.json");
                File.WriteAllText(path, "{\"mcp\":{\"servers\":{\"existing\":{\"type\":\"remote\",\"url\":\"https://example.invalid/mcp\"}}}}\n");
                var client = new McpClient { Id = "testopencode", Name = "Test OpenCode", Kind = McpClientKind.OpenCode, Path = path };
                string installError = McpInstaller.Install(client);
                var json = JsonValue.Parse(File.ReadAllText(path));
                string detail;
                var state = McpInstaller.GetState(client, out detail);
                bool ok = string.IsNullOrEmpty(installError) &&
                          json["mcp"]["servers"]["existing"]["url"].AsString() == "https://example.invalid/mcp" &&
                          json["mcp"]["servers"][McpInstaller.ServerName]["type"].AsString() == "remote" &&
                          state == McpInstallState.Installed;
                string removeError = McpInstaller.Uninstall(client);
                var after = JsonValue.Parse(File.ReadAllText(path));
                bool removed = string.IsNullOrEmpty(removeError) &&
                               after["mcp"]["servers"][McpInstaller.ServerName].IsObject == false &&
                               after["mcp"]["servers"]["existing"].IsObject;
                Check("mcp_installer_opencode_v2", ok && removed, installError ?? removeError ?? "opencode merge/remove failed");
                File.Delete(path);
            }
            catch (Exception e) { Check("mcp_installer_opencode_v2", false, e.Message); }

            // 31. MCP catalog: install a stdio server into a JSON client
            try
            {
                string path = Path.Combine(BridgePaths.TempDir, "mcp-catalog-stdio.json");
                File.WriteAllText(path, "{\"mcpServers\":{\"existing\":{\"url\":\"https://example.invalid/mcp\"}}}\n");
                var client = new McpClient { Id = "testcatalog", Name = "Test Catalog", Kind = McpClientKind.JsonMcpServers, Path = path };
                var spec = new McpServerSpec { Name = "playwright", IsStdio = true, Command = "npx", Args = new[] { "-y", "@playwright/mcp@latest" } };
                string error = McpCatalog.Install(client, spec);
                var json = JsonValue.Parse(File.ReadAllText(path));
                bool ok = string.IsNullOrEmpty(error) &&
                          json["mcpServers"]["existing"].IsObject &&
                          json["mcpServers"]["playwright"]["command"].AsString() == "npx" &&
                          json["mcpServers"]["playwright"]["args"][1].AsString() == "@playwright/mcp@latest" &&
                          McpCatalog.IsInstalled(client, "playwright", out _);
                string removeError = McpCatalog.Uninstall(client, "playwright");
                var after = JsonValue.Parse(File.ReadAllText(path));
                bool removed = string.IsNullOrEmpty(removeError) && after["mcpServers"]["playwright"].IsObject == false && after["mcpServers"]["existing"].IsObject;
                Check("mcp_catalog_stdio_json", ok && removed, error ?? removeError ?? "catalog stdio failed");
                File.Delete(path);
            }
            catch (Exception e) { Check("mcp_catalog_stdio_json", false, e.Message); }

            // 32. MCP catalog: install an OAuth HTTP server into OpenCode v2
            try
            {
                string path = Path.Combine(BridgePaths.TempDir, "mcp-catalog-oauth.json");
                File.WriteAllText(path, "{\"mcp\":{\"servers\":{\"existing\":{\"type\":\"remote\",\"url\":\"https://example.invalid/mcp\"}}}}\n");
                var client = new McpClient { Id = "testcatalog", Name = "Test Catalog", Kind = McpClientKind.OpenCode, Path = path };
                var spec = new McpServerSpec { Name = "github", IsStdio = false, Url = "https://api.githubcopilot.com/mcp/", OAuth = true };
                string error = McpCatalog.Install(client, spec);
                var json = JsonValue.Parse(File.ReadAllText(path));
                var entry = json["mcp"]["servers"]["github"];
                bool ok = string.IsNullOrEmpty(error) &&
                          json["mcp"]["servers"]["existing"].IsObject &&
                          entry["type"].AsString() == "remote" &&
                          entry["url"].AsString() == "https://api.githubcopilot.com/mcp/" &&
                          McpCatalog.IsInstalled(client, "github", out _);
                string removeError = McpCatalog.Uninstall(client, "github");
                var after = JsonValue.Parse(File.ReadAllText(path));
                bool removed = string.IsNullOrEmpty(removeError) && after["mcp"]["servers"]["github"].IsObject == false && after["mcp"]["servers"]["existing"].IsObject;
                Check("mcp_catalog_oauth_opencode", ok && removed, error ?? removeError ?? "catalog oauth failed");
                File.Delete(path);
            }
            catch (Exception e) { Check("mcp_catalog_oauth_opencode", false, e.Message); }

            RunMcpSyncTests();

            // 33. Tool loop stages a change, requires approval, then applies
            var loopTest = RunAgentToolLoopTest();
            while (loopTest.MoveNext()) yield return loopTest.Current;

            // 34. Paged history: 125 messages span three pages; only the latest page opens
            try
            {
                const string provider = "selftest-history";
                ChatHistory.ClearProvider(provider);
                var meta = ChatHistory.NewConversation(provider, "test-model", "History test");
                for (int i = 0; i < 125; i++)
                {
                    ChatHistory.Append(meta, new ChatMessage
                    {
                        Id = "m" + i,
                        Role = i % 2 == 0 ? "user" : "assistant",
                        Content = "message " + i + (i == 60 ? " needle" : ""),
                        TimeLocal = "12:00:00"
                    });
                }
                int oldest;
                var latest = ChatHistory.LoadLatestPage(meta, out oldest);
                int newOldest;
                var middle = ChatHistory.LoadOlderPage(meta, oldest, out newOldest);
                bool paged = latest.Count == 25 && oldest == 2 && middle != null && middle.Count == 50 && newOldest == 1;
                var matches = ChatHistory.List(provider, "needle");
                bool searched = matches.Count == 1 && matches[0].Id == meta.Id;
                ChatHistory.ClearConversation(meta);
                var empty = ChatHistory.LoadLatestPage(meta, out oldest);
                ChatHistory.Delete(meta);
                bool cleared = meta.MessageCount == 0 && meta.PageCount == 0 && empty.Count == 0 &&
                               !Directory.Exists(ChatHistory.Root(meta));
                Check("history_paged_lazy_search_clear", paged && searched && cleared,
                    "latest=" + latest.Count + " oldest=" + oldest + " middle=" + (middle == null ? -1 : middle.Count) +
                    " search=" + matches.Count + " cleared=" + cleared);
                ChatHistory.ClearProvider(provider);
            }
            catch (Exception e) { Check("history_paged_lazy_search_clear", false, e.Message); }

            // 35. Model context is capped separately from display history
            try
            {
                const string provider = "selftest-context";
                ChatHistory.ClearProvider(provider);
                var meta = ChatHistory.NewConversation(provider, "test-model", "Context test");
                var messages = new List<LlmMessage>();
                for (int i = 0; i < 200; i++) messages.Add(new LlmMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = "turn " + i });
                ChatHistory.SaveContext(meta, messages);
                var loaded = ChatHistory.LoadContext(meta);
                bool ok = loaded.Count == ChatHistory.ContextLimit && loaded[0].Content == "turn 80" && loaded[loaded.Count - 1].Content == "turn 199";
                Check("history_context_capped", ok, "count=" + loaded.Count + " first=" + (loaded.Count > 0 ? loaded[0].Content : "?"));
                ChatHistory.ClearProvider(provider);
            }
            catch (Exception e) { Check("history_context_capped", false, e.Message); }

            // 36. Markdown rendering and syntax highlighting (no GUI needed)
            try
            {
                var parent = new UnityEngine.UIElements.VisualElement();
                MarkdownView.Render(parent, "# Title\n\n**bold** and `inline` with <tag>\n\n```csharp\npublic class Demo { int x = 42; }\n```\n\n- one\n- two");
                string inline = MarkdownView.Inline("a < b & c");
                string highlighted = MarkdownView.Highlight("public class Demo { int x = 42; }", "csharp");
                bool ok = parent.childCount >= 4 && inline.IndexOf("&lt;", StringComparison.Ordinal) >= 0 &&
                          inline.IndexOf("&amp;", StringComparison.Ordinal) >= 0 && highlighted.IndexOf("<color=", StringComparison.Ordinal) >= 0;
                Check("markdown_and_syntax", ok, "blocks=" + parent.childCount + " inline=" + inline);
            }
            catch (Exception e) { Check("markdown_and_syntax", false, e.Message); }

            // 37. Chat panel can be constructed without a live provider
            try
            {
                var panel = new ChatPanel();
                bool ok = panel.childCount > 0 && panel.Q<Button>("chatgpt-connect") != null && panel.Q<TextField>("chat-composer") != null && panel.Q<DropdownField>("chat-mode") != null && panel.Q<DropdownField>("chat-reasoning") != null && panel.Q<Button>("chat-retry") != null;
                panel.Detach();
                Check("chat_panel_builds", ok, "children=" + panel.childCount);
            }
            catch (Exception e) { Check("chat_panel_builds", false, e.Message); }

            // 38. Full UI Toolkit shell builds in-memory
            try
            {
                var window = ScriptableObject.CreateInstance<UnityAIBridgeWindow>();
                int children = -1;
                try
                {
                    window.SelfTestCreateUi();
                    children = window.SelfTestRootChildren;
                }
                finally
                {
                    window.SelfTestTeardown();
                    UnityEngine.Object.DestroyImmediate(window);
                }
                Check("window_ui_shell_builds", children >= 3, "root children=" + children);
            }
            catch (Exception e) { Check("window_ui_shell_builds", false, e.ToString()); }

            // 39. Activity group with file cards renders without a panel
            try
            {
                var panel = new ChatPanel();
                var view = new ChatMessageView(panel);
                view.Bind(new ChatMessage
                {
                    Id = "activity-test",
                    Role = "activity",
                    TimeLocal = "12:00:00",
                    Activities = new List<ChatToolActivity>
                    {
                        new ChatToolActivity { CallId = "c1", Tool = "read_project_file", Status = "Done", Ok = true, Detail = "Read Assets/Test.cs" },
                        new ChatToolActivity { CallId = "c2", Tool = "create_script", Status = "Done", Ok = true, Detail = "Staged a change", ChangeIds = new List<string> { "chg1" } }
                    },
                    Files = new List<ChatFileRef>
                    {
                        new ChatFileRef { Path = "Assets/Test.cs", CallId = "c1", Status = "Read", IsRead = true, Title = "Test.cs" },
                        new ChatFileRef { Path = "Assets/New.cs", ChangeId = "chg1", Status = "Pending", Title = "New.cs" }
                    }
                });
                bool ok = view.childCount > 0;
                panel.Detach();
                Check("activity_group_file_cards", ok, "children=" + view.childCount);
            }
            catch (Exception e) { Check("activity_group_file_cards", false, e.ToString()); }

            // 39b. Activity foldout stays open while tool results update
            try
            {
                var panel = new ChatPanel();
                var view = new ChatMessageView(panel);
                var activity = new ChatMessage
                {
                    Id = "activity-foldout-test",
                    Role = "activity",
                    TimeLocal = "12:00:00",
                    Activities = new List<ChatToolActivity>
                    {
                        new ChatToolActivity { CallId = "c1", Tool = "read_project_file", Status = "Running", Ok = true, Detail = "Reading" }
                    }
                };

                view.Bind(activity);
                var foldout = view.Q<Foldout>();
                bool found = foldout != null;
                if (foldout != null) foldout.value = true;

                activity.Activities[0].Status = "Done";
                activity.Activities[0].Detail = "Read Assets/Test.cs";
                view.Bind(activity);

                var updated = view.Q<Foldout>();
                bool stillOpen = updated != null && updated.value;
                bool updatedText = updated != null && updated.text.IndexOf("done", StringComparison.OrdinalIgnoreCase) >= 0;
                panel.Detach();
                Check("activity_foldout_preserves_expansion", found && stillOpen && updatedText,
                    "found=" + found + " open=" + stillOpen + " text=" + (updated != null ? updated.text : "?"));
            }
            catch (Exception e) { Check("activity_foldout_preserves_expansion", false, e.ToString()); }

            // 40. History load benchmark at representative sizes
            try
            {
                var details = new List<string>();
                bool ok = true;
                foreach (int count in new[] { 100, 500, 1000 })
                {
                    string provider = "selftest-bench-" + count;
                    ChatHistory.ClearProvider(provider);
                    var meta = ChatHistory.NewConversation(provider, "bench", "Bench " + count);
                    for (int i = 0; i < count; i++)
                        ChatHistory.Append(meta, new ChatMessage { Id = "b" + i, Role = "user", Content = "benchmark message " + i });
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int latestCount = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        int ignored;
                        var page = ChatHistory.LoadLatestPage(meta, out ignored);
                        latestCount = page.Count;
                    }
                    sw.Stop();
                    double avg = sw.Elapsed.TotalMilliseconds / 10.0;
                    details.Add(count + "=" + avg.ToString("0.00") + "ms/page" + latestCount);
                    if (latestCount > ChatHistory.PageSize) ok = false;
                    ChatHistory.ClearProvider(provider);
                }
                Check("history_load_benchmark", ok, string.Join(" ", details.ToArray()));
            }
            catch (Exception e) { Check("history_load_benchmark", false, e.ToString()); }

            // 41. Tool step limit ends with a tool-free final answer instead of failing
            var limitProvider = new ScriptedProvider();
            limitProvider.Turns.Enqueue(new ProviderTurn
            {
                Text = string.Empty,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_limit", Name = "get_bridge_status", Arguments = "{}" } },
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
            });
            limitProvider.Turns.Enqueue(new ProviderTurn
            {
                Text = "Final answer after the tool limit.",
                Usage = new TokenUsage { PromptTokens = 20, CompletionTokens = 8 }
            });

            var limitTranscript = new List<LlmMessage>();
            var limitRequest = new LlmRequest
            {
                ProviderId = "scripted",
                Tools = McpTools.GetToolSpecs(true),
                Model = "scripted",
                Instructions = "system",
                Messages = limitTranscript,
                MaxSteps = 1
            };

            ProviderTurn limitResult = null;
            Exception limitFailure = null;
            var limitTask = Task.Run(async () =>
            {
                try { limitResult = await AgentRunner.RunAsync(limitProvider, limitRequest, limitTranscript, e => { }, CancellationToken.None); }
                catch (Exception e) { limitFailure = e; }
            });

            float limitStart = Time.realtimeSinceStartup;
            while (!limitTask.IsCompleted && Time.realtimeSinceStartup - limitStart < 30f) yield return null;

            try
            {
                if (!limitTask.IsCompleted) limitFailure = new AgentException("agent limit test timed out.");
                else if (limitTask.IsFaulted) limitFailure = limitTask.Exception != null ? limitTask.Exception.GetBaseException() : new AgentException("task failed");

                bool toolsDisabled = limitProvider.LastRequest != null && limitProvider.LastRequest.Tools != null && limitProvider.LastRequest.Tools.Count == 0;
                bool limitOk = limitFailure == null && limitResult != null &&
                               limitResult.Text == "Final answer after the tool limit." &&
                               toolsDisabled && limitTranscript.Count >= 3;
                Check("agent_final_answer_at_step_limit", limitOk,
                    limitFailure != null ? limitFailure.Message : "text=" + (limitResult != null ? limitResult.Text : "(null)") +
                    " tools=" + (limitProvider.LastRequest != null && limitProvider.LastRequest.Tools != null ? limitProvider.LastRequest.Tools.Count.ToString() : "?") +
                    " transcript=" + limitTranscript.Count);
            }
            catch (Exception e) { Check("agent_final_answer_at_step_limit", false, e.ToString()); }

            // 42. Malformed tool markup in a final answer is retried and never streamed
            var malformedFinal = RunAgentMalformedFinalAnswerTest();
            while (malformedFinal.MoveNext()) yield return malformedFinal.Current;

            var planLogin = RunChatGptPlanLoginTests();
            while (planLogin.MoveNext()) yield return planLogin.Current;

            var nativeProviders = RunNativeProviderTests();
            while (nativeProviders.MoveNext()) yield return nativeProviders.Current;

            Finish();
        }

        static IEnumerator RunMcpTest()
        {
            const int port = 7799;
            string token = "selftest-" + Guid.NewGuid().ToString("N");
            var server = new McpServer(port, token);
            string startError;
            if (!server.Start(out startError))
            {
                Check("mcp_http_roundtrip", false, startError);
                yield break;
            }

            string url = "http://127.0.0.1:" + port + "/mcp";
            var task = Task.Run(async () =>
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    string initBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\"selftest\"}}}";
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var init = await client.PostAsync(url, new StringContent(initBody, Encoding.UTF8, "application/json"));
                    string initText = await init.Content.ReadAsStringAsync();
                    string initStatus = ((int)init.StatusCode).ToString();

                    int unauthorizedStatus;
                    using (var anonymous = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                    {
                        var unauthorizedInit = await anonymous.PostAsync(url, new StringContent(initBody, Encoding.UTF8, "application/json"));
                        unauthorizedStatus = (int)unauthorizedInit.StatusCode;
                    }

                    var list = await client.PostAsync(url, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"));
                    string listText = await list.Content.ReadAsStringAsync();

                    string callBody = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"project_summary\",\"arguments\":{}}}";
                    var call = await client.PostAsync(url, new StringContent(callBody, Encoding.UTF8, "application/json"));
                    string callText = await call.Content.ReadAsStringAsync();

                    return new[] { initText, listText, callText, unauthorizedStatus.ToString(), initStatus };
                }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 60f) yield return null;

            bool completed = task.IsCompleted;
            string[] values = null;
            string failure = null;
            if (!completed) failure = "Timed out waiting for the MCP round trip.";
            else if (task.IsFaulted)
            {
                var exception = task.Exception;
                failure = exception != null ? exception.GetBaseException().Message : "MCP task failed.";
            }
            else
            {
                try { values = task.Result; }
                catch (Exception e) { failure = e.Message; }
            }

            server.Stop();

            if (values == null)
            {
                Check("mcp_http_roundtrip", false, failure);
                yield break;
            }

            try
            {
                var initJson = JsonValue.Parse(values[0]);
                var listJson = JsonValue.Parse(values[1]);
                var callJson = JsonValue.Parse(values[2]);
                bool initOk = initJson["result"]["protocolVersion"].AsString() == "2025-06-18" &&
                              initJson["result"]["serverInfo"]["name"].AsString() == "unity-ai-bridge";
                int toolCount = listJson["result"]["tools"].Count;
                bool listOk = listJson["result"]["tools"].IsArray && toolCount >= 15;
                bool callOk = !callJson["result"]["isError"].AsBool(true) &&
                              callJson["result"]["content"][0]["text"].AsString().IndexOf("project", StringComparison.OrdinalIgnoreCase) >= 0;
                bool unauthorizedOk = values[3] == "401" && values[4] == "200";
                Check("mcp_http_roundtrip", initOk && listOk && callOk && unauthorizedOk,
                    "init=" + initOk + " tools=" + toolCount + " call=" + callOk + " unauthorized401=" + (values[3] == "401") +
                    (initOk && listOk && callOk ? string.Empty : "\ninit: " + TruncateForTest(values[0]) + "\ncall: " + TruncateForTest(values[2])));
            }
            catch (Exception e)
            {
                Check("mcp_http_roundtrip", false, e.Message);
            }
        }

        static string TruncateForTest(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            return text.Length <= 700 ? text : text.Substring(0, 700) + "...";
        }

        static IEnumerator RunProviderFailureTest()
        {
            var settings = BridgeSettings.Current;
            string savedUrl = settings.OllamaUrl;
            int savedTimeout = settings.RequestTimeoutSeconds;
            settings.OllamaUrl = "http://127.0.0.1:9";
            settings.RequestTimeoutSeconds = 5;
            var provider = new OllamaLiveProvider();

            var task = Task.Run(async () =>
            {
                try
                {
                    await provider.GetModelsAsync(CancellationToken.None);
                    return "no exception";
                }
                catch (BridgeException e) { return e.Message; }
                catch (Exception e) { return "unexpected: " + e.GetType().Name + ": " + e.Message; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 30f) yield return null;
            settings.OllamaUrl = savedUrl;
            settings.RequestTimeoutSeconds = savedTimeout;

            if (!task.IsCompleted)
            {
                Check("provider_connection_error", false, "Timed out.");
                yield break;
            }

            string message = task.Result;
            bool ok = message.IndexOf("Could not reach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      message.IndexOf("did not respond", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      message.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0;
            Check("provider_connection_error", ok, message);
        }


        static string Sse(JsonValue value)
        {
            return "data: " + value.ToJson(false) + "\n\n";
        }

        static IEnumerator RunDeepSeekStreamTest()
        {
            var settings = BridgeSettings.Current;
            string savedBase = settings.DeepSeekBaseUrl;
            string savedKey = BridgeSettings.Secrets.DeepSeekApiKey;
            int savedTimeout = settings.RequestTimeoutSeconds;
            settings.RequestTimeoutSeconds = 20;

            var server = new LocalTestServer((request, body) =>
            {
                var sb = new StringBuilder();
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("reasoning_content", "Think. "))))));
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("content", "Hello "))))));
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("tool_calls", JsonValue.Arr(
                    JsonValue.Obj("index", 0, "id", "call_1", "type", "function", "function", JsonValue.Obj("name", "project_summary", "arguments", "{}")))))))));
                sb.Append(Sse(JsonValue.Obj(
                    "choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.NewObject(), "finish_reason", "tool_calls")),
                    "usage", JsonValue.Obj(
                        "prompt_tokens", 100,
                        "completion_tokens", 20,
                        "total_tokens", 120,
                        "prompt_cache_hit_tokens", 10,
                        "prompt_cache_miss_tokens", 90,
                        "completion_tokens_details", JsonValue.Obj("reasoning_tokens", 5)))));
                sb.Append("data: [DONE]\n\n");
                return new MockReply { ContentType = "text/event-stream", Body = sb.ToString() };
            });

            settings.DeepSeekBaseUrl = server.BaseUrl;
            BridgeSettings.Secrets.DeepSeekApiKey = "test-key";

            var request = new LlmRequest
            {
                ProviderId = BridgeSettings.ProviderDeepSeek,
                Model = "deepseek-flash",
                Instructions = "system",
                Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "hello" } },
                Tools = new List<ToolSpec> { new ToolSpec { Name = "project_summary", Description = "summary", Schema = JsonValue.Obj("type", "object", "properties", JsonValue.NewObject()) } },
                Thinking = true,
                ReasoningEffort = "high",
                MaxOutputTokens = 100
            };

            ProviderTurn turn = null;
            Exception failure = null;
            var events = new List<StreamEvent>();
            var task = Task.Run(async () =>
            {
                try
                {
                    turn = await new DeepSeekProvider().StreamTurnAsync(request, e => { lock (events) events.Add(e); }, CancellationToken.None);
                }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 45f) yield return null;
            server.Stop();

            try
            {
                if (!task.IsCompleted) failure = new BridgeException("DeepSeek mock request timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new BridgeException("task failed");
                bool ok = failure == null && turn != null &&
                          turn.Text == "Hello " &&
                          turn.Reasoning == "Think. " &&
                          turn.ToolCalls.Count == 1 &&
                          turn.ToolCalls[0].Name == "project_summary" &&
                          turn.ToolCalls[0].Arguments == "{}" &&
                          turn.Usage.PromptTokens == 100 &&
                          turn.Usage.CompletionTokens == 20 &&
                          !string.IsNullOrEmpty(turn.Usage.CostText) &&
                          turn.Usage.CostText.IndexOf("$", StringComparison.Ordinal) >= 0;
                Check("deepseek_stream_and_cost", ok,
                    failure != null ? failure.Message :
                    "text=" + (turn != null ? turn.Text : "(null)") + " reasoning=" + (turn != null ? turn.Reasoning : "") +
                    " calls=" + (turn != null ? turn.ToolCalls.Count : 0) + " usage=" + (turn != null ? turn.Usage.Summary() : ""));
            }
            finally
            {
                settings.DeepSeekBaseUrl = savedBase;
                BridgeSettings.Secrets.DeepSeekApiKey = savedKey;
                settings.RequestTimeoutSeconds = savedTimeout;
            }
            yield break;
        }

        static IEnumerator RunOpenAiStreamTest(bool chatGpt)
        {
            var settings = BridgeSettings.Current;
            var secrets = BridgeSettings.Secrets;
            string savedBase = settings.OpenAiBaseUrl;
            string savedKey = secrets.OpenAiApiKey;
            string savedAuthBase = ChatGptAuth.ApiBaseUrl;
            string sAccess = secrets.ChatGptAccessToken;
            string sRefresh = secrets.ChatGptRefreshToken;
            string sId = secrets.ChatGptIdToken;
            string sClient = secrets.ChatGptClientId;
            string sEmail = secrets.ChatGptEmail;
            string sSubject = secrets.ChatGptSubject;
            string sScopes = secrets.ChatGptScopes;
            long sExpiry = secrets.ChatGptExpiresAtUnix;
            bool sPlan = secrets.ChatGptPlanUsageEnabled;
            int savedTimeout = settings.RequestTimeoutSeconds;
            settings.RequestTimeoutSeconds = 20;

            string authHeader = null;
            var server = new LocalTestServer((request, body) =>
            {
                authHeader = request.Headers["Authorization"];
                var sb = new StringBuilder();
                sb.Append(Sse(JsonValue.Obj("type", "response.output_text.delta", "delta", "Hi ")));
                sb.Append(Sse(JsonValue.Obj("type", "response.output_item.added", "item", JsonValue.Obj(
                    "id", "fc_1", "type", "function_call", "call_id", "call_1", "name", "project_summary", "arguments", ""))));
                sb.Append(Sse(JsonValue.Obj("type", "response.function_call_arguments.delta", "item_id", "fc_1", "delta", "{}")));
                sb.Append(Sse(JsonValue.Obj("type", "response.function_call_arguments.done", "item_id", "fc_1", "arguments", "{}")));
                sb.Append(Sse(JsonValue.Obj("type", "response.output_item.done", "item", JsonValue.Obj(
                    "id", "fc_1", "type", "function_call", "call_id", "call_1", "name", "project_summary", "arguments", "{}"))));
                sb.Append(Sse(JsonValue.Obj("type", "response.completed", "response", JsonValue.Obj(
                    "status", "completed",
                    "usage", JsonValue.Obj(
                        "input_tokens", 12, "output_tokens", 3, "total_tokens", 15,
                        "input_tokens_details", JsonValue.Obj("cached_tokens", 0),
                        "output_tokens_details", JsonValue.Obj("reasoning_tokens", 0)),
                    "output", JsonValue.Arr(JsonValue.Obj("id", "fc_1", "type", "function_call", "call_id", "call_1", "name", "project_summary", "arguments", "{}"))))));
                return new MockReply { ContentType = "text/event-stream", Body = sb.ToString() };
            });

            settings.OpenAiBaseUrl = server.BaseUrl + "/v1";
            if (chatGpt)
            {
                secrets.ChatGptAccessToken = "oauth_test_token";
                secrets.ChatGptRefreshToken = "refresh_test";
                secrets.ChatGptIdToken = "id_test";
                secrets.ChatGptClientId = "oaiapp_test";
                secrets.ChatGptEmail = "user@example.com";
                secrets.ChatGptSubject = "subject";
                secrets.ChatGptScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
                secrets.ChatGptExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
                secrets.ChatGptPlanUsageEnabled = true;
                ChatGptAuth.ApiBaseUrl = server.BaseUrl + "/v1";
                ChatGptAuth.Reload();
            }
            else
            {
                secrets.OpenAiApiKey = "test-key";
            }

            var request = new LlmRequest
            {
                ProviderId = chatGpt ? BridgeSettings.ProviderChatGpt : BridgeSettings.ProviderOpenAi,
                Model = "gpt-5-test",
                Instructions = "system",
                Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "hello" } },
                Tools = new List<ToolSpec> { new ToolSpec { Name = "project_summary", Description = "summary", Schema = JsonValue.Obj("type", "object", "properties", JsonValue.NewObject()) } },
                MaxOutputTokens = 100,
                Temperature = 0.2
            };

            ProviderTurn turn = null;
            Exception failure = null;
            var task = Task.Run(async () =>
            {
                try
                {
                    turn = await new OpenAiResponsesProvider(chatGpt).StreamTurnAsync(request, e => { }, CancellationToken.None);
                }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 45f) yield return null;
            server.Stop();
            string bodySent = server.LastBody ?? string.Empty;

            try
            {
                if (!task.IsCompleted) failure = new BridgeException("Responses mock request timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new BridgeException("task failed");

                bool common = failure == null && turn != null &&
                              turn.Text == "Hi " &&
                              turn.ToolCalls.Count == 1 &&
                              turn.ToolCalls[0].Name == "project_summary" &&
                              turn.Usage.PromptTokens == 12 &&
                              turn.Usage.CompletionTokens == 3 &&
                              !string.IsNullOrEmpty(turn.RawItemsJson) &&
                              turn.RawItemsJson.IndexOf("fc_1", StringComparison.Ordinal) >= 0;
                bool reasoningOk = JsonValue.Parse(bodySent)["reasoning"]["effort"].AsString() == "high" && JsonValue.Parse(bodySent)["include"][0].AsString() == "reasoning.encrypted_content";
                bool requestOk = chatGpt
                    ? bodySent.IndexOf("\"additional_tools\"", StringComparison.Ordinal) >= 0 &&
                      bodySent.IndexOf("\"store\":false", StringComparison.Ordinal) >= 0 &&
                      bodySent.IndexOf("\"stream\":true", StringComparison.Ordinal) >= 0 &&
                      bodySent.IndexOf("\"max_output_tokens\"", StringComparison.Ordinal) < 0 &&
                      bodySent.IndexOf("\"temperature\"", StringComparison.Ordinal) < 0 &&
                      string.Equals(authHeader, "Bearer oauth_test_token", StringComparison.Ordinal)
                    : bodySent.IndexOf("\"tools\"", StringComparison.Ordinal) >= 0 &&
                      bodySent.IndexOf("\"max_output_tokens\"", StringComparison.Ordinal) >= 0 &&
                      bodySent.IndexOf("\"additional_tools\"", StringComparison.Ordinal) < 0;
                Check(chatGpt ? "chatgpt_oauth_responses_request" : "openai_responses_stream", common && requestOk && reasoningOk,
                    failure != null ? failure.Message : "common=" + common + " requestOk=" + requestOk + " body=" + TruncateForTest(bodySent));
            }
            finally
            {
                settings.OpenAiBaseUrl = savedBase;
                secrets.OpenAiApiKey = savedKey;
                ChatGptAuth.ApiBaseUrl = savedAuthBase;
                if (chatGpt)
                {
                    secrets.ChatGptAccessToken = sAccess;
                    secrets.ChatGptRefreshToken = sRefresh;
                    secrets.ChatGptIdToken = sId;
                    secrets.ChatGptClientId = sClient;
                    secrets.ChatGptEmail = sEmail;
                    secrets.ChatGptSubject = sSubject;
                    secrets.ChatGptScopes = sScopes;
                    secrets.ChatGptExpiresAtUnix = sExpiry;
                    secrets.ChatGptPlanUsageEnabled = sPlan;
                    ChatGptAuth.Reload();
                }
                settings.RequestTimeoutSeconds = savedTimeout;
            }
            yield break;
        }

        static IEnumerator RunChatGptRefreshTest()
        {
            var secrets = BridgeSettings.Secrets;
            string savedAuthBase = ChatGptAuth.AuthBaseUrl;
            string sAccess = secrets.ChatGptAccessToken;
            string sRefresh = secrets.ChatGptRefreshToken;
            string sId = secrets.ChatGptIdToken;
            string sClient = secrets.ChatGptClientId;
            string sScopes = secrets.ChatGptScopes;
            long sExpiry = secrets.ChatGptExpiresAtUnix;
            bool sPlan = secrets.ChatGptPlanUsageEnabled;
            int savedTimeout = BridgeSettings.Current.RequestTimeoutSeconds;
            BridgeSettings.Current.RequestTimeoutSeconds = 20;

            string bodySent = null;
            var server = new LocalTestServer((request, body) =>
            {
                bodySent = body;
                return new MockReply
                {
                    ContentType = "application/json",
                    Body = JsonValue.Obj(
                        "access_token", "new_access",
                        "refresh_token", "new_refresh",
                        "expires_in", 3600,
                        "scope", "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
                        "token_type", "Bearer").ToJson(false)
                };
            });

            ChatGptAuth.AuthBaseUrl = server.BaseUrl;
            secrets.ChatGptAccessToken = "expired_access";
            secrets.ChatGptRefreshToken = "old_refresh";
            secrets.ChatGptIdToken = "old_id";
            secrets.ChatGptClientId = "oaiapp_test";
            secrets.ChatGptScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
            secrets.ChatGptExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
            secrets.ChatGptPlanUsageEnabled = true;
            ChatGptAuth.Reload();

            string token = null;
            Exception failure = null;
            var task = Task.Run(async () =>
            {
                try { token = await ChatGptAuth.ForceRefreshAsync(CancellationToken.None); }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 30f) yield return null;
            server.Stop();

            try
            {
                if (!task.IsCompleted) failure = new BridgeException("refresh request timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new BridgeException("task failed");
                bool ok = failure == null && token == "new_access" && secrets.ChatGptRefreshToken == "new_refresh" &&
                          bodySent != null && bodySent.IndexOf("grant_type=refresh_token", StringComparison.Ordinal) >= 0 &&
                          bodySent.IndexOf("client_id=oaiapp_test", StringComparison.Ordinal) >= 0;
                Check("chatgpt_token_refresh", ok, failure != null ? failure.Message : "token=" + token + " refresh=" + secrets.ChatGptRefreshToken);
            }
            finally
            {
                ChatGptAuth.AuthBaseUrl = savedAuthBase;
                secrets.ChatGptAccessToken = sAccess;
                secrets.ChatGptRefreshToken = sRefresh;
                secrets.ChatGptIdToken = sId;
                secrets.ChatGptClientId = sClient;
                secrets.ChatGptScopes = sScopes;
                secrets.ChatGptExpiresAtUnix = sExpiry;
                secrets.ChatGptPlanUsageEnabled = sPlan;
                BridgeSettings.Current.RequestTimeoutSeconds = savedTimeout;
                BridgeSettings.SaveSecrets();
                ChatGptAuth.Reload();
            }
            string oldAuthBase = ChatGptAuth.AuthBaseUrl;
            string secretSnapshot = JsonUtility.ToJson(secrets);
            string oldModels = BridgeSettings.Current.ChatGptModelsJson;
            using (var renewalServer = new LocalTestServer((r, b) => new MockReply { Status = 503, Body = "{\"error\":\"temporarily_unavailable\"}" }))
            {
                ChatGptAuth.AuthBaseUrl = renewalServer.BaseUrl;
                secrets.ChatGptAccessToken = "access_test"; secrets.ChatGptRefreshToken = "refresh_test";
                secrets.ChatGptClientId = "oaiapp_test"; secrets.ChatGptExpiresAtUnix = 0;
                var transient = Task.Run(async () =>
                {
                    try { await ChatGptAuth.EnsureAccessTokenAsync(CancellationToken.None); return false; }
                    catch (BridgeException) { return secrets.ChatGptRefreshToken == "refresh_test" && secrets.ChatGptAccessToken == "access_test"; }
                });
                while (!transient.IsCompleted) yield return null;
                Check("chatgpt_transient_refresh_keeps_session", transient.Status == TaskStatus.RanToCompletion && transient.Result);
            }
            using (var arrived = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var renewalServer = new LocalTestServer((r, b) =>
            {
                if (r.Url.AbsolutePath.Contains("openid-configuration")) return new MockReply { Body = "{}" };
                arrived.Set(); release.Wait(5000);
                return new MockReply { Body = JsonValue.Obj("access_token", "late_access", "refresh_token", "late_refresh", "expires_in", 3600).ToJson(false) };
            }))
            {
                ChatGptAuth.AuthBaseUrl = renewalServer.BaseUrl;
                secrets.ChatGptAccessToken = "access_test"; secrets.ChatGptRefreshToken = "refresh_test";
                secrets.ChatGptClientId = "oaiapp_test"; secrets.ChatGptExpiresAtUnix = 0;
                var refreshing = Task.Run(async () =>
                {
                    try { await ChatGptAuth.EnsureAccessTokenAsync(CancellationToken.None); }
                    catch (OperationCanceledException) { }
                });
                float until = Time.realtimeSinceStartup + 8f;
                while (!arrived.IsSet && !refreshing.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                var signingOut = ChatGptAuth.SignOutAsync();
                release.Set();
                while (!refreshing.IsCompleted || !signingOut.IsCompleted) yield return null;
                Check("chatgpt_signout_wins_over_inflight_refresh", arrived.IsSet && !refreshing.IsFaulted &&
                    !signingOut.IsFaulted && !ChatGptAuth.HasTokens && !ChatGptAuth.IsReadyForInference);
            }
            ChatGptAuth.AuthBaseUrl = oldAuthBase;
            JsonUtility.FromJsonOverwrite(secretSnapshot, secrets);
            BridgeSettings.Current.ChatGptModelsJson = oldModels;
            BridgeSettings.SaveSecrets(); BridgeSettings.Save(); ChatGptAuth.Reload();
            yield break;
        }

        static IEnumerator RunOpenCodeStreamTest()
        {
            var settings = BridgeSettings.Current;
            string savedBase = settings.OpenCodeBaseUrl;
            string savedKey = BridgeSettings.Secrets.OpenCodeApiKey;

            var server = new LocalTestServer((request, body) =>
            {
                if (request.HttpMethod == "GET")
                {
                    return new MockReply
                    {
                        ContentType = "application/json",
                        Body = JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj(
                            "id", "deepseek-v4.1-flash",
                            "name", "DeepSeek V4.1 Flash",
                            "context_window", 1048576,
                            "max_output_tokens", 393216))).ToJson(false)
                    };
                }

                var sb = new StringBuilder();
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("reasoning_content", "Think. "))))));
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("content", "Hello "))))));
                sb.Append(Sse(JsonValue.Obj("choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.Obj("tool_calls", JsonValue.Arr(
                    JsonValue.Obj("index", 0, "id", "call_oc", "type", "function", "function", JsonValue.Obj("name", "project_summary", "arguments", "{}")))))))));
                sb.Append(Sse(JsonValue.Obj(
                    "choices", JsonValue.Arr(JsonValue.Obj("index", 0, "delta", JsonValue.NewObject(), "finish_reason", "tool_calls")),
                    "usage", JsonValue.Obj(
                        "prompt_tokens", 50,
                        "completion_tokens", 10,
                        "total_tokens", 60,
                        "completion_tokens_details", JsonValue.Obj("reasoning_tokens", 3)))));
                sb.Append("data: [DONE]\n\n");
                return new MockReply { ContentType = "text/event-stream", Body = sb.ToString() };
            });

            settings.OpenCodeBaseUrl = server.BaseUrl;
            BridgeSettings.Secrets.OpenCodeApiKey = "opencode-test-key";
            OpenCodeGoProvider.ResetResolvedBase();

            var request = new LlmRequest
            {
                ProviderId = BridgeSettings.ProviderOpenCode,
                SessionId = "oc-session-test",
                Model = "deepseek-v4.1-flash",
                Instructions = "system",
                Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "hello" } },
                Tools = new List<ToolSpec> { new ToolSpec { Name = "project_summary", Description = "summary", Schema = JsonValue.Obj("type", "object", "properties", JsonValue.NewObject()) } },
                MaxOutputTokens = 100,
                Temperature = 0.2
            };

            ProviderTurn turn = null;
            Exception failure = null;
            var task = Task.Run(async () =>
            {
                try { turn = await new OpenCodeGoProvider().StreamTurnAsync(request, e => { }, CancellationToken.None); }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 45f) yield return null;
            string path = server.LastPath;
            string bodySent = server.LastBody;
            string sessionHeader = server.LastSession;
            server.Stop();

            try
            {
                if (!task.IsCompleted) failure = new BridgeException("OpenCode Go mock request timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new BridgeException("task failed");
                bool ok = failure == null && turn != null &&
                          turn.Text == "Hello " &&
                          turn.Reasoning == "Think. " &&
                          turn.ToolCalls.Count == 1 &&
                          turn.ToolCalls[0].Name == "project_summary" &&
                          turn.ToolCalls[0].Arguments == "{}" &&
                          turn.Usage.PromptTokens == 50 &&
                          turn.Usage.CompletionTokens == 10 &&
                          path != null && path.EndsWith("/chat/completions", StringComparison.Ordinal) &&
                          bodySent != null && bodySent.IndexOf("deepseek-v4.1-flash", StringComparison.Ordinal) >= 0 &&
                          sessionHeader == "oc-session-test";
                Check("opencode_stream_and_tools", ok,
                    failure != null ? failure.Message : "text=" + (turn != null ? turn.Text : "") + " path=" + path + " usage=" + (turn != null ? turn.Usage.Summary() : ""));
            }
            finally
            {
                settings.OpenCodeBaseUrl = savedBase;
                BridgeSettings.Secrets.OpenCodeApiKey = savedKey;
                OpenCodeGoProvider.ResetResolvedBase();
            }
            yield break;
        }

        static IEnumerator RunAgentToolLoopTest()
        {
            string target = TestFolder + "/agent_loop.txt";
            var provider = new ScriptedProvider();
            var arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj(
                "kind", "create",
                "path", target,
                "content", "created by the agent tool loop\n")));
            provider.Turns.Enqueue(new ProviderTurn
            {
                Text = string.Empty,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_1", Name = "propose_file_changes", Arguments = arguments.ToJson(false) } },
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
            });
            provider.Turns.Enqueue(new ProviderTurn
            {
                Text = "Done.",
                Usage = new TokenUsage { PromptTokens = 20, CompletionTokens = 8 }
            });

            var transcript = new List<LlmMessage>();
            var request = new LlmRequest
            {
                ProviderId = "scripted",
                Tools = McpTools.GetToolSpecs(true),
                Model = "scripted",
                Instructions = "system",
                Messages = transcript,
                MaxSteps = 3
            };

            ProviderTurn result = null;
            Exception failure = null;
            var task = Task.Run(async () =>
            {
                try { result = await AgentRunner.RunAsync(provider, request, transcript, e => { }, CancellationToken.None); }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 45f) yield return null;
            BridgeChange staged = null;
            foreach (var change in ChangeService.All)
                if (change.TargetPath == target) { staged = change; break; }

            try
            {
                if (!task.IsCompleted) failure = new BridgeException("agent loop timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new BridgeException("task failed");

                bool toolMessage = false;
                foreach (var message in transcript)
                    if (message.Role == "tool" && message.Content != null && message.Content.IndexOf("Pending", StringComparison.Ordinal) >= 0) toolMessage = true;

                bool ok = failure == null && result != null && result.Text == "Done." &&
                          staged != null && staged.Status == ChangeStatus.Pending &&
                          !File.Exists(BridgePaths.GetFullPathSafe(target)) &&
                          toolMessage && transcript.Count >= 3;
                Check("agent_tool_loop_stages_change", ok,
                    failure != null ? failure.Message : "staged=" + (staged != null ? staged.Status : "none") + " toolMessage=" + toolMessage + " transcript=" + transcript.Count);

                if (staged != null)
                {
                    ChangeService.Approve(staged.Id);
                    string applyError = ChangeService.Apply(staged.Id);
                    string readError;
                    string content = FileService.ReadText(target, out readError);
                    bool applied = string.IsNullOrEmpty(applyError) && content != null && content.IndexOf("agent tool loop", StringComparison.Ordinal) >= 0;
                    Check("agent_tool_loop_apply_after_approval", applied, applyError ?? readError);
                    ChangeService.Remove(staged.Id);
                    string full = BridgePaths.GetFullPathSafe(target);
                    if (File.Exists(full)) File.Delete(full);
                    if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
                }
            }
            finally
            {
                if (staged != null) { try { ChangeService.Remove(staged.Id); } catch { } }
                string full = BridgePaths.GetFullPathSafe(target);
                if (File.Exists(full)) File.Delete(full);
                if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
            }
            yield break;
        }

        static IEnumerator RunAgentMalformedFinalAnswerTest()
        {
            const string malformed = "<｜｜DSML｜｜ calls>\n<｜｜DSML｜｜ invoke name=\"read_project_file\">\n<｜｜DSML｜｜ parameter name=\"path\">Assets/x</｜｜DSML｜｜ parameter>\n</｜｜DSML｜｜ invoke>\n</｜｜DSML｜｜ calls>";

            var provider = new ScriptedProvider { EmitTextDeltas = true };
            provider.Turns.Enqueue(new ProviderTurn
            {
                Text = string.Empty,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_malformed", Name = "get_bridge_status", Arguments = "{}" } }
            });
            provider.Turns.Enqueue(new ProviderTurn { Text = malformed, Reasoning = "first malformed reasoning" });
            provider.Turns.Enqueue(new ProviderTurn { Text = "Recovered final answer.", Reasoning = "retry reasoning" });

            var transcript = new List<LlmMessage>();
            var request = new LlmRequest
            {
                ProviderId = "scripted",
                Tools = McpTools.GetToolSpecs(true),
                Model = "scripted",
                Instructions = "system",
                Messages = transcript,
                MaxSteps = 1
            };

            var events = new List<StreamEvent>();
            ProviderTurn result = null;
            Exception failure = null;
            var task = Task.Run(async () =>
            {
                try { result = await AgentRunner.RunAsync(provider, request, transcript, e => { lock (events) events.Add(e); }, CancellationToken.None); }
                catch (Exception e) { failure = e; }
            });

            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - start < 30f) yield return null;

            try
            {
                if (!task.IsCompleted) failure = new AgentException("malformed final answer test timed out.");
                else if (task.IsFaulted) failure = task.Exception != null ? task.Exception.GetBaseException() : new AgentException("task failed");

                bool badStreamed = false;
                bool cleanStreamed = false;
                lock (events)
                {
                    foreach (var evt in events)
                    {
                        if (evt == null || evt.Kind != StreamEventKind.TextDelta) continue;
                        if (evt.Text.IndexOf("<｜", StringComparison.Ordinal) >= 0) badStreamed = true;
                        if (evt.Text == "Recovered final answer.") cleanStreamed = true;
                    }
                }

                bool toolsDisabled = provider.LastRequest != null && provider.LastRequest.Tools != null && provider.LastRequest.Tools.Count == 0;
                bool askedForPlainText = false;
                if (provider.LastRequest != null && provider.LastRequest.Messages != null && provider.LastRequest.Messages.Count > 0)
                {
                    var last = provider.LastRequest.Messages[provider.LastRequest.Messages.Count - 1];
                    askedForPlainText = last.Role == "user" && (last.Content ?? string.Empty).IndexOf("tool budget is exhausted", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                bool transcriptClean = transcript.Count >= 3 && transcript[transcript.Count - 1].Content == "Recovered final answer.";

                Check("agent_final_answer_retries_malformed_output",
                    failure == null && result != null && result.Text == "Recovered final answer." &&
                    !badStreamed && cleanStreamed && toolsDisabled && askedForPlainText && transcriptClean,
                    failure != null ? failure.Message : "badStreamed=" + badStreamed + " cleanStreamed=" + cleanStreamed +
                    " tools=" + (provider.LastRequest != null && provider.LastRequest.Tools != null ? provider.LastRequest.Tools.Count.ToString() : "?") +
                    " retryInstruction=" + askedForPlainText + " transcriptClean=" + transcriptClean);
            }
            catch (Exception e) { Check("agent_final_answer_retries_malformed_output", false, e.ToString()); }

            var reasonProvider = new ScriptedProvider { EmitTextDeltas = true };
            reasonProvider.Turns.Enqueue(new ProviderTurn
            {
                Text = string.Empty,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_reason", Name = "get_bridge_status", Arguments = "{}" } }
            });
            reasonProvider.Turns.Enqueue(new ProviderTurn { Text = malformed, Reasoning = "Coherent analysis of the asset folder." });
            reasonProvider.Turns.Enqueue(new ProviderTurn { Text = malformed, Reasoning = string.Empty });

            var reasonTranscript = new List<LlmMessage>();
            var reasonRequest = new LlmRequest
            {
                ProviderId = "scripted",
                Tools = McpTools.GetToolSpecs(true),
                Model = "scripted",
                Instructions = "system",
                Messages = reasonTranscript,
                MaxSteps = 1
            };

            var reasonEvents = new List<StreamEvent>();
            ProviderTurn reasonResult = null;
            Exception reasonFailure = null;
            var reasonTask = Task.Run(async () =>
            {
                try { reasonResult = await AgentRunner.RunAsync(reasonProvider, reasonRequest, reasonTranscript, e => { lock (reasonEvents) reasonEvents.Add(e); }, CancellationToken.None); }
                catch (Exception e) { reasonFailure = e; }
            });

            float reasonStart = Time.realtimeSinceStartup;
            while (!reasonTask.IsCompleted && Time.realtimeSinceStartup - reasonStart < 30f) yield return null;

            try
            {
                if (!reasonTask.IsCompleted) reasonFailure = new AgentException("reasoning fallback test timed out.");
                else if (reasonTask.IsFaulted) reasonFailure = reasonTask.Exception != null ? reasonTask.Exception.GetBaseException() : new AgentException("task failed");

                bool badStreamed = false;
                bool reasoningStreamed = false;
                lock (reasonEvents)
                {
                    foreach (var evt in reasonEvents)
                    {
                        if (evt == null || evt.Kind != StreamEventKind.TextDelta) continue;
                        if (evt.Text.IndexOf("<｜", StringComparison.Ordinal) >= 0) badStreamed = true;
                        if (evt.Text.IndexOf("Coherent analysis of the asset folder.", StringComparison.Ordinal) >= 0) reasoningStreamed = true;
                    }
                }

                bool ok = reasonFailure is AgentException && reasonResult == null && !badStreamed && !reasoningStreamed;
                Check("agent_final_answer_keeps_reasoning_separate", ok,
                    reasonFailure != null ? reasonFailure.Message : "badStreamed=" + badStreamed + " reasoningStreamed=" + reasoningStreamed +
                    " text=" + (reasonResult != null ? reasonResult.Text : "(null)"));
            }
            catch (Exception e) { Check("agent_final_answer_uses_reasoning_fallback", false, e.ToString()); }

            yield break;
        }

        sealed class ScriptedProvider : ILiveProvider
        {
            public readonly Queue<ProviderTurn> Turns = new Queue<ProviderTurn>();
            public LlmRequest LastRequest;
            public bool EmitTextDeltas;
            public string Id { get { return "scripted"; } }
            public string DisplayName { get { return "Scripted"; } }
            public string BillingNote { get { return "test"; } }
            public bool IsConfigured(out string reason) { reason = null; return true; }
            public Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new List<ModelInfo> { new ModelInfo { Id = "scripted", DisplayName = "Scripted" } });
            }
            public Task<string> TestConnectionAsync(CancellationToken cancellationToken) { return Task.FromResult("ok"); }
            public Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastRequest = request;
                if (Turns.Count == 0) return Task.FromResult(new ProviderTurn { Text = "No more turns." });
                var turn = Turns.Dequeue();
                if (EmitTextDeltas && !string.IsNullOrEmpty(turn.Text)) onEvent(StreamEvent.Delta(turn.Text));
                return Task.FromResult(turn);
            }
        }

        static IEnumerator RunPrefabRegressionTests()
        {
            string sourcePath = TestFolder + "/TowerMap.prefab";
            string destination = TestFolder + "/battle.prefab";
            GameObject source = new GameObject("TowerMap");
            var child = new GameObject("Tower");
            child.transform.SetParent(source.transform);
            child.AddComponent<BoxCollider2D>();
            PrefabUtility.SaveAsPrefabAsset(source, sourcePath);
            UnityEngine.Object.DestroyImmediate(source);
            string original = File.ReadAllText(BridgePaths.ToAbsolute(sourcePath));
            string originalGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var inspect = AssetService.InspectAsset(sourcePath, 3, 10);
            Check("prefab_inspection_exposes_bounded_hierarchy", inspect["hierarchy"]["children"][0]["name"].AsString() == "Tower" &&
                inspect["reuse_hint"].AsString().Contains("source_asset"));

            var args = JsonValue.Obj("path", destination, "source_asset", sourcePath, "name", "battle", "keep_in_scene", true);
            var staged = McpTools.InvokeOnMain("create_prefab", args);
            string id = staged["structuredContent"]["id"].AsString();
            var change = ChangeService.Find(id);
            if (change != null) _testChangeIds.Add(change.Id);
            Check("prefab_asset_reuse_stages_real_change", !staged["isError"].AsBool() && change != null && change.Kind == ChangeKinds.CreatePrefab &&
                change.TargetPath == destination && change.Status == ChangeStatus.Pending && !File.Exists(BridgePaths.ToAbsolute(destination)));
            var again = McpTools.InvokeOnMain("create_prefab", args);
            Check("prefab_repeat_proposal_reuses_pending_change", again["structuredContent"]["id"].AsString() == id);
            string error = change != null ? ChangeService.Approve(id) ?? ChangeService.Apply(id) : "No staged prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(destination);
            var instance = SceneService.FindByPath("battle");
            Check("prefab_reuse_creates_connected_scene_instance", error == null && prefab != null && prefab.name == "battle" &&
                instance != null && instance.transform.Find("Tower") != null && instance.transform.Find("Tower").GetComponent<BoxCollider2D>() != null &&
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance) == destination, error);
            Check("prefab_reuse_preserves_original_asset_and_guid", File.ReadAllText(BridgePaths.ToAbsolute(sourcePath)) == original && AssetDatabase.AssetPathToGUID(sourcePath) == originalGuid);
            Undo.PerformUndo();
            Check("prefab_reuse_scene_creation_supports_undo", instance == null && AssetDatabase.LoadAssetAtPath<GameObject>(destination) != null);
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance);

            var invalid = McpTools.InvokeOnMain("create_prefab", JsonValue.Obj("path", TestFolder + "/Invalid.prefab"));
            var outside = McpTools.InvokeOnMain("create_prefab", JsonValue.Obj("path", TestFolder + "/Invalid.prefab", "source_asset", "../outside.prefab"));
            var ambiguous = McpTools.InvokeOnMain("create_prefab", JsonValue.Obj("path", TestFolder + "/Invalid.prefab", "source_asset", sourcePath, "target", "Tower"));
            Check("prefab_reuse_rejects_missing_unsafe_and_ambiguous_sources", invalid["isError"].AsBool() && outside["isError"].AsBool() && ambiguous["isError"].AsBool());

            // Reproduce the exact agent sequence: preamble, direct tool proposal, final response.
            var provider = new ScriptedProvider { EmitTextDeltas = true };
            string agentDestination = TestFolder + "/AgentBattle.prefab";
            provider.Turns.Enqueue(new ProviderTurn { Text = "I found the map.", ToolCalls = new List<ToolCall> {
                new ToolCall { Id = "prefab_reuse", Name = "create_prefab", Arguments = JsonValue.Obj("path", agentDestination,
                    "source_asset", sourcePath, "name", "AgentBattle", "keep_in_scene", true).ToJson(false) } } });
            provider.Turns.Enqueue(new ProviderTurn { Text = "The prefab and scene instance are staged for review." });
            var transcript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Reuse the tower defence map as battle." } };
            var events = new List<StreamEvent>();
            var task = Task.Run(async () => await AgentRunner.RunAsync(provider, new LlmRequest { Model = "scripted", Tools = McpTools.GetToolSpecs(true), MaxSteps = 3 },
                transcript, e => { lock (events) events.Add(e); }, CancellationToken.None));
            while (!task.IsCompleted) yield return null;
            var visible = new StringBuilder();
            lock (events) foreach (var e in events) if (e.Kind == StreamEventKind.TextDelta) visible.Append(e.Text);
            Check("agent_prefab_request_uses_native_tool_and_separates_messages", !task.IsFaulted &&
                visible.ToString() == "I found the map.\n\nThe prefab and scene instance are staged for review." &&
                transcript.Count == 4 && transcript[2].Role == "tool" && !File.Exists(BridgePaths.ToAbsolute(agentDestination)));
            string obsoleteId = null;
            foreach (var c in ChangeService.All) if (c.TargetPath == agentDestination) { _testChangeIds.Add(c.Id); obsoleteId = c.Id; }
            var discard = McpTools.InvokeOnMain("discard_pending_changes", JsonValue.Obj("ids", JsonValue.Arr(obsoleteId)));
            var discardApplied = McpTools.InvokeOnMain("discard_pending_changes", JsonValue.Obj("ids", JsonValue.Arr(id)));
            Check("discard_obsolete_proposals_preserves_applied_changes", !discard["isError"].AsBool() && ChangeService.Find(obsoleteId).Status == ChangeStatus.Rejected &&
                discardApplied["isError"].AsBool() && ChangeService.Find(id).Status == ChangeStatus.Applied && File.Exists(BridgePaths.ToAbsolute(destination)));

            var currentState = JsonValue.Parse(ChatService.BuildChangeStateContext());
            bool sawDiscarded = false, sawApplied = false;
            foreach (var state in currentState.ArrayValue)
            {
                if (state["id"].AsString() == obsoleteId) sawDiscarded = state["status"].AsString() == ChangeStatus.Rejected;
                if (state["id"].AsString() == id) sawApplied = state["status"].AsString() == ChangeStatus.Applied;
            }
            Check("agent_context_uses_current_proposal_status", sawDiscarded && sawApplied);

            // The real map from the failed conversation, if present in this project.
            const string actual = "Assets/Assets/_Placeholder/KingdomGuard/_Export/KG_Level1_Map.prefab";
            if (File.Exists(BridgePaths.ToAbsolute(actual)))
            {
                var originalMap = AssetDatabase.LoadAssetAtPath<GameObject>(actual);
                string guid = AssetDatabase.AssetPathToGUID(actual);
                string originalText = File.ReadAllText(BridgePaths.ToAbsolute(actual));
                int sourceMissing = AssetService.InspectPrefabScripts(originalMap)["missing_script_count"].AsInt();
                var blockedSource = McpTools.InvokeOnMain("create_prefab", JsonValue.Obj("path", TestFolder + "/MustNotSave.prefab", "source_asset", actual));
                Check("prefab_missing_scripts_reported_before_staging", sourceMissing > 0 && blockedSource["isError"].AsBool());
                string actualDestination = TestFolder + "/RealBattle.prefab";
                var result = McpTools.InvokeOnMain("create_prefab", JsonValue.Obj("path", actualDestination, "source_asset", actual, "name", "RealBattle", "keep_in_scene", true, "remove_missing_scripts", true));
                string actualId = result["structuredContent"]["id"].AsString();
                if (!string.IsNullOrEmpty(actualId)) _testChangeIds.Add(actualId);
                string actualError = string.IsNullOrEmpty(actualId) ? result.ToJson(false) : ChangeService.Approve(actualId) ?? ChangeService.Apply(actualId);
                var realBattle = SceneService.FindByPath("RealBattle");
                Check("real_tower_defence_prefab_reuse_regression", actualError == null && realBattle != null &&
                    realBattle.transform.childCount == originalMap.transform.childCount &&
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(realBattle) == actualDestination &&
                    AssetDatabase.AssetPathToGUID(actual) == guid && originalMap.name == "KG_Level1_Map" &&
                    AssetService.InspectPrefabScripts(realBattle)["missing_script_count"].AsInt() == 0 &&
                    AssetService.InspectPrefabScripts(originalMap)["missing_script_count"].AsInt() == sourceMissing &&
                    File.ReadAllText(BridgePaths.ToAbsolute(actual)) == originalText, actualError);
                if (realBattle != null) UnityEngine.Object.DestroyImmediate(realBattle);
            }

            string largePath = TestFolder + "/LargePrefab.txt";
            var large = new StringBuilder();
            for (int line = 1; line <= 10000; line++) large.Append("line ").Append(line).Append(' ').Append(new string('x', 260)).Append('\n');
            File.WriteAllText(BridgePaths.ToAbsolute(largePath), large.ToString());
            var page = McpTools.InvokeOnMain("read_project_file", JsonValue.Obj("path", largePath, "start_line", 840, "line_count", 2, "max_chars", 1000));
            var data = page["structuredContent"];
            Check("large_file_read_returns_requested_lines", !page["isError"].AsBool() && data["content"].AsString().StartsWith("line 840 ", StringComparison.Ordinal) &&
                data["content"].AsString().Contains("line 841 ") && !data["content"].AsString().Contains("line 842 ") && data["next_start_line"].AsInt() == 842 && data["end_line"].AsInt() == 841);
            File.WriteAllText(BridgePaths.ToAbsolute(largePath), "abcdefghijk\nsecond\n");
            var first = FileService.ReadTextPage(largePath, 1, 10, 5);
            var next = FileService.ReadTextPage(largePath, first["next_start_line"].AsInt(), 10, 5, first["next_start_column"].AsInt());
            Check("long_line_read_continuation_does_not_skip_text", first["content"].AsString() == "abcde" && next["content"].AsString() == "fghij");
            string compact = AgentRunner.CompactToolResult(new string('"', 40000), "list_project_files", new List<string> { "pending-id" });
            var compactJson = JsonValue.Parse(compact);
            Check("oversize_tool_result_is_bounded_valid_json", compact.Length <= 18000 && compactJson["truncated"].AsBool() && compactJson["change_ids"][0].AsString() == "pending-id");
            Check("tool_activity_summary_is_readable", AgentRunner.SummarizeToolResult(new ToolCall { Name = "project_summary" }, "{\"project\":\"Example\",\"unity_version\":\"6\"}") == "Example · Unity 6");

            var emptyProvider = new ScriptedProvider { EmitTextDeltas = true };
            emptyProvider.Turns.Enqueue(new ProviderTurn { Reasoning = "Reasoning only.", Text = "", Usage = new TokenUsage { CompletionTokens = 20 } });
            emptyProvider.Turns.Enqueue(new ProviderTurn { Text = "Recovered user-visible answer.", Usage = new TokenUsage { CompletionTokens = 10 } });
            var emptyTranscript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Explain the map" } };
            var emptyTask = Task.Run(async () => await AgentRunner.RunAsync(emptyProvider, new LlmRequest { Model = "scripted", MaxOutputTokens = 100 }, emptyTranscript, e => { }, CancellationToken.None));
            while (!emptyTask.IsCompleted) yield return null;
            Check("reasoning_only_turn_recovers_without_rerunning_tools", !emptyTask.IsFaulted && emptyTask.Result.Text == "Recovered user-visible answer." &&
                emptyTask.Result.Usage.CompletionTokens == 30 && emptyProvider.LastRequest.Tools.Count == 0 && emptyProvider.LastRequest.MaxOutputTokens >= 8192 && emptyTranscript.Count == 2);
            var neverAnswers = new ScriptedProvider();
            neverAnswers.Turns.Enqueue(new ProviderTurn()); neverAnswers.Turns.Enqueue(new ProviderTurn());
            var failedRecovery = Task.Run(async () => await AgentRunner.RunAsync(neverAnswers, new LlmRequest { Model = "scripted" }, new List<LlmMessage>(), e => { }, CancellationToken.None));
            while (!failedRecovery.IsCompleted) yield return null;
            Check("empty_response_recovery_is_bounded", failedRecovery.IsFaulted && failedRecovery.Exception.GetBaseException() is AgentException && neverAnswers.Turns.Count == 0);
        }

        static IEnumerator RunChatReliabilityTests()
        {
            var task = Task.Run(async () =>
            {
                // A human usually needs longer than one second to complete OAuth.
                using (var listener = new HttpListener())
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
                {
                    listener.Prefixes.Add("http://127.0.0.1:7831/");
                    listener.Start();
                    var callback = ChatGptAuth.WaitForCallbackAsync(listener, "expected", deadline.Token);
                    await Task.Delay(2200);
                    await client.GetAsync("http://127.0.0.1:7831/favicon.ico");
                    await client.GetAsync("http://127.0.0.1:7831/auth/callback?state=stale&code=ignore");
                    await client.GetAsync("http://127.0.0.1:7831/auth/callback?state=expected&code=valid&client_id=oaiapp_test");
                    var result = await callback;
                    if (result[0] != "valid" || result[1] != "oaiapp_test") throw new Exception("Wrong OAuth callback.");
                }
            });
            while (!task.IsCompleted) yield return null;
            Check("chatgpt_delayed_callback_and_stale_tab", !task.IsFaulted && !task.IsCanceled,
                task.Exception != null ? task.Exception.GetBaseException().Message : null);

            var cancelTask = Task.Run(async () =>
            {
                using (var listener = new HttpListener())
                using (var cancel = new CancellationTokenSource())
                {
                    listener.Prefixes.Add("http://127.0.0.1:7831/"); listener.Start();
                    var callback = ChatGptAuth.WaitForCallbackAsync(listener, "expected", cancel.Token);
                    cancel.CancelAfter(100);
                    try { await callback; throw new Exception("Cancellation was ignored."); }
                    catch (OperationCanceledException) { }
                }
            });
            while (!cancelTask.IsCompleted) yield return null;
            Check("chatgpt_callback_cancels", !cancelTask.IsFaulted, cancelTask.Exception != null ? cancelTask.Exception.GetBaseException().Message : null);

            var catalog = OpenAiResponsesProvider.ParseChatGptModels(JsonValue.Obj("models", JsonValue.Arr(
                JsonValue.Obj("slug", "preferred", "display_name", "Preferred", "visibility", "list"),
                JsonValue.Obj("slug", "hidden", "visibility", "hide"),
                JsonValue.Obj("slug", "second", "display_name", "Second", "visibility", "list"))));
            var compatible = OpenAiResponsesProvider.ParseChatGptModels(JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj("id", "standard-model"))));
            Check("chatgpt_model_catalog_order_visibility_and_schema", catalog.Count == 2 && catalog[0].Id == "preferred" &&
                catalog[0].DisplayName == "Preferred" && catalog[1].Id == "second" && compatible.Count == 1 && compatible[0].Id == "standard-model");

            var chatModels = OpenAiResponsesProvider.ParseChatGptModels(JsonValue.Obj("models", JsonValue.Arr(
                JsonValue.Obj("slug", "gpt-5-chat", "display_name", "GPT-5 chat", "visibility", "list"),
                JsonValue.Obj("slug", "gpt-6-codex", "display_name", "GPT-6 Codex", "visibility", "list"),
                JsonValue.Obj("slug", "gpt-6", "display_name", "GPT-6", "visibility", "list"),
                JsonValue.Obj("slug", "gpt-6-pro", "display_name", "GPT-6 Pro", "visibility", "list"),
                JsonValue.Obj("slug", "gpt-6-hidden", "visibility", "hide"),
                JsonValue.Obj("slug", "coding-route", "display_name", "Codex", "visibility", "list"))));
            Check("chatgpt_chat_catalog_excludes_codex_and_retains_visible_gpt6", chatModels.Count == 3 && chatModels[0].Id == "gpt-5-chat" && chatModels[1].Id == "gpt-6" && chatModels[2].Id == "gpt-6-pro");
            Check("chatgpt_default_prefers_available_gpt6_without_inventing_access", OpenAiResponsesProvider.PreferredChatModel(chatModels) == "gpt-6" &&
                OpenAiResponsesProvider.PreferredChatModel(compatible) == "standard-model" && OpenAiResponsesProvider.PreferredChatModel(new List<ModelInfo>()) == null);
            var rejectedCodex = ProviderFactory.Create(BridgeSettings.ProviderChatGpt).StreamTurnAsync(new LlmRequest { Model = "gpt-6-codex" }, evt => { }, CancellationToken.None);
            while (!rejectedCodex.IsCompleted) yield return null;
            Check("chatgpt_direct_route_never_executes_a_codex_model", rejectedCodex.IsFaulted && rejectedCodex.Exception.GetBaseException().Message.Contains("separate Codex connection"));

            bool modes = ChatService.ToolsForMode("chat").Count == 0 &&
                !ChatService.ToolsForMode("plan").Exists(t => t.Name == "propose_file_changes" || t.Name == "save_scene") &&
                ChatService.ToolsForMode("agent").Exists(t => t.Name == "propose_file_changes");
            Check("chat_modes_offer_correct_tools", modes);

            // A model must not bypass Chat/Plan by inventing a write tool call.
            var provider = new ScriptedProvider();
            provider.Turns.Enqueue(new ProviderTurn { ToolCalls = new List<ToolCall> {
                new ToolCall { Id = "blocked", Name = "propose_file_changes", Arguments = "{}" } } });
            provider.Turns.Enqueue(new ProviderTurn { Text = "No write tools are available." });
            bool blocked = false;
            var transcript = new List<LlmMessage>();
            var modeTask = Task.Run(async () => await AgentRunner.RunAsync(provider,
                new LlmRequest { Model = "scripted", Tools = ChatService.ToolsForMode("plan") }, transcript,
                e => { if (e.Kind == StreamEventKind.ToolCallResult && !e.ToolOk) blocked = true; }, CancellationToken.None));
            while (!modeTask.IsCompleted) yield return null;
            Check("agent_rejects_unoffered_tools", !modeTask.IsFaulted && blocked && transcript.Count == 3 && transcript[1].Content.Contains("not available"));

            var interrupted = new List<LlmMessage> {
                new LlmMessage { Role = "user", Content = "Inspect" },
                new LlmMessage { Role = "assistant", ToolCalls = new List<ToolCall> {
                    new ToolCall { Id = "done", Name = "project_summary" }, new ToolCall { Id = "cancelled", Name = "inspect_scene" } } },
                new LlmMessage { Role = "tool", ToolCallId = "done", Content = "Actual result" }
            };
            ChatService.RepairInterruptedToolCalls(interrupted);
            ChatService.RepairInterruptedToolCalls(interrupted);
            Check("interrupted_tool_history_repaired_once", interrupted.Count == 4 && interrupted[2].Content == "Actual result" &&
                interrupted[3].Role == "tool" && interrupted[3].ToolCallId == "cancelled" && interrupted[3].Content.Contains("interrupted"));

            const string historyProvider = "selftest-tool-boundary";
            var meta = ChatHistory.NewConversation(historyProvider, "test", "Tool boundary");
            var history = new List<LlmMessage>();
            for (int turn = 0; turn < 50; turn++)
            {
                history.Add(new LlmMessage { Role = "user", Content = "turn " + turn });
                history.Add(new LlmMessage { Role = "assistant", ToolCalls = new List<ToolCall> { new ToolCall { Id = "c" + turn, Name = "project_summary" } } });
                history.Add(new LlmMessage { Role = "tool", ToolCallId = "c" + turn, Content = "result" });
                history.Add(new LlmMessage { Role = "assistant", Content = "Done" });
            }
            // Shift the 120-message boundary into a tool turn.
            history.Add(new LlmMessage { Role = "user", Content = "next" });
            ChatHistory.SaveContext(meta, history);
            var restored = ChatHistory.LoadContext(meta);
            Check("history_keeps_complete_tool_turns", restored.Count > 0 && restored.Count <= ChatHistory.ContextLimit && restored[0].Role == "user" && restored[restored.Count - 1].Content == "next");
            ChatHistory.ClearProvider(historyProvider);

            var settings = BridgeSettings.Current;
            string savedBase = settings.OpenAiBaseUrl;
            string savedKey = BridgeSettings.Secrets.OpenAiApiKey;
            using (var server = new LocalTestServer((r, b) => new MockReply { ContentType = "text/event-stream",
                Body = Sse(JsonValue.Obj("type", "response.output_text.delta", "delta", "Partial")) }))
            {
                settings.OpenAiBaseUrl = server.BaseUrl + "/v1";
                BridgeSettings.Secrets.OpenAiApiKey = "test-key";
                var incomplete = Task.Run(async () =>
                {
                    try { await new OpenAiResponsesProvider(false).StreamTurnAsync(new LlmRequest { Model = "test" }, e => { }, CancellationToken.None); return false; }
                    catch (BridgeException e) { return e.Message.Contains("before the response completed"); }
                });
                while (!incomplete.IsCompleted) yield return null;
                Check("responses_rejects_truncated_stream", incomplete.Status == TaskStatus.RanToCompletion && incomplete.Result);
            }
            settings.OpenAiBaseUrl = savedBase;
            BridgeSettings.Secrets.OpenAiApiKey = savedKey;

            // Send headers and one event, then stall. Stop must unblock ReadLineAsync.
            var streamTask = Task.Run(async () =>
            {
                using (var listener = new HttpListener())
                using (var stop = new CancellationTokenSource())
                {
                    listener.Prefixes.Add("http://127.0.0.1:7831/"); listener.Start();
                    var accept = listener.GetContextAsync();
                    var streaming = StreamingHttp.PostSseAsync("http://127.0.0.1:7831/stream", "{}", null, 20, d => stop.Cancel(), stop.Token);
                    var context = await accept;
                    context.Response.ContentType = "text/event-stream";
                    context.Response.SendChunked = true;
                    byte[] bytes = Encoding.UTF8.GetBytes("data: hello\n\n");
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    await context.Response.OutputStream.FlushAsync();
                    stop.CancelAfter(150);
                    var done = await Task.WhenAny(streaming, Task.Delay(3000));
                    context.Response.Close();
                    if (done != streaming) throw new Exception("Stop left the stream blocked.");
                    try { await streaming; throw new Exception("Expected cancellation."); }
                    catch (OperationCanceledException) { }
                }
            });
            while (!streamTask.IsCompleted) yield return null;
            Check("stream_stop_unblocks_stalled_read", !streamTask.IsFaulted,
                streamTask.Exception != null ? streamTask.Exception.GetBaseException().Message : null);
        }

        sealed class MockReply
        {
            public int Status = 200;
            public string ContentType = "application/json";
            public string Body = string.Empty;
        }

        sealed class LocalTestServer : IDisposable
        {
            readonly HttpListener _listener;
            readonly Func<HttpListenerRequest, string, MockReply> _handler;
            readonly Task _loop;
            public string BaseUrl { get; private set; }
            public string LastBody { get; private set; }
            public string LastAuthorization { get; private set; }
            public string LastSession { get; private set; }
            public string LastPath { get; private set; }

            public LocalTestServer(Func<HttpListenerRequest, string, MockReply> handler)
            {
                _handler = handler;
                for (int port = 7810; port <= 7830; port++)
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    try
                    {
                        listener.Start();
                        _listener = listener;
                        BaseUrl = "http://127.0.0.1:" + port;
                        break;
                    }
                    catch
                    {
                        try { listener.Close(); } catch { }
                    }
                }
                if (_listener == null) throw new BridgeException("Could not start the local mock server.");
                _loop = Task.Run(LoopAsync);
            }

            async Task LoopAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                    catch { break; }
                    _ = Task.Run(() => Handle(context));
                }
            }

            void Handle(HttpListenerContext context)
            {
                try
                {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    LastBody = body;
                    LastAuthorization = context.Request.Headers["Authorization"];
                    LastSession = context.Request.Headers["x-opencode-session"];
                    LastPath = context.Request.Url != null ? context.Request.Url.AbsolutePath : string.Empty;
                    var reply = _handler(context.Request, body) ?? new MockReply { Body = "{}" };
                    byte[] bytes = Encoding.UTF8.GetBytes(reply.Body ?? string.Empty);
                    context.Response.StatusCode = reply.Status;
                    context.Response.ContentType = reply.ContentType;
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.Close();
                }
                catch
                {
                    try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                }
            }

            public void Stop()
            {
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }

            public void Dispose()
            {
                Stop();
            }
        }

        static void Finish()
        {
            if (!Running) return;
            Running = false;
            EditorApplication.update -= Tick;

            try { AssetDatabase.AllowAutoRefresh(); } catch { }

            BridgeSettings.Current.AccessLevel = _savedAccessLevel;
            BridgeSettings.Current.AutoApplyWithWriteAccess = _savedAutoApply;
            BridgeSettings.Save();

            foreach (var id in _testChangeIds) { try { ChangeService.Remove(id); } catch { } }
            _testChangeIds.Clear();
            SafeDeleteFolder();
            AssetDatabase.Refresh();
            ChatService.ResumeBackgroundWork();

            WriteResults();
            BridgeLog.Info("tests", "Self tests finished: " + _passed + " passed, " + _failed + " failed.");
            Debug.Log("[Unity AI Bridge] Self tests: " + _passed + " passed, " + _failed + " failed. Results: " + BridgePaths.TestResultsFile);
        }

        static void WriteResults()
        {
            try
            {
                BridgePaths.EnsureDirectory(Path.GetDirectoryName(BridgePaths.TestResultsFile));
                var document = JsonValue.Obj(
                    "finished", DateTime.UtcNow.ToString("o"),
                    "duration_seconds", Math.Round(Time.realtimeSinceStartup - _startedAt, 2),
                    "passed", _passed,
                    "failed", _failed,
                    "results", JsonValue.Arr(_results.ToArray()));
                string json = document.ToJson(true);
                File.WriteAllText(BridgePaths.TestResultsFile, json, new UTF8Encoding(false));
                try
                {
                    BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
                    File.WriteAllText(Path.Combine(BridgePaths.ProjectConfigDir, "test-results.json"), json, new UTF8Encoding(false));
                }
                catch { }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Unity AI Bridge] Could not write test results: " + e.Message);
            }
        }

        static void WriteTestPng(string path, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool border = x < 2 || y < 2 || x >= width - 2 || y >= height - 2;
                    pixels[y * width + x] = border ? new Color32(255, 215, 0, 255) : new Color32(60, 90, 160, 255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
        }

#if !UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        static extern int symlink(string target, string linkPath);
#else
        static int symlink(string target, string linkPath) { return -1; }
#endif
    }
}
