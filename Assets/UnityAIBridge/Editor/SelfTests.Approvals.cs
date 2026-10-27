using System;
using System.Collections;
using System.Collections.Generic;
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
        static IEnumerator RunApprovalTests()
        {
            int savedAccess = BridgeSettings.Current.AccessLevel;
            foreach (var choice in new[] { ApprovalChoice.Accept, ApprovalChoice.Deny, ApprovalChoice.Comment })
            {
                ChatApproval.BeginRun();
                string path = TestFolder + "/approval-" + choice + ".txt";
                string unrelatedPath = TestFolder + "/unrelated-" + choice + ".txt";
                var unrelated = ChangeService.StageFile(ChangeKinds.CreateFile, unrelatedPath, "unrelated", "selftest");
                _testChangeIds.Add(unrelated.Id);
                var call = new ToolCall { Id = "approval_" + choice, Name = "propose_file_changes", Arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj("kind", "create", "path", path, "content", "reviewed"))).ToJson(false) };
                var provider = new ScriptedProvider();
                provider.Turns.Enqueue(new ProviderTurn { ToolCalls = new List<ToolCall> { call } });
                provider.Turns.Enqueue(new ProviderTurn { Text = "Continuing after the user's review." });
                var transcript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Create the requested file." } };
                var checkpoints = new List<bool>();
                var request = new LlmRequest { Model = "scripted", Tools = McpTools.GetToolSpecs(true), MaxSteps = 3,
                    ToolExecutor = (tool, token) => ChatApproval.ExecuteToolAsync(tool, token, keep => checkpoints.Add(keep)), ToolResultCommitted = ChatApproval.ToolResultCommitted };
                var task = Task.Run(async () => await AgentRunner.RunAsync(provider, request, transcript, e => { }, CancellationToken.None));
                float deadline = Time.realtimeSinceStartup + 10;
                while (ChatApproval.Pending == null && !task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                var prompt = ChatApproval.Pending;
                bool waiting = prompt != null && !task.IsCompleted && !File.Exists(BridgePaths.ToAbsolute(path));
                if (prompt != null)
                {
                    foreach (var id in prompt.ChangeIds) _testChangeIds.Add(id);
                    bool staleRejected = !ChatApproval.Resolve("stale-id", ApprovalChoice.Accept, "", false);
                    bool emptyCommentRejected = !ChatApproval.Resolve(prompt.Id, ApprovalChoice.Comment, "", false);
                    Check("approval_waits_for_matching_nonempty_decision_" + choice, waiting && staleRejected && emptyCommentRejected);
                    ChatApproval.Resolve(prompt.Id, choice, choice == ApprovalChoice.Comment ? "Use a different name." : "", false);
                }
                else Check("approval_waits_for_matching_nonempty_decision_" + choice, false, task.Exception == null ? "No prompt" : task.Exception.GetBaseException().Message);
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                string toolOutput = transcript.Count > 2 ? transcript[2].Content : string.Empty;
                bool correct = task.Status == TaskStatus.RanToCompletion && task.Result.Text == "Continuing after the user's review." &&
                    File.Exists(BridgePaths.ToAbsolute(path)) == (choice == ApprovalChoice.Accept) &&
                    toolOutput.Contains(choice == ApprovalChoice.Accept ? "Applied" : choice == ApprovalChoice.Deny ? "Denied" : "RevisionRequested") &&
                    (choice != ApprovalChoice.Comment || toolOutput.Contains("Use a different name.")) &&
                    unrelated.Status == ChangeStatus.Pending && !File.Exists(BridgePaths.ToAbsolute(unrelatedPath)) &&
                    ChatApproval.Pending == null && checkpoints.Count >= 2 && checkpoints[0] && !checkpoints[checkpoints.Count - 1];
                Check("approval_" + choice.ToString().ToLowerInvariant() + "_continues_same_agent_and_respects_scope", correct, task.Exception == null ? toolOutput : task.Exception.GetBaseException().Message);
                if (choice != ApprovalChoice.Accept)
                {
                    var repeat = Task.Run(async () => await ChatApproval.ExecuteToolAsync(call, CancellationToken.None));
                    while (!repeat.IsCompleted) yield return null;
                    Check("approval_does_not_repeat_denied_operation_" + choice, repeat.Status == TaskStatus.RanToCompletion && ChatApproval.Pending == null && repeat.Result["structuredContent"]["status"].AsString() == "Denied");
                }
                ChatApproval.CancelForStop();
            }

            ChatApproval.BeginRun();
            BridgeSettings.Current.AccessLevel = BridgeSettings.AccessReadOnly;
            string scopedPath = TestFolder + "/scoped-approval.txt";
            var scopedCall = new ToolCall { Id = "scoped", Name = "propose_file_changes", Arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj("kind", "create", "path", scopedPath, "content", "scoped"))).ToJson(false) };
            var scoped = Task.Run(async () => await ChatApproval.ExecuteToolAsync(scopedCall, CancellationToken.None));
            float scopedDeadline = Time.realtimeSinceStartup + 10;
            while (ChatApproval.Pending == null && !scoped.IsCompleted && Time.realtimeSinceStartup < scopedDeadline) yield return null;
            var permission = ChatApproval.Pending;
            bool permissionPrompt = permission != null && permission.Kind == "permission" && !File.Exists(BridgePaths.ToAbsolute(scopedPath));
            if (permission != null) ChatApproval.Resolve(permission.Id, ApprovalChoice.Accept, "", false);
            while ((ChatApproval.Pending == null || ChatApproval.Pending.Kind != "changes") && !scoped.IsCompleted && Time.realtimeSinceStartup < scopedDeadline) yield return null;
            var review = ChatApproval.Pending;
            bool restoredBeforeApply = BridgeSettings.Current.AccessLevel == BridgeSettings.AccessReadOnly && !File.Exists(BridgePaths.ToAbsolute(scopedPath));
            if (review != null)
            {
                foreach (var id in review.ChangeIds) _testChangeIds.Add(id);
                ChatApproval.Resolve(review.Id, ApprovalChoice.Accept, "", false);
            }
            while (!scoped.IsCompleted && Time.realtimeSinceStartup < scopedDeadline) yield return null;
            Check("read_only_grant_is_scoped_and_requires_actual_change_review", permissionPrompt && review != null && restoredBeforeApply && scoped.Status == TaskStatus.RanToCompletion &&
                scoped.Result["structuredContent"]["status"].AsString() == "Applied" && BridgeSettings.Current.AccessLevel == BridgeSettings.AccessReadOnly && File.Exists(BridgePaths.ToAbsolute(scopedPath)));
            ChatApproval.ToolResultCommitted();
            ChatApproval.CancelForStop();
            BridgeSettings.Current.AccessLevel = savedAccess;

            ChatApproval.BeginRun();
            string cancelledPath = TestFolder + "/cancelled-approval.txt";
            var cancellation = new CancellationTokenSource();
            var cancelled = Task.Run(async () => await ChatApproval.ExecuteToolAsync(new ToolCall { Id = "stop", Name = "propose_file_changes",
                Arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj("kind", "create", "path", cancelledPath, "content", "never applied"))).ToJson(false) }, cancellation.Token));
            float cancelDeadline = Time.realtimeSinceStartup + 10;
            while (ChatApproval.Pending == null && !cancelled.IsCompleted && Time.realtimeSinceStartup < cancelDeadline) yield return null;
            if (ChatApproval.Pending != null) foreach (var id in ChatApproval.Pending.ChangeIds) _testChangeIds.Add(id);
            cancellation.Cancel();
            while (!cancelled.IsCompleted && Time.realtimeSinceStartup < cancelDeadline) yield return null;
            Check("stop_cancels_approval_wait_without_applying", cancelled.IsCanceled && ChatApproval.Pending == null && !File.Exists(BridgePaths.ToAbsolute(cancelledPath)));
            cancellation.Dispose(); ChatApproval.CancelForStop();

            // Simulate destruction/recreation of static state during an assembly reload.
            ChatApproval.BeginRun();
            string durablePath = TestFolder + "/durable-approval.txt";
            var durabilityToken = new CancellationTokenSource();
            var originalWait = Task.Run(async () => await ChatApproval.ExecuteToolAsync(new ToolCall { Id = "durable", Name = "propose_file_changes",
                Arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj("kind", "create", "path", durablePath, "content", "survives"))).ToJson(false) }, durabilityToken.Token));
            float durableDeadline = Time.realtimeSinceStartup + 10;
            while (ChatApproval.Pending == null && !originalWait.IsCompleted && Time.realtimeSinceStartup < durableDeadline) yield return null;
            var durablePrompt = ChatApproval.Pending;
            if (durablePrompt != null) foreach (var id in durablePrompt.ChangeIds) _testChangeIds.Add(id);
            ChatApproval.PreserveForReload(); durabilityToken.Cancel();
            while (!originalWait.IsCompleted && Time.realtimeSinceStartup < durableDeadline) yield return null;
            typeof(ChatApproval).GetField("_journal", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            ChatApproval.LoadJournal();
            var restored = ChatApproval.Pending;
            var restoredWait = Task.Run(async () => await ChatApproval.ResumeReviewAsync(restored, CancellationToken.None, null));
            yield return null;
            bool restoredWithoutGrant = restored != null && durablePrompt != null && restored.Id == durablePrompt.Id && !File.Exists(BridgePaths.ToAbsolute(durablePath));
            if (restored != null) ChatApproval.Resolve(restored.Id, ApprovalChoice.Accept, "", false);
            while (!restoredWait.IsCompleted && Time.realtimeSinceStartup < durableDeadline) yield return null;
            Check("pending_approval_survives_reload_without_implicit_grant", restoredWithoutGrant && restoredWait.Status == TaskStatus.RanToCompletion && File.Exists(BridgePaths.ToAbsolute(durablePath)));
            ChatApproval.ToolResultCommitted(); ChatApproval.CancelForStop(); durabilityToken.Dispose();

            string deletePath = TestFolder + "/destructive-review.txt";
            var create = ChangeService.StageFile(ChangeKinds.CreateFile, deletePath, "delete only after explicit confirmation", "selftest");
            _testChangeIds.Add(create.Id); ChangeService.Approve(create.Id); ChangeService.Apply(create.Id);
            var deletion = ChangeService.StageFile(ChangeKinds.DeleteFile, deletePath, null, "selftest"); _testChangeIds.Add(deletion.Id);
            ChatApproval.BeginRun();
            var destructive = Task.Run(async () => await ChatApproval.ExecuteToolAsync(new ToolCall { Id = "destructive", Name = "request_approval", Arguments = JsonValue.Obj("ids", JsonValue.Arr(deletion.Id)).ToJson(false) }, CancellationToken.None));
            float destructiveDeadline = Time.realtimeSinceStartup + 10;
            while (ChatApproval.Pending == null && !destructive.IsCompleted && Time.realtimeSinceStartup < destructiveDeadline) yield return null;
            var destructivePrompt = ChatApproval.Pending;
            bool refused = destructivePrompt != null && destructivePrompt.Destructive && !ChatApproval.Resolve(destructivePrompt.Id, ApprovalChoice.Accept, "", false) && File.Exists(BridgePaths.ToAbsolute(deletePath));
            if (destructivePrompt != null) ChatApproval.Resolve(destructivePrompt.Id, ApprovalChoice.Accept, "", true);
            while (!destructive.IsCompleted && Time.realtimeSinceStartup < destructiveDeadline) yield return null;
            Check("destructive_review_requires_explicit_confirmation", refused && destructive.Status == TaskStatus.RanToCompletion && !File.Exists(BridgePaths.ToAbsolute(deletePath)));
            ChatApproval.ToolResultCommitted(); ChatApproval.CancelForStop();

            var panel = new ChatPanel();
            string summary = string.Join("\n", new string[200]).Replace("\n", "A long thinking summary line that must remain readable.\n");
            var message = new ChatMessage { Id = "long-summary", Role = "assistant", Content = "Final answer", Reasoning = summary };
            panel.SetReasoningExpanded(message.Id, true);
            var view = new ChatMessageView(panel); view.Bind(message);
            var summaryLabel = view.Q<Label>("thinking-summary-text");
            var summaryScroll = view.Q<ScrollView>("thinking-summary-scroll");
            Check("thinking_summary_uses_unshrunk_complete_scrollable_text", summaryLabel != null && summaryLabel.text == summary && summaryLabel.style.flexShrink.value == 0 && summaryScroll != null && summaryScroll.horizontalScrollerVisibility == ScrollerVisibility.Hidden);
            var approvalView = new ChatApprovalView();
            approvalView.ShowPrompt(new ChatApprovalPrompt { Title = "Review", Detail = "Full operation detail", Destructive = true });
            Check("approval_card_offers_accept_deny_comment_and_destructive_gate", approvalView.Q<Button>("approval-accept") != null && !approvalView.Q<Button>("approval-accept").enabledSelf &&
                approvalView.Q<Button>("approval-deny") != null && approvalView.Q<Button>("approval-send-comment") != null && approvalView.Q<TextField>("approval-comment") != null);
            panel.Detach();
        }
        static IEnumerator RunConversationAutoAcceptTests()
        {
            var metaField = typeof(ChatService).GetField("_meta", BindingFlags.Static | BindingFlags.NonPublic);
            var savedMeta = ChatService.CurrentConversation;
            int savedAccess = BridgeSettings.Current.AccessLevel;
            var meta = ChatHistory.NewConversation("selftest-auto-accept", "test");
            metaField.SetValue(null, meta);
            try
            {
                ChatApproval.BeginRun();
                Check("auto_accept_defaults_off_for_new_conversation", !ChatService.ConversationAutoAccept);
                var pending = ChatApproval.RequestAsync(new ChatApprovalPrompt { Kind = "permission", Title = "Pending when enabled" }, CancellationToken.None);
                ChatService.SetConversationAutoAccept(true);
                Check("auto_accept_enabling_resolves_current_prompt", pending.IsCompleted && pending.Result.Resolution == "Accept" && ChatApproval.Pending == null);
                ChatApproval.ToolResultCommitted();
                var loaded = ChatHistory.LoadMeta(meta.Provider, meta.Id);
                metaField.SetValue(null, loaded);
                Check("auto_accept_survives_conversation_reload", ChatService.ConversationAutoAccept);

                var other = ChatHistory.NewConversation("selftest-auto-accept", "test");
                metaField.SetValue(null, other);
                Check("auto_accept_does_not_leak_to_other_conversation", !ChatService.ConversationAutoAccept);
                metaField.SetValue(null, loaded);
                BridgeSettings.Current.AccessLevel = BridgeSettings.AccessReadOnly;
                string path = TestFolder + "/auto-accepted.txt";
                var apply = Task.Run(async () => await ChatApproval.ExecuteToolAsync(new ToolCall { Id = "auto-create", Name = "propose_file_changes",
                    Arguments = JsonValue.Obj("changes", JsonValue.Arr(JsonValue.Obj("kind", "create", "path", path, "content", "auto accepted"))).ToJson(false) }, CancellationToken.None));
                float deadline = Time.realtimeSinceStartup + 10;
                while (!apply.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Check("auto_accept_applies_read_only_tool_without_global_permission_change", apply.Status == TaskStatus.RanToCompletion && apply.Result["structuredContent"]["status"].AsString() == "Applied" &&
                    File.Exists(BridgePaths.ToAbsolute(path)) && BridgeSettings.Current.AccessLevel == BridgeSettings.AccessReadOnly && ChatApproval.Pending == null);
                if (apply.Status == TaskStatus.RanToCompletion)
                    foreach (var item in apply.Result["structuredContent"]["changes"].ArrayValue) _testChangeIds.Add(item["id"].AsString());
                ChatApproval.ToolResultCommitted();

                BridgeSettings.Current.AccessLevel = BridgeSettings.AccessAsk;
                var unrelated = ChangeService.StageFile(ChangeKinds.CreateFile, TestFolder + "/auto-unrelated.txt", "leave pending", "selftest");
                _testChangeIds.Add(unrelated.Id);
                var deletion = ChangeService.StageFile(ChangeKinds.DeleteFile, path, null, "selftest");
                _testChangeIds.Add(deletion.Id);
                var remove = Task.Run(async () => await ChatApproval.ExecuteToolAsync(new ToolCall { Id = "auto-delete", Name = "request_approval",
                    Arguments = JsonValue.Obj("ids", JsonValue.Arr(deletion.Id)).ToJson(false) }, CancellationToken.None));
                deadline = Time.realtimeSinceStartup + 10;
                while (!remove.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Check("auto_accept_includes_destructive_changes_and_preserves_unrelated_proposals", remove.Status == TaskStatus.RanToCompletion && remove.Result["structuredContent"]["status"].AsString() == "Applied" &&
                    !File.Exists(BridgePaths.ToAbsolute(path)) && unrelated.Status == ChangeStatus.Pending && !File.Exists(BridgePaths.ToAbsolute(unrelated.TargetPath)));
                ChatApproval.ToolResultCommitted();
                ChatService.SetConversationAutoAccept(false);
                var manual = ChatApproval.RequestAsync(new ChatApprovalPrompt { Kind = "permission", Title = "Manual again" }, CancellationToken.None);
                Check("auto_accept_can_be_disabled", !manual.IsCompleted && ChatApproval.Pending != null && !ChatHistory.LoadMeta(meta.Provider, meta.Id).AutoAcceptAll);
                ChatApproval.CancelForStop();

                var panel = new ChatPanel();
                var message = new ChatMessage { Id = "full-tool-output", Role = "activity", Activities = new List<ChatToolActivity> {
                    new ChatToolActivity { CallId = "full", Tool = "read_project_file", Status = "Done", Detail = "Short summary", Output = new string('x', 25000) + "\nEND OF TOOL OUTPUT" }
                }};
                var view = new ChatMessageView(panel); view.Bind(message);
                Check("tool_output_is_collapsed_by_default_and_unabridged", !view.Q<Foldout>("chat-tool-activity").value && view.Q<Label>("chat-tool-output").text == message.Activities[0].Output);
                Check("chat_exposes_conversation_auto_accept_toggle", panel.Q<Toggle>("chat-auto-accept") != null);
                panel.Detach();

                // Keep the full display output even when model context is bounded.
                string largeOutput = new string('x', 30000) + "\nEND OF ACTUAL TOOL OUTPUT";
                var provider = new ScriptedProvider();
                provider.Turns.Enqueue(new ProviderTurn { ToolCalls = new List<ToolCall> { new ToolCall { Id = "large-output", Name = "read_project_file", Arguments = "{}" } } });
                provider.Turns.Enqueue(new ProviderTurn { Text = "Verified output." });
                var transcript = new List<LlmMessage>();
                string observedOutput = null;
                var run = Task.Run(async () => await AgentRunner.RunAsync(provider, new LlmRequest
                {
                    Model = "test", Tools = new List<ToolSpec> { new ToolSpec { Name = "read_project_file" } },
                    ToolExecutor = (call, token) => Task.FromResult(JsonValue.Obj("content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", largeOutput))))
                }, transcript, evt => { if (evt.Kind == StreamEventKind.ToolCallResult) observedOutput = evt.ToolOutput; }, CancellationToken.None));
                deadline = Time.realtimeSinceStartup + 10;
                while (!run.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                var toolMessage = transcript.Find(m => m.Role == "tool");
                Check("full_tool_display_output_survives_model_context_compaction", run.Status == TaskStatus.RanToCompletion && observedOutput == largeOutput && toolMessage != null && toolMessage.Content.Length <= 18000);
            }
            finally
            {
                ChatApproval.CancelForStop();
                BridgeSettings.Current.AccessLevel = savedAccess;
                metaField.SetValue(null, savedMeta);
                ChatHistory.ClearProvider("selftest-auto-accept");
            }
        }

    }
}
