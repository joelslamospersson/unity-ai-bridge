using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityAIBridge
{
    [Serializable]
    public class BridgeSettings
    {
        public const string ProviderManual = "manual"; // legacy only
        public const string ProviderChatGpt = "chatgpt";
        public const string ProviderDeepSeek = "deepseek";
        public const string ProviderOpenCode = "opencode";
        public const string ProviderOpenAi = "openai";
        public const string ProviderOllama = "ollama";

        public const int AccessReadOnly = 0;
        public const int AccessAsk = 1;
        public const int AccessWrite = 2;

        public int AccessLevel = AccessAsk;
        public bool AutoApplyWithWriteAccess = false;
        public bool ConfirmScriptCompilation = true;

        public string Provider = ProviderDeepSeek;
        public string OllamaUrl = "http://127.0.0.1:11434";
        public string OllamaModel = "llama3.1";
        public string OpenAiBaseUrl = "https://api.openai.com/v1";
        public string OpenAiModel = "gpt-5.2";
        public string OpenAiApiStyle = "responses";

        public string DeepSeekBaseUrl = "https://api.deepseek.com";
        public string DeepSeekModel = "deepseek-flash";

        public string OpenCodeBaseUrl = "https://opencode.ai/zen/go/v1";
        public string OpenCodeModel = "deepseek-v4.1-flash";
        public bool DeepSeekThinking = true;
        public string DeepSeekReasoningEffort = "high";
        public int DeepSeekMaxTokens = 16384;

        public string ChatGptModel = "";

        public double Temperature = 0.2;
        public int MaxOutputTokens = 4000;
        public int RequestTimeoutSeconds = 300;
        public int MaxAgentSteps = 8;
        public bool ShowReasoning = true;

        public string DeepSeekModelsJson = "";
        public string OpenCodeModelsJson = "";
        public string OpenAiModelsJson = "";
        public string ChatGptModelsJson = "";
        public string OllamaModelsJson = "";

        public bool IncludeSceneContext = true;
        public bool IncludeConsoleContext = true;
        public bool IncludeFileTree = true;
        public bool IncludeExistingScripts = true;
        public int MaxContextCharacters = 60000;

        public bool McpEnabled = false;
        public bool McpAutoStart = false;
        public int McpPort = 7777;

        public bool AllowSelfModification = false;
        public bool LogToFile = true;

        public string ManualExportFolder = "";
        public string ProjectNotes = "";

        [NonSerialized] static BridgeSettings _current;
        [NonSerialized] static BridgeSecrets _secrets;

        public static BridgeSettings Current
        {
            get
            {
                if (_current == null) Load();
                return _current;
            }
        }

        public static BridgeSecrets Secrets
        {
            get
            {
                if (_secrets == null) Load();
                return _secrets;
            }
        }

        public static void Load()
        {
            if (BridgeSettings._current == null)
            {
                _current = LoadJson<BridgeSettings>(BridgePaths.SettingsFile);
                if (_current == null) _current = new BridgeSettings();
                _current.Normalize();
            }
            if (_secrets == null)
            {
                _secrets = LoadJson<BridgeSecrets>(BridgePaths.SecretsFile);
                if (_secrets == null) _secrets = new BridgeSecrets();
            }
        }

        static T LoadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text.Trim())) return null;
                return JsonUtility.FromJson<T>(text);
            }
            catch (Exception e)
            {
                try
                {
                    File.Copy(path, path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
                }
                catch { }
                Debug.LogWarning("[Unity AI Bridge] Could not read " + path + ": " + e.Message);
                return null;
            }
        }

        public static void Save()
        {
            Load();
            BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
            string json = JsonUtility.ToJson(_current, true);
            string temp = BridgePaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(BridgePaths.SettingsFile)) File.Delete(BridgePaths.SettingsFile);
            File.Move(temp, BridgePaths.SettingsFile);
            BridgePaths.TrySetUnixMode(BridgePaths.SettingsFile, 384);
            SaveSecrets();
        }

        public static void SaveSecrets()
        {
            if (_secrets == null) _secrets = new BridgeSecrets();
            string json = JsonUtility.ToJson(_secrets, true);
            BridgePaths.WriteSecretFile(BridgePaths.SecretsFile, json);
        }

        public static void Reset()
        {
            _current = new BridgeSettings();
            _secrets = new BridgeSecrets();
            Save();
        }

        public void Normalize()
        {
            if (string.IsNullOrEmpty(Provider) || Provider == ProviderManual) Provider = ProviderDeepSeek;
            if (Provider != ProviderChatGpt && Provider != ProviderDeepSeek && Provider != ProviderOpenCode &&
                Provider != ProviderOpenAi && Provider != ProviderOllama)
                Provider = ProviderDeepSeek;
            if (string.IsNullOrEmpty(DeepSeekBaseUrl)) DeepSeekBaseUrl = "https://api.deepseek.com";
            if (string.IsNullOrEmpty(DeepSeekModel)) DeepSeekModel = "deepseek-flash";
            if (string.IsNullOrEmpty(DeepSeekReasoningEffort)) DeepSeekReasoningEffort = "high";
            if (string.IsNullOrEmpty(OpenCodeBaseUrl)) OpenCodeBaseUrl = "https://opencode.ai/zen/go/v1";
            if (string.IsNullOrEmpty(OpenCodeModel)) OpenCodeModel = "deepseek-v4.1-flash";
            if (DeepSeekMaxTokens < 256) DeepSeekMaxTokens = 16384;
            if (string.IsNullOrEmpty(OpenAiBaseUrl)) OpenAiBaseUrl = "https://api.openai.com/v1";
            if (string.IsNullOrEmpty(OpenAiModel)) OpenAiModel = "gpt-5.2";
            if (string.IsNullOrEmpty(OllamaUrl)) OllamaUrl = "http://127.0.0.1:11434";
            if (string.IsNullOrEmpty(OllamaModel)) OllamaModel = "llama3.1";
            if (MaxContextCharacters < 1000) MaxContextCharacters = 60000;
            if (MaxAgentSteps < 1 || MaxAgentSteps > 24) MaxAgentSteps = 8;
            if (RequestTimeoutSeconds < 10) RequestTimeoutSeconds = 300;
            if (MaxOutputTokens < 256) MaxOutputTokens = 4000;
            AccessLevel = Mathf.Clamp(AccessLevel, 0, 2);
            McpPort = Mathf.Clamp(McpPort <= 0 ? 7777 : McpPort, 1024, 65535);
        }

        public AccessMode Level { get { return (AccessMode)Mathf.Clamp(AccessLevel, 0, 2); } }
        public bool IsReadOnly { get { return Level == AccessMode.ReadOnly; } }
        public bool IsAskMode { get { return Level == AccessMode.AskBeforeEditing; } }
        public bool HasProjectWrite { get { return Level == AccessMode.ProjectWrite; } }

        public string ManualExportPath
        {
            get
            {
                if (!string.IsNullOrEmpty(ManualExportFolder)) return ManualExportFolder;
                return BridgePaths.ExportsDir;
            }
        }

        public static string EnsureMcpToken()
        {
            Load();
            if (_secrets == null) _secrets = new BridgeSecrets();
            if (string.IsNullOrEmpty(_secrets.McpToken))
            {
                _secrets.McpToken = GenerateToken();
                SaveSecrets();
            }
            return _secrets.McpToken;
        }

        public static string RegenerateMcpToken()
        {
            Load();
            _secrets.McpToken = GenerateToken();
            SaveSecrets();
            return _secrets.McpToken;
        }

        static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        public static string Mask(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "(not set)";
            if (secret.Length <= 8) return new string('*', secret.Length);
            return secret.Substring(0, 4) + new string('*', 8) + secret.Substring(secret.Length - 4);
        }

        // Convenience wrappers so the enum lives in one place.
        public enum AccessMode
        {
            ReadOnly = 0,
            AskBeforeEditing = 1,
            ProjectWrite = 2
        }
    }

    [Serializable]
    public class BridgeSecrets
    {
        public string OpenAiApiKey = "";
        public string DeepSeekApiKey = "";
        public string OpenCodeApiKey = "";
        public string McpToken = "";
        public string OllamaApiKey = "";

        public string ChatGptAccessToken = "";
        public string ChatGptRefreshToken = "";
        public string ChatGptIdToken = "";
        public string ChatGptClientId = "";
        public string ChatGptEmail = "";
        public string ChatGptSubject = "";
        public string ChatGptScopes = "";
        public string ChatGptTokenType = "Bearer";
        public long ChatGptExpiresAtUnix = 0;
        public string ChatGptHostId = "";
        public bool ChatGptPlanUsageEnabled = false;
    }
}
