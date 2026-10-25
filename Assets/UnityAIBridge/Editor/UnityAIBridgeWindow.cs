using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAIBridge
{
    public sealed class UnityAIBridgeWindow : EditorWindow
    {
        static readonly string[] TabNames = { "Chat", "Explorer", "Scene", "Assets", "Changes", "MCP", "Settings", "Logs" };
        static readonly string[] ProviderIds = { BridgeSettings.ProviderChatGpt, BridgeSettings.ProviderDeepSeek, BridgeSettings.ProviderOpenCode, BridgeSettings.ProviderOpenAi, BridgeSettings.ProviderOllama };
        static readonly string[] ProviderLabels = { "ChatGPT", "DeepSeek", "OpenCode Go", "OpenAI API", "Ollama (local)" };
        static readonly string[] LevelLabels = { "Read Only", "Ask Before Editing", "Project Write Access" };
        static readonly string[] PrimitiveLabels = { "Empty", "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        static readonly string[] PrimitiveValues = { "empty", "cube", "sphere", "capsule", "cylinder", "plane", "quad" };
        static readonly string[] FilterLabels = { "Point", "Bilinear", "Trilinear" };
        static readonly string[] CompressionLabels = { "Uncompressed", "Compressed", "Compressed HQ", "Compressed LQ" };
        static readonly string[] CompressionValues = { "Uncompressed", "Compressed", "CompressedHQ", "CompressedLQ" };
        static readonly string[] SpriteModeLabels = { "Single", "Multiple" };
        static readonly string[] ComponentLabels = { "SpriteRenderer", "Image" };

        int _tab;
        bool _needsRepaint;

        Vector2 _chatScroll, _treeScroll, _explorerListScroll, _previewScroll, _sceneScroll, _inspectorScroll;
        Vector2 _assetListScroll, _assetDetailScroll, _changesListScroll, _diffScroll, _mcpScroll, _settingsScroll, _logsScroll;

        string _input = string.Empty;
        bool _followOutput = true;
        readonly HashSet<string> _expandedReasoning = new HashSet<string>();

        string _explorerDir = "Assets";
        string _previewPath;
        bool _showNewFile;
        string _newFilePath = "Assets/Scripts/NewScript.cs";
        string _newFileContent = "using UnityEngine;\n\npublic class NewScript : MonoBehaviour\n{\n}\n";
        readonly HashSet<string> _expandedFolders = new HashSet<string>();
        readonly HashSet<string> _expandedScene = new HashSet<string>();

        string _goName = "NewUnit";
        string _goParent = string.Empty;
        string _goSprite = string.Empty;
        int _goPrimitive = 1;
        Vector3 _goPosition = Vector3.zero;
        string _prefabPath = "Assets/Prefabs/NewPrefab.prefab";

        string _assetFolder = "Assets";
        List<string> _sprites = new List<string>();
        int _spriteIndex = -1;
        string _importSource = string.Empty;
        string _importDest = "Assets/Art/Characters/NewSprite.png";
        int _spriteMode;
        float _ppu = 100f;
        int _filterIndex = 1;
        int _compressionIndex = 1;
        int _maxSize = 2048;
        bool _alphaTransparency = true;
        bool _mipmaps;
        bool _readable;
        int _sliceRows = 4;
        int _sliceColumns = 4;
        int _assignComponent;
        string _assignTarget = string.Empty;
        string _materialPath = "Assets/Materials/NewMaterial.mat";
        Color _materialColor = Color.white;
        string _materialTexture = string.Empty;

        string _selectedChangeId;
        bool _showRejected = true;

        string _mcpPortText = string.Empty;
        bool _revealToken;
        bool _mcpTesting;
        string _mcpTestResult;
        string _deepSeekKeyDraft = string.Empty;
        string _openCodeKeyDraft = string.Empty;
        string _apiKeyDraft = string.Empty;
        string _chatGptNotice = string.Empty;
        string _feedback = string.Empty;
        bool _feedbackError;

        bool _logInfo = true, _logWarn = true, _logError = true;
        string _diffCacheId;
        string _diffCacheText;

        [MenuItem("Window/Unity AI Bridge")]
        static void OpenFromWindowMenu() { UnityAIBridgeBootstrap.OpenWindow(); }

        void OnEnable()
        {
            titleContent = new GUIContent("Unity AI Bridge");
            minSize = new Vector2(900f, 560f);
            ChatService.EnsureLoaded();
            ChangeService.Changed += MarkDirty;
            ChatService.Changed += MarkDirty;
            BridgeLog.Changed += MarkDirty;
            _mcpPortText = BridgeSettings.Current.McpPort.ToString();
            if (_sprites.Count == 0) RefreshSpriteList();
        }

        void OnDisable()
        {
            ChangeService.Changed -= MarkDirty;
            ChatService.Changed -= MarkDirty;
            BridgeLog.Changed -= MarkDirty;
        }

        void MarkDirty() { _needsRepaint = true; }

        void Update()
        {
            if (!_needsRepaint) return;
            _needsRepaint = false;
            Repaint();
        }

        void OnGUI()
        {
            DrawHeader();
            _tab = GUILayout.Toolbar(_tab, TabNames, EditorStyles.toolbarButton, GUILayout.Height(22f));
            EditorGUILayout.Space(4f);

            switch (_tab)
            {
                case 0: DrawChat(); break;
                case 1: DrawExplorer(); break;
                case 2: DrawScene(); break;
                case 3: DrawAssets(); break;
                case 4: DrawChanges(); break;
                case 5: DrawMcp(); break;
                case 6: DrawSettings(); break;
                default: DrawLogs(); break;
            }

            DrawStatusBar();
        }

        // ---------------------------------------------------------------- header

        void DrawHeader()
        {
            var settings = BridgeSettings.Current;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Unity AI Bridge", EditorStyles.boldLabel, GUILayout.Width(140f));

                int providerIndex = Math.Max(0, Array.IndexOf(ProviderIds, ChatService.CurrentProviderId));
                int newProvider = EditorGUILayout.Popup(providerIndex, ProviderLabels, EditorStyles.toolbarPopup, GUILayout.Width(108f));
                if (newProvider != providerIndex)
                {
                    ChatService.SwitchProvider(ProviderIds[newProvider]);
                    SetFeedback("Provider: " + ProviderLabels[newProvider] + ".");
                }

                GUILayout.Space(4f);
                DrawModelSelector();

                GUILayout.Space(6f);
                DrawConnectionBadge();

                GUILayout.FlexibleSpace();
                GUILayout.Label("Access:", EditorStyles.miniLabel, GUILayout.Width(42f));
                int level = (int)settings.Level;
                int newLevel = EditorGUILayout.Popup(level, LevelLabels, EditorStyles.toolbarPopup, GUILayout.Width(142f));
                if (newLevel != level)
                {
                    Permissions.SetLevel((BridgeSettings.AccessMode)newLevel);
                    SetFeedback("Permission level: " + LevelLabels[newLevel] + ".");
                }

                GUILayout.Space(6f);
                if (GUILayout.Button(ChangeService.PendingCount + " pending", EditorStyles.toolbarButton, GUILayout.Width(78f))) _tab = 4;
                if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(46f))) UnityAIBridgeBootstrap.OpenDocumentation();
                if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(62f))) _tab = 6;
            }
        }

        void DrawModelSelector()
        {
            var models = ChatService.Models;
            string current = ChatService.CurrentModel;
            int index = models.FindIndex(m => m.Id == current);
            if (index < 0) index = 0;

            if (models.Count == 0)
            {
                GUILayout.Label(ChatService.IsLoadingModels ? "Models..." : "No model", EditorStyles.toolbarPopup, GUILayout.Width(190f));
            }
            else
            {
                var names = new string[models.Count];
                for (int i = 0; i < models.Count; i++) names[i] = models[i].ToString();
                int selected = EditorGUILayout.Popup(index, names, EditorStyles.toolbarPopup, GUILayout.Width(210f));
                if (selected != index) ChatService.SelectModel(models[selected].Id);
            }
            using (new EditorGUI.DisabledScope(ChatService.IsLoadingModels))
            {
                if (GUILayout.Button(ChatService.IsLoadingModels ? "..." : "Refresh", EditorStyles.toolbarButton, GUILayout.Width(58f)))
                    ChatService.RefreshModels(true);
            }
        }

        void DrawConnectionBadge()
        {
            Color color = new Color(0.85f, 0.75f, 0.35f);
            string text = "Not configured";
            switch (ChatService.ConnectionState)
            {
                case ProviderConnectionState.Connected: color = new Color(0.42f, 0.85f, 0.52f); text = "Connected"; break;
                case ProviderConnectionState.Ready: color = new Color(0.85f, 0.78f, 0.42f); text = "Ready"; break;
                case ProviderConnectionState.SigningIn: color = new Color(0.55f, 0.72f, 1f); text = "Signing in"; break;
                case ProviderConnectionState.Error: color = new Color(1f, 0.5f, 0.5f); text = "Error"; break;
                case ProviderConnectionState.NotConfigured: text = "Not configured"; break;
            }
            GUI.contentColor = color;
            GUILayout.Label(new GUIContent("● " + text, ChatService.ConnectionMessage ?? string.Empty), EditorStyles.miniLabel, GUILayout.Width(94f));
            GUI.contentColor = Color.white;
        }

        void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                if (_feedbackError) GUI.contentColor = new Color(1f, 0.55f, 0.55f);
                GUILayout.Label(string.IsNullOrEmpty(_feedback) ? StatusSummary() : _feedback, EditorStyles.miniLabel);
                GUI.contentColor = Color.white;
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_feedback) && GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(20f)))
                    _feedback = string.Empty;
            }
        }

        string StatusSummary()
        {
            var sb = new StringBuilder();
            sb.Append(Permissions.LevelName);
            sb.Append(" | ").Append(ProviderFactory.DisplayName(ChatService.CurrentProviderId));
            if (!string.IsNullOrEmpty(ChatService.CurrentModel)) sb.Append(" · ").Append(ChatService.CurrentModel);
            sb.Append(" | ").Append(ChatService.ConnectionState);
            if (ChangeService.PendingCount > 0) sb.Append(" | ").Append(ChangeService.PendingCount).Append(" pending");
            if (ChatService.IsBusy && !string.IsNullOrEmpty(ChatService.StatusMessage)) sb.Append(" | ").Append(ChatService.StatusMessage);
            else if (!ChatService.IsBusy && !string.IsNullOrEmpty(ChatService.StatusMessage)) sb.Append(" | ").Append(ChatService.StatusMessage);
            return sb.ToString();
        }

        void SetFeedback(string message, bool error = false)
        {
            _feedback = message ?? string.Empty;
            _feedbackError = error;
            if (error) BridgeLog.Warn("ui", message);
            Repaint();
        }

        // ---------------------------------------------------------------- chat

        void DrawChat()
        {
            var settings = BridgeSettings.Current;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Context:", EditorStyles.miniLabel, GUILayout.Width(48f));
                settings.IncludeSceneContext = GUILayout.Toggle(settings.IncludeSceneContext, "Scene", EditorStyles.toolbarButton, GUILayout.Width(56f));
                settings.IncludeConsoleContext = GUILayout.Toggle(settings.IncludeConsoleContext, "Console", EditorStyles.toolbarButton, GUILayout.Width(66f));
                settings.IncludeFileTree = GUILayout.Toggle(settings.IncludeFileTree, "Tree", EditorStyles.toolbarButton, GUILayout.Width(46f));
                settings.IncludeExistingScripts = GUILayout.Toggle(settings.IncludeExistingScripts, "Scripts", EditorStyles.toolbarButton, GUILayout.Width(60f));
                GUILayout.Space(8f);
                settings.ShowReasoning = GUILayout.Toggle(settings.ShowReasoning, "Reasoning", EditorStyles.toolbarButton, GUILayout.Width(78f));
                GUILayout.FlexibleSpace();
                _followOutput = GUILayout.Toggle(_followOutput, "Follow", EditorStyles.toolbarButton, GUILayout.Width(56f));
                if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(46f))) BridgeSettings.Save();
                if (GUILayout.Button("Clear chat", EditorStyles.toolbarButton, GUILayout.Width(76f)))
                {
                    if (EditorUtility.DisplayDialog("Clear chat", "Clear the conversation for " + ProviderFactory.DisplayName(ChatService.CurrentProviderId) + "?", "Clear", "Cancel"))
                        ChatService.Clear();
                }
            }

            DrawAccountBar();

            if (ChatService.AttachedFiles.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Attached:", EditorStyles.miniLabel, GUILayout.Width(56f));
                    for (int i = ChatService.AttachedFiles.Count - 1; i >= 0; i--)
                    {
                        string file = ChatService.AttachedFiles[i];
                        if (GUILayout.Button(Path.GetFileName(file) + "  x", EditorStyles.miniButton, GUILayout.Width(150f)))
                            ChatService.RemoveAttachment(file);
                    }
                }
            }

            if (!string.IsNullOrEmpty(ChatService.LastError) && !ChatService.IsBusy)
                EditorGUILayout.HelpBox(ChatService.LastError, MessageType.Error);

            using (var scroll = new EditorGUILayout.ScrollViewScope(_chatScroll, GUILayout.ExpandHeight(true)))
            {
                _chatScroll = scroll.scrollPosition;
                var messages = ChatService.Messages;
                if (messages.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Ask the assistant to inspect and work on your Unity project. It has direct Unity tools and every edit is staged for your approval.\n\n" +
                        "Try: \"Inspect my current Unity scene.\" or \"Create a basic player controller.\"",
                        MessageType.Info);
                }
                foreach (var message in messages) DrawChatMessage(message);
            }

            if (_followOutput && (ChatService.IsBusy || (ChatService.Messages.Count > 0 && ChatService.Messages[ChatService.Messages.Count - 1].IsStreaming)))
                _chatScroll.y = float.MaxValue;

            if (ChatService.IsBusy)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(ChatService.StatusMessage ?? "Working...", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Stop generation", EditorStyles.miniButton, GUILayout.Width(110f))) ChatService.Stop();
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _input = EditorGUILayout.TextArea(_input, GUILayout.Height(68f));
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool canSend = !ChatService.IsBusy && _input.Trim().Length > 0 && ChatService.Models.Count > 0;
                    using (new EditorGUI.DisabledScope(!canSend))
                    {
                        if (GUILayout.Button("Send", GUILayout.Width(86f), GUILayout.Height(24f)))
                        {
                            string text = _input;
                            _input = string.Empty;
                            ChatService.Send(text);
                        }
                    }
                    using (new EditorGUI.DisabledScope(!ChatService.IsBusy))
                    {
                        if (GUILayout.Button("Stop", EditorStyles.miniButton, GUILayout.Width(60f))) ChatService.Stop();
                    }
                    if (GUILayout.Button("Quick prompts ▾", EditorStyles.miniButton, GUILayout.Width(110f)))
                    {
                        var menu = new GenericMenu();
                        AddQuickPrompt(menu, "Inspect my current Unity scene.");
                        AddQuickPrompt(menu, "Create a basic player controller.");
                        AddQuickPrompt(menu, "Find and explain the errors currently displayed in the Unity Console.");
                        AddQuickPrompt(menu, "Import a texture and configure it as a sprite.");
                        AddQuickPrompt(menu, "Create a reusable prefab from the selected object.");
                        AddQuickPrompt(menu, "Review the project structure and suggest improvements.");
                        AddQuickPrompt(menu, "Create a simple input and camera setup for the current project.");
                        AddQuickPrompt(menu, "Create a small inventory system using the project's existing conventions.");
                        menu.ShowAsContext();
                    }
                    GUILayout.FlexibleSpace();
                    if (ChangeService.PendingCount > 0)
                    {
                        if (GUILayout.Button("Review " + ChangeService.PendingCount + " change(s)", EditorStyles.miniButton, GUILayout.Width(150f))) _tab = 4;
                    }
                    if (ChatService.IsBusy)
                    {
                        GUILayout.Label("Streaming...", EditorStyles.miniLabel, GUILayout.Width(74f));
                    }
                }
            }
        }

        void DrawAccountBar()
        {
            string provider = ChatService.CurrentProviderId;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (provider == BridgeSettings.ProviderChatGpt) DrawChatGptAccount();
                else if (provider == BridgeSettings.ProviderDeepSeek) DrawDeepSeekAccount();
                else if (provider == BridgeSettings.ProviderOpenCode) DrawOpenCodeAccount();
                else if (provider == BridgeSettings.ProviderOpenAi) DrawOpenAiAccount();
                else if (provider == BridgeSettings.ProviderOllama) DrawOllamaAccount();

                var billing = ProviderFactory.BillingNote(provider);
                if (!string.IsNullOrEmpty(billing)) GUILayout.Label(billing, EditorStyles.miniLabel);
            }
        }

        void DrawChatGptAccount()
        {
            if (ChatGptAuth.IsBusy)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Waiting for ChatGPT sign-in in your browser...", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(70f))) ChatGptAuth.CancelSignIn();
                }
                return;
            }

            if (ChatGptAuth.IsReadyForInference)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Signed in as " + ChatGptAuth.AccountLabel, EditorStyles.boldLabel);
                    GUILayout.Label("· ChatGPT plan usage enabled (shared plan limits)", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Reconnect", EditorStyles.miniButton, GUILayout.Width(82f))) ChatGptAuth.StartSignIn(false);
                    if (GUILayout.Button("Sign out", EditorStyles.miniButton, GUILayout.Width(70f))) SignOutChatGpt();
                }
                return;
            }

            if (ChatGptAuth.Status == ChatGptStatus.PlanUsageUnavailable)
            {
                EditorGUILayout.HelpBox("Signed in as " + ChatGptAuth.AccountLabel + ", but ChatGPT plan usage was not granted. Reconnect and allow plan usage, or use the OpenAI API provider with your own key.", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reconnect & allow plan usage", GUILayout.Width(190f))) ChatGptAuth.StartSignIn(true);
                    if (GUILayout.Button("Use OpenAI API", EditorStyles.miniButton, GUILayout.Width(120f))) ChatService.SwitchProvider(BridgeSettings.ProviderOpenAi);
                    if (GUILayout.Button("Sign out", EditorStyles.miniButton, GUILayout.Width(70f))) SignOutChatGpt();
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Continue with ChatGPT", GUILayout.Width(180f), GUILayout.Height(24f))) ChatGptAuth.StartSignIn(false);
                    GUILayout.Label("Opens your browser. No API key. Uses your ChatGPT plan's shared usage limits (Plus: 5-hour limit across apps; Pro: no 5-hour limit).", EditorStyles.miniLabel);
                }
                if (!string.IsNullOrEmpty(ChatGptAuth.LastError)) EditorGUILayout.HelpBox(ChatGptAuth.LastError, MessageType.Warning);
            }
        }

        void DrawDeepSeekAccount()
        {
            if (!string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("DeepSeek API key configured", EditorStyles.boldLabel);
                    GUILayout.Label("· " + ChatService.ConnectionState, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                    {
                        if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(112f)))
                            ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Settings", EditorStyles.miniButton, GUILayout.Width(68f))) _tab = 6;
                    if (GUILayout.Button("Clear key", EditorStyles.miniButton, GUILayout.Width(70f)))
                    {
                        BridgeSettings.Secrets.DeepSeekApiKey = string.Empty;
                        BridgeSettings.SaveSecrets();
                        ChatService.RefreshModels(true);
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _deepSeekKeyDraft = EditorGUILayout.PasswordField("DeepSeek API key", _deepSeekKeyDraft, GUILayout.Width(360f));
                    if (GUILayout.Button("Save & test", EditorStyles.miniButton, GUILayout.Width(90f)))
                    {
                        BridgeSettings.Secrets.DeepSeekApiKey = _deepSeekKeyDraft.Trim();
                        BridgeSettings.SaveSecrets();
                        _deepSeekKeyDraft = string.Empty;
                        ChatService.RefreshModels(true);
                        ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Get a key", EditorStyles.miniButton, GUILayout.Width(70f)))
                        Application.OpenURL("https://platform.deepseek.com/api_keys");
                }
                if (!string.IsNullOrEmpty(ChatService.LastTestResult)) GUILayout.Label(ChatService.LastTestResult, EditorStyles.miniLabel);
            }
        }

        void DrawOpenCodeAccount()
        {
            if (!string.IsNullOrEmpty(BridgeSettings.Secrets.OpenCodeApiKey))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("OpenCode Go API key configured", EditorStyles.boldLabel);
                    GUILayout.Label("· " + ChatService.ConnectionState, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                    {
                        if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(112f)))
                            ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Settings", EditorStyles.miniButton, GUILayout.Width(68f))) _tab = 6;
                    if (GUILayout.Button("Clear key", EditorStyles.miniButton, GUILayout.Width(70f)))
                    {
                        BridgeSettings.Secrets.OpenCodeApiKey = string.Empty;
                        BridgeSettings.SaveSecrets();
                        ChatService.RefreshModels(true);
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _openCodeKeyDraft = EditorGUILayout.PasswordField("OpenCode Go API key", _openCodeKeyDraft, GUILayout.Width(340f));
                    if (GUILayout.Button("Save & test", EditorStyles.miniButton, GUILayout.Width(90f)))
                    {
                        BridgeSettings.Secrets.OpenCodeApiKey = _openCodeKeyDraft.Trim();
                        BridgeSettings.SaveSecrets();
                        _openCodeKeyDraft = string.Empty;
                        OpenCodeGoProvider.ResetResolvedBase();
                        ChatService.RefreshModels(true);
                        ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Get a key", EditorStyles.miniButton, GUILayout.Width(74f)))
                        Application.OpenURL("https://opencode.ai/auth");
                }
                if (!string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
                    EditorGUILayout.HelpBox("A key is saved for DeepSeek. If that key is actually an OpenCode Go key (the DeepSeek endpoint rejects it), reuse it here.", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
                    {
                        if (GUILayout.Button("Reuse the key saved for DeepSeek", EditorStyles.miniButton, GUILayout.Width(210f)))
                            ReuseDeepSeekKeyForOpenCode();
                    }
                    GUILayout.Label("Default endpoint: " + BridgeSettings.Current.OpenCodeBaseUrl, EditorStyles.miniLabel);
                }
            }
            if (!string.IsNullOrEmpty(ChatService.LastTestResult)) GUILayout.Label(ChatService.LastTestResult, EditorStyles.miniLabel);
            if (ChatService.ConnectionState == ProviderConnectionState.Error && !string.IsNullOrEmpty(ChatService.ConnectionMessage))
                EditorGUILayout.HelpBox(ChatService.ConnectionMessage, MessageType.Error);
        }

        void ReuseDeepSeekKeyForOpenCode()
        {
            if (string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey)) return;
            if (!EditorUtility.DisplayDialog("Reuse saved key?",
                    "Copy the API key currently saved for the DeepSeek provider to OpenCode Go? The key is not displayed or logged.",
                    "Use this key", "Cancel")) return;
            BridgeSettings.Secrets.OpenCodeApiKey = BridgeSettings.Secrets.DeepSeekApiKey;
            BridgeSettings.SaveSecrets();
            OpenCodeGoProvider.ResetResolvedBase();
            ChatService.RefreshModels(true);
            ChatService.TestConnection();
        }

        void ReuseDeepSeekKeyForOpenCodeAndSwitch()
        {
            if (string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey)) return;
            if (!EditorUtility.DisplayDialog("Switch provider?",
                    "Copy the saved DeepSeek key to OpenCode Go and switch the provider? The key is not displayed or logged.",
                    "Switch and use key", "Cancel")) return;
            BridgeSettings.Secrets.OpenCodeApiKey = BridgeSettings.Secrets.DeepSeekApiKey;
            BridgeSettings.SaveSecrets();
            OpenCodeGoProvider.ResetResolvedBase();
            ChatService.SwitchProvider(BridgeSettings.ProviderOpenCode);
            ChatService.RefreshModels(true);
            ChatService.TestConnection();
            _tab = 0;
        }

        void DrawOpenAiAccount()
        {
            if (!string.IsNullOrEmpty(BridgeSettings.Secrets.OpenAiApiKey))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("OpenAI API key configured", EditorStyles.boldLabel);
                    GUILayout.Label("· " + ChatService.ConnectionState, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                    {
                        if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(112f)))
                            ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Settings", EditorStyles.miniButton, GUILayout.Width(68f))) _tab = 6;
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _apiKeyDraft = EditorGUILayout.PasswordField("OpenAI API key", _apiKeyDraft, GUILayout.Width(360f));
                    if (GUILayout.Button("Save & test", EditorStyles.miniButton, GUILayout.Width(90f)))
                    {
                        BridgeSettings.Secrets.OpenAiApiKey = _apiKeyDraft.Trim();
                        BridgeSettings.SaveSecrets();
                        _apiKeyDraft = string.Empty;
                        ChatService.RefreshModels(true);
                        ChatService.TestConnection();
                    }
                    if (GUILayout.Button("Use ChatGPT instead", EditorStyles.miniButton, GUILayout.Width(130f)))
                        ChatService.SwitchProvider(BridgeSettings.ProviderChatGpt);
                }
                EditorGUILayout.HelpBox("OpenAI API keys are billed per token by OpenAI. A ChatGPT Plus subscription does not include API usage.", MessageType.Warning);
            }
        }

        void DrawOllamaAccount()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Ollama: " + BridgeSettings.Current.OllamaUrl, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                {
                    if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(112f)))
                        ChatService.TestConnection();
                }
                if (GUILayout.Button("Settings", EditorStyles.miniButton, GUILayout.Width(68f))) _tab = 6;
            }
        }

        void SignOutChatGpt()
        {
            Task.Run(async () =>
            {
                await ChatGptAuth.SignOutAsync();
                MainThread.Run(() => { ChatService.RefreshModels(true); MarkDirty(); });
            });
        }

        void DrawChatMessage(ChatMessage message)
        {
            bool isError = message.IsError || message.Role == "error";
            var box = isError ? new GUIStyle(EditorStyles.helpBox) : EditorStyles.helpBox;
            using (new EditorGUILayout.VerticalScope(box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string role = message.Role == "user" ? "You" :
                                  message.Role == "assistant" ? "Assistant" :
                                  message.Role == "tool" ? "Tool" :
                                  message.Role == "error" ? "Error" : "Bridge";
                    GUILayout.Label(role, EditorStyles.boldLabel, GUILayout.Width(70f));
                    GUILayout.Label(message.TimeLocal + (string.IsNullOrEmpty(message.Model) ? string.Empty : " · " + message.Model), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (message.IsStreaming) GUILayout.Label("streaming...", EditorStyles.miniLabel);
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44f)))
                        EditorGUIUtility.systemCopyBuffer = message.Content ?? string.Empty;
                }

                if (message.Role == "tool")
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label((message.ToolName ?? "tool") + " · " + (message.ToolStatus ?? "done"), EditorStyles.miniBoldLabel);
                        if (!message.ToolOk) GUILayout.Label("failed", EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        if (message.ChangeIds.Count > 0 && GUILayout.Button("Review changes", EditorStyles.miniButton, GUILayout.Width(112f))) _tab = 4;
                    }
                    if (!string.IsNullOrEmpty(message.Content))
                        GUILayout.Label(message.Content, EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                if (!string.IsNullOrEmpty(message.Reasoning) && BridgeSettings.Current.ShowReasoning)
                {
                    bool expanded = _expandedReasoning.Contains(message.Id);
                    bool newExpanded = EditorGUILayout.Foldout(expanded, "Reasoning", true);
                    if (newExpanded != expanded)
                    {
                        if (newExpanded) _expandedReasoning.Add(message.Id);
                        else _expandedReasoning.Remove(message.Id);
                    }
                    if (newExpanded)
                    {
                        var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                        style.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.65f, 0.65f, 0.68f) : new Color(0.35f, 0.35f, 0.35f);
                        GUILayout.Label(message.Reasoning, style);
                    }
                }

                if (!string.IsNullOrEmpty(message.Content))
                {
                    if (isError) GUI.contentColor = new Color(1f, 0.65f, 0.65f);
                    MarkdownRenderer.Draw(message.Content, message.Id);
                    GUI.contentColor = Color.white;
                }

                if (!string.IsNullOrEmpty(message.Usage))
                    GUILayout.Label(message.Usage, EditorStyles.miniLabel);

                if (message.ChangeIds != null && message.ChangeIds.Count > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Open Pending Changes", EditorStyles.miniButton, GUILayout.Width(150f))) _tab = 4;
                    }
                }
            }
        }

        void AddQuickPrompt(GenericMenu menu, string prompt)
        {
            menu.AddItem(new GUIContent(prompt), false, () => { _input = prompt; GUI.FocusControl(null); });
        }

        // ---------------------------------------------------------------- explorer

        void DrawExplorer()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(64f)))
                {
                    AssetDatabase.Refresh();
                    Repaint();
                }
                if (GUILayout.Button("Attach selected file", EditorStyles.toolbarButton, GUILayout.Width(130f)))
                {
                    if (!string.IsNullOrEmpty(_previewPath)) ChatService.AttachFile(_previewPath);
                }
                if (GUILayout.Button(_showNewFile ? "Hide new file" : "New file...", EditorStyles.toolbarButton, GUILayout.Width(96f)))
                    _showNewFile = !_showNewFile;
                GUILayout.FlexibleSpace();
                GUILayout.Label("Read-only sandbox: project root · writes: Assets/", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(_treeScroll, GUILayout.Width(280f), GUILayout.ExpandHeight(true)))
                {
                    _treeScroll = scroll.scrollPosition;
                    DrawFolderNode("Assets", 0);
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.LabelField(_explorerDir, EditorStyles.boldLabel);
                    using (var scroll = new EditorGUILayout.ScrollViewScope(_explorerListScroll, GUILayout.Height(170f)))
                    {
                        _explorerListScroll = scroll.scrollPosition;
                        List<ProjectFileInfo> files;
                        try { files = FileService.ListFiles(_explorerDir, false, 400, null); }
                        catch (Exception e) { files = new List<ProjectFileInfo>(); EditorGUILayout.HelpBox(e.Message, MessageType.Error); }

                        if (files.Count == 0) EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel);
                        foreach (var file in files)
                        {
                            bool selected = file.RelativePath == _previewPath;
                            if (GUILayout.Toggle(selected, Path.GetFileName(file.RelativePath) + "   " + FileService.FormatSize(file.Size), EditorStyles.miniButton, GUILayout.ExpandWidth(true)))
                                _previewPath = file.RelativePath;
                        }
                    }

                    if (_showNewFile)
                    {
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            EditorGUILayout.LabelField("Create file (staged for approval)", EditorStyles.boldLabel);
                            _newFilePath = EditorGUILayout.TextField("Path", _newFilePath);
                            _newFileContent = EditorGUILayout.TextArea(_newFileContent, GUILayout.Height(110f));
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                if (GUILayout.Button("Stage create", GUILayout.Width(110f)))
                                {
                                    try
                                    {
                                        var change = ChangeService.StageFile(ChangeKinds.CreateFile, _newFilePath, _newFileContent, "ui");
                                        SetFeedback("Staged " + _newFilePath + " [" + change.Id + "]. Review the diff in Pending Changes.");
                                        _previewPath = _newFilePath;
                                        _showNewFile = false;
                                    }
                                    catch (Exception e) { SetFeedback(e.Message, true); }
                                }
                                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_previewPath)))
                                {
                                    if (GUILayout.Button("Use selected file content", EditorStyles.miniButton, GUILayout.Width(170f)))
                                    {
                                        string error;
                                        string content = FileService.ReadText(_previewPath, out error);
                                        if (content == null) SetFeedback(error, true);
                                        else { _newFilePath = _previewPath; _newFileContent = content; }
                                    }
                                }
                            }
                        }
                    }

                    GUILayout.Space(4f);
                    if (string.IsNullOrEmpty(_previewPath))
                    {
                        EditorGUILayout.HelpBox("Select a file to preview it. Attach it to the chat to give the assistant explicit context.", MessageType.None);
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(_previewPath, EditorStyles.boldLabel);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Copy path", EditorStyles.miniButton, GUILayout.Width(76f)))
                                EditorGUIUtility.systemCopyBuffer = _previewPath;
                            if (GUILayout.Button("Attach", EditorStyles.miniButton, GUILayout.Width(56f)))
                                ChatService.AttachFile(_previewPath);
                            if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(48f)))
                            {
                                var asset = AssetDatabase.LoadMainAssetAtPath(_previewPath);
                                if (asset != null) EditorGUIUtility.PingObject(asset);
                            }
                        }

                        using (var scroll = new EditorGUILayout.ScrollViewScope(_previewScroll, GUILayout.ExpandHeight(true)))
                        {
                            _previewScroll = scroll.scrollPosition;
                            string error;
                            string text = FileService.ReadText(_previewPath, out error, 512 * 1024);
                            if (text == null) EditorGUILayout.HelpBox(error, MessageType.Warning);
                            else
                            {
                                if (text.Length > 60000) text = text.Substring(0, 60000) + "\n... preview truncated ...";
                                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
                            }
                        }
                    }
                }
            }
        }

        void DrawFolderNode(string folder, int depth)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 12f);
            bool expanded = _expandedFolders.Contains(folder);
            Rect foldoutRect = GUILayoutUtility.GetRect(12f, EditorGUIUtility.singleLineHeight, GUILayout.Width(12f));
            bool newExpanded = EditorGUI.Foldout(foldoutRect, expanded, GUIContent.none, true);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expandedFolders.Add(folder);
                else _expandedFolders.Remove(folder);
            }

            var style = folder == _explorerDir ? EditorStyles.boldLabel : EditorStyles.label;
            if (GUILayout.Button(Path.GetFileName(folder), style))
                _explorerDir = folder;
            GUILayout.EndHorizontal();

            if (!newExpanded) return;
            List<string> children;
            try { children = FileService.ListDirectories(folder); }
            catch { return; }
            foreach (var child in children) DrawFolderNode(child, depth + 1);
        }

        // ---------------------------------------------------------------- scene

        void DrawScene()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(64f))) Repaint();
                if (GUILayout.Button("Save scene", EditorStyles.toolbarButton, GUILayout.Width(84f)))
                {
                    try
                    {
                        var change = ChangeService.Stage(ChangeKinds.SaveScene, "Save active scene", string.Empty, null, JsonValue.NewObject(), "ui", false);
                        SetFeedback("Staged save of the active scene [" + change.Id + "]. Approve it in Pending Changes.");
                    }
                    catch (BridgeException e) { SetFeedback(e.Message, true); }
                }
                if (GUILayout.Button("Select scene asset", EditorStyles.toolbarButton, GUILayout.Width(120f)))
                {
                    var scene = SceneService.ActiveScene;
                    if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
                        if (asset != null) EditorGUIUtility.PingObject(asset);
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(SceneService.ActiveScene.IsValid() ? SceneService.ActiveScene.name : "(no scene)", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(380f)))
                {
                    using (var scroll = new EditorGUILayout.ScrollViewScope(_sceneScroll, GUILayout.ExpandHeight(true)))
                    {
                        _sceneScroll = scroll.scrollPosition;
                        var scene = SceneService.ActiveScene;
                        if (!scene.IsValid()) EditorGUILayout.LabelField("(no active scene)");
                        else
                        {
                            foreach (var root in scene.GetRootGameObjects()) DrawSceneNode(root, 0);
                        }
                    }
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    var selected = Selection.activeGameObject;
                    if (selected == null)
                    {
                        EditorGUILayout.HelpBox("Select a GameObject in the hierarchy to inspect components and properties.", MessageType.None);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(SceneService.GetPath(selected), EditorStyles.boldLabel);
                        using (var scroll = new EditorGUILayout.ScrollViewScope(_inspectorScroll, GUILayout.Height(170f)))
                        {
                            _inspectorScroll = scroll.scrollPosition;
                            GUILayout.Label(SceneService.DescribeGameObject(selected, true).ToJson(true), EditorStyles.wordWrappedLabel);
                        }
                    }

                    GUILayout.Space(6f);
                    EditorGUILayout.LabelField("Create GameObject (staged)", EditorStyles.boldLabel);
                    _goName = EditorGUILayout.TextField("Name", _goName);
                    _goParent = EditorGUILayout.TextField("Parent path", _goParent);
                    _goPrimitive = EditorGUILayout.Popup("Primitive", _goPrimitive, PrimitiveLabels);
                    _goPosition = EditorGUILayout.Vector3Field("Position", _goPosition);
                    _goSprite = EditorGUILayout.TextField("Sprite (optional)", _goSprite);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Use selection as parent", EditorStyles.miniButton, GUILayout.Width(160f)) && selected != null)
                            _goParent = SceneService.GetPath(selected);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Stage create", GUILayout.Width(110f)))
                        {
                            try
                            {
                                var action = JsonValue.Obj(
                                    "type", "create_gameobject",
                                    "name", _goName,
                                    "parent", _goParent,
                                    "primitive", PrimitiveValues[_goPrimitive],
                                    "position", JsonValue.Arr(_goPosition.x, _goPosition.y, _goPosition.z));
                                if (!string.IsNullOrEmpty(_goSprite)) action["sprite"] = _goSprite;
                                var change = ActionRouter.StageAction(action, "ui");
                                SetFeedback("Staged creation of " + _goName + " [" + change.Id + "]. Approve it in Pending Changes.");
                            }
                            catch (Exception e) { SetFeedback(e.Message, true); }
                        }
                    }

                    GUILayout.Space(6f);
                    EditorGUILayout.LabelField("Create prefab from selection (staged)", EditorStyles.boldLabel);
                    _prefabPath = EditorGUILayout.TextField("Prefab path", _prefabPath);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Stage prefab", GUILayout.Width(110f)))
                        {
                            if (selected == null) SetFeedback("Select a GameObject in the scene first.", true);
                            else
                            {
                                try
                                {
                                    var action = JsonValue.Obj("type", "create_prefab", "path", _prefabPath, "target", SceneService.GetPath(selected), "keep_in_scene", true);
                                    var change = ActionRouter.StageAction(action, "ui");
                                    SetFeedback("Staged prefab " + _prefabPath + " [" + change.Id + "].");
                                }
                                catch (Exception e) { SetFeedback(e.Message, true); }
                            }
                        }
                    }
                }
            }
        }

        void DrawSceneNode(GameObject go, int depth)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 14f);
            string id = SceneService.GetPath(go);
            bool expanded = _expandedScene.Contains(id);
            if (go.transform.childCount > 0)
            {
                Rect foldoutRect = GUILayoutUtility.GetRect(12f, EditorGUIUtility.singleLineHeight, GUILayout.Width(12f));
            bool newExpanded = EditorGUI.Foldout(foldoutRect, expanded, GUIContent.none, true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _expandedScene.Add(id);
                    else _expandedScene.Remove(id);
                }
            }
            else GUILayout.Space(14f);

            bool isSelected = Selection.activeGameObject == go;
            var style = isSelected ? EditorStyles.boldLabel : EditorStyles.label;
            if (GUILayout.Button(go.name + (go.activeSelf ? string.Empty : " (inactive)"), style))
            {
                Selection.activeGameObject = go;
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();

            if (!expanded) return;
            for (int i = 0; i < go.transform.childCount; i++)
                DrawSceneNode(go.transform.GetChild(i).gameObject, depth + 1);
        }

        // ---------------------------------------------------------------- assets

        void DrawAssets()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Folder:", EditorStyles.miniLabel, GUILayout.Width(44f));
                _assetFolder = EditorGUILayout.TextField(_assetFolder, EditorStyles.toolbarTextField, GUILayout.Width(260f));
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(64f))) RefreshSpriteList();
                if (GUILayout.Button("Import PNG...", EditorStyles.toolbarButton, GUILayout.Width(96f)))
                {
                    string path = EditorUtility.OpenFilePanel("Import image as sprite", string.Empty, "png,jpg,jpeg,tga,bmp");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _importSource = path;
                        string folder = string.IsNullOrEmpty(_assetFolder) ? "Assets/Art" : _assetFolder;
                        _importDest = folder.TrimEnd('/') + "/" + Path.GetFileName(path);
                        _tab = 3;
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(_sprites.Count + " sprites", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(_assetListScroll, GUILayout.Width(300f), GUILayout.ExpandHeight(true)))
                {
                    _assetListScroll = scroll.scrollPosition;
                    for (int i = 0; i < _sprites.Count; i++)
                    {
                        if (GUILayout.Toggle(i == _spriteIndex, Path.GetFileName(_sprites[i]), EditorStyles.miniButton, GUILayout.ExpandWidth(true)))
                            _spriteIndex = i;
                    }
                    if (_sprites.Count == 0) EditorGUILayout.LabelField("No sprites found. Import a PNG to get started.", EditorStyles.wordWrappedMiniLabel);
                }

                using (var scroll = new EditorGUILayout.ScrollViewScope(_assetDetailScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
                {
                    _assetDetailScroll = scroll.scrollPosition;
                    string selectedPath = _spriteIndex >= 0 && _spriteIndex < _sprites.Count ? _sprites[_spriteIndex] : null;
                    DrawAssetDetail(selectedPath);
                }
            }
        }

        void DrawAssetDetail(string selectedPath)
        {
            EditorGUILayout.LabelField("Import settings", EditorStyles.boldLabel);
            _importSource = EditorGUILayout.TextField("Source file", _importSource);
            _importDest = EditorGUILayout.TextField("Destination", _importDest);
            _spriteMode = EditorGUILayout.Popup("Sprite mode", _spriteMode, SpriteModeLabels);
            _ppu = EditorGUILayout.FloatField("Pixels per unit", _ppu);
            _filterIndex = EditorGUILayout.Popup("Filter", _filterIndex, FilterLabels);
            _compressionIndex = EditorGUILayout.Popup("Compression", _compressionIndex, CompressionLabels);
            _maxSize = EditorGUILayout.IntPopup("Max size", _maxSize, new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
            _alphaTransparency = EditorGUILayout.Toggle("Alpha is transparency", _alphaTransparency);
            _mipmaps = EditorGUILayout.Toggle("Generate mipmaps", _mipmaps);
            _readable = EditorGUILayout.Toggle("Read/Write enabled", _readable);
            EditorGUILayout.HelpBox("Platform texture overrides: ETC2 (Android) / ASTC (iOS) when applicable, clamped max size, mipmaps off by default.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_importSource)))
                {
                    if (GUILayout.Button("Stage import", GUILayout.Width(120f)))
                    {
                        try
                        {
                            var change = ActionRouter.StageSpriteImportFromFile(_importSource, _importDest, BuildSpriteParameters(), "ui");
                            SetFeedback("Staged sprite import [" + change.Id + "]. Approve it in Pending Changes.");
                        }
                        catch (Exception e) { SetFeedback(e.Message, true); }
                    }
                }
                if (GUILayout.Button("Use selected sprite path", EditorStyles.miniButton, GUILayout.Width(160f)) && !string.IsNullOrEmpty(selectedPath))
                {
                    _importDest = selectedPath;
                }
            }

            EditorGUILayout.Space(8f);
            if (string.IsNullOrEmpty(selectedPath))
            {
                EditorGUILayout.HelpBox("Select a sprite on the left to inspect and edit its import settings.", MessageType.None);
                DrawMaterialForm();
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(selectedPath);
            var preview = asset != null ? AssetPreview.GetAssetPreview(asset) : null;
            if (asset != null && preview == null) Repaint();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Box(preview != null ? preview : Texture2D.whiteTexture, GUILayout.Width(128f), GUILayout.Height(128f));
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(selectedPath, EditorStyles.boldLabel);
                    var info = AssetService.InspectAsset(selectedPath);
                    GUILayout.Label("Size: " + info["width"].AsInt() + " x " + info["height"].AsInt() + "   Mode: " + info["sprite_mode"].AsString() +
                                    "   PPU: " + info["pixels_per_unit"].AsNumber(), EditorStyles.miniLabel);
                    GUILayout.Label("Sprites: " + info["sprites"].Count, EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Stage settings", EditorStyles.miniButton, GUILayout.Width(110f)))
                        {
                            try
                            {
                                var parameters = BuildSpriteParameters();
                                var change = ChangeService.Stage(ChangeKinds.ConfigureSprite, "Update " + Path.GetFileName(selectedPath), selectedPath, null, parameters, "ui", false);
                                SetFeedback("Staged import settings [" + change.Id + "].");
                            }
                            catch (Exception e) { SetFeedback(e.Message, true); }
                        }
                        if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(52f))) EditorGUIUtility.PingObject(asset);
                    }
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Sprite sheet slicing", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _sliceRows = EditorGUILayout.IntField("Rows", _sliceRows, GUILayout.Width(140f));
                _sliceColumns = EditorGUILayout.IntField("Columns", _sliceColumns, GUILayout.Width(150f));
                if (GUILayout.Button("Stage slice", EditorStyles.miniButton, GUILayout.Width(100f)))
                {
                    try
                    {
                        var parameters = JsonValue.Obj("rows", _sliceRows, "columns", _sliceColumns);
                        var change = ChangeService.Stage(ChangeKinds.SliceSprite, "Slice " + Path.GetFileName(selectedPath), selectedPath, null, parameters, "ui", false);
                        SetFeedback("Staged slicing [" + change.Id + "].");
                    }
                    catch (Exception e) { SetFeedback(e.Message, true); }
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Organise artwork", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string[] categories = { "Characters", "Environments", "UI", "Enemies", "Buildings", "Effects" };
                foreach (var category in categories)
                {
                    if (GUILayout.Button(category, EditorStyles.miniButton, GUILayout.Width(82f)))
                    {
                        string folder = "Assets/Art/" + category;
                        try
                        {
                            AssetService.EnsureAssetFolder(folder);
                            SetFeedback("Folder ready: " + folder);
                            RefreshSpriteList();
                        }
                        catch (Exception e) { SetFeedback(e.Message, true); }
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Move selected sprite into folder above", EditorStyles.miniButton))
                {
                    string folder = "Assets/Art/Characters";
                    string destination = folder + "/" + Path.GetFileName(selectedPath);
                    try
                    {
                        var parameters = JsonValue.Obj("from", selectedPath);
                        var change = ChangeService.Stage(ChangeKinds.MoveFile, "Move " + Path.GetFileName(selectedPath), destination, null, parameters, "ui", true);
                        SetFeedback("Staged move [" + change.Id + "]. AssetDatabase.MoveAsset keeps the .meta GUIDs.");
                    }
                    catch (Exception e) { SetFeedback(e.Message, true); }
                }
                GUILayout.Label("Moves use AssetDatabase.MoveAsset, preserving GUID references.", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Assign to selection", EditorStyles.boldLabel);
            var selectedGo = Selection.activeGameObject;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(selectedGo != null ? SceneService.GetPath(selectedGo) : "(select a GameObject in the scene)", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                _assignComponent = EditorGUILayout.Popup(_assignComponent, ComponentLabels, GUILayout.Width(130f));
                if (GUILayout.Button("Stage assign", EditorStyles.miniButton, GUILayout.Width(100f)))
                {
                    if (selectedGo == null) SetFeedback("Select a GameObject in the scene first.", true);
                    else
                    {
                        try
                        {
                            var action = JsonValue.Obj(
                                "type", "assign_sprite",
                                "target", SceneService.GetPath(selectedGo),
                                "component", ComponentLabels[_assignComponent],
                                "sprite", selectedPath);
                            var change = ActionRouter.StageAction(action, "ui");
                            SetFeedback("Staged sprite assignment [" + change.Id + "].");
                        }
                        catch (Exception e) { SetFeedback(e.Message, true); }
                    }
                }
            }

            EditorGUILayout.HelpBox("Image generation is not configured. This panel imports and configures existing artwork only; it never claims to create images.", MessageType.Info);
            DrawMaterialForm();
        }

        void DrawMaterialForm()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Create material (staged)", EditorStyles.boldLabel);
            _materialPath = EditorGUILayout.TextField("Path", _materialPath);
            _materialColor = EditorGUILayout.ColorField("Base colour", _materialColor);
            _materialTexture = EditorGUILayout.TextField("Texture (optional)", _materialTexture);
            if (GUILayout.Button("Stage material", GUILayout.Width(130f)))
            {
                try
                {
                    var parameters = JsonValue.Obj(
                        "color", JsonValue.Arr(_materialColor.r, _materialColor.g, _materialColor.b, _materialColor.a));
                    if (!string.IsNullOrEmpty(_materialTexture)) parameters["texture"] = _materialTexture;
                    var change = ChangeService.Stage(ChangeKinds.CreateMaterial, "Create material " + Path.GetFileName(_materialPath), _materialPath, null, parameters, "ui", false);
                    SetFeedback("Staged material [" + change.Id + "].");
                }
                catch (Exception e) { SetFeedback(e.Message, true); }
            }
        }

        JsonValue BuildSpriteParameters()
        {
            return JsonValue.Obj(
                "sprite_mode", SpriteModeLabels[_spriteMode],
                "pixels_per_unit", _ppu,
                "filter", FilterLabels[_filterIndex],
                "compression", CompressionValues[_compressionIndex],
                "max_size", _maxSize,
                "alpha_is_transparency", _alphaTransparency,
                "mipmaps", _mipmaps,
                "readable", _readable);
        }

        void RefreshSpriteList()
        {
            try { _sprites = AssetService.FindSprites(string.IsNullOrEmpty(_assetFolder) ? "Assets" : _assetFolder, 300); }
            catch (Exception e) { SetFeedback(e.Message, true); _sprites = new List<string>(); }
            if (_spriteIndex >= _sprites.Count) _spriteIndex = _sprites.Count - 1;
            Repaint();
        }

        // ---------------------------------------------------------------- changes

        void DrawChanges()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(ChangeService.PendingCount + " pending / " + ChangeService.ApprovedCount + " approved", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Approve all (safe)", EditorStyles.toolbarButton, GUILayout.Width(130f)))
                {
                    ChangeService.ApproveAllPending(false);
                    SetFeedback("Approved all non-destructive pending changes.");
                }
                if (GUILayout.Button("Apply approved", EditorStyles.toolbarButton, GUILayout.Width(110f)))
                {
                    var results = ChangeService.ApplyAllApproved();
                    SetFeedback(results.Count == 0 ? "No approved changes to apply." : string.Join("\n", results.ToArray()));
                    _tab = 4;
                }
                if (GUILayout.Button("Clear applied", EditorStyles.toolbarButton, GUILayout.Width(90f))) ChangeService.ClearApplied();
                if (GUILayout.Button("Open backups", EditorStyles.toolbarButton, GUILayout.Width(96f))) UnityAIBridgeBootstrap.OpenBackupsFolder();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(_changesListScroll, GUILayout.Width(300f), GUILayout.ExpandHeight(true)))
                {
                    _changesListScroll = scroll.scrollPosition;
                    if (ChangeService.All.Count == 0) EditorGUILayout.LabelField("No changes yet.", EditorStyles.wordWrappedMiniLabel);
                    foreach (var change in ChangeService.All)
                    {
                        if (!_showRejected && (change.Status == ChangeStatus.Rejected || change.Status == ChangeStatus.Failed)) continue;
                        bool selected = change.Id == _selectedChangeId;
                        string label = "[" + change.Status + "] " + change.Title;
                        if (GUILayout.Toggle(selected, label, EditorStyles.miniButton, GUILayout.ExpandWidth(true)))
                        {
                            _selectedChangeId = change.Id;
                            _diffCacheId = null;
                        }
                    }
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    var change = ChangeService.Find(_selectedChangeId);
                    if (change == null)
                    {
                        EditorGUILayout.HelpBox("Select a staged change to review its diff. Nothing is written until you approve and apply it.", MessageType.None);
                        return;
                    }

                    EditorGUILayout.LabelField(change.Title, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Kind: " + change.Kind + "   Source: " + change.Source + "   Status: " + change.Status);
                    if (!string.IsNullOrEmpty(change.TargetPath)) EditorGUILayout.LabelField("Target: " + change.TargetPath);
                    if (change.Destructive) EditorGUILayout.HelpBox("Destructive operation: explicit confirmation is required.", MessageType.Warning);
                    if (!string.IsNullOrEmpty(change.Error)) EditorGUILayout.HelpBox(change.Error, MessageType.Error);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(change.Status == ChangeStatus.Applied))
                        {
                            if (GUILayout.Button("Approve", GUILayout.Width(90f))) ApproveChange(change);
                            if (GUILayout.Button(change.Status == ChangeStatus.Approved ? "Apply now" : "Apply (needs approval)", GUILayout.Width(180f)))
                            {
                                string error = ChangeService.Apply(change.Id);
                                if (error != null) SetFeedback(error, true);
                                else SetFeedback("Applied " + change.Title + ".");
                            }
                        }
                        if (GUILayout.Button("Reject", EditorStyles.miniButton, GUILayout.Width(70f))) ChangeService.Reject(change.Id);
                        if (!string.IsNullOrEmpty(change.BackupPath) && File.Exists(change.BackupPath))
                        {
                            if (GUILayout.Button("Restore backup", EditorStyles.miniButton, GUILayout.Width(110f)))
                            {
                                try
                                {
                                    FileService.RestoreBackup(change.BackupPath, change.TargetPath);
                                    SetFeedback("Restored " + change.TargetPath + " from backup.", false);
                                }
                                catch (Exception e) { SetFeedback(e.Message, true); }
                            }
                        }
                        if (GUILayout.Button("Copy diff", EditorStyles.miniButton, GUILayout.Width(76f)))
                            EditorGUIUtility.systemCopyBuffer = change.Diff ?? string.Empty;
                    }

                    string diff = change.Diff;
                    if (string.IsNullOrEmpty(diff)) diff = "(no textual diff for this operation)";
                    if (_diffCacheId != change.Id || _diffCacheText == null)
                    {
                        _diffCacheId = change.Id;
                        _diffCacheText = BuildColoredDiff(diff);
                    }

                    using (var scroll = new EditorGUILayout.ScrollViewScope(_diffScroll, GUILayout.ExpandHeight(true)))
                    {
                        _diffScroll = scroll.scrollPosition;
                        var style = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = false };
                        GUILayout.Label(_diffCacheText, style);
                    }
                }
            }
        }

        void ApproveChange(BridgeChange change)
        {
            bool acknowledge = false;
            if (change.Destructive)
            {
                if (!EditorUtility.DisplayDialog("Confirm destructive change",
                        "This will " + change.Kind.Replace('_', ' ') + ":\n\n" + change.TargetPath +
                        "\n\nA backup will be made when possible. Continue?", "Approve", "Cancel"))
                    return;
                acknowledge = true;
            }

            if (Permissions.RequiresScriptWarning(change.TargetPath))
            {
                if (!EditorUtility.DisplayDialog("Script will compile",
                        "Approving this change will make Unity compile the script. Scripts can run editor code (for example [InitializeOnLoad]) without further prompts.\n\nOnly approve code you have reviewed.",
                        "I reviewed it", "Cancel"))
                    return;
            }

            string error = ChangeService.Approve(change.Id, acknowledge);
            if (error != null) SetFeedback(error, true);
            else SetFeedback("Approved " + change.Title + ". Press Apply to write it.");
        }

        string BuildColoredDiff(string diff)
        {
            var sb = new StringBuilder();
            string[] lines = (diff ?? string.Empty).Split('\n');
            int max = Math.Min(lines.Length, 900);
            for (int i = 0; i < max; i++)
            {
                string line = lines[i].Replace("<", "&lt;").Replace(">", "&gt;");
                string color = null;
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) color = "#9CDCFE";
                else if (line.StartsWith("+", StringComparison.Ordinal)) color = "#7BD88F";
                else if (line.StartsWith("-", StringComparison.Ordinal)) color = "#FF7B7B";
                else if (line.StartsWith("@@", StringComparison.Ordinal)) color = "#C792EA";
                if (color != null) sb.Append("<color=").Append(color).Append('>').Append(line).Append("</color>");
                else sb.Append(line);
                sb.Append('\n');
            }
            if (lines.Length > max) sb.Append("\n... diff display truncated at ").Append(max).Append(" lines ...");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- MCP

        void DrawMcp()
        {
            var settings = BridgeSettings.Current;
            bool running = McpServer.Current != null && McpServer.Current.IsRunning;

            using (var scroll = new EditorGUILayout.ScrollViewScope(_mcpScroll, GUILayout.ExpandHeight(true)))
            {
                _mcpScroll = scroll.scrollPosition;
                EditorGUILayout.LabelField("Local MCP server", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "The bridge runs an MCP (JSON-RPC 2.0) server on 127.0.0.1 only. It is used by MCP clients such as Codex CLI, Claude Desktop, Cursor or MCP Inspector. " +
                    "ChatGPT on the web cannot reach localhost directly; use Codex with this MCP server, or an officially supported tunnel (see below).",
                    MessageType.Info);

                EditorGUILayout.LabelField("Status", running ? "Running on " + McpServer.Current.Url : "Stopped", EditorStyles.boldLabel);
                if (McpServer.Current != null && !string.IsNullOrEmpty(McpServer.Current.LastError))
                    EditorGUILayout.HelpBox(McpServer.Current.LastError, MessageType.Error);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _mcpPortText = EditorGUILayout.TextField("Port", _mcpPortText, GUILayout.Width(140f));
                    if (GUILayout.Button("Apply port", EditorStyles.miniButton, GUILayout.Width(90f)))
                    {
                        int port;
                        if (!int.TryParse(_mcpPortText, out port) || port < 1024 || port > 65535)
                            SetFeedback("Port must be between 1024 and 65535.", true);
                        else
                        {
                            settings.McpPort = port;
                            BridgeSettings.Save();
                            McpServer.RestartFromSettings();
                            SetFeedback(running ? "MCP restarted on port " + port + "." : "MCP port saved.");
                        }
                    }
                    if (running)
                    {
                        if (GUILayout.Button("Stop", GUILayout.Width(70f)))
                        {
                            McpServer.StopCurrent();
                            settings.McpEnabled = false;
                            BridgeSettings.Save();
                            SetFeedback("MCP server stopped.");
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("Start", GUILayout.Width(70f)))
                        {
                            McpServer.StartFromSettings();
                            SetFeedback(McpServer.Current != null && McpServer.Current.IsRunning ? "MCP server started." : "MCP server failed to start (see log).", McpServer.Current == null);
                        }
                    }
                    if (GUILayout.Button("Health check", EditorStyles.miniButton, GUILayout.Width(100f))) RunHealthCheck();
                }
                if (!string.IsNullOrEmpty(_mcpTestResult)) EditorGUILayout.HelpBox(_mcpTestResult, _mcpTestResult.StartsWith("OK", StringComparison.Ordinal) ? MessageType.Info : MessageType.Warning);

                settings.McpAutoStart = EditorGUILayout.Toggle("Start automatically with Unity", settings.McpAutoStart);
                settings.McpEnabled = EditorGUILayout.Toggle("Enabled", settings.McpEnabled);
                if (GUILayout.Button("Save", GUILayout.Width(80f))) BridgeSettings.Save();

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Bearer token", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    string token = BridgeSettings.EnsureMcpToken();
                    _revealToken = EditorGUILayout.ToggleLeft("Show", _revealToken, GUILayout.Width(56f));
                    EditorGUILayout.SelectableLabel(_revealToken ? token : BridgeSettings.Mask(token), EditorStyles.textField, GUILayout.Height(18f));
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(56f))) EditorGUIUtility.systemCopyBuffer = token;
                    if (GUILayout.Button("Regenerate", EditorStyles.miniButton, GUILayout.Width(86f)))
                    {
                        if (EditorUtility.DisplayDialog("Regenerate token", "Existing MCP clients will stop working until you update them. Continue?", "Regenerate", "Cancel"))
                        {
                            BridgeSettings.RegenerateMcpToken();
                            McpServer.RestartFromSettings();
                            SetFeedback("New MCP token generated.");
                        }
                    }
                }
                EditorGUILayout.HelpBox("The token is stored outside the Unity project (see Settings for the path) with 0600 permissions. It is never sent to an AI provider.", MessageType.None);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Connect a client", EditorStyles.boldLabel);
                var configStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = false };
                EditorGUILayout.TextArea(McpServer.ClientConfigJson(), configStyle, GUILayout.Height(120f));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy JSON config", GUILayout.Width(130f))) EditorGUIUtility.systemCopyBuffer = McpServer.ClientConfigJson();
                    if (GUILayout.Button("Copy Codex command", GUILayout.Width(140f))) EditorGUIUtility.systemCopyBuffer = McpServer.CodexCommand();
                }
                EditorGUILayout.HelpBox("Codex CLI (included with ChatGPT Plus plans, subject to Codex usage limits) can connect to this local server directly. It runs on your machine and uses your existing Codex/ChatGPT sign-in — no API key needed.", MessageType.None);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("ChatGPT on the web", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "ChatGPT web can only connect to remote MCP servers. There is no 'Sign in with ChatGPT' developer app for an independent Unity plugin (that programme is limited to selected partners). " +
                    "Officially supported option: OpenAI Secure MCP Tunnel — create a tunnel in Platform settings, run tunnel-client on this machine pointing at " + McpServer.Current?.Url + ", then add the tunnel as a custom MCP server in ChatGPT. " +
                    "Availability of custom MCP servers depends on your ChatGPT plan/workspace. This panel never fakes a ChatGPT login.",
                    MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open Secure MCP Tunnel docs", EditorStyles.miniButton, GUILayout.Width(190f)))
                        Application.OpenURL("https://developers.openai.com/api/docs/guides/secure-mcp-tunnels");
                    if (GUILayout.Button("Open custom MCP server docs", EditorStyles.miniButton, GUILayout.Width(180f)))
                        Application.OpenURL("https://help.openai.com/en/articles/12584461");
                }

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Recent calls", EditorStyles.boldLabel);
                if (McpServer.Current == null || McpServer.Current.RecentCalls.Count == 0)
                    EditorGUILayout.LabelField("(none yet)", EditorStyles.miniLabel);
                else
                {
                    var calls = McpServer.Current.RecentCalls;
                    int start = Math.Max(0, calls.Count - 15);
                    for (int i = calls.Count - 1; i >= start; i--)
                    {
                        var call = calls[i];
                        GUILayout.Label(call.TimeUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + (call.Ok ? "OK " : "ERR ") + call.Summary + "  " + call.DurationMs.ToString("0") + " ms", EditorStyles.miniLabel);
                    }
                }
            }
        }

        void RunHealthCheck()
        {
            if (McpServer.Current == null || !McpServer.Current.IsRunning)
            {
                _mcpTestResult = "MCP server is not running.";
                return;
            }

            if (_mcpTesting) return;
            _mcpTesting = true;
            _mcpTestResult = "Checking...";
            string url = McpServer.Current.Url.Replace("/mcp", "/health");
            Task.Run(async () =>
            {
                string result;
                try
                {
                    result = await StreamingHttp.GetTextAsync(url, null, 10, CancellationToken.None);
                    result = "OK: " + result;
                }
                catch (Exception e) { result = "Health check failed: " + e.Message; }
                MainThread.Run(() => { _mcpTestResult = result; _mcpTesting = false; MarkDirty(); });
            });
        }

        // ---------------------------------------------------------------- settings

        void DrawSettings()
        {
            var settings = BridgeSettings.Current;
            using (var scroll = new EditorGUILayout.ScrollViewScope(_settingsScroll, GUILayout.ExpandHeight(true)))
            {
                _settingsScroll = scroll.scrollPosition;

                EditorGUILayout.LabelField("Permissions", EditorStyles.boldLabel);
                int level = (int)settings.Level;
                int newLevel = EditorGUILayout.Popup("Access level", level, LevelLabels);
                if (newLevel != level) Permissions.SetLevel((BridgeSettings.AccessMode)newLevel);
                EditorGUILayout.HelpBox(Permissions.DescribeLevel, MessageType.Info);
                settings.AutoApplyWithWriteAccess = EditorGUILayout.Toggle("Auto-apply AI changes (Project Write only)", settings.AutoApplyWithWriteAccess);
                if (settings.AutoApplyWithWriteAccess)
                    EditorGUILayout.HelpBox("Auto-apply is strong. Destructive operations still require approval. Leave this off unless you understand the risk.", MessageType.Warning);
                settings.ConfirmScriptCompilation = EditorGUILayout.Toggle("Warn before compiling AI-written scripts", settings.ConfirmScriptCompilation);

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Provider and model", EditorStyles.boldLabel);
                int providerIndex = Math.Max(0, Array.IndexOf(ProviderIds, ChatService.CurrentProviderId));
                int newProvider = EditorGUILayout.Popup("Provider", providerIndex, ProviderLabels);
                if (newProvider != providerIndex) ChatService.SwitchProvider(ProviderIds[newProvider]);
                DrawSettingsModelRow();
                EditorGUILayout.HelpBox(ProviderFactory.BillingNote(ChatService.CurrentProviderId), MessageType.None);

                if (ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt) DrawChatGptSettings();
                else if (ChatService.CurrentProviderId == BridgeSettings.ProviderDeepSeek) DrawDeepSeekSettings();
                else if (ChatService.CurrentProviderId == BridgeSettings.ProviderOpenCode) DrawOpenCodeSettings();
                else if (ChatService.CurrentProviderId == BridgeSettings.ProviderOpenAi) DrawOpenAiSettings();
                else DrawOllamaSettings();

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Context sent to the assistant", EditorStyles.boldLabel);
                settings.IncludeSceneContext = EditorGUILayout.Toggle("Scene hierarchy", settings.IncludeSceneContext);
                settings.IncludeConsoleContext = EditorGUILayout.Toggle("Unity console / compiler errors", settings.IncludeConsoleContext);
                settings.IncludeFileTree = EditorGUILayout.Toggle("Asset tree", settings.IncludeFileTree);
                settings.IncludeExistingScripts = EditorGUILayout.Toggle("Existing script source", settings.IncludeExistingScripts);
                settings.MaxContextCharacters = EditorGUILayout.IntField("Max context characters", settings.MaxContextCharacters);
                settings.MaxAgentSteps = EditorGUILayout.IntSlider("Max tool steps per request", settings.MaxAgentSteps, 1, 24);
                settings.ShowReasoning = EditorGUILayout.Toggle("Show reasoning / thinking", settings.ShowReasoning);
                EditorGUILayout.LabelField("Project notes (sent with every request)", EditorStyles.miniLabel);
                settings.ProjectNotes = EditorGUILayout.TextArea(settings.ProjectNotes, GUILayout.Height(56f));

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Security", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Config folder", BridgePaths.ProjectConfigDir, EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open config", EditorStyles.miniButton, GUILayout.Width(90f))) UnityAIBridgeBootstrap.OpenConfigFolder();
                    if (GUILayout.Button("Copy config path", EditorStyles.miniButton, GUILayout.Width(110f))) EditorGUIUtility.systemCopyBuffer = BridgePaths.ProjectConfigDir;
                    if (GUILayout.Button("Open backups", EditorStyles.miniButton, GUILayout.Width(96f))) UnityAIBridgeBootstrap.OpenBackupsFolder();
                }
                EditorGUILayout.HelpBox("Writes are restricted to Assets/ and the bridge's own source is protected. Paths are canonicalised and symlink escapes are rejected. Reads outside the project, .git, Library, browser data and sensitive files are blocked. API keys and OAuth tokens are stored in secrets.json (0600) outside the project and are never put in chat logs.", MessageType.None);
                settings.AllowSelfModification = EditorGUILayout.Toggle("Allow assistant to modify the bridge's own source", settings.AllowSelfModification);
                settings.LogToFile = EditorGUILayout.Toggle("Write bridge log to file", settings.LogToFile);

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("ChatGPT subscription and API reality", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "ChatGPT (this provider) uses OpenAI's Sign in with ChatGPT flow for open-source clients. It is real OAuth (PKCE + ID-token validation) and uses ChatGPT plan usage, not an API key.\n\n" +
                    "• Plus: plan usage shares a 5-hour limit across apps; Pro has no 5-hour limit. Limits are controlled by OpenAI/ChatGPT settings.\n" +
                    "• It does not provide unlimited API access, and it is not a Codex Work quota.\n" +
                    "• Eligibility and availability are controlled by OpenAI; if the account/workspace is not eligible, use the OpenAI API provider or another provider.\n" +
                    "• No browser cookies, scraping or unofficial endpoints are used. The bridge never marks a provider connected unless OAuth and the plan scope actually succeed.\n" +
                    "• If plan usage is not granted you can still sign in, and the app will say so instead of pretending to be connected.",
                    MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Sign in with ChatGPT docs", EditorStyles.miniButton, GUILayout.Width(170f)))
                        Application.OpenURL("https://developers.openai.com/siwc/token-sharing-open-source");
                    if (GUILayout.Button("Sign in with ChatGPT quickstart", EditorStyles.miniButton, GUILayout.Width(190f)))
                        Application.OpenURL("https://developers.openai.com/siwc/quickstart");
                    if (GUILayout.Button("ChatGPT usage settings", EditorStyles.miniButton, GUILayout.Width(160f)))
                        Application.OpenURL("https://chatgpt.com/settings/usage");
                }

                EditorGUILayout.Space(12f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save settings", GUILayout.Width(120f))) { BridgeSettings.Save(); SetFeedback("Settings saved."); }
                    if (GUILayout.Button("Open documentation", EditorStyles.miniButton, GUILayout.Width(130f))) UnityAIBridgeBootstrap.OpenDocumentation();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Reset settings and secrets", EditorStyles.miniButton, GUILayout.Width(180f))) UnityAIBridgeBootstrap.ResetSettings();
                }
            }
        }

        void DrawSettingsModelRow()
        {
            var models = ChatService.Models;
            string current = ChatService.CurrentModel;
            int index = models.FindIndex(m => m.Id == current);
            if (index < 0) index = 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (models.Count == 0)
                {
                    GUILayout.Label("Model: none available", EditorStyles.miniLabel);
                }
                else
                {
                    var names = new string[models.Count];
                    for (int i = 0; i < models.Count; i++) names[i] = models[i].ToString();
                    int selected = EditorGUILayout.Popup("Model", index, names);
                    if (selected != index) ChatService.SelectModel(models[selected].Id);
                }
                using (new EditorGUI.DisabledScope(ChatService.IsLoadingModels))
                {
                    if (GUILayout.Button(ChatService.IsLoadingModels ? "Loading..." : "Refresh models", EditorStyles.miniButton, GUILayout.Width(110f)))
                        ChatService.RefreshModels(true);
                }
            }
        }

        void DrawChatGptSettings()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("ChatGPT account", EditorStyles.boldLabel);
            DrawChatGptAccount();
            EditorGUILayout.LabelField("Account", string.IsNullOrEmpty(ChatGptAuth.AccountLabel) ? "(not signed in)" : ChatGptAuth.AccountLabel);
            EditorGUILayout.LabelField("Plan usage", ChatGptAuth.PlanUsageEnabled ? "granted" : "not granted");
            long expiry = BridgeSettings.Secrets.ChatGptExpiresAtUnix;
            if (expiry > 0)
                EditorGUILayout.LabelField("Access token expiry", DateTimeOffset.FromUnixTimeSeconds(expiry).ToLocalTime().ToString("g"));
            EditorGUILayout.LabelField("Issued client", string.IsNullOrEmpty(BridgeSettings.Secrets.ChatGptClientId) ? "(none)" : BridgeSettings.Secrets.ChatGptClientId);
            EditorGUILayout.LabelField("Host ID", string.IsNullOrEmpty(BridgeSettings.Secrets.ChatGptHostId) ? "(generated on first sign-in)" : BridgeSettings.Secrets.ChatGptHostId);
            EditorGUILayout.HelpBox("Tokens are stored in " + BridgePaths.SecretsFile + " (0600). Sign out revokes the renewable session when possible and clears local tokens; the issued client ID is retained for a later sign-in.", MessageType.None);
        }

        void DrawDeepSeekSettings()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("DeepSeek API", EditorStyles.boldLabel);
            BridgeSettings.Current.DeepSeekBaseUrl = EditorGUILayout.TextField("Base URL", BridgeSettings.Current.DeepSeekBaseUrl);
            using (new EditorGUILayout.HorizontalScope())
            {
                string key = BridgeSettings.Secrets.DeepSeekApiKey;
                _deepSeekKeyDraft = EditorGUILayout.PasswordField(key.Length == 0 ? "API key" : "API key (" + BridgeSettings.Mask(key) + ")", _deepSeekKeyDraft);
                if (GUILayout.Button("Save key", EditorStyles.miniButton, GUILayout.Width(76f)))
                {
                    BridgeSettings.Secrets.DeepSeekApiKey = _deepSeekKeyDraft.Trim();
                    BridgeSettings.SaveSecrets();
                    _deepSeekKeyDraft = string.Empty;
                    ChatService.RefreshModels(true);
                    SetFeedback("DeepSeek API key saved.");
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(54f)))
                {
                    BridgeSettings.Secrets.DeepSeekApiKey = string.Empty;
                    BridgeSettings.SaveSecrets();
                    ChatService.RefreshModels(true);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                {
                    if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(110f)))
                        ChatService.TestConnection();
                }
                if (GUILayout.Button("Get an API key", EditorStyles.miniButton, GUILayout.Width(110f)))
                    Application.OpenURL("https://platform.deepseek.com/api_keys");
                if (GUILayout.Button("DeepSeek pricing", EditorStyles.miniButton, GUILayout.Width(110f)))
                    Application.OpenURL("https://api-docs.deepseek.com/quick_start/pricing");
            }
            if (!string.IsNullOrEmpty(ChatService.LastTestResult)) EditorGUILayout.HelpBox(ChatService.LastTestResult, ChatService.ConnectionState == ProviderConnectionState.Error ? MessageType.Error : MessageType.Info);
            if (ChatService.ConnectionState == ProviderConnectionState.Error && !string.IsNullOrEmpty(ChatService.ConnectionMessage) &&
                ChatService.ConnectionMessage.IndexOf("401", StringComparison.Ordinal) >= 0)
            {
                EditorGUILayout.HelpBox("If this is an OpenCode Go key, it is not valid at api.deepseek.com. Use it with the OpenCode Go provider instead.", MessageType.Warning);
                if (!string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey) && GUILayout.Button("Switch to OpenCode Go and reuse this key", GUILayout.Width(260f)))
                    ReuseDeepSeekKeyForOpenCodeAndSwitch();
            }
            string deepSeekModel = EditorGUILayout.TextField("Selected model", ChatService.CurrentModel);
            if (deepSeekModel != ChatService.CurrentModel) ChatService.SelectModel(deepSeekModel);
            BridgeSettings.Current.DeepSeekThinking = EditorGUILayout.Toggle("Thinking / reasoning mode", BridgeSettings.Current.DeepSeekThinking);
            int effortIndex = Math.Max(0, Array.IndexOf(new[] { "low", "high", "max" }, BridgeSettings.Current.DeepSeekReasoningEffort));
            effortIndex = EditorGUILayout.Popup("Reasoning effort", effortIndex, new[] { "low", "high", "max" });
            BridgeSettings.Current.DeepSeekReasoningEffort = new[] { "low", "high", "max" }[effortIndex];
            BridgeSettings.Current.DeepSeekMaxTokens = EditorGUILayout.IntField("Max output tokens", BridgeSettings.Current.DeepSeekMaxTokens);
            EditorGUILayout.HelpBox("Models: deepseek-flash (V4.1 Flash) and deepseek-v4-pro. Thinking mode supports low/high/max effort; required/named tool choice is not available in thinking mode, so the bridge uses auto. Cost is an estimate from the current public rates and peak/off-peak UTC windows.", MessageType.None);
        }

        void DrawOpenCodeSettings()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("OpenCode Go", EditorStyles.boldLabel);
            BridgeSettings.Current.OpenCodeBaseUrl = EditorGUILayout.TextField("Base URL", BridgeSettings.Current.OpenCodeBaseUrl);
            using (new EditorGUILayout.HorizontalScope())
            {
                string key = BridgeSettings.Secrets.OpenCodeApiKey;
                _openCodeKeyDraft = EditorGUILayout.PasswordField(key.Length == 0 ? "API key" : "API key (" + BridgeSettings.Mask(key) + ")", _openCodeKeyDraft);
                if (GUILayout.Button("Save key", EditorStyles.miniButton, GUILayout.Width(76f)))
                {
                    BridgeSettings.Secrets.OpenCodeApiKey = _openCodeKeyDraft.Trim();
                    BridgeSettings.SaveSecrets();
                    _openCodeKeyDraft = string.Empty;
                    OpenCodeGoProvider.ResetResolvedBase();
                    ChatService.RefreshModels(true);
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(54f)))
                {
                    BridgeSettings.Secrets.OpenCodeApiKey = string.Empty;
                    BridgeSettings.SaveSecrets();
                    ChatService.RefreshModels(true);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                {
                    if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(110f)))
                        ChatService.TestConnection();
                }
                if (GUILayout.Button("Get an OpenCode key", EditorStyles.miniButton, GUILayout.Width(140f)))
                    Application.OpenURL("https://opencode.ai/auth");
                if (!string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
                {
                    if (GUILayout.Button("Reuse saved DeepSeek key", EditorStyles.miniButton, GUILayout.Width(170f)))
                        ReuseDeepSeekKeyForOpenCode();
                }
            }
            if (!string.IsNullOrEmpty(ChatService.LastTestResult))
                EditorGUILayout.HelpBox(ChatService.LastTestResult, ChatService.ConnectionState == ProviderConnectionState.Error ? MessageType.Error : MessageType.Info);
            EditorGUILayout.HelpBox(
                "OpenCode Go exposes OpenAI-compatible chat-completions models (DeepSeek, Kimi, GLM, MiMo and others). " +
                "The default base URL is https://opencode.ai/zen/go/v1; the alternate https://opencode.ai/inference/go/openai/v1 is tried if the default is unavailable. " +
                "Some OpenCode Go models (for example GPT/Grok entries) use a Responses endpoint and are not available through this provider. " +
                "OpenCode Go usage is governed by your OpenCode subscription; it is not OpenAI or DeepSeek API billing.",
                MessageType.None);
            EditorGUILayout.LabelField("Model", ChatService.CurrentModel);
        }

        void DrawOpenAiSettings()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("OpenAI API", EditorStyles.boldLabel);
            BridgeSettings.Current.OpenAiBaseUrl = EditorGUILayout.TextField("Base URL", BridgeSettings.Current.OpenAiBaseUrl);
            string openAiModel = EditorGUILayout.TextField("Model", ChatService.CurrentModel);
            if (openAiModel != ChatService.CurrentModel) ChatService.SelectModel(openAiModel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string key = BridgeSettings.Secrets.OpenAiApiKey;
                _apiKeyDraft = EditorGUILayout.PasswordField(key.Length == 0 ? "API key" : "API key (" + BridgeSettings.Mask(key) + ")", _apiKeyDraft);
                if (GUILayout.Button("Save key", EditorStyles.miniButton, GUILayout.Width(76f)))
                {
                    BridgeSettings.Secrets.OpenAiApiKey = _apiKeyDraft.Trim();
                    BridgeSettings.SaveSecrets();
                    _apiKeyDraft = string.Empty;
                    ChatService.RefreshModels(true);
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(54f)))
                {
                    BridgeSettings.Secrets.OpenAiApiKey = string.Empty;
                    BridgeSettings.SaveSecrets();
                    ChatService.RefreshModels(true);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                {
                    if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(110f)))
                        ChatService.TestConnection();
                }
                if (GUILayout.Button("Get an API key", EditorStyles.miniButton, GUILayout.Width(110f)))
                    Application.OpenURL("https://platform.openai.com/api-keys");
            }
            EditorGUILayout.HelpBox("Uses the Responses API with streaming and function tools. Billing is per token on your OpenAI platform account; ChatGPT Plus does not include API usage.", MessageType.Warning);
        }

        void DrawOllamaSettings()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Ollama", EditorStyles.boldLabel);
            BridgeSettings.Current.OllamaUrl = EditorGUILayout.TextField("URL", BridgeSettings.Current.OllamaUrl);
            string ollamaModel = EditorGUILayout.TextField("Model", ChatService.CurrentModel);
            if (ollamaModel != ChatService.CurrentModel) ChatService.SelectModel(ollamaModel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ChatService.IsTesting))
                {
                    if (GUILayout.Button(ChatService.IsTesting ? "Testing..." : "Test connection", EditorStyles.miniButton, GUILayout.Width(110f)))
                        ChatService.TestConnection();
                }
                if (GUILayout.Button("Refresh models", EditorStyles.miniButton, GUILayout.Width(110f))) ChatService.RefreshModels(true);
            }
            EditorGUILayout.HelpBox("Local inference. Install models with: ollama pull " + BridgeSettings.Current.OllamaModel + ". Tool calling depends on the selected model.", MessageType.None);
        }

        // ---------------------------------------------------------------- logs

        void DrawLogs()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _logInfo = GUILayout.Toggle(_logInfo, "Info", EditorStyles.toolbarButton, GUILayout.Width(46f));
                _logWarn = GUILayout.Toggle(_logWarn, "Warn", EditorStyles.toolbarButton, GUILayout.Width(50f));
                _logError = GUILayout.Toggle(_logError, "Error", EditorStyles.toolbarButton, GUILayout.Width(50f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Copy", EditorStyles.toolbarButton, GUILayout.Width(50f))) EditorGUIUtility.systemCopyBuffer = FilteredLogs();
                if (GUILayout.Button("Open file", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                {
                    BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
                    if (!File.Exists(BridgePaths.LogFile)) File.WriteAllText(BridgePaths.LogFile, string.Empty);
                    EditorUtility.OpenWithDefaultApp(BridgePaths.LogFile);
                }
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50f))) BridgeLog.Clear();
                if (GUILayout.Button("Console errors", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                {
                    var errors = ConsoleService.GetErrors(30, true);
                    if (errors.Count == 0) SetFeedback("No console errors captured.");
                    else
                    {
                        var sb = new StringBuilder();
                        foreach (var error in errors) sb.AppendLine(error.Format());
                        EditorGUIUtility.systemCopyBuffer = sb.ToString();
                        SetFeedback("Copied " + errors.Count + " console error(s) to the clipboard.");
                    }
                }
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(_logsScroll, GUILayout.ExpandHeight(true)))
            {
                _logsScroll = scroll.scrollPosition;
                GUILayout.Label(FilteredLogs(), EditorStyles.wordWrappedMiniLabel);
            }
        }

        string FilteredLogs()
        {
            var sb = new StringBuilder();
            var entries = BridgeLog.Snapshot();
            foreach (var entry in entries)
            {
                bool isError = entry.Level == "ERROR";
                bool isWarn = entry.Level == "WARN";
                if (isError && !_logError) continue;
                if (isWarn && !_logWarn) continue;
                if (!isError && !isWarn && !_logInfo) continue;
                sb.AppendLine(entry.Format());
            }
            return sb.ToString();
        }
    }
}
