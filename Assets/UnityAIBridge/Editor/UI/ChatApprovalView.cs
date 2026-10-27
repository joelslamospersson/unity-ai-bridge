using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    internal sealed class ChatApprovalView : VisualElement
    {
        string _promptId;
        bool _requiresDestructive;
        readonly Label _title;
        readonly Label _scope;
        readonly Label _detail;
        readonly TextField _comment;
        readonly Toggle _confirm;
        readonly Button _accept;
        readonly Button _deny;
        readonly Button _sendComment;

        public ChatApprovalView()
        {
            name = "chat-approval";
            style.display = DisplayStyle.None;
            style.flexShrink = 0;
            style.paddingLeft = style.paddingRight = 10;
            style.paddingTop = style.paddingBottom = 8;
            style.marginTop = 6;
            style.borderLeftWidth = 3;
            style.borderLeftColor = new Color(0.3f, 0.6f, 0.9f);
            style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.17f, 0.2f, 0.24f) : new Color(0.88f, 0.94f, 1f);
            _title = new Label(); _title.style.unityFontStyleAndWeight = FontStyle.Bold; Add(_title);
            _scope = new Label(); _scope.style.whiteSpace = WhiteSpace.Normal; _scope.style.fontSize = 11; Add(_scope);
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "approval-details-scroll" };
            scroll.style.height = 110; scroll.style.flexShrink = 0;
            scroll.contentContainer.style.width = Length.Percent(100);
            _detail = new Label { enableRichText = false, name = "approval-details" };
            _detail.style.whiteSpace = WhiteSpace.Normal;
            _detail.style.width = Length.Percent(100);
            _detail.style.flexShrink = 0;
            _detail.style.fontSize = 11;
            _detail.selection.isSelectable = true;
            scroll.Add(_detail); Add(scroll);
            _comment = new TextField { multiline = true, name = "approval-comment" };
            _comment.textEdition.placeholder = "Optional comment, or describe what should change…";
            _comment.style.height = 46; _comment.style.flexShrink = 0; Add(_comment);
            _confirm = new Toggle("I confirm the destructive changes listed above") { name = "approval-destructive-confirmation" };
            _confirm.RegisterValueChangedCallback(_ => UpdateButtons()); Add(_confirm);
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            _accept = new Button(() => Respond(ApprovalChoice.Accept)) { text = "Accept", name = "approval-accept" };
            _deny = new Button(() => Respond(ApprovalChoice.Deny)) { text = "Deny", name = "approval-deny" };
            _sendComment = new Button(() => Respond(ApprovalChoice.Comment)) { text = "Send comment", name = "approval-send-comment" };
            buttons.Add(_accept); buttons.Add(_deny); buttons.Add(_sendComment); Add(buttons);
            _comment.RegisterValueChangedCallback(_ => UpdateButtons());
        }

        internal void ShowPrompt(ChatApprovalPrompt prompt)
        {
            style.display = prompt != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (prompt == null) { _promptId = null; return; }
            if (_promptId == prompt.Id) return;
            _promptId = prompt.Id;
            _requiresDestructive = prompt.Destructive;
            _title.text = prompt.Title;
            _scope.text = prompt.Kind == "permission" ? "The agent is waiting. This grants permission to propose changes only."
                : "The agent is waiting. Accept applies only the changes below; Deny or Comment continues without granting permission.";
            _detail.text = prompt.Detail ?? string.Empty;
            _comment.SetValueWithoutNotify(string.Empty);
            _confirm.SetValueWithoutNotify(false);
            _confirm.style.display = prompt.Destructive ? DisplayStyle.Flex : DisplayStyle.None;
            _accept.text = prompt.Kind == "permission" ? "Accept" : "Accept & apply";
            UpdateButtons();
        }

        void UpdateButtons()
        {
            _accept.SetEnabled(!_requiresDestructive || _confirm.value);
            _sendComment.SetEnabled(!string.IsNullOrWhiteSpace(_comment.value));
        }

        void Respond(ApprovalChoice choice)
        {
            if (ChatApproval.Resolve(_promptId, choice, _comment.value, _confirm.value)) ShowPrompt(null);
        }
    }
}
