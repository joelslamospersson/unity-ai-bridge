# Testing and results

## Run the tests

### From the Editor

**Tools → Unity AI Bridge → Run Self Tests**

Results are written to:

- `Library/UnityAIBridge/test-results.json`
- `~/.config/unity-ai-bridge/<project-id>/test-results.json`

### Automatically on the next domain reload

From the Unity project root:

```bash
mkdir -p "Library/UnityAIBridge"
touch "Library/UnityAIBridge/run-tests.flag"
```

Then focus the Editor or cause a recompile. The bootstrap consumes the flag, runs the suite once and removes it.

### Batch mode

Unity locks an open project, so test a copy:

```bash
rsync -a --exclude Library --exclude Temp --exclude Logs --exclude obj \
  --exclude .git --exclude UserSettings "/path/to/your/project/" /tmp/unity-ai-bridge-test/

"$UNITY_EDITOR" \
  -projectPath /tmp/unity-ai-bridge-test \
  -batchmode -nographics -quit \
  -executeMethod UnityAIBridge.SelfTests.RunBatch \
  -logFile /tmp/unity-ai-bridge-test.log

cat /tmp/unity-ai-bridge-test/Library/UnityAIBridge/test-results.json
```

The suite pauses automatic asset refresh, uses `Assets/__UAIBTests__/`, starts local-only mock HTTP servers on `127.0.0.1:7810-7830`, and cleans up afterwards. It restores permissions, auto-apply, provider URLs and credentials touched by tests.

## Results

Unity 6000.6.7f1, Linux Editor, batch mode against a copy:

```
44 passed, 0 failed   (~4.4 s of test time)
```

The UI tests build the chat panel and the full UI Toolkit shell in memory; they do not capture pixels or simulate mouse input.

| # | Test | Verifies |
| --- | --- | --- |
| 1 | `json_roundtrip` | JSON parser/serializer used by HTTP + MCP |
| 2 | `diff_unified` | Unified diff generation |
| 3 | `read_existing_script` | Reading an existing project C# file |
| 4 | `path_traversal_rejected` | `../`, absolute paths, `.git` rejected; valid path allowed |
| 5 | `symlink_escape_rejected` | Symlink inside Assets pointing outside the project rejected |
| 6 | `readonly_rejects_write` | Read Only blocks staging |
| 7 | `file_create` | Create → approve → apply a text file |
| 8 | `file_modify_and_diff` | Diff + modify + backup |
| 9 | `unauthorized_edit_rejected` | Applying an unapproved change is refused |
| 10 | `script_create_modify` | Script file create/modify (no domain reload during tests) |
| 11 | `sprite_import` | PNG → Sprite, PPU, importer settings |
| 12 | `sprite_slicing` | 4×4 grid → 16 sprites |
| 13 | `sprite_move_preserves_guid` | `AssetDatabase.MoveAsset` keeps the `.meta` GUID |
| 14 | `material_create` | Material creation with an available shader |
| 15 | `gameobject_create_and_undo` | Scene object creation + `Undo.PerformUndo` |
| 16 | `component_add_and_update` | Add `SpriteRenderer`, set `m_SortingOrder` |
| 17 | `prefab_create` | Save as prefab, asset exists |
| 18 | `console_errors_readable` | Console/compiler diagnostics service |
| 19 | `invalid_sprite_source_rejected` | Rooted source outside the staging area refused |
| 20 | `mcp_http_roundtrip` | MCP `initialize`, `tools/list` (22 tools), `tools/call`, `401` without token |
| 21 | `provider_connection_error` | Connection refusal surfaced as a readable error |
| 22 | `provider_factory` | ChatGPT/DeepSeek/OpenCode Go/OpenAI/Ollama factories and IDs |
| 23 | `chatgpt_pkce` | RFC 7636 PKCE S256 test vector |
| 24 | `chatgpt_authorize_url` | OAuth parameters, scopes, resource, agent hint, host ID |
| 25 | `chatgpt_id_token_validation` | RS256 JWKS signature, nonce, audience, issuer, claims |
| 26 | `deepseek_stream_and_cost` | SSE streaming, `reasoning_content`, tool calls, usage and cost estimate |
| 27 | `openai_responses_stream` | Responses API SSE, function call assembly, usage, raw items |
| 28 | `chatgpt_oauth_responses_request` | ChatGPT route uses `additional_tools`, `store:false`, `stream:true`, no unsupported fields, bearer OAuth token |
| 29 | `chatgpt_token_refresh` | Form-encoded refresh grant, rotating tokens, secret persistence |
| 30 | `opencode_stream_and_tools` | OpenCode Go model discovery, chat-completions streaming, reasoning, tools and usage |
| 31 | `mcp_installer_json_merge` | JSON client config merge/remove preserves existing servers |
| 32 | `mcp_installer_codex_toml` | Codex TOML section merge/remove preserves existing config |
| 33 | `mcp_installer_opencode_v2` | OpenCode `mcp.servers` merge/remove preserves existing servers |
| 34 | `mcp_catalog_stdio_json` | Catalog stdio server install/remove (Playwright) preserves existing servers |
| 35 | `mcp_catalog_oauth_opencode` | Catalog OAuth HTTP server install/remove (GitHub) in OpenCode v2 |
| 36 | `agent_tool_loop_stages_change` | Real tool loop: model → tool → result → final answer; write is staged, not applied |
| 37 | `agent_tool_loop_apply_after_approval` | Human approval + apply writes the file correctly |
| 38 | `history_paged_lazy_search_clear` | 125 messages span pages; latest-page load, older-page load, search and clear |
| 39 | `history_context_capped` | Model context keeps the last 120 messages while display history stays separate |
| 40 | `markdown_and_syntax` | Markdown blocks, escaping and syntax-highlight tags |
| 41 | `chat_panel_builds` | Chat panel / `ListView` construction without a panel |
| 42 | `window_ui_shell_builds` | Full UI Toolkit shell builds in memory |
| 43 | `activity_group_file_cards` | Compact tool group with activities, read file card and change card |
| 44 | `history_load_benchmark` | Latest-page load at 100/500/1000 messages (representative local benchmark) |

## Migration checks

The upgrade moves the previous configuration and library directories without losing data:

- `~/.config/unity-ai-bridge/<project-id>/settings.json`, `secrets.json`, `pending-changes.json`, `chat-*.json` and `bridge.log` are retained.
- Existing pending changes keep working; legacy backup paths are remapped to `Library/UnityAIBridge/Backups/`.
- Library backups and test artifacts are moved to `Library/UnityAIBridge/`.
- Existing ChatGPT OAuth client ID, tokens and host ID are preserved. New OAuth registrations use the new agent name.
- Existing MCP client configurations must be updated to the new server name `unity-ai-bridge`; the bearer token itself is unchanged.

## What is not covered by automation

- **Real ChatGPT OAuth consent**: requires an eligible OpenAI account and browser interaction. PKCE, URL construction, ID-token crypto, refresh and the ChatGPT Responses request shape are tested locally.
- **Real DeepSeek/OpenAI/Ollama inference**: no credentials are included. Protocol handling is tested against local mock servers; live inference depends on your accounts.
- **Applying a `.cs` change end-to-end**: this triggers a domain reload, which would interrupt the runner. File-level create/modify is tested; script application is verified manually.
- **UI layout/interaction**: UI Toolkit shell and chat are smoke-tested in memory; pixel layout and real mouse/keyboard interaction are checked manually. Legacy tabs use IMGUI.
- **Live model availability**: depends on your accounts.

## Manual checklist

1. Open **Tools → Unity AI Bridge**. Switch all tabs; confirm no Console exceptions.
2. In Chat: create a new conversation, send a short request, verify streaming updates at a steady rate, scroll up to load older pages, search/rename/delete a conversation, and confirm the model/context/output settings are respected.
3. DeepSeek: enter a key → **Save & test** → model dropdown fills → send "Inspect my current Unity scene." Watch streaming, reasoning, tool messages and token/cost line.
4. Ask: "Create a basic player controller." Confirm changes appear in **Pending Changes** with diffs; approve and apply; confirm compilation.
5. Reject a change and confirm no file is written.
6. ChatGPT: press **Continue with ChatGPT** → complete consent → confirm the account label and model list. If plan usage is not granted, confirm the UI says so instead of connecting.
7. Switch provider mid-session; confirm each provider keeps its own conversation and model.
8. Sign out of ChatGPT; confirm tokens are cleared and the provider shows signed out.
9. Set **Read Only** and confirm write tools are not offered and direct staging is rejected.
10. Import a texture, configure it as a sprite, and create/assign a prefab.
11. Verify Explorer/Scene/Assets/Changes/MCP/Logs behave as expected.
