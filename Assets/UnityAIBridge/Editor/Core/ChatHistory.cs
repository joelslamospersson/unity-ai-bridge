using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UnityAIBridge
{
    [Serializable]
    public class ChatFileRef
    {
        public string Path;
        public string ChangeId;
        public string CallId;
        public string Status;
        public string Title;
        public bool IsRead;
    }

    [Serializable]
    public class ChatToolActivity
    {
        public string CallId;
        public string Tool;
        public string Status;
        public bool Ok = true;
        public string Detail;
        public string Output;
        public List<string> ChangeIds = new List<string>();
    }

    [Serializable]
    public class ConversationMeta
    {
        public int Version = 1;
        public string Id;
        public string Provider;
        public string Title;
        public string Model;
        public string CreatedUtc;
        public string UpdatedUtc;
        public int MessageCount;
        public int PageCount;
        public string LastUserText;
        public bool AutoAcceptAll;
    }

    [Serializable]
    public class ConversationPage
    {
        public int Version = 1;
        public int Index;
        public List<ChatMessage> Items = new List<ChatMessage>();
    }

    [Serializable]
    public class ConversationContext
    {
        public int Version = 1;
        public List<LlmMessage> Messages = new List<LlmMessage>();
    }

    /// <summary>
    /// Versioned conversation storage independent of the UI. Display history is stored
    /// in pages of 50; model context is stored separately and capped. It never reads all
    /// pages just to open the latest conversation.
    /// </summary>
    public static class ChatHistory
    {
        public const int PageSize = 50;
        public const int ContextLimit = 120;

        public static string HistoryRoot { get { return Path.Combine(BridgePaths.ProjectConfigDir, "history"); } }
        public static string ProviderRoot(string provider) { return Path.Combine(HistoryRoot, BridgePaths.Sanitize(provider)); }
        public static string Root(ConversationMeta meta) { return Path.Combine(ProviderRoot(meta.Provider), meta.Id); }
        static string MetaFile(ConversationMeta meta) { return Path.Combine(Root(meta), "meta.json"); }
        static string ContextFile(ConversationMeta meta) { return Path.Combine(Root(meta), "context.json"); }
        static string StreamingFile(ConversationMeta meta) { return Path.Combine(Root(meta), "streaming.json"); }
        static string PageFile(ConversationMeta meta, int index) { return Path.Combine(Root(meta), "page-" + index.ToString("D6") + ".json"); }
        static string ActiveFile(string provider) { return Path.Combine(ProviderRoot(provider), "active.txt"); }
        static string MigrationMarker(string provider) { return Path.Combine(ProviderRoot(provider), ".v2"); }

        public static void EnsureMigrated(string provider)
        {
            try
            {
                string root = ProviderRoot(provider);
                Directory.CreateDirectory(root);
                string marker = MigrationMarker(provider);
                if (File.Exists(marker)) return;

                bool hasConversations = false;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, "meta.json"))) { hasConversations = true; break; }
                }
                if (!hasConversations)
                {
                    string legacy = BridgePaths.ChatSessionFile(provider);
                    if (File.Exists(legacy))
                    {
                        var data = JsonUtility.FromJson<ChatSessionData>(File.ReadAllText(legacy, Encoding.UTF8));
                        if (data != null && data.Display != null && data.Display.Count > 0)
                        {
                            var meta = NewConversation(provider, string.IsNullOrEmpty(data.Model) ? "" : data.Model, "Imported chat");
                            int page = 0;
                            var current = new ConversationPage { Index = 0 };
                            foreach (var message in data.Display)
                            {
                                if (current.Items.Count >= PageSize)
                                {
                                    WritePage(meta, current);
                                    page++;
                                    current = new ConversationPage { Index = page };
                                }
                                if (message != null) current.Items.Add(message);
                            }
                            if (current.Items.Count > 0) WritePage(meta, current);
                            meta.PageCount = page + 1;
                            meta.MessageCount = data.Display.Count;
                            meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
                            var firstUser = data.Display.Find(m => m != null && m.Role == "user");
                            if (firstUser != null)
                            {
                                meta.Title = MakeTitle(firstUser.Content);
                                meta.LastUserText = Truncate(firstUser.Content, 200);
                            }
                            WriteMeta(meta);
                            if (data.Transcript != null) SaveContext(meta, data.Transcript);
                            SetActive(provider, meta.Id);
                        }
                        try { File.Move(legacy, legacy + ".migrated"); } catch { }
                    }
                }
                File.WriteAllText(marker, "2");
            }
            catch (Exception e)
            {
                BridgeLog.Warn("history", "Migration failed for " + provider + ": " + e.Message);
            }
        }

        public static ConversationMeta LoadOrCreateActive(string provider, string model)
        {
            EnsureMigrated(provider);
            string active = ActiveFile(provider);
            string id = File.Exists(active) ? File.ReadAllText(active).Trim() : null;
            var meta = string.IsNullOrEmpty(id) ? null : LoadMeta(provider, id);
            if (meta == null)
            {
                var list = List(provider, null);
                meta = list.Count > 0 ? list[0] : NewConversation(provider, model, "New chat");
            }
            if (string.IsNullOrEmpty(meta.Model) && !string.IsNullOrEmpty(model)) { meta.Model = model; WriteMeta(meta); }
            SetActive(provider, meta.Id);
            return meta;
        }

        public static ConversationMeta NewConversation(string provider, string model, string title = "New chat")
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var meta = new ConversationMeta
            {
                Id = id,
                Provider = provider,
                Title = string.IsNullOrEmpty(title) ? "New chat" : title,
                Model = model ?? string.Empty,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                MessageCount = 0,
                PageCount = 0
            };
            Directory.CreateDirectory(Root(meta));
            WriteMeta(meta);
            SetActive(provider, id);
            return meta;
        }

        public static ConversationMeta LoadMeta(string provider, string id)
        {
            string path = Path.Combine(ProviderRoot(provider), id, "meta.json");
            return File.Exists(path) ? ReadJson<ConversationMeta>(path) : null;
        }

        public static List<ConversationMeta> List(string provider, string search)
        {
            EnsureMigrated(provider);
            var result = new List<ConversationMeta>();
            string root = ProviderRoot(provider);
            if (!Directory.Exists(root)) return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var meta = ReadJson<ConversationMeta>(Path.Combine(dir, "meta.json"));
                if (meta == null) continue;
                if (!string.IsNullOrEmpty(search))
                {
                    bool match = Contains(meta.Title, search) || Contains(meta.LastUserText, search);
                    if (!match)
                    {
                        try
                        {
                            foreach (var page in Directory.GetFiles(dir, "page-*.json"))
                            {
                                if (Contains(File.ReadAllText(page), search)) { match = true; break; }
                            }
                        }
                        catch { }
                    }
                    if (!match) continue;
                }
                result.Add(meta);
            }
            result.Sort((a, b) => string.CompareOrdinal(b.UpdatedUtc, a.UpdatedUtc));
            return result;
        }

        public static List<ChatMessage> LoadLatestPage(ConversationMeta meta, out int oldestPage)
        {
            oldestPage = -1;
            meta = RefreshMeta(meta);
            if (meta.PageCount <= 0) return new List<ChatMessage>();
            oldestPage = meta.PageCount - 1;
            var page = ReadJson<ConversationPage>(PageFile(meta, oldestPage));
            return page != null && page.Items != null ? page.Items : new List<ChatMessage>();
        }

        public static List<ChatMessage> LoadOlderPage(ConversationMeta meta, int currentOldest, out int newOldest)
        {
            newOldest = currentOldest;
            if (currentOldest <= 0) return null;
            int index = currentOldest - 1;
            var page = ReadJson<ConversationPage>(PageFile(meta, index));
            if (page == null) return null;
            newOldest = index;
            return page.Items ?? new List<ChatMessage>();
        }

        public static ConversationMeta RefreshMeta(ConversationMeta meta)
        {
            var refreshed = LoadMeta(meta.Provider, meta.Id);
            if (refreshed == null) return meta;
            meta.Title = refreshed.Title;
            meta.Model = refreshed.Model;
            meta.MessageCount = refreshed.MessageCount;
            meta.PageCount = refreshed.PageCount;
            meta.UpdatedUtc = refreshed.UpdatedUtc;
            meta.LastUserText = refreshed.LastUserText;
            return meta;
        }

        public static void Append(ConversationMeta meta, ChatMessage message)
        {
            if (meta == null || message == null) return;
            meta = RefreshMeta(meta);
            Directory.CreateDirectory(Root(meta));
            int index = Math.Max(0, meta.PageCount - 1);
            ConversationPage page = meta.PageCount > 0 ? ReadJson<ConversationPage>(PageFile(meta, index)) : null;
            if (page == null) { page = new ConversationPage { Index = 0 }; index = 0; }
            if (page.Items == null) page.Items = new List<ChatMessage>();
            if (page.Items.Count >= PageSize)
            {
                index++;
                page = new ConversationPage { Index = index };
            }
            page.Items.Add(message);
            WritePage(meta, page);
            meta.PageCount = index + 1;
            meta.MessageCount++;
            meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
            if (message.Role == "user")
            {
                meta.LastUserText = Truncate(message.Content, 200);
                if (string.IsNullOrEmpty(meta.Title) || meta.Title == "New chat") meta.Title = MakeTitle(message.Content);
            }
            WriteMeta(meta);
        }

        public static void UpdateMessage(ConversationMeta meta, ChatMessage message)
        {
            if (meta == null || message == null) return;
            meta = RefreshMeta(meta);
            int index = Math.Max(0, meta.PageCount - 1);
            var page = ReadJson<ConversationPage>(PageFile(meta, index));
            if (page == null || page.Items == null) { Append(meta, message); return; }
            int found = page.Items.FindIndex(m => m != null && m.Id == message.Id);
            if (found < 0) { Append(meta, message); return; }
            page.Items[found] = message;
            WritePage(meta, page);
            meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
            WriteMeta(meta);
        }

        public static void SaveStreaming(ConversationMeta meta, ChatMessage message)
        {
            if (meta == null || message == null) return;
            try { WriteAtomic(StreamingFile(meta), JsonUtility.ToJson(message)); }
            catch { }
        }

        public static ChatMessage LoadStreaming(ConversationMeta meta)
        {
            try
            {
                return File.Exists(StreamingFile(meta)) ? JsonUtility.FromJson<ChatMessage>(File.ReadAllText(StreamingFile(meta))) : null;
            }
            catch { return null; }
        }

        public static void DeleteStreaming(ConversationMeta meta)
        {
            try { if (File.Exists(StreamingFile(meta))) File.Delete(StreamingFile(meta)); } catch { }
        }

        public static List<LlmMessage> LoadContext(ConversationMeta meta)
        {
            var context = ReadJson<ConversationContext>(ContextFile(meta));
            return context != null && context.Messages != null ? context.Messages : new List<LlmMessage>();
        }

        public static void SaveContext(ConversationMeta meta, List<LlmMessage> messages)
        {
            try
            {
                if (meta == null) return;
                var context = new ConversationContext();
                if (messages != null)
                {
                    int start = Math.Max(0, messages.Count - ContextLimit);
                    // Keep complete user turns so function outputs never lose their calls.
                    if (start > 0 && messages.Exists(m => m != null && m.Role == "user"))
                    {
                        int boundary = start;
                        while (boundary < messages.Count && (messages[boundary] == null || messages[boundary].Role != "user")) boundary++;
                        if (boundary < messages.Count) start = boundary;
                        else while (start > 0 && (messages[start] == null || messages[start].Role != "user")) start--;
                    }
                    for (int i = start; i < messages.Count; i++) context.Messages.Add(messages[i]);
                }
                Directory.CreateDirectory(Root(meta));
                WriteAtomic(ContextFile(meta), JsonUtility.ToJson(context));
            }
            catch (Exception e) { BridgeLog.Warn("history", "Could not save context: " + e.Message); }
        }

        public static void SaveMeta(ConversationMeta meta)
        {
            if (meta == null) return;
            meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
            WriteMeta(meta);
        }

        public static void ClearConversation(ConversationMeta meta)
        {
            if (meta == null) return;
            try
            {
                Directory.CreateDirectory(Root(meta));
                foreach (var file in Directory.GetFiles(Root(meta), "page-*.json")) File.Delete(file);
                if (File.Exists(ContextFile(meta))) File.Delete(ContextFile(meta));
                if (File.Exists(StreamingFile(meta))) File.Delete(StreamingFile(meta));
                meta.MessageCount = 0;
                meta.PageCount = 0;
                meta.Title = "New chat";
                meta.LastUserText = string.Empty;
                meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
                WriteMeta(meta);
            }
            catch (Exception e) { BridgeLog.Warn("history", "Could not clear conversation: " + e.Message); }
        }

        public static void Rename(ConversationMeta meta, string title)
        {
            if (meta == null) return;
            meta.Title = string.IsNullOrEmpty(title) ? "New chat" : title.Trim();
            meta.UpdatedUtc = DateTime.UtcNow.ToString("o");
            WriteMeta(meta);
        }

        public static void Delete(ConversationMeta meta)
        {
            if (meta == null) return;
            try { Directory.Delete(Root(meta), true); } catch { }
            try
            {
                string active = ActiveFile(meta.Provider);
                if (File.Exists(active) && File.ReadAllText(active).Trim() == meta.Id) File.Delete(active);
            }
            catch { }
        }

        public static void ClearProvider(string provider)
        {
            try { if (Directory.Exists(ProviderRoot(provider))) Directory.Delete(ProviderRoot(provider), true); }
            catch { }
        }

        public static void SetActive(string provider, string id)
        {
            try
            {
                Directory.CreateDirectory(ProviderRoot(provider));
                WriteAtomic(ActiveFile(provider), id);
            }
            catch { }
        }

        static void WritePage(ConversationMeta meta, ConversationPage page)
        {
            Directory.CreateDirectory(Root(meta));
            WriteAtomic(PageFile(meta, page.Index), JsonUtility.ToJson(page));
        }

        static void WriteMeta(ConversationMeta meta)
        {
            Directory.CreateDirectory(Root(meta));
            WriteAtomic(MetaFile(meta), JsonUtility.ToJson(meta));
            SetActive(meta.Provider, meta.Id);
        }

        static T ReadJson<T>(string path) where T : class
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        static void WriteAtomic(string path, string contents)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); return; }
                catch { }
            }
            File.Move(temp, path);
        }

        static string MakeTitle(string text)
        {
            if (string.IsNullOrEmpty(text)) return "New chat";
            string oneLine = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return oneLine.Length > 48 ? oneLine.Substring(0, 48) + "…" : oneLine;
        }

        static string Truncate(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
