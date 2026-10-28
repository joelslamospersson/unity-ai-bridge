using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    public sealed class ClaudeApiProvider : ILiveProvider
    {
        public string Id { get { return BridgeSettings.ProviderClaude; } }
        public string DisplayName { get { return "Claude API"; } }
        public string BillingNote { get { return "Anthropic API billing. Uses your Claude Console API key; Claude Pro/Max subscriptions are separate."; } }

        public bool IsConfigured(out string reason)
        {
            reason = string.IsNullOrWhiteSpace(BridgeSettings.Secrets.ClaudeApiKey) ? "Add a Claude API key in Settings, then Save & test." : null;
            return reason == null;
        }

        Dictionary<string, string> Headers()
        {
            return new Dictionary<string, string> { { "x-api-key", BridgeSettings.Secrets.ClaudeApiKey }, { "anthropic-version", "2023-06-01" } };
        }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken token)
        {
            string reason; if (!IsConfigured(out reason)) throw new BridgeException(reason);
            var models = new List<ModelInfo>();
            string cursor = null;
            var seen = new HashSet<string>();
            var cursors = new HashSet<string>();
            do
            {
                var page = await StreamingHttp.GetJsonAsync(BridgeSettings.Current.ClaudeBaseUrl.TrimEnd('/') + "/models?limit=100" + (cursor == null ? "" : "&after_id=" + Uri.EscapeDataString(cursor)), Headers(), 30, token).ConfigureAwait(false);
                if (page["data"].IsArray) foreach (var model in page["data"].ArrayValue)
                {
                    string id = model["id"].AsString();
                    if (!string.IsNullOrEmpty(id) && seen.Add(id)) models.Add(new ModelInfo { Id = id, DisplayName = model["display_name"].AsString(id) });
                }
                string next = page["has_more"].AsBool() ? page["last_id"].AsString() : null;
                if (string.IsNullOrEmpty(next) || !cursors.Add(next)) break;
                cursor = next;
            } while (true);
            return models;
        }

        public async Task<string> TestConnectionAsync(CancellationToken token)
        {
            return "Claude API key accepted · " + (await GetModelsAsync(token).ConfigureAwait(false)).Count + " models available.";
        }

        static void AddMessage(JsonValue messages, string role, JsonValue content)
        {
            if (messages.Count > 0 && messages[messages.Count - 1]["role"].AsString() == role)
                foreach (var block in content.ArrayValue) messages[messages.Count - 1]["content"].Add(block);
            else messages.Add(JsonValue.Obj("role", role, "content", content));
        }

        internal static JsonValue Messages(List<LlmMessage> transcript)
        {
            var messages = JsonValue.NewArray();
            foreach (var message in transcript)
            {
                if (message == null) continue;
                var content = JsonValue.NewArray();
                if (message.Role == "tool")
                {
                    content.Add(JsonValue.Obj("type", "tool_result", "tool_use_id", message.ToolCallId, "content", message.Content ?? ""));
                    AddMessage(messages, "user", content); continue;
                }
                JsonValue raw; string error;
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.RawItemsJson) && JsonValue.TryParse(message.RawItemsJson, out raw, out error) && raw["provider"].AsString() == "claude" && raw["content"].IsArray)
                    content = JsonValue.Parse(raw["content"].ToJson(false));
                else
                {
                    if (!string.IsNullOrEmpty(message.Content)) content.Add(JsonValue.Obj("type", "text", "text", message.Content));
                    if (message.Role == "assistant" && message.ToolCalls != null) foreach (var call in message.ToolCalls)
                        content.Add(JsonValue.Obj("type", "tool_use", "id", call.Id, "name", call.Name, "input", JsonValue.Parse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments)));
                }
                if (content.Count > 0) AddMessage(messages, message.Role == "assistant" ? "assistant" : "user", content);
            }
            return messages;
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken token)
        {
            string reason; if (!IsConfigured(out reason)) throw new BridgeException(reason);
            int maxTokens = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : 8192;
            var body = JsonValue.Obj("model", request.Model, "messages", Messages(request.Messages), "max_tokens", maxTokens, "stream", true);
            if (!string.IsNullOrEmpty(request.Instructions)) body["system"] = request.Instructions;
            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = JsonValue.NewArray();
                foreach (var tool in request.Tools) tools.Add(JsonValue.Obj("name", tool.Name, "description", tool.Description, "input_schema", tool.Schema));
                body["tools"] = tools;
            }
            if (request.Thinking && maxTokens > 1024)
            {
                bool adaptive = System.Text.RegularExpressions.Regex.IsMatch(request.Model ?? "", @"claude-(?:opus|sonnet)-(?:[5-9]|4[-.]6)");
                body["thinking"] = adaptive ? JsonValue.Obj("type", "adaptive") : JsonValue.Obj("type", "enabled", "budget_tokens", Math.Min(4096, Math.Max(1024, maxTokens / 2)));
                if (adaptive && (request.ReasoningEffort == "low" || request.ReasoningEffort == "medium" || request.ReasoningEffort == "high")) body["output_config"] = JsonValue.Obj("effort", request.ReasoningEffort);
            }
            else if (request.Temperature >= 0) body["temperature"] = request.Temperature;

            var text = new StringBuilder(); var thinking = new StringBuilder();
            var blocks = new SortedDictionary<int, JsonValue>();
            var arguments = new Dictionary<int, StringBuilder>();
            var usage = new TokenUsage { Model = request.Model, CostText = "Claude API (Anthropic billing)" };
            bool stopped = false; string stopReason = null;
            await StreamingHttp.PostSseAsync(BridgeSettings.Current.ClaudeBaseUrl.TrimEnd('/') + "/messages", body.ToJson(false), Headers(), BridgeSettings.Current.RequestTimeoutSeconds, data =>
            {
                var evt = JsonValue.Parse(data); string type = evt["type"].AsString();
                if (type == "error") throw new BridgeException("Claude: " + FileService.RedactSecrets(evt["error"]["message"].AsString("Stream failed.")));
                int index = evt["index"].AsInt();
                if (type == "message_start")
                {
                    var u = evt["message"]["usage"];
                    usage.PromptTokens = u["input_tokens"].AsInt() + u["cache_creation_input_tokens"].AsInt() + u["cache_read_input_tokens"].AsInt();
                    usage.CachedTokens = u["cache_read_input_tokens"].AsInt();
                }
                else if (type == "content_block_start")
                {
                    blocks[index] = evt["content_block"];
                    if (blocks[index]["type"].AsString() == "tool_use") arguments[index] = new StringBuilder();
                    if (blocks[index]["type"].AsString() == "text")
                    { string delta = blocks[index]["text"].AsString(""); text.Append(delta); if (delta.Length > 0) onEvent(StreamEvent.Delta(delta)); }
                }
                else if (type == "content_block_delta")
                {
                    if (!blocks.ContainsKey(index)) throw new BridgeException("Claude returned a delta without its content block.");
                    var delta = evt["delta"]; string kind = delta["type"].AsString();
                    if (kind == "text_delta") { string value = delta["text"].AsString(""); text.Append(value); blocks[index]["text"] = blocks[index]["text"].AsString("") + value; onEvent(StreamEvent.Delta(value)); }
                    else if (kind == "thinking_delta") { string value = delta["thinking"].AsString(""); thinking.Append(value); blocks[index]["thinking"] = blocks[index]["thinking"].AsString("") + value; onEvent(StreamEvent.Reasoning(value)); }
                    else if (kind == "signature_delta") blocks[index]["signature"] = blocks[index]["signature"].AsString("") + delta["signature"].AsString("");
                    else if (kind == "input_json_delta" && arguments.ContainsKey(index)) arguments[index].Append(delta["partial_json"].AsString());
                }
                else if (type == "message_delta") { stopReason = evt["delta"]["stop_reason"].AsString(); usage.CompletionTokens = evt["usage"]["output_tokens"].AsInt(usage.CompletionTokens); }
                else if (type == "message_stop") stopped = true;
            }, token, () => stopped, () => onEvent(new StreamEvent { Kind = StreamEventKind.TransportActivity })).ConfigureAwait(false);
            if (!stopped) throw new BridgeException("Claude stream ended before message_stop. Retry the response.");
            if (stopReason == "max_tokens") throw new BridgeException("Claude reached the output limit before finishing. Increase max output tokens and retry.");
            if (stopReason != "end_turn" && stopReason != "tool_use" && stopReason != "stop_sequence") throw new BridgeException("Claude did not finish successfully: " + (stopReason ?? "missing stop reason"));
            var rawContent = JsonValue.NewArray(); var calls = new List<ToolCall>();
            foreach (var pair in blocks)
            {
                var block = pair.Value;
                if (block["type"].AsString() == "tool_use")
                {
                    if (arguments[pair.Key].Length > 0) block["input"] = JsonValue.Parse(arguments[pair.Key].ToString());
                    if (!block["input"].IsObject) throw new BridgeException("Claude returned invalid tool arguments.");
                    calls.Add(new ToolCall { Id = block["id"].AsString(), Name = block["name"].AsString(), Arguments = block["input"].ToJson(false) });
                }
                rawContent.Add(block);
            }
            usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
            return new ProviderTurn { Text = text.ToString(), Reasoning = thinking.ToString(), ToolCalls = calls, Usage = usage, StopReason = stopReason, RawItemsJson = JsonValue.Obj("provider", "claude", "content", rawContent).ToJson(false) };
        }
    }
}
