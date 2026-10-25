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
        Foldout _reasoning;
        Foldout _activity;
        VisualElement _files;
        VisualElement _actions;
        bool _built;

        public ChatMessageView(ChatPanel owner)
        {
            _owner = owner;
            style.marginBottom = 10;
            style.flexDirection = FlexDirection.Column;
            _bubble = new VisualElement();
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

            if (message.IsStreaming)
            {
                UpdateStreaming(message);
                return;
            }

            if (_stream != null || _message == null || _body == null || _body.childCount == 0)
                Build(message);
            else
            {
                _header.text = HeaderText(message);
                if (message.IsError) ApplyRoleStyle(message);
            }
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

            ApplyRoleStyle(message);
            _header = new Label(HeaderText(message));
            _header.enableRichText = true;
            _header.style.fontSize = 10;
            _header.style.color = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f);
            _header.style.marginBottom = 4;
            _bubble.Add(_header);

            if (message.Role == "activity")
            {
                BuildActivity(message);
                _built = true;
                return;
            }

            bool showReasoning = BridgeSettings.Current.ShowReasoning && !string.IsNullOrEmpty(message.Reasoning);
            if (showReasoning)
            {
                _reasoning = new Foldout { text = "Reasoning", value = false };
                _reasoning.style.marginBottom = 5;
                var reasoning = new Label(message.Reasoning);
                reasoning.style.whiteSpace = WhiteSpace.Normal;
                reasoning.style.fontSize = 11;
                reasoning.style.color = EditorGUIUtility.isProSkin ? new Color(0.7f, 0.7f, 0.7f) : new Color(0.35f, 0.35f, 0.35f);
                reasoning.style.paddingLeft = 6;
                _reasoning.Add(reasoning);
                _bubble.Add(_reasoning);
            }

            _body = new VisualElement();
            _bubble.Add(_body);

            if (message.IsStreaming)
            {
                _stream = new Label(string.IsNullOrEmpty(message.Content) ? "Thinking…" : message.Content + "▋");
                _stream.style.whiteSpace = WhiteSpace.Normal;
                _stream.style.fontSize = 12;
                _stream.style.color = EditorGUIUtility.isProSkin ? new Color(0.86f, 0.86f, 0.86f) : new Color(0.12f, 0.12f, 0.12f);
                _body.Add(_stream);
            }
            else if (message.IsCompactionNotice)
            {
                var note = new Label(message.Content);
                note.style.whiteSpace = WhiteSpace.Normal;
                note.style.fontSize = 11;
                note.style.unityFontStyleAndWeight = FontStyle.Italic;
                note.style.color = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.62f, 0.7f) : new Color(0.35f, 0.42f, 0.5f);
                _body.Add(note);
            }
            else
            {
                MarkdownView.Render(_body, string.IsNullOrEmpty(message.Content) ? "…" : message.Content);
            }

            BuildFiles(message);
            BuildActions(message);
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
                _reasoning = new Foldout { text = "Reasoning", value = false };
                _reasoning.style.marginBottom = 5;
                var label = new Label(message.Reasoning);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = 11;
                label.style.color = EditorGUIUtility.isProSkin ? new Color(0.7f, 0.7f, 0.7f) : new Color(0.35f, 0.35f, 0.35f);
                label.style.paddingLeft = 6;
                _reasoning.Add(label);
                int bodyIndex = _bubble.IndexOf(_body);
                if (bodyIndex >= 0) _bubble.Insert(bodyIndex, _reasoning); else _bubble.Add(_reasoning);
            }
            if (_reasoning != null && !string.IsNullOrEmpty(message.Reasoning))
            {
                var label = _reasoning.contentContainer.Q<Label>();
                if (label != null) label.text = message.Reasoning;
            }
        }

        void BuildActivity(ChatMessage message)
        {
            var activities = message.Activities ?? new System.Collections.Generic.List<ChatToolActivity>();
            int failures = 0;
            foreach (var activity in activities) if (activity != null && !activity.Ok) failures++;
            string summary = activities.Count + " tool call" + (activities.Count == 1 ? "" : "s") + (failures > 0 ? " · " + failures + " failed" : "");
            _activity = new Foldout { text = summary, value = false };
            _activity.style.marginBottom = 4;
            _activity.RegisterValueChangedCallback(_ => _owner.NotifyLayoutChanged());
            foreach (var activity in activities)
            {
                if (activity == null) continue;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 3 } };
                var dot = new Label("●");
                dot.style.width = 14;
                dot.style.fontSize = 9;
                dot.style.marginTop = 3;
                dot.style.color = activity.Ok
                    ? new Color(0.35f, 0.68f, 0.4f)
                    : new Color(0.85f, 0.35f, 0.3f);
                row.Add(dot);
                var text = new Label(activity.Tool + "  ·  " + activity.Status + (string.IsNullOrEmpty(activity.Detail) ? "" : "\n" + Trim(activity.Detail)));
                text.style.whiteSpace = WhiteSpace.Normal;
                text.style.fontSize = 11;
                text.style.flexGrow = 1;
                text.style.color = EditorGUIUtility.isProSkin ? new Color(0.74f, 0.74f, 0.74f) : new Color(0.3f, 0.3f, 0.3f);
                row.Add(text);
                _activity.Add(row);
            }
            _bubble.Add(_activity);
            BuildFiles(message);
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
            _actions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 5 } };
            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = message.Content) { text = "Copy" };
            copy.style.fontSize = 10;
            copy.style.marginRight = 6;
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
            _bubble.style.backgroundColor = Color.clear;
            _bubble.style.borderLeftWidth = _bubble.style.borderRightWidth = _bubble.style.borderTopWidth = _bubble.style.borderBottomWidth = 0;
            _bubble.style.marginLeft = 0;
            _bubble.style.marginRight = 0;
            _bubble.style.maxWidth = Length.Percent(100);

            if (message.Role == "user")
            {
                style.alignItems = Align.FlexEnd;
                _bubble.style.maxWidth = Length.Percent(82);
                _bubble.style.backgroundColor = pro ? new Color(0.16f, 0.28f, 0.4f) : new Color(0.85f, 0.91f, 0.97f);
                _bubble.style.borderTopLeftRadius = 10;
                _bubble.style.borderTopRightRadius = 10;
            }
            else if (message.Role == "assistant")
            {
                _bubble.style.backgroundColor = pro ? new Color(0.16f, 0.16f, 0.17f) : new Color(0.98f, 0.98f, 0.98f);
                _bubble.style.borderTopLeftRadius = 2;
                _bubble.style.borderTopRightRadius = 10;
                if (message.IsError)
                {
                    _bubble.style.borderLeftWidth = 2;
                    _bubble.style.borderLeftColor = new Color(0.8f, 0.3f, 0.3f);
                }
            }
            else if (message.Role == "activity")
            {
                _bubble.style.backgroundColor = pro ? new Color(0.14f, 0.14f, 0.15f) : new Color(0.95f, 0.95f, 0.95f);
                _bubble.style.borderLeftWidth = 2;
                _bubble.style.borderLeftColor = pro ? new Color(0.35f, 0.45f, 0.55f) : new Color(0.55f, 0.65f, 0.78f);
            }
            else if (message.Role == "error")
            {
                _bubble.style.backgroundColor = pro ? new Color(0.24f, 0.12f, 0.12f) : new Color(1f, 0.9f, 0.9f);
                _bubble.style.borderLeftWidth = 2;
                _bubble.style.borderLeftColor = new Color(0.85f, 0.35f, 0.3f);
            }
            else
            {
                _bubble.style.backgroundColor = pro ? new Color(0.17f, 0.17f, 0.18f) : new Color(0.94f, 0.94f, 0.94f);
                _bubble.style.borderTopLeftRadius = 4;
                _bubble.style.borderTopRightRadius = 4;
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

        static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 220 ? text.Substring(0, 220) + "…" : text;
        }
    }
}
