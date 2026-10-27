using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    internal sealed class ChatMessageView : VisualElement
    {
        readonly ChatPanel _owner;
        ChatMessage _message;
        VisualElement _bubble;
        Label _header;
        Label _stream;
        VisualElement _body;
        ThinkingSummaryView _reasoning;
        Foldout _activity;
        VisualElement _files;
        VisualElement _actions;
        bool _built;

        public ChatMessageView(ChatPanel owner)
        {
            _owner = owner;
            style.flexShrink = 0;
            style.minWidth = 0;
            style.marginBottom = 10;
            style.flexDirection = FlexDirection.Column;
            _bubble = new VisualElement();
            _bubble.style.flexShrink = 0;
            _bubble.style.minWidth = 0;
            _bubble.style.paddingLeft = 12;
            _bubble.style.paddingRight = 12;
            _bubble.style.paddingTop = 9;
            _bubble.style.paddingBottom = 9;
            _bubble.style.borderTopLeftRadius = 8;
            _bubble.style.borderTopRightRadius = 8;
            _bubble.style.borderBottomLeftRadius = 8;
            _bubble.style.borderBottomRightRadius = 8;
            Add(_bubble);
            _built = false;
        }

        public void Bind(ChatMessage message)
        {
            if (message == null) return;
            bool same = _message != null && _message.Id == message.Id;
            _message = message;

            if (!same || !_built)
            {
                Build(message);
                return;
            }

            if (message.Role == "activity")
            {
                UpdateActivity(message);
                return;
            }

            if (message.IsStreaming)
            {
                UpdateStreaming(message);
                return;
            }

            if (_stream != null)
            {
                Build(message);
                return;
            }

            if (_header != null) _header.text = HeaderText(message);
            if (message.IsError) ApplyRoleStyle(message);
        }

        void Build(ChatMessage message)
        {
            _bubble.Clear();
            _stream = null;
            _body = null;
            _reasoning = null;
            _activity = null;
            _files = null;
            _actions = null;

            _header = new Label(HeaderText(message));
            _header.enableRichText = true;
            _header.style.fontSize = 10;
            _header.style.color = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f);
            _header.style.marginBottom = 4;
            _bubble.Add(_header);
            ApplyRoleStyle(message);

            if (message.Role == "activity")
            {
                BuildActivity(message);
                _built = true;
                return;
            }

            bool showReasoning = BridgeSettings.Current.ShowReasoning && !string.IsNullOrEmpty(message.Reasoning);
            if (showReasoning)
            {
                _reasoning = new ThinkingSummaryView(_owner, message.Id, message.Reasoning, _owner.ReasoningExpanded(message.Id));
                _bubble.Add(_reasoning);
            }

            _body = new VisualElement();
            _body.style.flexShrink = 0;
            _body.style.minWidth = 0;
            _bubble.Add(_body);

            if (message.IsStreaming)
            {
                _stream = new Label(string.IsNullOrEmpty(message.Content) ? "Thinking…" : message.Content + "▋");
                _stream.name = "chat-stream-text";
                _stream.enableRichText = false;
                _stream.style.flexShrink = 0;
                _stream.style.whiteSpace = WhiteSpace.Normal;
                _stream.style.fontSize = 12;
                _stream.style.color = EditorGUIUtility.isProSkin ? new Color(0.86f, 0.86f, 0.86f) : new Color(0.12f, 0.12f, 0.12f);
                _stream.selection.isSelectable = true;
                _body.Add(_stream);
            }
            else if (message.IsCompactionNotice)
            {
                var note = new Label(message.Content);
                note.style.whiteSpace = WhiteSpace.Normal;
                note.style.fontSize = 11;
                note.style.unityTextAlign = TextAnchor.MiddleCenter;
                note.style.unityFontStyleAndWeight = FontStyle.Italic;
                note.style.color = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.62f, 0.7f) : new Color(0.35f, 0.42f, 0.5f);
                note.selection.isSelectable = true;
                _body.Add(note);
            }
            else
            {
                MarkdownView.Render(_body, string.IsNullOrEmpty(message.Content) ? "…" : message.Content);
            }

            BuildFiles(message);
            BuildActions(message);
            if (!string.IsNullOrEmpty(message.Content) && message.Role != "activity")
            {
                _bubble.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Copy message", _ => EditorGUIUtility.systemCopyBuffer = message.Content);
                }));
            }
            _built = true;
        }

        void UpdateStreaming(ChatMessage message)
        {
            if (_stream == null)
            {
                Build(message);
                return;
            }
            _stream.text = string.IsNullOrEmpty(message.Content) ? "Thinking…" : message.Content + "▋";
            _header.text = HeaderText(message);
            if (BridgeSettings.Current.ShowReasoning && !string.IsNullOrEmpty(message.Reasoning) && _reasoning == null && _body != null)
            {
                _reasoning = new ThinkingSummaryView(_owner, message.Id, message.Reasoning, _owner.ReasoningExpanded(message.Id));
                int bodyIndex = _bubble.IndexOf(_body);
                if (bodyIndex >= 0) _bubble.Insert(bodyIndex, _reasoning); else _bubble.Add(_reasoning);
            }
            if (_reasoning != null && !string.IsNullOrEmpty(message.Reasoning))
            {
                _reasoning.SetSummary(message.Reasoning);
            }
        }

        void BuildActivity(ChatMessage message)
        {
            _activity = new Foldout { name = "chat-tool-activity", text = ActivitySummary(message), value = _owner.ActivityExpanded(message.Id) };
            _activity.style.marginBottom = 0;
            _activity.style.flexShrink = 0;
            _activity.RegisterValueChangedCallback(evt =>
            {
                _owner.SetActivityExpanded(message.Id, evt.newValue);
                _owner.NotifyLayoutChanged(message.Id);
            });
            FillActivityRows(message);
            _bubble.Add(_activity);
            BuildFiles(message);
        }

        void UpdateActivity(ChatMessage message)
        {
            if (_activity == null)
            {
                Build(message);
                return;
            }

            _activity.text = ActivitySummary(message);
            FillActivityRows(message);
            if (_files != null)
            {
                _files.RemoveFromHierarchy();
                _files = null;
            }
            BuildFiles(message);
        }

        void FillActivityRows(ChatMessage message)
        {
            _activity.contentContainer.Clear();
            var activities = message.Activities ?? new System.Collections.Generic.List<ChatToolActivity>();
            bool pro = EditorGUIUtility.isProSkin;

            foreach (var activity in activities)
            {
                if (activity == null) continue;
                var row = new VisualElement();
                row.style.flexShrink = 0;
                row.style.flexDirection = FlexDirection.Row;
                row.style.marginBottom = 5;
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;

                var dot = new Label("●");
                dot.style.width = 14;
                dot.style.fontSize = 9;
                dot.style.marginTop = 4;
                dot.style.color = activity.Ok
                    ? new Color(0.35f, 0.72f, 0.45f)
                    : new Color(0.9f, 0.4f, 0.35f);
                row.Add(dot);

                var content = new VisualElement();
                content.style.flexGrow = 1;
                content.style.flexShrink = 1;
                content.style.minWidth = 0;
                row.Add(content);

                var title = new Label(activity.Tool + "  ·  " + activity.Status);
                title.style.fontSize = 11;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.whiteSpace = WhiteSpace.Normal;
                title.style.color = pro ? new Color(0.78f, 0.8f, 0.84f) : new Color(0.2f, 0.24f, 0.3f);
                title.selection.isSelectable = true;
                content.Add(title);

                string output = string.IsNullOrEmpty(activity.Output) ? activity.Detail : activity.Output;
                if (!string.IsNullOrEmpty(output))
                {
                    var detail = new Label(output) { name = "chat-tool-output", enableRichText = false };
                    detail.style.flexShrink = 0;
                    detail.style.fontSize = 10;
                    detail.style.whiteSpace = WhiteSpace.Normal;
                    detail.style.marginTop = 1;
                    detail.style.color = activity.Ok
                        ? (pro ? new Color(0.6f, 0.62f, 0.66f) : new Color(0.4f, 0.42f, 0.46f))
                        : (pro ? new Color(0.88f, 0.52f, 0.48f) : new Color(0.66f, 0.18f, 0.16f));
                    detail.selection.isSelectable = true;
                    content.Add(detail);
                }

                _activity.contentContainer.Add(row);
            }
        }

        static string ActivitySummary(ChatMessage message)
        {
            var activities = message.Activities ?? new System.Collections.Generic.List<ChatToolActivity>();
            int failures = 0;
            int running = 0;
            foreach (var activity in activities)
            {
                if (activity == null) continue;
                if (!activity.Ok) failures++;
                if (string.Equals(activity.Status, "Running", StringComparison.OrdinalIgnoreCase)) running++;
            }

            string summary = activities.Count + " tool call" + (activities.Count == 1 ? string.Empty : "s");
            if (running > 0) summary += " · running " + running;
            else if (failures > 0) summary += " · " + failures + " failed";
            else summary += " · done";
            return summary;
        }

        void BuildFiles(ChatMessage message)
        {
            if (message.Files == null || message.Files.Count == 0) return;
            _files = new VisualElement();
            _files.style.flexDirection = FlexDirection.Row;
            _files.style.flexWrap = Wrap.Wrap;
            _files.style.marginTop = 6;
            foreach (var file in message.Files)
            {
                if (file == null) continue;
                var button = new Button(() => _owner.RequestFile(file));
                string icon = file.IsRead ? "Read" : "Change";
                string status = file.Status;
                if (!string.IsNullOrEmpty(file.ChangeId))
                {
                    var change = ChangeService.Find(file.ChangeId);
                    if (change != null) status = change.Status.ToString();
                }
                button.text = icon + "  " + (string.IsNullOrEmpty(file.Title) ? file.Path : file.Title) + "  ·  " + status;
                button.tooltip = file.Path + (string.IsNullOrEmpty(file.ChangeId) ? "" : "\nChange: " + file.ChangeId);
                button.style.fontSize = 10;
                button.style.marginRight = 6;
                button.style.marginBottom = 4;
                button.style.paddingLeft = 8;
                button.style.paddingRight = 8;
                button.style.paddingTop = 4;
                button.style.paddingBottom = 4;
                button.style.borderTopLeftRadius = 4;
                button.style.borderTopRightRadius = 4;
                button.style.borderBottomLeftRadius = 4;
                button.style.borderBottomRightRadius = 4;
                bool pro = EditorGUIUtility.isProSkin;
                button.style.backgroundColor = StatusColor(file, pro);
                button.style.color = pro ? Color.white : Color.black;
                _files.Add(button);
            }
            _bubble.Add(_files);
        }

        static Color StatusColor(ChatFileRef file, bool pro)
        {
            if (!string.IsNullOrEmpty(file.Status) &&
                (file.Status.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 file.Status.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0))
                return pro ? new Color(0.45f, 0.22f, 0.22f) : new Color(0.95f, 0.8f, 0.8f);
            if (!string.IsNullOrEmpty(file.ChangeId))
                return pro ? new Color(0.22f, 0.32f, 0.42f) : new Color(0.82f, 0.9f, 0.98f);
            return pro ? new Color(0.24f, 0.24f, 0.26f) : new Color(0.9f, 0.9f, 0.9f);
        }

        void BuildActions(ChatMessage message)
        {
            if (message.IsStreaming || message.Role == "activity") return;
            if (string.IsNullOrEmpty(message.Content)) return;
            _actions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6, alignItems = Align.Center } };
            var copy = new Button { text = "Copy message" };
            copy.tooltip = "Copy this message as Markdown";
            copy.style.fontSize = 9;
            copy.style.marginRight = 8;
            copy.style.paddingLeft = 7;
            copy.style.paddingRight = 7;
            copy.style.paddingTop = 3;
            copy.style.paddingBottom = 3;
            copy.style.borderTopLeftRadius = 4;
            copy.style.borderTopRightRadius = 4;
            copy.style.borderBottomLeftRadius = 4;
            copy.style.borderBottomRightRadius = 4;
            copy.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.21f, 0.22f, 0.24f) : new Color(0.89f, 0.9f, 0.92f);
            copy.style.color = EditorGUIUtility.isProSkin ? new Color(0.7f, 0.74f, 0.8f) : new Color(0.3f, 0.34f, 0.4f);
            copy.clicked += () =>
            {
                EditorGUIUtility.systemCopyBuffer = message.Content;
                copy.text = "Copied";
                copy.schedule.Execute(() =>
                {
                    if (copy.panel != null) copy.text = "Copy message";
                }).StartingIn(1000);
            };
            _actions.Add(copy);
            if (!string.IsNullOrEmpty(message.Usage))
            {
                var usage = new Label(message.Usage);
                usage.style.fontSize = 9;
                usage.style.marginTop = 4;
                usage.style.color = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.45f, 0.45f, 0.45f);
                _actions.Add(usage);
            }
            _bubble.Add(_actions);
        }

        void ApplyRoleStyle(ChatMessage message)
        {
            bool pro = EditorGUIUtility.isProSkin;
            style.alignItems = Align.Stretch;
            style.width = Length.Percent(100);
            style.marginBottom = 12;
            _bubble.style.backgroundColor = Color.clear;
            _bubble.style.borderLeftWidth = _bubble.style.borderRightWidth = _bubble.style.borderTopWidth = _bubble.style.borderBottomWidth = 0;
            _bubble.style.marginLeft = 0;
            _bubble.style.marginRight = 0;
            _bubble.style.marginTop = 0;
            _bubble.style.marginBottom = 0;
            _bubble.style.paddingLeft = 0;
            _bubble.style.paddingRight = 0;
            _bubble.style.paddingTop = 0;
            _bubble.style.paddingBottom = 0;
            _bubble.style.maxWidth = Length.Percent(100);
            _bubble.style.flexShrink = 1;
            _header.style.display = DisplayStyle.Flex;
            _header.style.unityTextAlign = TextAnchor.MiddleLeft;

            if (message.Role == "user")
            {
                style.alignItems = Align.FlexEnd;
                style.marginBottom = 10;
                _header.style.display = DisplayStyle.None;
                _bubble.style.maxWidth = Length.Percent(78);
                _bubble.style.minWidth = 180;
                _bubble.style.paddingLeft = 14;
                _bubble.style.paddingRight = 14;
                _bubble.style.paddingTop = 10;
                _bubble.style.paddingBottom = 10;
                _bubble.style.backgroundColor = pro ? new Color(0.19f, 0.31f, 0.44f) : new Color(0.87f, 0.93f, 0.99f);
                _bubble.style.borderTopLeftRadius = 14;
                _bubble.style.borderTopRightRadius = 14;
                _bubble.style.borderBottomLeftRadius = 14;
                _bubble.style.borderBottomRightRadius = 5;
            }
            else if (message.Role == "assistant")
            {
                style.marginBottom = 16;
                _header.style.color = pro ? new Color(0.56f, 0.58f, 0.62f) : new Color(0.42f, 0.44f, 0.48f);
                _bubble.style.paddingTop = 2;
                _bubble.style.paddingBottom = 2;
                if (message.IsError)
                {
                    _bubble.style.paddingLeft = 10;
                    _bubble.style.paddingRight = 10;
                    _bubble.style.paddingTop = 8;
                    _bubble.style.paddingBottom = 8;
                    _bubble.style.backgroundColor = pro ? new Color(0.24f, 0.12f, 0.12f) : new Color(1f, 0.9f, 0.9f);
                    _bubble.style.borderLeftWidth = 3;
                    _bubble.style.borderLeftColor = new Color(0.85f, 0.35f, 0.3f);
                    _bubble.style.borderTopLeftRadius = 6;
                    _bubble.style.borderTopRightRadius = 6;
                    _bubble.style.borderBottomLeftRadius = 6;
                    _bubble.style.borderBottomRightRadius = 6;
                }
            }
            else if (message.Role == "activity")
            {
                style.marginBottom = 6;
                _header.style.display = DisplayStyle.None;
                _bubble.style.paddingLeft = 10;
                _bubble.style.paddingRight = 10;
                _bubble.style.paddingTop = 7;
                _bubble.style.paddingBottom = 7;
                _bubble.style.backgroundColor = pro ? new Color(0.145f, 0.15f, 0.16f) : new Color(0.955f, 0.96f, 0.97f);
                _bubble.style.borderTopWidth = 1;
                _bubble.style.borderBottomWidth = 1;
                _bubble.style.borderLeftWidth = 1;
                _bubble.style.borderRightWidth = 1;
                _bubble.style.borderTopColor = _bubble.style.borderBottomColor =
                    _bubble.style.borderLeftColor = _bubble.style.borderRightColor =
                    pro ? new Color(0.25f, 0.27f, 0.3f) : new Color(0.82f, 0.84f, 0.87f);
                _bubble.style.borderTopLeftRadius = 8;
                _bubble.style.borderTopRightRadius = 8;
                _bubble.style.borderBottomLeftRadius = 8;
                _bubble.style.borderBottomRightRadius = 8;
            }
            else if (message.Role == "error")
            {
                style.marginBottom = 12;
                _header.style.display = DisplayStyle.None;
                _bubble.style.paddingLeft = 10;
                _bubble.style.paddingRight = 10;
                _bubble.style.paddingTop = 8;
                _bubble.style.paddingBottom = 8;
                _bubble.style.backgroundColor = pro ? new Color(0.24f, 0.12f, 0.12f) : new Color(1f, 0.9f, 0.9f);
                _bubble.style.borderLeftWidth = 3;
                _bubble.style.borderLeftColor = new Color(0.85f, 0.35f, 0.3f);
                _bubble.style.borderTopLeftRadius = 6;
                _bubble.style.borderTopRightRadius = 6;
                _bubble.style.borderBottomLeftRadius = 6;
                _bubble.style.borderBottomRightRadius = 6;
            }
            else if (message.IsCompactionNotice)
            {
                style.alignItems = Align.Center;
                style.marginBottom = 10;
                _header.style.display = DisplayStyle.None;
                _bubble.style.maxWidth = Length.Percent(88);
                _bubble.style.paddingLeft = 10;
                _bubble.style.paddingRight = 10;
                _bubble.style.paddingTop = 4;
                _bubble.style.paddingBottom = 4;
                _bubble.style.backgroundColor = pro ? new Color(0.16f, 0.18f, 0.2f) : new Color(0.94f, 0.95f, 0.97f);
                _bubble.style.borderTopLeftRadius = 10;
                _bubble.style.borderTopRightRadius = 10;
                _bubble.style.borderBottomLeftRadius = 10;
                _bubble.style.borderBottomRightRadius = 10;
            }
            else
            {
                style.alignItems = Align.Center;
                style.marginBottom = 8;
                _header.style.display = DisplayStyle.None;
                _bubble.style.maxWidth = Length.Percent(90);
                _bubble.style.paddingLeft = 10;
                _bubble.style.paddingRight = 10;
                _bubble.style.paddingTop = 4;
                _bubble.style.paddingBottom = 4;
                _bubble.style.backgroundColor = pro ? new Color(0.16f, 0.16f, 0.17f) : new Color(0.94f, 0.94f, 0.95f);
                _bubble.style.borderTopLeftRadius = 8;
                _bubble.style.borderTopRightRadius = 8;
                _bubble.style.borderBottomLeftRadius = 8;
                _bubble.style.borderBottomRightRadius = 8;
            }
        }

        static string HeaderText(ChatMessage message)
        {
            string label;
            switch (message.Role)
            {
                case "user": label = "You"; break;
                case "assistant": label = string.IsNullOrEmpty(message.Model) ? "Assistant" : message.Model; break;
                case "activity": label = "Tool activity"; break;
                case "error": label = "Error"; break;
                case "tool": label = "Tool"; break;
                default: label = "System"; break;
            }
            var text = new StringBuilder(label);
            if (!string.IsNullOrEmpty(message.TimeLocal)) text.Append("  ·  ").Append(message.TimeLocal);
            if (message.IsStreaming) text.Append("  ·  streaming");
            return "<b>" + MarkdownView.Escape(text.ToString()) + "</b>";
        }

    }
}
