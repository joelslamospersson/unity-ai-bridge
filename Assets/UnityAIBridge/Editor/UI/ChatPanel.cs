using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    internal sealed class ChatPanel : VisualElement
    {
        readonly ScrollView _transcript;
        readonly Dictionary<string, ChatMessageView> _messageViews = new Dictionary<string, ChatMessageView>(StringComparer.Ordinal);
        readonly Toggle _autoAccept;
        readonly TextField _input;
        readonly Button _send;
        readonly Button _attach;
        readonly Button _pending;
        readonly Button _jump;
        readonly Button _older;
        readonly Label _empty;
        readonly VisualElement _attachments;
        readonly Label _hint;
        readonly VisualElement _accountRow;
        readonly Label _accountStatus;
        readonly Button _connect;
        readonly Button _reopen;
        readonly Button _accountSettings;
        readonly Button _manageUsage;
        readonly Button _claudeLogin;
        readonly Button _claudeOpen;
        readonly VisualElement _planWelcome;
        readonly DropdownField _mode;
        readonly DropdownField _effort;
        readonly Label _runStatus;
        readonly RunActivityView _activity;
        readonly Button _retry;
        readonly ChatApprovalView _approval;
        string _accountSignature;
        string _draftKey;
        static readonly Dictionary<string, string> Drafts = new Dictionary<string, string>();

        readonly Dictionary<string, bool> _activityExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> _reasoningExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool _dirty = true;
        bool _follow = true;
        bool _detached;
        readonly Dictionary<string, string> _messageSignatures = new Dictionary<string, string>(StringComparer.Ordinal);

        public event Action PendingRequested;
        public event Action AccountRequested;
        public event Action<ChatFileRef> FileRequested;

        public ChatPanel()
        {
            style.flexGrow = 1;
            style.flexBasis = 0;
            style.minHeight = 0;
            style.minWidth = 0;
            style.flexDirection = FlexDirection.Column;
            style.paddingLeft = 10;
            style.paddingRight = 10;
            style.paddingTop = 8;
            style.paddingBottom = 8;

            _accountRow = new VisualElement { name = "chat-account", style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginBottom = 8, paddingTop = 6, paddingBottom = 6 } };
            _accountStatus = new Label();
            _accountStatus.style.flexGrow = 1;
            _accountStatus.style.whiteSpace = WhiteSpace.Normal;
            _accountStatus.style.minWidth = 160;
            _accountStatus.style.fontSize = 11;
            _accountRow.Add(_accountStatus);
            _connect = new Button(() =>
            {
                if (ChatGptAuth.IsBusy) ChatGptAuth.CancelSignIn();
                else ChatService.UseChatGptPlan(ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt && ChatGptAuth.IsReadyForInference);
            }) { name = "chatgpt-connect" };
            _accountRow.Add(_connect);
            _reopen = new Button(() =>
            {
                if (!string.IsNullOrEmpty(ChatGptAuth.PendingAuthorizationUrl)) Application.OpenURL(ChatGptAuth.PendingAuthorizationUrl);
            }) { text = "Open sign-in page" };
            _accountRow.Add(_reopen);
            _manageUsage = new Button(() => Application.OpenURL(ChatGptAuth.UsageUrl)) { text = "Manage usage", name = "chatgpt-manage-usage" };
            _accountRow.Add(_manageUsage);
            _claudeLogin = new Button(() =>
            {
                if (ClaudeSubscriptionHandoff.IsBusy) ClaudeSubscriptionHandoff.CancelLogin(); else ClaudeSubscriptionHandoff.Login();
            }) { text = "Sign in to Claude Code", name = "claude-subscription-login" };
            _accountRow.Add(_claudeLogin);
            _claudeOpen = new Button(ClaudeSubscriptionHandoff.OpenClient) { text = "Open Claude Code", name = "claude-subscription-open" };
            _accountRow.Add(_claudeOpen);
            _accountSettings = new Button(() => { if (AccountRequested != null) AccountRequested(); }) { text = "Account & settings" };
            _accountRow.Add(_accountSettings);
            Add(_accountRow);

            _planWelcome = new VisualElement { name = "chatgpt-plan-welcome", style = { flexShrink = 0, paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 8, marginBottom = 8 } };
            var welcomeTitle = new Label("You’re using your ChatGPT plan");
            welcomeTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _planWelcome.Add(welcomeTitle);
            var welcomeText = new Label("Eligible requests in Unity AI Bridge use your ChatGPT Plus or Pro plan. Review your usage and app limits with Manage usage.");
            welcomeText.style.whiteSpace = WhiteSpace.Normal;
            _planWelcome.Add(welcomeText);
            var dismissWelcome = new Button(() =>
            {
                BridgeSettings.Current.ChatGptPlanWelcomeShown = true;
                BridgeSettings.Save();
                _accountSignature = null;
                UpdateAccount();
            }) { text = "Got it", name = "chatgpt-plan-welcome-dismiss" };
            dismissWelcome.style.alignSelf = Align.FlexStart;
            _planWelcome.Add(dismissWelcome);
            Add(_planWelcome);

            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            _older = new Button(LoadOlder) { text = "Load older messages" };
            _older.style.fontSize = 10;
            _older.style.display = DisplayStyle.None;
            top.Add(_older);
            var spacer = new VisualElement(); spacer.style.flexGrow = 1; top.Add(spacer);
            _jump = new Button(ScrollToLatest) { text = "Jump to latest" };
            _jump.style.fontSize = 10;
            _jump.style.display = DisplayStyle.None;
            top.Add(_jump);
            Add(top);

            _empty = new Label("Ask about your project, inspect assets, or request changes.\n\nEnter to send · Shift+Enter for a new line · Attach selection adds project files.");
            _empty.style.whiteSpace = WhiteSpace.Normal;
            _empty.style.fontSize = 12;
            _empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _empty.style.marginTop = 22;
            _empty.style.marginBottom = 18;
            _empty.style.color = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.4f, 0.4f, 0.4f);
            _empty.style.display = DisplayStyle.None;
            Add(_empty);

            // History is already paged. Keep live rows in normal layout so a
            // growing stream or expanded tool result never uses a cached height.
            _transcript = new ScrollView(ScrollViewMode.Vertical) { name = "chat-transcript" };
            _transcript.style.flexGrow = 1;
            _transcript.style.flexBasis = 0;
            _transcript.style.minHeight = 40;
            _transcript.style.marginTop = 2;
            _transcript.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _transcript.contentContainer.style.flexShrink = 0;
            _transcript.contentContainer.style.width = Length.Percent(100);
            _transcript.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => FollowAfterLayout());
            _transcript.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => FollowAfterLayout());
            _transcript.RegisterCallback<WheelEvent>(evt =>
            {
                if (evt.delta.y < -0.01f)
                {
                    _follow = false;
                    UpdateJump();
                }
            }, TrickleDown.TrickleDown);
            _transcript.verticalScroller.RegisterCallback<ChangeEvent<float>>(evt =>
            {
                // Dragging the scrollbar must allow reading earlier output too.
                if (evt.newValue < evt.previousValue - 1 &&
                    evt.newValue < _transcript.verticalScroller.highValue - 2)
                {
                    _follow = false;
                    UpdateJump();
                }
            });
            Add(_transcript);

            _approval = new ChatApprovalView();
            Add(_approval);

            _activity = new RunActivityView();
            Add(_activity);

            _runStatus = new Label { name = "chat-run-status" };
            _runStatus.style.fontSize = 11;
            _runStatus.style.whiteSpace = WhiteSpace.Normal;
            _runStatus.style.marginTop = 4;
            _runStatus.style.marginBottom = 4;
            Add(_runStatus);
            _retry = new Button(() => ChatService.RetryLastResponse()) { text = "Retry response", name = "chat-retry", tooltip = "Continue the last request using recorded tool results without adding a duplicate prompt." };
            _retry.style.alignSelf = Align.FlexStart;
            Add(_retry);

            _attachments = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            Add(_attachments);

            var composer = new VisualElement();
            composer.style.marginTop = 8;
            composer.style.flexShrink = 0;
            composer.style.borderTopWidth = 1;
            composer.style.borderBottomWidth = 1;
            composer.style.borderLeftWidth = 1;
            composer.style.borderRightWidth = 1;
            composer.style.borderTopColor = composer.style.borderBottomColor =
                composer.style.borderLeftColor = composer.style.borderRightColor =
                EditorGUIUtility.isProSkin ? new Color(0.3f, 0.3f, 0.32f) : new Color(0.78f, 0.79f, 0.81f);
            composer.style.borderTopLeftRadius = 10;
            composer.style.borderTopRightRadius = 10;
            composer.style.borderBottomLeftRadius = 10;
            composer.style.borderBottomRightRadius = 10;
            composer.style.paddingLeft = 8;
            composer.style.paddingRight = 8;
            composer.style.paddingTop = 7;
            composer.style.paddingBottom = 6;
            composer.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.17f, 0.17f, 0.18f) : new Color(0.99f, 0.99f, 0.99f);
            Add(composer);

            var options = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginBottom = 5 } };
            _mode = new DropdownField(new List<string> { "Chat", "Plan", "Agent" }, 2) { name = "chat-mode", tooltip = "Chat answers without tools. Plan inspects without editing. Agent can stage changes for review." };
            _mode.style.width = 100;
            _mode.RegisterValueChangedCallback(evt =>
            {
                BridgeSettings.Current.ChatMode = new[] { "chat", "plan", "agent" }[Mathf.Clamp(_mode.index, 0, 2)];
                BridgeSettings.Save();
            });
            options.Add(_mode);
            var effortLabel = new Label("Reasoning"); effortLabel.style.marginLeft = 10; options.Add(effortLabel);
            _effort = new DropdownField(new List<string> { "Default", "Low", "Medium", "High" }, 2) { name = "chat-reasoning", tooltip = "Controls model reasoning effort for the next response. Model support varies; Default uses the provider default." };
            _effort.style.width = 110;
            _effort.RegisterValueChangedCallback(evt =>
            {
                var settings = BridgeSettings.Current;
                bool openai = IsOpenAi();
                if (ChatService.CurrentProviderId == BridgeSettings.ProviderClaude)
                {
                    settings.ClaudeThinking = _effort.value != "Off";
                    settings.ClaudeEffort = _effort.value.ToLowerInvariant();
                }
                else if (ChatService.CurrentProviderId == BridgeSettings.ProviderGemini) settings.GeminiThinking = _effort.value == "Summaries";
                else if (openai) settings.OpenAiReasoningEffort = _effort.value.ToLowerInvariant();
                else
                {
                    settings.DeepSeekThinking = _effort.value != "Off";
                    settings.DeepSeekReasoningEffort = _effort.value.ToLowerInvariant();
                }
                BridgeSettings.Save();
            });
            options.Add(_effort);
            _autoAccept = new Toggle("Auto accept all for this conversation")
            {
                name = "chat-auto-accept",
                tooltip = "Accept and apply all tool requests and proposed changes, including destructive changes, in this conversation. Paid image generation always needs explicit billing consent. Turn off to review each project operation. New conversations start with this off."
            };
            _autoAccept.style.marginLeft = 10;
            _autoAccept.RegisterValueChangedCallback(evt => ChatService.SetConversationAutoAccept(evt.newValue));
            options.Add(_autoAccept);
            composer.Add(options);

            _input = new TextField { multiline = true, name = "chat-composer" };
            _input.textEdition.placeholder = "Message Unity AI Bridge…";
            _input.style.height = 76;
            _input.style.minHeight = 76;
            _input.style.maxHeight = 180;
            _input.style.flexShrink = 0;
            _input.style.whiteSpace = WhiteSpace.Normal;
            _input.style.fontSize = 13;
            _input.RegisterCallback<KeyDownEvent>(OnComposerKeyDown, TrickleDown.TrickleDown);
            _input.RegisterValueChangedCallback(_ => { if (_draftKey != null) Drafts[_draftKey] = _input.value ?? string.Empty; UpdateInputHeight(); UpdateComposer(); });
            composer.Add(_input);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 5, alignItems = Align.Center, flexShrink = 0 } };
            _attach = new Button(AttachSelection) { text = "Attach selection" };
            _attach.style.fontSize = 10;
            row.Add(_attach);

            _pending = new Button(() => { if (PendingRequested != null) PendingRequested(); }) { text = "0 pending" };
            _pending.style.fontSize = 10;
            _pending.style.marginLeft = 6;
            _pending.style.display = DisplayStyle.None;
            row.Add(_pending);

            var flexible = new VisualElement(); flexible.style.flexGrow = 1; row.Add(flexible);

            _hint = new Label("Enter to send · Shift+Enter for a new line");
            _hint.style.fontSize = 9;
            _hint.style.marginRight = 8;
            _hint.style.color = EditorGUIUtility.isProSkin ? new Color(0.52f, 0.52f, 0.52f) : new Color(0.45f, 0.45f, 0.45f);
            row.Add(_hint);

            _send = new Button(() =>
            {
                if (ChatService.IsBusy) ChatService.Stop();
                else SendCurrent();
            }) { text = "Send", name = "chat-send-stop" };
            _send.style.fontSize = 11;
            _send.style.minWidth = 88;
            _send.style.height = 27;
            _send.style.unityFontStyleAndWeight = FontStyle.Bold;
            _send.style.borderTopLeftRadius = 6;
            _send.style.borderTopRightRadius = 6;
            _send.style.borderBottomLeftRadius = 6;
            _send.style.borderBottomRightRadius = 6;
            _send.style.color = Color.white;
            row.Add(_send);
            composer.Add(row);

            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape && ChatService.IsBusy)
                {
                    evt.StopPropagation();
                    ChatService.Stop();
                }
            }, TrickleDown.TrickleDown);

            ChatService.Changed += OnServiceChanged;
            schedule.Execute(Tick).Every(80);
            RefreshAll();
        }

        public void Detach()
        {
            if (_detached) return;
            _detached = true;
            if (_draftKey != null) Drafts[_draftKey] = _input.value ?? string.Empty;
            ChatService.Changed -= OnServiceChanged;
        }

        void OnServiceChanged()
        {
            _dirty = true;
        }

        public void RefreshAll()
        {
            _messageSignatures.Clear();
            _follow = true;
            _dirty = true;
            Tick();
            schedule.Execute(() => { if (_follow) ScrollToLatest(); }).StartingIn(120);
            schedule.Execute(() => { if (_follow) ScrollToLatest(); }).StartingIn(320);
        }

        void Tick()
        {
            if (_detached) return;
            _autoAccept.SetValueWithoutNotify(ChatService.ConversationAutoAccept);
            _approval.ShowPrompt(ChatApproval.Pending);
            UpdateAccount();
            UpdateDraft();
            UpdateComposer();
            UpdatePending();
            UpdateAttachments();
            _older.style.display = ChatService.HasOlderMessages ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = ChatService.Messages.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_dirty) return;
            _dirty = false;
            Apply();
        }

        void Apply()
        {
            // The active assistant may precede status or tool rows. Refresh every
            // changed message, not just the last row in the transcript.
            SyncMessageViews(ChatService.Messages);
            FollowAfterLayout();
            UpdateJump();
        }

        void SyncMessageViews(List<ChatMessage> messages)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                ids.Add(message.Id);
                ChatMessageView view;
                if (!_messageViews.TryGetValue(message.Id, out view))
                {
                    view = new ChatMessageView(this);
                    _messageViews.Add(message.Id, view);
                }
                if (view.parent != _transcript.contentContainer || _transcript.IndexOf(view) != i)
                    _transcript.Insert(i, view);
                string signature = Signature(message);
                string previous;
                if (!_messageSignatures.TryGetValue(message.Id, out previous) || previous != signature)
                {
                    view.Bind(message);
                    _messageSignatures[message.Id] = signature;
                }
            }
            var removed = new List<string>();
            foreach (var pair in _messageViews)
                if (!ids.Contains(pair.Key)) { pair.Value.RemoveFromHierarchy(); removed.Add(pair.Key); }
            foreach (var id in removed) { _messageViews.Remove(id); _messageSignatures.Remove(id); }
        }

        void FollowAfterLayout()
        {
            // Scroll only after Unity has measured the complete new content.
            if (_follow) schedule.Execute(() =>
            {
                if (_follow) _transcript.verticalScroller.value = _transcript.verticalScroller.highValue;
            });
        }

        static string Signature(ChatMessage message)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(message.Content).Append('|').Append(message.Reasoning);
            builder.Append('|').Append(message.IsStreaming).Append('|').Append(message.IsError).Append('|').Append(message.Usage);
            if (message.Activities != null)
                foreach (var activity in message.Activities)
                {
                    if (activity == null) continue;
                    builder.Append('|').Append(activity.CallId).Append(':').Append(activity.Status).Append(':').Append(activity.Ok);
                    builder.Append(':').Append(activity.Detail).Append(':').Append(activity.Output);
                }
            if (message.Files != null)
                foreach (var file in message.Files)
                    if (file != null) builder.Append('|').Append(file.Path).Append(':').Append(file.ChangeId).Append(':').Append(file.Status);
            return builder.ToString();
        }

        void SendCurrent()
        {
            string text = _input.value;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text.Trim()) || ChatService.IsBusy) return;
            if (!ChatService.Send(text)) return;
            _input.SetValueWithoutNotify(string.Empty);
            if (_draftKey != null) Drafts.Remove(_draftKey);
            _follow = true;
            _input.Focus();
            UpdateInputHeight();
            UpdateComposer();
        }

        void LoadOlder()
        {
            if (ChatService.LoadOlderMessages() > 0)
            {
                SyncMessageViews(ChatService.Messages);
                _follow = false;
                UpdateJump();
            }
        }

        void ScrollToLatest()
        {
            _follow = true;
            UpdateJump();
            _transcript.verticalScroller.value = _transcript.verticalScroller.highValue;
            FollowAfterLayout();
        }

        void UpdateJump()
        {
            _jump.style.display = _follow ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void OnComposerKeyDown(KeyDownEvent evt)
        {
            bool isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter ||
                           evt.character == '\n' || evt.character == '\r';
            if (!isEnter) return;

            bool wantsNewline = evt.altKey || evt.shiftKey ||
                                (evt.modifiers & (EventModifiers.Alt | EventModifiers.Shift)) != 0;

            if (wantsNewline)
            {
                string before = _input.value ?? string.Empty;
                int cursor = Mathf.Clamp(_input.cursorIndex, 0, before.Length);
                int select = Mathf.Clamp(_input.selectIndex, 0, before.Length);
                int start = Mathf.Min(cursor, select);
                int end = Mathf.Max(cursor, select);

                // Let the multiline TextField insert the newline, then restore focus/caret.
                // Some editor versions drop focus after this key combination.
                _input.schedule.Execute(() =>
                {
                    if (_input.panel == null) return;

                    string after = _input.value ?? string.Empty;
                    int position = start + 1;
                    if (string.Equals(after, before, StringComparison.Ordinal))
                    {
                        _input.value = before.Substring(0, start) + "\n" + before.Substring(end);
                    }

                    _input.Focus();
                    int length = (_input.value ?? string.Empty).Length;
                    _input.cursorIndex = Mathf.Clamp(position, 0, length);
                    _input.selectIndex = _input.cursorIndex;
                }).StartingIn(0);
                return;
            }

            if (_input.focusController != null) _input.focusController.IgnoreEvent(evt);
            evt.StopImmediatePropagation();
            SendCurrent();
        }

        void UpdateInputHeight()
        {
            string text = _input.value ?? string.Empty;
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            int wrapped = Mathf.CeilToInt(text.Length / 90f);
            float height = Mathf.Clamp(30f + Mathf.Max(lines, wrapped) * 18f, 76f, 180f);
            _input.style.height = height;
        }

        void UpdateComposer()
        {
            bool busy = ChatService.IsBusy;
            _send.text = busy ? "Stop" : "Send";
            string reason;
            bool ready = ProviderFactory.IsConfigured(ChatService.CurrentProviderId, out reason) && !string.IsNullOrEmpty(ChatService.CurrentModel);
            _send.SetEnabled(busy || (ready && !string.IsNullOrWhiteSpace(_input.value)));
            _send.tooltip = ready || busy ? string.Empty : (reason ?? "Select a model to send.");
            _mode.SetEnabled(!busy);
            _effort.SetEnabled(!busy && (IsOpenAi() ? OpenAiResponsesProvider.SupportsReasoning(ChatService.CurrentModel) : ChatService.CurrentProviderId != BridgeSettings.ProviderOllama));
            _activity.Show(ChatService.ActivitySnapshot, busy);
            _runStatus.style.display = !busy && !string.IsNullOrEmpty(ChatService.LastError) ? DisplayStyle.Flex : DisplayStyle.None;
            _retry.style.display = !busy && !string.IsNullOrEmpty(ChatService.LastError) ? DisplayStyle.Flex : DisplayStyle.None;
            _runStatus.text = ChatService.LastError ?? "";
            _send.style.backgroundColor = busy
                ? new Color(0.55f, 0.22f, 0.22f)
                : (EditorGUIUtility.isProSkin ? new Color(0.2f, 0.42f, 0.64f) : new Color(0.2f, 0.46f, 0.72f));
            _send.style.borderTopColor = _send.style.borderBottomColor =
                _send.style.borderLeftColor = _send.style.borderRightColor =
                busy ? new Color(0.7f, 0.3f, 0.3f) : new Color(0.28f, 0.52f, 0.76f);
            _send.style.borderTopWidth = _send.style.borderBottomWidth = 1;
            _send.style.borderLeftWidth = _send.style.borderRightWidth = 1;
            bool externalClaude = ChatService.CurrentProviderId == BridgeSettings.ProviderClaudeSubscription;
            _input.SetEnabled(!externalClaude);
            _attach.SetEnabled(!busy && !externalClaude);
            _hint.text = externalClaude ? "Subscription chat runs in the official Claude Code client" : ChatApproval.Pending != null ? "Waiting for your decision above" : busy ? "Esc or Stop to cancel" : "Enter to send · Shift+Enter for a new line";
        }

        static bool IsOpenAi()
        {
            return ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt || ChatService.CurrentProviderId == BridgeSettings.ProviderOpenAi;
        }

        void UpdateDraft()
        {
            string key = ChatService.CurrentProviderId + ":" + (ChatService.CurrentConversation != null ? ChatService.CurrentConversation.Id : "new");
            if (key != _draftKey)
            {
                if (_draftKey != null) Drafts[_draftKey] = _input.value ?? string.Empty;
                _draftKey = key;
                string draft;
                _input.SetValueWithoutNotify(Drafts.TryGetValue(key, out draft) ? draft : string.Empty);
                UpdateInputHeight();
            }
        }

        void UpdateAccount()
        {
            var settings = BridgeSettings.Current;
            bool chatgpt = ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt;
            bool externalClaude = ChatService.CurrentProviderId == BridgeSettings.ProviderClaudeSubscription;
            string signature = ChatService.CurrentProviderId + "|" + ChatGptAuth.Status + "|" + ChatGptAuth.IsBusy + "|" + ChatService.IsBusy + "|" + ChatGptAuth.AccountLabel + "|" + ChatGptAuth.LastError + "|" + ChatService.ConnectionMessage + "|" + ChatGptAuth.PendingAuthorizationUrl + "|" + settings.ChatMode + "|" + settings.OpenAiReasoningEffort + "|" + settings.DeepSeekReasoningEffort + "|" + settings.DeepSeekThinking + "|" + settings.ChatGptPlanWelcomeShown + "|" + (ChatApproval.Pending == null ? "" : ChatApproval.Pending.Id) + "|" + settings.ClaudeThinking + settings.ClaudeEffort + settings.GeminiThinking + ClaudeSubscriptionHandoff.Status + ClaudeSubscriptionHandoff.IsBusy;
            if (signature == _accountSignature) return;
            _accountSignature = signature;
            _connect.style.display = externalClaude ? DisplayStyle.None : DisplayStyle.Flex;
            _claudeLogin.style.display = _claudeOpen.style.display = externalClaude ? DisplayStyle.Flex : DisplayStyle.None;
            _claudeLogin.text = ClaudeSubscriptionHandoff.IsBusy ? "Cancel sign-in" : "Sign in to Claude Code";
            _connect.text = ChatGptAuth.IsBusy ? "Cancel sign-in" : ChatGptAuth.IsReadyForInference ? (chatgpt ? "Reconnect" : "Use ChatGPT plan") : ChatGptAuth.Status == ChatGptStatus.PlanUsageUnavailable ? "Enable plan usage" : "Continue with ChatGPT";
            _connect.SetEnabled(!ChatService.IsBusy);
            _reopen.style.display = chatgpt && ChatGptAuth.IsBusy && !string.IsNullOrEmpty(ChatGptAuth.PendingAuthorizationUrl) ? DisplayStyle.Flex : DisplayStyle.None;
            _manageUsage.style.display = chatgpt && ChatGptAuth.HasTokens ? DisplayStyle.Flex : DisplayStyle.None;
            _planWelcome.style.display = chatgpt && ChatGptAuth.IsReadyForInference && !settings.ChatGptPlanWelcomeShown && !ChatService.IsBusy && ChatApproval.Pending == null ? DisplayStyle.Flex : DisplayStyle.None;
            _accountStatus.text = externalClaude ? (ClaudeSubscriptionHandoff.Status ?? "Claude subscription runs in the official Claude Code client. Use Account & settings to install its Unity tools, or choose Claude API for chat here.") : chatgpt
                ? (ChatGptAuth.IsBusy ? "Finish signing in in your browser. You can return to Unity while the browser is open."
                    : ChatGptAuth.IsReadyForInference ? ChatGptAuth.AccountLabel + " · Using ChatGPT plan"
                    : ChatGptAuth.LastError ?? "Connect your ChatGPT account to start chatting.")
                : ProviderFactory.DisplayName(ChatService.CurrentProviderId) + " · " + (ChatService.ConnectionMessage ?? "Connect in Account & settings") + "\nYou can also connect your ChatGPT Plus or Pro subscription.";
            _mode.SetValueWithoutNotify(settings.ChatMode == "chat" ? "Chat" : settings.ChatMode == "plan" ? "Plan" : "Agent");
            _mode.SetEnabled(!externalClaude);
            _effort.SetEnabled(!externalClaude);
            _effort.choices = ChatService.CurrentProviderId == BridgeSettings.ProviderGemini ? new List<string> { "Default", "Summaries" }
                : ChatService.CurrentProviderId == BridgeSettings.ProviderClaude ? new List<string> { "Off", "Low", "Medium", "High" }
                : IsOpenAi() ? new List<string> { "Default", "Low", "Medium", "High" } : new List<string> { "Off", "Low", "High", "Max" };
            string effort = ChatService.CurrentProviderId == BridgeSettings.ProviderGemini ? (settings.GeminiThinking ? "summaries" : "default")
                : ChatService.CurrentProviderId == BridgeSettings.ProviderClaude ? (settings.ClaudeThinking ? settings.ClaudeEffort : "off")
                : IsOpenAi() ? settings.OpenAiReasoningEffort : settings.DeepSeekThinking ? settings.DeepSeekReasoningEffort : "off";
            string label = string.IsNullOrEmpty(effort) ? _effort.choices[0] : char.ToUpperInvariant(effort[0]) + effort.Substring(1);
            _effort.SetValueWithoutNotify(_effort.choices.Contains(label) ? label : _effort.choices[0]);
        }

        internal bool ActivityExpanded(string id)
        {
            bool value;
            return !string.IsNullOrEmpty(id) && _activityExpanded.TryGetValue(id, out value) && value;
        }

        internal void SetActivityExpanded(string id, bool value)
        {
            if (!string.IsNullOrEmpty(id)) _activityExpanded[id] = value;
        }

        internal bool ReasoningExpanded(string id)
        {
            bool value;
            return string.IsNullOrEmpty(id) || !_reasoningExpanded.TryGetValue(id, out value) || value;
        }

        internal void SetReasoningExpanded(string id, bool value)
        {
            if (!string.IsNullOrEmpty(id)) _reasoningExpanded[id] = value;
        }

        void UpdatePending()
        {
            int count = ChangeService.PendingCount;
            _pending.text = count + " pending";
            _pending.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void UpdateAttachments()
        {
            var files = ChatService.AttachedFiles;
            if (_attachments.childCount == files.Count) return;
            _attachments.Clear();
            foreach (var file in files)
            {
                string path = file;
                var chip = new Button(() => ChatService.RemoveAttachment(path)) { text = System.IO.Path.GetFileName(file) + "  ×" };
                chip.tooltip = file;
                chip.style.fontSize = 9;
                chip.style.marginRight = 4;
                chip.style.marginBottom = 4;
                _attachments.Add(chip);
            }
        }

        void AttachSelection()
        {
            var selected = Selection.activeObject;
            if (selected == null) return;
            string path = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            ChatService.AttachFile(path);
        }

        internal float TranscriptViewportHeight { get { return _transcript.contentViewport.resolvedStyle.height; } }

        internal void NotifyLayoutChanged(string messageId = null)
        {
            _follow = false;
            UpdateJump();
        }

        internal void RequestFile(ChatFileRef file)
        {
            if (FileRequested != null) FileRequested(file);
        }
    }
}
