using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    [InitializeOnLoad]
    public static class UnityAIBridgeBootstrap
    {
        public const string MenuRoot = "Tools/Unity AI Bridge";

        static UnityAIBridgeBootstrap()
        {
            MainThread.Init();
            ConsoleService.Init();
            BridgeSettings.Load();

            AssemblyReloadEvents.beforeAssemblyReload += McpServer.StopForReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.delayCall += OnEditorLoaded;
        }

        static void OnAfterAssemblyReload()
        {
            BridgeSettings.Load();
            if (BridgeSettings.Current.McpEnabled && BridgeSettings.Current.McpAutoStart)
                McpServer.StartFromSettings();
        }

        static void OnEditorLoaded()
        {
            BridgePaths.EnsureMigrated();

            if (BridgeSettings.Current.McpEnabled && BridgeSettings.Current.McpAutoStart && McpServer.Current == null)
                McpServer.StartFromSettings();

            if (File.Exists(BridgePaths.TestFlagFile))
            {
                try { File.Delete(BridgePaths.TestFlagFile); } catch { }
                EditorApplication.delayCall += SelfTests.Start;
            }
        }

        [MenuItem(MenuRoot, false, 0)]
        public static void OpenWindow()
        {
            var window = EditorWindow.GetWindow<UnityAIBridgeWindow>("Unity AI Bridge");
            window.minSize = new Vector2(900f, 560f);
            window.Show();
        }

        [MenuItem(MenuRoot + "/Open Window", false, 1)]
        public static void OpenWindowMenu() { OpenWindow(); }

        [MenuItem(MenuRoot + "/Run Self Tests", false, 20)]
        public static void RunSelfTests()
        {
            SelfTests.Start();
        }

        [MenuItem(MenuRoot + "/MCP Server/Start", false, 40)]
        public static void StartMcp()
        {
            McpServer.StartFromSettings();
            OpenWindow();
        }

        [MenuItem(MenuRoot + "/MCP Server/Start", true)]
        public static bool StartMcpValidate() { return McpServer.Current == null || !McpServer.Current.IsRunning; }

        [MenuItem(MenuRoot + "/MCP Server/Stop", false, 41)]
        public static void StopMcp()
        {
            McpServer.StopCurrent();
            BridgeSettings.Current.McpEnabled = false;
            BridgeSettings.Save();
        }

        [MenuItem(MenuRoot + "/MCP Server/Stop", true)]
        public static bool StopMcpValidate() { return McpServer.Current != null && McpServer.Current.IsRunning; }

        [MenuItem(MenuRoot + "/MCP Server/Copy Client Config (JSON)", false, 42)]
        public static void CopyMcpConfig()
        {
            EditorGUIUtility.systemCopyBuffer = McpServer.ClientConfigJson();
            Debug.Log("[Unity AI Bridge] MCP client config copied to the clipboard (it contains your local bearer token; do not share it).");
        }

        [MenuItem(MenuRoot + "/MCP Server/Copy Codex Command", false, 43)]
        public static void CopyCodexCommand()
        {
            EditorGUIUtility.systemCopyBuffer = McpServer.CodexCommand();
            Debug.Log("[Unity AI Bridge] Codex MCP command copied to the clipboard.");
        }

        [MenuItem(MenuRoot + "/Open Config Folder", false, 60)]
        public static void OpenConfigFolder()
        {
            BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
            EditorUtility.RevealInFinder(BridgePaths.ProjectConfigDir);
        }

        [MenuItem(MenuRoot + "/Open Backups Folder", false, 61)]
        public static void OpenBackupsFolder()
        {
            BridgePaths.EnsureDirectory(BridgePaths.BackupsDir);
            EditorUtility.RevealInFinder(BridgePaths.BackupsDir);
        }

        [MenuItem(MenuRoot + "/Documentation", false, 80)]
        public static void OpenDocumentation()
        {
            string path = Path.Combine(BridgePaths.ProjectRoot, "Documentation", "UnityAIBridge", "README.md");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[Unity AI Bridge] Documentation not found at " + path);
                return;
            }
            EditorUtility.OpenWithDefaultApp(path);
        }

        [MenuItem(MenuRoot + "/Reset Settings", false, 100)]
        public static void ResetSettings()
        {
            if (!EditorUtility.DisplayDialog("Unity AI Bridge",
                    "Reset all bridge settings and secrets (API key, MCP token) for this project? Pending changes and backups are kept.",
                    "Reset", "Cancel")) return;
            BridgeSettings.Reset();
            BridgeLog.Warn("settings", "Settings and secrets were reset.");
        }
    }
}
