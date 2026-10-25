using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
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
            Action<string> onData, CancellationToken cancellationToken)
        {
            await PostStreamAsync(url, json, headers, timeoutSeconds, true, onData, cancellationToken).ConfigureAwait(false);
        }

        public static async Task PostNdjsonAsync(string url, string json, Dictionary<string, string> headers, int timeoutSeconds,
            Action<string> onLine, CancellationToken cancellationToken)
        {
            await PostStreamAsync(url, json, headers, timeoutSeconds, false, onLine, cancellationToken).ConfigureAwait(false);
        }

        static async Task PostStreamAsync(string url, string json, Dictionary<string, string> headers, int timeoutSeconds,
            bool sse, Action<string> onLine, CancellationToken cancellationToken)
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
                    linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
                    HttpResponseMessage response;
                    try
                    {
                        response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
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

                    using (response)
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
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string line;
                                try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                                catch (OperationCanceledException)
                                {
                                    if (cancellationToken.IsCancellationRequested) throw;
                                    throw new BridgeException("The provider stream timed out.");
                                }
                                if (line == null) break;

                                if (!sse)
                                {
                                    if (line.Length > 0) onLine(line);
                                    continue;
                                }

                                if (line.Length == 0)
                                {
                                    if (data.Length > 0)
                                    {
                                        onLine(data.ToString());
                                        data.Length = 0;
                                    }
                                    continue;
                                }
                                if (line.StartsWith(":", StringComparison.Ordinal)) continue;
                                if (line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    string payload = line.Substring(5);
                                    if (payload.StartsWith(" ", StringComparison.Ordinal)) payload = payload.Substring(1);
                                    if (data.Length > 0) data.Append('\n');
                                    data.Append(payload);
                                }
                            }
                            if (sse && data.Length > 0) onLine(data.ToString());
                        }
                    }
                }
            }
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
