using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    // A bounded viewport gives long summaries their own scrollbar. The text itself
    // never shrinks to fit its viewport or receives a fixed text height.
    internal sealed class ThinkingSummaryView : Foldout
    {
        readonly ScrollView _scroll;
        readonly Label _text;
        readonly ChatPanel _owner;
        readonly string _messageId;

        public ThinkingSummaryView(ChatPanel owner, string messageId, string summary, bool expanded)
        {
            _owner = owner;
            _messageId = messageId;
            name = "thinking-summary";
            text = "Thinking summary";
            SetValueWithoutNotify(expanded);
            style.flexShrink = 0;
            style.minWidth = 0;
            style.marginBottom = 6;
            _scroll = new ScrollView(ScrollViewMode.Vertical) { name = "thinking-summary-scroll" };
            _scroll.style.flexShrink = 0;
            _scroll.style.minHeight = 60;
            _scroll.style.height = 180;
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _scroll.contentContainer.style.width = Length.Percent(100);
            _scroll.contentContainer.style.flexShrink = 0;
            _text = new Label { name = "thinking-summary-text", enableRichText = false };
            _text.style.whiteSpace = WhiteSpace.Normal;
            _text.style.width = Length.Percent(100);
            _text.style.minWidth = 0;
            _text.style.flexShrink = 0;
            _text.style.fontSize = 12;
            _text.style.paddingLeft = 6;
            _text.style.paddingRight = 8;
            _text.style.paddingTop = 4;
            _text.style.paddingBottom = 8;
            _text.selection.isSelectable = true;
            _scroll.Add(_text);
            Add(_scroll);
            _scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => ResizeViewport());
            this.RegisterValueChangedCallback(evt =>
            {
                owner.SetReasoningExpanded(messageId, evt.newValue);
                ResizeViewport();
                owner.NotifyLayoutChanged(messageId);
            });
            SetSummary(summary);
        }

        internal void SetSummary(string summary)
        {
            string content = summary ?? string.Empty;
            if (_text.text == content) return;
            bool follow = _scroll.verticalScroller.value >= _scroll.verticalScroller.highValue - 2;
            _text.text = content;
            ResizeViewport();
            if (follow) _scroll.schedule.Execute(() => _scroll.verticalScroller.value = _scroll.verticalScroller.highValue);
        }

        void ResizeViewport()
        {
            float width = _scroll.contentViewport.resolvedStyle.width;
            if (float.IsNaN(width) || width < 40) return;
            float measured = _text.MeasureTextSize(_text.text, Mathf.Max(20, width - 14), MeasureMode.Exactly, 0, MeasureMode.Undefined).y + 16;
            float available = _owner.TranscriptViewportHeight;
            float maximum = available > 100 ? Mathf.Clamp(available * 0.65f, 100, 360) : 280;
            float height = Mathf.Clamp(measured, 60, maximum);
            if (Mathf.Abs(_scroll.resolvedStyle.height - height) > 1) _scroll.style.height = height;
        }
    }
}
