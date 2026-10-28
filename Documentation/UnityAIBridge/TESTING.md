# Testing and results

## Image generation and advanced animation (2.4.0)

**209 self-tests pass** in isolated Unity 6000.6.7f1. New localhost image fixtures verify disabled/invalid requests send no generation, explicit billing consent survives conversation auto acceptance, denial sends no request, request headers/body use the API route, PNGs are staged before import, alpha is configured, destination races preserve existing assets, invalid image payloads are rejected, provider errors redact credentials and send no retry, and cancellation stages no asset. A retained-preview regression covers a destination changing while generation is in flight. Submitted paid requests are not replayed after reload; changed billing options require a new decision.

Advanced animation tests verify parameter defaults, conditional transitions, serialized blend subassets/thresholds, inspection and rejection of invalid parameter/mode combinations, integer thresholds, duplicate names and invalid blend parameters.

Rendered pointer-interaction validation now includes the image-generation form: entering a prompt alone leaves generation disabled, consent enables it, and disabling generation disables it again. It submits no image-generation request. No paid inference, image-quality evaluation, visual timeline, native desktop-input automation or history rewrite is claimed.

## Activity, animation and interaction validation (2.3.0)

The isolated Unity 6000.6.7f1 suite passes **184 tests, 0 failures**. Added coverage verifies truthful activity phases, monotonic run timers, real transport heartbeats separated from model progress, quiet-provider notices, approval/Stop states, safe retry after heartbeats, animation approval boundaries, interpolation/loop/sample settings, sprite subasset references, controller states/transitions, invalid keys/paths/sources and destination collision protection at apply time.

The rendered layout check exercises growing answers, tool output, long thinking summaries and compact approval/composer layout. The interaction harness runs a rendered Editor window and routes pointer clicks through production Stop and approval controls. Results are written to `Library/UnityAIBridge/chat-layout-result.json` and `interaction-result.json`. The remaining UI limitations are screenshot-diff comparisons and native desktop input automation.

To reproduce in a **disposable** project containing the bridge and Unity UI:

```bash
python3 scripts/validate_unity_ui.py \
  --unity /path/to/Unity \
  --project /tmp/unity-ai-bridge-validation
```

The runner requires Python 3 and a working graphical display (an X11 desktop or Xvfb on Linux). The Unity harness picks visible controls in a rendered panel and dispatches pooled pointer events through their production handlers. It exercises Stop, destructive confirmation, Accept, Deny and Send comment; it does not submit provider prompts or modify gameplay assets. Never point it at a working game project: the harness uses disposable conversations and exits the Editor. Pass `--mode layout` or `--mode interaction` to select one check. Logs and JSON results remain under the disposable project's `Library/UnityAIBridge/`.

## Provider stream timeout repair (2026-10-28)

**165 passed, 0 failed** in an isolated Unity 6000.6.7f1 project. A ChatGPT request failed with `The provider stream timed out.` because its configured 180-second timer measured the whole HTTP request and was never renewed by incoming data. The Responses provider also waited for the HTTP connection to close after receiving a terminal event.

Streaming now uses an inactivity deadline, renewed when fragments arrive (including comments/heartbeats and partial lines). Response headers have a separate deadline. Responses completion/failure events and chat-completion `[DONE]` markers end the read immediately. Error-body reads and Stop remain bounded. A typed timeout permits one retry of the current inference only before output begins; completed tool results remain in the transcript. Partial output, user cancellation and HTTP/provider errors do not enter this retry path.

Thirteen added regressions exercise active SSE and NDJSON streams exceeding their timeout duration, fragmented CRLF/multiline data and Unicode, real stalls, stalled HTTP error bodies, header deadlines, Stop, terminal events on connections kept open by the server, bounded retry after committed tool results, no replay after partial text/reasoning/tool output, and cancellation during retry. These use local mock servers and the real Unity/Mono HTTP transport. No live generation or changes to gameplay assets were performed.

Result file: `/tmp/unity-bridge-timeout/test-project/Library/UnityAIBridge/test-results.json`.

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
91 passed, 0 failed   (includes prefab and approval regressions)
```

Validated on 2026-10-26 in an isolated Unity 6000.6.7f1 project with Unity UI installed. See [chat repair notes](CHAT-REPAIR.md).

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
- **UI layout/interaction**: coverage includes in-memory smoke tests, rendered streaming layout and simulated pointer interactions through production controls. Native desktop mouse/keyboard automation and screenshot comparisons remain manual. Legacy tabs use IMGUI.
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

## Login and chat recovery regression coverage (2026-10-26)

The suite now also covers delayed OAuth callbacks (more than two seconds), favicon requests and stale browser tabs; callback cancellation; forced renewal of a rejected but unexpired token; transient refresh errors retaining credentials; sign-out during an in-flight refresh; model catalog visibility, ordering and compatible schemas; reasoning request fields on both Responses routes; rejection of truncated streams; stopping a stream after headers arrive; Chat/Plan/Agent tool availability and rejection of unoffered tools; repair of interrupted multi-tool turns; and history retention at complete user-turn boundaries.

The native panel smoke test checks that the sign-in button, composer, mode selector, reasoning selector and retry button are constructed. Final-answer tests keep thinking summaries separate from failed or malformed answers.

## Latest prefab conversation regression

See [prefab conversation repair](PREFAB-CONVERSATION-REPAIR.md) for the exact failed request, live proposal repair, and added tests. The real-map checks run when the source KingdomGuard map exists in the test project; the recorded 77-test run includes that fixture.

## Summary layout and inline approval validation

See [approval and summary validation](APPROVALS-AND-SUMMARY.md) for the new review-flow tests, rendered layout test, and actual assembly-reload test. The 91-test count includes the optional real map fixture.


## Shared MCP installation and key application (2026-10-27)

**117 passed, 0 failed** in Unity 6000.6.7f1 against `/tmp/unity-bridge-chat-fix`. MCP coverage includes saving/replacing/clearing keys across selected clients, current Context7 bearer authentication, GitHub PAT/OAuth selection, persistent target selection, Codex ↔ OpenCode sync, quoted TOML names and nested header/environment tables, multiline command arguments, environment-based credentials, working directories/timeouts, JSONC, unrelated-config preservation, source immutability, malformed configs and partial failures. Generated TOML was also parsed independently with Python's standard TOML reader, and generated configs retained mode 0600.

The full MCP tab was exercised at 1100×800 in Unity's UI Toolkit shell without GUI exceptions. Test results are in `/tmp/unity-bridge-chat-fix/Library/UnityAIBridge/test-results.json`; UI smoke results are in `mcp-ui-result.json`. No real client configurations were changed by validation.


## ChatGPT Plus / Pro login and return (2026-10-27)

**130 passed, 0 failed** in an isolated Unity 6000.6.7f1 project. The added local OAuth integration test exercises first registration, PKCE code exchange using the issued client ID and exact loopback callback, signed ID-token verification, granted plan permission, browser completion pages, main-thread return events, subscription model discovery and a completed streamed inference using the OAuth token. It also covers missing plan consent, explicit reconsent, stable registration/host reuse, invalid nonce, changed callback client ID, the usage link and dismissible first-connection confirmation. The platform API key is deliberately different and is never used for the subscription request.

All identity, model and inference endpoints in these tests are localhost mocks; no real account sign-in or paid inference occurs. Actual account/workspace eligibility and browser consent must be verified with the user's ChatGPT account. The production flow uses OpenAI's documented `auth.openai.com` registration and public Responses API contracts, without a Codex process.

Result file: `/tmp/unity-bridge-chat-fix/Library/UnityAIBridge/test-results.json`. The existing rendered streaming/approval layout validation is also run after the account controls change.


## ChatGPT chat-model separation (2026-10-27)

**133 passed, 0 failed.** The additional regressions verify that the ChatGPT account catalog omits Codex-labelled entries while retaining visible GPT-6/GPT-6 Pro chat models, selects an exposed GPT-6 model as the preferred initial model without inventing model access, and refuses Codex model execution through the direct ChatGPT route. Cached catalogs are filtered and obsolete cached model selections are migrated. This does not provide access to existing ChatGPT website conversations or introduce a native Codex runtime.


## Direct plan access and usage-limit reporting (2026-10-27)

**134 passed, 0 failed.** A regression now verifies that a subscription usage-limit response is reported as a plan-or-app limit without inventing a reset time, retrying the limited request, discarding the session or switching billing/provider. UI and setup text no longer claim that direct requests necessarily have a separate Codex allowance. The history notice was removed from the account bar because importing website history is outside the requested scope.

A read-only live catalog request using the Unity project's saved ChatGPT OAuth session returned HTTP 200 and listed GPT-6.1 Sol, GPT-6 Astra, GPT-6.1 Terra, GPT-6 Sol and GPT-6 Luna, along with earlier chat models. No live generation was performed. The response did not expose quota information, so independence from the account's Codex allowance remains unverified. See OpenAI's [shared plan usage](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions) and [usage-limit error guidance](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery).


## Collapsed tools, Claude and Gemini (2026-10-27)

**152 passed, 0 failed** in the isolated Unity 6000.6.7f1 project. New regressions verify that tool groups remain collapsed during streaming until explicitly opened, retain their full output, preserve the user's expansion choice and stay closed after the user collapses them again.

Claude coverage includes model pagination and headers, streaming text/thinking and fragmented tool JSON, complete agent/tool-result loops, native schemas/system instructions, signed thinking replay, usage and rejection of incomplete responses. Gemini coverage includes filtered/paginated model discovery, API-key headers without URL credentials, thought summaries and function calls, complete tool-result loops, native schemas/system instructions, thought-signature and call-ID replay, usage, blocked prompts and incomplete responses. Provider factories, saved provider selection, credential isolation and redaction are covered as well.

The rendered streaming layout validation also passed at 680×620, including full expanded tool output, thinking and accessible approval/composer controls. All new provider requests use localhost mocks. No live Claude/Gemini inference was performed, and the official Claude Code CLI was not installed in this environment, so its browser login and terminal launch still need a real account/client check. The external handoff does not claim native subscription inference.
