using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>
    /// OpenCode Go provider. OpenCode Go exposes an OpenAI-compatible chat
    /// completions endpoint for coding models; the endpoint is configurable.
    /// </summary>
    public sealed class OpenCodeGoProvider : ILiveProvider
    {
        const string PrimaryBase = "https://opencode.ai/zen/go/v1";
        const string AlternateBase = "https://opencode.ai/inference/go/openai/v1";
        static string _resolvedBase;

        public string Id { get { return BridgeSettings.ProviderOpenCode; } }
        public string DisplayName { get { return "OpenCode Go"; } }
        public string BillingNote { get { return "OpenCode Go subscription/usage. Requires an OpenCode Go API key from opencode.ai/auth."; } }

        public bool IsConfigured(out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(BridgeSettings.Secrets.OpenCodeApiKey))
            {
                reason = "Add an OpenCode Go API key (opencode.ai/auth). If you saved it under DeepSeek, use 'Reuse saved DeepSeek key' in Settings.";
                return false;
            }
            if (string.IsNullOrEmpty(BaseUrl()))
            {
                reason = "Set the OpenCode Go base URL.";
                return false;
            }
            return true;
        }

        public static List<ModelInfo> VerifiedModels()
        {
            var ids = new[]
            {
                "deepseek-v4.1-flash", "deepseek-v4-pro", "deepseek-v4-flash",
                "kimi-k3", "kimi-k2.7-code", "kimi-k2.6",
                "glm-5.3", "glm-5.2", "glm-5.3-flash",
                "mimo-v2.6-flash", "mimo-v2.6-pro", "longcat-2.0", "hy3"
            };
            var result = new List<ModelInfo>();
            foreach (var id in ids) result.Add(new ModelInfo { Id = id, DisplayName = id });
            return result;
        }

        public static void ResetResolvedBase() { _resolvedBase = null; }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
        {
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);

            BridgeException last = null;
            foreach (var candidate in CandidateBases())
            {
                try
                {
                    var json = await StreamingHttp.GetJsonAsync(ModelsBase(candidate) + "/models", AuthHeaders(), 30, cancellationToken).ConfigureAwait(false);
                    var models = ParseModels(json);
                    if (models.Count == 0) continue;
                    _resolvedBase = candidate;
                    return models;
                }
                catch (HttpStatusException e)
                {
                    if (e.StatusCode == 404 || e.StatusCode == 405) { last = e; continue; }
                    throw new BridgeException(FriendlyHttpError(e), e);
                }
                catch (BridgeException e)
                {
                    last = e;
                }
            }
            if (last != null) throw last;
            return VerifiedModels();
        }

        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var names = new List<string>();
            foreach (var model in models) names.Add(model.Id);
            return "OpenCode Go connected. Models: " + string.Join(", ", names.ToArray());
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);

            string baseUrl = BaseUrl();
            try
            {
                baseUrl = await ResolveBaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeException e)
            {
                // The model list can be unavailable while chat still works; let the chat
                // request surface the authoritative error.
                if (e is HttpStatusException && ((HttpStatusException)e).StatusCode == 401) throw;
            }

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
                    if (!string.IsNullOrEmpty(message.Reasoning) &&
                        (request.Model ?? string.Empty).StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase))
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

            var settings = BridgeSettings.Current;
            var body = JsonValue.Obj(
                "model", request.Model,
                "messages", messages,
                "stream", true,
                "stream_options", JsonValue.Obj("include_usage", true));
            if (request.MaxOutputTokens > 0) body["max_tokens"] = request.MaxOutputTokens;
            if (request.Temperature >= 0) body["temperature"] = request.Temperature;

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

            onEvent(StreamEvent.Status("Contacting OpenCode Go (" + request.Model + ")..."));

            var chatHeaders = AuthHeaders(request.SessionId);
            var text = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            var toolCalls = new Dictionary<int, ToolCall>();
            TokenUsage usage = null;
            string finishReason = null;

            try
            {
                await StreamingHttp.PostSseAsync(baseUrl + "/chat/completions", body.ToJson(false), chatHeaders,
                    settings.RequestTimeoutSeconds, data =>
                    {
                        if (data == "[DONE]") return;
                        JsonValue chunk;
                        string parseError;
                        if (!JsonValue.TryParse(data, out chunk, out parseError)) return;

                        if (chunk["error"].IsObject || chunk["error"].IsString)
                        {
                            string message = chunk["error"]["message"].AsString(chunk["error"].AsString("OpenCode Go error."));
                            throw new BridgeException("OpenCode Go error: " + message);
                        }

                        var choice = chunk["choices"][0];
                        var delta = choice["delta"];

                        string reasoningDelta = delta["reasoning_content"].AsString(delta["reasoning"].AsString());
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
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpStatusException e)
            {
                throw new BridgeException(FriendlyHttpError(e), e);
            }

            var turn = new ProviderTurn
            {
                Text = text.ToString(),
                Reasoning = reasoning.ToString(),
                StopReason = finishReason,
                Usage = usage ?? new TokenUsage { Model = request.Model }
            };
            var ordered = new List<int>(toolCalls.Keys);
            ordered.Sort();
            foreach (var index in ordered)
            {
                var call = toolCalls[index];
                turn.ToolCalls.Add(call);
                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = call.Clone() });
            }
            if (string.IsNullOrEmpty(turn.Usage.CostText)) turn.Usage.CostText = "OpenCode Go plan";
            onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = turn.Usage });
            return turn;
        }

        async Task<string> ResolveBaseAsync(CancellationToken cancellationToken)
        {
            var candidates = CandidateBases();
            if (!string.IsNullOrEmpty(_resolvedBase) && candidates.Contains(_resolvedBase)) return _resolvedBase;
            BridgeException last = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var json = await StreamingHttp.GetJsonAsync(ModelsBase(candidate) + "/models", AuthHeaders(), 20, cancellationToken).ConfigureAwait(false);
                    if (ParseModels(json).Count > 0)
                    {
                        _resolvedBase = candidate;
                        return candidate;
                    }
                }
                catch (HttpStatusException e)
                {
                    if (e.StatusCode == 401 || e.StatusCode == 403) throw new BridgeException(FriendlyHttpError(e), e);
                    last = e;
                }
                catch (BridgeException e) { last = e; }
            }
            if (last != null) throw last;
            return BaseUrl();
        }

        static List<ModelInfo> ParseModels(JsonValue json)
        {
            var result = new List<ModelInfo>();
            var array = json["data"].IsArray ? json["data"] : json["models"];
            if (!array.IsArray) return result;
            foreach (var model in array.ArrayValue)
            {
                string id = model["id"].AsString(model["slug"].AsString(model["model"].AsString()));
                if (string.IsNullOrEmpty(id)) continue;
                var info = new ModelInfo
                {
                    Id = id,
                    DisplayName = model["name"].AsString(model["display_name"].AsString(id)),
                    ContextWindow = model["context_window"].AsInt(),
                    MaxOutputTokens = model["max_output_tokens"].AsInt()
                };
                if (model["effort"]["supported_levels"].IsArray)
                    foreach (var level in model["effort"]["supported_levels"].ArrayValue) info.EffortLevels.Add(level.AsString());
                result.Add(info);
            }
            return result;
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
            var opencode = usage["opencode"];
            if (opencode.IsObject)
            {
                double allowance = opencode["allowance"].AsNumber();
                string source = opencode["billing_source"].AsString();
                string text = "OpenCode Go";
                if (allowance > 0) text += " · allowance $" + allowance.ToString("0.000000");
                if (!string.IsNullOrEmpty(source)) text += " (" + source + ")";
                result.CostText = text;
            }
            return result;
        }

        Dictionary<string, string> AuthHeaders(string sessionId = null)
        {
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + BridgeSettings.Secrets.OpenCodeApiKey }
            };
            if (!string.IsNullOrEmpty(sessionId)) headers["x-opencode-session"] = sessionId;
            return headers;
        }

        static string BaseUrl()
        {
            string value = BridgeSettings.Current.OpenCodeBaseUrl;
            if (string.IsNullOrEmpty(value)) value = PrimaryBase;
            return value.TrimEnd('/');
        }

        static string ModelsBase(string baseUrl)
        {
            // The v2 inference chat endpoint is /inference/go/openai/v1, while its
            // model catalog is /inference/go/v1/models.
            if (baseUrl.EndsWith("/inference/go/openai/v1", StringComparison.OrdinalIgnoreCase))
                return baseUrl.Substring(0, baseUrl.Length - "/openai/v1".Length) + "/v1";
            return baseUrl;
        }

        static List<string> CandidateBases()
        {
            string configured = BaseUrl();
            var result = new List<string>();
            if (!string.Equals(configured, PrimaryBase, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(configured);
                return result;
            }
            result.Add(PrimaryBase);
            result.Add(AlternateBase);
            return result;
        }

        static string FriendlyHttpError(HttpStatusException e)
        {
            switch (e.StatusCode)
            {
                case 401:
                case 403:
                    return "OpenCode Go rejected the API key. Create/copy a key at opencode.ai/auth. If this is a DeepSeek API key, switch to the DeepSeek provider. (" + e.Message + ")";
                case 402:
                    return "OpenCode Go credits/usage are unavailable. Check your OpenCode plan. (" + e.Message + ")";
                case 404:
                    return "OpenCode Go endpoint or model not found. Verify the base URL and model; some models use a Responses endpoint and are not available through this chat-completions provider. (" + e.Message + ")";
                case 429:
                    return "OpenCode Go rate/usage limit reached. Wait or check your OpenCode plan. (" + e.Message + ")";
                default:
                    return e.Message;
            }
        }
    }
}
