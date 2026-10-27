using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static Dictionary<string, string> AuthFields(string value)
        {
            var fields = new Dictionary<string, string>();
            foreach (var pair in value.TrimStart('?').Split('&'))
            {
                int separator = pair.IndexOf('=');
                if (separator >= 0) fields[WebUtility.UrlDecode(pair.Substring(0, separator))] = WebUtility.UrlDecode(pair.Substring(separator + 1));
            }
            return fields;
        }

        static IEnumerator RunChatGptPlanLoginTests()
        {
            string savedSecrets = JsonUtility.ToJson(BridgeSettings.Secrets);
            string savedSettings = JsonUtility.ToJson(BridgeSettings.Current);
            string savedProvider = ChatService.CurrentProviderId;
            string savedAuthBase = ChatGptAuth.AuthBaseUrl, savedApiBase = ChatGptAuth.ApiBaseUrl;
            var savedBrowser = ChatGptAuth.OpenBrowser;
            int completed = 0;
            bool completedOnMain = false;
            Action completion = () => { completed++; completedOnMain = MainThread.IsMainThread && !ChatGptAuth.IsBusy; };
            ChatGptAuth.SignInCompleted += completion;
            using (var rsa = RSA.Create())
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
            {
                rsa.KeySize = 2048;
                var key = rsa.ExportParameters(false);
                string jwks = JsonValue.Obj("keys", JsonValue.Arr(JsonValue.Obj("kty", "RSA", "kid", "plan-login", "n", ChatGptAuth.Base64UrlEncode(key.Modulus), "e", ChatGptAuth.Base64UrlEncode(key.Exponent)))).ToJson(false);
                Dictionary<string, string> authorization = null, tokenFields = null;
                Task<HttpResponseMessage> browser = null;
                string requestAuth = null, inferenceBody = null;
                bool allowPlan = true, wrongNonce = false, mismatchClient = false;
                bool usageLimited = false;
                int inferenceRequests = 0;
                LocalTestServer server = null;
                try
                {
                    server = new LocalTestServer((request, body) =>
                    {
                        if (request.Url.AbsolutePath.EndsWith("jwks.json", StringComparison.Ordinal)) return new MockReply { Body = jwks };
                        if (request.Url.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                        {
                            tokenFields = AuthFields(body);
                            string header = JsonValue.Obj("alg", "RS256", "kid", "plan-login").ToJson(false);
                            string payload = JsonValue.Obj("iss", server.BaseUrl, "aud", "oaiapp_plan_test", "sub", "plan-test-user", "email", "plan@example.invalid", "nonce", wrongNonce ? "wrong-nonce" : authorization["nonce"], "exp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600).ToJson(false);
                            string signed = ChatGptAuth.Base64UrlEncode(Encoding.UTF8.GetBytes(header)) + "." + ChatGptAuth.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
                            string jwt = signed + "." + ChatGptAuth.Base64UrlEncode(rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                            return new MockReply { Body = JsonValue.Obj("access_token", "plan-access-test", "refresh_token", "plan-refresh-test", "id_token", jwt, "scope", "openid email profile offline_access resource.invoke" + (allowPlan ? " chatgpt.tokens.use.direct" : ""), "expires_in", 3600).ToJson(false) };
                        }
                        requestAuth = request.Headers["Authorization"];
                        if (request.Url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                            return new MockReply { Body = JsonValue.Obj("models", JsonValue.Arr(JsonValue.Obj("slug", "subscription-model", "display_name", "Subscription model", "visibility", "list"))).ToJson(false) };
                        inferenceBody = body;
                        inferenceRequests++;
                        if (usageLimited) return new MockReply { Status = 429, Body = JsonValue.Obj("error", JsonValue.Obj("code", "subscription_sharing_usage_limit_exceeded", "message", "App allowance reached.")).ToJson(false) };
                        return new MockReply { ContentType = "text/event-stream", Body = Sse(JsonValue.Obj("type", "response.output_text.delta", "delta", "Plan reply.")) +
                            Sse(JsonValue.Obj("type", "response.completed", "response", JsonValue.Obj("status", "completed", "output", JsonValue.NewArray()))) };
                    });
                    ChatGptAuth.AuthBaseUrl = server.BaseUrl;
                    ChatGptAuth.ApiBaseUrl = server.BaseUrl + "/v1";
                    var secrets = BridgeSettings.Secrets;
                    secrets.ChatGptAccessToken = ""; secrets.ChatGptRefreshToken = ""; secrets.ChatGptIdToken = "";
                    secrets.ChatGptClientId = ""; secrets.ChatGptSubject = ""; secrets.ChatGptHostId = "";
                    secrets.ChatGptPlanUsageEnabled = false; secrets.ChatGptScopes = "";
                    secrets.OpenAiApiKey = "must-not-use-platform-key";
                    ChatGptAuth.Reload();
                    ChatGptAuth.OpenBrowser = url =>
                    {
                        authorization = AuthFields(new Uri(url).Query);
                        string callback = authorization["redirect_uri"] + "?state=" + Uri.EscapeDataString(authorization["state"]) + "&code=local-test-code&client_id=" + (mismatchClient ? "oaiapp_unexpected" : "oaiapp_plan_test");
                        browser = http.GetAsync(callback);
                    };

                    // This action is available from any provider and selects plan billing.
                    ChatService.SwitchProvider(BridgeSettings.ProviderDeepSeek);
                    ChatService.UseChatGptPlan();
                    float deadline = Time.realtimeSinceStartup + 15;
                    while ((ChatGptAuth.IsBusy || browser == null || !browser.IsCompleted) && Time.realtimeSinceStartup < deadline) yield return null;
                    yield return null;
                    Check("chatgpt_plus_login_selects_subscription_provider_and_completes", ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt && ChatGptAuth.IsReadyForInference && completed == 1 && completedOnMain,
                        ChatGptAuth.LastError);
                    Check("chatgpt_plus_login_requests_plan_scope_and_dynamic_registration", authorization != null && authorization["client_id"] == "dynamic_agent_client" &&
                        authorization["scope"].Contains(ChatGptAuth.PlanScope) && authorization["agent_name_hint"] == ChatGptAuth.AgentName && authorization["resource"] == ChatGptAuth.ApiResource);
                    Check("chatgpt_plus_exchange_uses_issued_client_pkce_and_exact_callback", tokenFields != null && tokenFields["client_id"] == "oaiapp_plan_test" &&
                        tokenFields["redirect_uri"] == authorization["redirect_uri"] && ChatGptAuth.CreateCodeChallenge(tokenFields["code_verifier"]) == authorization["code_challenge"]);
                    string host = secrets.ChatGptHostId;
                    var browserPage = browser != null && browser.Status == TaskStatus.RanToCompletion ? browser.Result.Content.ReadAsStringAsync() : Task.FromResult("");
                    while (!browserPage.IsCompleted) yield return null;
                    Check("chatgpt_plus_browser_success_requires_verified_plan_permission", browserPage.Result.Contains("ChatGPT connected.") && browserPage.Result.Contains("return to Unity") && !browserPage.Result.Contains("plan-access-test") &&
                        browser.Result.Headers.CacheControl != null && browser.Result.Headers.CacheControl.NoStore);

                    var provider = new OpenAiResponsesProvider(true);
                    var catalog = provider.GetModelsAsync(CancellationToken.None);
                    while (!catalog.IsCompleted) yield return null;
                    Check("chatgpt_plus_login_discovers_subscription_models", catalog.Status == TaskStatus.RanToCompletion && catalog.Result.Count == 1 && catalog.Result[0].Id == "subscription-model" && requestAuth == "Bearer plan-access-test");
                    var inference = provider.StreamTurnAsync(new LlmRequest { Model = "subscription-model", Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Hello" } } }, evt => { }, CancellationToken.None);
                    while (!inference.IsCompleted) yield return null;
                    var requestBody = string.IsNullOrEmpty(inferenceBody) ? JsonValue.NewObject() : JsonValue.Parse(inferenceBody);
                    Check("chatgpt_plus_inference_uses_oauth_plan_without_api_key", inference.Status == TaskStatus.RanToCompletion && inference.Result.Text == "Plan reply." && requestAuth == "Bearer plan-access-test" &&
                        !requestBody["store"].AsBool(true) && requestBody["stream"].AsBool() && requestBody["max_output_tokens"].IsNull && secrets.OpenAiApiKey == "must-not-use-platform-key");

                    usageLimited = true;
                    int previousRequests = inferenceRequests;
                    var limited = provider.StreamTurnAsync(new LlmRequest { Model = "subscription-model", Messages = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Hello" } } }, evt => { }, CancellationToken.None);
                    while (!limited.IsCompleted) yield return null;
                    string limitError = limited.IsFaulted ? limited.Exception.GetBaseException().Message : "";
                    Check("chatgpt_usage_limit_preserves_session_without_retry_or_billing_fallback", limited.IsFaulted && limitError.Contains("plan or app usage limit") && limitError.Contains("Manage usage") && !limitError.Contains("5-hour") &&
                        inferenceRequests == previousRequests + 1 && ChatGptAuth.IsReadyForInference && ChatService.CurrentProviderId == BridgeSettings.ProviderChatGpt && secrets.OpenAiApiKey == "must-not-use-platform-key");
                    usageLimited = false;

                    BridgeSettings.Current.ChatGptPlanWelcomeShown = false;
                    var panel = new ChatPanel();
                    Check("chatgpt_plus_shows_usage_link_and_first_connection_confirmation", panel.Q<Button>("chatgpt-manage-usage").style.display.value == DisplayStyle.Flex && panel.Q<VisualElement>("chatgpt-plan-welcome").style.display.value == DisplayStyle.Flex);
                    panel.Detach();
                    BridgeSettings.Current.ChatGptPlanWelcomeShown = true;
                    var returningPanel = new ChatPanel();
                    Check("chatgpt_plus_confirmation_can_stay_dismissed", returningPanel.Q<VisualElement>("chatgpt-plan-welcome").style.display.value == DisplayStyle.None);
                    returningPanel.Detach();

                    allowPlan = false; browser = null;
                    ChatService.UseChatGptPlan(true);
                    deadline = Time.realtimeSinceStartup + 15;
                    while ((ChatGptAuth.IsBusy || browser == null || !browser.IsCompleted) && Time.realtimeSinceStartup < deadline) yield return null;
                    yield return null;
                    var declinedPage = browser != null && browser.Status == TaskStatus.RanToCompletion ? browser.Result.Content.ReadAsStringAsync() : Task.FromResult("");
                    while (!declinedPage.IsCompleted) yield return null;
                    Check("chatgpt_plus_missing_consent_never_enables_inference", ChatGptAuth.Status == ChatGptStatus.PlanUsageUnavailable && !ChatGptAuth.IsReadyForInference && completed == 2 && declinedPage.Result.Contains("plan usage was not allowed") && !declinedPage.Result.Contains("ChatGPT connected."));
                    Check("chatgpt_plus_returning_login_reuses_registration_and_host", authorization["client_id"] == "oaiapp_plan_test" && !authorization.ContainsKey("agent_name_hint") && secrets.ChatGptHostId == host);

                    allowPlan = true; wrongNonce = true; browser = null;
                    ChatService.UseChatGptPlan(true);
                    deadline = Time.realtimeSinceStartup + 15;
                    while ((ChatGptAuth.IsBusy || browser == null || !browser.IsCompleted) && Time.realtimeSinceStartup < deadline) yield return null;
                    yield return null;
                    var rejectedPage = browser != null && browser.Status == TaskStatus.RanToCompletion ? browser.Result.Content.ReadAsStringAsync() : Task.FromResult("");
                    while (!rejectedPage.IsCompleted) yield return null;
                    string consent;
                    Check("chatgpt_plus_missing_permission_requests_explicit_reconsent", authorization.TryGetValue("prompt", out consent) && consent == "consent" && authorization["scope"].Contains(ChatGptAuth.PlanScope));
                    Check("chatgpt_plus_invalid_identity_does_not_return_success", completed == 2 && !ChatGptAuth.IsReadyForInference && rejectedPage.Result.Contains("could not be connected") && !rejectedPage.Result.Contains("ChatGPT connected."));

                    wrongNonce = false; mismatchClient = true; browser = null;
                    ChatService.UseChatGptPlan(true);
                    deadline = Time.realtimeSinceStartup + 15;
                    while ((ChatGptAuth.IsBusy || browser == null || !browser.IsCompleted) && Time.realtimeSinceStartup < deadline) yield return null;
                    yield return null;
                    Check("chatgpt_plus_rejects_changed_registration_on_return", completed == 2 && secrets.ChatGptClientId == "oaiapp_plan_test" && !ChatGptAuth.IsReadyForInference && (ChatGptAuth.LastError ?? "").Contains("different client ID"));
                }
                finally
                {
                    ChatGptAuth.CancelSignIn();
                    ChatGptAuth.OpenBrowser = savedBrowser;
                    ChatGptAuth.AuthBaseUrl = savedAuthBase; ChatGptAuth.ApiBaseUrl = savedApiBase;
                    ChatGptAuth.SignInCompleted -= completion;
                    if (server != null) server.Dispose();
                    JsonUtility.FromJsonOverwrite(savedSecrets, BridgeSettings.Secrets);
                    JsonUtility.FromJsonOverwrite(savedSettings, BridgeSettings.Current);
                    ChatGptAuth.Reload();
                    ChatService.SwitchProvider(savedProvider);
                    BridgeSettings.SaveSecrets(); BridgeSettings.Save();
                }
            }
        }
    }
}
