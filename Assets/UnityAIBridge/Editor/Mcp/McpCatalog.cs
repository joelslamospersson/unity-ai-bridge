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
        public bool Enabled = true;
        public Dictionary<string, string> Environment = new Dictionary<string, string>();
        public Dictionary<string, string> EnvHeaders = new Dictionary<string, string>();
        public string BearerTokenEnvVar;
        public JsonValue OAuthConfig;
        public int TimeoutMs;
        public int StartupTimeoutMs;
        public string WorkingDirectory;
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
                    spec.Headers["Authorization"] = "Bearer " + context7Key;
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
                    return McpToml.ReadServers(File.ReadAllText(client.Path))[name].IsObject;
                JsonValue root;
                string error;
                if (!McpConfigSync.TryReadJson(client, File.ReadAllText(client.Path), out root, out error) || !root.IsObject)
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
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : string.Empty;
                    string updated = ReplaceToml(text, spec);
                    McpInstaller.Backup(client.Path);
                    McpInstaller.WritePrivate(client.Path, updated);
                }
                else
                {
                    string text = File.Exists(client.Path) ? File.ReadAllText(client.Path, Encoding.UTF8) : "{}";
                    JsonValue root;
                    string error;
                    if (!McpConfigSync.TryReadJson(client, string.IsNullOrWhiteSpace(text) ? "{}" : text, out root, out error) || !root.IsObject)
                        return "Existing config is not valid JSON; not modified. " + error;

                    var entry = McpConfigSync.JsonEntry(client, spec);
                    if (client.Kind == McpClientKind.OpenCode)
                    {
                        if (!root["mcp"].IsObject) root["mcp"] = JsonValue.NewObject();
                        var mcp = root["mcp"];
                        if (mcp["servers"].IsObject) mcp["servers"][spec.Name] = entry;
                        else mcp[spec.Name] = entry;
                    }
                    else UpsertCollection(root, client.Kind == McpClientKind.JsonServers ? "servers" : "mcpServers", spec.Name, entry);

                    McpInstaller.Backup(client.Path);
                    McpInstaller.WritePrivate(client.Path, root.ToJson(true) + "\n");
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
                McpInstaller.Backup(client.Path);
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string text = File.ReadAllText(client.Path);
                    McpInstaller.WritePrivate(client.Path, RemoveToml(text, name));
                }
                else
                {
                    JsonValue root;
                    string error;
                    if (!McpConfigSync.TryReadJson(client, File.ReadAllText(client.Path), out root, out error) || !root.IsObject)
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
                    McpInstaller.WritePrivate(client.Path, root.ToJson(true) + "\n");
                }
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
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

        static string ReplaceToml(string text, McpServerSpec spec)
        {
            // Validate the existing MCP tables before changing any of them.
            McpToml.ReadServers(text);
            if (spec.OAuthConfig != null && spec.OAuthConfig.IsObject && spec.OAuthConfig.Count > 0)
                throw new BridgeException("Client-specific OAuth options require setup in Codex; this server was not changed.");
            var client = new McpClient { Kind = McpClientKind.CodexToml };
            var output = new StringBuilder(RemoveToml(text, spec.Name).TrimEnd());
            if (output.Length > 0) output.Append("\n\n");
            output.AppendLine("[mcp_servers." + (System.Text.RegularExpressions.Regex.IsMatch(spec.Name, @"^[A-Za-z0-9_-]+$") ? spec.Name : Quoted(spec.Name)) + "]");
            output.AppendLine("enabled = " + (spec.Enabled ? "true" : "false"));
            if (spec.IsStdio)
            {
                output.AppendLine("command = " + Quoted(McpConfigSync.Translate(spec.Command, client)));
                var args = new List<string>();
                foreach (var arg in spec.Args) args.Add(Quoted(McpConfigSync.Translate(arg, client)));
                output.AppendLine("args = [" + string.Join(", ", args.ToArray()) + "]");
                AppendMap(output, "env", spec.Environment, client);
            }
            else
            {
                output.AppendLine("url = " + Quoted(McpConfigSync.Translate(spec.Url, client)));
                var staticHeaders = new Dictionary<string, string>();
                var envHeaders = new Dictionary<string, string>(spec.EnvHeaders);
                string bearer = spec.BearerTokenEnvVar;
                foreach (var pair in spec.Headers)
                {
                    var reference = System.Text.RegularExpressions.Regex.Match(pair.Value, @"^(Bearer )?(?:\{env:([A-Za-z_][A-Za-z0-9_]*)\}|\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)\})$");
                    if (reference.Success)
                    {
                        string variable = reference.Groups[2].Success ? reference.Groups[2].Value : reference.Groups[3].Value;
                        if (reference.Groups[1].Success && string.Equals(pair.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) bearer = variable;
                        else if (!reference.Groups[1].Success) envHeaders[pair.Key] = variable;
                        else staticHeaders[pair.Key] = McpConfigSync.Translate(pair.Value, client);
                    }
                    else staticHeaders[pair.Key] = McpConfigSync.Translate(pair.Value, client);
                }
                AppendMap(output, "http_headers", staticHeaders, client);
                AppendMap(output, "env_http_headers", envHeaders, null);
                if (!string.IsNullOrEmpty(bearer)) output.AppendLine("bearer_token_env_var = " + Quoted(bearer));
            }
            if (spec.TimeoutMs > 0) output.AppendLine("tool_timeout_sec = " + (spec.TimeoutMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (spec.StartupTimeoutMs > 0) output.AppendLine("startup_timeout_sec = " + (spec.StartupTimeoutMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(spec.WorkingDirectory)) output.AppendLine("cwd = " + Quoted(McpConfigSync.Translate(spec.WorkingDirectory, client)));
            return output.ToString();
        }

        static string Quoted(string value) { return JsonValue.Of(value).ToJson(false); }

        static void AppendMap(StringBuilder output, string field, Dictionary<string, string> values, McpClient client)
        {
            if (values.Count == 0) return;
            var pairs = new List<string>();
            foreach (var pair in values) pairs.Add(Quoted(pair.Key) + " = " + Quoted(client == null ? pair.Value : McpConfigSync.Translate(pair.Value, client)));
            output.AppendLine(field + " = { " + string.Join(", ", pairs.ToArray()) + " }");
        }

        static string RemoveToml(string text, string name)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var output = new List<string>();
            bool skipping = false;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    skipping = McpToml.IsServerTable(line, name);
                    if (skipping) continue;
                }
                if (!skipping) output.Add(line);
            }
            return string.Join("\n", output.ToArray()).TrimEnd('\n') + (output.Count > 0 ? "\n" : string.Empty);
        }

        static string Section(string name) { return "mcp_servers." + name; }
    }
}
