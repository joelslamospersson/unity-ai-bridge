# MCP bridge

The bridge runs an MCP (Model Context Protocol) server over **Streamable HTTP JSON-RPC 2.0** on `127.0.0.1` only. It is intended for MCP clients such as Codex CLI, Claude Desktop, Cursor, Cline and MCP Inspector.

## Endpoint and authentication

```
POST http://127.0.0.1:7777/mcp        (port configurable)
GET  http://127.0.0.1:7777/health     (no auth, no project data)
```

Authentication (required):

```
Authorization: Bearer <token>
```

`X-Bridge-Token: <token>` is also accepted. Clients that cannot set headers may use `?token=<token>` or `/mcp/<token>` when a tunnel preserves the path/query. URL tokens can appear in logs, so prefer the header and rotate the token afterwards.

Tokens are generated in the **MCP** tab, stored in `secrets.json` (0600) outside the project, and never sent to an AI provider. `401` is returned for missing/invalid tokens, with a JSON-RPC error body.

## Protocol

Supported methods:

| Method | Behaviour |
| --- | --- |
| `initialize` | Returns protocol version (echoes the client's or `2025-06-18`), tool capability, server info, instructions |
| `notifications/initialized` | `202` with empty body |
| `ping` | `{}` |
| `tools/list` | Tool definitions with JSON Schemas |
| `tools/call` | Executes a tool; result is an MCP `content` envelope (`content[]`, `isError`) with `structuredContent` |
| `resources/list`, `prompts/list` | Empty lists |
| `logging/setLevel` | Accepted |

Batch requests are supported. Unknown methods return `-32601`; parse errors `-32700`. The server is stateless apart from the token and a session id header.

## Tools

### Read tools (execute immediately)

| Tool | Arguments | Purpose |
| --- | --- | --- |
| `project_summary` | — | Unity version, pipeline, packages, counts, folders, gap analysis |
| `list_project_files` | `path`, `recursive`, `extensions`, `max` | Discover files |
| `read_project_file` | `path`, `max_chars` | Read text (`Assets/`, `Packages/`, `ProjectSettings/`, `Documentation/`) |
| `inspect_scene` | `depth`, `max_nodes`, `include_components` | Active scene hierarchy |
| `list_gameobjects` | `max` | Exact hierarchy paths |
| `inspect_asset` | `path` | Importer, sprite rects, prefab components, material properties, animator states |
| `read_console_errors` | `max`, `include_warnings` | Compiler diagnostics + console errors |
| `refresh_assets` | — | `AssetDatabase.Refresh()` |
| `get_pending_changes` | `status`, `include_diff` | Staged change state |
| `get_bridge_status` | — | Permission level, provider, MCP state, pending counts |

### Write tools (staged for approval)

Every write tool creates a pending change with a unified diff. **Nothing is written to the project until a human approves it in Unity.** The response contains the change `id`, `status` (`Pending`), target and a truncated diff. Only after approval does `apply_approved_changes` write anything.

| Tool | Arguments | Purpose |
| --- | --- | --- |
| `propose_file_changes` | `changes[] {kind: create/modify/delete, path, content}` | Create/overwrite/delete text files with complete content |
| `apply_approved_changes` | — | Apply changes already approved by a human |
| `create_gameobject` | `name`, `parent`, `primitive`, `position`, `rotation`, `scale`, `components[]`, `sprite` | Add an object to the active scene |
| `update_component` | `target`, `component`, `property`+`value` or `properties{}` | Change serialized properties (e.g. `m_SortingOrder`) |
| `create_prefab` | `path`, `target` or `spec`, `keep_in_scene` | Save a scene object (or a new spec) as a prefab |
| `import_sprite` | `path`, `source_path` or `image_base64`, `sprite_mode`, `pixels_per_unit`, `filter`, `compression`, `max_size`, `alpha_is_transparency` | Copy an image into the project and import it as a Sprite |
| `set_sprite_import` | `path`, same settings | Update existing texture import settings |
| `slice_sprite` | `path`, `rows`, `columns` | Grid-slice a sprite sheet |
| `assign_sprite` | `target`, `component` (`SpriteRenderer`/`Image`), `sprite` | Assign a Sprite |
| `create_material` | `path`, `color` (`#RRGGBB`), `texture`, `shader` | Create a material with an available shader |
| `create_scriptable_object` | `path`, `scriptable_type`, `properties{}` | Create a `.asset` from an existing ScriptableObject type |
| `save_scene` | — | Save the active scene (staged) |

`import_sprite` does **not** generate art. `image_base64` is for an image you already produced elsewhere; the file is staged under `Library/UnityAIBridge/incoming/` and only copied into `Assets/` on approval.

Write tools are refused entirely in **Read Only** mode. In **Project Write Access**, non-destructive changes auto-apply only if you explicitly enable auto-apply in Settings; destructive changes always require approval.

## Example: curl

```bash
TOKEN="<from MCP tab>"
URL="http://127.0.0.1:7777/mcp"

curl -s "$URL" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'

curl -s "$URL" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

curl -s "$URL" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"project_summary","arguments":{}}}'

# stage a script (no write yet)
curl -s "$URL" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"propose_file_changes","arguments":{"changes":[{"kind":"create","path":"Assets/Scripts/Hello.cs","content":"public class Hello {}\n"}]}}}'
```

## Upgrading from earlier releases

The MCP registration name is now **`unity-ai-bridge`** and the Codex environment variable is **`UNITY_AI_BRIDGE_TOKEN`**. Existing client configurations keep working with their existing bearer token, but the server entry/name must be updated in the client. Re-copy the JSON config or the Codex command from the MCP tab.

## Client configuration

The MCP tab copies a ready JSON block. Generic form (Claude Desktop, Cursor, many clients):

```json
{
  "mcpServers": {
    "unity-ai-bridge": {
      "url": "http://127.0.0.1:7777/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Codex CLI:

```bash
export UNITY_AI_BRIDGE_TOKEN="<token>"
codex mcp add unity-ai-bridge --url http://127.0.0.1:7777/mcp \
  --bearer-token-env-var UNITY_AI_BRIDGE_TOKEN
```

MCP Inspector (manual testing):

```bash
npx @modelcontextprotocol/inspector@latest
# Transport: Streamable HTTP
# URL: http://127.0.0.1:7777/mcp
# Header: Authorization: Bearer <token>
```

## Recommended assistant workflow

1. `project_summary` → `list_project_files` → `read_project_file` / `inspect_scene` / `inspect_asset`.
2. Reuse existing scripts/assets; do not duplicate.
3. Call write tools. The reply says a human must approve.
4. Wait for the human to approve in **Pending Changes**.
5. `get_pending_changes` to check status, then `apply_approved_changes`.
6. `refresh_assets`, then `read_console_errors`.
7. If compilation fails, inspect the errors and propose a fix as a new change.

## Security notes

- Binds to `127.0.0.1` only; no LAN/public exposure.
- Bearer token comparison is constant-time; regenerate from the UI.
- No shell execution, no filesystem access outside the project sandbox, no reading `.git`/`Library`/secrets.
- A local process running as your user could connect with the token and stage changes; it still cannot apply them without approval (unless you enabled auto-apply).
- MCP tool results can contain project code. Only connect clients you trust.
- Prompt injection in project files is possible: an assistant could be tricked into proposing a bad change. The diff/approval step is the defence.
