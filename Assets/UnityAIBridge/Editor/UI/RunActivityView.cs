using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    internal sealed class RunActivityView : VisualElement
    {
        readonly Label _dot, _phase, _health;

        public RunActivityView()
        {
            name = "chat-activity-indicator";
            style.flexShrink = 0;
            style.marginTop = 6;
            style.marginBottom = 6;
            style.paddingLeft = style.paddingRight = 8;
            style.paddingTop = style.paddingBottom = 5;
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            _dot = new Label("●") { name = "chat-activity-pulse", tooltip = "This dot animates locally. The last-data timer below reflects actual provider traffic." };
            _dot.style.width = 18;
            row.Add(_dot);
            _phase = new Label { name = "chat-activity-phase" };
            _phase.style.whiteSpace = WhiteSpace.Normal;
            _phase.style.flexShrink = 1;
            _phase.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(_phase);
            Add(row);
            _health = new Label { name = "chat-activity-health" };
            _health.style.fontSize = 11;
            _health.style.whiteSpace = WhiteSpace.Normal;
            _health.style.marginLeft = 18;
            Add(_health);
            style.display = DisplayStyle.None;
        }

        internal void Show(RunActivitySnapshot activity, bool busy)
        {
            style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            if (!busy || activity == null) return;
            string elapsed = TimeSpan.FromSeconds(Math.Floor(activity.ElapsedSeconds)).ToString(activity.ElapsedSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            _phase.text = activity.Phase + (string.IsNullOrEmpty(activity.Detail) ? "" : " · " + activity.Detail) + " · " + elapsed;
            _health.text = activity.Health;
            _dot.style.color = activity.MayBeStalled ? new Color(0.95f, 0.64f, 0.2f) : activity.AwaitingReview ? new Color(0.55f, 0.7f, 1f) : new Color(0.35f, 0.8f, 0.55f);
            _dot.style.opacity = activity.MayBeStalled || activity.AwaitingReview ? 1f : 0.45f + 0.55f * Mathf.PingPong((float)activity.ElapsedSeconds, 1f);
        }
    }
}
