using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityAIBridge
{
    public enum McpAuthKind { None, OptionalBearer, OAuthOrToken }

    public sealed class McpCatalogServer
    {
        public string Name;
        public string DisplayName;
        public string Description;
        public bool IsStdio;
        public string Url;
        public string Command;
        public string[] Args;
        public McpAuthKind Auth;
        public string DocsUrl;
        public string SetupNote;
    }

    public sealed class McpServerSpec
    {
        public string Name;
        public string Url;
        public string Command;
        public string[] Args = new string[0];
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public bool OAuth;
        public bool IsStdio;
    }

    /// <summary>
    /// Installs additional third-party MCP servers into the same supported client
    /// configs used by <see cref="McpInstaller"/>. Existing servers are preserved.
    /// </summary>
    public static class McpCatalog
    {
        public static List<McpCatalogServer> Servers()
        {
            return new List<McpCatalogServer>
            {
                new McpCatalogServer
                {
                    Name = "context7",
                    DisplayName = "Context7",
                    Description = "Up-to-date library documentation. Optional API key (ctx7sk-...) or OAuth for higher limits/private libraries.",
                    IsStdio = false,
                    Url = "https://mcp.context7.com/mcp",
                    Auth = McpAuthKind.OptionalBearer,
                    DocsUrl = "https://context7.com/docs/resources/all-clients",
                    SetupNote = "API keys start with ctx7sk- and are created at context7.com/dashboard. OAuth uses /mcp/oauth and is handled by the client."
                },
                new McpCatalogServer
                {
                    Name = "github",
                    DisplayName = "GitHub",
                    Description = "Official GitHub MCP server for repositories, issues, pull requests and code search.",
                    IsStdio = false,
                    Url = "https://api.githubcopilot.com/mcp/",
                    Auth = McpAuthKind.OAuthOrToken,
                    DocsUrl = "https://github.com/github/github-mcp-server",
                    SetupNote = "Leave the token empty to let the client run OAuth. Enter a GitHub PAT if your client does not support MCP OAuth."
                },
                new McpCatalogServer
                {
                    Name = "unity-mcp",
                    DisplayName = "MCP for Unity (Coplay)",
                    Description = "CoplayDev MCP for Unity. Requires the 'MCP for Unity' package installed in this Unity project and its server running on localhost:8080.",
                    IsStdio = false,
                    Url = "http://localhost:8080/mcp",
                    Auth = McpAuthKind.None,
                    DocsUrl = "https://coplaydev.github.io/unity-mcp/getting-started/install",
                    SetupNote = "Install the package from Git URL: https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main, then Window > MCP for Unity to start it."
                },
                new McpCatalogServer
                {
                    Name = "blender",
                    DisplayName = "MCP for Blender",
                    Description = "Blender scene control via ahujasid/mcp-for-blender. Requires uv and the Blender add-on.",
                    IsStdio = true,
                    Command = "uvx",
                    Args = new[] { "mcp-for-blender" },
                    Auth = McpAuthKind.None,
                    DocsUrl = "https://www.mcp-for-blender.com/docs/mcp-clients",
                    SetupNote = "Install uvx and the Blender add-on (uvx mcp-for-blender setup). Only run one client against Blender at a time."
                },
                new McpCatalogServer
                {
                    Name = "playwright",
                    DisplayName = "Playwright",
                    Description = "Browser automation MCP server (Microsoft Playwright). No authentication required.",
                    IsStdio = true,
                    Command = "npx",
                    Args = new[] { "-y", "@playwright/mcp@latest" },
                    Auth = McpAuthKind.None,
                    DocsUrl = "https://github.com/microsoft/playwright-mcp",
                    SetupNote = "Requires Node.js/npx."
                }
            };
        }

        public static McpServerSpec BuildSpec(McpCatalogServer server, bool context7OAuth, string context7Key, string githubToken)
        {
            var spec = new McpServerSpec
            {
                Name = server.Name,
                IsStdio = server.IsStdio,
                Url = server.Url,
                Command = server.Command,
                Args = server.Args ?? new string[0]
            };
            if (server.Name == "context7")
            {
                if (context7OAuth)
                {
                    spec.Url = "https://mcp.context7.com/mcp/oauth";
                    spec.OAuth = true;
                }
                else if (!string.IsNullOrEmpty(context7Key))
                {
                    spec.Headers["Context7-API-Key"] = context7Key;
                }
            }
            else if (server.Name == "github")
            {
                if (!string.IsNullOrEmpty(githubToken))
                    spec.Headers["Authorization"] = "Bearer " + githubToken;
                else
                    spec.OAuth = true;
            }
            return spec;
        }

        public static bool IsInstalled(McpClient client, string name, out string detail)
        {
            detail = null;
            if (client == null || string.IsNullOrEmpty(client.Path) || !File.Exists(client.Path)) return false;
            try
            {
                if (client.Kind == McpClientKind.CodexToml)
                    return File.ReadAllText(client.Path).IndexOf("[" + Section(name) + "]", StringComparison.Ordinal) >= 0;
                JsonValue root;
                string error;
                if (!JsonValue.TryParse(File.ReadAllText(client.Path), out root, out error) || !root.IsObject)
                {
                    detail = "Config is not valid JSON.";
                    return false;
                }
                if (client.Kind == McpClientKind.OpenCode)
                {
                    var mcp = root["mcp"];
                    if (mcp["servers"].IsObject) return mcp["servers"][name].IsObject;
                    return mcp[name].IsObject;
                }
                string key = client.Kind == McpClientKind.JsonServers ? "servers" : "mcpServers";
                return root[key][name].IsObject;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return false;
            }
        }

        public static string Install(McpClient client, McpServerSpec spec)
        {
            if (client == null || !client.CanInstall) return "Select a supported client.";
            try
            {
                if (!File.Exists(client.Path)) Directory.CreateDirectory(Path.GetDirectoryName(client.Path));
                Backup(client.Path);
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : string.Empty;
                    File.WriteAllText(client.Path, ReplaceToml(text, spec), new UTF8Encoding(false));
                }
                else
                {
                    string text = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : "{}";
                    JsonValue root;
                    string error;
                    if (!JsonValue.TryParse(string.IsNullOrWhiteSpace(text) ? "{}" : text, out root, out error) || !root.IsObject)
                        return "Existing config is not valid JSON; not modified. " + error;

                    if (client.Kind == McpClientKind.OpenCode) UpsertOpenCode(root, spec);
                    else if (client.Kind == McpClientKind.JsonServers) UpsertCollection(root, "servers", spec.Name, Entry(spec, true));
                    else UpsertCollection(root, "mcpServers", spec.Name, Entry(spec, client.Id == "claudecode"));

                    File.WriteAllText(client.Path, root.ToJson(true) + "\n", new UTF8Encoding(false));
                }
                BridgePaths.TrySetUnixMode(client.Path, 384);
                BridgeLog.Success("mcp", "Installed " + spec.Name + " into " + client.Name + ".");
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static string Uninstall(McpClient client, string name)
        {
            if (client == null || !client.CanInstall) return "Select a supported client.";
            if (!File.Exists(client.Path)) return null;
            try
            {
                Backup(client.Path);
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.ReadAllText(client.Path);
                    File.WriteAllText(client.Path, RemoveToml(text, name), new UTF8Encoding(false));
                }
                else
                {
                    JsonValue root;
                    string error;
                    if (!JsonValue.TryParse(File.ReadAllText(client.Path), out root, out error) || !root.IsObject)
                        return "Config is not valid JSON; not modified.";
                    if (client.Kind == McpClientKind.OpenCode)
                    {
                        var mcp = root["mcp"];
                        if (mcp["servers"].IsObject) mcp["servers"].ObjectValue.Remove(name);
                        else if (mcp.IsObject) mcp.ObjectValue.Remove(name);
                    }
                    else
                    {
                        string key = client.Kind == McpClientKind.JsonServers ? "servers" : "mcpServers";
                        if (root[key].IsObject) root[key].ObjectValue.Remove(name);
                    }
                    File.WriteAllText(client.Path, root.ToJson(true) + "\n", new UTF8Encoding(false));
                }
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        static JsonValue Entry(McpServerSpec spec, bool includeType)
        {
            if (spec.IsStdio) return JsonValue.Obj("command", spec.Command, "args", JsonValue.Arr(spec.Args));
            var entry = JsonValue.Obj("url", spec.Url);
            if (spec.Headers.Count > 0)
            {
                var headers = JsonValue.NewObject();
                foreach (var kv in spec.Headers) headers[kv.Key] = kv.Value;
                entry["headers"] = headers;
            }
            if (includeType) entry["type"] = "http";
            return entry;
        }

        static void UpsertCollection(JsonValue root, string collection, string name, JsonValue entry)
        {
            var target = root[collection];
            if (!target.IsObject)
            {
                target = JsonValue.NewObject();
                root[collection] = target;
            }
            target[name] = entry;
        }

        static void UpsertOpenCode(JsonValue root, McpServerSpec spec)
        {
            var mcp = root["mcp"];
            if (!mcp.IsObject)
            {
                mcp = JsonValue.NewObject();
                root["mcp"] = mcp;
            }
            JsonValue entry;
            if (spec.IsStdio)
            {
                var command = JsonValue.NewArray();
                command.Add(spec.Command);
                foreach (var arg in spec.Args) command.Add(arg);
                entry = JsonValue.Obj("type", "local", "command", command, "enabled", true);
            }
            else
            {
                entry = JsonValue.Obj("type", "remote", "url", spec.Url, "enabled", true);
                if (spec.Headers.Count > 0)
                {
                    var headers = JsonValue.NewObject();
                    foreach (var kv in spec.Headers) headers[kv.Key] = kv.Value;
                    entry["headers"] = headers;
                    entry["oauth"] = false;
                }
            }
            if (mcp["servers"].IsObject) mcp["servers"][spec.Name] = entry;
            else mcp[spec.Name] = entry;
        }

        static string ReplaceToml(string text, McpServerSpec spec)
        {
            string cleaned = RemoveToml(text, spec.Name);
            if (!string.IsNullOrEmpty(cleaned) && !cleaned.EndsWith("\n", StringComparison.Ordinal)) cleaned += "\n";
            if (!string.IsNullOrEmpty(cleaned)) cleaned += "\n";
            cleaned += "[" + Section(spec.Name) + "]\n";
            if (spec.IsStdio)
            {
                cleaned += "command = \"" + Escape(spec.Command) + "\"\n";
                var args = new List<string>();
                foreach (var arg in spec.Args) args.Add("\"" + Escape(arg) + "\"");
                cleaned += "args = [" + string.Join(", ", args.ToArray()) + "]\n";
            }
            else
            {
                cleaned += "url = \"" + Escape(spec.Url) + "\"\n";
                cleaned += "enabled = true\n";
                if (spec.Headers.Count > 0)
                {
                    var pairs = new List<string>();
                    foreach (var kv in spec.Headers) pairs.Add(kv.Key + " = \"" + Escape(kv.Value) + "\"");
                    cleaned += "http_headers = { " + string.Join(", ", pairs.ToArray()) + " }\n";
                }
            }
            return cleaned;
        }

        static string RemoveToml(string text, string name)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string target = "[" + Section(name) + "]";
            var output = new List<string>();
            bool skipping = false;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    skipping = string.Equals(trimmed, target, StringComparison.Ordinal);
                    if (skipping) continue;
                }
                if (!skipping) output.Add(line);
            }
            return string.Join("\n", output.ToArray()).TrimEnd('\n') + (output.Count > 0 ? "\n" : string.Empty);
        }

        static string Section(string name) { return "mcp_servers." + name; }
        static string Escape(string value) { return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\""); }

        static void Backup(string path)
        {
            if (!File.Exists(path)) return;
            string root = Path.Combine(BridgePaths.ConfigRoot, "mcp-backups");
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Path.GetFileName(path) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".bak");
            File.Copy(path, target, true);
            BridgePaths.TrySetUnixMode(target, 384);
        }
    }
}
