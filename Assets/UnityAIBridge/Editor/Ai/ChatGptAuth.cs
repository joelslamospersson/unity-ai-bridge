using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityAIBridge
{
    public enum ChatGptStatus
    {
        NotSignedIn,
        SigningIn,
        SignedIn,
        PlanUsageUnavailable,
        SignedOut,
        Error
    }

    /// <summary>
    /// Sign in with ChatGPT for open-source clients (ChatGPT plan usage).
    /// Implements OAuth 2.0 Authorization Code + PKCE against auth.openai.com and
    /// validates the OIDC ID token against OpenAI's JWKS. No cookies, no scraping.
    /// </summary>
    public static class ChatGptAuth
    {
        public const string DefaultAuthBaseUrl = "https://auth.openai.com";
        public const string DefaultApiBaseUrl = "https://api.openai.com/v1";
        public const string ApiResource = "https://api.openai.com/v1";
        public const string AgentName = "Unity AI Bridge";
        public const string PlanScope = "chatgpt.tokens.use.direct";
        const string RequestedScope = "openid profile email offline_access resource.invoke " + PlanScope;

        public static string AuthBaseUrl = DefaultAuthBaseUrl;
        public static string ApiBaseUrl = DefaultApiBaseUrl;

        static readonly SemaphoreSlim RefreshGate = new SemaphoreSlim(1, 1);
        static CancellationTokenSource _signInCancellation;
        static HttpListener _signInListener;
        static string _jwksJson;
        static DateTime _jwksFetchedUtc;

        public static ChatGptStatus Status { get; private set; }
        public static string LastError { get; private set; }
        public static string AccountLabel { get; private set; }
        public static bool PlanUsageEnabled { get; private set; }

        public static event Action Changed;

        static ChatGptAuth()
        {
            Reload();
        }

        public static bool IsBusy { get { return Status == ChatGptStatus.SigningIn; } }

        public static bool HasTokens
        {
            get
            {
                var s = BridgeSettings.Secrets;
                return !string.IsNullOrEmpty(s.ChatGptAccessToken) || !string.IsNullOrEmpty(s.ChatGptRefreshToken);
            }
        }

        public static bool IsReadyForInference
        {
            get { return Status == ChatGptStatus.SignedIn && PlanUsageEnabled && HasTokens; }
        }

        public static void Reload()
        {
            var s = BridgeSettings.Secrets;
            AccountLabel = string.IsNullOrEmpty(s.ChatGptEmail) ? "ChatGPT account" : s.ChatGptEmail;
            PlanUsageEnabled = s.ChatGptPlanUsageEnabled && HasScope(s.ChatGptScopes, PlanScope);
            LastError = null;
            if (!HasTokens)
                Status = string.IsNullOrEmpty(s.ChatGptClientId) ? ChatGptStatus.NotSignedIn : ChatGptStatus.SignedOut;
            else if (PlanUsageEnabled) Status = ChatGptStatus.SignedIn;
            else Status = ChatGptStatus.PlanUsageUnavailable;
        }

        static void Notify()
        {
            var handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        // ------------------------------------------------------------------ sign in

        public static void StartSignIn(bool forceConsent = false)
        {
            if (IsBusy) return;
            Status = ChatGptStatus.SigningIn;
            LastError = null;
            Notify();
            _signInCancellation = new CancellationTokenSource();
            var token = _signInCancellation.Token;
            Task.Run(() => SignInAsync(forceConsent, token));
        }

        public static void CancelSignIn()
        {
            try { if (_signInCancellation != null) _signInCancellation.Cancel(); } catch { }
            try { if (_signInListener != null) _signInListener.Close(); } catch { }
            Status = HasTokens ? (PlanUsageEnabled ? ChatGptStatus.SignedIn : ChatGptStatus.PlanUsageUnavailable) : ChatGptStatus.NotSignedIn;
            LastError = null;
            Notify();
        }

        static async Task SignInAsync(bool forceConsent, CancellationToken cancellationToken)
        {
            string authBase = AuthBaseUrl;
            string apiBase = ApiBaseUrl;
            int port = 0;
            try
            {
                EnsureHostId();
                var secrets = BridgeSettings.Secrets;
                string hostId = secrets.ChatGptHostId;
                string state = RandomBase64Url(32);
                string nonce = RandomBase64Url(32);
                string verifier = CreateCodeVerifier();
                string challenge = CreateCodeChallenge(verifier);

                _signInListener = StartListener(out port);
                string redirectUri = "http://127.0.0.1:" + port + "/auth/callback";
                bool isNew = string.IsNullOrEmpty(secrets.ChatGptClientId);
                string authorizationUrl = BuildAuthorizationUrl(
                    isNew ? "dynamic_agent_client" : secrets.ChatGptClientId,
                    redirectUri, state, nonce, challenge, hostId,
                    isNew ? null : secrets.ChatGptIdToken,
                    isNew ? null : secrets.ChatGptEmail,
                    forceConsent, isNew);

                // Never log this URL: it may contain id_token_hint.
                MainThread.Invoke(() => Application.OpenURL(authorizationUrl), 15000);

                var callback = await WaitForCallback(state, cancellationToken).ConfigureAwait(false);
                string code = callback[0];
                string callbackClientId = callback[1];

                if (!isNew && !string.IsNullOrEmpty(callbackClientId) &&
                    !string.Equals(callbackClientId, secrets.ChatGptClientId, StringComparison.Ordinal))
                    throw new BridgeException("ChatGPT returned a different client ID than the saved registration. The result was rejected.");

                string issuedClientId = !string.IsNullOrEmpty(callbackClientId)
                    ? callbackClientId
                    : secrets.ChatGptClientId;
                if (string.IsNullOrEmpty(issuedClientId) || issuedClientId == "dynamic_agent_client")
                    throw new BridgeException("ChatGPT did not return an issued client ID, so registration is incomplete.");

                var fields = new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "client_id", issuedClientId },
                    { "code", code },
                    { "code_verifier", verifier },
                    { "redirect_uri", redirectUri },
                    { "resource", ApiResource }
                };
                var tokenResponse = await StreamingHttp.PostFormAsync(authBase + "/api/accounts/oauth/token", fields, 60, cancellationToken).ConfigureAwait(false);
                string accessToken = tokenResponse["access_token"].AsString();
                string refreshToken = tokenResponse["refresh_token"].AsString();
                string idToken = tokenResponse["id_token"].AsString();
                string scope = tokenResponse["scope"].AsString();
                int expiresIn = tokenResponse["expires_in"].AsInt(3600);
                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(idToken))
                    throw new BridgeException("ChatGPT authorization did not return the required tokens. " + tokenResponse["error_description"].AsString());

                string jwks = await FetchJwksAsync(authBase, cancellationToken).ConfigureAwait(false);
                string validationError;
                string email;
                string subject;
                if (!ValidateIdToken(idToken, jwks, nonce, issuedClientId, authBase, out validationError, out email, out subject))
                    throw new BridgeException("ChatGPT sign-in could not be verified: " + validationError);

                secrets.ChatGptAccessToken = accessToken;
                secrets.ChatGptRefreshToken = refreshToken;
                secrets.ChatGptIdToken = idToken;
                secrets.ChatGptClientId = issuedClientId;
                secrets.ChatGptEmail = email ?? string.Empty;
                secrets.ChatGptSubject = subject ?? string.Empty;
                secrets.ChatGptScopes = scope ?? string.Empty;
                secrets.ChatGptTokenType = tokenResponse["token_type"].AsString("Bearer");
                secrets.ChatGptExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
                secrets.ChatGptPlanUsageEnabled = HasScope(scope, PlanScope);
                BridgeSettings.SaveSecrets();

                PlanUsageEnabled = secrets.ChatGptPlanUsageEnabled;
                AccountLabel = string.IsNullOrEmpty(email) ? "ChatGPT account" : email;
                if (PlanUsageEnabled)
                {
                    Status = ChatGptStatus.SignedIn;
                    LastError = null;
                }
                else
                {
                    Status = ChatGptStatus.PlanUsageUnavailable;
                    LastError = "ChatGPT plan usage was not granted. Reconnect and allow the app to use your ChatGPT plan, or use the OpenAI API provider instead.";
                }
                BridgeLog.Success("chatgpt", "Signed in with ChatGPT as " + AccountLabel + " (plan usage " + (PlanUsageEnabled ? "enabled" : "not granted") + ").");
                Notify();
            }
            catch (Exception e)
            {
                var bridge = e as BridgeException;
                string message = bridge != null ? bridge.Message : e.Message;
                if (cancellationToken.IsCancellationRequested) message = "Sign-in cancelled.";
                LastError = message;
                Status = HasTokens ? (PlanUsageEnabled ? ChatGptStatus.SignedIn : ChatGptStatus.PlanUsageUnavailable) : ChatGptStatus.Error;
                BridgeLog.Warn("chatgpt", "Sign-in failed: " + message);
                Notify();
            }
            finally
            {
                StopListener();
                try { if (_signInCancellation != null) _signInCancellation.Dispose(); } catch { }
                _signInCancellation = null;
            }
        }

        static HttpListener StartListener(out int port)
        {
            for (int candidate = 1455; candidate <= 1465; candidate++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + candidate + "/");
                try
                {
                    listener.Start();
                    port = candidate;
                    return listener;
                }
                catch
                {
                    try { listener.Close(); } catch { }
                }
            }
            port = 0;
            throw new BridgeException("Could not start the local ChatGPT callback listener on ports 1455-1465.");
        }

        static void StopListener()
        {
            try { if (_signInListener != null) _signInListener.Close(); } catch { }
            _signInListener = null;
        }

        static async Task<string[]> WaitForCallback(string expectedState, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HttpListenerContext context;
                try
                {
                    var getContext = _signInListener.GetContextAsync();
                    var completed = await Task.WhenAny(getContext, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
                    if (completed != getContext) continue;
                    context = getContext.Result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    throw new BridgeException("The ChatGPT callback listener stopped: " + e.Message);
                }

                var request = context.Request;
                var response = context.Response;
                string path = request.Url != null ? request.Url.AbsolutePath : "/";
                if (path != "/auth/callback")
                {
                    response.StatusCode = 404;
                    response.Close();
                    continue;
                }

                var query = ParseQuery(request.Url != null ? request.Url.Query : string.Empty);
                string state;
                string error;
                query.TryGetValue("state", out state);
                query.TryGetValue("error", out error);

                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    WriteBrowserPage(response, false, "The sign-in response did not match this request. Please try again.");
                    throw new BridgeException("ChatGPT sign-in state mismatch. The attempt was rejected.");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    string description;
                    query.TryGetValue("error_description", out description);
                    WriteBrowserPage(response, false, description ?? error);
                    throw new BridgeException("ChatGPT sign-in was denied: " + (string.IsNullOrEmpty(description) ? error : description));
                }

                string code;
                string clientId;
                query.TryGetValue("code", out code);
                query.TryGetValue("client_id", out clientId);
                if (string.IsNullOrEmpty(code))
                {
                    WriteBrowserPage(response, false, "No authorization code was returned.");
                    throw new BridgeException("ChatGPT sign-in did not return an authorization code.");
                }

                WriteBrowserPage(response, true, "Signed in. You can close this window and return to Unity.");
                return new[] { code, clientId ?? string.Empty };
            }
            throw new BridgeException("ChatGPT sign-in timed out after 5 minutes.");
        }

        static void WriteBrowserPage(HttpListenerResponse response, bool success, string message)
        {
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Unity AI Bridge</title>" +
                          "<style>body{font-family:system-ui,sans-serif;background:#111;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
                          ".card{max-width:520px;padding:28px;border-radius:14px;background:#1b1b1b;border:1px solid #333}" +
                          "h1{font-size:20px;margin:0 0 10px}p{color:" + (success ? "#9fe8b0" : "#ff9b9b") + ";line-height:1.5}</style></head>" +
                          "<body><div class=\"card\"><h1>Unity AI Bridge</h1><p>" + WebUtility.HtmlEncode(message) + "</p></div></body></html>";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            try { response.OutputStream.Write(bytes, 0, bytes.Length); } catch { }
            response.Close();
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;
            if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals < 0) result[Uri.UnescapeDataString(pair)] = string.Empty;
                else result[Uri.UnescapeDataString(pair.Substring(0, equals))] = Uri.UnescapeDataString(pair.Substring(equals + 1));
            }
            return result;
        }

        public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string nonce,
            string codeChallenge, string hostId, string idTokenHint, string loginHint, bool forceConsent, bool isNewRegistration)
        {
            var parameters = new List<string>
            {
                "client_id=" + Uri.EscapeDataString(clientId),
                "response_type=code",
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "scope=" + Uri.EscapeDataString(RequestedScope),
                "resource=" + Uri.EscapeDataString(ApiResource),
                "state=" + Uri.EscapeDataString(state),
                "nonce=" + Uri.EscapeDataString(nonce),
                "code_challenge_method=S256",
                "code_challenge=" + Uri.EscapeDataString(codeChallenge),
                "ext_agent_host_id=" + Uri.EscapeDataString(hostId)
            };
            if (isNewRegistration) parameters.Add("agent_name_hint=" + Uri.EscapeDataString(AgentName));
            if (!string.IsNullOrEmpty(idTokenHint)) parameters.Add("id_token_hint=" + Uri.EscapeDataString(idTokenHint));
            if (!string.IsNullOrEmpty(loginHint)) parameters.Add("login_hint=" + Uri.EscapeDataString(loginHint));
            if (forceConsent) parameters.Add("prompt=consent");
            return AuthBaseUrl + "/api/accounts/authorize?" + string.Join("&", parameters.ToArray());
        }

        // ------------------------------------------------------------------ tokens

        public static async Task<string> EnsureAccessTokenAsync(CancellationToken cancellationToken)
        {
            var secrets = BridgeSettings.Secrets;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(secrets.ChatGptAccessToken) && secrets.ChatGptExpiresAtUnix > now + 120)
                return secrets.ChatGptAccessToken;
            if (string.IsNullOrEmpty(secrets.ChatGptRefreshToken))
                throw new BridgeException("Not signed in with ChatGPT. Press 'Continue with ChatGPT'.");
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        public static async Task<string> ForceRefreshAsync(CancellationToken cancellationToken)
        {
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        static async Task<string> RefreshAsync(CancellationToken cancellationToken)
        {
            await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var secrets = BridgeSettings.Secrets;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!string.IsNullOrEmpty(secrets.ChatGptAccessToken) && secrets.ChatGptExpiresAtUnix > now + 120)
                    return secrets.ChatGptAccessToken;
                if (string.IsNullOrEmpty(secrets.ChatGptRefreshToken))
                    throw new BridgeException("ChatGPT session has no refresh token. Please sign in again.");

                var fields = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "client_id", secrets.ChatGptClientId },
                    { "refresh_token", secrets.ChatGptRefreshToken },
                    { "resource", ApiResource }
                };

                JsonValue response;
                try
                {
                    response = await StreamingHttp.PostFormAsync(AuthBaseUrl + "/api/accounts/oauth/token", fields, 60, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpStatusException e)
                {
                    string code = ExtractOAuthError(e.Body);
                    ClearTokensAfterFailure();
                    throw new BridgeException("ChatGPT session expired and could not be refreshed (" + (string.IsNullOrEmpty(code) ? e.Message : code) + "). Please sign in again.");
                }

                string access = response["access_token"].AsString();
                string refresh = response["refresh_token"].AsString();
                string scope = response["scope"].AsString(secrets.ChatGptScopes);
                int expiresIn = response["expires_in"].AsInt(3600);
                if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                    throw new BridgeException("ChatGPT refresh did not return new tokens. Please sign in again.");

                secrets.ChatGptAccessToken = access;
                secrets.ChatGptRefreshToken = refresh;
                secrets.ChatGptScopes = scope;
                secrets.ChatGptExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
                secrets.ChatGptPlanUsageEnabled = HasScope(scope, PlanScope);
                BridgeSettings.SaveSecrets();

                PlanUsageEnabled = secrets.ChatGptPlanUsageEnabled;
                Status = PlanUsageEnabled ? ChatGptStatus.SignedIn : ChatGptStatus.PlanUsageUnavailable;
                Notify();
                return access;
            }
            finally
            {
                RefreshGate.Release();
            }
        }

        static void ClearTokensAfterFailure()
        {
            var secrets = BridgeSettings.Secrets;
            secrets.ChatGptAccessToken = string.Empty;
            secrets.ChatGptRefreshToken = string.Empty;
            secrets.ChatGptIdToken = string.Empty;
            secrets.ChatGptExpiresAtUnix = 0;
            secrets.ChatGptPlanUsageEnabled = false;
            BridgeSettings.SaveSecrets();
            PlanUsageEnabled = false;
            Status = ChatGptStatus.Error;
            LastError = "ChatGPT session expired. Please sign in again.";
            Notify();
        }

        public static async Task SignOutAsync()
        {
            var secrets = BridgeSettings.Secrets;
            string refreshToken = secrets.ChatGptRefreshToken;
            string clientId = secrets.ChatGptClientId;
            if (!string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    var discovery = await StreamingHttp.GetJsonAsync(AuthBaseUrl + "/.well-known/openid-configuration", null, 15, CancellationToken.None).ConfigureAwait(false);
                    string revocation = discovery["revocation_endpoint"].AsString();
                    if (!string.IsNullOrEmpty(revocation))
                    {
                        await StreamingHttp.PostFormAsync(revocation, new Dictionary<string, string>
                        {
                            { "token", refreshToken },
                            { "token_type_hint", "refresh_token" },
                            { "client_id", clientId ?? string.Empty }
                        }, 15, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            secrets.ChatGptAccessToken = string.Empty;
            secrets.ChatGptRefreshToken = string.Empty;
            secrets.ChatGptIdToken = string.Empty;
            secrets.ChatGptEmail = string.Empty;
            secrets.ChatGptSubject = string.Empty;
            secrets.ChatGptScopes = string.Empty;
            secrets.ChatGptExpiresAtUnix = 0;
            secrets.ChatGptPlanUsageEnabled = false;
            BridgeSettings.SaveSecrets();
            BridgeSettings.Current.ChatGptModelsJson = string.Empty;
            BridgeSettings.Save();

            PlanUsageEnabled = false;
            AccountLabel = "ChatGPT account";
            Status = string.IsNullOrEmpty(secrets.ChatGptClientId) ? ChatGptStatus.NotSignedIn : ChatGptStatus.SignedOut;
            LastError = null;
            BridgeLog.Info("chatgpt", "Signed out of ChatGPT.");
            Notify();
        }

        public static async Task<JsonValue> GetModelsAsync(CancellationToken cancellationToken)
        {
            if (!IsReadyForInference) throw new BridgeException("Sign in with ChatGPT and grant plan usage before loading models.");
            string access = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + access } };
            try
            {
                return await StreamingHttp.GetJsonAsync(ApiBaseUrl + "/models", headers, 30, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpStatusException e)
            {
                if (e.StatusCode != 401) throw;
                string refreshed = await ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
                headers["Authorization"] = "Bearer " + refreshed;
                return await StreamingHttp.GetJsonAsync(ApiBaseUrl + "/models", headers, 30, cancellationToken).ConfigureAwait(false);
            }
        }

        static async Task<string> FetchJwksAsync(string authBase, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_jwksJson) && (DateTime.UtcNow - _jwksFetchedUtc).TotalMinutes < 10)
                return _jwksJson;
            string json = await StreamingHttp.GetTextAsync(authBase + "/.well-known/jwks.json", null, 30, cancellationToken).ConfigureAwait(false);
            _jwksJson = json;
            _jwksFetchedUtc = DateTime.UtcNow;
            return json;
        }

        // ------------------------------------------------------------------ validation

        public static bool ValidateIdToken(string idToken, string jwksJson, string expectedNonce, string expectedClientId,
            string expectedIssuer, out string error, out string email, out string subject)
        {
            error = null;
            email = null;
            subject = null;
            try
            {
                if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(jwksJson))
                {
                    error = "Missing ID token or JWKS.";
                    return false;
                }

                string[] parts = idToken.Split('.');
                if (parts.Length != 3) { error = "Malformed ID token."; return false; }
                byte[] headerBytes = Base64UrlDecode(parts[0]);
                byte[] payloadBytes = Base64UrlDecode(parts[1]);
                byte[] signature = Base64UrlDecode(parts[2]);
                string headerJson = Encoding.UTF8.GetString(headerBytes);
                string payloadJson = Encoding.UTF8.GetString(payloadBytes);

                JsonValue header;
                JsonValue payload;
                string parseError;
                if (!JsonValue.TryParse(headerJson, out header, out parseError) ||
                    !JsonValue.TryParse(payloadJson, out payload, out parseError))
                {
                    error = "Malformed ID token JSON: " + parseError;
                    return false;
                }
                if (!string.Equals(header["alg"].AsString(), "RS256", StringComparison.Ordinal))
                {
                    error = "Unsupported ID token algorithm: " + header["alg"].AsString();
                    return false;
                }
                string kid = header["kid"].AsString();
                if (string.IsNullOrEmpty(kid)) { error = "ID token has no key ID."; return false; }

                JsonValue jwks;
                if (!JsonValue.TryParse(jwksJson, out jwks, out parseError))
                {
                    error = "Malformed JWKS: " + parseError;
                    return false;
                }
                JsonValue key = null;
                if (jwks["keys"].IsArray)
                {
                    foreach (var candidate in jwks["keys"].ArrayValue)
                    {
                        if (string.Equals(candidate["kid"].AsString(), kid, StringComparison.Ordinal)) { key = candidate; break; }
                    }
                }
                if (key == null) { error = "No matching JWKS key for kid " + kid + "."; return false; }

                byte[] modulus = Base64UrlDecode(key["n"].AsString());
                byte[] exponent = Base64UrlDecode(key["e"].AsString());
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                    byte[] signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
                    if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    {
                        error = "ID token signature verification failed.";
                        return false;
                    }
                }

                string issuer = payload["iss"].AsString();
                if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
                {
                    error = "Unexpected ID token issuer: " + issuer;
                    return false;
                }
                bool audienceOk = string.Equals(payload["aud"].AsString(), expectedClientId, StringComparison.Ordinal);
                if (!audienceOk && payload["aud"].IsArray)
                {
                    foreach (var audience in payload["aud"].ArrayValue)
                        if (string.Equals(audience.AsString(), expectedClientId, StringComparison.Ordinal)) { audienceOk = true; break; }
                }
                if (!audienceOk) { error = "ID token audience does not match the issued client ID."; return false; }
                if (!string.Equals(payload["nonce"].AsString(), expectedNonce, StringComparison.Ordinal))
                {
                    error = "ID token nonce mismatch.";
                    return false;
                }
                long exp = (long)payload["exp"].AsNumber();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (exp > 0 && exp < now - 60) { error = "ID token has expired."; return false; }

                subject = payload["sub"].AsString();
                if (string.IsNullOrEmpty(subject)) { error = "ID token has no subject."; return false; }
                email = payload["email"].AsString();
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ helpers

        public static void InvalidateJwksCache()
        {
            _jwksJson = null;
            _jwksFetchedUtc = DateTime.MinValue;
        }

        public static string CreateCodeVerifier()
        {
            return RandomBase64Url(32);
        }

        public static string CreateCodeChallenge(string verifier)
        {
            using (var sha = SHA256.Create())
            {
                return Base64UrlEncode(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        public static string RandomBase64Url(int bytes)
        {
            byte[] data = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(data);
            return Base64UrlEncode(data);
        }

        public static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Base64UrlDecode(string value)
        {
            if (value == null) return new byte[0];
            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: throw new FormatException("Invalid base64url value.");
            }
            return Convert.FromBase64String(padded);
        }

        public static bool HasScope(string scopes, string scope)
        {
            if (string.IsNullOrEmpty(scopes) || string.IsNullOrEmpty(scope)) return false;
            foreach (var entry in scopes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(entry, scope, StringComparison.Ordinal)) return true;
            return false;
        }

        static string ExtractOAuthError(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var json = JsonValue.Parse(body);
                return json["error"].AsString(json["error"]["code"].AsString());
            }
            catch { return null; }
        }

        static void EnsureHostId()
        {
            var secrets = BridgeSettings.Secrets;
            if (!string.IsNullOrEmpty(secrets.ChatGptHostId)) return;
            secrets.ChatGptHostId = "urn:uuid:" + Guid.NewGuid().ToString();
            BridgeSettings.SaveSecrets();
        }
    }
}
