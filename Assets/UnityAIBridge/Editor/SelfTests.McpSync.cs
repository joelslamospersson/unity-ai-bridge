using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static void RunMcpSyncTests()
        {
            string folder = Path.Combine(BridgePaths.TempDir, "mcp-sync");
            Directory.CreateDirectory(folder);
            var codex = new McpClient { Id = "test-codex", Name = "Test Codex", Kind = McpClientKind.CodexToml, Path = Path.Combine(folder, "config.toml") };
            var opencode = new McpClient { Id = "test-opencode", Name = "Test OpenCode", Kind = McpClientKind.OpenCode, Path = Path.Combine(folder, "opencode.json") };
            var cursor = new McpClient { Id = "test-cursor", Name = "Test JSON", Kind = McpClientKind.JsonMcpServers, Path = Path.Combine(folder, "mcp.json") };
            var targets = new[] { codex, opencode, cursor };
            string savedContext = BridgeSettings.Secrets.Context7ApiKey;
            string savedGitHub = BridgeSettings.Secrets.GitHubMcpToken;
            bool savedOAuth = BridgeSettings.Current.Context7OAuth;
            var savedTargets = BridgeSettings.Current.McpTargetClients;
            try
            {
                File.WriteAllText(codex.Path, "model = \"keep-this\"\n[mcp_servers.unrelated]\nurl = \"https://keep.invalid/mcp\"\n");
                File.WriteAllText(opencode.Path, "{\"theme\":\"keep\",\"mcp\":{\"servers\":{\"unrelated\":{\"type\":\"remote\",\"url\":\"https://keep.invalid/mcp\"}}}}");
                File.WriteAllText(cursor.Path, "{\"other\":42,\"mcpServers\":{\"unrelated\":{\"url\":\"https://keep.invalid/mcp\"}}}");
                var context = McpCatalog.Servers().Find(s => s.Name == "context7");
                var github = McpCatalog.Servers().Find(s => s.Name == "github");
                var apply = McpConfigSync.SaveAndApply(context, "  ctx7-test-key  ", false, false, targets);
                var persisted = JsonUtility.FromJson<BridgeSecrets>(File.ReadAllText(BridgePaths.SecretsFile));
                Check("mcp_key_save_applies_to_all_selected_clients", apply.Ok && apply.Applied == 3 && persisted.Context7ApiKey == "ctx7-test-key" &&
                    McpConfigSync.Read(codex).Find(s => s.Name == "context7").Headers["Authorization"] == "Bearer ctx7-test-key" &&
                    JsonValue.Parse(File.ReadAllText(opencode.Path))["mcp"]["servers"]["context7"]["headers"]["Authorization"].AsString() == "Bearer ctx7-test-key" &&
                    JsonValue.Parse(File.ReadAllText(cursor.Path))["mcpServers"]["context7"]["headers"]["Authorization"].AsString() == "Bearer ctx7-test-key");

                var update = McpConfigSync.SaveAndApply(context, "ctx7-replaced", false, false, targets);
                Check("mcp_saved_key_replacement_updates_existing_installs", update.Ok && update.Applied == 3 && !File.ReadAllText(codex.Path).Contains("ctx7-test-key") &&
                    !File.ReadAllText(opencode.Path).Contains("ctx7-test-key") && File.ReadAllText(cursor.Path).Contains("ctx7-replaced"));
                Check("mcp_bulk_install_preserves_unrelated_settings_and_servers", File.ReadAllText(codex.Path).Contains("model = \"keep-this\"") &&
                    McpConfigSync.Read(codex).Exists(s => s.Name == "unrelated") && JsonValue.Parse(File.ReadAllText(opencode.Path))["theme"].AsString() == "keep" &&
                    JsonValue.Parse(File.ReadAllText(cursor.Path))["other"].AsInt() == 42);

                var clear = McpConfigSync.SaveAndApply(context, "", true, false, targets);
                Check("mcp_clear_key_removes_credentials_from_selected_configs", clear.Ok && BridgeSettings.Secrets.Context7ApiKey == "" &&
                    McpConfigSync.Read(codex).Find(s => s.Name == "context7").Headers.Count == 0 && !File.ReadAllText(opencode.Path).Contains("ctx7-replaced") && !File.ReadAllText(cursor.Path).Contains("ctx7-replaced"));

                var oauth = McpConfigSync.SaveAndApply(context, "", false, true, targets);
                var remote = JsonValue.Parse(File.ReadAllText(opencode.Path))["mcp"]["servers"]["context7"];
                Check("mcp_oauth_choice_is_saved_and_applied_to_all_targets", oauth.Ok && BridgeSettings.Current.Context7OAuth && remote["url"].AsString().EndsWith("/oauth") &&
                    remote["oauth"].IsNull && McpConfigSync.Read(codex).Find(s => s.Name == "context7").Url.EndsWith("/oauth"));
                var pat = McpConfigSync.SaveAndApply(github, "fake-pat", false, false, targets);
                Check("mcp_github_pat_save_applies_and_disables_opencode_oauth", pat.Ok && BridgeSettings.Secrets.GitHubMcpToken == "fake-pat" &&
                    JsonValue.Parse(File.ReadAllText(opencode.Path))["mcp"]["servers"]["github"]["oauth"].IsBool && !JsonValue.Parse(File.ReadAllText(opencode.Path))["mcp"]["servers"]["github"]["oauth"].AsBool());

                File.WriteAllText(codex.Path, "model = \"keep\"\n[[skills.config]]\npath = \"ignore\"\n" +
                    "[mcp_servers.context7] # inline comment\nurl = 'https://mcp.context7.com/mcp'\n[mcp_servers.context7.http_headers]\nContext7-API-Key = 'source#key=value'\n" +
                    "[mcp_servers.\"local.with.dots\"]\ncommand = 'npx'\nargs = [\n'-y',\n'@example/mcp',\n'--flag=a#b',\n]\nenabled = false\ncwd = '/tmp/mcp-workdir'\nstartup_timeout_sec = 20\n" +
                    "[mcp_servers.\"local.with.dots\".env]\nTEST_TOKEN = 'local-key'\n" +
                    "[mcp_servers.github]\nurl = 'https://api.githubcopilot.com/mcp/'\nbearer_token_env_var = 'TEST_GITHUB_TOKEN'\n" +
                    "[mcp_servers.github.env_http_headers]\nX-Client-Key = 'TEST_CLIENT_KEY'\n");
                string sourceBefore = File.ReadAllText(codex.Path);
                var sync = McpConfigSync.Sync(codex, new[] { codex, opencode });
                var synced = JsonValue.Parse(File.ReadAllText(opencode.Path))["mcp"]["servers"];
                Check("mcp_sync_codex_to_opencode_copies_keys_commands_env_and_enabled", sync.Ok && sync.Applied == 3 &&
                    synced["context7"]["headers"]["Context7-API-Key"].AsString() == "source#key=value" && synced["local.with.dots"]["command"][3].AsString() == "--flag=a#b" &&
                    synced["local.with.dots"]["environment"]["TEST_TOKEN"].AsString() == "local-key" && !synced["local.with.dots"]["enabled"].AsBool(true));
                Check("mcp_sync_translates_working_directory_and_startup_timeout", synced["local.with.dots"]["cwd"].AsString() == "/tmp/mcp-workdir" && synced["local.with.dots"]["timeout"].AsInt() == 20000 && sync.Notes.Count == 1);
                Check("mcp_sync_preserves_environment_based_header_credentials", synced["github"]["headers"]["Authorization"].AsString() == "Bearer {env:TEST_GITHUB_TOKEN}" &&
                    synced["github"]["headers"]["X-Client-Key"].AsString() == "{env:TEST_CLIENT_KEY}");
                Check("mcp_sync_keeps_source_and_unrelated_destination_entries", sourceBefore == File.ReadAllText(codex.Path) && synced["unrelated"].IsObject);
                Check("mcp_sync_imports_catalog_credentials_for_future_shared_installs", BridgeSettings.Secrets.Context7ApiKey == "source#key=value" &&
                    BridgeSettings.Secrets.GitHubMcpToken == "{env:TEST_GITHUB_TOKEN}" && !BridgeSettings.Current.Context7OAuth);

                // Reverse sync replaces the entire old server table, including sub-tables.
                var reverse = McpConfigSync.Sync(opencode, new[] { codex });
                var roundtrip = McpConfigSync.Read(codex);
                var roundtripLocal = roundtrip.Find(s => s.Name == "local.with.dots");
                var roundtripGitHub = roundtrip.Find(s => s.Name == "github");
                Check("mcp_sync_opencode_to_codex_roundtrips_without_stale_nested_tables", reverse.Ok && roundtripLocal != null && !roundtripLocal.Enabled && roundtripLocal.Environment["TEST_TOKEN"] == "local-key" &&
                    roundtripGitHub.BearerTokenEnvVar == "TEST_GITHUB_TOKEN" && roundtripGitHub.EnvHeaders["X-Client-Key"] == "TEST_CLIENT_KEY" && roundtrip.Find(s => s.Name == "context7").Headers["Context7-API-Key"] == "source#key=value");

                string destinationBefore = File.ReadAllText(codex.Path);
                File.WriteAllText(opencode.Path, "{invalid-json");
                var invalid = McpConfigSync.Sync(opencode, new[] { codex });
                Check("mcp_sync_rejects_malformed_source_without_destination_writes", !invalid.Ok && invalid.Applied == 0 && File.ReadAllText(codex.Path) == destinationBefore);
                File.WriteAllText(opencode.Path, "{\"mcp\":{\"broken\":{\"type\":\"remote\",\"url\":\"https://example.invalid\",\"unsupported_client_option\":true}}}");
                var unsupported = McpConfigSync.Sync(opencode, new[] { codex });
                Check("mcp_sync_reports_untranslatable_settings_without_silent_loss", !unsupported.Ok && unsupported.Applied == 0 && File.ReadAllText(codex.Path) == destinationBefore);

                File.WriteAllText(opencode.Path, "{do-not-overwrite");
                var partial = McpConfigSync.SaveAndApply(context, "partial-key", false, false, targets);
                Check("mcp_bulk_apply_reports_partial_failures_and_keeps_invalid_configs", !partial.Ok && partial.Applied == 2 && partial.Errors.Count == 1 && File.ReadAllText(opencode.Path) == "{do-not-overwrite" &&
                    McpConfigSync.Read(codex).Find(s => s.Name == "context7").Headers["Authorization"] == "Bearer partial-key");

                BridgeSettings.Current.McpTargetClients = new List<string> { "codex", "opencode" };
                BridgeSettings.Save();
                var persistedSettings = JsonUtility.FromJson<BridgeSettings>(File.ReadAllText(BridgePaths.SettingsFile));
                var selected = McpConfigSync.SelectedClients();
                Check("mcp_shared_target_selection_is_persisted_and_filters_unselected_clients", selected.Count == 2 && selected.Exists(c => c.Id == "codex") && selected.Exists(c => c.Id == "opencode") && persistedSettings.McpTargetClients.Count == 2);

                File.WriteAllText(opencode.Path, "{ // keep settings\n\"theme\":\"saved\", /* block comment */ \"mcp\": { \"custom\": { \"type\":\"remote\", \"url\":\"https://example.invalid/a//b\", \"headers\": { \"X-Key\":\"literal/*key*/,}\", }, }, }, }");
                var jsoncRead = McpConfigSync.Read(opencode);
                var jsoncApply = McpConfigSync.SaveAndApply(github, "jsonc-fake-pat", false, false, new[] { opencode });
                var jsoncAfter = JsonValue.Parse(File.ReadAllText(opencode.Path));
                Check("mcp_jsonc_read_and_apply_preserve_literal_credentials_and_settings", jsoncApply.Ok && jsoncRead[0].Headers["X-Key"] == "literal/*key*/,}" &&
                    jsoncAfter["theme"].AsString() == "saved" && jsoncAfter["mcp"]["custom"]["url"].AsString() == "https://example.invalid/a//b");

                // Current Context7 bearer authentication also imports into the shared key store.
                var contextBearer = McpConfigSync.SaveAndApply(context, "bearer-import-key", false, false, new[] { opencode });
                var bearerSync = McpConfigSync.Sync(opencode, new[] { codex });
                Check("mcp_context7_bearer_key_imports_for_future_applies", contextBearer.Ok && bearerSync.Ok && BridgeSettings.Secrets.Context7ApiKey == "bearer-import-key" &&
                    McpConfigSync.Read(codex).Find(s => s.Name == "context7").Headers["Authorization"] == "Bearer bearer-import-key");
            }
            catch (Exception e) { Check("mcp_sync_runner", false, e.ToString()); }
            finally
            {
                BridgeSettings.Secrets.Context7ApiKey = savedContext;
                BridgeSettings.Secrets.GitHubMcpToken = savedGitHub;
                BridgeSettings.Current.Context7OAuth = savedOAuth;
                BridgeSettings.Current.McpTargetClients = savedTargets;
                BridgeSettings.SaveSecrets(); BridgeSettings.Save();
            }
        }
    }
}
