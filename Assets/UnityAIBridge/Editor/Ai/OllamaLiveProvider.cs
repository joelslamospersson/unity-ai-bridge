using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>Ollama local inference with streaming and function calling (model dependent).</summary>
    public sealed class OllamaLiveProvider : ILiveProvider
    {
        public string Id { get { return BridgeSettings.ProviderOllama; } }
        public string DisplayName { get { return "Ollama (local)"; } }
        public string BillingNote { get { return "Runs on this machine. No API billing or ChatGPT quota."; } }

        public bool IsConfigured(out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(BridgeSettings.Current.OllamaUrl))
            {
                reason = "Set the Ollama URL (default http://127.0.0.1:11434).";
                return false;
            }
            return true;
        }

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
        {
            var json = await StreamingHttp.GetJsonAsync(BaseUrl() + "/api/tags", Headers(), 20, cancellationToken).ConfigureAwait(false);
            var result = new List<ModelInfo>();
            if (json["models"].IsArray)
            {
                foreach (var model in json["models"].ArrayValue)
                {
                    string name = model["name"].AsString(model["model"].AsString());
                    if (!string.IsNullOrEmpty(name))
                        result.Add(new ModelInfo { Id = name, DisplayName = name });
                }
            }
            return result;
        }

        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
                return "Ollama is reachable, but no models are installed. Run: ollama pull " + BridgeSettings.Current.OllamaModel;
            var names = new List<string>();
            foreach (var model in models) names.Add(model.Id);
            return "Ollama is reachable. Models: " + string.Join(", ", names.ToArray());
        }

        public async Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken)
        {
            string reason;
            if (!IsConfigured(out reason)) throw new BridgeException(reason);

            var messages = JsonValue.NewArray();
            if (!string.IsNullOrEmpty(request.Instructions))
                messages.Add(JsonValue.Obj("role", "system", "content", request.Instructions));

            foreach (var message in request.Messages)
            {
                if (message.Role == "tool")
                {
                    var entry = JsonValue.Obj("role", "tool", "content", message.Content ?? string.Empty);
                    if (!string.IsNullOrEmpty(message.Name)) entry["tool_name"] = message.Name;
                    messages.Add(entry);
                    continue;
                }
                if (message.Role == "assistant")
                {
                    var entry = JsonValue.Obj("role", "assistant", "content", message.Content ?? string.Empty);
                    if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                    {
                        var toolCallsJson = JsonValue.NewArray();
                        foreach (var call in message.ToolCalls)
                        {
                            JsonValue arguments;
                            string error;
                            if (!JsonValue.TryParse(call.Arguments ?? "{}", out arguments, out error)) arguments = JsonValue.NewObject();
                            toolCallsJson.Add(JsonValue.Obj(
                                "function", JsonValue.Obj("name", call.Name, "arguments", arguments)));
                        }
                        entry["tool_calls"] = toolCallsJson;
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
                "options", JsonValue.Obj(
                    "temperature", request.Temperature,
                    "num_predict", Math.Max(256, request.MaxOutputTokens)));

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
            }

            onEvent(StreamEvent.Status("Contacting Ollama (" + request.Model + ")..."));

            var text = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            var calls = new List<ToolCall>();
            var callIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            TokenUsage usage = new TokenUsage { Model = request.Model };

            await StreamingHttp.PostNdjsonAsync(BaseUrl() + "/api/chat", body.ToJson(false), Headers(),
                BridgeSettings.Current.RequestTimeoutSeconds, line =>
                {
                    JsonValue chunk;
                    string error;
                    if (!JsonValue.TryParse(line, out chunk, out error)) return;
                    if (!chunk["error"].IsNull)
                        throw new BridgeException("Ollama error: " + chunk["error"].AsString());

                    var message = chunk["message"];
                    string content = message["content"].AsString();
                    if (!string.IsNullOrEmpty(content)) { text.Append(content); onEvent(StreamEvent.Delta(content)); }
                    string think = message["thinking"].AsString();
                    if (!string.IsNullOrEmpty(think)) { reasoning.Append(think); onEvent(StreamEvent.Reasoning(think)); }

                    if (message["tool_calls"].IsArray)
                    {
                        foreach (var part in message["tool_calls"].ArrayValue)
                        {
                            string name = part["function"]["name"].AsString();
                            string arguments = part["function"]["arguments"].IsString
                                ? part["function"]["arguments"].AsString()
                                : part["function"]["arguments"].ToJson(false);
                            string key = name + "|" + arguments;
                            int index;
                            if (callIndex.TryGetValue(key, out index))
                            {
                                calls[index].Arguments = arguments;
                                continue;
                            }
                            var call = new ToolCall
                            {
                                Id = part["id"].AsString("ollama_" + calls.Count.ToString()),
                                Name = name,
                                Arguments = arguments
                            };
                            callIndex[key] = calls.Count;
                            calls.Add(call);
                            onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = call.Clone(), Text = name });
                        }
                    }

                    if (chunk["done"].AsBool())
                    {
                        usage.PromptTokens = chunk["prompt_eval_count"].AsInt();
                        usage.CompletionTokens = chunk["eval_count"].AsInt();
                        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                    }
                }, cancellationToken).ConfigureAwait(false);

            var turn = new ProviderTurn
            {
                Text = text.ToString(),
                Reasoning = reasoning.ToString(),
                Usage = usage,
                StopReason = "stop"
            };
            foreach (var call in calls)
            {
                turn.ToolCalls.Add(call);
                onEvent(new StreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = call.Clone() });
            }
            onEvent(new StreamEvent { Kind = StreamEventKind.Usage, Usage = usage });
            return turn;
        }

        static string BaseUrl()
        {
            return BridgeSettings.Current.OllamaUrl.TrimEnd('/');
        }

        static Dictionary<string, string> Headers()
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(BridgeSettings.Secrets.OllamaApiKey))
                headers["Authorization"] = "Bearer " + BridgeSettings.Secrets.OllamaApiKey;
            return headers;
        }
    }
}
