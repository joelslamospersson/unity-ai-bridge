using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static string ClaudeFixture(bool tools, bool truncated)
        {
            string value = Sse(JsonValue.Obj("type", "message_start", "message", JsonValue.Obj("usage", JsonValue.Obj("input_tokens", 10, "cache_read_input_tokens", 2))));
            if (tools)
            {
                value += Sse(JsonValue.Obj("type", "content_block_start", "index", 0, "content_block", JsonValue.Obj("type", "thinking", "thinking", "")));
                value += Sse(JsonValue.Obj("type", "content_block_delta", "index", 0, "delta", JsonValue.Obj("type", "thinking_delta", "thinking", "Inspect first.")));
                value += Sse(JsonValue.Obj("type", "content_block_delta", "index", 0, "delta", JsonValue.Obj("type", "signature_delta", "signature", "signed-thinking")));
                value += Sse(JsonValue.Obj("type", "content_block_stop", "index", 0));
                value += Sse(JsonValue.Obj("type", "content_block_start", "index", 1, "content_block", JsonValue.Obj("type", "tool_use", "id", "claude_call", "name", "project_summary", "input", JsonValue.NewObject())));
                value += Sse(JsonValue.Obj("type", "content_block_delta", "index", 1, "delta", JsonValue.Obj("type", "input_json_delta", "partial_json", "{")));
                value += Sse(JsonValue.Obj("type", "content_block_delta", "index", 1, "delta", JsonValue.Obj("type", "input_json_delta", "partial_json", "}")));
            }
            else
            {
                value += Sse(JsonValue.Obj("type", "content_block_start", "index", 0, "content_block", JsonValue.Obj("type", "text", "text", "")));
                value += Sse(JsonValue.Obj("type", "content_block_delta", "index", 0, "delta", JsonValue.Obj("type", "text_delta", "text", "Claude final.")));
            }
            if (!truncated) value += Sse(JsonValue.Obj("type", "message_delta", "delta", JsonValue.Obj("stop_reason", tools ? "tool_use" : "end_turn"), "usage", JsonValue.Obj("output_tokens", 5))) + Sse(JsonValue.Obj("type", "message_stop"));
            return value;
        }

        static string GeminiFixture(bool tools, bool truncated)
        {
            var parts = tools ? JsonValue.Arr(
                JsonValue.Obj("text", "Inspect first.", "thought", true),
                JsonValue.Obj("functionCall", JsonValue.Obj("id", "gemini_call", "name", "project_summary", "args", JsonValue.NewObject()), "thoughtSignature", "signed-gemini"))
                : JsonValue.Arr(JsonValue.Obj("text", "Gemini final."));
            var candidate = JsonValue.Obj("content", JsonValue.Obj("role", "model", "parts", parts));
            if (!truncated) candidate["finishReason"] = "STOP";
            return Sse(JsonValue.Obj("candidates", JsonValue.Arr(candidate), "usageMetadata", JsonValue.Obj("promptTokenCount", 10, "candidatesTokenCount", 5, "thoughtsTokenCount", tools ? 2 : 0, "totalTokenCount", tools ? 17 : 15)));
        }

        static IEnumerator RunNativeProviderTests()
        {
            string savedSettings = JsonUtility.ToJson(BridgeSettings.Current);
            string savedSecrets = JsonUtility.ToJson(BridgeSettings.Secrets);
            try
            {
                BridgeSettings.Secrets.ClaudeApiKey = ""; BridgeSettings.Secrets.GeminiApiKey = "";
                string reason;
                Check("new_provider_factory_and_auth_isolation", ProviderFactory.Create(BridgeSettings.ProviderClaude) is ClaudeApiProvider &&
                    ProviderFactory.Create(BridgeSettings.ProviderGemini) is GeminiProvider && ProviderFactory.Create(BridgeSettings.ProviderClaudeSubscription) is ClaudeSubscriptionProvider &&
                    !ProviderFactory.IsConfigured(BridgeSettings.ProviderClaude, out reason) && !ProviderFactory.IsConfigured(BridgeSettings.ProviderGemini, out reason) && !ProviderFactory.IsConfigured(BridgeSettings.ProviderClaudeSubscription, out reason));
                foreach (string id in new[] { BridgeSettings.ProviderClaude, BridgeSettings.ProviderGemini, BridgeSettings.ProviderClaudeSubscription })
                {
                    var settings = new BridgeSettings { Provider = id }; settings.Normalize();
                    Check("provider_settings_accept_" + id, settings.Provider == id);
                }
                string google = "AIza" + new string('A', 35), github = "ghp_" + new string('B', 36);
                string safe = FileService.RedactSecrets(google + " " + github);
                Check("google_and_github_credentials_are_redacted", !safe.Contains(google) && !safe.Contains(github));

                var panel = new ChatPanel(); var view = new ChatMessageView(panel);
                var activity = new ChatMessage { Id = "collapse-regression", Role = "activity", Activities = new List<ChatToolActivity> { new ChatToolActivity { Tool = "project_summary", CallId = "live", Status = "Running", Detail = "Waiting" } } };
                view.Bind(activity); var foldout = view.Q<Foldout>("chat-tool-activity"); bool initiallyClosed = !foldout.value;
                activity.Activities[0].Detail = "Live progress"; view.Bind(activity); bool remainsClosed = !foldout.value;
                foldout.value = true; activity.Activities[0].Status = "Done"; activity.Activities[0].Output = new string('x', 5000); view.Bind(activity);
                Check("tools_stay_collapsed_until_user_expands_and_keep_full_output", initiallyClosed && remainsClosed && foldout.value && view.Q<Label>("chat-tool-output").text.Length == 5000);
                foldout.value = false; view.Bind(activity);
                Check("tools_keep_user_collapse_after_updates", !foldout.value && !panel.ActivityExpanded(activity.Id));
                panel.Detach();

                var tool = new ToolSpec { Name = "project_summary", Description = "Read project state", Schema = JsonValue.Obj("type", "object", "properties", JsonValue.NewObject()) };
                BridgeSettings.Secrets.ClaudeApiKey = "test-claude-key";
                bool claudeAuth = true, claudeTruncated = false; int claudePosts = 0, claudeTools = 0, claudeGets = 0;
                JsonValue claudeFirst = null, claudeSecond = null;
                using (var server = new LocalTestServer((request, body) =>
                {
                    claudeAuth &= request.Headers["x-api-key"] == "test-claude-key" && request.Headers["anthropic-version"] == "2023-06-01" && request.Headers["Authorization"] == null;
                    if (request.HttpMethod == "GET")
                    {
                        claudeGets++;
                        bool more = request.QueryString["after_id"] == null;
                        return new MockReply { Body = JsonValue.Obj("data", JsonValue.Arr(JsonValue.Obj("id", more ? "claude-test" : "claude-second", "display_name", more ? "Claude Test" : "Claude Second")), "has_more", more, "last_id", more ? "cursor" : "last").ToJson(false) };
                    }
                    claudePosts++; if (claudePosts == 1) claudeFirst = JsonValue.Parse(body); else claudeSecond = JsonValue.Parse(body);
                    return new MockReply { ContentType = "text/event-stream", Body = ClaudeFixture(claudePosts == 1, claudeTruncated) };
                }))
                {
                    BridgeSettings.Current.ClaudeBaseUrl = server.BaseUrl + "/v1";
                    var provider = new ClaudeApiProvider(); var models = provider.GetModelsAsync(CancellationToken.None);
                    while (!models.IsCompleted) yield return null;
                    Check("claude_model_catalog_paginates_with_api_key", models.Status == TaskStatus.RanToCompletion && models.Result.Count == 2 && claudeGets == 2 && models.Result[0].Id == "claude-test" && claudeAuth);
                    var transcript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Inspect the project" } };
                    string streamedThinking = "";
                    var run = AgentRunner.RunAsync(provider, new LlmRequest { Model = "claude-test", Instructions = "Inspect and verify", Thinking = true, Tools = new List<ToolSpec> { tool }, ToolExecutor = (call, token) =>
                    { claudeTools++; return Task.FromResult(JsonValue.Obj("content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", "Project inspected")))); } }, transcript,
                        evt => { if (evt.Kind == StreamEventKind.ReasoningDelta) streamedThinking += evt.Text; }, CancellationToken.None);
                    while (!run.IsCompleted) yield return null;
                    Check("claude_streaming_tool_loop_returns_final_and_usage", run.Status == TaskStatus.RanToCompletion && run.Result.Text == "Claude final." && claudeTools == 1 && claudePosts == 2 && run.Result.Usage.PromptTokens == 24 && streamedThinking == "Inspect first." && claudeAuth,
                        run.Exception == null ? null : run.Exception.GetBaseException().Message);
                    Check("claude_request_has_native_schema_and_system_instructions", claudeFirst != null && claudeFirst["system"].AsString() == "Inspect and verify" && claudeFirst["tools"][0]["input_schema"]["type"].AsString() == "object" && claudeFirst["thinking"]["type"].AsString() == "enabled");
                    Check("claude_tool_replay_preserves_signed_thinking_and_matching_result", claudeSecond != null && claudeSecond["messages"][1]["content"][0]["signature"].AsString() == "signed-thinking" &&
                        claudeSecond["messages"][1]["content"][1]["id"].AsString() == "claude_call" && claudeSecond["messages"][2]["content"][0]["tool_use_id"].AsString() == "claude_call");
                    claudeTruncated = true;
                    var incomplete = provider.StreamTurnAsync(new LlmRequest { Model = "claude-test", Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Hi" } } }, evt => { }, CancellationToken.None);
                    while (!incomplete.IsCompleted) yield return null;
                    Check("claude_rejects_incomplete_stream", incomplete.IsFaulted && incomplete.Exception.GetBaseException().Message.Contains("message_stop"));
                }

                BridgeSettings.Secrets.GeminiApiKey = "test-gemini-key";
                bool geminiAuth = true, geminiTruncated = false, geminiBlocked = false; int geminiPosts = 0, geminiTools = 0, geminiGets = 0;
                JsonValue geminiFirst = null, geminiSecond = null;
                using (var server = new LocalTestServer((request, body) =>
                {
                    geminiAuth &= request.Headers["x-goog-api-key"] == "test-gemini-key" && !request.Url.Query.Contains("key=") && request.Headers["Authorization"] == null;
                    if (request.HttpMethod == "GET")
                    {
                        geminiGets++; bool more = request.QueryString["pageToken"] == null;
                        var page = JsonValue.Obj("models", JsonValue.Arr(
                            JsonValue.Obj("name", more ? "models/gemini-3-test" : "models/gemini-3-second", "displayName", "Gemini Test", "supportedGenerationMethods", JsonValue.Arr("generateContent")),
                            JsonValue.Obj("name", "models/embedding-test", "supportedGenerationMethods", JsonValue.Arr("embedContent"))));
                        if (more) page["nextPageToken"] = "next page";
                        return new MockReply { Body = page.ToJson(false) };
                    }
                    geminiPosts++; if (geminiPosts == 1) geminiFirst = JsonValue.Parse(body); else geminiSecond = JsonValue.Parse(body);
                    return new MockReply { ContentType = "text/event-stream", Body = geminiBlocked ? Sse(JsonValue.Obj("promptFeedback", JsonValue.Obj("blockReason", "SAFETY"))) : GeminiFixture(geminiPosts == 1, geminiTruncated) };
                }))
                {
                    BridgeSettings.Current.GeminiBaseUrl = server.BaseUrl + "/v1beta";
                    var provider = new GeminiProvider(); var models = provider.GetModelsAsync(CancellationToken.None);
                    while (!models.IsCompleted) yield return null;
                    Check("gemini_catalog_paginates_and_filters_non_chat_models", models.Status == TaskStatus.RanToCompletion && models.Result.Count == 2 && geminiGets == 2 && models.Result[0].Id == "gemini-3-test" && geminiAuth);
                    var transcript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Inspect the project" } };
                    var run = AgentRunner.RunAsync(provider, new LlmRequest { Model = "gemini-3-test", Instructions = "Inspect and verify", Thinking = true, Tools = new List<ToolSpec> { tool }, ToolExecutor = (call, token) =>
                    { geminiTools++; return Task.FromResult(JsonValue.Obj("content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", "Project inspected")))); } }, transcript, evt => { }, CancellationToken.None);
                    while (!run.IsCompleted) yield return null;
                    Check("gemini_streaming_tool_loop_returns_final_and_usage", run.Status == TaskStatus.RanToCompletion && run.Result.Text == "Gemini final." && geminiTools == 1 && geminiPosts == 2 && run.Result.Usage.ReasoningTokens == 2 && geminiAuth,
                        run.Exception == null ? null : run.Exception.GetBaseException().Message);
                    Check("gemini_request_has_native_schema_system_and_thinking", geminiFirst != null && geminiFirst["systemInstruction"]["parts"][0]["text"].AsString() == "Inspect and verify" && geminiFirst["tools"][0]["functionDeclarations"][0]["parametersJsonSchema"]["type"].AsString() == "object" && geminiFirst["generationConfig"]["thinkingConfig"]["includeThoughts"].AsBool());
                    Check("gemini_tool_replay_preserves_thought_signature_and_call_id", geminiSecond != null && geminiSecond["contents"][1]["parts"][1]["thoughtSignature"].AsString() == "signed-gemini" && geminiSecond["contents"][2]["parts"][0]["functionResponse"]["id"].AsString() == "gemini_call");
                    geminiTruncated = true;
                    var incomplete = provider.StreamTurnAsync(new LlmRequest { Model = "gemini-3-test", Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Hi" } } }, evt => { }, CancellationToken.None);
                    while (!incomplete.IsCompleted) yield return null;
                    Check("gemini_rejects_incomplete_stream", incomplete.IsFaulted && incomplete.Exception.GetBaseException().Message.Contains("finish reason"));
                    geminiBlocked = true;
                    var blocked = provider.StreamTurnAsync(new LlmRequest { Model = "gemini-3-test", Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Hi" } } }, evt => { }, CancellationToken.None);
                    while (!blocked.IsCompleted) yield return null;
                    Check("gemini_surfaces_prompt_block_without_fake_success", blocked.IsFaulted && blocked.Exception.GetBaseException().Message.Contains("SAFETY"));
                }
            }
            finally
            {
                JsonUtility.FromJsonOverwrite(savedSettings, BridgeSettings.Current);
                JsonUtility.FromJsonOverwrite(savedSecrets, BridgeSettings.Secrets);
                BridgeSettings.Save(); BridgeSettings.SaveSecrets();
            }
        }
    }
}
