using System;

namespace UnityAIBridge
{
    public static class ProviderFactory
    {
        public static ILiveProvider Create(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case BridgeSettings.ProviderChatGpt: return new OpenAiResponsesProvider(true);
                case BridgeSettings.ProviderDeepSeek: return new DeepSeekProvider();
                case BridgeSettings.ProviderOpenCode: return new OpenCodeGoProvider();
                case BridgeSettings.ProviderOpenAi: return new OpenAiResponsesProvider(false);
                case BridgeSettings.ProviderOllama: return new OllamaLiveProvider();
                default: return null;
            }
        }

        public static string DisplayName(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case BridgeSettings.ProviderChatGpt: return "ChatGPT";
                case BridgeSettings.ProviderDeepSeek: return "DeepSeek";
                case BridgeSettings.ProviderOpenCode: return "OpenCode Go";
                case BridgeSettings.ProviderOpenAi: return "OpenAI API";
                case BridgeSettings.ProviderOllama: return "Ollama (local)";
                default: return "Not configured";
            }
        }

        public static string BillingNote(string id)
        {
            var provider = Create(id);
            return provider != null ? provider.BillingNote : string.Empty;
        }

        public static bool IsConfigured(string id, out string reason)
        {
            var provider = Create(id);
            if (provider == null) { reason = "Unknown provider."; return false; }
            return provider.IsConfigured(out reason);
        }
    }
}
