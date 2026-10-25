using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>
    /// Real tool-calling loop: stream a turn, execute requested Unity tools on the main
    /// thread, return their results to the model, and repeat until the model answers.
    /// </summary>
    public static class AgentRunner
    {
        public static async Task<ProviderTurn> RunAsync(ILiveProvider provider, LlmRequest request, List<LlmMessage> transcript,
            Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            if (provider == null) throw new BridgeException("No AI provider selected.");
            var totalUsage = new TokenUsage();
            int maxSteps = Math.Max(1, Math.Min(24, request.MaxSteps));

            for (int step = 0; step < maxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step > 0) onEvent(StreamEvent.Status("Continuing after tool results (step " + (step + 1) + "/" + maxSteps + ")..."));

                var turn = await provider.StreamTurnAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
                totalUsage.Add(turn.Usage);

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
                    turn.Usage = totalUsage;
                    onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = totalUsage });
                    onEvent(new StreamEvent { Kind = StreamEventKind.Completed, Text = turn.Text });
                    return turn;
                }

                foreach (var call in turn.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = call.Clone(), Text = call.Name });
                    string resultText;
                    bool ok = true;
                    var changeIds = new List<string>();
                    try
                    {
                        JsonValue arguments;
                        string parseError;
                        if (!JsonValue.TryParse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments, out arguments, out parseError))
                        {
                            resultText = "ERROR: tool arguments were not valid JSON: " + parseError;
                            ok = false;
                        }
                        else
                        {
                            var result = McpTools.InvokeFromAnyThread(call.Name, arguments);
                            ok = !result["isError"].AsBool();
                            resultText = ExtractText(result);
                            CollectChangeIds(result, changeIds);
                        }
                    }
                    catch (Exception e)
                    {
                        ok = false;
                        resultText = "ERROR: " + e.Message;
                    }

                    resultText = FileService.RedactSecrets(resultText ?? string.Empty);
                    if (resultText.Length > 30000)
                        resultText = resultText.Substring(0, 30000) + "\n... tool output truncated by the bridge ...";

                    transcript.Add(new LlmMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Name = call.Name,
                        Content = resultText
                    });

                    onEvent(StreamEvent.Result(call, Summarize(resultText), ok, changeIds));
                    if (changeIds.Count > 0)
                        onEvent(new StreamEvent { Kind = StreamEventKind.ChangeStaged, ChangeIds = changeIds, Text = call.Name });
                }
            }

            throw new BridgeException("The assistant stopped after " + maxSteps + " tool steps without a final answer. Ask it to continue or narrow the request.");
        }

        static string ExtractText(JsonValue result)
        {
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
