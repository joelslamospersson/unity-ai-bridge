using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static void FeatureCheck(string name, Action test)
        {
            try { test(); Check(name, true); }
            catch (Exception e) { Check(name, false, e.ToString()); }
        }

        static void RunActivityTests()
        {
            double now = 0;
            var tracker = new RunActivityTracker(() => now);
            tracker.Begin();
            FeatureCheck("activity_waiting_is_not_invented_thinking", () =>
            {
                now = 12;
                var state = tracker.Snapshot();
                RequireStream(state.Phase == "Waiting for provider" && state.ElapsedSeconds == 12 && state.SecondsSinceData == -1 && !state.MayBeStalled, "Initial connection state is misleading.");
            });
            FeatureCheck("activity_thinking_and_writing_follow_real_deltas", () =>
            {
                tracker.Observe(StreamEvent.Reasoning("reasoning"));
                RequireStream(tracker.Snapshot().Phase == "Thinking", "Reasoning phase missing.");
                tracker.Observe(StreamEvent.Delta("answer"));
                RequireStream(tracker.Snapshot().Phase == "Writing response", "Writing phase missing.");
            });
            FeatureCheck("activity_silent_provider_detected", () =>
            {
                now = 72;
                var state = tracker.Snapshot();
                RequireStream(state.MayBeStalled && state.SecondsSinceData == 60 && state.Health.Contains("Stop"), "Silent provider was not identified.");
            });
            FeatureCheck("activity_heartbeat_is_not_model_progress", () =>
            {
                tracker.Observe(new StreamEvent { Kind = StreamEventKind.TransportActivity });
                var state = tracker.Snapshot();
                RequireStream(!state.MayBeStalled && state.SecondsSinceData == 0 && state.SecondsSinceProgress == 60 && state.Health.Contains("last progress 60s"), "Heartbeat invented model progress.");
            });
            FeatureCheck("activity_read_and_edit_tools_are_distinct", () =>
            {
                tracker.Observe(new StreamEvent { Kind = StreamEventKind.ToolExecutionStarted, ToolCall = new ToolCall { Name = "read_project_file" } });
                RequireStream(tracker.Snapshot().Phase == "Reading project", "Read tool was not classified.");
                tracker.Observe(new StreamEvent { Kind = StreamEventKind.ToolExecutionStarted, ToolCall = new ToolCall { Name = "create_animation_clip" } });
                RequireStream(tracker.Snapshot().Phase == "Editing project", "Edit tool was not classified.");
                now = 200;
                RequireStream(!tracker.Snapshot().MayBeStalled, "Local tool execution was labelled a stalled provider.");
            });
            FeatureCheck("activity_approval_and_stop_do_not_look_stalled", () =>
            {
                tracker.Observe(StreamEvent.Status("Continuing"));
                now = 260;
                RequireStream(tracker.Snapshot().MayBeStalled, "Expected quiet provider.");
                var review = tracker.Snapshot(true);
                RequireStream(review.Phase == "Waiting for your approval" && !review.MayBeStalled, "Approval wait was labelled stalled.");
                tracker.Stopping();
                RequireStream(tracker.Snapshot().Phase == "Stopping" && !tracker.Snapshot().MayBeStalled, "Stop status was not preserved.");
            });
            FeatureCheck("activity_new_run_resets_ages", () =>
            {
                tracker.Begin();
                var state = tracker.Snapshot();
                RequireStream(state.ElapsedSeconds == 0 && state.SecondsSinceData == -1 && !state.MayBeStalled, "New run retained stale traffic.");
            });
            FeatureCheck("activity_view_shows_status_and_hides_when_idle", () =>
            {
                var view = new RunActivityView();
                now = 330;
                view.Show(tracker.Snapshot(), true);
                RequireStream(view.Q<Label>("chat-activity-phase").text.Contains("1:10") && view.Q<Label>("chat-activity-health").text.Contains("may be stalled"), "Visible status lacks duration or stale-connection notice.");
                view.Show(tracker.Snapshot(), false);
                RequireStream(view.style.display.value == DisplayStyle.None, "Indicator stayed visible when idle.");
            });
        }

        static IEnumerator RunActivityTransportTests()
        {
            var traffic = Task.Run(async () =>
            {
                int heartbeats = 0, output = 0;
                await StreamScenarioAsync(async (response, token) =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        await StreamWriteAsync(response, ": heartbeat\n\n");
                        await Task.Delay(100, token);
                    }
                }, (url, token) => StreamingHttp.PostStreamAsync(url, "{}", null, TimeSpan.FromSeconds(2), true,
                    data => output++, token, null, () => heartbeats++));
                RequireStream(heartbeats >= 3 && output == 0, "Real keepalive traffic was not reported independently of output.");
            });
            while (!traffic.IsCompleted) yield return null;
            Check("activity_transport_reports_real_heartbeats", traffic.Status == TaskStatus.RanToCompletion,
                traffic.Exception != null ? traffic.Exception.GetBaseException().Message : null);

            var recovery = Task.Run(async () =>
            {
                var provider = new TimeoutTestProvider { Respond = (n, r, emit) =>
                {
                    emit(new StreamEvent { Kind = StreamEventKind.TransportActivity });
                    if (n == 1) throw new ProviderTimeoutException(TimeSpan.FromSeconds(1), true);
                    return new ProviderTurn { Text = "Recovered" };
                } };
                var turn = await AgentRunner.StreamWithTimeoutRecoveryAsync(provider, new LlmRequest(), e => { }, CancellationToken.None);
                RequireStream(provider.Calls == 2 && turn.Text == "Recovered", "Transport keepalive disabled safe timeout recovery.");
            });
            while (!recovery.IsCompleted) yield return null;
            Check("activity_heartbeat_does_not_disable_safe_retry", recovery.Status == TaskStatus.RanToCompletion,
                recovery.Exception != null ? recovery.Exception.GetBaseException().Message : null);
        }
    }
}
