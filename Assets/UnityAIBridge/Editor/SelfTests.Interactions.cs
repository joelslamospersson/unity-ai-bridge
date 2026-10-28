using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static EditorWindow _interactionWindow;
        static ChatPanel _interactionChat;
        static Task<ChatApprovalPrompt> _interactionDecision;
        static int _interactionPhase;
        static double _interactionDeadline;
        static double _interactionNotBefore;

        // Run in a disposable project with a real display. Pointer events traverse
        // the rendered panel; success requires the production button callbacks.
        public static void RunInteractionValidation()
        {
            ChatService.SuspendBackgroundWork();
            ChatService.EnsureLoaded();
            ChatService.SetConversationAutoAccept(false);
            ChatApproval.BeginRun();
            ChatApproval.CancelForStop();
            ChatService.Messages.Clear();
            ChatService.Messages.Add(new ChatMessage { Id = "interaction-output", Role = "assistant", Content = "Working on the test request.", IsStreaming = true });
            SetInteractionBusy(true);
            ChatService.Activity.Begin();
            ChatService.Activity.Observe(StreamEvent.Reasoning("Visible reasoning"));
            _interactionWindow = ScriptableObject.CreateInstance<ChatLayoutValidationWindow>();
            _interactionWindow.titleContent = new GUIContent("Unity AI Bridge interaction test");
            _interactionWindow.position = new Rect(100, 100, 840, 800);
            _interactionWindow.ShowUtility();
            _interactionWindow.titleContent = new GUIContent("Unity AI Bridge interaction test");
            _interactionWindow.Focus();
            _interactionWindow.rootVisualElement.style.flexDirection = FlexDirection.Column;
            _interactionWindow.rootVisualElement.RegisterCallback<PointerDownEvent>(evt =>
                Debug.Log("[Unity AI Bridge] Interaction pointer at " + evt.position + " target=" + (evt.target as VisualElement)?.name), TrickleDown.TrickleDown);
            _interactionChat = new ChatPanel();
            _interactionWindow.rootVisualElement.Add(_interactionChat);
            _interactionPhase = 0;
            _interactionDeadline = EditorApplication.timeSinceStartup + 60;
            _interactionNotBefore = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update += CheckInteractions;
        }

        static void SetInteractionBusy(bool busy)
        {
            typeof(ChatService).GetProperty("IsBusy", BindingFlags.Public | BindingFlags.Static).SetValue(null, busy);
            ChatService.Raise();
        }

        static void PublishMouseTarget(string id, VisualElement target)
        {
            var root = _interactionWindow.rootVisualElement;
            RequireStream(target != null && target.enabledInHierarchy && target.worldBound.width > 5 && target.worldBound.height > 5, "Mouse target unavailable: " + id);
            RequireStream(root.worldBound.Contains(target.worldBound.center), "Mouse target is outside the window: " + id);
            Vector2 center = target.worldBound.center;
            var picked = root.panel.Pick(center);
            RequireStream(picked == target || (picked != null && target.Contains(picked)), "Mouse target is covered: " + id);
            root.schedule.Execute(() =>
            {
                var press = new Event { type = EventType.MouseDown, button = 0, mousePosition = center, clickCount = 1 };
                using (var down = PointerDownEvent.GetPooled(press)) target.SendEvent(down);
                var release = new Event { type = EventType.MouseUp, button = 0, mousePosition = center, clickCount = 1 };
                using (var up = PointerUpEvent.GetPooled(release)) target.SendEvent(up);
                Debug.Log("[Unity AI Bridge] Dispatched rendered pointer click: " + id);
            }).StartingIn(100);
        }

        static void InteractionReview(bool destructive = false)
        {
            _interactionDecision = ChatApproval.RequestAsync(new ChatApprovalPrompt
            {
                Kind = "changes", Title = "Review the test operation", Detail = "Only disposable test data is involved.", Destructive = destructive
            }, CancellationToken.None);
        }

        static void CheckInteractions()
        {
            if (_interactionWindow == null) return;
            if (EditorApplication.timeSinceStartup < _interactionNotBefore) return;
            try
            {
                var root = _interactionWindow.rootVisualElement;
                var phase = root.Q<Label>("chat-activity-phase");
                var indicator = root.Q<RunActivityView>("chat-activity-indicator");
                var composer = root.Q<TextField>("chat-composer");
                switch (_interactionPhase)
                {
                    case 0:
                        if (phase == null || !phase.text.Contains("Thinking") || phase.worldBound.height < 5) break;
                        RequireStream(root.worldBound.Contains(indicator.worldBound.center), "Activity indicator is clipped. root=" + root.worldBound + " indicator=" + indicator.worldBound);
                        ChatService.Activity.Observe(new StreamEvent { Kind = StreamEventKind.ToolExecutionStarted, ToolCall = new ToolCall { Name = "read_project_file" } });
                        _interactionPhase = 1;
                        break;
                    case 1:
                        if (!phase.text.Contains("Reading project")) break;
                        ChatService.Activity.Observe(new StreamEvent { Kind = StreamEventKind.ToolExecutionStarted, ToolCall = new ToolCall { Name = "create_animation_clip" } });
                        _interactionPhase = 2;
                        break;
                    case 2:
                        if (!phase.text.Contains("Editing project")) break;
                        ChatService.Activity.Observe(StreamEvent.Delta("Answer"));
                        _interactionPhase = 3;
                        break;
                    case 3:
                        if (!phase.text.Contains("Writing response")) break;
                        PublishMouseTarget("stop", root.Q<Button>("chat-send-stop"));
                        _interactionPhase = 4;
                        break;
                    case 4:
                        if (!phase.text.Contains("Stopping")) break;
                        SetInteractionBusy(false);
                        _interactionPhase = 5;
                        break;
                    case 5:
                        if (indicator.resolvedStyle.display != DisplayStyle.None) break;
                        SetInteractionBusy(true);
                        ChatService.Activity.Begin();
                        _interactionWindow.position = new Rect(100, 100, 680, 620);
                        InteractionReview(true);
                        _interactionPhase = 6;
                        break;
                    case 6:
                        if (!phase.text.Contains("Waiting for your approval")) break;
                        var confirm = root.Q<Toggle>("approval-destructive-confirmation");
                        var accept = root.Q<Button>("approval-accept");
                        if (accept.worldBound.height < 5 || confirm.worldBound.height < 5) break;
                        RequireStream(!accept.enabledInHierarchy, "Destructive approval is enabled without confirmation.");
                        RequireStream(composer.worldBound.yMax <= root.worldBound.yMax + 1, "Composer is clipped at 680×620.");
                        PublishMouseTarget("confirm", confirm);
                        _interactionPhase = 7;
                        break;
                    case 7:
                        if (!root.Q<Toggle>("approval-destructive-confirmation").value) break;
                        PublishMouseTarget("accept", root.Q<Button>("approval-accept"));
                        _interactionPhase = 8;
                        break;
                    case 8:
                        if (!_interactionDecision.IsCompleted) break;
                        RequireStream(_interactionDecision.Result.Resolution == "Accept" && _interactionDecision.Result.ConfirmedDestructive, "Real Accept click did not record the review.");
                        InteractionReview();
                        _interactionPhase = 9;
                        break;
                    case 9:
                        if (root.Q<Button>("approval-deny").worldBound.height < 5 || ChatApproval.Pending == null) break;
                        PublishMouseTarget("deny", root.Q<Button>("approval-deny"));
                        _interactionPhase = 10;
                        break;
                    case 10:
                        if (!_interactionDecision.IsCompleted) break;
                        RequireStream(_interactionDecision.Result.Resolution == "Deny", "Real Deny click did not record the review.");
                        InteractionReview();
                        _interactionPhase = 11;
                        break;
                    case 11:
                        if (ChatApproval.Pending == null || root.Q<TextField>("approval-comment").worldBound.height < 5) break;
                        root.Q<TextField>("approval-comment").value = "Please revise the animation.";
                        PublishMouseTarget("comment", root.Q<Button>("approval-send-comment"));
                        _interactionPhase = 12;
                        break;
                    case 12:
                        if (!_interactionDecision.IsCompleted) break;
                        RequireStream(_interactionDecision.Result.Resolution == "Comment" && _interactionDecision.Result.Comment == "Please revise the animation.", "Real Comment click did not record the comment.");
                        FinishInteractions(true, "Rendered phases, Stop, idle visibility, 680×620 composer, destructive confirmation, Accept, Deny and Send comment passed through UI Toolkit pointer dispatch.");
                        return;
                }
                if (EditorApplication.timeSinceStartup > _interactionDeadline) throw new Exception("Interaction validation timed out at phase " + _interactionPhase);
            }
            catch (Exception e) { FinishInteractions(false, "Phase " + _interactionPhase + ": " + e); }
        }

        static void FinishInteractions(bool ok, string detail)
        {
            EditorApplication.update -= CheckInteractions;
            ChatApproval.CancelForStop();
            SetInteractionBusy(false);
            _interactionChat.Detach();
            File.WriteAllText(Path.Combine(BridgePaths.LibraryRoot, "interaction-result.json"), JsonValue.Obj("ok", ok, "detail", detail).ToJson(true));
            Debug.Log("[Unity AI Bridge] Interaction validation: " + ok + " " + detail);
            _interactionWindow.Close(); _interactionWindow = null;
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
