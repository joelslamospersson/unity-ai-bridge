using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public sealed partial class UnityAIBridgeWindow
    {
        VisualElement _uiRoot;
        VisualElement _uiContent;
        VisualElement _historySection;
        ScrollView _historyScroll;
        ChatPanel _chatPanel;
        IMGUIContainer _legacyHost;

        DropdownField _uiProvider;
        DropdownField _uiModel;
        DropdownField _uiPermission;
        Button _uiPending;
        Button _uiNewChat;
        Button _uiRename;
        Button _uiDelete;
        Button _uiClearHistory;
        TextField _uiSearch;
        TextField _uiRenameField;
        Label _uiStatus;
        Label _uiBrand;

        readonly Dictionary<int, Button> _navButtons = new Dictionary<int, Button>();
        bool _suppressUi;
        bool _headerDirty = true;
        bool _conversationsDirty = true;
        double _lastConversationRefresh;
        string _lastModelSignature = string.Empty;
        string _renameConversationId;

        void CreateGUI()
        {
            _uiRoot = rootVisualElement;
            _uiRoot.Clear();
            _uiRoot.style.flexGrow = 1;
            _uiRoot.style.flexDirection = FlexDirection.Column;
            _uiRoot.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.13f, 0.13f, 0.14f)
                : new Color(0.93f, 0.93f, 0.94f);

            BuildHeader();
            BuildBody();
            BuildStatusBar();

            _chatPanel = new ChatPanel();
            _chatPanel.PendingRequested += () => SetTab(TabChanges);
            _chatPanel.FileRequested += OnFileRequested;

            SetTab(_tab);
            RefreshHeader(true);
            RefreshConversations();
            TickUi();
            _uiRoot.schedule.Execute(TickUi).Every(250);
        }

        internal void SelfTestCreateUi()
        {
            CreateGUI();
        }

        internal int SelfTestRootChildren
        {
            get { return rootVisualElement != null ? rootVisualElement.childCount : -1; }
        }

        internal void SelfTestTeardown()
        {
            DetachUi();
        }

        void DetachUi()
        {
            if (_chatPanel != null)
            {
                _chatPanel.Detach();
                _chatPanel = null;
            }
            _uiRoot = null;
            _legacyHost = null;
            _navButtons.Clear();
        }

        void OnUiChanged()
        {
            _headerDirty = true;
            if (!ChatService.IsBusy) _conversationsDirty = true;
        }

        void BuildHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.height = 40;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.17f, 0.17f, 0.18f)
                : new Color(0.97f, 0.97f, 0.98f);
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = EditorGUIUtility.isProSkin
                ? new Color(0.26f, 0.26f, 0.27f)
                : new Color(0.8f, 0.8f, 0.82f);
            _uiRoot.Add(header);

            _uiBrand = new Label("Unity AI Bridge");
            _uiBrand.style.unityFontStyleAndWeight = FontStyle.Bold;
            _uiBrand.style.fontSize = 13;
            _uiBrand.style.marginRight = 14;
            header.Add(_uiBrand);

            _uiProvider = new DropdownField(new List<string>(ProviderLabels), 0);
            _uiProvider.style.width = 138;
            _uiProvider.style.marginRight = 6;
            _uiProvider.tooltip = "Inference provider";
            _uiProvider.RegisterValueChangedCallback(evt =>
            {
                if (_suppressUi) return;
                int index = Array.IndexOf(ProviderLabels, evt.newValue);
                if (index < 0 || index >= ProviderIds.Length) return;
                ChatService.SwitchProvider(ProviderIds[index]);
                RefreshHeader(true);
                RefreshConversations();
                if (_chatPanel != null) _chatPanel.RefreshAll();
            });
            header.Add(_uiProvider);

            _uiModel = new DropdownField(new List<string>(), 0);
            _uiModel.style.width = 250;
            _uiModel.style.marginRight = 4;
            _uiModel.tooltip = "Model";
            _uiModel.RegisterValueChangedCallback(evt =>
            {
                if (_suppressUi) return;
                int index = _uiModel.index;
                var models = ChatService.Models;
                if (index >= 0 && index < models.Count) ChatService.SelectModel(models[index].Id);
                RefreshHeader(false);
            });
            header.Add(_uiModel);

            var refresh = new Button(() => ChatService.RefreshModels(true)) { text = "Refresh" };
            refresh.style.fontSize = 10;
            refresh.style.marginRight = 10;
            header.Add(refresh);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            header.Add(spacer);

            _uiPermission = new DropdownField(new List<string>(LevelLabels), 0);
            _uiPermission.style.width = 160;
            _uiPermission.style.marginRight = 8;
            _uiPermission.tooltip = "Permission level";
            _uiPermission.RegisterValueChangedCallback(evt =>
            {
                if (_suppressUi) return;
                Permissions.SetLevel((BridgeSettings.AccessMode)_uiPermission.index);
                MarkDirty();
            });
            header.Add(_uiPermission);

            _uiPending = new Button(() => SetTab(TabChanges));
            _uiPending.style.fontSize = 10;
            _uiPending.style.marginRight = 4;
            header.Add(_uiPending);
        }

        void BuildBody()
        {
            var body = new VisualElement();
            body.style.flexGrow = 1;
            body.style.flexDirection = FlexDirection.Row;
            _uiRoot.Add(body);

            var sidebar = new VisualElement();
            sidebar.style.width = 232;
            sidebar.style.paddingLeft = 8;
            sidebar.style.paddingRight = 8;
            sidebar.style.paddingTop = 8;
            sidebar.style.paddingBottom = 8;
            sidebar.style.borderRightWidth = 1;
            sidebar.style.borderRightColor = EditorGUIUtility.isProSkin
                ? new Color(0.24f, 0.24f, 0.25f)
                : new Color(0.82f, 0.82f, 0.84f);
            sidebar.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.15f, 0.15f, 0.16f)
                : new Color(0.95f, 0.95f, 0.96f);
            sidebar.style.flexShrink = 0;
            body.Add(sidebar);

            for (int i = 0; i < TabNames.Length; i++)
            {
                int tab = i;
                var button = new Button(() => SetTab(tab));
                button.text = TabNames[i];
                button.style.height = 30;
                button.style.marginBottom = 2;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.paddingLeft = 10;
                button.style.fontSize = 11;
                button.style.borderTopLeftRadius = 4;
                button.style.borderTopRightRadius = 4;
                button.style.borderBottomLeftRadius = 4;
                button.style.borderBottomRightRadius = 4;
                sidebar.Add(button);
                _navButtons[tab] = button;
            }

            _historySection = new VisualElement();
            _historySection.style.flexGrow = 1;
            _historySection.style.marginTop = 10;
            sidebar.Add(_historySection);

            var conversations = new Label("CONVERSATIONS");
            conversations.style.fontSize = 9;
            conversations.style.marginBottom = 4;
            conversations.style.color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.4f, 0.4f, 0.4f);
            _historySection.Add(conversations);

            _uiNewChat = new Button(() =>
            {
                ChatService.NewChat();
                RefreshHeader(true);
                RefreshConversations();
                if (_chatPanel != null) _chatPanel.RefreshAll();
            }) { text = "New chat" };
            _uiNewChat.style.fontSize = 10;
            _uiNewChat.style.marginBottom = 4;
            _historySection.Add(_uiNewChat);

            var searchLabel = new Label("SEARCH");
            searchLabel.style.fontSize = 9;
            searchLabel.style.marginTop = 4;
            searchLabel.style.marginBottom = 2;
            searchLabel.style.color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.4f, 0.4f, 0.4f);
            _historySection.Add(searchLabel);
            _uiSearch = new TextField();
            _uiSearch.textEdition.placeholder = "Search conversations";
            _uiSearch.style.height = 22;
            _uiSearch.style.marginBottom = 6;
            _uiSearch.RegisterValueChangedCallback(_ => RefreshConversations());
            _historySection.Add(_uiSearch);

            _historyScroll = new ScrollView();
            _historyScroll.style.flexGrow = 1;
            _historyScroll.style.minHeight = 100;
            _historySection.Add(_historyScroll);

            var renameRow = new VisualElement();
            renameRow.style.flexDirection = FlexDirection.Row;
            renameRow.style.marginTop = 4;
            _uiRenameField = new TextField();
            _uiRenameField.style.flexGrow = 1;
            _uiRenameField.style.minWidth = 40;
            renameRow.Add(_uiRenameField);
            _uiRename = new Button(() =>
            {
                if (ChatService.CurrentConversation == null) return;
                ChatService.RenameConversation(ChatService.CurrentConversation.Id, _uiRenameField.value);
                RefreshConversations();
            }) { text = "Rename" };
            _uiRename.style.fontSize = 9;
            renameRow.Add(_uiRename);
            _historySection.Add(renameRow);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 4;
            _uiDelete = new Button(() =>
            {
                var current = ChatService.CurrentConversation;
                if (current == null) return;
                if (!EditorUtility.DisplayDialog("Delete conversation", "Delete \"" + current.Title + "\"? This cannot be undone.", "Delete", "Cancel")) return;
                ChatService.DeleteConversation(current.Id);
                RefreshHeader(true);
                RefreshConversations();
                if (_chatPanel != null) _chatPanel.RefreshAll();
            }) { text = "Delete" };
            _uiDelete.style.fontSize = 9;
            _uiDelete.style.marginRight = 4;
            actions.Add(_uiDelete);

            _uiClearHistory = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Clear chat history", "Delete all conversations for this provider? This cannot be undone.", "Clear history", "Cancel")) return;
                ChatService.ClearHistory();
                RefreshHeader(true);
                RefreshConversations();
                if (_chatPanel != null) _chatPanel.RefreshAll();
            }) { text = "Clear history" };
            _uiClearHistory.style.fontSize = 9;
            actions.Add(_uiClearHistory);
            _historySection.Add(actions);

            _uiContent = new VisualElement();
            _uiContent.style.flexGrow = 1;
            _uiContent.style.minWidth = 320;
            body.Add(_uiContent);
        }

        void BuildStatusBar()
        {
            _uiStatus = new Label();
            _uiStatus.style.height = 22;
            _uiStatus.style.fontSize = 10;
            _uiStatus.style.paddingLeft = 10;
            _uiStatus.style.unityTextAlign = TextAnchor.MiddleLeft;
            _uiStatus.style.borderTopWidth = 1;
            _uiStatus.style.borderTopColor = EditorGUIUtility.isProSkin
                ? new Color(0.24f, 0.24f, 0.25f)
                : new Color(0.82f, 0.82f, 0.84f);
            _uiStatus.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.16f, 0.16f, 0.17f)
                : new Color(0.96f, 0.96f, 0.97f);
            _uiStatus.style.color = EditorGUIUtility.isProSkin
                ? new Color(0.65f, 0.65f, 0.65f)
                : new Color(0.35f, 0.35f, 0.35f);
            _uiRoot.Add(_uiStatus);
        }

        void SetTab(int tab)
        {
            if (tab < 0 || tab >= TabNames.Length) tab = TabChat;
            _tab = tab;
            foreach (var pair in _navButtons)
            {
                bool selected = pair.Key == tab;
                pair.Value.style.backgroundColor = selected
                    ? (EditorGUIUtility.isProSkin ? new Color(0.24f, 0.3f, 0.38f) : new Color(0.82f, 0.88f, 0.95f))
                    : Color.clear;
                pair.Value.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
            }
            if (_historySection != null)
                _historySection.style.display = tab == TabChat ? DisplayStyle.Flex : DisplayStyle.None;
            if (_uiContent == null) return;
            _uiContent.Clear();
            _legacyHost = null;
            if (tab == TabChat)
            {
                if (_chatPanel == null) _chatPanel = new ChatPanel();
                _uiContent.Add(_chatPanel);
                _chatPanel.RefreshAll();
            }
            else
            {
                _legacyHost = new IMGUIContainer(DrawLegacyTab);
                _legacyHost.style.flexGrow = 1;
                _uiContent.Add(_legacyHost);
            }
            _headerDirty = true;
        }

        void DrawLegacyTab()
        {
            int drawn = _tab;
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp((position.width - 280f) * 0.26f, 190f, 310f);
            try
            {
                switch (drawn)
                {
                    case TabProject: DrawProject(); break;
                    case TabChanges: DrawChanges(); break;
                    case TabMcp: DrawMcp(); break;
                    case TabSettings: DrawSettings(); break;
                    case TabLogs: DrawLogs(); break;
                }
            }
            finally { EditorGUIUtility.labelWidth = previousLabelWidth; }
            if (_tab != drawn && _uiRoot != null)
                _uiRoot.schedule.Execute(() => SetTab(_tab));
        }

        void TickUi()
        {
            if (_uiRoot == null) return;
            if (_headerDirty) RefreshHeader(false);
            RefreshStatus();
            if (_conversationsDirty && EditorApplication.timeSinceStartup - _lastConversationRefresh > 1.0)
                RefreshConversations();
            RefreshPendingButton();
        }

        void RefreshHeader(bool rebuildModels)
        {
            if (_uiProvider == null || _suppressUi) return;
            _suppressUi = true;
            try
            {
                int providerIndex = Array.IndexOf(ProviderIds, ChatService.CurrentProviderId);
                if (providerIndex < 0) providerIndex = 0;
                if (_uiProvider.index != providerIndex) _uiProvider.index = providerIndex;

                string signature = ChatService.Models.Count + "|" + ChatService.CurrentModel + "|" + (ChatService.IsLoadingModels ? "1" : "0");
                if (rebuildModels || signature != _lastModelSignature)
                {
                    _lastModelSignature = signature;
                    var labels = new List<string>();
                    foreach (var model in ChatService.Models)
                    {
                        string name = string.IsNullOrEmpty(model.DisplayName) ? model.Id : model.DisplayName;
                        labels.Add(name == model.Id ? model.Id : name + "  ·  " + model.Id);
                    }
                    if (labels.Count == 0) labels.Add(ChatService.IsLoadingModels ? "Loading models…" : "No models");
                    _uiModel.choices = labels;
                    int modelIndex = 0;
                    for (int i = 0; i < ChatService.Models.Count; i++)
                        if (ChatService.Models[i].Id == ChatService.CurrentModel) { modelIndex = i; break; }
                    if (modelIndex >= labels.Count) modelIndex = 0;
                    _uiModel.index = modelIndex;
                    _uiModel.SetEnabled(ChatService.Models.Count > 0 && !ChatService.IsLoadingModels);
                }

                int permission = (int)BridgeSettings.Current.Level;
                if (_uiPermission.index != permission) _uiPermission.index = permission;
            }
            finally { _suppressUi = false; }
            _headerDirty = false;
        }

        void RefreshPendingButton()
        {
            if (_uiPending == null) return;
            int pending = ChangeService.PendingCount;
            _uiPending.text = pending > 0 ? "Review " + pending + " pending" : "No pending changes";
            _uiPending.SetEnabled(pending > 0);
            Button changesButton;
            if (_navButtons.TryGetValue(TabChanges, out changesButton))
                changesButton.text = pending > 0 ? "Changes  (" + pending + ")" : "Changes";
            Button mcpButton;
            if (_navButtons.TryGetValue(TabMcp, out mcpButton))
                mcpButton.text = McpServer.Current != null && McpServer.Current.IsRunning ? "MCP  ·  on" : "MCP";
        }

        void RefreshStatus()
        {
            if (_uiStatus == null) return;
            string text = ChatService.ProviderStatusText();
            if (!string.IsNullOrEmpty(ChatService.StatusMessage)) text += "  ·  " + ChatService.StatusMessage;
            _uiStatus.text = text;
            var state = ChatService.ConnectionState;
            _uiStatus.style.color = state == ProviderConnectionState.Error
                ? new Color(0.9f, 0.4f, 0.35f)
                : state == ProviderConnectionState.Connected
                    ? (EditorGUIUtility.isProSkin ? new Color(0.45f, 0.78f, 0.5f) : new Color(0.16f, 0.48f, 0.22f))
                    : (EditorGUIUtility.isProSkin ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.35f, 0.35f, 0.35f));
            _uiStatus.tooltip = ChatService.ConnectionMessage ?? string.Empty;
        }

        void RefreshConversations()
        {
            if (_historyScroll == null) return;
            _lastConversationRefresh = EditorApplication.timeSinceStartup;
            _historyScroll.Clear();
            string search = _uiSearch != null ? (_uiSearch.value ?? string.Empty).Trim() : string.Empty;
            var conversations = ChatHistory.List(ChatService.CurrentProviderId, string.IsNullOrEmpty(search) ? null : search);
            string currentId = ChatService.CurrentConversation != null ? ChatService.CurrentConversation.Id : null;
            int shown = 0;
            foreach (var conversation in conversations)
            {
                if (shown++ >= 80) break;
                var meta = conversation;
                var button = new Button(() =>
                {
                    ChatService.OpenConversation(meta.Id);
                    RefreshConversations();
                    RefreshHeader(false);
                    if (_chatPanel != null) _chatPanel.RefreshAll();
                });
                button.text = string.IsNullOrEmpty(meta.Title) ? "Untitled" : meta.Title;
                button.tooltip = (meta.MessageCount + " message(s)\nUpdated " + meta.UpdatedUtc) ?? string.Empty;
                button.style.height = 27;
                button.style.marginBottom = 2;
                button.style.fontSize = 10;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.paddingLeft = 8;
                button.style.borderTopLeftRadius = 4;
                button.style.borderTopRightRadius = 4;
                button.style.borderBottomLeftRadius = 4;
                button.style.borderBottomRightRadius = 4;
                if (meta.Id == currentId)
                {
                    button.style.backgroundColor = EditorGUIUtility.isProSkin
                        ? new Color(0.24f, 0.3f, 0.38f)
                        : new Color(0.82f, 0.88f, 0.95f);
                    button.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
                _historyScroll.Add(button);
            }
            if (shown == 0)
            {
                var empty = new Label(string.IsNullOrEmpty(search) ? "No conversations yet." : "No matching conversations.");
                empty.style.fontSize = 10;
                empty.style.color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.45f, 0.45f, 0.45f);
                _historyScroll.Add(empty);
            }

            var current = ChatService.CurrentConversation;
            if (current != null && _uiRenameField != null)
            {
                if (_renameConversationId != current.Id)
                {
                    _renameConversationId = current.Id;
                    _uiRenameField.SetValueWithoutNotify(current.Title ?? string.Empty);
                }
            }
            _conversationsDirty = false;
        }

        void OnFileRequested(ChatFileRef file)
        {
            if (file == null) return;
            if (!string.IsNullOrEmpty(file.ChangeId))
            {
                _selectedChangeId = file.ChangeId;
                SetTab(TabChanges);
                return;
            }
            if (string.IsNullOrEmpty(file.Path)) return;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(file.Path);
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
