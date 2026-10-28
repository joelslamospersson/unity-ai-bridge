using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static IEnumerator RunStreamTimeoutTests()
        {
            var cases = new Dictionary<string, Func<Task>>
            {
                { "stream_activity_outlives_deadline", TestActiveSseAsync },
                { "stream_fragmented_ndjson_outlives_deadline", TestActiveNdjsonAsync },
                { "stream_silence_times_out", () => TestStalledStreamAsync(false, false) },
                { "stream_stalled_error_body_times_out", () => TestStalledStreamAsync(true, false) },
                { "stream_stop_is_cancellation", () => TestStalledStreamAsync(false, true) },
                { "stream_headers_timeout_is_bounded", TestHeadersTimeoutAsync },
                { "stream_done_does_not_wait_for_disconnect", TestDoneMarkerAsync },
                { "responses_terminal_does_not_wait_for_disconnect", () => TestResponsesTerminalAsync(false) },
                { "responses_error_does_not_wait_for_disconnect", () => TestResponsesTerminalAsync(true) },
                { "agent_timeout_retry_preserves_completed_tools", TestTimeoutRecoveryAsync },
                { "agent_timeout_never_replays_partial_output", TestPartialTimeoutAsync },
                { "agent_timeout_retry_is_bounded", TestRepeatedTimeoutAsync },
                { "agent_timeout_retry_stop_is_immediate", TestRetryStopAsync }
            };
            foreach (var test in cases)
            {
                var task = Task.Run(test.Value);
                while (!task.IsCompleted) yield return null;
                Check(test.Key, task.Status == TaskStatus.RanToCompletion,
                    task.Exception != null ? task.Exception.GetBaseException().ToString() : null);
            }
        }

        static void RequireStream(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        // Every scenario uses the real HttpClient and Unity/Mono socket stack.
        // A server can keep its connection open until the client finishes or the guard fires.
        static async Task StreamScenarioAsync(Func<HttpListenerResponse, CancellationToken, Task> write,
            Func<string, CancellationToken, Task> read)
        {
            using (var listener = new HttpListener())
            using (var lifetime = new CancellationTokenSource())
            using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                string url = null;
                for (int port = 7840; port < 7860; port++)
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    try { listener.Start(); url = "http://127.0.0.1:" + port + "/"; break; }
                    catch (HttpListenerException) { }
                }
                if (url == null) throw new Exception("No streaming test port available.");
                var server = Task.Run(async () =>
                {
                    var context = await listener.GetContextAsync();
                    try
                    {
                        using (var body = new StreamReader(context.Request.InputStream)) await body.ReadToEndAsync();
                        context.Response.ContentType = "text/event-stream";
                        context.Response.SendChunked = true;
                        await write(context.Response, lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    finally { try { context.Response.Close(); } catch { } }
                });
                try { await read(url, guard.Token); }
                finally
                {
                    lifetime.Cancel();
                    listener.Stop();
                    await server;
                }
            }
        }

        static async Task StreamWriteAsync(HttpListenerResponse response, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            await response.OutputStream.FlushAsync();
        }

        static async Task TestActiveSseAsync()
        {
            var received = new List<string>();
            await StreamScenarioAsync(async (r, ct) =>
            {
                // Heartbeats, fragmented CRLF, multiline data and a final event without a newline.
                foreach (string fragment in new[] { ": ping\r", "\n\r\n", "data: hel", "lo\r", "\ndata: world 😀\r\n", "\r", "\ndata: tail" })
                {
                    await StreamWriteAsync(r, fragment);
                    await Task.Delay(200, ct);
                }
            }, (url, ct) => StreamingHttp.PostStreamAsync(url, "{}", null, TimeSpan.FromMilliseconds(700), true, received.Add, ct));
            RequireStream(received.Count == 2 && received[0] == "hello\nworld 😀" && received[1] == "tail", "SSE fragments were lost or a healthy stream timed out.");
        }

        static async Task TestActiveNdjsonAsync()
        {
            var received = new List<string>();
            await StreamScenarioAsync(async (r, ct) =>
            {
                foreach (string fragment in new[] { "{\"text\":", "\"hel", "lo 😀\"", "}\r", "\n{\"done\":true}" })
                {
                    await StreamWriteAsync(r, fragment);
                    await Task.Delay(200, ct);
                }
            }, (url, ct) => StreamingHttp.PostStreamAsync(url, "{}", null, TimeSpan.FromMilliseconds(700), false, received.Add, ct));
            RequireStream(received.Count == 2 && received[0] == "{\"text\":\"hello 😀\"}" && received[1] == "{\"done\":true}", "NDJSON fragments were lost or a healthy stream timed out.");
        }

        static async Task TestStalledStreamAsync(bool errorBody, bool cancel)
        {
            await StreamScenarioAsync(async (r, ct) =>
            {
                if (errorBody) r.StatusCode = 503;
                await StreamWriteAsync(r, errorBody ? "partial error" : "data: partial\n\n");
                await Task.Delay(Timeout.Infinite, ct);
            }, async (url, ct) =>
            {
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    if (cancel) stop.CancelAfter(200);
                    try
                    {
                        await StreamingHttp.PostStreamAsync(url, "{}", null, TimeSpan.FromMilliseconds(700), true, d => { }, stop.Token);
                        throw new Exception("Expected an idle timeout or Stop cancellation.");
                    }
                    catch (ProviderTimeoutException e)
                    {
                        RequireStream(!cancel && e.Message.Contains("no stream data"), "Timeout misclassified or missing inactivity diagnostic.");
                    }
                    catch (OperationCanceledException)
                    {
                        RequireStream(cancel && !ct.IsCancellationRequested, "Stream hung until the five-second safety guard.");
                    }
                }
            });
        }

        static async Task TestHeadersTimeoutAsync()
        {
            await StreamScenarioAsync((r, ct) => Task.Delay(Timeout.Infinite, ct), async (url, ct) =>
            {
                try
                {
                    await StreamingHttp.PostStreamAsync(url, "{}", null, TimeSpan.FromMilliseconds(700), true, d => { }, ct);
                    throw new Exception("Expected response headers timeout.");
                }
                catch (ProviderTimeoutException e) { RequireStream(e.Message.Contains("response headers"), "Wrong timeout phase."); }
            });
        }

        static async Task TestDoneMarkerAsync()
        {
            var received = new List<string>();
            await StreamScenarioAsync(async (r, ct) =>
            {
                await StreamWriteAsync(r, "data: hello\n\ndata: [DONE]\n\n");
                await Task.Delay(Timeout.Infinite, ct);
            }, (url, ct) => StreamingHttp.PostSseAsync(url, "{}", null, 10, received.Add, ct));
            RequireStream(received.Count == 2, "The DONE marker was not delivered exactly once.");
        }

        static async Task TestResponsesTerminalAsync(bool error)
        {
            var settings = BridgeSettings.Current;
            string savedBase = settings.OpenAiBaseUrl, savedKey = BridgeSettings.Secrets.OpenAiApiKey;
            try
            {
                BridgeSettings.Secrets.OpenAiApiKey = "local-timeout-test";
                await StreamScenarioAsync(async (r, ct) =>
                {
                    string terminal = error
                        ? Sse(JsonValue.Obj("type", "response.failed", "response", JsonValue.Obj("error", JsonValue.Obj("message", "fixture failure"))))
                        : Sse(JsonValue.Obj("type", "response.completed", "response", JsonValue.Obj("status", "completed", "usage", JsonValue.Obj("output_tokens", 2))));
                    await StreamWriteAsync(r, Sse(JsonValue.Obj("type", "response.output_text.delta", "delta", "Hello")) + terminal);
                    await Task.Delay(Timeout.Infinite, ct);
                }, async (url, ct) =>
                {
                    settings.OpenAiBaseUrl = url.TrimEnd('/');
                    try
                    {
                        var turn = await new OpenAiResponsesProvider(false).StreamTurnAsync(new LlmRequest { Model = "fixture" }, e => { }, ct);
                        RequireStream(!error && turn.Text == "Hello" && turn.Usage.CompletionTokens == 2, "Completed response lost text or usage.");
                    }
                    catch (BridgeException e) when (error) { RequireStream(e.Message == "fixture failure", "Provider failure was hidden by a timeout."); }
                });
            }
            finally { settings.OpenAiBaseUrl = savedBase; BridgeSettings.Secrets.OpenAiApiKey = savedKey; }
        }

        sealed class TimeoutTestProvider : ILiveProvider
        {
            public int Calls;
            public Func<int, LlmRequest, Action<StreamEvent>, ProviderTurn> Respond;
            public string Id { get { return "timeout-test"; } }
            public string DisplayName { get { return Id; } }
            public string BillingNote { get { return "mock"; } }
            public bool IsConfigured(out string reason) { reason = null; return true; }
            public Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct) { return Task.FromResult(new List<ModelInfo>()); }
            public Task<string> TestConnectionAsync(CancellationToken ct) { return Task.FromResult("mock"); }
            public Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Respond(++Calls, request, onEvent));
            }
        }

        static async Task TestTimeoutRecoveryAsync()
        {
            var provider = new TimeoutTestProvider();
            int toolsRun = 0, commits = 0;
            provider.Respond = (attempt, r, emit) =>
            {
                if (attempt == 1) return new ProviderTurn { ToolCalls = new List<ToolCall> { new ToolCall { Id = "already-done", Name = "project_summary", Arguments = "{}" } } };
                RequireStream(r.Messages[r.Messages.Count - 1].Role == "tool" && r.Messages[r.Messages.Count - 1].Content.Contains("recorded-result"), "Retry lost committed tool output.");
                if (attempt == 2) throw new ProviderTimeoutException(TimeSpan.FromSeconds(1), true);
                emit(StreamEvent.Delta("Recovered"));
                return new ProviderTurn { Text = "Recovered" };
            };
            var transcript = new List<LlmMessage> { new LlmMessage { Role = "user", Content = "Inspect" } };
            var turn = await AgentRunner.RunAsync(provider, new LlmRequest
            {
                Tools = new List<ToolSpec> { new ToolSpec { Name = "project_summary" } },
                ToolExecutor = (call, ct) => { toolsRun++; return Task.FromResult(JsonValue.Obj("structuredContent", JsonValue.Obj("data", "recorded-result"))); },
                ToolResultCommitted = () => commits++
            }, transcript, e => { }, CancellationToken.None);
            RequireStream(provider.Calls == 3 && toolsRun == 1 && commits == 1 && turn.Text == "Recovered" && transcript.Count == 4, "Timeout retry duplicated tool work or damaged the transcript.");
        }

        static async Task TestPartialTimeoutAsync()
        {
            foreach (var kind in new[] { StreamEventKind.TextDelta, StreamEventKind.ReasoningDelta, StreamEventKind.ToolCallStarted, StreamEventKind.ToolCallArguments })
            {
                var provider = new TimeoutTestProvider { Respond = (n, r, emit) =>
                {
                    emit(new StreamEvent { Kind = kind, Text = "partial" });
                    throw new ProviderTimeoutException(TimeSpan.FromSeconds(1), true);
                } };
                try
                {
                    await AgentRunner.StreamWithTimeoutRecoveryAsync(provider, new LlmRequest(), e => { }, CancellationToken.None);
                    throw new Exception("Expected timeout after partial output.");
                }
                catch (ProviderTimeoutException) { RequireStream(provider.Calls == 1, "Partial output was replayed: " + kind); }
            }
        }

        static async Task TestRepeatedTimeoutAsync()
        {
            var provider = new TimeoutTestProvider { Respond = (n, r, emit) => { throw new ProviderTimeoutException(TimeSpan.FromSeconds(1), false); } };
            try
            {
                await AgentRunner.StreamWithTimeoutRecoveryAsync(provider, new LlmRequest(), e => { }, CancellationToken.None);
                throw new Exception("Expected timeout after one retry.");
            }
            catch (ProviderTimeoutException) { RequireStream(provider.Calls == 2, "Timeout retry was not bounded to one."); }
        }

        static async Task TestRetryStopAsync()
        {
            using (var stop = new CancellationTokenSource())
            {
                var provider = new TimeoutTestProvider { Respond = (n, r, emit) => { throw new ProviderTimeoutException(TimeSpan.FromSeconds(1), true); } };
                try
                {
                    await AgentRunner.StreamWithTimeoutRecoveryAsync(provider, new LlmRequest(), e => stop.Cancel(), stop.Token);
                    throw new Exception("Expected Stop to cancel retry.");
                }
                catch (OperationCanceledException) { RequireStream(provider.Calls == 1, "Stop allowed another provider request."); }
            }
        }
    }
}
