using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    // Standalone integration check run in an isolated project with a localhost mock.
    // The flag survives an actual Unity assembly reload; no production model is used.
    [InitializeOnLoad]
    internal static class ChatApprovalReloadValidation
    {
        const string Folder = "Assets/__ApprovalReloadValidation__";
        static string Flag { get { return Path.Combine(BridgePaths.LibraryRoot, "approval-reload-validation.json"); } }
        static string BackupSettings { get { return Path.Combine(BridgePaths.LibraryRoot, "approval-validation-settings.json"); } }
        static string BackupSecrets { get { return Path.Combine(BridgePaths.LibraryRoot, "approval-validation-secrets.json"); } }
        static bool _monitoring;

        static ChatApprovalReloadValidation()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode && File.Exists(Flag))
                {
                    var state = JsonValue.Parse(File.ReadAllText(Flag));
                    state["reloads"] = state["reloads"].AsInt() + 1;
                    File.WriteAllText(Flag, state.ToJson(false));
                    StartMonitor();
                }
            };
        }

        public static void Start()
        {
            if (!Application.isBatchMode || !BridgePaths.ProjectRoot.StartsWith("/tmp/unity-bridge-", StringComparison.Ordinal))
                throw new InvalidOperationException("Run this integration check in an isolated /tmp/unity-bridge-* batch project.");
            File.WriteAllText(BackupSettings, JsonUtility.ToJson(BridgeSettings.Current));
            File.WriteAllText(BackupSecrets, JsonUtility.ToJson(BridgeSettings.Secrets));
            var settings = BridgeSettings.Current;
            settings.Provider = BridgeSettings.ProviderOpenAi;
            settings.OpenAiBaseUrl = "http://127.0.0.1:7860/v1";
            settings.OpenAiModel = "gpt-test";
            settings.ChatMode = "agent";
            settings.AccessLevel = BridgeSettings.AccessAsk;
            settings.AutoApplyWithWriteAccess = false;
            BridgeSettings.Secrets.OpenAiApiKey = "local-test-key";
            BridgeSettings.Save(); BridgeSettings.SaveSecrets();
            File.WriteAllText(Flag, JsonValue.Obj("reloads", 0, "started", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "accepted", false).ToJson(false));
            ChatService.EnsureLoaded();
            ChatService.NewChat();
            if (!ChatService.Send("Create the approval reload validation script, then verify and continue."))
            { Finish(false, "The test chat did not start."); return; }
            StartMonitor();
        }

        static void StartMonitor()
        {
            if (_monitoring) return;
            _monitoring = true;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (!File.Exists(Flag)) return;
            try
            {
                var state = JsonValue.Parse(File.ReadAllText(Flag));
                var prompt = ChatApproval.Pending;
                if (prompt != null)
                {
                    if (!prompt.CompilesScripts) { Finish(false, "Unexpected approval scope."); return; }
                    state["accepted"] = true; File.WriteAllText(Flag, state.ToJson(false));
                    ChatApproval.Resolve(prompt.Id, ApprovalChoice.Accept, "Continue after compilation.", true);
                }
                bool final = ChatService.Messages.Exists(m => m.Role == "assistant" && !m.IsStreaming && (m.Content ?? string.Empty).Contains("RESUMED_AFTER_SCRIPT_APPROVAL"));
                if (state["reloads"].AsInt() > 0 && !ChatService.IsBusy && final)
                {
                    bool valid = state["accepted"].AsBool() && File.Exists(BridgePaths.ToAbsolute(Folder + "/Generated.cs")) && ConsoleService.GetCompilerDiagnostics(200).Count == 0;
                    Finish(valid, "Assembly reloads=" + state["reloads"].AsInt() + "; resumed final answer=" + final);
                }
                else if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - state["started"].AsNumber() > 70)
                    Finish(false, "Timed out: reloads=" + state["reloads"].AsInt() + "; busy=" + ChatService.IsBusy + "; " + ChatService.StatusMessage + "; " + ChatService.LastError);
            }
            catch (Exception e) { Finish(false, e.ToString()); }
        }

        static void Finish(bool ok, string detail)
        {
            EditorApplication.update -= Poll;
            _monitoring = false;
            if (File.Exists(Flag)) File.Delete(Flag);
            ChatService.Stop();
            JsonUtility.FromJsonOverwrite(File.ReadAllText(BackupSettings), BridgeSettings.Current);
            JsonUtility.FromJsonOverwrite(File.ReadAllText(BackupSecrets), BridgeSettings.Secrets);
            BridgeSettings.Save(); BridgeSettings.SaveSecrets();
            File.Delete(BackupSettings); File.Delete(BackupSecrets);
            AssetDatabase.DeleteAsset(Folder);
            File.WriteAllText(Path.Combine(BridgePaths.LibraryRoot, "approval-reload-result.json"), JsonValue.Obj("ok", ok, "detail", detail).ToJson(true));
            Debug.Log("[Unity AI Bridge] Approval reload validation: " + ok + " " + detail);
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
