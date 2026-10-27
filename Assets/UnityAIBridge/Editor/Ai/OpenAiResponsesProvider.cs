using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>
    /// OpenAI Responses API provider. Used by both the API-key mode (top-level tools)
    /// and Sign in with ChatGPT (OAuth, tools supplied through an additional_tools item,
    /// store:false, stream:true, no temperature/max_output_tokens).
    /// </summary>
    public sealed class OpenAiResponsesProvider : ILiveProvider
    {
        readonly bool _chatGpt;

        public OpenAiResponsesProvider(bool chatGpt)
        {
            _chatGpt = chatGpt;
        }

        public string Id { get { return _chatGpt ? BridgeSettings.ProviderChatGpt : BridgeSettings.ProviderOpenAi; } }
        public string DisplayName { get { return _chatGpt ? "ChatGPT Plus / Pro" : "OpenAI API"; } }

        public string BillingNote
        {
            get
            {
                return _chatGpt
                    ? "Direct ChatGPT model access using your subscription. OpenAI applies plan and app usage limits; review them with Manage usage."
                    : "Requires a platform.openai.com API key and is billed separately by OpenAI. ChatGPT Plus is not API access.";
            }
        }

        public bool IsConfigured(out string reason)
        {
            reason = null;
            if (_chatGpt)
            {
                if (ChatGptAuth.Status == ChatGptStatus.SigningIn)
                {
                    reason = "ChatGPT sign-in is in progress in your browser.";
                    return false;
                }
                if (!ChatGptAuth.IsReadyForInference)
                {
                    if (ChatGptAuth.Status == ChatGptStatus.PlanUsageUnavailable)
                        reason = "Signed in, but ChatGPT plan usage was not granted. Reconnect and allow plan usage, or use the OpenAI API provider.";
                    else if (ChatGptAuth.Status == ChatGptStatus.SignedOut || ChatGptAuth.Status == ChatGptStatus.NotSignedIn)
                        reason = "Sign in with ChatGPT to use your plan. No API key is required.";
                    else if (ChatGptAuth.Status == ChatGptStatus.Error)
                        reason = string.IsNullOrEmpty(ChatGptAuth.LastError) ? "ChatGPT session error. Sign in again." : ChatGptAuth.LastError;
                    else reason = "ChatGPT is not connected.";
                    return false;
                }
                return true;
            }
            if (string.IsNullOrEmpty(BridgeSettings.Secrets.OpenAiApiKey))
            {
                reason = "Add an OpenAI API key in Settings (platform.openai.com). This is separate from a ChatGPT Plus subscription.";
                return false;
            }
            return true;
        }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
        {
            var result = new List<ModelInfo>();
            if (_chatGpt)
            {
                var json = await ChatGptAuth.GetModelsAsync(cancellationToken).ConfigureAwait(false);
                return ParseChatGptModels(json);
            }

            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + BridgeSettings.Secrets.OpenAiApiKey } };
            var models = await StreamingHttp.GetJsonAsync(ProviderBaseUrl() + "/models", headers, 30, cancellationToken).ConfigureAwait(false);
            if (models["data"].IsArray)
            {
                foreach (var model in models["data"].ArrayValue)
                {
                    string id = model["id"].AsString();
                    if (IsTextModel(id)) result.Add(new ModelInfo { Id = id, DisplayName = model["name"].AsString(id) });
                }
            }
            return result;
        }

        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (_chatGpt)
                return "ChatGPT connected as " + ChatGptAuth.AccountLabel + " · " + models.Count + " model(s) available. Plan usage is enabled.";
            return "OpenAI API key accepted · " + models.Count + " model(s) available.";
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            if (_chatGpt && IsCodexModel(request.Model))
                throw new BridgeException("Choose a ChatGPT chat model. Codex models belong to a separate Codex connection.");
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);
            try
            {
                return await StreamOnceAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpStatusException e)
            {
                if (_chatGpt && e.StatusCode == 401)
                {
                    await ChatGptAuth.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
                    return await StreamOnceAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
                }
                throw new BridgeException(FriendlyHttpError(e), e);
            }
        }

        async Task<ProviderTurn> StreamOnceAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            var settings = BridgeSettings.Current;
            string accessToken;
            if (_chatGpt)
                accessToken = await ChatGptAuth.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            else
                accessToken = BridgeSettings.Secrets.OpenAiApiKey;

            var input = JsonValue.NewArray();
            if (_chatGpt && request.Tools != null && request.Tools.Count > 0)
            {
                input.Add(JsonValue.Obj(
                    "type", "additional_tools",
                    "role", "developer",
                    "tools", BuildToolDefinitions(request.Tools, false)));
            }

            foreach (var message in request.Messages) AppendMessage(input, message);

            var body = JsonValue.Obj(
                "model", request.Model,
                "input", input,
                "store", false,
                "stream", true);
            if (!string.IsNullOrEmpty(request.Instructions)) body["instructions"] = request.Instructions;

            if (!_chatGpt)
            {
                if (request.MaxOutputTokens > 0) body["max_output_tokens"] = request.MaxOutputTokens;
                if (request.Temperature >= 0 && SupportsTemperature(request.Model)) body["temperature"] = request.Temperature;
                if (request.Tools != null && request.Tools.Count > 0)
                    body["tools"] = BuildToolDefinitions(request.Tools, true);
            }

            if (SupportsReasoning(request.Model))
            {
                var options = JsonValue.Obj("summary", "auto");
                if (request.Thinking && !string.IsNullOrEmpty(request.ReasoningEffort) && request.ReasoningEffort != "default")
                    options["effort"] = request.ReasoningEffort;
                body["reasoning"] = options;
                // Preserve encrypted reasoning items between stateless tool turns.
                body["include"] = JsonValue.Arr("reasoning.encrypted_content");
            }

            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + accessToken } };
            onEvent(StreamEvent.Status((_chatGpt ? "Contacting ChatGPT" : "Contacting OpenAI API") + " (" + request.Model + ")..."));

            var text = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            var calls = new List<ToolCall>();
            var callsByItem = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
            var rawItems = new List<JsonValue>();
            TokenUsage usage = null;
            string stopReason = null;
            string streamError = null;

            await StreamingHttp.PostSseAsync(ProviderBaseUrl() + "/responses", body.ToJson(false), headers, settings.RequestTimeoutSeconds, data =>
            {
                JsonValue evt;
                string parseError;
                if (!JsonValue.TryParse(data, out evt, out parseError)) return;
                string type = evt["type"].AsString();

                switch (type)
                {
                    case "response.output_text.delta":
                        {
                            string delta = evt["delta"].AsString();
                            if (!string.IsNullOrEmpty(delta)) { text.Append(delta); onEvent(StreamEvent.Delta(delta)); }
                            break;
                        }
                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta":
                        {
                            string delta = evt["delta"].AsString();
                            if (!string.IsNullOrEmpty(delta)) { reasoning.Append(delta); onEvent(StreamEvent.Reasoning(delta)); }
                            break;
                        }
                    case "response.output_item.added":
                        {
                            var item = evt["item"];
                            if (item["type"].AsString() == "function_call")
                            {
                                string key = item["id"].AsString(item["call_id"].AsString());
                                var call = new ToolCall
                                {
                                    Id = item["call_id"].AsString(item["id"].AsString()),
                                    Name = item["name"].AsString(),
                                    Arguments = item["arguments"].AsString(string.Empty)
                                };
                                if (!string.IsNullOrEmpty(key)) callsByItem[key] = call;
                                calls.Add(call);
                                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = call.Clone(), Text = call.Name });
                            }
                            break;
                        }
                    case "response.function_call_arguments.delta":
                        {
                            string key = evt["item_id"].AsString();
                            ToolCall call;
                            if (callsByItem.TryGetValue(key, out call))
                            {
                                string delta = evt["delta"].AsString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    call.Arguments += delta;
                                    onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallArguments, ToolCall = call.Clone(), Text = delta });
                                }
                            }
                            break;
                        }
                    case "response.function_call_arguments.done":
                        {
                            string key = evt["item_id"].AsString();
                            ToolCall call;
                            if (callsByItem.TryGetValue(key, out call))
                            {
                                string args = evt["arguments"].AsString();
                                if (!string.IsNullOrEmpty(args)) call.Arguments = args;
                            }
                            break;
                        }
                    case "response.output_item.done":
                        {
                            var item = evt["item"];
                            if (item.IsObject && !string.IsNullOrEmpty(item["id"].AsString()))
                                AddRawItem(rawItems, item);
                            if (item["type"].AsString() == "function_call")
                            {
                                string key = item["id"].AsString(item["call_id"].AsString());
                                ToolCall call;
                                if (!callsByItem.TryGetValue(key, out call))
                                {
                                    call = new ToolCall { Id = item["call_id"].AsString(item["id"].AsString()), Name = item["name"].AsString() };
                                    callsByItem[key] = call;
                                    calls.Add(call);
                                    onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = call.Clone(), Text = call.Name });
                                }
                                else
                                {
                                    string callId = item["call_id"].AsString();
                                    if (!string.IsNullOrEmpty(callId)) call.Id = callId;
                                    string name = item["name"].AsString();
                                    if (!string.IsNullOrEmpty(name)) call.Name = name;
                                }
                                string args = item["arguments"].AsString();
                                if (!string.IsNullOrEmpty(args)) call.Arguments = args;
                            }
                            break;
                        }
                    case "response.completed":
                        {
                            var response = evt["response"];
                            usage = ParseUsage(response["usage"], request.Model);
                            stopReason = response["status"].AsString("completed");
                            if (response["output"].IsArray)
                            {
                                foreach (var item in response["output"].ArrayValue)
                                    if (item.IsObject) AddRawItem(rawItems, item);
                            }
                            break;
                        }
                    case "response.failed":
                        {
                            var error = evt["response"]["error"];
                            streamError = error["message"].AsString(error["code"].AsString("OpenAI response failed."));
                            break;
                        }
                    case "response.incomplete":
                        {
                            streamError = "The response was incomplete: " + evt["response"]["incomplete_details"]["reason"].AsString("unknown reason");
                            break;
                        }
                    case "error":
                        {
                            streamError = evt["message"].AsString(evt["error"]["message"].AsString("OpenAI stream error."));
                            break;
                        }
                }
            }, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(streamError)) throw new BridgeException(streamError);
            if (stopReason != "completed")
                throw new BridgeException("The connection ended before the response completed. You can retry your message.");

            var turn = new ProviderTurn
            {
                Text = text.ToString(),
                Reasoning = reasoning.ToString(),
                StopReason = stopReason,
                Usage = usage ?? new TokenUsage { Model = request.Model },
                RawItemsJson = rawItems.Count > 0 ? JsonValue.Arr(rawItems.ToArray()).ToJson(false) : null
            };
            foreach (var call in calls) turn.ToolCalls.Add(call);
            if (usage != null)
                usage.CostText = _chatGpt ? "ChatGPT plan usage" : "OpenAI API (billed by OpenAI)";
            onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = turn.Usage });
            return turn;
        }

        internal static List<ModelInfo> ParseChatGptModels(JsonValue json)
        {
            var result = new List<ModelInfo>();
            var catalog = json["models"].IsArray ? json["models"] : json["data"];
            if (catalog.IsArray)
            {
                foreach (var model in catalog.ArrayValue)
                {
                    string visibility = model["visibility"].AsString();
                    if (!string.IsNullOrEmpty(visibility) && visibility != "list") continue;
                    string slug = model["slug"].AsString(model["id"].AsString());
                    if (string.IsNullOrEmpty(slug) || IsCodexModel(slug, model["display_name"].AsString())) continue;
                    result.Add(new ModelInfo { Id = slug, DisplayName = model["display_name"].AsString(slug),
                        ContextWindow = model["context_window"].AsInt(), MaxOutputTokens = model["max_output_tokens"].AsInt() });
                }
            }
            return result;
        }

        internal static bool IsCodexModel(string id, string displayName = null)
        {
            return (id ?? "").IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (displayName ?? "").IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string PreferredChatModel(List<ModelInfo> models)
        {
            if (models == null || models.Count == 0) return null;
            var gpt6 = models.Find(m => m != null && !IsCodexModel(m.Id, m.DisplayName) && (m.Id ?? "").StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase));
            if (gpt6 != null) return gpt6.Id;
            var first = models.Find(m => m != null && !IsCodexModel(m.Id, m.DisplayName));
            return first != null ? first.Id : null;
        }

        static bool IsTextModel(string model)
        {
            string id = (model ?? string.Empty).ToLowerInvariant();
            if (!(id.StartsWith("gpt-") || id.StartsWith("chatgpt-") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4"))) return false;
            foreach (var unsupported in new[] { "audio", "realtime", "transcribe", "tts", "image", "search", "instruct" })
                if (id.Contains(unsupported)) return false;
            return true;
        }

        public static bool SupportsReasoning(string model)
        {
            string id = (model ?? string.Empty).ToLowerInvariant();
            return id.StartsWith("gpt-5", StringComparison.Ordinal) || id.StartsWith("gpt-6", StringComparison.Ordinal) ||
                   id.StartsWith("o3", StringComparison.Ordinal) || id.StartsWith("o4", StringComparison.Ordinal);
        }

        static void AddRawItem(List<JsonValue> items, JsonValue item)
        {
            string id = item["id"].AsString();
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i]["id"].AsString(), id, StringComparison.Ordinal))
                {
                    items[i] = item;
                    return;
                }
            }
            items.Add(item);
        }

        static void AppendMessage(JsonValue input, LlmMessage message)
        {
            if (message.Role == "tool")
            {
                input.Add(JsonValue.Obj(
                    "type", "function_call_output",
                    "call_id", message.ToolCallId ?? string.Empty,
                    "output", message.Content ?? string.Empty));
                return;
            }

            if (message.Role == "assistant")
            {
                if (!string.IsNullOrEmpty(message.RawItemsJson))
                {
                    JsonValue items;
                    string error;
                    if (JsonValue.TryParse(message.RawItemsJson, out items, out error) && items.IsArray)
                    {
                        foreach (var item in items.ArrayValue) input.Add(item);
                        return;
                    }
                }
                if (!string.IsNullOrEmpty(message.Content))
                    input.Add(JsonValue.Obj("role", "assistant", "content", message.Content));
                if (message.ToolCalls != null)
                {
                    foreach (var call in message.ToolCalls)
                    {
                        input.Add(JsonValue.Obj(
                            "type", "function_call",
                            "call_id", call.Id,
                            "name", call.Name,
                            "arguments", call.Arguments ?? "{}"));
                    }
                }
                return;
            }

            input.Add(JsonValue.Obj("role", "user", "content", message.Content ?? string.Empty));
        }

        static JsonValue BuildToolDefinitions(List<ToolSpec> tools, bool topLevel)
        {
            var array = JsonValue.NewArray();
            foreach (var tool in tools)
            {
                array.Add(JsonValue.Obj(
                    "type", "function",
                    "name", tool.Name,
                    "description", tool.Description,
                    "parameters", tool.Schema ?? JsonValue.Obj("type", "object", "properties", JsonValue.NewObject())));
            }
            return array;
        }

        static TokenUsage ParseUsage(JsonValue usage, string model)
        {
            if (!usage.IsObject) return new TokenUsage { Model = model };
            return new TokenUsage
            {
                Model = model,
                PromptTokens = usage["input_tokens"].AsInt(),
                CompletionTokens = usage["output_tokens"].AsInt(),
                TotalTokens = usage["total_tokens"].AsInt(),
                CachedTokens = usage["input_tokens_details"]["cached_tokens"].AsInt(),
                ReasoningTokens = usage["output_tokens_details"]["reasoning_tokens"].AsInt()
            };
        }

        static bool SupportsTemperature(string model)
        {
            if (string.IsNullOrEmpty(model)) return true;
            string name = model.ToLowerInvariant();
            return !(name.StartsWith("gpt-5", StringComparison.Ordinal) ||
                     name.StartsWith("gpt-6", StringComparison.Ordinal) ||
                     name.StartsWith("o1", StringComparison.Ordinal) ||
                     name.StartsWith("o3", StringComparison.Ordinal) ||
                     name.StartsWith("o4", StringComparison.Ordinal));
        }

        string ProviderBaseUrl()
        {
            return _chatGpt ? ChatGptAuth.ApiBaseUrl : BridgeSettings.Current.OpenAiBaseUrl.TrimEnd('/');
        }

        static string FriendlyHttpError(HttpStatusException e)
        {
            string body = e.Body ?? string.Empty;
            if (body.IndexOf("subscription_sharing_user_not_eligible", StringComparison.Ordinal) >= 0)
                return "ChatGPT plan usage is not available for this account, workspace or region. Use the OpenAI API provider or another provider.";
            if (body.IndexOf("subscription_sharing_usage_limit_exceeded", StringComparison.Ordinal) >= 0)
                return "ChatGPT plan or app usage limit reached. Open Manage usage to review the applicable limit in ChatGPT settings.";
            if (body.IndexOf("subscription_sharing_usage_unavailable", StringComparison.Ordinal) >= 0)
                return "ChatGPT plan usage availability could not be checked. Try again shortly. " + e.Message;
            if (body.IndexOf("subscription_sharing_unsupported_capability", StringComparison.Ordinal) >= 0)
                return "ChatGPT rejected a request capability. " + e.Message;
            if (body.IndexOf("subscription_sharing_route_not_supported", StringComparison.Ordinal) >= 0)
                return "ChatGPT plan usage is not supported for this route. " + e.Message;
            if (e.StatusCode == 401) return "ChatGPT/OpenAI authentication failed. Sign in again or check the API key. " + e.Message;
            if (e.StatusCode == 403) return "Access denied by OpenAI: " + e.Message;
            return e.Message;
        }
    }
}
