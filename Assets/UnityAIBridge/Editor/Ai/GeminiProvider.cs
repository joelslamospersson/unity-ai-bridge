using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    public sealed class GeminiProvider : ILiveProvider
    {
        public string Id { get { return BridgeSettings.ProviderGemini; } }
        public string DisplayName { get { return "Gemini API"; } }
        public string BillingNote { get { return "Google Gemini API usage through an AI Studio API key. Gemini app subscriptions are separate."; } }
        public bool IsConfigured(out string reason)
        {
            reason = string.IsNullOrWhiteSpace(BridgeSettings.Secrets.GeminiApiKey) ? "Add a Gemini API key in Settings, then Save & test." : null;
            return reason == null;
        }
        Dictionary<string, string> Headers() { return new Dictionary<string, string> { { "x-goog-api-key", BridgeSettings.Secrets.GeminiApiKey } }; }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken token)
        {
            string reason; if (!IsConfigured(out reason)) throw new BridgeException(reason);
            var models = new List<ModelInfo>(); var seen = new HashSet<string>(); string cursor = null;
            var cursors = new HashSet<string>();
            do
            {
                var page = await StreamingHttp.GetJsonAsync(BridgeSettings.Current.GeminiBaseUrl.TrimEnd('/') + "/models?pageSize=100" + (cursor == null ? "" : "&pageToken=" + Uri.EscapeDataString(cursor)), Headers(), 30, token).ConfigureAwait(false);
                if (page["models"].IsArray) foreach (var model in page["models"].ArrayValue)
                {
                    bool canGenerate = false;
                    if (model["supportedGenerationMethods"].IsArray) foreach (var method in model["supportedGenerationMethods"].ArrayValue)
                        canGenerate |= method.AsString() == "generateContent" || method.AsString() == "streamGenerateContent";
                    string id = model["name"].AsString(""); if (id.StartsWith("models/", StringComparison.Ordinal)) id = id.Substring(7);
                    if (canGenerate && !string.IsNullOrEmpty(id) && seen.Add(id)) models.Add(new ModelInfo { Id = id, DisplayName = model["displayName"].AsString(id), ContextWindow = model["inputTokenLimit"].AsInt(), MaxOutputTokens = model["outputTokenLimit"].AsInt() });
                }
                cursor = page["nextPageToken"].AsString();
                if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor)) break;
            } while (true);
            return models;
        }

        public async Task<string> TestConnectionAsync(CancellationToken token)
        { return "Gemini API key accepted · " + (await GetModelsAsync(token).ConfigureAwait(false)).Count + " chat models available."; }

        static void AddContent(JsonValue contents, string role, JsonValue parts)
        {
            if (contents.Count > 0 && contents[contents.Count - 1]["role"].AsString() == role)
                foreach (var part in parts.ArrayValue) contents[contents.Count - 1]["parts"].Add(part);
            else contents.Add(JsonValue.Obj("role", role, "parts", parts));
        }

        internal static JsonValue Contents(List<LlmMessage> transcript)
        {
            var contents = JsonValue.NewArray();
            foreach (var message in transcript)
            {
                if (message == null) continue;
                var parts = JsonValue.NewArray();
                if (message.Role == "tool")
                {
                    var result = JsonValue.Obj("name", message.Name, "response", JsonValue.Obj("result", message.Content ?? ""));
                    if (!string.IsNullOrEmpty(message.ToolCallId) && !message.ToolCallId.StartsWith("local_gemini_", StringComparison.Ordinal)) result["id"] = message.ToolCallId;
                    parts.Add(JsonValue.Obj("functionResponse", result));
                    AddContent(contents, "user", parts); continue;
                }
                JsonValue raw; string error;
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.RawItemsJson) && JsonValue.TryParse(message.RawItemsJson, out raw, out error) && raw["provider"].AsString() == "gemini" && raw["parts"].IsArray)
                    parts = JsonValue.Parse(raw["parts"].ToJson(false));
                else
                {
                    if (!string.IsNullOrEmpty(message.Content)) parts.Add(JsonValue.Obj("text", message.Content));
                    if (message.Role == "assistant" && message.ToolCalls != null) foreach (var call in message.ToolCalls)
                    {
                        var function = JsonValue.Obj("name", call.Name, "args", JsonValue.Parse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments));
                        if (!string.IsNullOrEmpty(call.Id) && !call.Id.StartsWith("local_gemini_", StringComparison.Ordinal)) function["id"] = call.Id;
                        parts.Add(JsonValue.Obj("functionCall", function));
                    }
                }
                if (parts.Count > 0) AddContent(contents, message.Role == "assistant" ? "model" : "user", parts);
            }
            return contents;
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken token)
        {
            string reason; if (!IsConfigured(out reason)) throw new BridgeException(reason);
            string model = request.Model ?? "";
            if (model.StartsWith("models/", StringComparison.Ordinal)) model = model.Substring(7);
            if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9_.-]+$")) throw new BridgeException("Choose a valid Gemini model from the account catalog.");
            var config = JsonValue.NewObject();
            if (request.MaxOutputTokens > 0) config["maxOutputTokens"] = request.MaxOutputTokens;
            if (request.Temperature >= 0) config["temperature"] = request.Temperature;
            if (request.Thinking && System.Text.RegularExpressions.Regex.IsMatch(model, @"^gemini-(?:2\.5|[3-9])")) config["thinkingConfig"] = JsonValue.Obj("includeThoughts", true);
            var body = JsonValue.Obj("contents", Contents(request.Messages), "generationConfig", config);
            if (!string.IsNullOrEmpty(request.Instructions)) body["systemInstruction"] = JsonValue.Obj("parts", JsonValue.Arr(JsonValue.Obj("text", request.Instructions)));
            if (request.Tools != null && request.Tools.Count > 0)
            {
                var declarations = JsonValue.NewArray();
                foreach (var tool in request.Tools) declarations.Add(JsonValue.Obj("name", tool.Name, "description", tool.Description, "parametersJsonSchema", tool.Schema));
                body["tools"] = JsonValue.Arr(JsonValue.Obj("functionDeclarations", declarations));
            }
            var text = new StringBuilder(); var thinking = new StringBuilder();
            var parts = JsonValue.NewArray(); var calls = new List<ToolCall>();
            var usage = new TokenUsage { Model = model, CostText = "Gemini API (Google billing)" };
            string finish = null;
            await StreamingHttp.PostSseAsync(BridgeSettings.Current.GeminiBaseUrl.TrimEnd('/') + "/models/" + Uri.EscapeDataString(model) + ":streamGenerateContent?alt=sse", body.ToJson(false), Headers(), BridgeSettings.Current.RequestTimeoutSeconds, data =>
            {
                var chunk = JsonValue.Parse(data);
                if (chunk["error"].IsObject) throw new BridgeException("Gemini: " + FileService.RedactSecrets(chunk["error"]["message"].AsString("Stream failed.")));
                string blocked = chunk["promptFeedback"]["blockReason"].AsString();
                if (!string.IsNullOrEmpty(blocked)) throw new BridgeException("Gemini blocked this prompt: " + blocked);
                if (chunk["candidates"].IsArray && chunk["candidates"].Count > 0)
                {
                    var candidate = chunk["candidates"][0];
                    if (candidate["content"]["parts"].IsArray) foreach (var part in candidate["content"]["parts"].ArrayValue)
                    {
                        parts.Add(part);
                        string delta = part["text"].AsString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            if (part["thought"].AsBool()) { thinking.Append(delta); onEvent(StreamEvent.Reasoning(delta)); }
                            else { text.Append(delta); onEvent(StreamEvent.Delta(delta)); }
                        }
                        if (part["functionCall"].IsObject)
                        {
                            var function = part["functionCall"];
                            if (!function["args"].IsObject) throw new BridgeException("Gemini returned invalid function arguments.");
                            calls.Add(new ToolCall { Id = function["id"].AsString("local_gemini_" + Guid.NewGuid().ToString("N")), Name = function["name"].AsString(), Arguments = function["args"].ToJson(false) });
                        }
                    }
                    if (!candidate["finishReason"].IsNull) finish = candidate["finishReason"].AsString();
                }
                if (chunk["usageMetadata"].IsObject)
                {
                    var u = chunk["usageMetadata"];
                    usage.PromptTokens = u["promptTokenCount"].AsInt(); usage.ReasoningTokens = u["thoughtsTokenCount"].AsInt();
                    usage.CompletionTokens = u["candidatesTokenCount"].AsInt() + usage.ReasoningTokens;
                    usage.TotalTokens = u["totalTokenCount"].AsInt(usage.PromptTokens + usage.CompletionTokens);
                    usage.CachedTokens = u["cachedContentTokenCount"].AsInt();
                }
            }, token).ConfigureAwait(false);
            if (finish != "STOP") throw new BridgeException("Gemini did not finish successfully: " + (finish ?? "stream ended without a finish reason") + ". Retry or adjust the output limit.");
            return new ProviderTurn { Text = text.ToString(), Reasoning = thinking.ToString(), ToolCalls = calls, Usage = usage, StopReason = finish, RawItemsJson = JsonValue.Obj("provider", "gemini", "parts", parts).ToJson(false) };
        }
    }
}
