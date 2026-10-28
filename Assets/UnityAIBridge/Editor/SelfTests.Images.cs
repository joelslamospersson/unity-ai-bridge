using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static JsonValue ImageFixture(string name)
        {
            return JsonValue.Obj("prompt", "A simple painted game icon.", "path", TestFolder + "/" + name + ".png",
                "size", "1024x1024", "quality", "low", "background", "transparent");
        }

        static IEnumerator RunImageGenerationTests()
        {
            FeatureCheck("image_settings_upgrade_keeps_generation_optin", () =>
            {
                var legacy = JsonUtility.FromJson<BridgeSettings>("{\"Provider\":\"chatgpt\"}");
                legacy.ImageGenerationModel = null;
                legacy.Normalize();
                RequireStream(!legacy.ImageGenerationEnabled && legacy.ImageGenerationModel == "gpt-image-2.5-sunburst", "Legacy settings did not acquire an image model while preserving opt-in billing.");
            });
            var settings = BridgeSettings.Current;
            bool savedEnabled = settings.ImageGenerationEnabled;
            string savedModel = settings.ImageGenerationModel, savedBase = settings.OpenAiBaseUrl, savedKey = BridgeSettings.Secrets.OpenAiApiKey;
            var metaField = typeof(ChatService).GetField("_meta", BindingFlags.Static | BindingFlags.NonPublic);
            var savedMeta = ChatService.CurrentConversation;
            var meta = ChatHistory.NewConversation("selftest-images", "fixture");
            metaField.SetValue(null, meta);
            int calls = 0;
            string png = Path.Combine(BridgePaths.TempDir, "image-api-fixture.png");
            WriteTestPng(png, 64, 64);
            string encoded = Convert.ToBase64String(File.ReadAllBytes(png));
            string reply = JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj("b64_json", encoded)), "usage", JsonValue.Obj("total_tokens", 10)).ToJson(false);
            int status = 200;
            int delayMs = 0;
            var incoming = new System.Collections.Generic.List<string>();
            using (var server = new LocalTestServer((request, body) =>
            {
                Interlocked.Increment(ref calls);
                if (delayMs > 0) Thread.Sleep(delayMs);
                return new MockReply { Status = status, Body = reply };
            }))
            try
            {
                settings.OpenAiBaseUrl = server.BaseUrl + "/v1";
                settings.ImageGenerationModel = "fixture-image";
                BridgeSettings.Secrets.OpenAiApiKey = "image-fixture-key";
                ChatApproval.BeginRun();
                settings.ImageGenerationEnabled = false;
                var disabled = Task.Run(async () =>
                {
                    try { await ImageGenerationService.GenerateAndStageAsync(ImageFixture("disabled"), CancellationToken.None, false, true); return false; }
                    catch (BridgeException e) { return e.Message.Contains("Enable image generation"); }
                });
                while (!disabled.IsCompleted) yield return null;
                Check("image_generation_disabled_sends_no_request", disabled.Status == TaskStatus.RanToCompletion && disabled.Result && calls == 0);
                settings.ImageGenerationEnabled = true;

                foreach (var pair in new[] { new[] { "path", "../escape.png" }, new[] { "size", "bad-size" }, new[] { "prompt", "" }, new[] { "background", "unsupported" } })
                {
                    var spec = ImageFixture("invalid"); spec[pair[0]] = pair[1];
                    var invalid = Task.Run(async () =>
                    {
                        try { await ImageGenerationService.GenerateAndStageAsync(spec, CancellationToken.None, false, true); return false; }
                        catch (BridgeException) { return true; }
                    });
                    while (!invalid.IsCompleted) yield return null;
                    Check("image_invalid_" + pair[0] + "_sends_no_request", invalid.Status == TaskStatus.RanToCompletion && invalid.Result && calls == 0);
                }

                ChatService.SetConversationAutoAccept(true);
                var denied = Task.Run(async () => await McpTools.InvokeAsync("generate_image", ImageFixture("denied"), CancellationToken.None));
                float deadline = Time.realtimeSinceStartup + 5;
                while (ChatApproval.Pending == null && !denied.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                var cost = ChatApproval.Pending;
                ChatService.SetConversationAutoAccept(true);
                Check("image_cost_consent_is_not_bypassed_by_auto_accept", cost != null && cost.RequireExplicitConsent && cost.Kind == "image-generation" && !denied.IsCompleted && calls == 0);
                if (cost != null) ChatApproval.Resolve(cost.Id, ApprovalChoice.Deny, "", false);
                while (!denied.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Check("image_denial_sends_no_billable_request", denied.Status == TaskStatus.RanToCompletion && denied.Result["structuredContent"]["status"].AsString() == "Denied" && calls == 0);
                ChatApproval.CancelForStop(); ChatApproval.ToolResultCommitted(); ChatService.SetConversationAutoAccept(false);

                var generate = Task.Run(async () => await McpTools.InvokeAsync("generate_image", ImageFixture("generated"), CancellationToken.None));
                deadline = Time.realtimeSinceStartup + 5;
                while (ChatApproval.Pending == null && !generate.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (ChatApproval.Pending != null) ChatApproval.Resolve(ChatApproval.Pending.Id, ApprovalChoice.Accept, "", false);
                while (!generate.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                bool generated = generate.Status == TaskStatus.RanToCompletion && !generate.Result["isError"].AsBool();
                string id = generated ? generate.Result["structuredContent"]["id"].AsString() : "";
                if (!string.IsNullOrEmpty(id)) _testChangeIds.Add(id);
                var change = ChangeService.Find(id);
                if (change != null) incoming.Add(change.Parameters["incoming_path"].AsString());
                Check("image_api_generation_stages_png_with_receipt", generated && calls == 1 && change != null && change.Status == ChangeStatus.Pending &&
                    change.Diff.Contains("fixture-image") && !File.Exists(BridgePaths.ToAbsolute(TestFolder + "/generated.png")) &&
                    !change.ParametersJson.Contains("image-fixture-key"), generate.Exception != null ? generate.Exception.GetBaseException().Message : null);
                var body = JsonValue.Parse(server.LastBody ?? "{}");
                Check("image_request_uses_api_key_and_png_parameters", server.LastPath == "/v1/images/generations" && server.LastAuthorization == "Bearer image-fixture-key" &&
                    body["model"].AsString() == "fixture-image" && body["output_format"].AsString() == "png" && body["background"].AsString() == "transparent" && body["n"].AsInt() == 1);
                FeatureCheck("image_import_requires_approval_and_preserves_alpha", () =>
                {
                    RequireStream(change != null && ChangeService.Apply(id) != null, "Generated image import bypassed review.");
                    ChangeService.Approve(id);
                    RequireStream(ChangeService.Apply(id) == null && AssetDatabase.LoadAssetAtPath<Sprite>(change.TargetPath) != null, "Generated PNG was not imported as a sprite.");
                    RequireStream((AssetImporter.GetAtPath(change.TargetPath) as TextureImporter).alphaIsTransparency, "Transparency import setting changed.");
                });
                ChatApproval.ToolResultCommitted();

                var collision = Task.Run(async () => await ImageGenerationService.GenerateAndStageAsync(ImageFixture("collision"), CancellationToken.None, false, true));
                while (!collision.IsCompleted) yield return null;
                FeatureCheck("image_import_rechecks_destination_collision", () =>
                {
                    RequireStream(collision.Status == TaskStatus.RanToCompletion, "Collision fixture did not generate.");
                    string collisionId = collision.Result["structuredContent"]["id"].AsString(); _testChangeIds.Add(collisionId);
                    var proposal = ChangeService.Find(collisionId); incoming.Add(proposal.Parameters["incoming_path"].AsString());
                    string full = BridgePaths.ToAbsolute(proposal.TargetPath);
                    WriteTestPng(full, 16, 16); AssetDatabase.ImportAsset(proposal.TargetPath);
                    byte[] before = File.ReadAllBytes(full); string guid = AssetDatabase.AssetPathToGUID(proposal.TargetPath);
                    ChangeService.Approve(collisionId);
                    RequireStream(ChangeService.Apply(collisionId) != null && before.SequenceEqual(File.ReadAllBytes(full)) && AssetDatabase.AssetPathToGUID(proposal.TargetPath) == guid, "Import overwrote a destination that appeared later.");
                });

                foreach (string payload in new[] { "not-base64!", Convert.ToBase64String(new byte[40]) })
                {
                    reply = JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj("b64_json", payload))).ToJson(false);
                    var invalid = Task.Run(async () =>
                    {
                        try { await ImageGenerationService.GenerateAndStageAsync(ImageFixture("bad-payload"), CancellationToken.None, false, true); return false; }
                        catch (BridgeException) { return true; }
                    });
                    while (!invalid.IsCompleted) yield return null;
                    Check(payload == "not-base64!" ? "image_rejects_bad_base64" : "image_rejects_non_png", invalid.Status == TaskStatus.RanToCompletion && invalid.Result && !File.Exists(BridgePaths.ToAbsolute(TestFolder + "/bad-payload.png")));
                }

                status = 429; reply = "{\"error\":{\"message\":\"image-fixture-key quota exceeded\"}}";
                int beforeCalls = calls;
                var limited = Task.Run(async () =>
                {
                    try { await ImageGenerationService.GenerateAndStageAsync(ImageFixture("rate-limited"), CancellationToken.None, false, true); return false; }
                    catch (BridgeException e) { return e.Message.Contains("429") && !e.Message.Contains("image-fixture-key"); }
                });
                while (!limited.IsCompleted) yield return null;
                Check("image_provider_failure_is_redacted_and_never_retried", limited.Status == TaskStatus.RanToCompletion && limited.Result && calls == beforeCalls + 1);

                FeatureCheck("image_window_requires_optin_prompt_and_cost_consent", () =>
                {
                    var window = ScriptableObject.CreateInstance<ImageGenerationWindow>();
                    try
                    {
                        typeof(ImageGenerationWindow).GetMethod("CreateGUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                        var root = window.rootVisualElement;
                        RequireStream(!root.Q<Button>("image-generate").enabledInHierarchy, "Generation was enabled without a prompt and cost consent.");
                        root.Q<TextField>("image-prompt").value = "Fixture prompt";
                        typeof(ImageGenerationWindow).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                        RequireStream(!root.Q<Button>("image-generate").enabledInHierarchy, "Generation was enabled without cost consent.");
                        root.Q<Toggle>("image-cost-consent").value = true;
                        typeof(ImageGenerationWindow).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                        RequireStream(root.Q<Button>("image-generate").enabledInHierarchy, "Consented image generation stayed disabled.");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(window); }
                });

                int requestsBeforeRestore = calls;
                var restored = Task.Run(async () =>
                {
                    try
                    {
                        await ImageGenerationService.GenerateAndStageAsync(ImageFixture("interrupted"), CancellationToken.None, false, false,
                            null, new ChatApprovalPrompt { Kind = "image-generation", ExternalRequestStarted = true });
                        return false;
                    }
                    catch (BridgeException e) { return e.Message.Contains("no automatic retry"); }
                });
                while (!restored.IsCompleted) yield return null;
                Check("image_reload_never_replays_submitted_paid_request", restored.Status == TaskStatus.RanToCompletion && restored.Result && calls == requestsBeforeRestore);

                var changed = Task.Run(async () => await McpTools.InvokeAsync("generate_image", ImageFixture("changed"), CancellationToken.None));
                deadline = Time.realtimeSinceStartup + 5;
                while (ChatApproval.Pending == null && !changed.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                settings.ImageGenerationModel = "changed-after-consent";
                if (ChatApproval.Pending != null) ChatApproval.Resolve(ChatApproval.Pending.Id, ApprovalChoice.Accept, "", false);
                while (!changed.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Check("image_changed_billing_options_require_new_consent", changed.Status == TaskStatus.RanToCompletion && changed.Result["isError"].AsBool() && calls == requestsBeforeRestore);
                settings.ImageGenerationModel = "fixture-image";
                ChatApproval.CancelForStop(); ChatApproval.ToolResultCommitted();

                settings.ImageGenerationModel = "changed-during-reload";
                if (cost != null)
                {
                    cost.Resolution = "Accept";
                    typeof(ChatApproval).GetField("_journal", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, cost);
                }
                var resumedOptions = Task.Run(async () =>
                {
                    if (cost == null) return false;
                    try
                    {
                        await ImageGenerationService.GenerateAndStageAsync(JsonValue.Parse(cost.Tool.Arguments), CancellationToken.None, false, false, cost.Tool, cost);
                        return false;
                    }
                    catch (BridgeException e) { return e.Message.Contains("options changed"); }
                });
                while (!resumedOptions.IsCompleted) yield return null;
                Check("image_restored_consent_preserves_billing_fingerprint", resumedOptions.Status == TaskStatus.RanToCompletion && resumedOptions.Result && calls == requestsBeforeRestore);
                settings.ImageGenerationModel = "fixture-image";
                ChatApproval.CancelForStop(); ChatApproval.ToolResultCommitted();

                status = 200; delayMs = 500;
                reply = JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj("b64_json", encoded))).ToJson(false);
                int callsBeforeRace = calls;
                var lateCollision = Task.Run(async () => await ImageGenerationService.GenerateAndStageAsync(ImageFixture("late-collision"), CancellationToken.None, false, true));
                deadline = Time.realtimeSinceStartup + 5;
                while (calls == callsBeforeRace && !lateCollision.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                string racedFile = BridgePaths.ToAbsolute(TestFolder + "/late-collision.png");
                WriteTestPng(racedFile, 16, 16);
                byte[] racedBytes = File.ReadAllBytes(racedFile);
                while (!lateCollision.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                bool retained = lateCollision.Status == TaskStatus.RanToCompletion && lateCollision.Result["structuredContent"]["status"].AsString() == "GeneratedNeedsImport";
                string retainedPath = retained ? lateCollision.Result["structuredContent"]["preview_path"].AsString() : "";
                incoming.Add(retainedPath);
                Check("image_keeps_paid_preview_when_import_destination_changes", retained && File.Exists(retainedPath) &&
                    racedBytes.SequenceEqual(File.ReadAllBytes(racedFile)) && calls == callsBeforeRace + 1);
                using (var stop = new CancellationTokenSource())
                {
                    stop.CancelAfter(150);
                    var cancelled = Task.Run(async () =>
                    {
                        try { await ImageGenerationService.GenerateAndStageAsync(ImageFixture("cancelled"), stop.Token, false, true); return false; }
                        catch (OperationCanceledException) { return true; }
                    });
                    while (!cancelled.IsCompleted) yield return null;
                    Check("image_stop_cancels_without_staging_or_retry", cancelled.Status == TaskStatus.RanToCompletion && cancelled.Result &&
                        !ChangeService.All.Any(c => c.TargetPath == TestFolder + "/cancelled.png"));
                }
            }
            finally
            {
                ChatApproval.CancelForStop(); ChatApproval.ToolResultCommitted();
                metaField.SetValue(null, savedMeta); ChatHistory.ClearProvider("selftest-images");
                settings.ImageGenerationEnabled = savedEnabled; settings.ImageGenerationModel = savedModel;
                settings.OpenAiBaseUrl = savedBase; BridgeSettings.Secrets.OpenAiApiKey = savedKey;
                foreach (string path in incoming) if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
        }
    }
}
