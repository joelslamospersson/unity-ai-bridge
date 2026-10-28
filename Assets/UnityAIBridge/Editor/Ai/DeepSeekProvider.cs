using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>DeepSeek API provider (OpenAI-compatible chat completions, streaming tools).</summary>
    public sealed class DeepSeekProvider : ILiveProvider
    {
        sealed class Rates
        {
            public double CacheHit;
            public double CacheMiss;
            public double Output;
        }

        static readonly Dictionary<string, Rates> PriceTable = new Dictionary<string, Rates>(StringComparer.OrdinalIgnoreCase)
        {
            { "deepseek-flash", new Rates { CacheHit = 0.003, CacheMiss = 0.15, Output = 0.60 } },
            { "deepseek-v4-pro", new Rates { CacheHit = 0.022, CacheMiss = 0.66, Output = 1.98 } }
        };

        public string Id { get { return BridgeSettings.ProviderDeepSeek; } }
        public string DisplayName { get { return "DeepSeek"; } }
        public string BillingNote { get { return "DeepSeek API billing (per token). Enter a DeepSeek API key from platform.deepseek.com."; } }

        public bool IsConfigured(out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
            {
                reason = "Add a DeepSeek API key in Settings (platform.deepseek.com/api_keys).";
                return false;
            }
            if (string.IsNullOrEmpty(BridgeSettings.Current.DeepSeekBaseUrl))
            {
                reason = "Set the DeepSeek base URL.";
                return false;
            }
            return true;
        }

        public static List<ModelInfo> VerifiedModels()
        {
            return new List<ModelInfo>
            {
                new ModelInfo
                {
                    Id = "deepseek-flash",
                    DisplayName = "DeepSeek V4.1 Flash",
                    ContextWindow = 1048576,
                    MaxOutputTokens = 393216,
                    EffortLevels = new List<string> { "low", "high", "max" }
                },
                new ModelInfo
                {
                    Id = "deepseek-v4-pro",
                    DisplayName = "DeepSeek V4 Pro",
                    ContextWindow = 1048576,
                    MaxOutputTokens = 393216,
                    EffortLevels = new List<string> { "low", "high", "max" }
                }
            };
        }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
        {
            var settings = BridgeSettings.Current;
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);

            var headers = AuthHeaders();
            var json = await StreamingHttp.GetJsonAsync(BaseUrl() + "/models", headers, 30, cancellationToken).ConfigureAwait(false);
            var result = new List<ModelInfo>();
            if (json["data"].IsArray)
            {
                foreach (var model in json["data"].ArrayValue)
                {
                    var info = new ModelInfo
                    {
                        Id = model["id"].AsString(),
                        DisplayName = model["name"].AsString(model["id"].AsString()),
                        ContextWindow = model["context_window"].AsInt(),
                        MaxOutputTokens = model["max_output_tokens"].AsInt()
                    };
                    if (model["effort"]["supported_levels"].IsArray)
                        foreach (var level in model["effort"]["supported_levels"].ArrayValue)
                            info.EffortLevels.Add(level.AsString());
                    if (!string.IsNullOrEmpty(info.Id)) result.Add(info);
                }
            }
            if (result.Count == 0) result = VerifiedModels();
            return result;
        }

        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var names = new List<string>();
            foreach (var model in models) names.Add(model.Id);
            return "DeepSeek connected. Models: " + string.Join(", ", names.ToArray());
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            var settings = BridgeSettings.Current;
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);

            var messages = JsonValue.NewArray();
            if (!string.IsNullOrEmpty(request.Instructions))
                messages.Add(JsonValue.Obj("role", "system", "content", request.Instructions));

            foreach (var message in request.Messages)
            {
                if (message.Role == "tool")
                {
                    messages.Add(JsonValue.Obj(
                        "role", "tool",
                        "tool_call_id", message.ToolCallId ?? string.Empty,
                        "content", message.Content ?? string.Empty));
                    continue;
                }
                if (message.Role == "assistant")
                {
                    var entry = JsonValue.Obj("role", "assistant", "content", message.Content ?? string.Empty);
                    if (!string.IsNullOrEmpty(message.Reasoning))
                        entry["reasoning_content"] = message.Reasoning;
                    if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                    {
                        var calls = JsonValue.NewArray();
                        foreach (var call in message.ToolCalls)
                        {
                            calls.Add(JsonValue.Obj(
                                "id", call.Id,
                                "type", "function",
                                "function", JsonValue.Obj("name", call.Name, "arguments", call.Arguments ?? "{}")));
                        }
                        entry["tool_calls"] = calls;
                    }
                    messages.Add(entry);
                    continue;
                }
                messages.Add(JsonValue.Obj("role", "user", "content", message.Content ?? string.Empty));
            }

            var body = JsonValue.Obj(
                "model", request.Model,
                "messages", messages,
                "stream", true,
                "stream_options", JsonValue.Obj("include_usage", true));

            if (request.MaxOutputTokens > 0) body["max_tokens"] = request.MaxOutputTokens;

            if (request.Thinking)
            {
                body["thinking"] = JsonValue.Obj("type", "enabled");
                body["reasoning_effort"] = string.IsNullOrEmpty(request.ReasoningEffort) ? "high" : request.ReasoningEffort;
            }
            else
            {
                body["thinking"] = JsonValue.Obj("type", "disabled");
                if (request.Temperature >= 0) body["temperature"] = request.Temperature;
            }

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = JsonValue.NewArray();
                foreach (var tool in request.Tools)
                {
                    tools.Add(JsonValue.Obj(
                        "type", "function",
                        "function", JsonValue.Obj(
                            "name", tool.Name,
                            "description", tool.Description,
                            "parameters", tool.Schema ?? JsonValue.Obj("type", "object", "properties", JsonValue.NewObject()))));
                }
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }

            var headers = AuthHeaders();
            onEvent(StreamEvent.Status("Contacting DeepSeek (" + request.Model + ")..."));

            var text = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            var toolCalls = new Dictionary<int, ToolCall>();
            TokenUsage usage = null;
            string finishReason = null;

            string url = BaseUrl() + "/chat/completions";
            try
            {
                await StreamingHttp.PostSseAsync(url, body.ToJson(false), headers, settings.RequestTimeoutSeconds, data =>
                {
                    if (data == "[DONE]") return;
                    JsonValue chunk;
                    string parseError;
                    if (!JsonValue.TryParse(data, out chunk, out parseError)) return;

                    if (chunk["error"].IsObject || chunk["error"].IsString)
                    {
                        string message = chunk["error"]["message"].AsString(chunk["error"].AsString("DeepSeek error."));
                        throw new BridgeException("DeepSeek error: " + message);
                    }

                    var choice = chunk["choices"][0];
                    var delta = choice["delta"];

                    string reasoningDelta = delta["reasoning_content"].AsString();
                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        reasoning.Append(reasoningDelta);
                        onEvent(StreamEvent.Reasoning(reasoningDelta));
                    }

                    string contentDelta = delta["content"].AsString();
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        text.Append(contentDelta);
                        onEvent(StreamEvent.Delta(contentDelta));
                    }

                    if (delta["tool_calls"].IsArray)
                    {
                        foreach (var part in delta["tool_calls"].ArrayValue)
                        {
                            int index = part["index"].AsInt(0);
                            ToolCall call;
                            if (!toolCalls.TryGetValue(index, out call))
                            {
                                call = new ToolCall { Id = part["id"].AsString(), Name = part["function"]["name"].AsString(), Arguments = string.Empty };
                                toolCalls[index] = call;
                                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = call.Clone(), Text = call.Name });
                            }
                            else
                            {
                                string name = part["function"]["name"].AsString();
                                if (!string.IsNullOrEmpty(name)) call.Name = name;
                                string id = part["id"].AsString();
                                if (!string.IsNullOrEmpty(id)) call.Id = id;
                            }
                            string args = part["function"]["arguments"].AsString();
                            if (!string.IsNullOrEmpty(args))
                            {
                                call.Arguments += args;
                                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallArguments, ToolCall = call.Clone(), Text = args });
                            }
                        }
                    }

                    string finish = choice["finish_reason"].AsString();
                    if (!string.IsNullOrEmpty(finish)) finishReason = finish;

                    if (chunk["usage"].IsObject) usage = ParseUsage(chunk["usage"], request.Model);
                }, cancellationToken, null, () => onEvent(new StreamEvent { Kind = StreamEventKind.TransportActivity })).ConfigureAwait(false);
            }
            catch (HttpStatusException e)
            {
                throw new BridgeException(FriendlyHttpError(e), e);
            }

            var turn = new ProviderTurn
            {
                Text = text.ToString(),
                Reasoning = reasoning.ToString(),
                StopReason = finishReason
            };

            var ordered = new List<int>(toolCalls.Keys);
            ordered.Sort();
            foreach (var index in ordered)
            {
                var call = toolCalls[index];
                turn.ToolCalls.Add(call);
                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = call.Clone() });
            }

            turn.Usage = usage ?? new TokenUsage { Model = request.Model };
            if (string.IsNullOrEmpty(turn.Usage.CostText) && usage != null)
                turn.Usage.CostText = EstimateCost(usage, request.Model);
            onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = turn.Usage });
            return turn;
        }

        Dictionary<string, string> AuthHeaders()
        {
            return new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + BridgeSettings.Secrets.DeepSeekApiKey }
            };
        }

        static string BaseUrl()
        {
            return BridgeSettings.Current.DeepSeekBaseUrl.TrimEnd('/');
        }

        static TokenUsage ParseUsage(JsonValue usage, string model)
        {
            var result = new TokenUsage
            {
                Model = model,
                PromptTokens = usage["prompt_tokens"].AsInt(),
                CompletionTokens = usage["completion_tokens"].AsInt(),
                TotalTokens = usage["total_tokens"].AsInt(),
                CachedTokens = usage["prompt_cache_hit_tokens"].AsInt(usage["prompt_tokens_details"]["cached_tokens"].AsInt()),
                ReasoningTokens = usage["completion_tokens_details"]["reasoning_tokens"].AsInt()
            };
            if (result.TotalTokens == 0) result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }

        public static string EstimateCost(TokenUsage usage, string model)
        {
            if (usage == null) return null;
            Rates rates;
            if (!PriceTable.TryGetValue(model ?? string.Empty, out rates)) rates = PriceTable["deepseek-flash"];
            int cacheHit = Math.Max(0, usage.CachedTokens);
            int cacheMiss = Math.Max(0, usage.PromptTokens - cacheHit);
            double cost = (cacheHit * rates.CacheHit + cacheMiss * rates.CacheMiss + usage.CompletionTokens * rates.Output) / 1000000.0;
            bool peak = IsPeakUtc(DateTime.UtcNow);
            if (peak) cost *= 2.0;
            string shortName = string.Equals(model, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase) ? "Pro" : "Flash";
            return "$" + cost.ToString("0.000000") + " (" + shortName + ", " + (peak ? "peak" : "off-peak") + " estimate)";
        }

        public static bool IsPeakUtc(DateTime utc)
        {
            if (utc.DayOfWeek == DayOfWeek.Saturday || utc.DayOfWeek == DayOfWeek.Sunday) return false;
            int hour = utc.Hour;
            return (hour >= 1 && hour < 4) || (hour >= 6 && hour < 10);
        }

        static string FriendlyHttpError(HttpStatusException e)
        {
            switch (e.StatusCode)
            {
                case 401: return "DeepSeek rejected the API key. Check it in Settings. If this is an OpenCode Go key, switch to the OpenCode Go provider. (" + e.Message + ")";
                case 402: return "DeepSeek account has insufficient balance. Top up at platform.deepseek.com. (" + e.Message + ")";
                case 429: return "DeepSeek rate limit reached. Wait a moment and retry. (" + e.Message + ")";
                case 400: return "DeepSeek rejected the request (check model/thinking/tool settings). " + e.Message;
                default: return e.Message;
            }
        }
    }
}
