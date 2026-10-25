using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    internal sealed class ChatPanel : VisualElement
    {
        readonly ListView _list;
        readonly TextField _input;
        readonly Button _send;
        readonly Button _stop;
        readonly Button _attach;
        readonly Button _pending;
        readonly Button _jump;
        readonly Button _older;
        readonly Label _empty;
        readonly VisualElement _attachments;
        readonly Label _hint;

        bool _dirty = true;
        bool _follow = true;
        bool _detached;
        int _lastCount = -1;
        string _lastId = string.Empty;
        string _lastSignature = string.Empty;
        string _lastActivityId = string.Empty;
        string _lastActivitySignature = string.Empty;

        public event Action PendingRequested;
        public event Action<ChatFileRef> FileRequested;

        public ChatPanel()
        {
            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Column;
            style.paddingLeft = 10;
            style.paddingRight = 10;
            style.paddingTop = 8;
            style.paddingBottom = 8;

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

            _empty = new Label("Start a conversation with Unity AI Bridge.\nCtrl+Enter sends a message; use Attach selection to add project files.");
            _empty.style.whiteSpace = WhiteSpace.Normal;
            _empty.style.fontSize = 12;
            _empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _empty.style.marginTop = 22;
            _empty.style.marginBottom = 22;
            _empty.style.color = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.4f, 0.4f, 0.4f);
            _empty.style.display = DisplayStyle.None;
            Add(_empty);

            _list = new ListView
            {
                style = { flexGrow = 1 },
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                selectionType = SelectionType.None,
                makeItem = () => new ChatMessageView(this),
                bindItem = (element, index) =>
                {
                    var view = element as ChatMessageView;
                    var messages = ChatService.Messages;
                    if (view != null && index >= 0 && index < messages.Count) view.Bind(messages[index]);
                }
            };
            _list.itemsSource = ChatService.Messages;
            _list.style.minHeight = 120;
            _list.RegisterCallback<WheelEvent>(evt =>
            {
                if (evt.delta.y > 0.01f)
                {
                    _follow = false;
                    UpdateJump();
                }
            });
            Add(_list);

            _attachments = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            Add(_attachments);

            var composer = new VisualElement();
            composer.style.marginTop = 6;
            composer.style.flexShrink = 0;
            composer.style.borderTopWidth = 1;
            composer.style.borderTopColor = EditorGUIUtility.isProSkin ? new Color(0.28f, 0.28f, 0.28f) : new Color(0.82f, 0.82f, 0.82f);
            composer.style.paddingTop = 6;
            Add(composer);

            _input = new TextField { multiline = true };
            _input.textEdition.placeholder = "Message Unity AI Bridge…";
            _input.style.height = 58;
            _input.style.minHeight = 58;
            _input.style.maxHeight = 58;
            _input.style.flexShrink = 0;
            _input.style.whiteSpace = WhiteSpace.Normal;
            _input.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return && (evt.ctrlKey || evt.commandKey))
                {
                    evt.StopPropagation();
                    SendCurrent();
                }
            });
            _input.RegisterValueChangedCallback(_ => UpdateComposer());
            composer.Add(_input);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4, alignItems = Align.Center, flexShrink = 0 } };
            _attach = new Button(AttachSelection) { text = "Attach selection" };
            _attach.style.fontSize = 10;
            row.Add(_attach);

            _pending = new Button(() => { if (PendingRequested != null) PendingRequested(); }) { text = "0 pending" };
            _pending.style.fontSize = 10;
            _pending.style.marginLeft = 6;
            _pending.style.display = DisplayStyle.None;
            row.Add(_pending);

            var flexible = new VisualElement(); flexible.style.flexGrow = 1; row.Add(flexible);

            _hint = new Label("Ctrl+Enter to send");
            _hint.style.fontSize = 9;
            _hint.style.marginRight = 8;
            _hint.style.color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.45f, 0.45f, 0.45f);
            row.Add(_hint);

            _stop = new Button(() => ChatService.Stop()) { text = "Stop" };
            _stop.style.fontSize = 10;
            _stop.style.marginRight = 6;
            _stop.style.display = DisplayStyle.None;
            row.Add(_stop);

            _send = new Button(SendCurrent) { text = "Send" };
            _send.style.fontSize = 10;
            _send.style.minWidth = 72;
            row.Add(_send);
            composer.Add(row);

            ChatService.Changed += OnServiceChanged;
            schedule.Execute(Tick).Every(80);
            RefreshAll();
        }

        public void Detach()
        {
            if (_detached) return;
            _detached = true;
            ChatService.Changed -= OnServiceChanged;
        }

        void OnServiceChanged()
        {
            _dirty = true;
        }

        public void RefreshAll()
        {
            _lastCount = -1;
            _lastId = string.Empty;
            _lastSignature = string.Empty;
            _lastActivityId = string.Empty;
            _lastActivitySignature = string.Empty;
            _follow = true;
            _dirty = true;
            Tick();
            schedule.Execute(ScrollToLatest).StartingIn(120);
            schedule.Execute(ScrollToLatest).StartingIn(320);
        }

        void Tick()
        {
            if (_detached) return;
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
            var messages = ChatService.Messages;
            int count = messages.Count;
            string id = count > 0 ? messages[count - 1].Id : string.Empty;
            string signature = Signature(messages);

            int activityIndex = LastActivityIndex(messages);
            string activityId = activityIndex >= 0 ? messages[activityIndex].Id : string.Empty;
            string activitySignature = ActivitySignature(messages, activityIndex);

            if (count != _lastCount || id != _lastId)
            {
                _lastCount = count;
                _lastId = id;
                _lastSignature = signature;
                _lastActivityId = activityId;
                _lastActivitySignature = activitySignature;
                _list.itemsSource = messages;
                _list.Rebuild();
                if (_follow)
                {
                    schedule.Execute(ScrollToLatest).StartingIn(30);
                    schedule.Execute(ScrollToLatest).StartingIn(180);
                }
                return;
            }

            bool activityChanged = activityIndex >= 0 &&
                                   (activityId != _lastActivityId || activitySignature != _lastActivitySignature);
            if (signature == _lastSignature && !activityChanged) return;
            _lastSignature = signature;
            _lastActivityId = activityId;
            _lastActivitySignature = activitySignature;
            if (activityChanged) _list.RefreshItem(activityIndex);
            if (count > 0)
            {
                _list.RefreshItem(count - 1);
                if (_follow) schedule.Execute(ScrollToLatest).StartingIn(20);
            }
            UpdateJump();
        }

        static int LastActivityIndex(List<ChatMessage> messages)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
                if (messages[i] != null && messages[i].Role == "activity") return i;
            return -1;
        }

        static string ActivitySignature(List<ChatMessage> messages, int index)
        {
            if (index < 0 || index >= messages.Count) return string.Empty;
            var message = messages[index];
            if (message == null) return string.Empty;
            var activities = message.Activities;
            if (activities == null) return "0";
            var builder = new System.Text.StringBuilder();
            builder.Append(activities.Count);
            foreach (var activity in activities)
            {
                if (activity == null) continue;
                builder.Append('|').Append(activity.CallId).Append(':').Append(activity.Status).Append(':').Append(activity.Ok);
                builder.Append(':').Append(activity.Detail == null ? 0 : activity.Detail.Length);
            }
            builder.Append('|').Append(message.Files == null ? 0 : message.Files.Count);
            return builder.ToString();
        }

        static string Signature(List<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0) return "0";
            var last = messages[messages.Count - 1];
            if (last == null) return "null";
            int activity = last.Activities != null ? last.Activities.Count : 0;
            string activityState = string.Empty;
            if (last.Activities != null && last.Activities.Count > 0)
            {
                var a = last.Activities[last.Activities.Count - 1];
                if (a != null) activityState = a.Status + ":" + (a.Detail == null ? 0 : a.Detail.Length);
            }
            return messages.Count + "|" + last.Id + "|" +
                   (last.Content == null ? 0 : last.Content.Length) + "|" +
                   (last.Reasoning == null ? 0 : last.Reasoning.Length) + "|" +
                   (last.IsStreaming ? "1" : "0") + "|" + activity + "|" + activityState + "|" +
                   (last.Files == null ? 0 : last.Files.Count);
        }

        void SendCurrent()
        {
            string text = _input.value;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text.Trim()) || ChatService.IsBusy) return;
            _input.SetValueWithoutNotify(string.Empty);
            _follow = true;
            ChatService.Send(text);
            _input.Focus();
            UpdateComposer();
        }

        void LoadOlder()
        {
            if (ChatService.LoadOlderMessages() > 0)
            {
                _list.Rebuild();
                _follow = false;
                UpdateJump();
            }
        }

        void ScrollToLatest()
        {
            _follow = true;
            UpdateJump();
            int count = ChatService.Messages.Count;
            if (count <= 0) return;
            _list.ScrollToItem(count - 1);
            var scroll = _list.Q<ScrollView>();
            if (scroll != null && scroll.verticalScroller != null)
                scroll.verticalScroller.value = scroll.verticalScroller.highValue;
        }

        void UpdateJump()
        {
            _jump.style.display = _follow ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void UpdateComposer()
        {
            bool busy = ChatService.IsBusy;
            _send.SetEnabled(!busy && !string.IsNullOrWhiteSpace(_input.value));
            _stop.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            _attach.SetEnabled(!busy);
            _hint.text = busy ? "Generating…" : "Ctrl+Enter to send";
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

        internal void NotifyLayoutChanged()
        {
            schedule.Execute(() => _list.RefreshItems()).StartingIn(16);
        }

        internal void RequestFile(ChatFileRef file)
        {
            if (FileRequested != null) FileRequested(file);
        }
    }
}
