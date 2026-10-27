using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static EditorWindow _layoutWindow;
        static ChatPanel _layoutChat;
        static double _layoutDeadline;
        static int _layoutPhase;
        static Label _layoutStream;
        static float _initialStreamHeight;
        static ChatMessage _layoutAssistant;
        static ChatMessage _layoutActivity;
        static string LayoutLines(string prefix, int count)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++) text.AppendLine(prefix + " line " + i + ": this text must wrap and remain fully readable.");
            return text.ToString();
        }

        public static void RunChatLayoutValidation()
        {
            ChatService.SuspendBackgroundWork();
            _layoutWindow = ScriptableObject.CreateInstance<ChatLayoutValidationWindow>();
            _layoutWindow.position = new Rect(40, 40, 680, 620);
            _layoutWindow.ShowUtility();
            var root = _layoutWindow.rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;
            _layoutChat = new ChatPanel();
            _layoutChat.style.minHeight = 0;
            string summary = "";
            for (int i = 0; i < 160; i++) summary += "Thinking summary line " + i + ": this text must wrap and remain fully readable.\n";
            summary += "END OF THINKING SUMMARY";
            ChatService.Messages.Clear();
            _layoutAssistant = new ChatMessage { Id = "layout-summary", Role = "assistant", Content = "Streaming initial output", Reasoning = summary, IsStreaming = true };
            _layoutActivity = new ChatMessage { Id = "layout-tools", Role = "activity", Activities = new System.Collections.Generic.List<ChatToolActivity> {
                new ChatToolActivity { CallId = "layout-call", Tool = "read_project_file", Status = "Running", Detail = "Reading" }
            }};
            ChatService.Messages.Add(_layoutAssistant);
            // Deliberately put tool activity after the streaming assistant.
            ChatService.Messages.Add(_layoutActivity);
            _layoutChat.SetActivityExpanded(_layoutActivity.Id, true);
            _layoutChat.SetReasoningExpanded("layout-summary", true);
            root.Add(_layoutChat);
            _layoutChat.RefreshAll();
            ChatApproval.BeginRun();
            ChatApproval.CancelForStop();
            _layoutDeadline = EditorApplication.timeSinceStartup + 40;
            _layoutPhase = 0;
            EditorApplication.update += CheckChatLayout;
        }

        static void CheckChatLayout()
        {
            if (_layoutWindow == null) return;
            try
            {
                var root = _layoutWindow.rootVisualElement;
                var label = root.Q<Label>("thinking-summary-text");
                var scroll = root.Q<ScrollView>("thinking-summary-scroll");
                var accept = root.Q<Button>("approval-accept");
                var transcript = root.Q<ScrollView>("chat-transcript");
                var stream = root.Q<Label>("chat-stream-text");
                if (_layoutPhase == 0 && stream != null && stream.layout.height > 5 && transcript.contentViewport.layout.height > 80)
                {
                    _layoutStream = stream;
                    _initialStreamHeight = stream.layout.height;
                    _layoutAssistant.Content = LayoutLines("Streaming output", 90) + "END OF STREAMING OUTPUT";
                    _layoutAssistant.Reasoning += "\nNEW THINKING DURING STREAM";
                    _layoutActivity.Activities[0].Output = LayoutLines("Tool output", 70) + "END OF TOOL OUTPUT";
                    _layoutActivity.Activities[0].Status = "Done";
                    ChatService.Raise();
                    _layoutPhase = 1;
                    return;
                }
                if (_layoutPhase == 1 && stream != null && stream.text.Contains("END OF STREAMING OUTPUT") && stream.layout.height > _initialStreamHeight + 100)
                {
                    var output = root.Q<Label>("chat-tool-output");
                    if (!ReferenceEquals(stream, _layoutStream)) throw new Exception("Streaming updates replaced the live row.");
                    if (!_layoutAssistant.IsStreaming || ChatApproval.Pending != null) throw new Exception("Output was not visible during the active stream.");
                    if (stream.parent.worldBound.yMax < stream.worldBound.yMax - 1) throw new Exception("The streaming body clips its growing text.");
                    if (output == null || !output.text.EndsWith("END OF TOOL OUTPUT")) return;
                    if (label == null || !label.text.EndsWith("NEW THINKING DURING STREAM")) return;
                    if (output.parent.parent.parent.worldBound.yMax < output.worldBound.yMax - 1) throw new Exception("Tool activity clips its complete output.");
                    if (transcript.verticalScroller.highValue < 100) throw new Exception("Growing transcript is not scrollable.");
                    // Check the final tool output line at the real scroll limit.
                    transcript.verticalScroller.value = transcript.verticalScroller.highValue;
                    _layoutPhase = 2;
                    return;
                }
                if (_layoutPhase == 2)
                {
                    var output = root.Q<Label>("chat-tool-output");
                    if (output.worldBound.yMax > transcript.contentViewport.worldBound.yMax + 24) return;
                    _ = ChatApproval.RequestAsync(new ChatApprovalPrompt { Kind = "changes", Title = "Review these changes", Detail = "Example proposed change.\nAccept, Deny, and Comment must remain visible in this window." }, CancellationToken.None);
                    _layoutPhase = 3;
                    return;
                }
                if (_layoutPhase >= 3 && label != null && scroll != null && scroll.contentViewport.layout.height > 30 && scroll.verticalScroller.highValue > 0 && accept != null && accept.worldBound.height > 5)
                {
                    if (_layoutPhase == 3)
                    {
                        if (label.layout.height <= scroll.contentViewport.layout.height) throw new Exception("Long summary text was shrunk to the viewport.");
                        scroll.verticalScroller.value = scroll.verticalScroller.highValue;
                        _layoutPhase = 4;
                        return;
                    }
                    if (scroll.scrollOffset.y <= 0) throw new Exception("Summary cannot scroll to its end.");
                    if (label.worldBound.yMax > scroll.contentViewport.worldBound.yMax + 24) throw new Exception("The summary's final line is still clipped at the scroll limit.");
                    if (accept.worldBound.yMin < root.worldBound.yMin || accept.worldBound.yMax > root.worldBound.yMax) throw new Exception("Approval buttons are outside the window.");
                    var composer = root.Q<TextField>("chat-composer");
                    if (composer.worldBound.yMax > root.worldBound.yMax) throw new Exception("Composer is outside the window.");
                    FinishChatLayout(true, "Live output grows in place before completion or approval, including an assistant followed by tool rows. Full tool output and thinking scroll to their final lines; approval controls and composer remain inside the window.");
                    return;
                }
                if (EditorApplication.timeSinceStartup > _layoutDeadline)
                    FinishChatLayout(false, "Layout did not settle. Phase=" + _layoutPhase + " Text=" + (label == null ? "missing" : label.layout.ToString()) + " viewport=" + (scroll == null ? "missing" : scroll.contentViewport.layout.ToString()) + " scrollMax=" + (scroll == null ? "missing" : scroll.verticalScroller.highValue.ToString()));
            }
            catch (Exception e) { FinishChatLayout(false, e.ToString()); }
        }

        static void FinishChatLayout(bool ok, string detail)
        {
            EditorApplication.update -= CheckChatLayout;
            ChatApproval.CancelForStop();
            _layoutChat.Detach();
            File.WriteAllText(Path.Combine(BridgePaths.LibraryRoot, "chat-layout-result.json"), JsonValue.Obj("ok", ok, "detail", detail).ToJson(true));
            Debug.Log("[Unity AI Bridge] Chat layout validation: " + ok + " " + detail);
            _layoutWindow.Close(); _layoutWindow = null;
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }

    internal sealed class ChatLayoutValidationWindow : EditorWindow { }
}
