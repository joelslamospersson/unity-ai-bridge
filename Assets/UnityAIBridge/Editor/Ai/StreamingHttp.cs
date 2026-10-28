using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    public sealed class ProviderTimeoutException : BridgeException
    {
        public ProviderTimeoutException(TimeSpan timeout, bool streaming)
            : base(streaming
                ? "The provider sent no stream data for " + timeout.TotalSeconds + " seconds. Completed tool results are preserved; retry to continue."
                : "The provider did not send response headers within " + timeout.TotalSeconds + " seconds. Retry to continue.") { }
    }

    public sealed class HttpStatusException : BridgeException
    {
        public int StatusCode { get; private set; }
        public string Body { get; private set; }

        public HttpStatusException(int statusCode, string message, string body) : base(message)
        {
            StatusCode = statusCode;
            Body = body;
        }
    }

    /// <summary>Async HTTP helpers with SSE/NDJSON streaming and no editor dependencies.</summary>
    public static class StreamingHttp
    {
        static readonly HttpClient Client = new HttpClient(new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };

        public static async Task PostSseAsync(string url, string json, Dictionary<string, string> headers, int timeoutSeconds,
            Action<string> onData, CancellationToken cancellationToken, Func<bool> isComplete = null, Action onActivity = null)
        {
            await PostStreamAsync(url, json, headers, TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)), true, onData, cancellationToken, isComplete, onActivity).ConfigureAwait(false);
        }

        public static async Task PostNdjsonAsync(string url, string json, Dictionary<string, string> headers, int timeoutSeconds,
            Action<string> onLine, CancellationToken cancellationToken, Func<bool> isComplete = null, Action onActivity = null)
        {
            await PostStreamAsync(url, json, headers, TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)), false, onLine, cancellationToken, isComplete, onActivity).ConfigureAwait(false);
        }

        // A TimeSpan entry point lets regressions exercise the real transport with short deadlines.
        internal static async Task PostStreamAsync(string url, string json, Dictionary<string, string> headers, TimeSpan idleTimeout,
            bool sse, Action<string> onLine, CancellationToken cancellationToken, Func<bool> isComplete = null, Action onActivity = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Accept", sse ? "text/event-stream" : "application/x-ndjson, application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "UnityAIBridge/2.0");
                if (headers != null)
                    foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    linked.CancelAfter(idleTimeout);
                    HttpResponseMessage response;
                    try
                    {
                        response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested) throw;
                        throw new ProviderTimeoutException(idleTimeout, false);
                    }
                    catch (HttpRequestException e)
                    {
                        throw new BridgeException("Could not reach " + SafeHost(url) + ": " + e.Message);
                    }

                    using (response)
                    using (linked.Token.Register(() => { try { response.Dispose(); } catch { } }))
                    {
                        // Headers start a fresh inactivity window. There is no total-duration limit.
                        linked.CancelAfter(idleTimeout);
                        if (onActivity != null) onActivity();
                        try
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                throw new HttpStatusException((int)response.StatusCode, DescribeError(response.StatusCode, body), body);
                            }

                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                            {
                                var data = new StringBuilder();
                                var line = new StringBuilder();
                                var buffer = new char[4096];
                                bool skipLf = false;
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    linked.Token.ThrowIfCancellationRequested();
                                    int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                    cancellationToken.ThrowIfCancellationRequested();
                                    linked.Token.ThrowIfCancellationRequested();
                                    if (count == 0) break;
                                    // Reset on received fragments, including heartbeats and partial lines.
                                    linked.CancelAfter(idleTimeout);
                                    if (onActivity != null) onActivity();
                                    for (int i = 0; i < count; i++)
                                    {
                                        char c = buffer[i];
                                        if (skipLf && c == '\n') { skipLf = false; continue; }
                                        skipLf = c == '\r';
                                        if (c == '\r' || c == '\n')
                                        {
                                            if (DispatchLine(line.ToString(), sse, data, onLine, isComplete)) return;
                                            line.Length = 0;
                                        }
                                        else line.Append(c);
                                    }
                                }
                                if (line.Length > 0 && DispatchLine(line.ToString(), sse, data, onLine, isComplete)) return;
                                if (sse && data.Length > 0) onLine(data.ToString());
                            }
                        }
                        catch (Exception) when (linked.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new ProviderTimeoutException(idleTimeout, true);
                        }
                    }
                }
            }
        }

        static bool DispatchLine(string line, bool sse, StringBuilder data, Action<string> onLine, Func<bool> isComplete)
        {
            if (!sse)
            {
                if (line.Length > 0) onLine(line);
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                string payload = data.ToString();
                data.Length = 0;
                onLine(payload);
                if (payload == "[DONE]") return true;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = line.Substring(5);
                if (payload.StartsWith(" ", StringComparison.Ordinal)) payload = payload.Substring(1);
                if (data.Length > 0) data.Append('\n');
                data.Append(payload);
            }
            return isComplete != null && isComplete();
        }

        public static async Task<JsonValue> GetJsonAsync(string url, Dictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            string text = await SendAsync(HttpMethod.Get, url, null, null, headers, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            JsonValue value;
            string error;
            if (!JsonValue.TryParse(text, out value, out error))
                throw new BridgeException("The provider returned invalid JSON: " + error);
            return value;
        }

        public static async Task<JsonValue> PostJsonAsync(string url, string json, Dictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            string text = await SendAsync(HttpMethod.Post, url, json, "application/json", headers, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            JsonValue value;
            string error;
            if (!JsonValue.TryParse(text, out value, out error))
                throw new BridgeException("The provider returned invalid JSON: " + error);
            return value;
        }

        public static async Task<JsonValue> PostFormAsync(string url, Dictionary<string, string> fields, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in fields) pairs.Add(new KeyValuePair<string, string>(kv.Key, kv.Value ?? string.Empty));
            using (var content = new FormUrlEncodedContent(pairs))
            {
                string text = await SendAsync(HttpMethod.Post, url, null, null, null, timeoutSeconds, cancellationToken, content).ConfigureAwait(false);
                JsonValue value;
                string error;
                if (!JsonValue.TryParse(text, out value, out error))
                    throw new BridgeException("The authentication server returned invalid JSON: " + error);
                return value;
            }
        }

        public static async Task<string> GetTextAsync(string url, Dictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            return await SendAsync(HttpMethod.Get, url, null, null, headers, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        static async Task<string> SendAsync(HttpMethod method, string url, string json, string contentType,
            Dictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellationToken, HttpContent contentOverride = null)
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                if (contentOverride != null) request.Content = contentOverride;
                else if (json != null) request.Content = new StringContent(json, Encoding.UTF8, contentType ?? "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "UnityAIBridge/2.0");
                if (headers != null)
                    foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
                    try
                    {
                        using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false))
                        {
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                                throw new HttpStatusException((int)response.StatusCode, DescribeError(response.StatusCode, body), body);
                            return body;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested) throw;
                        throw new BridgeException("The provider did not respond within " + timeoutSeconds + " seconds.");
                    }
                    catch (HttpRequestException e)
                    {
                        throw new BridgeException("Could not reach " + SafeHost(url) + ": " + e.Message);
                    }
                }
            }
        }

        public static string DescribeError(HttpStatusCode status, string body)
        {
            string detail = null;
            try
            {
                var json = JsonValue.Parse(body);
                detail = json["error"]["message"].AsString();
                if (string.IsNullOrEmpty(detail)) detail = json["message"].AsString();
                if (string.IsNullOrEmpty(detail)) detail = json["error"].AsString();
                if (string.IsNullOrEmpty(detail)) detail = json["detail"].AsString();
                if (string.IsNullOrEmpty(detail) && json["error"].IsObject)
                    detail = json["error"]["code"].AsString();
            }
            catch { }

            if (string.IsNullOrEmpty(detail))
            {
                detail = body ?? string.Empty;
                if (detail.Length > 600) detail = detail.Substring(0, 600) + "...";
            }
            return "HTTP " + (int)status + " " + status + ": " + detail;
        }

        static string SafeHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return "the provider"; }
        }
    }
}
