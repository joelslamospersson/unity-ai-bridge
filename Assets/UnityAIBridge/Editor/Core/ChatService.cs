using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    [Serializable]
    public class ChatMessage
    {
        public string Id;
        public string Role; // user, assistant, system, tool, error
        public string Content;
        public string Reasoning;
        public string TimeLocal;
        public string Model;
        public string Usage;
        public bool IsError;
        public bool IsStreaming;
        public string ToolName;
        public string ToolStatus;
        public bool ToolOk = true;
        public List<string> ChangeIds = new List<string>();
    }

    [Serializable]
    public class ChatSessionData
    {
        public string Provider;
        public string Model;
        public List<ChatMessage> Display = new List<ChatMessage>();
        public List<LlmMessage> Transcript = new List<LlmMessage>();
    }

    /// <summary>
    /// Live chat orchestration for ChatGPT / DeepSeek / OpenAI API / Ollama.
    /// Network work is async; Unity APIs are only touched on the main thread.
    /// </summary>
    public static class ChatService
    {
        static readonly Dictionary<string, ChatSessionData> Sessions = new Dictionary<string, ChatSessionData>(StringComparer.Ordinal);
        static readonly List<string> _attached = new List<string>();
        static readonly object StreamLock = new object();
        static readonly StringBuilder PendingText = new StringBuilder();
        static readonly StringBuilder PendingReasoning = new StringBuilder();

        static string _activeProvider;
        static bool _flushScheduled;
        static int _runSerial;
        static CancellationTokenSource _cancellation;
        static bool _chatGptSubscribed;
        static List<ModelInfo> _models = new List<ModelInfo>();
        static bool _loadingModels;
        static string _testResult;
        static bool _testing;

        public static bool IsBusy { get; private set; }
        public static string StatusMessage { get; private set; }
        public static string LastError { get; private set; }
        public static ProviderConnectionState ConnectionState { get; private set; }
        public static string ConnectionMessage { get; private set; }
        public static event Action Changed;

        public static List<string> AttachedFiles { get { return _attached; } }
        public static List<ModelInfo> Models { get { return _models; } }
        public static bool IsLoadingModels { get { return _loadingModels; } }
        public static string LastTestResult { get { return _testResult; } }
        public static bool IsTesting { get { return _testing; } }

        public static string CurrentProviderId
        {
            get
            {
                if (string.IsNullOrEmpty(_activeProvider)) _activeProvider = BridgeSettings.Current.Provider;
                return _activeProvider;
            }
        }

        public static ChatSessionData CurrentSession { get { return GetSession(CurrentProviderId); } }
        public static string CurrentModel { get { return CurrentSession.Model; } }
        public static List<ChatMessage> Messages { get { return CurrentSession.Display; } }

        public static void Raise()
        {
            var handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        public static void EnsureLoaded()
        {
            BridgeSettings.Load();
            string provider = BridgeSettings.Current.Provider;
            if (string.IsNullOrEmpty(_activeProvider)) _activeProvider = provider;
            GetSession(_activeProvider);
            if (!_chatGptSubscribed)
            {
                _chatGptSubscribed = true;
                ChatGptAuth.Changed += OnChatGptChanged;
            }
            LoadCachedModels(_activeProvider);
            if (_activeProvider == BridgeSettings.ProviderDeepSeek && _models.Count == 0 && string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
                _models = DeepSeekProvider.VerifiedModels();
            if (string.IsNullOrEmpty(CurrentSession.Model)) CurrentSession.Model = DefaultModel(_activeProvider);
            UpdateConnectionFromProvider();
            if (_models.Count == 0 && (ProviderFactory.IsConfigured(_activeProvider, out _) || _activeProvider == BridgeSettings.ProviderChatGpt))
                RefreshModels(false);
            Raise();
        }

        static void OnChatGptChanged()
        {
            MainThread.Run(() =>
            {
                UpdateConnectionFromProvider();
                if (ChatGptAuth.IsReadyForInference) RefreshModels(false);
                else if (CurrentProviderId == BridgeSettings.ProviderChatGpt)
                {
                    _models = new List<ModelInfo>();
                    SaveSession(CurrentProviderId);
                }
                Raise();
            });
        }

        // ------------------------------------------------------------------ sessions

        static ChatSessionData GetSession(string provider)
        {
            if (string.IsNullOrEmpty(provider)) provider = BridgeSettings.ProviderDeepSeek;
            ChatSessionData session;
            if (Sessions.TryGetValue(provider, out session)) return session;

            session = null;
            try
            {
                string path = BridgePaths.ChatSessionFile(provider);
                if (File.Exists(path))
                    session = JsonUtility.FromJson<ChatSessionData>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e)
            {
                BridgeLog.Warn("chat", "Could not load " + provider + " chat: " + e.Message);
            }
            if (session == null) session = new ChatSessionData { Provider = provider };
            if (session.Display == null) session.Display = new List<ChatMessage>();
            if (session.Transcript == null) session.Transcript = new List<LlmMessage>();
            session.Provider = provider;
            if (string.IsNullOrEmpty(session.Model)) session.Model = DefaultModel(provider);
            Sessions[provider] = session;
            return session;
        }

        static void SaveSession(string provider)
        {
            try
            {
                var session = GetSession(provider);
                if (session.Display.Count > 200)
                    session.Display.RemoveRange(0, session.Display.Count - 200);
                if (session.Transcript.Count > 200)
                    session.Transcript.RemoveRange(0, session.Transcript.Count - 200);
                BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
                string path = BridgePaths.ChatSessionFile(provider);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(session, false), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                BridgeLog.Warn("chat", "Could not save chat: " + e.Message);
            }
        }

        static string DefaultModel(string provider)
        {
            var settings = BridgeSettings.Current;
            switch (provider)
            {
                case BridgeSettings.ProviderDeepSeek: return string.IsNullOrEmpty(settings.DeepSeekModel) ? "deepseek-flash" : settings.DeepSeekModel;
                case BridgeSettings.ProviderChatGpt: return settings.ChatGptModel ?? string.Empty;
                case BridgeSettings.ProviderOpenAi: return settings.OpenAiModel;
                case BridgeSettings.ProviderOllama: return settings.OllamaModel;
                default: return string.Empty;
            }
        }

        static void PersistModel(string provider, string model)
        {
            var settings = BridgeSettings.Current;
            switch (provider)
            {
                case BridgeSettings.ProviderDeepSeek: settings.DeepSeekModel = model; break;
                case BridgeSettings.ProviderChatGpt: settings.ChatGptModel = model; break;
                case BridgeSettings.ProviderOpenAi: settings.OpenAiModel = model; break;
                case BridgeSettings.ProviderOllama: settings.OllamaModel = model; break;
            }
            BridgeSettings.Save();
        }

        public static void SelectModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return;
            CurrentSession.Model = model;
            PersistModel(CurrentProviderId, model);
            SaveSession(CurrentProviderId);
            Raise();
        }

        public static void SwitchProvider(string provider)
        {
            if (string.IsNullOrEmpty(provider) || provider == CurrentProviderId) return;
            SaveSession(CurrentProviderId);
            if (IsBusy) Stop();

            Interlocked.Increment(ref _runSerial);
            IsBusy = false;
            _cancellation = null;

            BridgeSettings.Current.Provider = provider;
            BridgeSettings.Save();
            _activeProvider = provider;
            GetSession(provider);
            if (string.IsNullOrEmpty(CurrentSession.Model)) CurrentSession.Model = DefaultModel(provider);
            _models = new List<ModelInfo>();
            _loadingModels = false;
            ConnectionState = ProviderConnectionState.NotConfigured;
            ConnectionMessage = null;
            LoadCachedModels(provider);
            UpdateConnectionFromProvider();
            RefreshModels(false);
            Raise();
        }

        // ------------------------------------------------------------------ models / connection

        public static void RefreshModels(bool force)
        {
            if (_loadingModels) return;
            string providerId = CurrentProviderId;
            var provider = ProviderFactory.Create(providerId);
            if (provider == null)
            {
                ConnectionState = ProviderConnectionState.NotConfigured;
                ConnectionMessage = "Unknown provider.";
                Raise();
                return;
            }

            if (providerId == BridgeSettings.ProviderChatGpt && ChatGptAuth.IsBusy)
            {
                ConnectionState = ProviderConnectionState.SigningIn;
                ConnectionMessage = "Finish signing in in your browser...";
                Raise();
                return;
            }

            string reason = null;
            bool configured = ChatGptAuth.IsBusy ? false : provider.IsConfigured(out reason);
            if (!configured)
            {
                ConnectionState = providerId == BridgeSettings.ProviderChatGpt && ChatGptAuth.IsBusy
                    ? ProviderConnectionState.SigningIn
                    : ProviderConnectionState.NotConfigured;
                ConnectionMessage = reason;
                if (providerId == BridgeSettings.ProviderDeepSeek) _models = DeepSeekProvider.VerifiedModels();
                if (providerId == BridgeSettings.ProviderChatGpt) _models = new List<ModelInfo>();
                Raise();
                return;
            }

            _loadingModels = true;
            ConnectionMessage = "Loading models...";
            Raise();
            Task.Run(async () =>
            {
                try
                {
                    var list = await provider.GetModelsAsync(CancellationToken.None).ConfigureAwait(false);
                    MainThread.Run(() =>
                    {
                        _loadingModels = false;
                        if (CurrentProviderId != providerId) return;
                        _models = list ?? new List<ModelInfo>();
                        ConnectionState = ProviderConnectionState.Connected;
                        ConnectionMessage = "Connected · " + _models.Count + " model(s)";
                        CacheModels(providerId, _models);
                        if (_models.Count > 0 && !_models.Exists(m => m.Id == CurrentSession.Model))
                        {
                            CurrentSession.Model = _models[0].Id;
                            PersistModel(providerId, CurrentSession.Model);
                        }
                        SaveSession(providerId);
                        Raise();
                    });
                }
                catch (Exception e)
                {
                    MainThread.Run(() =>
                    {
                        _loadingModels = false;
                        if (CurrentProviderId != providerId) return;
                        ConnectionState = ProviderConnectionState.Error;
                        ConnectionMessage = e.Message;
                        if (providerId == BridgeSettings.ProviderDeepSeek && _models.Count == 0)
                        {
                            _models = DeepSeekProvider.VerifiedModels();
                            if (!_models.Exists(m => m.Id == CurrentSession.Model))
                            {
                                CurrentSession.Model = "deepseek-flash";
                                PersistModel(providerId, CurrentSession.Model);
                            }
                        }
                        BridgeLog.Warn("chat", "Could not load models for " + providerId + ": " + e.Message);
                        Raise();
                    });
                }
            });
        }

        static void UpdateConnectionFromProvider()
        {
            string providerId = CurrentProviderId;
            if (providerId == BridgeSettings.ProviderChatGpt)
            {
                if (ChatGptAuth.IsBusy) { ConnectionState = ProviderConnectionState.SigningIn; ConnectionMessage = "Signing in..."; return; }
                if (ChatGptAuth.Status == ChatGptStatus.PlanUsageUnavailable) { ConnectionState = ProviderConnectionState.NotConfigured; ConnectionMessage = ChatGptAuth.LastError ?? "ChatGPT plan usage not granted."; _models = new List<ModelInfo>(); return; }
                if (!ChatGptAuth.IsReadyForInference) { ConnectionState = ProviderConnectionState.NotConfigured; ConnectionMessage = ChatGptAuth.Status == ChatGptStatus.Error ? ChatGptAuth.LastError : "Not signed in."; _models = new List<ModelInfo>(); return; }
                ConnectionState = ProviderConnectionState.Connected;
                ConnectionMessage = "Signed in as " + ChatGptAuth.AccountLabel;
                return;
            }

            var provider = ProviderFactory.Create(providerId);
            string reason = null;
            if (provider == null || !provider.IsConfigured(out reason))
            {
                ConnectionState = ProviderConnectionState.NotConfigured;
                ConnectionMessage = reason ?? "Not configured.";
                return;
            }
            if (ConnectionState == ProviderConnectionState.Connected) return;
            ConnectionState = ProviderConnectionState.Ready;
            ConnectionMessage = providerId == BridgeSettings.ProviderDeepSeek
                ? "API key set · press Test Connection or send a message"
                : "Configured";
        }

        public static void TestConnection()
        {
            if (_testing) return;
            var provider = ProviderFactory.Create(CurrentProviderId);
            if (provider == null) { _testResult = "Unknown provider."; Raise(); return; }
            string reason = null;
            if (!provider.IsConfigured(out reason)) { _testResult = reason; ConnectionState = ProviderConnectionState.NotConfigured; ConnectionMessage = reason; Raise(); return; }
            _testing = true;
            _testResult = "Testing...";
            Raise();
            string providerId = CurrentProviderId;
            Task.Run(async () =>
            {
                string result;
                bool ok = true;
                try { result = await provider.TestConnectionAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) { result = e.Message; ok = false; }
                MainThread.Run(() =>
                {
                    _testing = false;
                    _testResult = result;
                    if (CurrentProviderId == providerId)
                    {
                        ConnectionState = ok ? ProviderConnectionState.Connected : ProviderConnectionState.Error;
                        ConnectionMessage = result;
                        if (ok) RefreshModels(false);
                    }
                    Raise();
                });
            });
        }

        // ------------------------------------------------------------------ chat

        public static void Send(string text)
        {
            if (IsBusy) return;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            EnsureLoaded();

            string providerId = CurrentProviderId;
            var provider = ProviderFactory.Create(providerId);
            string reason = null;
            if (provider == null || !provider.IsConfigured(out reason))
            {
                AddDisplay("error", "Cannot send: " + (reason ?? "provider is not configured.") + "\nOpen Settings to connect a provider.", true);
                ConnectionState = ProviderConnectionState.NotConfigured;
                ConnectionMessage = reason;
                Raise();
                return;
            }

            string model = CurrentSession.Model;
            if (string.IsNullOrEmpty(model) && _models.Count > 0)
            {
                model = _models[0].Id;
                CurrentSession.Model = model;
                PersistModel(providerId, model);
            }
            if (string.IsNullOrEmpty(model))
            {
                AddDisplay("error", "No model selected. Press the refresh button next to the model dropdown or sign in again.", true);
                Raise();
                return;
            }

            var session = CurrentSession;
            AddDisplay("user", text, false);
            session.Transcript.Add(new LlmMessage { Role = "user", Content = text });

            var assistant = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Role = "assistant",
                Content = string.Empty,
                TimeLocal = DateTime.Now.ToString("HH:mm:ss"),
                Model = model,
                IsStreaming = true
            };
            session.Display.Add(assistant);

            string context;
            try { context = ProjectService.BuildContext(BridgeSettings.Current, new List<string>(_attached), false); }
            catch (Exception e) { context = "(project context unavailable: " + e.Message + ")"; }
            string instructions = PromptBuilder.AgentSystemPrompt(BridgeSettings.Current) + "\n\nPROJECT CONTEXT\n" + context;
            var settings = BridgeSettings.Current;
            settings.MaxContextCharacters = Mathf.Max(8000, settings.MaxContextCharacters);
            if (instructions.Length > settings.MaxContextCharacters + 12000)
                instructions = instructions.Substring(0, settings.MaxContextCharacters + 12000) + "\n... context truncated ...";

            var request = new LlmRequest
            {
                ProviderId = providerId,
                Model = model,
                Instructions = instructions,
                Messages = session.Transcript,
                Tools = McpTools.GetToolSpecs(Permissions.CanPropose),
                Temperature = settings.Temperature,
                MaxOutputTokens = providerId == BridgeSettings.ProviderDeepSeek ? settings.DeepSeekMaxTokens : settings.MaxOutputTokens,
                Thinking = settings.DeepSeekThinking,
                ReasoningEffort = settings.DeepSeekReasoningEffort,
                MaxSteps = settings.MaxAgentSteps
            };

            int run = Interlocked.Increment(ref _runSerial);
            ResetStreamBuffers();
            IsBusy = true;
            LastError = null;
            StatusMessage = "Connecting to " + provider.DisplayName + "...";
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            Raise();

            Task.Run(async () =>
            {
                try
                {
                    var turn = await AgentRunner.RunAsync(provider, request, session.Transcript, evt => OnProviderEvent(evt, assistant.Id, run), token).ConfigureAwait(false);
                    MainThread.Run(() => Complete(turn, assistant.Id, run, providerId));
                }
                catch (OperationCanceledException)
                {
                    MainThread.Run(() => Cancelled(run, providerId));
                }
                catch (Exception e)
                {
                    MainThread.Run(() => Failed(e, run, providerId));
                }
            });
        }

        static void OnProviderEvent(StreamEvent evt, string assistantId, int run)
        {
            if (evt == null) return;
            if (run != Volatile.Read(ref _runSerial)) return;

            if (evt.Kind == StreamEventKind.TextDelta)
            {
                lock (StreamLock) PendingText.Append(evt.Text);
                ScheduleFlush(assistantId, run);
                return;
            }
            if (evt.Kind == StreamEventKind.ReasoningDelta)
            {
                lock (StreamLock) PendingReasoning.Append(evt.Text);
                ScheduleFlush(assistantId, run);
                return;
            }

            MainThread.Run(() =>
            {
                if (run != Volatile.Read(ref _runSerial)) return;
                HandleEvent(evt, assistantId);
            });
        }

        static void ScheduleFlush(string assistantId, int run)
        {
            lock (StreamLock)
            {
                if (_flushScheduled) return;
                _flushScheduled = true;
            }
            MainThread.Run(() => FlushStream(assistantId, run));
        }

        static void FlushStream(string assistantId, int run)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            string text, reasoning;
            bool again;
            lock (StreamLock)
            {
                text = PendingText.ToString();
                reasoning = PendingReasoning.ToString();
                PendingText.Length = 0;
                PendingReasoning.Length = 0;
                _flushScheduled = false;
                again = PendingText.Length > 0 || PendingReasoning.Length > 0;
            }
            var message = FindMessage(assistantId);
            if (message != null && (text.Length > 0 || reasoning.Length > 0))
            {
                message.Content += text;
                if (!string.IsNullOrEmpty(reasoning)) message.Reasoning += reasoning;
                Raise();
            }
            if (again) ScheduleFlush(assistantId, run);
        }

        static void FlushPendingNow(string assistantId)
        {
            string text, reasoning;
            lock (StreamLock)
            {
                text = PendingText.ToString();
                reasoning = PendingReasoning.ToString();
                PendingText.Length = 0;
                PendingReasoning.Length = 0;
                _flushScheduled = false;
            }
            var message = FindMessage(assistantId);
            if (message == null) return;
            if (text.Length > 0) message.Content += text;
            if (reasoning.Length > 0) message.Reasoning += reasoning;
        }

        static void HandleEvent(StreamEvent evt, string assistantId)
        {
            var assistant = FindMessage(assistantId);
            switch (evt.Kind)
            {
                case StreamEventKind.Status:
                    StatusMessage = evt.Text;
                    Raise();
                    break;

                case StreamEventKind.ToolCallStarted:
                    {
                        if (evt.ToolCall == null) break;
                        var tool = FindToolMessage(evt.ToolCall.Id);
                        if (tool == null)
                        {
                            tool = new ChatMessage
                            {
                                Id = "tool_" + (string.IsNullOrEmpty(evt.ToolCall.Id) ? Guid.NewGuid().ToString("N").Substring(0, 8) : evt.ToolCall.Id),
                                Role = "tool",
                                ToolName = evt.ToolCall.Name,
                                ToolStatus = "Running",
                                Content = string.Empty,
                                TimeLocal = DateTime.Now.ToString("HH:mm:ss")
                            };
                            CurrentSession.Display.Add(tool);
                        }
                        else
                        {
                            tool.ToolName = evt.ToolCall.Name;
                            tool.ToolStatus = "Running";
                        }
                        StatusMessage = "Running tool: " + evt.ToolCall.Name;
                        Raise();
                        break;
                    }

                case StreamEventKind.ToolCallResult:
                    {
                        if (evt.ToolCall == null) break;
                        var tool = FindToolMessage(evt.ToolCall.Id);
                        if (tool == null)
                        {
                            tool = new ChatMessage
                            {
                                Id = "tool_" + evt.ToolCall.Id,
                                Role = "tool",
                                ToolName = evt.ToolCall.Name,
                                TimeLocal = DateTime.Now.ToString("HH:mm:ss")
                            };
                            CurrentSession.Display.Add(tool);
                        }
                        tool.ToolName = evt.ToolCall.Name;
                        tool.ToolStatus = evt.ToolOk ? "Done" : "Failed";
                        tool.ToolOk = evt.ToolOk;
                        tool.Content = FileService.RedactSecrets(evt.Text);
                        if (evt.ChangeIds != null) foreach (var id in evt.ChangeIds) if (!tool.ChangeIds.Contains(id)) tool.ChangeIds.Add(id);
                        Raise();
                        break;
                    }

                case StreamEventKind.ChangeStaged:
                    {
                        if (evt.ChangeIds == null || evt.ChangeIds.Count == 0) break;
                        var lines = new StringBuilder();
                        lines.Append("Staged ").Append(evt.ChangeIds.Count).Append(" change(s) for review:");
                        foreach (var id in evt.ChangeIds)
                        {
                            var change = ChangeService.Find(id);
                            lines.Append("\n- ").Append(change != null ? change.Title : id);
                            lines.Append(" [").Append(id).Append(']');
                            if (change != null && change.Status == ChangeStatus.Applied) lines.Append(" (applied)");
                        }
                        if (Permissions.Level == BridgeSettings.AccessMode.ReadOnly)
                            lines.Append("\nRead Only mode: nothing can be approved.");
                        else
                            lines.Append("\nOpen the Pending Changes tab to review the diff and approve. Then ask me to apply them.");
                        var message = AddDisplay("system", lines.ToString(), false);
                        message.ChangeIds.AddRange(evt.ChangeIds);
                        Raise();
                        break;
                    }

                case StreamEventKind.Usage:
                    if (assistant != null && evt.Usage != null)
                    {
                        assistant.Usage = evt.Usage.Summary();
                        Raise();
                    }
                    break;

                case StreamEventKind.Error:
                    LastError = evt.Text;
                    StatusMessage = "Provider error.";
                    Raise();
                    break;

                case StreamEventKind.Completed:
                    StatusMessage = "Finalizing...";
                    Raise();
                    break;
            }
        }

        static void Complete(ProviderTurn turn, string assistantId, int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            FlushPendingNow(assistantId);
            var assistant = FindMessage(assistantId);
            if (assistant != null)
            {
                assistant.IsStreaming = false;
                if (string.IsNullOrEmpty(assistant.Content) && string.IsNullOrEmpty(assistant.Reasoning))
                    assistant.Content = "(The assistant returned no text.)";
                if (turn != null && turn.Usage != null)
                {
                    string usage = turn.Usage.Summary();
                    if (!string.IsNullOrEmpty(usage)) assistant.Usage = usage;
                }
            }

            IsBusy = false;
            _cancellation = null;
            StatusMessage = ChangeService.PendingCount > 0
                ? ChangeService.PendingCount + " change(s) awaiting approval"
                : "Ready";
            ConnectionState = ProviderConnectionState.Connected;
            ConnectionMessage = "Connected";
            LastError = null;
            SaveSession(providerId);
            BridgeLog.Info("chat", "Reply completed with " + ProviderFactory.DisplayName(providerId) + " (" + CurrentSession.Model + ").");
            Raise();
        }

        static void Failed(Exception exception, int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            string assistantId = null;
            var session = CurrentSession;
            for (int i = session.Display.Count - 1; i >= 0; i--)
                if (session.Display[i].Role == "assistant" && session.Display[i].IsStreaming) { assistantId = session.Display[i].Id; break; }
            if (assistantId != null) FlushPendingNow(assistantId);

            var assistant = assistantId != null ? FindMessage(assistantId) : null;
            if (assistant != null) assistant.IsStreaming = false;

            string message = exception is BridgeException ? exception.Message : exception.GetType().Name + ": " + exception.Message;
            LastError = message;
            IsBusy = false;
            _cancellation = null;
            StatusMessage = "Error";
            if (!(exception is OperationCanceledException)) ConnectionState = ProviderConnectionState.Error;
            AddDisplay("error", "Provider error: " + message, true);
            SaveSession(providerId);
            BridgeLog.Error("chat", "Provider call failed: " + message);
            Raise();
        }

        static void Cancelled(int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            string assistantId = null;
            var session = CurrentSession;
            for (int i = session.Display.Count - 1; i >= 0; i--)
                if (session.Display[i].Role == "assistant" && session.Display[i].IsStreaming) { assistantId = session.Display[i].Id; break; }
            if (assistantId != null) FlushPendingNow(assistantId);
            var assistant = assistantId != null ? FindMessage(assistantId) : null;
            if (assistant != null) assistant.IsStreaming = false;
            IsBusy = false;
            _cancellation = null;
            StatusMessage = "Stopped";
            AddDisplay("system", "Generation stopped by user.", false);
            SaveSession(providerId);
            Raise();
        }

        public static void Stop()
        {
            if (!IsBusy) return;
            StatusMessage = "Stopping...";
            try { if (_cancellation != null) _cancellation.Cancel(); } catch { }
            Raise();
        }

        public static void Clear()
        {
            if (IsBusy) Stop();
            Interlocked.Increment(ref _runSerial);
            var session = CurrentSession;
            session.Display.Clear();
            session.Transcript.Clear();
            string path = BridgePaths.ChatSessionFile(CurrentProviderId);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            StatusMessage = "Ready";
            Raise();
        }

        public static void ContinueAfterApproval()
        {
            Send("I approved and applied the pending changes. Check the Unity console for compilation errors and continue with the task.");
        }

        // ------------------------------------------------------------------ attachments

        public static void AttachFile(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return;
            if (_attached.Contains(projectRelative)) return;
            if (_attached.Count >= 8) { StatusMessage = "At most 8 files can be attached."; Raise(); return; }
            _attached.Add(projectRelative);
            Raise();
        }

        public static void RemoveAttachment(string projectRelative)
        {
            _attached.Remove(projectRelative);
            Raise();
        }

        public static void ClearAttachments()
        {
            _attached.Clear();
            Raise();
        }

        // ------------------------------------------------------------------ helpers

        public static ChatMessage AddDisplay(string role, string content, bool error)
        {
            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Role = role,
                Content = content ?? string.Empty,
                TimeLocal = DateTime.Now.ToString("HH:mm:ss"),
                IsError = error
            };
            CurrentSession.Display.Add(message);
            return message;
        }

        static ChatMessage FindMessage(string id)
        {
            foreach (var message in CurrentSession.Display)
                if (message.Id == id) return message;
            return null;
        }

        static ChatMessage FindToolMessage(string callId)
        {
            if (string.IsNullOrEmpty(callId)) return null;
            foreach (var message in CurrentSession.Display)
                if (message.Role == "tool" && message.Id == "tool_" + callId) return message;
            return null;
        }

        static void ResetStreamBuffers()
        {
            lock (StreamLock)
            {
                PendingText.Length = 0;
                PendingReasoning.Length = 0;
                _flushScheduled = false;
            }
        }

        // ------------------------------------------------------------------ model cache

        static void CacheModels(string provider, List<ModelInfo> models)
        {
            var array = JsonValue.NewArray();
            foreach (var model in models)
            {
                var efforts = JsonValue.NewArray();
                foreach (var effort in model.EffortLevels) efforts.Add(effort);
                array.Add(JsonValue.Obj(
                    "id", model.Id,
                    "name", model.DisplayName,
                    "context", model.ContextWindow,
                    "max_output", model.MaxOutputTokens,
                    "efforts", efforts));
            }
            string json = array.ToJson(false);
            var settings = BridgeSettings.Current;
            switch (provider)
            {
                case BridgeSettings.ProviderDeepSeek: settings.DeepSeekModelsJson = json; break;
                case BridgeSettings.ProviderChatGpt: settings.ChatGptModelsJson = json; break;
                case BridgeSettings.ProviderOpenAi: settings.OpenAiModelsJson = json; break;
                case BridgeSettings.ProviderOllama: settings.OllamaModelsJson = json; break;
            }
            BridgeSettings.Save();
        }

        static void LoadCachedModels(string provider)
        {
            var settings = BridgeSettings.Current;
            string json = null;
            switch (provider)
            {
                case BridgeSettings.ProviderDeepSeek: json = settings.DeepSeekModelsJson; break;
                case BridgeSettings.ProviderChatGpt: json = settings.ChatGptModelsJson; break;
                case BridgeSettings.ProviderOpenAi: json = settings.OpenAiModelsJson; break;
                case BridgeSettings.ProviderOllama: json = settings.OllamaModelsJson; break;
            }
            if (string.IsNullOrEmpty(json)) return;
            JsonValue array;
            string error;
            if (!JsonValue.TryParse(json, out array, out error) || !array.IsArray) return;
            var result = new List<ModelInfo>();
            foreach (var item in array.ArrayValue)
            {
                var info = new ModelInfo
                {
                    Id = item["id"].AsString(),
                    DisplayName = item["name"].AsString(item["id"].AsString()),
                    ContextWindow = item["context"].AsInt(),
                    MaxOutputTokens = item["max_output"].AsInt()
                };
                if (item["efforts"].IsArray)
                    foreach (var effort in item["efforts"].ArrayValue) info.EffortLevels.Add(effort.AsString());
                if (!string.IsNullOrEmpty(info.Id)) result.Add(info);
            }
            if (result.Count > 0) _models = result;
        }

        public static string ProviderStatusText()
        {
            var session = CurrentSession;
            string text = ProviderFactory.DisplayName(CurrentProviderId);
            if (!string.IsNullOrEmpty(session.Model)) text += " · " + session.Model;
            text += " · " + ConnectionState;
            return text;
        }
    }
}
