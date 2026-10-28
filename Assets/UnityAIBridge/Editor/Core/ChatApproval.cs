using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    public enum ApprovalChoice { Accept, Deny, Comment }

    [Serializable]
    public sealed class ChatApprovalPrompt
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Kind;
        public ToolCall Tool;
        public string Title;
        public string Detail;
        public List<string> ChangeIds = new List<string>();
        public bool Destructive;
        public bool CompilesScripts;
        public string Resolution;
        public string Comment;
        public bool ConfirmedDestructive;
        public bool RequireExplicitConsent;
        public bool ScopedWrite;
        public bool ExternalRequestStarted;
        public string BillingFingerprint;
    }

    // Only the Unity main thread publishes/resolves prompts or applies changes.
    // Waiting uses a Task, with no timeout and no outstanding provider connection.
    public static class ChatApproval
    {
        static ChatApprovalPrompt _journal;
        static TaskCompletionSource<ChatApprovalPrompt> _answer;
        static Action<bool> _checkpoint;
        static bool _preserve;
        static bool _reloadLocked;
        static readonly HashSet<string> DeniedCalls = new HashSet<string>();
        static string JournalFile { get { return Path.Combine(BridgePaths.LibraryRoot, "chat-approval.json"); } }
        public static event Action Changed;
        public static ChatApprovalPrompt Pending { get { return _journal != null && string.IsNullOrEmpty(_journal.Resolution) ? _journal : null; } }
        internal static ChatApprovalPrompt Journal { get { return _journal; } }

        static void Notify() { var handler = Changed; if (handler != null) handler(); }
        static void Save()
        {
            if (_journal == null) { if (File.Exists(JournalFile)) File.Delete(JournalFile); return; }
            string temp = JournalFile + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(_journal));
            if (File.Exists(JournalFile)) File.Replace(temp, JournalFile, null);
            else File.Move(temp, JournalFile);
        }

        internal static void MarkExternalRequestStarted(string promptId)
        {
            MainThread.Invoke(() =>
            {
                if (_journal == null || _journal.Id != promptId || _journal.Resolution != "Accept" || _journal.ExternalRequestStarted)
                    throw new BridgeException("Image-generation consent is no longer available. Review the request again.");
                _journal.ExternalRequestStarted = true;
                Save();
            });
        }

        internal static void BeginRun(bool resetDecisions = true) { if (resetDecisions) DeniedCalls.Clear(); _preserve = false; }
        internal static void PreserveForReload() { _preserve = true; }
        internal static void LoadJournal()
        {
            if (_journal == null && File.Exists(JournalFile)) _journal = JsonUtility.FromJson<ChatApprovalPrompt>(File.ReadAllText(JournalFile));
        }

        public static bool Resolve(string id, ApprovalChoice choice, string comment, bool destructiveConfirmed)
        {
            var prompt = Pending;
            if (prompt == null || prompt.Id != id) return false;
            if (choice == ApprovalChoice.Accept && prompt.Destructive && !destructiveConfirmed) return false;
            if (choice == ApprovalChoice.Comment && string.IsNullOrWhiteSpace(comment)) return false;
            prompt.Resolution = choice.ToString();
            prompt.Comment = FileService.RedactSecrets((comment ?? string.Empty).Trim());
            prompt.ConfirmedDestructive = destructiveConfirmed;
            Save();
            var completion = _answer;
            _answer = null;
            if (completion != null) completion.TrySetResult(prompt);
            Notify();
            return true;
        }

        internal static Task<ChatApprovalPrompt> RequestAsync(ChatApprovalPrompt prompt, CancellationToken token)
        {
            return MainThread.Invoke(() =>
            {
                token.ThrowIfCancellationRequested();
                if (_journal != null && _journal.Id == prompt.Id && !string.IsNullOrEmpty(_journal.Resolution))
                    return Task.FromResult(_journal);
                if (_answer != null) throw new BridgeException("Another operation is already awaiting review.");
                _journal = prompt;
                _answer = new TaskCompletionSource<ChatApprovalPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = _answer;
                Save();
                if (_checkpoint != null) _checkpoint(true);
                var registration = token.Register(() => MainThread.Run(() =>
                {
                    completion.TrySetCanceled();
                    if (ReferenceEquals(_answer, completion))
                    {
                        _answer = null;
                        if (!_preserve) { _journal = null; Save(); }
                        Notify();
                    }
                }));
                completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
                // The user can opt this conversation into accepting every prompt,
                // including destructive changes. Keep journaling/checkpointing so
                // script compilation resumes through the normal approval path.
                if (ChatService.ConversationAutoAccept && !prompt.RequireExplicitConsent) Resolve(prompt.Id, ApprovalChoice.Accept, "", true);
                Notify();
                return completion.Task;
            });
        }

        static bool IsWrite(string name)
        {
            var tool = McpTools.Tools.Find(t => t.Name == name);
            return tool != null && tool.IsWrite;
        }

        static JsonValue InvokeScoped(ToolCall call, bool allowWrite)
        {
            return MainThread.Invoke(() =>
            {
                int saved = BridgeSettings.Current.AccessLevel;
                try
                {
                    if (allowWrite && !Permissions.CanPropose) BridgeSettings.Current.AccessLevel = BridgeSettings.AccessAsk;
                    return McpTools.InvokeOnMain(call.Name, JsonValue.Parse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments));
                }
                finally { BridgeSettings.Current.AccessLevel = saved; }
            });
        }

        public static async Task<JsonValue> ExecuteToolAsync(ToolCall call, CancellationToken token, Action<bool> checkpoint = null)
        {
            _checkpoint = checkpoint;
            token.ThrowIfCancellationRequested();
            if (call.Name == "apply_approved_changes" && !MainThread.Invoke(() => Permissions.CanApply))
            {
                var approved = MainThread.Invoke(() => ChangeService.All.FindAll(c => c.Status == ChangeStatus.Approved).ConvertAll(c => c.Id));
                if (approved.Count == 0) return DecisionResult("AwaitingReview", "No changes have been approved. Use request_approval with specific pending IDs.", null, null);
                return await ReviewChangesAsync(call, approved, token).ConfigureAwait(false);
            }
            bool scopedWrite = false;
            string signature = call.Name + "|" + call.Arguments;
            if (DeniedCalls.Contains(signature)) return DecisionResult("Denied", "The user already denied this operation. Do not request it again unchanged.", null, null);
            if (IsWrite(call.Name) && !MainThread.Invoke(() => Permissions.CanPropose))
            {
                var decision = await RequestAsync(new ChatApprovalPrompt
                {
                    Kind = "permission", Tool = call.Clone(), Title = "Allow this operation?",
                    Detail = "Read Only is active. Accept permits this tool to stage or manage proposals for this operation only. Actual project edits still require review. Your permission setting is unchanged.\n\n" + call.Name + "\n" + FileService.RedactSecrets(call.Arguments)
                }, token).ConfigureAwait(false);
                if (decision.Resolution != ApprovalChoice.Accept.ToString())
                {
                    DeniedCalls.Add(signature);
                    return DecisionResult(decision.Resolution == "Comment" ? "RevisionRequested" : "Denied", "The operation was not run. Respect the user's decision and continue with another approach.", decision.Comment, null);
                }
                scopedWrite = true;
            }
            token.ThrowIfCancellationRequested();
            var result = call.Name == "generate_image"
                ? await ImageGenerationService.GenerateAndStageAsync(JsonValue.Parse(call.Arguments), token, scopedWrite, false, call).ConfigureAwait(false)
                : InvokeScoped(call, scopedWrite);
            if (result["isError"].AsBool()) return result;
            if (call.Name == "generate_image" && (result["structuredContent"]["status"].AsString() == "Denied" || result["structuredContent"]["status"].AsString() == "RevisionRequested"))
                DeniedCalls.Add(signature);
            var ids = new List<string>();
            CollectPendingIds(result, ids);
            if (ids.Count == 0) return result;
            return await ReviewChangesAsync(call, ids, token).ConfigureAwait(false);
        }

        static void CollectPendingIds(JsonValue node, List<string> ids)
        {
            if (node.IsArray) { foreach (var child in node.ArrayValue) CollectPendingIds(child, ids); return; }
            if (!node.IsObject) return;
            string id = node["id"].AsString();
            if (!string.IsNullOrEmpty(id) && (node["status"].AsString() == ChangeStatus.Pending || node["status"].AsString() == ChangeStatus.Approved) && !ids.Contains(id)) ids.Add(id);
            foreach (var pair in node.Properties) if (pair.Value.IsObject || pair.Value.IsArray) CollectPendingIds(pair.Value, ids);
        }

        static ChatApprovalPrompt BuildChangePrompt(ToolCall call, List<string> ids)
        {
            var prompt = new ChatApprovalPrompt { Kind = "changes", Tool = call.Clone(), Title = "Review changes to continue", ChangeIds = ids };
            var detail = new System.Text.StringBuilder();
            foreach (var id in ids)
            {
                var change = ChangeService.Find(id);
                if (change == null) throw new BridgeException("Proposed change no longer exists: " + id);
                prompt.Destructive |= change.Destructive;
                prompt.CompilesScripts |= NeedsReload(change);
                detail.AppendLine(change.Title + "\n" + change.TargetPath);
                detail.AppendLine(string.IsNullOrEmpty(change.Diff) ? change.ParametersJson : change.Diff);
                detail.AppendLine();
            }
            prompt.Detail = FileService.RedactSecrets(detail.ToString());
            if (prompt.CompilesScripts) prompt.Detail += "\nApplying scripts triggers Unity compilation. The conversation will resume afterward. Editor scripts may run when loaded.";
            return prompt;
        }

        static bool NeedsReload(BridgeChange change)
        {
            string extension = Path.GetExtension(change.TargetPath).ToLowerInvariant();
            return extension == ".cs" || extension == ".asmdef" || extension == ".asmref" || extension == ".rsp";
        }

        static async Task<JsonValue> ReviewChangesAsync(ToolCall call, List<string> ids, CancellationToken token, ChatApprovalPrompt restored = null)
        {
            var prompt = restored ?? MainThread.Invoke(() => BuildChangePrompt(call, ids));
            var decision = await RequestAsync(prompt, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return MainThread.Invoke(() =>
            {
                if (decision.Resolution != ApprovalChoice.Accept.ToString())
                {
                    foreach (var id in ids)
                    {
                        var change = ChangeService.Find(id);
                        if (change != null && change.Status == ChangeStatus.Pending) ChangeService.Reject(id);
                    }
                    DeniedCalls.Add(call.Name + "|" + call.Arguments);
                    return DecisionResult(decision.Resolution == "Comment" ? "RevisionRequested" : "Denied", "No changes from this proposal were applied. Continue with the user's decision; revise the proposal when feedback was supplied.", decision.Comment, ids);
                }
                // Only the IDs displayed in this prompt may be approved/applied.
                var errors = new List<string>();
                int saved = BridgeSettings.Current.AccessLevel;
                if (prompt.CompilesScripts && ids.Exists(id => ChangeService.Find(id) != null && ChangeService.Find(id).Status != ChangeStatus.Applied) && !_reloadLocked) { EditorApplication.LockReloadAssemblies(); _reloadLocked = true; }
                try
                {
                    if (!Permissions.CanApply) BridgeSettings.Current.AccessLevel = BridgeSettings.AccessAsk;
                    foreach (var id in ids)
                    {
                        var change = ChangeService.Find(id);
                        if (change == null || (change.Status != ChangeStatus.Pending && change.Status != ChangeStatus.Approved && change.Status != ChangeStatus.Applied))
                        { errors.Add("Change is no longer available for approval: " + id); continue; }
                        string error = change.Status == ChangeStatus.Applied ? null : ChangeService.Approve(id, decision.ConfirmedDestructive) ?? ChangeService.Apply(id);
                        if (error != null) errors.Add(error);
                    }
                }
                finally { BridgeSettings.Current.AccessLevel = saved; }
                return DecisionResult(errors.Count == 0 ? "Applied" : "Failed", errors.Count == 0 ? "The user accepted and the reviewed changes were applied. Verify the result and continue the original task." : string.Join("\n", errors.ToArray()), decision.Comment, ids);
            });
        }

        static JsonValue DecisionResult(string status, string message, string comment, List<string> ids)
        {
            var changes = JsonValue.NewArray();
            if (ids != null) foreach (var id in ids)
            {
                var change = MainThread.Invoke(() => ChangeService.Find(id));
                if (change != null) changes.Add(JsonValue.Obj("id", id, "path", change.TargetPath, "status", change.Status, "kind", change.Kind));
            }
            var structured = JsonValue.Obj("status", status, "message", message, "user_comment", comment ?? string.Empty, "changes", changes);
            return JsonValue.Obj("isError", status == "Failed", "structuredContent", structured, "content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", structured.ToJson(false))));
        }

        internal static void ToolResultCommitted()
        {
            MainThread.Invoke(() =>
            {
                if (_journal == null && !_reloadLocked) return;
                try
                {
                    if (_checkpoint != null) _checkpoint(_reloadLocked);
                    _journal = null; Save();
                }
                finally
                {
                    if (_reloadLocked) { _reloadLocked = false; EditorApplication.UnlockReloadAssemblies(); }
                }
                Notify();
            });
        }

        internal static async Task<JsonValue> ResumeReviewAsync(ChatApprovalPrompt prompt, CancellationToken token, Action<bool> checkpoint)
        {
            _checkpoint = checkpoint;
            if (prompt.Kind == "changes") return await ReviewChangesAsync(prompt.Tool, prompt.ChangeIds, token, prompt).ConfigureAwait(false);
            if (prompt.Kind == "image-generation")
            {
                var generated = await ImageGenerationService.GenerateAndStageAsync(JsonValue.Parse(prompt.Tool.Arguments), token, prompt.ScopedWrite, false, prompt.Tool, prompt).ConfigureAwait(false);
                var generatedIds = new List<string>(); CollectPendingIds(generated, generatedIds);
                if (generatedIds.Count > 0) return await ReviewChangesAsync(prompt.Tool, generatedIds, token).ConfigureAwait(false);
                return generated;
            }
            // Reuse the persisted permission decision; never make a new implicit grant.
            var decision = await RequestAsync(prompt, token).ConfigureAwait(false);
            if (decision.Resolution != "Accept")
            {
                DeniedCalls.Add(prompt.Tool.Name + "|" + prompt.Tool.Arguments);
                return DecisionResult(decision.Resolution == "Comment" ? "RevisionRequested" : "Denied", "The operation was not run.", decision.Comment, null);
            }
            var result = prompt.Tool.Name == "generate_image"
                ? await ImageGenerationService.GenerateAndStageAsync(JsonValue.Parse(prompt.Tool.Arguments), token, true, false, prompt.Tool).ConfigureAwait(false)
                : InvokeScoped(prompt.Tool, true);
            var ids = new List<string>(); CollectPendingIds(result, ids);
            if (ids.Count > 0) return await ReviewChangesAsync(prompt.Tool, ids, token).ConfigureAwait(false);
            return result;
        }

        internal static void CancelForStop()
        {
            _preserve = false;
            if (_answer != null) _answer.TrySetCanceled();
            _answer = null; _journal = null; Save();
            if (_reloadLocked) { _reloadLocked = false; EditorApplication.UnlockReloadAssemblies(); }
            Notify();
        }
    }
}
