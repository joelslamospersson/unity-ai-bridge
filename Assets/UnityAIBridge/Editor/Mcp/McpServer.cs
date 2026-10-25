using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityAIBridge
{
    public sealed class McpCallRecord
    {
        public DateTime TimeUtc = DateTime.UtcNow;
        public string Method;
        public string Tool;
        public double DurationMs;
        public bool Ok;
        public string Summary;
    }

    /// <summary>
    /// Minimal, dependency-free MCP (JSON-RPC 2.0) server over the Streamable HTTP
    /// transport, bound to 127.0.0.1 only and protected by a bearer token.
    /// </summary>
    public sealed class McpServer
    {
        public static McpServer Current;

        readonly int _port;
        readonly string _token;
        readonly string _sessionId = Guid.NewGuid().ToString("N");
        readonly List<McpCallRecord> _recent = new List<McpCallRecord>();
        HttpListener _listener;
        CancellationTokenSource _cancellation;
        Task _loop;

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }
        public int Port { get { return _port; } }
        public string Url { get { return "http://127.0.0.1:" + _port + "/mcp"; } }
        public string Token { get { return _token; } }

        public McpServer(int port, string token)
        {
            _port = port;
            _token = token ?? string.Empty;
        }

        public List<McpCallRecord> RecentCalls
        {
            get { lock (_recent) return new List<McpCallRecord>(_recent); }
        }

        public bool Start(out string error)
        {
            error = null;
            if (IsRunning) return true;
            if (string.IsNullOrEmpty(_token))
            {
                error = "The MCP bearer token is empty. Generate one in the MCP tab.";
                return false;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                _listener.Start();
                _cancellation = new CancellationTokenSource();
                IsRunning = true;
                LastError = null;
                _loop = Task.Run(() => LoopAsync(_cancellation.Token));
                BridgeLog.Success("mcp", "MCP server listening on " + Url + " (localhost only, bearer auth).");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                error = "Could not start the MCP server on 127.0.0.1:" + _port + ": " + e.Message;
                BridgeLog.Error("mcp", error);
                try { if (_listener != null) _listener.Close(); } catch { }
                _listener = null;
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { if (_cancellation != null) _cancellation.Cancel(); } catch { }
            try { if (_listener != null) _listener.Close(); } catch { }
            _listener = null;
            _cancellation = null;
            _loop = null;
            BridgeLog.Info("mcp", "MCP server stopped.");
        }

        async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning && _listener != null)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception)
                {
                    if (token.IsCancellationRequested || !IsRunning) break;
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(() => HandleAsync(context, token));
            }
        }

        async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            string methodName = string.Empty;
            string toolName = string.Empty;
            bool ok = false;
            try
            {
                var request = context.Request;
                var response = context.Response;

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                string path = request.Url != null ? request.Url.AbsolutePath : "/";

                if (request.HttpMethod == "GET")
                {
                    if (path == "/health" || path.EndsWith("/health", StringComparison.Ordinal))
                    {
                        await WriteJsonAsync(response, 200, JsonValue.Obj(
                            "status", "ok",
                            "name", "unity-ai-bridge",
                            "version", McpTools.ServerVersion,
                            "tools", McpTools.Tools.Count,
                            "auth", "bearer",
                            "permission", Permissions.LevelName), token).ConfigureAwait(false);
                        return;
                    }
                    await WriteJsonAsync(response, 405, JsonValue.Obj("error", "Use POST for MCP JSON-RPC."), token).ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod != "POST")
                {
                    await WriteJsonAsync(response, 405, JsonValue.Obj("error", "Method not allowed."), token).ConfigureAwait(false);
                    return;
                }

                if (!IsAuthorized(request, path))
                {
                    BridgeLog.Warn("mcp", "Rejected unauthorized request from " + (request.RemoteEndPoint != null ? request.RemoteEndPoint.ToString() : "unknown"));
                    await WriteJsonAsync(response, 401, JsonValue.Obj(
                        "jsonrpc", "2.0",
                        "id", JsonValue.Null(),
                        "error", JsonValue.Obj("code", -32001, "message", "Unauthorized. Use 'Authorization: Bearer <token>'.")), token).ConfigureAwait(false);
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                if (body.Length > 8 * 1024 * 1024)
                {
                    await WriteJsonAsync(response, 413, JsonValue.Obj("error", "Request body too large."), token).ConfigureAwait(false);
                    return;
                }

                JsonValue document;
                string parseError;
                if (!JsonValue.TryParse(body, out document, out parseError))
                {
                    await WriteJsonAsync(response, 200, ErrorResponse(JsonValue.Null(), -32700, "Parse error: " + parseError), token).ConfigureAwait(false);
                    return;
                }

                if (document.IsArray)
                {
                    var batch = JsonValue.NewArray();
                    foreach (var item in document.ArrayValue)
                    {
                        var result = await ProcessAsync(item, token).ConfigureAwait(false);
                        if (result != null) batch.Add(result);
                    }
                    if (batch.Count == 0)
                    {
                        response.StatusCode = 202;
                        response.Close();
                        return;
                    }
                    await WriteJsonAsync(response, 200, batch, token).ConfigureAwait(false);
                    ok = true;
                    return;
                }

                methodName = document["method"].AsString();
                var parameters = document["params"];
                if (string.Equals(methodName, "tools/call", StringComparison.Ordinal)) toolName = parameters["name"].AsString();

                var reply = await ProcessAsync(document, token).ConfigureAwait(false);
                if (reply == null)
                {
                    response.StatusCode = 202;
                    response.Close();
                    ok = true;
                    return;
                }

                ok = reply["error"].IsNull;
                await WriteJsonAsync(response, 200, reply, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                BridgeLog.Warn("mcp", "Request handling failed: " + e.Message);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                stopwatch.Stop();
                if (methodName.Length > 0)
                {
                    lock (_recent)
                    {
                        _recent.Add(new McpCallRecord
                        {
                            Method = methodName,
                            Tool = toolName,
                            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                            Ok = ok,
                            Summary = toolName.Length > 0 ? toolName : methodName
                        });
                        if (_recent.Count > 50) _recent.RemoveRange(0, _recent.Count - 50);
                    }
                }
            }
        }

        async Task<JsonValue> ProcessAsync(JsonValue request, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (!request.IsObject) return ErrorResponse(JsonValue.Null(), -32600, "Invalid Request");

            var id = request["id"];
            string method = request["method"].AsString();
            var parameters = request["params"];
            bool isNotification = id.IsNull;

            if (string.IsNullOrEmpty(method))
                return isNotification ? null : ErrorResponse(id, -32600, "Missing method.");

            try
            {
                if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null;

                switch (method)
                {
                    case "initialize":
                        return OkResponse(id, JsonValue.Obj(
                            "protocolVersion", parameters["protocolVersion"].AsString("2025-06-18"),
                            "capabilities", JsonValue.Obj(
                                "tools", JsonValue.Obj("listChanged", false),
                                "logging", JsonValue.NewObject()),
                            "serverInfo", JsonValue.Obj("name", "unity-ai-bridge", "version", McpTools.ServerVersion),
                            "instructions", McpTools.Instructions));

                    case "ping":
                        return OkResponse(id, JsonValue.NewObject());

                    case "tools/list":
                        return OkResponse(id, JsonValue.Obj("tools", McpTools.ToolsList()));

                    case "tools/call":
                        {
                            string name = parameters["name"].AsString();
                            var arguments = parameters["arguments"].IsObject ? parameters["arguments"] : JsonValue.NewObject();
                            var result = McpTools.InvokeFromAnyThread(name, arguments);
                            return OkResponse(id, result);
                        }

                    case "resources/list":
                        return OkResponse(id, JsonValue.Obj("resources", JsonValue.NewArray()));

                    case "resources/templates/list":
                        return OkResponse(id, JsonValue.Obj("resourceTemplates", JsonValue.NewArray()));

                    case "prompts/list":
                        return OkResponse(id, JsonValue.Obj("prompts", JsonValue.NewArray()));

                    case "logging/setLevel":
                        return OkResponse(id, JsonValue.NewObject());

                    default:
                        return ErrorResponse(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception e)
            {
                return ErrorResponse(id, -32603, e.Message);
            }
        }

        static JsonValue OkResponse(JsonValue id, JsonValue result)
        {
            return JsonValue.Obj("jsonrpc", "2.0", "id", id, "result", result);
        }

        static JsonValue ErrorResponse(JsonValue id, int code, string message)
        {
            return JsonValue.Obj("jsonrpc", "2.0", "id", id, "error", JsonValue.Obj("code", code, "message", message));
        }

        bool IsAuthorized(HttpListenerRequest request, string path)
        {
            string provided = null;
            string header = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = header.Substring(7).Trim();
            if (string.IsNullOrEmpty(provided)) provided = request.Headers["X-Bridge-Token"];

            if (string.IsNullOrEmpty(provided) && request.Url != null)
            {
                string query = request.Url.Query ?? string.Empty;
                if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
                foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!pair.StartsWith("token=", StringComparison.Ordinal)) continue;
                    provided = Uri.UnescapeDataString(pair.Substring("token=".Length));
                    break;
                }
                if (string.IsNullOrEmpty(provided) && path.StartsWith("/mcp/", StringComparison.Ordinal))
                    provided = Uri.UnescapeDataString(path.Substring("/mcp/".Length));
            }

            if (string.IsNullOrEmpty(provided)) return false;
            return FixedTimeEquals(provided, _token);
        }

        static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int length = Math.Max(a.Length, b.Length);
            if (length == 0) return false;
            for (int i = 0; i < length; i++)
                diff |= a[i % a.Length] ^ b[i % b.Length];
            return diff == 0;
        }

        async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, JsonValue body, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToJson(false));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Mcp-Session-Id"] = _sessionId;
            try
            {
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        // ----- static lifecycle helpers -------------------------------------------------

        public static void StartFromSettings()
        {
            BridgeSettings.Load();
            var settings = BridgeSettings.Current;
            string token = BridgeSettings.EnsureMcpToken();
            if (Current != null && Current.IsRunning && Current.Port == settings.McpPort)
            {
                BridgeLog.Info("mcp", "MCP server already running on port " + settings.McpPort + ".");
                return;
            }

            if (Current != null) Current.Stop();
            var server = new McpServer(settings.McpPort, token);
            string error;
            if (server.Start(out error))
            {
                Current = server;
                settings.McpEnabled = true;
                BridgeSettings.Save();
            }
            else
            {
                Current = null;
                BridgeLog.Error("mcp", error);
            }
        }

        public static void StopCurrent()
        {
            if (Current != null)
            {
                Current.Stop();
                Current = null;
            }
        }

        public static void RestartFromSettings()
        {
            StopCurrent();
            StartFromSettings();
        }

        public static void StopForReload()
        {
            if (Current != null) Current.Stop();
        }

        public static string ClientConfigJson()
        {
            string token = BridgeSettings.EnsureMcpToken();
            int port = BridgeSettings.Current.McpPort;
            return JsonValue.Obj(
                "mcpServers", JsonValue.Obj(
                    "unity-ai-bridge", JsonValue.Obj(
                        "url", "http://127.0.0.1:" + port + "/mcp",
                        "headers", JsonValue.Obj("Authorization", "Bearer " + token)))).ToJson(true);
        }

        public static string CodexCommand()
        {
            int port = BridgeSettings.Current.McpPort;
            string token = BridgeSettings.EnsureMcpToken();
            return "codex mcp add unity-ai-bridge --url http://127.0.0.1:" + port + "/mcp --bearer-token-env-var UNITY_AI_BRIDGE_TOKEN\n" +
                   "# then export the token in the shell that launches Codex:\n" +
                   "export UNITY_AI_BRIDGE_TOKEN=\"" + token + "\"";
        }
    }
}
