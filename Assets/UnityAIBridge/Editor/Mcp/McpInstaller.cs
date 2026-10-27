using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace UnityAIBridge
{
    public enum McpClientKind
    {
        Manual,
        JsonMcpServers,
        JsonServers,
        OpenCode,
        CodexToml
    }

    public enum McpInstallState
    {
        NotInstalled,
        Installed,
        InstalledStale,
        ConfigUnreadable,
        Manual
    }

    public sealed class McpClient
    {
        public string Id;
        public string Name;
        public string Description;
        public string Path;
        public string DocsUrl;
        public McpClientKind Kind;

        public bool CanInstall { get { return Kind != McpClientKind.Manual; } }
    }

    /// <summary>
    /// Installs the local Unity AI Bridge MCP server into supported client configs.
    /// Existing config is parsed, merged (never replaced wholesale), backed up and
    /// written atomically with owner-only permissions.
    /// </summary>
    public static class McpInstaller
    {
        public const string ServerName = "unity-ai-bridge";
        const string SectionName = "mcp_servers." + ServerName;

        public static List<McpClient> Clients()
        {
            string home = Home();
            string xdg = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
                ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                : System.IO.Path.Combine(home, ".config");

            var clients = new List<McpClient>();
            clients.Add(new McpClient
            {
                Id = "codex",
                Name = "Codex",
                Description = "OpenAI Codex CLI (ChatGPT sign-in). Writes ~/.codex/config.toml with a static Authorization header.",
                Path = System.IO.Path.Combine(home, ".codex", "config.toml"),
                DocsUrl = "https://developers.openai.com/codex/mcp",
                Kind = McpClientKind.CodexToml
            });
            clients.Add(new McpClient
            {
                Id = "opencode",
                Name = "OpenCode",
                Description = "OpenCode global config. Supports both the v1 direct mcp map and the v2 mcp.servers layout.",
                Path = OpenCodeConfigPath(xdg),
                DocsUrl = "https://opencode.ai/docs/mcp-servers/",
                Kind = McpClientKind.OpenCode
            });
            clients.Add(new McpClient
            {
                Id = "cursor",
                Name = "Cursor",
                Description = "Cursor global MCP config (~/.cursor/mcp.json).",
                Path = System.IO.Path.Combine(home, ".cursor", "mcp.json"),
                DocsUrl = "https://docs.cursor.com/context/mcp",
                Kind = McpClientKind.JsonMcpServers
            });
            clients.Add(new McpClient
            {
                Id = "vscode",
                Name = "VS Code",
                Description = "VS Code user MCP config. Uses the servers object and HTTP transport.",
                Path = System.IO.Path.Combine(xdg, "Code", "User", "mcp.json"),
                DocsUrl = "https://code.visualstudio.com/docs/agents/reference/mcp-configuration",
                Kind = McpClientKind.JsonServers
            });
            clients.Add(new McpClient
            {
                Id = "claudecode",
                Name = "Claude Code",
                Description = "Creates/merges ~/.claude.json with an HTTP mcpServers entry.",
                Path = System.IO.Path.Combine(home, ".claude.json"),
                DocsUrl = "https://code.claude.com/docs/en/mcp",
                Kind = McpClientKind.JsonMcpServers
            });
            clients.Add(new McpClient
            {
                Id = "claudedesktop",
                Name = "Claude Desktop",
                Description = "Claude Desktop requires its Connectors UI for remote servers and does not document a static header map; configure it manually.",
                Path = System.IO.Path.Combine(xdg, "Claude", "claude_desktop_config.json"),
                DocsUrl = "https://support.anthropic.com/en/articles/11175166-about-custom-integrations-using-remote-mcp",
                Kind = McpClientKind.Manual
            });
            clients.Add(new McpClient
            {
                Id = "inspector",
                Name = "MCP Inspector",
                Description = "No installation needed. Use the generated command in a terminal.",
                DocsUrl = "https://modelcontextprotocol.io/docs/tools/inspector",
                Kind = McpClientKind.Manual
            });
            return clients;
        }

        public static string ServerUrl()
        {
            if (McpServer.Current != null && McpServer.Current.IsRunning) return McpServer.Current.Url;
            return "http://127.0.0.1:" + BridgeSettings.Current.McpPort + "/mcp";
        }

        public static string ServerToken() { return BridgeSettings.EnsureMcpToken(); }

        public static McpInstallState GetState(McpClient client, out string detail)
        {
            detail = null;
            if (client == null || client.Kind == McpClientKind.Manual) return McpInstallState.Manual;
            if (!File.Exists(client.Path))
            {
                detail = "Config not created yet.";
                return McpInstallState.NotInstalled;
            }
            try
            {
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.ReadAllText(client.Path, Encoding.UTF8);
                    if (!McpToml.ReadServers(text)[ServerName].IsObject)
                        return McpInstallState.NotInstalled;
                    return CompareToml(text) ? McpInstallState.Installed : McpInstallState.InstalledStale;
                }

                JsonValue root;
                string error;
                if (!McpConfigSync.TryReadJson(client, File.ReadAllText(client.Path, Encoding.UTF8), out root, out error) || !root.IsObject)
                {
                    detail = "Config is not valid JSON: " + error;
                    return McpInstallState.ConfigUnreadable;
                }

                JsonValue entry = FindJsonEntry(root, client);
                if (entry == null || !entry.IsObject) return McpInstallState.NotInstalled;
                string url = entry["url"].AsString(entry["serverUrl"].AsString());
                string auth = entry["headers"]["Authorization"].AsString(entry["headers"]["authorization"].AsString());
                bool fresh = string.Equals(url, ServerUrl(), StringComparison.Ordinal) &&
                             string.Equals(auth, "Bearer " + ServerToken(), StringComparison.Ordinal);
                return fresh ? McpInstallState.Installed : McpInstallState.InstalledStale;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return McpInstallState.ConfigUnreadable;
            }
        }

        public static string Install(McpClient client)
        {
            if (client == null) return "No client selected.";
            if (!client.CanInstall) return client.Name + " does not support click-to-install; use its own configuration UI or the manual config.";

            try
            {
                string directory = System.IO.Path.GetDirectoryName(client.Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                Backup(client.Path);

                string token = ServerToken();
                string url = ServerUrl();
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string existing = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : string.Empty;
                    string updated = ReplaceTomlSection(existing, url, token);
                    WritePrivate(client.Path, updated);
                }
                else
                {
                    string existing = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : "{}";
                    JsonValue root;
                    string error;
                    if (!McpConfigSync.TryReadJson(client, string.IsNullOrWhiteSpace(existing) ? "{}" : existing, out root, out error) || !root.IsObject)
                        return "Existing config is not valid JSON; not modified. " + error;

                    if (client.Kind == McpClientKind.OpenCode) UpsertOpenCode(root, url, token);
                    else if (client.Kind == McpClientKind.JsonServers) UpsertCollection(root, "servers", BuildHttpEntry(url, token, true));
                    else UpsertCollection(root, "mcpServers", BuildHttpEntry(url, token, client.Id == "claudecode"));

                    WritePrivate(client.Path, root.ToJson(true) + "\n");
                }

                BridgeLog.Success("mcp", "Installed " + ServerName + " into " + client.Name + " (" + client.Path + ").");
                return null;
            }
            catch (Exception e)
            {
                BridgeLog.Warn("mcp", "Could not install into " + client.Name + ": " + e.Message);
                return e.Message;
            }
        }

        public static string Uninstall(McpClient client)
        {
            if (client == null) return "No client selected.";
            if (!client.CanInstall) return "This client does not support automatic removal.";
            try
            {
                if (!File.Exists(client.Path)) return null;
                Backup(client.Path);
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.ReadAllText(client.Path, Encoding.UTF8);
                    WritePrivate(client.Path, RemoveTomlSection(text));
                }
                else
                {
                    JsonValue root;
                    string error;
                    if (!McpConfigSync.TryReadJson(client, File.ReadAllText(client.Path, Encoding.UTF8), out root, out error) || !root.IsObject)
                        return "Config is not valid JSON; not modified.";
                    RemoveJsonEntry(root, client);
                    WritePrivate(client.Path, root.ToJson(true) + "\n");
                }
                BridgeLog.Info("mcp", "Removed " + ServerName + " from " + client.Name + ".");
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static string ManualJson()
        {
            return BuildHttpEntry(ServerUrl(), ServerToken(), true).ToJson(true);
        }

        public static string InspectorCommand()
        {
            return "npx @modelcontextprotocol/inspector@latest\n" +
                   "# Transport: Streamable HTTP\n" +
                   "# URL: " + ServerUrl() + "\n" +
                   "# Header: Authorization: Bearer " + ServerToken();
        }

        // ------------------------------------------------------------------ JSON helpers

        static JsonValue BuildHttpEntry(string url, string token, bool includeType)
        {
            var entry = JsonValue.Obj(
                "url", url,
                "headers", JsonValue.Obj("Authorization", "Bearer " + token));
            if (includeType) entry["type"] = "http";
            return entry;
        }

        static void UpsertCollection(JsonValue root, string collectionName, JsonValue entry)
        {
            var collection = root[collectionName];
            if (!collection.IsObject)
            {
                collection = JsonValue.NewObject();
                root[collectionName] = collection;
            }
            collection[ServerName] = entry;
        }

        static void UpsertOpenCode(JsonValue root, string url, string token)
        {
            var mcp = root["mcp"];
            if (!mcp.IsObject)
            {
                mcp = JsonValue.NewObject();
                root["mcp"] = mcp;
            }
            var entry = JsonValue.Obj(
                "type", "remote",
                "url", url,
                "enabled", true,
                "oauth", false,
                "headers", JsonValue.Obj("Authorization", "Bearer " + token));
            if (mcp["servers"].IsObject) mcp["servers"][ServerName] = entry;
            else mcp[ServerName] = entry;
        }

        static JsonValue FindJsonEntry(JsonValue root, McpClient client)
        {
            if (client.Kind == McpClientKind.OpenCode)
            {
                var mcp = root["mcp"];
                if (mcp["servers"].IsObject) return mcp["servers"][ServerName];
                return mcp[ServerName];
            }
            if (client.Kind == McpClientKind.JsonServers) return root["servers"][ServerName];
            return root["mcpServers"][ServerName];
        }

        static void RemoveJsonEntry(JsonValue root, McpClient client)
        {
            if (client.Kind == McpClientKind.OpenCode)
            {
                var mcp = root["mcp"];
                if (mcp["servers"].IsObject) mcp["servers"].ObjectValue.Remove(ServerName);
                else if (mcp.IsObject) mcp.ObjectValue.Remove(ServerName);
                return;
            }
            string key = client.Kind == McpClientKind.JsonServers ? "servers" : "mcpServers";
            var collection = root[key];
            if (collection.IsObject) collection.ObjectValue.Remove(ServerName);
        }

        // ------------------------------------------------------------------ TOML helpers

        static string ReplaceTomlSection(string text, string url, string token)
        {
            string cleaned = RemoveTomlSection(text);
            if (!string.IsNullOrEmpty(cleaned) && !cleaned.EndsWith("\n", StringComparison.Ordinal)) cleaned += "\n";
            if (!string.IsNullOrEmpty(cleaned)) cleaned += "\n";
            cleaned += "[" + SectionName + "]\n";
            cleaned += "url = \"" + EscapeToml(url) + "\"\n";
            cleaned += "enabled = true\n";
            cleaned += "http_headers = { Authorization = \"Bearer " + EscapeToml(token) + "\" }\n";
            return cleaned;
        }

        static string RemoveTomlSection(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var output = new List<string>();
            bool skipping = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    skipping = McpToml.IsServerTable(line, ServerName);
                    if (skipping) continue;
                }
                if (!skipping) output.Add(line);
            }
            return string.Join("\n", output.ToArray()).TrimEnd('\n') + (output.Count > 0 ? "\n" : string.Empty);
        }

        static bool CompareToml(string text)
        {
            string url = ExtractTomlValue(text, "url");
            bool urlOk = string.Equals(url, ServerUrl(), StringComparison.Ordinal);
            bool authOk = text.IndexOf("Bearer " + ServerToken(), StringComparison.Ordinal) >= 0;
            return urlOk && authOk;
        }

        static string ExtractTomlValue(string text, string key)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    inSection = string.Equals(trimmed, "[" + SectionName + "]", StringComparison.Ordinal);
                    continue;
                }
                if (!inSection) continue;
                if (!trimmed.StartsWith(key + " ", StringComparison.Ordinal) && !trimmed.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                int equals = trimmed.IndexOf('=');
                if (equals < 0) continue;
                string value = trimmed.Substring(equals + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    return value.Substring(1, value.Length - 2);
                return value;
            }
            return null;
        }

        static string EscapeToml(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ------------------------------------------------------------------ files

        internal static void Backup(string path)
        {
            if (!File.Exists(path)) return;
            string root = System.IO.Path.Combine(BridgePaths.ConfigRoot, "mcp-backups");
            Directory.CreateDirectory(root);
            string name = System.IO.Path.GetFileName(path) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "." + Guid.NewGuid().ToString("N") + ".bak";
            string target = System.IO.Path.Combine(root, name);
            File.Copy(path, target, true);
            BridgePaths.TrySetUnixMode(target, 384);
        }

        internal static void WritePrivate(string path, string contents)
        {
            string temp = path + ".unity-ai-bridge-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    BridgePaths.TrySetUnixMode(temp, 384);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(contents);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                BridgePaths.TrySetUnixMode(path, 384);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static string Home()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        static string OpenCodeConfigPath(string xdg)
        {
            string json = System.IO.Path.Combine(xdg, "opencode", "opencode.json");
            string jsonc = System.IO.Path.Combine(xdg, "opencode", "opencode.jsonc");
            return !File.Exists(json) && File.Exists(jsonc) ? jsonc : json;
        }
    }
}
