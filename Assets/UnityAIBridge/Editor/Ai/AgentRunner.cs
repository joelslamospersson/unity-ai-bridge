using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>Raised when the agent loop itself cannot complete, not when a provider fails.</summary>
    public sealed class AgentException : BridgeException
    {
        public AgentException(string message) : base(message) { }
    }

    /// <summary>
    /// Real tool-calling loop: stream a turn, execute requested Unity tools on the main
    /// thread, return their results to the model, and repeat until the model answers.
    /// </summary>
    public static class AgentRunner
    {
        const string FinalAnswerInstruction =
            "TOOL STEP LIMIT REACHED. Do not call any more tools. Give the user your best final answer now using the information already gathered. " +
            "Reply in plain prose only: do not emit XML, tool-call syntax, function-call syntax, special tokens or raw tool markup. " +
            "State clearly what you verified, what remains uncertain, and the single best next step. Do not claim any pending change was applied.";

        const string FinalAnswerRetryInstruction =
            "The tool budget is exhausted. Do not call or describe any tools. Answer my original question now in plain prose only. " +
            "Do not output XML, <tags>, tool-call syntax, special tokens or function-call markup. Summarize your findings and the best next step.";

        public static async Task<ProviderTurn> RunAsync(ILiveProvider provider, LlmRequest request, List<LlmMessage> transcript,
            Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            if (provider == null) throw new BridgeException("No AI provider selected.");
            request.Messages = transcript;
            var totalUsage = new TokenUsage();
            int maxSteps = Math.Max(1, Math.Min(24, request.MaxSteps));
            bool hasVisibleText = false;

            for (int step = 0; step < maxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step > 0) onEvent(StreamEvent.Status("Continuing after tool results (step " + (step + 1) + "/" + maxSteps + ")..."));

                bool firstText = true;
                var turn = await StreamWithTimeoutRecoveryAsync(provider, request, evt =>
                {
                    if (evt != null && evt.Kind == StreamEventKind.TextDelta && !string.IsNullOrEmpty(evt.Text))
                    {
                        if (firstText && hasVisibleText) onEvent(StreamEvent.Delta("\n\n"));
                        firstText = false;
                        hasVisibleText = true;
                    }
                    onEvent(evt);
                }, cancellationToken).ConfigureAwait(false);
                totalUsage.Add(turn.Usage);
                if ((turn.ToolCalls == null || turn.ToolCalls.Count == 0) && string.IsNullOrWhiteSpace(turn.Text))
                {
                    onEvent(StreamEvent.Status("The model finished without an answer. Retrying a final response with more output room..."));
                    turn = await RunFinalTurnAsync(provider, request, transcript, onEvent, cancellationToken, false, false).ConfigureAwait(false);
                    totalUsage.Add(turn.Usage);
                    if (IsUnusableFinalAnswer(turn.Text) || (turn.ToolCalls != null && turn.ToolCalls.Count > 0))
                        throw new AgentException("The model returned no usable answer after a recovery attempt. No new tool work was run by the retry; recorded results and pending changes are preserved.");
                    if (!string.IsNullOrEmpty(turn.Reasoning)) onEvent(StreamEvent.Reasoning("\n\n" + turn.Reasoning));
                    if (hasVisibleText) onEvent(StreamEvent.Delta("\n\n"));
                    onEvent(StreamEvent.Delta(turn.Text));
                    hasVisibleText = true;
                }

                var assistant = new LlmMessage
                {
                    Role = "assistant",
                    Content = turn.Text,
                    Reasoning = turn.Reasoning,
                    ToolCalls = turn.ToolCalls ?? new List<ToolCall>(),
                    RawItemsJson = turn.RawItemsJson
                };
                transcript.Add(assistant);

                if (turn.ToolCalls == null || turn.ToolCalls.Count == 0)
                {
                    if (IsUnusableFinalAnswer(turn.Text))
                        throw new AgentException("The model did not return a usable answer. Retry the message or choose another model.");
                    turn.Usage = totalUsage;
                    onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = totalUsage });
                    onEvent(new StreamEvent { Kind = StreamEventKind.Completed, Text = turn.Text });
                    return turn;
                }

                foreach (var call in turn.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onEvent(new StreamEvent { Kind = StreamEventKind.ToolExecutionStarted, ToolCall = call.Clone(), Text = call.Name });
                    string resultText;
                    bool ok = true;
                    var changeIds = new List<string>();
                    try
                    {
                        if (request.Tools == null || !request.Tools.Exists(t => t.Name == call.Name))
                            throw new BridgeException("This tool is not available in the current chat mode: " + call.Name);
                        JsonValue arguments;
                        string parseError;
                        if (!JsonValue.TryParse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments, out arguments, out parseError))
                        {
                            resultText = "ERROR: tool arguments were not valid JSON: " + parseError;
                            ok = false;
                        }
                        else
                        {
                            var result = request.ToolExecutor != null
                                ? await request.ToolExecutor(call, cancellationToken).ConfigureAwait(false)
                                : await McpTools.InvokeAsync(call.Name, arguments, cancellationToken).ConfigureAwait(false);
                            ok = !result["isError"].AsBool();
                            resultText = ExtractText(result);
                            CollectChangeIds(result, changeIds);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        ok = false;
                        resultText = "ERROR: " + e.Message;
                    }

                    string displayOutput = FileService.RedactSecrets(resultText ?? string.Empty);
                    resultText = CompactToolResult(displayOutput, call.Name, changeIds);

                    transcript.Add(new LlmMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Name = call.Name,
                        Content = resultText
                    });

                    if (request.ToolResultCommitted != null) request.ToolResultCommitted();
                    var resultEvent = StreamEvent.Result(call, SummarizeToolResult(call, resultText), ok, changeIds);
                    resultEvent.ToolOutput = displayOutput;
                    onEvent(resultEvent);
                    if (changeIds.Count > 0)
                        onEvent(new StreamEvent { Kind = StreamEventKind.ChangeStaged, ChangeIds = changeIds, Text = call.Name });
                }
            }

            onEvent(StreamEvent.Status("Tool step limit reached (" + maxSteps + " turns). Asking for a final answer without tools..."));
            var finalTurn = await RunFinalTurnAsync(provider, request, transcript, onEvent, cancellationToken, false).ConfigureAwait(false);
            totalUsage.Add(finalTurn.Usage);
            string finalAnswer = finalTurn.Text;
            string finalReasoning = finalTurn.Reasoning;

            if (IsUnusableFinalAnswer(finalAnswer))
            {
                onEvent(StreamEvent.Status("The model returned tool-call markup instead of an answer. Retrying the final answer..."));
                var retry = await RunFinalTurnAsync(provider, request, transcript, onEvent, cancellationToken, true).ConfigureAwait(false);
                totalUsage.Add(retry.Usage);
                finalTurn = retry;

                if (!IsUnusableFinalAnswer(retry.Text))
                {
                    finalAnswer = retry.Text;
                    finalReasoning = retry.Reasoning;
                }
                else
                {
                    finalAnswer = string.Empty;
                    finalReasoning = string.Empty;
                }
            }

            finalTurn.ToolCalls = new List<ToolCall>();
            finalTurn.Text = finalAnswer;
            finalTurn.Usage = totalUsage;

            if (string.IsNullOrWhiteSpace(finalAnswer))
                throw new AgentException("The assistant reached the " + maxSteps + "-turn tool limit and did not return a final answer. Ask it to continue or narrow the request.");

            transcript.Add(new LlmMessage
            {
                Role = "assistant",
                Content = finalAnswer,
                Reasoning = finalReasoning,
                ToolCalls = new List<ToolCall>(),
                RawItemsJson = finalTurn.RawItemsJson
            });

            if (!string.IsNullOrEmpty(finalReasoning))
                onEvent(StreamEvent.Reasoning(finalReasoning));
            if (hasVisibleText) onEvent(StreamEvent.Delta("\n\n"));
            onEvent(StreamEvent.Delta(finalAnswer));
            onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = totalUsage });
            onEvent(new StreamEvent { Kind = StreamEventKind.Completed, Text = finalAnswer });
            return finalTurn;
        }

        static async Task<ProviderTurn> RunFinalTurnAsync(ILiveProvider provider, LlmRequest request, List<LlmMessage> transcript,
            Action<StreamEvent> onEvent, CancellationToken cancellationToken, bool retry, bool stepLimit = true)
        {
            List<LlmMessage> messages = transcript;
            if (retry)
            {
                messages = new List<LlmMessage>(transcript);
                messages.Add(new LlmMessage { Role = "user", Content = FinalAnswerRetryInstruction });
            }

            var finalRequest = FinalAnswerRequest(request, messages, retry);
            if (!stepLimit)
            {
                finalRequest.Instructions = (request.Instructions ?? string.Empty) + "\n\nThe last model response ended without a user-visible answer. Give a concise final answer to the user's latest request using only recorded results. Do not output reasoning alone, tool markup, or pretend unfinished work is complete. State any pending changes or missing work honestly. No tools are available for this recovery response.";
                finalRequest.MaxOutputTokens = Math.Max(8192, request.MaxOutputTokens);
            }
            Action<StreamEvent> buffered = evt =>
            {
                if (evt == null) return;
                if (evt.Kind == StreamEventKind.TextDelta || evt.Kind == StreamEventKind.ReasoningDelta ||
                    evt.Kind == StreamEventKind.ToolCallStarted || evt.Kind == StreamEventKind.ToolCallArguments ||
                    evt.Kind == StreamEventKind.ToolCallCompleted || evt.Kind == StreamEventKind.ToolCallResult ||
                    evt.Kind == StreamEventKind.ChangeStaged) return;
                onEvent(evt);
            };
            return await StreamWithTimeoutRecoveryAsync(provider, finalRequest, buffered, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<ProviderTurn> StreamWithTimeoutRecoveryAsync(ILiveProvider provider, LlmRequest request,
            Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                bool outputStarted = false;
                try
                {
                    return await provider.StreamTurnAsync(request, evt =>
                    {
                        if (evt != null && evt.Kind != StreamEventKind.Status && evt.Kind != StreamEventKind.Usage && evt.Kind != StreamEventKind.TransportActivity)
                            outputStarted = true;
                        onEvent(evt);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (ProviderTimeoutException) when (attempt == 0 && !outputStarted && !cancellationToken.IsCancellationRequested)
                {
                    // Retry only this inference. Completed tools remain in request.Messages;
                    // no project tools, partial answers or partial tool calls are replayed.
                    onEvent(StreamEvent.Status("The provider stopped responding before output began. Retrying once; completed tool results are preserved..."));
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        static bool IsUnusableFinalAnswer(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            return text.IndexOf("<｜", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("<|tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("<|function", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("invoke name=", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static LlmRequest FinalAnswerRequest(LlmRequest request, List<LlmMessage> messages, bool retry)
        {
            return new LlmRequest
            {
                ProviderId = request.ProviderId,
                SessionId = request.SessionId,
                Model = request.Model,
                Instructions = (request.Instructions ?? string.Empty) + "\n\n" + FinalAnswerInstruction +
                               (retry ? "\n\n" + FinalAnswerRetryInstruction : string.Empty),
                Messages = messages,
                Tools = new List<ToolSpec>(),
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens,
                Thinking = request.Thinking,
                ReasoningEffort = request.ReasoningEffort,
                MaxSteps = request.MaxSteps
            };
        }

        internal static string CompactToolResult(string text, string tool, List<string> changeIds)
        {
            const int budget = 18000;
            if (text.Length <= budget) return text;
            var ids = JsonValue.NewArray();
            foreach (var id in changeIds) ids.Add(id);
            int length = Math.Min(text.Length, budget - 1000);
            while (true)
            {
                string compact = JsonValue.Obj("truncated", true, "tool", tool, "original_characters", text.Length,
                    "preview", text.Substring(0, length), "change_ids", ids,
                    "hint", "Output was shortened to keep model context bounded. Narrow list_project_files by path/extensions/max, or read_project_file by start_line/line_count/max_chars. Use inspect_asset for prefab hierarchy.").ToJson(false);
                if (compact.Length <= budget) return compact;
                length = Math.Max(0, length * 3 / 4);
            }
        }

        static string ExtractText(JsonValue result)
        {
            if (result["structuredContent"].IsObject || result["structuredContent"].IsArray)
                return result["structuredContent"].ToJson(false);
            if (result["content"].IsArray)
            {
                var sb = new StringBuilder();
                foreach (var part in result["content"].ArrayValue)
                {
                    if (part["type"].AsString() == "text")
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(part["text"].AsString());
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }
            return result.ToJson(false);
        }

        internal static string SummarizeToolResult(ToolCall call, string text)
        {
            JsonValue result;
            string error;
            if (JsonValue.TryParse(text, out result, out error) && result.IsObject)
            {
                if (result["truncated"].AsBool() && !result["preview"].IsNull) return "Output shortened; use a narrower query.";
                string path = result["path"].AsString(result["target"].AsString());
                if (call.Name == "read_project_file") return path + " · lines " + result["start_line"].AsInt(1) + "–" + result["end_line"].AsInt() + (result["truncated"].AsBool() ? " · more available" : "");
                if (call.Name == "list_project_files") return result["folder"].AsString() + " · " + result["count"].AsInt() + " files";
                if (call.Name == "project_summary") return result["project"].AsString() + " · Unity " + result["unity_version"].AsString();
                if (call.Name == "inspect_asset") return path + " · " + result["kind"].AsString(result["type"].AsString());
                if (call.Name == "inspect_scene") return result["scene"].AsString() + (result["dirty"].AsBool() ? " · unsaved changes" : "");
                if (!string.IsNullOrEmpty(result["status"].AsString())) return result["status"].AsString() + " · " + result["title"].AsString(path);
                if (!string.IsNullOrEmpty(result["message"].AsString())) return result["message"].AsString();
                if (result["changes"].IsArray) return result["changes"].Count + " change(s) staged for review";
            }
            return Summarize(text);
        }

        static string Summarize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty result)";
            int newline = text.IndexOf('\n');
            string first = newline >= 0 ? text.Substring(0, newline) : text;
            if (first.Length > 240) first = first.Substring(0, 240) + "...";
            return first;
        }

        static void CollectChangeIds(JsonValue node, List<string> ids)
        {
            if (node == null) return;
            if (node.IsArray)
            {
                foreach (var item in node.ArrayValue) CollectChangeIds(item, ids);
                return;
            }
            if (!node.IsObject) return;

            string id = node["id"].AsString();
            string status = node["status"].AsString();
            if (!string.IsNullOrEmpty(id) && (status == "Pending" || status == "Approved" || status == "Applied" || status == "Failed"))
                if (!ids.Contains(id)) ids.Add(id);

            if (node["changes"].IsArray) CollectChangeIds(node["changes"], ids);
            if (node["structuredContent"].IsObject) CollectChangeIds(node["structuredContent"], ids);
            if (node["result"].IsObject) CollectChangeIds(node["result"], ids);
        }
    }
}
