using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    [Serializable]
    public class ToolCall
    {
        public string Id;
        public string Name;
        public string Arguments;

        public ToolCall Clone()
        {
            return new ToolCall { Id = Id, Name = Name, Arguments = Arguments };
        }
    }

    [Serializable]
    public class LlmMessage
    {
        public string Role;
        public string Content;
        public string Reasoning;
        public string ToolCallId;
        public string Name;
        public List<ToolCall> ToolCalls = new List<ToolCall>();
        public string RawItemsJson;
    }

    public sealed class LlmRequest
    {
        public string ProviderId;
        public string SessionId;
        public string Model;
        public string Instructions;
        public List<LlmMessage> Messages = new List<LlmMessage>();
        public List<ToolSpec> Tools = new List<ToolSpec>();
        public double Temperature = 0.2;
        public int MaxOutputTokens = 8192;
        public bool Thinking = true;
        public string ReasoningEffort = "high";
        public int MaxSteps = 8;
        public Func<ToolCall, CancellationToken, Task<JsonValue>> ToolExecutor;
        public Action ToolResultCommitted;
    }

    public sealed class TokenUsage
    {
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens;
        public int CachedTokens;
        public int ReasoningTokens;
        public string Model;
        public string CostText;

        public void Add(TokenUsage other)
        {
            if (other == null) return;
            PromptTokens += other.PromptTokens;
            CompletionTokens += other.CompletionTokens;
            TotalTokens += other.TotalTokens;
            CachedTokens += other.CachedTokens;
            ReasoningTokens += other.ReasoningTokens;
            if (!string.IsNullOrEmpty(other.Model)) Model = other.Model;
            if (!string.IsNullOrEmpty(other.CostText)) CostText = other.CostText;
        }

        public string Summary()
        {
            string text = (PromptTokens + CompletionTokens) > 0
                ? PromptTokens.ToString("N0") + " in / " + CompletionTokens.ToString("N0") + " out"
                : string.Empty;
            if (ReasoningTokens > 0) text += " (" + ReasoningTokens.ToString("N0") + " reasoning)";
            if (!string.IsNullOrEmpty(CostText)) text += " · " + CostText;
            return text;
        }
    }

    public sealed class ProviderTurn
    {
        public string Text;
        public string Reasoning;
        public List<ToolCall> ToolCalls = new List<ToolCall>();
        public TokenUsage Usage = new TokenUsage();
        public string RawItemsJson;
        public string StopReason;
    }

    public enum StreamEventKind
    {
        TransportActivity,
        ToolExecutionStarted,
        Status,
        TextDelta,
        ReasoningDelta,
        ToolCallStarted,
        ToolCallArguments,
        ToolCallCompleted,
        ToolCallResult,
        ChangeStaged,
        Usage,
        Completed,
        Error
    }

    public sealed class StreamEvent
    {
        public StreamEventKind Kind;
        public string Text;
        public ToolCall ToolCall;
        public TokenUsage Usage;
        public bool IsError;
        public bool ToolOk = true;
        public string ToolOutput;
        public List<string> ChangeIds;

        public static StreamEvent Status(string text) { return new StreamEvent { Kind = StreamEventKind.Status, Text = text }; }
        public static StreamEvent Delta(string text) { return new StreamEvent { Kind = StreamEventKind.TextDelta, Text = text }; }
        public static StreamEvent Reasoning(string text) { return new StreamEvent { Kind = StreamEventKind.ReasoningDelta, Text = text }; }
        public static StreamEvent Error(string text) { return new StreamEvent { Kind = StreamEventKind.Error, Text = text, IsError = true }; }
        public static StreamEvent Result(ToolCall call, string text, bool ok, List<string> changeIds)
        {
            return new StreamEvent
            {
                Kind = StreamEventKind.ToolCallResult,
                ToolCall = call,
                Text = text,
                ToolOk = ok,
                ChangeIds = changeIds ?? new List<string>()
            };
        }
    }

    public sealed class ModelInfo
    {
        public string Id;
        public string DisplayName;
        public int ContextWindow;
        public int MaxOutputTokens;
        public List<string> EffortLevels = new List<string>();

        public override string ToString()
        {
            return string.IsNullOrEmpty(DisplayName) || DisplayName == Id ? Id : DisplayName + " (" + Id + ")";
        }
    }

    public sealed class ToolSpec
    {
        public string Name;
        public string Description;
        public JsonValue Schema;
    }

    public enum ProviderConnectionState
    {
        NotConfigured,
        Ready,
        SigningIn,
        Connected,
        Error
    }

    public interface ILiveProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string BillingNote { get; }
        bool IsConfigured(out string reason);
        Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken);
        Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken cancellationToken);
        Task<string> TestConnectionAsync(CancellationToken cancellationToken);
    }
}
