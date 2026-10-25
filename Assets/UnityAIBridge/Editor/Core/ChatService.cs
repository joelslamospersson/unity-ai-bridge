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
        public string Role; // user, assistant, system, error, activity
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
        public List<ChatFileRef> Files = new List<ChatFileRef>();
        public List<ChatToolActivity> Activities = new List<ChatToolActivity>();
        public bool IsCompactionNotice;
    }

    [Serializable]
    public class ChatSessionData
    {
        public string Provider;
        public string SessionId;
        public string Model;
        public List<ChatMessage> Display = new List<ChatMessage>();
        public List<LlmMessage> Transcript = new List<LlmMessage>();
    }

    /// <summary>
    /// Chat orchestration. Display history is paged on disk and loaded lazily;
    /// model context is persisted separately so long chats never send the full transcript.
    /// </summary>
    public static class ChatService
    {
        static readonly List<ChatMessage> Display = new List<ChatMessage>();
        static readonly List<string> _attached = new List<string>();
        static readonly object StreamLock = new object();
        static readonly StringBuilder PendingText = new StringBuilder();
        static readonly StringBuilder PendingReasoning = new StringBuilder();

        static ConversationMeta _meta;
        static List<LlmMessage> _transcript = new List<LlmMessage>();
        static int _oldestPage = -1;
        static bool _hasOlder;
        static bool _compactionNoticed;
        static string _activityGroupId;
        static string _activeAssistantId;
        static List<LlmMessage> _activeModelMessages;
        static int _activeInitialCount;
        static DateTime _lastCheckpointUtc;
        static DateTime _lastFlushUtc;

        static string _activeProvider;
        static bool _flushScheduled;
        static int _runSerial;
        static CancellationTokenSource _cancellation;
        static CancellationTokenSource _modelCancellation;
        static bool _chatGptSubscribed;
        static List<ModelInfo> _models = new List<ModelInfo>();
        static bool _loadingModels;
        static string _testResult;
        static bool _testing;

        public static bool IsBusy { get; private set; }
        public static bool Suspended { get; private set; }
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

        public static ConversationMeta CurrentConversation { get { return _meta; } }
        public static string CurrentModel { get { return _meta != null ? _meta.Model : DefaultModel(CurrentProviderId); } }
        public static List<ChatMessage> Messages { get { return Display; } }
        public static List<ConversationMeta> Conversations { get { return ChatHistory.List(CurrentProviderId, null); } }
        public static bool HasOlderMessages { get { return _hasOlder; } }
        public static string ConversationTitle { get { return _meta != null ? _meta.Title : "New chat"; } }

        public static void SuspendBackgroundWork()
        {
            Suspended = true;
            try { if (_modelCancellation != null) _modelCancellation.Cancel(); } catch { }
            _loadingModels = false;
        }

        public static void ResumeBackgroundWork()
        {
            Suspended = false;
            _loadingModels = false;
            RefreshModels(true);
        }

        public static void Raise()
        {
            var handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        public static void EnsureLoaded()
        {
            BridgeSettings.Load();
            string provider = BridgeSettings.Current.Provider;
            if (_activeProvider != provider)
            {
                _activeProvider = provider;
                _meta = null;
            }
            if (_meta == null || _meta.Provider != provider) LoadActiveConversation(provider);
            if (!_chatGptSubscribed)
            {
                _chatGptSubscribed = true;
                ChatGptAuth.Changed += OnChatGptChanged;
            }
            LoadCachedModels(_activeProvider);
            if (_activeProvider == BridgeSettings.ProviderDeepSeek && _models.Count == 0 && string.IsNullOrEmpty(BridgeSettings.Secrets.DeepSeekApiKey))
                _models = DeepSeekProvider.VerifiedModels();
            if (_activeProvider == BridgeSettings.ProviderOpenCode && _models.Count == 0 && string.IsNullOrEmpty(BridgeSettings.Secrets.OpenCodeApiKey))
                _models = OpenCodeGoProvider.VerifiedModels();
            if (_meta != null && string.IsNullOrEmpty(_meta.Model))
            {
                _meta.Model = DefaultModel(_activeProvider);
                ChatHistory.SaveMeta(_meta);
            }
            UpdateConnectionFromProvider();
            if (_models.Count == 0 && (ProviderFactory.IsConfigured(_activeProvider, out _) || _activeProvider == BridgeSettings.ProviderChatGpt))
                RefreshModels(false);
            Raise();
        }

        static void LoadActiveConversation(string provider)
        {
            ChatHistory.EnsureMigrated(provider);
            var meta = ChatHistory.LoadOrCreateActive(provider, DefaultModel(provider));
            OpenMeta(meta);
        }

        static void OpenMeta(ConversationMeta meta)
        {
            _meta = meta;
            Display.Clear();
            _oldestPage = -1;
            _hasOlder = false;
            _activityGroupId = null;
            _activeAssistantId = null;
            _activeModelMessages = null;
            _compactionNoticed = false;
            _transcript = ChatHistory.LoadContext(meta);
            var page = ChatHistory.LoadLatestPage(meta, out _oldestPage);
            Display.AddRange(page);
            _hasOlder = _oldestPage > 0;

            var draft = ChatHistory.LoadStreaming(meta);
            if (draft != null)
            {
                if (!string.IsNullOrEmpty(draft.Content))
                {
                    draft.IsStreaming = false;
                    draft.Content += "\n\n*Recovered incomplete response.*";
                    Display.Add(draft);
                    ChatHistory.Append(meta, draft);
                    _transcript.Add(new LlmMessage { Role = "assistant", Content = draft.Content });
                }
                ChatHistory.DeleteStreaming(meta);
            }
            if (string.IsNullOrEmpty(meta.Model)) meta.Model = DefaultModel(meta.Provider);
        }

        static void PersistConversation()
        {
            if (_meta == null) return;
            ChatHistory.SaveContext(_meta, _transcript);
            ChatHistory.DeleteStreaming(_meta);
        }

        static void CancelActive()
        {
            try { if (_cancellation != null) _cancellation.Cancel(); } catch { }
            Interlocked.Increment(ref _runSerial);
            IsBusy = false;
            _cancellation = null;
            _activeModelMessages = null;
            ResetStreamBuffers();
        }

        public static int LoadOlderMessages()
        {
            if (_meta == null || !_hasOlder) return 0;
            int newOldest;
            var page = ChatHistory.LoadOlderPage(_meta, _oldestPage, out newOldest);
            if (page == null) { _hasOlder = false; return 0; }
            Display.InsertRange(0, page);
            _oldestPage = newOldest;
            _hasOlder = newOldest > 0;
            Raise();
            return page.Count;
        }

        public static void NewChat()
        {
            EnsureLoaded();
            PersistConversation();
            CancelActive();
            var meta = ChatHistory.NewConversation(CurrentProviderId, DefaultModel(CurrentProviderId));
            OpenMeta(meta);
            Raise();
        }

        public static void OpenConversation(string id)
        {
            if (string.IsNullOrEmpty(id) || (_meta != null && _meta.Id == id)) return;
            EnsureLoaded();
            PersistConversation();
            CancelActive();
            var meta = ChatHistory.LoadMeta(CurrentProviderId, id);
            if (meta == null) return;
            ChatHistory.SetActive(CurrentProviderId, id);
            OpenMeta(meta);
            Raise();
        }

        public static void RenameConversation(string id, string title)
        {
            var meta = _meta != null && _meta.Id == id ? _meta : ChatHistory.LoadMeta(CurrentProviderId, id);
            if (meta == null) return;
            ChatHistory.Rename(meta, title);
            Raise();
        }

        public static void DeleteConversation(string id)
        {
            var meta = _meta != null && _meta.Id == id ? _meta : ChatHistory.LoadMeta(CurrentProviderId, id);
            if (meta == null) return;
            bool wasCurrent = _meta != null && _meta.Id == id;
            if (wasCurrent) CancelActive();
            ChatHistory.Delete(meta);
            if (wasCurrent)
            {
                _meta = null;
                LoadActiveConversation(CurrentProviderId);
            }
            Raise();
        }

        public static void ClearHistory()
        {
            CancelActive();
            ChatHistory.ClearProvider(CurrentProviderId);
            _meta = null;
            LoadActiveConversation(CurrentProviderId);
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
                    if (_meta != null) ChatHistory.SaveMeta(_meta);
                }
                Raise();
            });
        }

        // ------------------------------------------------------------------ models / connection

        static string DefaultModel(string provider)
        {
            var settings = BridgeSettings.Current;
            switch (provider)
            {
                case BridgeSettings.ProviderDeepSeek: return string.IsNullOrEmpty(settings.DeepSeekModel) ? "deepseek-flash" : settings.DeepSeekModel;
                case BridgeSettings.ProviderOpenCode: return string.IsNullOrEmpty(settings.OpenCodeModel) ? "deepseek-v4.1-flash" : settings.OpenCodeModel;
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
                case BridgeSettings.ProviderOpenCode: settings.OpenCodeModel = model; break;
                case BridgeSettings.ProviderChatGpt: settings.ChatGptModel = model; break;
                case BridgeSettings.ProviderOpenAi: settings.OpenAiModel = model; break;
                case BridgeSettings.ProviderOllama: settings.OllamaModel = model; break;
            }
            BridgeSettings.Save();
        }

        public static void SelectModel(string model)
        {
            if (string.IsNullOrEmpty(model) || _meta == null) return;
            _meta.Model = model;
            PersistModel(CurrentProviderId, model);
            ChatHistory.SaveMeta(_meta);
            Raise();
        }

        public static void SwitchProvider(string provider)
        {
            if (string.IsNullOrEmpty(provider) || provider == CurrentProviderId) return;
            PersistConversation();
            CancelActive();

            BridgeSettings.Current.Provider = provider;
            BridgeSettings.Save();
            _activeProvider = provider;
            _meta = null;
            LoadActiveConversation(provider);
            if (_meta != null && string.IsNullOrEmpty(_meta.Model)) _meta.Model = DefaultModel(provider);
            _models = new List<ModelInfo>();
            _loadingModels = false;
            ConnectionState = ProviderConnectionState.NotConfigured;
            ConnectionMessage = null;
            LoadCachedModels(provider);
            UpdateConnectionFromProvider();
            RefreshModels(false);
            Raise();
        }

        public static void RefreshModels(bool force)
        {
            if (Suspended || _loadingModels) return;
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
                if (providerId == BridgeSettings.ProviderOpenCode) _models = OpenCodeGoProvider.VerifiedModels();
                if (providerId == BridgeSettings.ProviderChatGpt) _models = new List<ModelInfo>();
                Raise();
                return;
            }

            _loadingModels = true;
            ConnectionMessage = "Loading models...";
            string fingerprint = ProviderFingerprint(providerId);
            _modelCancellation = new CancellationTokenSource();
            var modelToken = _modelCancellation.Token;
            Raise();
            Task.Run(async () =>
            {
                try
                {
                    var list = await provider.GetModelsAsync(modelToken).ConfigureAwait(false);
                    MainThread.Run(() =>
                    {
                        _loadingModels = false;
                        if (CurrentProviderId != providerId || fingerprint != ProviderFingerprint(providerId)) return;
                        _models = list ?? new List<ModelInfo>();
                        ConnectionState = ProviderConnectionState.Connected;
                        ConnectionMessage = "Connected · " + _models.Count + " model(s)";
                        CacheModels(providerId, _models);
                        if (_models.Count > 0 && _meta != null && !_models.Exists(m => m.Id == _meta.Model))
                        {
                            _meta.Model = _models[0].Id;
                            PersistModel(providerId, _meta.Model);
                            ChatHistory.SaveMeta(_meta);
                        }
                        else if (_meta != null) ChatHistory.SaveMeta(_meta);
                        BridgeLog.Info("chat", "Loaded " + _models.Count + " model(s) for " + providerId + ".");
                        Raise();
                    });
                }
                catch (OperationCanceledException)
                {
                    MainThread.Run(() => { _loadingModels = false; Raise(); });
                }
                catch (Exception e)
                {
                    MainThread.Run(() =>
                    {
                        _loadingModels = false;
                        if (CurrentProviderId != providerId || fingerprint != ProviderFingerprint(providerId)) return;
                        ConnectionState = ProviderConnectionState.Error;
                        ConnectionMessage = e.Message;
                        if (providerId == BridgeSettings.ProviderDeepSeek && _models.Count == 0)
                        {
                            _models = DeepSeekProvider.VerifiedModels();
                            if (_meta != null && !_models.Exists(m => m.Id == _meta.Model))
                            {
                                _meta.Model = "deepseek-flash";
                                PersistModel(providerId, _meta.Model);
                                ChatHistory.SaveMeta(_meta);
                            }
                        }
                        if (providerId == BridgeSettings.ProviderOpenCode && _models.Count == 0)
                        {
                            _models = OpenCodeGoProvider.VerifiedModels();
                            if (_meta != null && !_models.Exists(m => m.Id == _meta.Model))
                            {
                                _meta.Model = "deepseek-v4.1-flash";
                                PersistModel(providerId, _meta.Model);
                                ChatHistory.SaveMeta(_meta);
                            }
                        }
                        BridgeLog.Warn("chat", "Could not load models for " + providerId + ": " + e.Message);
                        Raise();
                    });
                }
            });
        }

        static string ProviderFingerprint(string providerId)
        {
            var settings = BridgeSettings.Current;
            var secrets = BridgeSettings.Secrets;
            switch (providerId)
            {
                case BridgeSettings.ProviderDeepSeek: return settings.DeepSeekBaseUrl + "|" + secrets.DeepSeekApiKey + "|" + settings.DeepSeekModel;
                case BridgeSettings.ProviderOpenCode: return settings.OpenCodeBaseUrl + "|" + secrets.OpenCodeApiKey + "|" + settings.OpenCodeModel;
                case BridgeSettings.ProviderOpenAi: return settings.OpenAiBaseUrl + "|" + secrets.OpenAiApiKey;
                case BridgeSettings.ProviderOllama: return settings.OllamaUrl + "|" + secrets.OllamaApiKey;
                case BridgeSettings.ProviderChatGpt: return secrets.ChatGptClientId + "|" + secrets.ChatGptEmail + "|" + secrets.ChatGptScopes;
                default: return providerId;
            }
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

            string model = _meta != null ? _meta.Model : null;
            if (string.IsNullOrEmpty(model) && _models.Count > 0)
            {
                model = _models[0].Id;
                _meta.Model = model;
                PersistModel(providerId, model);
            }
            if (string.IsNullOrEmpty(model))
            {
                AddDisplay("error", "No model selected. Press the refresh button next to the model dropdown or sign in again.", true);
                Raise();
                return;
            }

            var user = new LlmMessage { Role = "user", Content = text };
            AddDisplay("user", text, false);
            _transcript.Add(user);
            if (_meta != null) ChatHistory.SaveContext(_meta, _transcript);

            var assistant = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Role = "assistant",
                Content = string.Empty,
                TimeLocal = DateTime.Now.ToString("HH:mm:ss"),
                Model = model,
                IsStreaming = true
            };
            Display.Add(assistant);
            _activeAssistantId = assistant.Id;
            _activityGroupId = null;
            _lastCheckpointUtc = DateTime.UtcNow;

            var settings = BridgeSettings.Current;
            bool compacted;
            string olderSummary;
            var modelMessages = BuildModelMessages(settings, out compacted, out olderSummary);
            _activeModelMessages = modelMessages;
            _activeInitialCount = modelMessages.Count;
            if (compacted) AddCompactionNotice();

            string instructions = BuildInstructions(settings, olderSummary);
            var request = new LlmRequest
            {
                ProviderId = providerId,
                SessionId = _meta.Id,
                Model = model,
                Instructions = instructions,
                Messages = modelMessages,
                Tools = McpTools.GetToolSpecs(Permissions.CanPropose),
                Temperature = settings.Temperature,
                MaxOutputTokens = providerId == BridgeSettings.ProviderDeepSeek ? settings.DeepSeekMaxTokens : settings.MaxOutputTokens,
                Thinking = settings.DeepSeekThinking,
                ReasoningEffort = settings.DeepSeekReasoningEffort,
                MaxSteps = settings.MaxAgentSteps
            };

            int run = Interlocked.Increment(ref _runSerial);
            ResetStreamBuffers();
            _lastFlushUtc = DateTime.UtcNow.AddMilliseconds(-100);
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
                    var turn = await AgentRunner.RunAsync(provider, request, modelMessages, evt => OnProviderEvent(evt, assistant.Id, run), token).ConfigureAwait(false);
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

        static List<LlmMessage> BuildModelMessages(BridgeSettings settings, out bool compacted, out string olderSummary)
        {
            compacted = false;
            olderSummary = null;
            var result = new List<LlmMessage>();
            if (_transcript.Count == 0) return result;

            int budget = Math.Max(4000, settings.MaxContextCharacters * 2 / 3);
            var userIndices = new List<int>();
            for (int i = 0; i < _transcript.Count; i++)
                if (_transcript[i] != null && _transcript[i].Role == "user") userIndices.Add(i);

            int start;
            if (userIndices.Count > 0)
            {
                start = userIndices[userIndices.Count - 1];
                for (int u = userIndices.Count - 2; u >= 0; u--)
                {
                    int candidate = userIndices[u];
                    int cost = 0;
                    for (int i = candidate; i < _transcript.Count; i++) cost += MessageCost(_transcript[i]);
                    if (cost > budget) break;
                    start = candidate;
                }
            }
            else
            {
                start = _transcript.Count;
                int cost = 0;
                for (int i = _transcript.Count - 1; i >= 0; i--)
                {
                    cost += MessageCost(_transcript[i]);
                    if (cost > budget) break;
                    start = i;
                }
            }

            compacted = start > 0;
            if (compacted) olderSummary = SummarizeOlder(start);
            for (int i = start; i < _transcript.Count; i++)
                if (_transcript[i] != null) result.Add(_transcript[i]);
            return result;
        }

        static int MessageCost(LlmMessage message)
        {
            if (message == null) return 0;
            int cost = 24;
            if (!string.IsNullOrEmpty(message.Content)) cost += message.Content.Length;
            if (message.ToolCalls != null) cost += message.ToolCalls.Count * 80;
            return cost;
        }

        static string SummarizeOlder(int start)
        {
            var lines = new List<string>();
            for (int i = 0; i < start; i++)
            {
                var message = _transcript[i];
                if (message == null || message.Role != "user" || string.IsNullOrEmpty(message.Content)) continue;
                string text = message.Content.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (text.Length > 180) text = text.Substring(0, 180) + "…";
                lines.Add("- " + text);
            }
            if (lines.Count > 8) lines.RemoveRange(0, lines.Count - 8);
            return string.Join("\n", lines.ToArray());
        }

        static string BuildInstructions(BridgeSettings settings, string olderSummary)
        {
            string context;
            try { context = ProjectService.BuildAgentContext(settings, new List<string>(_attached)); }
            catch (Exception e) { context = "(project context unavailable: " + e.Message + ")"; }

            var builder = new StringBuilder();
            builder.Append(PromptBuilder.AgentSystemPrompt(settings));
            builder.Append("\n\n").Append(PromptBuilder.ResponseModeInstruction(settings.ResponseMode));
            builder.Append("\n\nPROJECT CONTEXT\n").Append(context);
            if (!string.IsNullOrEmpty(olderSummary))
                builder.Append("\n\nOLDER CONVERSATION SUMMARY (the full messages remain in transcript for reference; use this summary to stay oriented)\n").Append(olderSummary);

            settings.MaxContextCharacters = Mathf.Max(8000, settings.MaxContextCharacters);
            string instructions = builder.ToString();
            if (instructions.Length > settings.MaxContextCharacters + 12000)
                instructions = instructions.Substring(0, settings.MaxContextCharacters + 12000) + "\n... context truncated ...";
            return instructions;
        }

        static void AddCompactionNotice()
        {
            if (_compactionNoticed) return;
            _compactionNoticed = true;
            var message = AddDisplay("system", "Older conversation turns were summarized for this request to keep context small. Earlier messages remain in the transcript above.", false);
            message.IsCompactionNotice = true;
            var assistant = _activeAssistantId != null ? FindMessage(_activeAssistantId) : null;
            if (assistant != null && Display.Contains(message))
            {
                Display.Remove(message);
                int index = Display.IndexOf(assistant);
                if (index >= 0) Display.Insert(index, message); else Display.Add(message);
            }
            ChatHistory.UpdateMessage(_meta, message);
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
            bool hasPending;
            lock (StreamLock) hasPending = PendingText.Length > 0 || PendingReasoning.Length > 0;
            if (hasPending && (DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds < 65)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(65).ConfigureAwait(false);
                    MainThread.Run(() => FlushStream(assistantId, run));
                });
                return;
            }
            _lastFlushUtc = DateTime.UtcNow;
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
                Checkpoint(message);
                Raise();
            }
            if (again) ScheduleFlush(assistantId, run);
        }

        static void Checkpoint(ChatMessage message)
        {
            if (message == null || _meta == null) return;
            if ((DateTime.UtcNow - _lastCheckpointUtc).TotalMilliseconds < 750) return;
            _lastCheckpointUtc = DateTime.UtcNow;
            ChatHistory.SaveStreaming(_meta, message);
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
                    if (evt.ToolCall != null) OnToolStarted(evt.ToolCall);
                    break;

                case StreamEventKind.ToolCallResult:
                    if (evt.ToolCall != null) OnToolResult(evt);
                    break;

                case StreamEventKind.ChangeStaged:
                    if (evt.ChangeIds != null && evt.ChangeIds.Count > 0) OnChangesStaged(evt);
                    break;

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

        static ChatMessage EnsureActivityGroup()
        {
            ChatMessage group = _activityGroupId != null ? FindMessage(_activityGroupId) : null;
            if (group != null && group.Role == "activity") return group;

            group = new ChatMessage
            {
                Id = "act_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Role = "activity",
                TimeLocal = DateTime.Now.ToString("HH:mm:ss"),
                Model = CurrentModel
            };
            var assistant = _activeAssistantId != null ? FindMessage(_activeAssistantId) : null;
            int index = assistant != null ? Display.IndexOf(assistant) : -1;
            if (index >= 0) Display.Insert(index, group); else Display.Add(group);
            _activityGroupId = group.Id;
            ChatHistory.Append(_meta, group);
            return group;
        }

        static void OnToolStarted(ToolCall call)
        {
            var group = EnsureActivityGroup();
            if (group.Activities == null) group.Activities = new List<ChatToolActivity>();
            var activity = group.Activities.Find(a => a != null && a.CallId == call.Id);
            if (activity == null)
            {
                activity = new ChatToolActivity { CallId = call.Id, Tool = call.Name, Status = "Running", Ok = true };
                group.Activities.Add(activity);
            }
            else
            {
                activity.Tool = call.Name;
                activity.Status = "Running";
                activity.Ok = true;
            }

            string path = ExtractPath(call.Arguments);
            if (!string.IsNullOrEmpty(path) && IsReadTool(call.Name))
            {
                AddFileRef(group, path, null, call.Id, "Reading", true, null);
            }
            StatusMessage = "Running tool: " + call.Name;
            ChatHistory.UpdateMessage(_meta, group);
            Raise();
        }

        static void OnToolResult(StreamEvent evt)
        {
            var group = EnsureActivityGroup();
            if (group.Activities == null) group.Activities = new List<ChatToolActivity>();
            string callId = evt.ToolCall.Id;
            var activity = group.Activities.Find(a => a != null && a.CallId == callId);
            if (activity == null)
            {
                activity = new ChatToolActivity { CallId = callId, Tool = evt.ToolCall.Name };
                group.Activities.Add(activity);
            }
            activity.Tool = evt.ToolCall.Name;
            activity.Status = evt.ToolOk ? "Done" : "Failed";
            activity.Ok = evt.ToolOk;
            activity.Detail = FileService.RedactSecrets(evt.Text);
            if (evt.ChangeIds != null)
                foreach (var id in evt.ChangeIds)
                    if (!activity.ChangeIds.Contains(id)) activity.ChangeIds.Add(id);

            if (group.Files != null)
                foreach (var file in group.Files)
                    if (file != null && file.CallId == callId && file.IsRead)
                        file.Status = evt.ToolOk ? "Read" : "Read failed";

            if (evt.ChangeIds != null)
                foreach (var id in evt.ChangeIds) AddChangeFile(group, id);

            ChatHistory.UpdateMessage(_meta, group);
            StatusMessage = evt.ToolOk ? "Tool finished: " + evt.ToolCall.Name : "Tool failed: " + evt.ToolCall.Name;
            Raise();
        }

        static void OnChangesStaged(StreamEvent evt)
        {
            var group = EnsureActivityGroup();
            foreach (var id in evt.ChangeIds) AddChangeFile(group, id);
            ChatHistory.UpdateMessage(_meta, group);
            if (Permissions.Level == BridgeSettings.AccessMode.ReadOnly)
            {
                var message = AddDisplay("system", "Read Only mode is active. Staged changes cannot be approved until you switch access mode in Settings.", false);
                message.ChangeIds.AddRange(evt.ChangeIds);
                ChatHistory.UpdateMessage(_meta, message);
            }
            Raise();
        }

        static void AddChangeFile(ChatMessage group, string changeId)
        {
            if (group == null || string.IsNullOrEmpty(changeId)) return;
            if (group.Files == null) group.Files = new List<ChatFileRef>();
            if (group.Files.Exists(f => f != null && f.ChangeId == changeId)) return;
            var change = ChangeService.Find(changeId);
            if (change == null)
            {
                group.Files.Add(new ChatFileRef { ChangeId = changeId, Status = "Staged", Title = changeId });
                return;
            }
            group.Files.Add(new ChatFileRef
            {
                Path = change.TargetPath,
                ChangeId = changeId,
                Title = change.Title,
                Status = change.Status.ToString(),
                IsRead = false
            });
        }

        static void AddFileRef(ChatMessage group, string path, string changeId, string callId, string status, bool isRead, string title)
        {
            if (group == null || string.IsNullOrEmpty(path)) return;
            if (group.Files == null) group.Files = new List<ChatFileRef>();
            string full, error;
            if (!BridgePaths.TryResolve(path, out full, out error)) return;
            string relative = BridgePaths.ToProjectRelative(full);
            var existing = group.Files.Find(f => f != null && f.Path == relative && f.ChangeId == changeId);
            if (existing != null)
            {
                existing.Status = status;
                existing.CallId = callId;
                existing.IsRead = isRead;
                return;
            }
            group.Files.Add(new ChatFileRef
            {
                Path = relative,
                ChangeId = changeId,
                CallId = callId,
                Status = status,
                IsRead = isRead,
                Title = string.IsNullOrEmpty(title) ? Path.GetFileName(relative) : title
            });
        }

        static string ExtractPath(string arguments)
        {
            if (string.IsNullOrEmpty(arguments)) return null;
            JsonValue value;
            string error;
            if (!JsonValue.TryParse(arguments, out value, out error) || value == null || !value.IsObject) return null;
            string[] keys = { "path", "asset_path", "file", "assetPath", "project_path" };
            foreach (var key in keys)
            {
                var item = value[key];
                if (item != null && !item.IsNull)
                {
                    string text = item.AsString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            return null;
        }

        static bool IsReadTool(string name)
        {
            return name == "read_project_file" || name == "inspect_asset" || name == "read_asset" || name == "get_file_info";
        }

        static void SyncTranscript()
        {
            if (_activeModelMessages == null) return;
            for (int i = _activeInitialCount; i < _activeModelMessages.Count; i++)
            {
                var message = _activeModelMessages[i];
                if (message == null) continue;
                _transcript.Add(message);
            }
            _activeModelMessages = null;
            _activeInitialCount = 0;
            if (_transcript.Count > 300) _transcript.RemoveRange(0, _transcript.Count - 300);
        }

        static void Complete(ProviderTurn turn, string assistantId, int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            FlushPendingNow(assistantId);
            SyncTranscript();
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
                ChatHistory.Append(_meta, assistant);
            }

            IsBusy = false;
            _cancellation = null;
            _activeAssistantId = null;
            StatusMessage = ChangeService.PendingCount > 0
                ? ChangeService.PendingCount + " change(s) awaiting approval"
                : "Ready";
            ConnectionState = ProviderConnectionState.Connected;
            ConnectionMessage = "Connected";
            LastError = null;
            if (_meta != null)
            {
                ChatHistory.SaveContext(_meta, _transcript);
                ChatHistory.DeleteStreaming(_meta);
                ChatHistory.SaveMeta(_meta);
            }
            BridgeLog.Info("chat", "Reply completed with " + ProviderFactory.DisplayName(providerId) + " (" + CurrentModel + ").");
            Raise();
        }

        static void Failed(Exception exception, int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            string assistantId = _activeAssistantId;
            if (assistantId != null) FlushPendingNow(assistantId);
            SyncTranscript();

            var assistant = assistantId != null ? FindMessage(assistantId) : null;
            if (assistant != null)
            {
                assistant.IsStreaming = false;
                if (string.IsNullOrEmpty(assistant.Content) && string.IsNullOrEmpty(assistant.Reasoning)) Display.Remove(assistant);
                else ChatHistory.Append(_meta, assistant);
            }

            string message = exception is BridgeException ? exception.Message : exception.GetType().Name + ": " + exception.Message;
            LastError = message;
            IsBusy = false;
            _cancellation = null;
            _activeAssistantId = null;
            StatusMessage = "Error";
            if (!(exception is OperationCanceledException)) ConnectionState = ProviderConnectionState.Error;
            AddDisplay("error", "Provider error: " + message, true);
            if (_meta != null)
            {
                ChatHistory.SaveContext(_meta, _transcript);
                ChatHistory.DeleteStreaming(_meta);
                ChatHistory.SaveMeta(_meta);
            }
            BridgeLog.Error("chat", "Provider call failed: " + message);
            Raise();
        }

        static void Cancelled(int run, string providerId)
        {
            if (run != Volatile.Read(ref _runSerial)) return;
            string assistantId = _activeAssistantId;
            if (assistantId != null) FlushPendingNow(assistantId);
            SyncTranscript();

            var assistant = assistantId != null ? FindMessage(assistantId) : null;
            if (assistant != null)
            {
                assistant.IsStreaming = false;
                if (string.IsNullOrEmpty(assistant.Content) && string.IsNullOrEmpty(assistant.Reasoning)) Display.Remove(assistant);
                else ChatHistory.Append(_meta, assistant);
            }
            IsBusy = false;
            _cancellation = null;
            _activeAssistantId = null;
            StatusMessage = "Stopped";
            AddDisplay("system", "Generation stopped by user.", false);
            if (_meta != null)
            {
                ChatHistory.SaveContext(_meta, _transcript);
                ChatHistory.DeleteStreaming(_meta);
                ChatHistory.SaveMeta(_meta);
            }
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
            CancelActive();
            if (_meta == null) return;
            _transcript.Clear();
            ChatHistory.ClearConversation(_meta);
            OpenMeta(_meta);
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
            if (_meta == null) EnsureLoaded();
            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Role = role,
                Content = content ?? string.Empty,
                TimeLocal = DateTime.Now.ToString("HH:mm:ss"),
                IsError = error
            };
            Display.Add(message);
            if (_meta != null) ChatHistory.Append(_meta, message);
            return message;
        }

        static ChatMessage FindMessage(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = Display.Count - 1; i >= 0; i--)
                if (Display[i] != null && Display[i].Id == id) return Display[i];
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
                case BridgeSettings.ProviderOpenCode: settings.OpenCodeModelsJson = json; break;
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
                case BridgeSettings.ProviderOpenCode: json = settings.OpenCodeModelsJson; break;
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
            string text = ProviderFactory.DisplayName(CurrentProviderId);
            if (_meta != null && !string.IsNullOrEmpty(_meta.Model)) text += " · " + _meta.Model;
            text += " · " + ConnectionState;
            return text;
        }
    }
}
