using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityAIBridge
{
    public sealed class McpBatchResult
    {
        public int Applied;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public bool Ok { get { return Errors.Count == 0; } }
        public string Summary
        {
            get { return "Applied " + Applied + " server configuration(s)." + (Ok ? " Restart the affected clients to connect." : "\n" + string.Join("\n", Errors.ToArray())) + (Notes.Count == 0 ? "" : "\n" + string.Join("\n", Notes.ToArray())); }
        }
    }

    // Translates portable MCP settings; it never copies a client's OAuth sessions.
    public static class McpConfigSync
    {
        static readonly Regex EnvReference = new Regex(@"\{env:([A-Za-z_][A-Za-z0-9_]*)\}|\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)\}");

        public static List<McpClient> SelectedClients()
        {
            var ids = BridgeSettings.Current.McpTargetClients;
            return McpInstaller.Clients().FindAll(c => c.CanInstall && ids != null && ids.Contains(c.Id));
        }

        public static McpBatchResult Apply(IEnumerable<McpClient> targets, McpServerSpec spec)
        {
            var result = new McpBatchResult();
            foreach (var target in targets)
            {
                string error = McpCatalog.Install(target, spec);
                if (error == null) result.Applied++;
                else result.Errors.Add(target.Name + ": " + error);
            }
            if (result.Applied == 0 && result.Ok) result.Errors.Add("Select at least one client.");
            return result;
        }

        public static McpBatchResult SaveAndApply(McpCatalogServer server, string draft, bool clearKey, bool useOAuth, IEnumerable<McpClient> targets)
        {
            var secrets = BridgeSettings.Secrets;
            if (server.Name == "context7")
            {
                if (clearKey) secrets.Context7ApiKey = "";
                else if (!string.IsNullOrWhiteSpace(draft)) secrets.Context7ApiKey = draft.Trim();
                BridgeSettings.Current.Context7OAuth = useOAuth;
                BridgeSettings.Save();
            }
            else if (server.Name == "github")
            {
                if (clearKey) secrets.GitHubMcpToken = "";
                else if (!string.IsNullOrWhiteSpace(draft)) secrets.GitHubMcpToken = draft.Trim();
            }
            BridgeSettings.SaveSecrets();
            return Apply(targets, McpCatalog.BuildSpec(server, BridgeSettings.Current.Context7OAuth, secrets.Context7ApiKey, secrets.GitHubMcpToken));
        }

        public static McpBatchResult Sync(McpClient source, IEnumerable<McpClient> targets)
        {
            var result = new McpBatchResult();
            List<McpServerSpec> servers;
            try { servers = Read(source); }
            catch (Exception e) { result.Errors.Add("Source could not be read. " + e.Message); return result; }
            if (servers.Count == 0) { result.Errors.Add("The source contains no MCP server configurations."); return result; }
            foreach (var target in targets)
            {
                if (string.Equals(Path.GetFullPath(target.Path), Path.GetFullPath(source.Path), StringComparison.Ordinal)) continue;
                foreach (var spec in servers)
                {
                    string error = McpCatalog.Install(target, spec);
                    if (error == null) result.Applied++;
                    else result.Errors.Add(target.Name + " / " + spec.Name + ": " + error);
                    if (error == null && target.Kind == McpClientKind.OpenCode && spec.StartupTimeoutMs > 0)
                        result.Notes.Add(target.Name + " / " + spec.Name + ": OpenCode uses one timeout; " + (spec.TimeoutMs > 0 ? spec.TimeoutMs : spec.StartupTimeoutMs) + " ms was applied.");
                }
            }
            if (result.Applied > 0) ImportCatalogCredentials(servers);
            if (result.Applied == 0 && result.Ok) result.Errors.Add("Select a destination other than the source.");
            return result;
        }

        static void ImportCatalogCredentials(List<McpServerSpec> servers)
        {
            bool changed = false;
            foreach (var spec in servers)
            {
                if (string.Equals(spec.Name, "context7", StringComparison.OrdinalIgnoreCase))
                {
                    string key = "";
                    foreach (var header in spec.Headers)
                    {
                        if (string.Equals(header.Key, "Context7-API-Key", StringComparison.OrdinalIgnoreCase) || string.Equals(header.Key, "CONTEXT7_API_KEY", StringComparison.OrdinalIgnoreCase)) key = header.Value;
                        if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) && header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = header.Value.Substring(7);
                    }
                    foreach (var header in spec.EnvHeaders)
                        if (string.Equals(header.Key, "Context7-API-Key", StringComparison.OrdinalIgnoreCase) || string.Equals(header.Key, "CONTEXT7_API_KEY", StringComparison.OrdinalIgnoreCase)) key = "{env:" + header.Value + "}";
                    if (!string.IsNullOrEmpty(spec.BearerTokenEnvVar)) key = "{env:" + spec.BearerTokenEnvVar + "}";
                    BridgeSettings.Secrets.Context7ApiKey = key;
                    BridgeSettings.Current.Context7OAuth = spec.Url != null && spec.Url.TrimEnd('/').EndsWith("/oauth", StringComparison.Ordinal);
                    changed = true;
                }
                else if (string.Equals(spec.Name, "github", StringComparison.OrdinalIgnoreCase))
                {
                    string token = "";
                    foreach (var header in spec.Headers)
                        if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) && header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header.Value.Substring(7);
                    if (!string.IsNullOrEmpty(spec.BearerTokenEnvVar)) token = "{env:" + spec.BearerTokenEnvVar + "}";
                    BridgeSettings.Secrets.GitHubMcpToken = token;
                    changed = true;
                }
            }
            if (changed) { BridgeSettings.SaveSecrets(); BridgeSettings.Save(); }
        }

        public static List<McpServerSpec> Read(McpClient client)
        {
            if (client == null || !client.CanInstall || !File.Exists(client.Path)) throw new BridgeException("Choose an existing supported client config.");
            string text = File.ReadAllText(client.Path, Encoding.UTF8);
            JsonValue collection;
            if (client.Kind == McpClientKind.CodexToml) collection = McpToml.ReadServers(text);
            else
            {
                JsonValue root;
                string error;
                if (!TryReadJson(client, text, out root, out error) || !root.IsObject) throw new BridgeException("The source is not valid JSON/JSONC.");
                collection = client.Kind == McpClientKind.OpenCode ? root["mcp"] : root[client.Kind == McpClientKind.JsonServers ? "servers" : "mcpServers"];
                if (client.Kind == McpClientKind.OpenCode && collection["servers"].IsObject) collection = collection["servers"];
            }
            var servers = new List<McpServerSpec>();
            if (!collection.IsObject) return servers;
            foreach (var pair in collection.Properties)
            {
                if (!pair.Value.IsObject) throw new BridgeException("Unsupported MCP entry: " + pair.Key + ". No configurations were synced.");
                servers.Add(ReadSpec(pair.Key, pair.Value, client.Kind));
            }
            return servers;
        }

        internal static bool TryReadJson(McpClient client, string text, out JsonValue root, out string error)
        {
            if (JsonValue.TryParse(text, out root, out error)) return true;
            if (client.Kind != McpClientKind.OpenCode && client.Kind != McpClientKind.JsonServers) return false;
            // JSONC is accepted by OpenCode and VS Code. Strip only outside
            // strings, so URLs, escaped quotes and credential text stay intact.
            var cleaned = new StringBuilder();
            bool quoted = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    cleaned.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; cleaned.Append(c); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i + 1 < text.Length && text[i + 1] != '\n') i++;
                    cleaned.Append(' '); continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) { error = "Unterminated JSONC comment."; return false; }
                    i = end + 1; cleaned.Append(' '); continue;
                }
                cleaned.Append(c);
            }
            text = cleaned.ToString(); cleaned.Length = 0; quoted = false; escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    cleaned.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                if (c == ',')
                {
                    int next = i + 1;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next < text.Length && (text[next] == '}' || text[next] == ']')) continue;
                }
                cleaned.Append(c);
            }
            return JsonValue.TryParse(cleaned.ToString(), out root, out error);
        }

        static McpServerSpec ReadSpec(string name, JsonValue entry, McpClientKind kind)
        {
            bool codex = kind == McpClientKind.CodexToml;
            var spec = new McpServerSpec { Name = name, Url = entry["url"].AsString(entry["serverUrl"].AsString()), Enabled = entry["enabled"].AsBool(true) && !entry["disabled"].AsBool() };
            if (entry["command"].IsArray)
            {
                var command = Strings(entry["command"]);
                if (command.Length == 0) throw new BridgeException("Empty command for " + name + ".");
                spec.Command = command[0];
                spec.Args = new List<string>(command).GetRange(1, command.Length - 1).ToArray();
            }
            else { spec.Command = entry["command"].AsString(); spec.Args = Strings(entry["args"]); }
            spec.IsStdio = !string.IsNullOrEmpty(spec.Command);
            if (!spec.IsStdio && string.IsNullOrEmpty(spec.Url)) throw new BridgeException("No URL or command for " + name + ".");
            string type = entry["type"].AsString();
            if (!string.IsNullOrEmpty(type) && type != "http" && type != "remote" && type != "local" && type != "stdio" && type != "streamable-http")
                throw new BridgeException("Unsupported transport for " + name + ". Use its client configuration UI.");
            CopyMap(entry[codex ? "http_headers" : "headers"], spec.Headers);
            CopyMap(entry[kind == McpClientKind.OpenCode ? "environment" : "env"], spec.Environment);
            CopyMap(entry["env_http_headers"], spec.EnvHeaders);
            spec.BearerTokenEnvVar = entry["bearer_token_env_var"].AsString();
            spec.OAuth = !spec.IsStdio && spec.Headers.Count == 0 && spec.EnvHeaders.Count == 0 && string.IsNullOrEmpty(spec.BearerTokenEnvVar);
            if (!entry["oauth"].IsNull)
            {
                spec.OAuth = !entry["oauth"].IsBool || entry["oauth"].AsBool();
                spec.OAuthConfig = JsonValue.Parse(entry["oauth"].ToJson(false));
            }
            spec.TimeoutMs = codex ? (int)(entry["tool_timeout_sec"].AsNumber() * 1000) : entry["timeout"].AsInt();
            spec.StartupTimeoutMs = (int)(entry["startup_timeout_sec"].AsNumber() * 1000);
            spec.WorkingDirectory = entry["cwd"].AsString();
            var known = new HashSet<string> { "url", "serverUrl", "command", "args", "enabled", "disabled", "type", "http_headers", "headers", "environment", "env", "env_http_headers", "bearer_token_env_var", "oauth", "tool_timeout_sec", "timeout", "startup_timeout_sec", "cwd" };
            foreach (var field in entry.Properties)
                if (!known.Contains(field.Key)) throw new BridgeException("Cannot translate setting '" + field.Key + "' for " + name + ". No configurations were synced.");
            return spec;
        }

        static string[] Strings(JsonValue array)
        {
            var values = new List<string>();
            if (array.IsNull) return values.ToArray();
            if (!array.IsArray) throw new BridgeException("Expected an array of command arguments.");
            foreach (var value in array.ArrayValue)
            {
                if (!value.IsString) throw new BridgeException("Command arguments must be strings.");
                values.Add(value.AsString());
            }
            return values.ToArray();
        }

        static void CopyMap(JsonValue map, Dictionary<string, string> output)
        {
            if (map.IsNull) return;
            if (!map.IsObject) throw new BridgeException("Expected a header or environment map.");
            foreach (var pair in map.Properties)
            {
                if (!pair.Value.IsString) throw new BridgeException("Header and environment values must be strings.");
                output[pair.Key] = pair.Value.AsString();
            }
        }

        internal static string Translate(string value, McpClient client)
        {
            return EnvReference.Replace(value ?? "", match =>
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (client.Kind == McpClientKind.OpenCode) return "{env:" + key + "}";
                if (client.Kind == McpClientKind.CodexToml)
                {
                    string resolved = Environment.GetEnvironmentVariable(key);
                    if (resolved == null) throw new BridgeException("Environment variable " + key + " is unavailable to Unity; this server was not changed.");
                    return resolved;
                }
                return client.Id == "claudecode" ? "${" + key + "}" : "${env:" + key + "}";
            });
        }

        internal static JsonValue JsonEntry(McpClient client, McpServerSpec spec)
        {
            if (!spec.Enabled && client.Kind != McpClientKind.OpenCode) throw new BridgeException("This client's disabled-server setting cannot be translated; the server was not changed.");
            var entry = JsonValue.NewObject();
            if (spec.IsStdio)
            {
                if (client.Kind == McpClientKind.OpenCode)
                {
                    var command = JsonValue.Arr(Translate(spec.Command, client));
                    foreach (var arg in spec.Args) command.Add(Translate(arg, client));
                    entry["type"] = "local"; entry["command"] = command;
                }
                else
                {
                    entry["command"] = Translate(spec.Command, client);
                    var args = JsonValue.NewArray();
                    foreach (var arg in spec.Args) args.Add(Translate(arg, client));
                    entry["args"] = args;
                    if (client.Kind == McpClientKind.JsonServers || client.Id == "claudecode") entry["type"] = "stdio";
                }
                if (spec.Environment.Count > 0)
                {
                    var env = JsonValue.NewObject();
                    foreach (var pair in spec.Environment) env[pair.Key] = Translate(pair.Value, client);
                    entry[client.Kind == McpClientKind.OpenCode ? "environment" : "env"] = env;
                }
            }
            else
            {
                entry["url"] = Translate(spec.Url, client);
                if (client.Kind == McpClientKind.OpenCode) entry["type"] = "remote";
                else if (client.Kind == McpClientKind.JsonServers || client.Id == "claudecode") entry["type"] = "http";
                var headers = JsonValue.NewObject();
                foreach (var pair in spec.Headers) headers[pair.Key] = Translate(pair.Value, client);
                foreach (var pair in spec.EnvHeaders) headers[pair.Key] = Translate("{env:" + pair.Value + "}", client);
                if (!string.IsNullOrEmpty(spec.BearerTokenEnvVar)) headers["Authorization"] = Translate("Bearer {env:" + spec.BearerTokenEnvVar + "}", client);
                if (headers.Count > 0) entry["headers"] = headers;
                if (client.Kind == McpClientKind.OpenCode)
                {
                    if (spec.OAuthConfig != null && !spec.OAuthConfig.IsBool) entry["oauth"] = spec.OAuthConfig;
                    else if (!spec.OAuth || headers.Count > 0) entry["oauth"] = false;
                }
                else if (spec.OAuthConfig != null && spec.OAuthConfig.IsObject && spec.OAuthConfig.Count > 0)
                    throw new BridgeException("Client-specific OAuth options require setup in the destination client; this server was not changed.");
            }
            if (client.Kind == McpClientKind.OpenCode) entry["enabled"] = spec.Enabled;
            if (spec.TimeoutMs > 0 || spec.StartupTimeoutMs > 0)
            {
                if (client.Kind != McpClientKind.OpenCode) throw new BridgeException("This client's timeout setting cannot be translated; the server was not changed.");
                entry["timeout"] = spec.TimeoutMs > 0 ? spec.TimeoutMs : spec.StartupTimeoutMs;
            }
            if (!string.IsNullOrEmpty(spec.WorkingDirectory))
            {
                if (client.Kind != McpClientKind.OpenCode && client.Kind != McpClientKind.JsonServers) throw new BridgeException("This client's working directory setting cannot be translated; the server was not changed.");
                entry["cwd"] = Translate(spec.WorkingDirectory, client);
            }
            return entry;
        }
    }

    // A strict reader for MCP TOML tables: strings (basic/literal), arrays,
    // booleans, numbers, inline maps, quoted names and nested env/header tables.
    // Unsupported syntax fails before sync writes any destination configuration.
    internal static class McpToml
    {
        internal static JsonValue ReadServers(string text)
        {
            var servers = JsonValue.NewObject();
            JsonValue table = null;
            var statement = new StringBuilder();
            foreach (var original in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = StripComment(original).Trim();
                if (line.Length == 0) continue;
                if (statement.Length == 0 && line.StartsWith("[", StringComparison.Ordinal))
                {
                    table = null;
                    if (!Regex.IsMatch(line, @"^\[\s*(?:mcp_servers|""mcp_servers""|'mcp_servers')(?=\.|\s*\])")) continue;
                    if (!line.EndsWith("]", StringComparison.Ordinal)) throw new BridgeException("Invalid TOML table header.");
                    var parts = Split(line.Substring(1, line.Length - 2), '.');
                    if (parts.Count < 1 || Key(parts[0]) != "mcp_servers") continue;
                    table = servers;
                    for (int i = 1; i < parts.Count; i++)
                    {
                        string key = Key(parts[i]);
                        if (table[key].IsNull) table[key] = JsonValue.NewObject();
                        if (!table[key].IsObject) throw new BridgeException("Conflicting MCP TOML tables.");
                        table = table[key];
                    }
                    continue;
                }
                if (table == null) continue;
                statement.Append(line).Append('\n');
                if (!Complete(statement.ToString())) continue;
                var assignment = Split(statement.ToString(), '=');
                statement.Length = 0;
                if (assignment.Count != 2) throw new BridgeException("Unsupported TOML assignment in an MCP table.");
                string field = Key(assignment[0]);
                if (!table[field].IsNull) throw new BridgeException("Duplicate MCP TOML setting: " + field + ".");
                table[field] = Value(assignment[1]);
            }
            if (statement.Length > 0) throw new BridgeException("Incomplete MCP TOML value.");
            return servers;
        }

        internal static bool IsServerTable(string line, string name)
        {
            string trimmed = StripComment(line).Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)) return false;
            var parts = Split(trimmed.Substring(1, trimmed.Length - 2), '.');
            return parts.Count >= 2 && Key(parts[0]) == "mcp_servers" && Key(parts[1]) == name;
        }

        static string Key(string text)
        {
            text = text.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal) || text.StartsWith("'", StringComparison.Ordinal)) return Value(text).AsString();
            if (!Regex.IsMatch(text, @"^[A-Za-z0-9_-]+$")) throw new BridgeException("Unsupported TOML key syntax.");
            return text;
        }

        static JsonValue Value(string text)
        {
            text = text.Trim();
            if (text.StartsWith("'", StringComparison.Ordinal) && text.EndsWith("'", StringComparison.Ordinal) && !text.StartsWith("'''", StringComparison.Ordinal))
                return JsonValue.Of(text.Substring(1, text.Length - 2));
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                JsonValue parsed; string error;
                if (!JsonValue.TryParse(text, out parsed, out error) || !parsed.IsString) throw new BridgeException("Unsupported TOML string syntax.");
                return parsed;
            }
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
            {
                var array = JsonValue.NewArray();
                foreach (string part in Split(text.Substring(1, text.Length - 2), ',')) if (!string.IsNullOrWhiteSpace(part)) array.Add(Value(part));
                return array;
            }
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
            {
                var map = JsonValue.NewObject();
                foreach (string part in Split(text.Substring(1, text.Length - 2), ','))
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    var pair = Split(part, '=');
                    if (pair.Count != 2) throw new BridgeException("Invalid TOML inline map.");
                    string key = Key(pair[0]);
                    if (!map[key].IsNull) throw new BridgeException("Duplicate TOML inline key.");
                    map[key] = Value(pair[1]);
                }
                return map;
            }
            JsonValue scalar; string scalarError;
            if (JsonValue.TryParse(text, out scalar, out scalarError) && (scalar.IsBool || scalar.Kind == JsonKind.Number)) return scalar;
            throw new BridgeException("Unsupported value in an MCP TOML table.");
        }

        static string StripComment(string text)
        {
            char quote = '\0'; bool escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (escaped) { escaped = false; continue; }
                    if (quote == '"' && c == '\\') { escaped = true; continue; }
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '#') return text.Substring(0, i);
            }
            return text;
        }

        static bool Complete(string text)
        {
            int depth; char quote;
            Scan(text, '\0', out depth, out quote);
            return depth == 0 && quote == '\0';
        }

        static List<string> Split(string text, char separator)
        {
            int depth; char quote;
            var parts = Scan(text, separator, out depth, out quote);
            if (depth != 0 || quote != '\0') throw new BridgeException("Unbalanced MCP TOML value.");
            return parts;
        }

        static List<string> Scan(string text, char separator, out int depth, out char quote)
        {
            var parts = new List<string>();
            depth = 0; quote = '\0'; bool escaped = false; int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (escaped) { escaped = false; continue; }
                    if (quote == '"' && c == '\\') { escaped = true; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}') depth--;
                if (depth < 0) throw new BridgeException("Unbalanced MCP TOML value.");
                if (c == separator && depth == 0) { parts.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(text.Substring(start));
            return parts;
        }
    }
}
