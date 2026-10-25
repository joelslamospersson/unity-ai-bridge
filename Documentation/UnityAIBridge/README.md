# Unity AI Bridge

**Open-source AI development assistant for the Unity Editor.**

Unity AI Bridge is an independent, MIT-licensed Editor extension that connects AI assistants to your Unity development workflow. Chat with ChatGPT, DeepSeek, OpenCode Go, OpenAI API, or Ollama, inspect project context, and manage scripts, scenes, prefabs, and assets through permission-controlled tools.

It is not affiliated with, endorsed by, or sponsored by Unity Technologies, OpenAI, DeepSeek, OpenCode, or Ollama.

## Highlights

- **Live streaming chat** inside a native Unity Editor window
- **ChatGPT** via Sign in with ChatGPT (OAuth + PKCE, ChatGPT plan usage)
- **DeepSeek** API with model discovery, reasoning, streaming tool calls, usage and estimated cost
- **OpenCode Go** with OpenAI-compatible chat completions, model discovery and tool calling
- **OpenAI API** with streaming Responses API and function tools
- **Ollama** local inference with streaming and tool calling where supported
- **Permission-controlled project tools** for scripts, scenes, GameObjects, prefabs, materials, sprites, ScriptableObjects, console diagnostics and asset refresh
- **Pending Changes** with diffs and human approval before anything is written
- **MCP server** for compatible local clients (Codex, Claude Desktop, Cursor, MCP Inspector)
- **Any Unity project**: 2D, 3D, mobile, desktop, VR and any folder layout; render-pipeline aware, with an explicit shader option for custom pipelines

## Requirements

- Unity **6.x** (developed and tested with **6000.6.7f1**)
- Linux, macOS, or Windows Editor
- No additional Unity packages are required

Earlier Unity versions are untested. The extension uses standard public Editor APIs (`AssetDatabase`, `PrefabUtility`, `EditorSceneManager`, `Undo`).

## Installation

1. Copy the `Assets/UnityAIBridge` folder into your Unity project's `Assets/` folder, keeping the `.meta` files.
2. Wait for Unity to import and compile.
3. Open **Tools → Unity AI Bridge**.

No runtime scripts or build dependencies are added; all code lives in an `Editor` folder.

## Live chat

The window includes a provider and model dropdown, connection status, permission mode, streaming chat, Markdown/code blocks, reasoning display, stop generation, tool progress, and per-provider conversation history.

Useful example requests:

- "Inspect my current Unity scene."
- "Create a basic player controller."
- "Find and explain compilation errors."
- "Import a texture and configure it as a sprite."
- "Create a reusable prefab."
- "Review the project structure and suggest improvements."

The assistant is instructed to inspect the project dynamically. It must not assume genre, mechanics, architecture, target platform, render pipeline, input system, package set, or folder layout.

## Project inspection and editing

| Area | Capabilities |
| --- | --- |
| Inspect | Project summary, files, scripts, scene hierarchy, components/properties, prefabs, materials, animator controllers, sprite import settings, packages, console and compiler diagnostics |
| Edit | Text files, folders, GameObjects, components, prefabs, materials, ScriptableObjects, PNG/JPG sprite import, sprite-sheet slicing, sprite assignment, scene save |
| Reuse | Existing scripts, assets, prefabs, materials and project conventions are inspected before proposing changes |

Write operations are staged as diffs in **Pending Changes**. Nothing is written until you approve and apply it.

## Permissions and safety

| Level | Behaviour |
| --- | --- |
| **Read Only** | Inspect only; write tools are not offered |
| **Ask Before Editing** (default) | Every change is staged and requires approval |
| **Project Write Access** | Approved writes under `Assets/`; optional auto-apply for non-destructive changes (off by default) |

- All paths are project-relative and sandboxed to the project; symlink escapes are rejected.
- Writes are limited to `Assets/`; the bridge's own source is protected by default.
- Backups are created before overwrite/delete; scene operations use Unity Undo; asset moves/deletes preserve `.meta` GUIDs.
- Destructive changes require explicit confirmation. Script changes warn that Unity will compile them.
- API keys and OAuth tokens are stored outside the project in `~/.config/unity-ai-bridge/<project-id>/secrets.json` (0600 on Unix).
- The bridge never uses browser cookies, scraping, or unofficial provider endpoints.

See [SECURITY.md](SECURITY.md).

## AI providers and billing

| Provider | Authentication | Billing / limits |
| --- | --- | --- |
| ChatGPT | Sign in with ChatGPT (OAuth + PKCE) | ChatGPT plan usage, subject to OpenAI eligibility and shared usage limits |
| DeepSeek | API key | Per-token DeepSeek API billing |
| OpenCode Go | API key | OpenCode Go subscription/usage |
| OpenAI API | Platform API key | Per-token OpenAI API billing |
| Ollama | Local URL | Local inference |

**ChatGPT plan usage is not API access and is not unlimited.** On Plus, usage is subject to a shared 5-hour limit; Pro has no 5-hour limit. Availability is controlled by OpenAI. If plan usage is not granted, the provider is not marked connected and the OpenAI API provider can be used instead.

Real ChatGPT sign-in and real DeepSeek/OpenAI/Ollama inference require your own accounts. The automated suite validates protocol, streaming, tool-call and refresh logic against local mock servers; it does not claim live inference results without credentials.

See [SETUP.md](SETUP.md).

## MCP integration

The extension can run a local MCP server at `http://127.0.0.1:7777/mcp` with a generated bearer token. It exposes the same project tools to compatible local clients. It binds to localhost only and is not publicly exposed.

See [MCP.md](MCP.md).

## Testing

Run **Tools → Unity AI Bridge → Run Self Tests**. The suite covers file and asset operations, sandbox escapes, permission failures, Undo, MCP, provider request/stream parsing, OAuth PKCE/JWT/refresh and the tool loop.

See [TESTING.md](TESTING.md).

## Upgrade / migration

Existing installations are migrated automatically:

- Settings, credentials, pending changes, conversations and logs from the previous configuration directory are moved to `~/.config/unity-ai-bridge/`.
- Backups and test artifacts are moved from the previous plugin library folder to `Library/UnityAIBridge/`.
- Existing ChatGPT OAuth tokens and client registrations are retained. The new agent name is used for new registrations; an existing ChatGPT registration may continue to show its original display name until you disconnect and reconnect it in ChatGPT settings.
- Existing MCP client configurations use the new server name `unity-ai-bridge`; update those clients after upgrading.
- The Editor window class was renamed. A saved window layout entry from the previous version may not restore automatically; open **Tools → Unity AI Bridge** once after upgrading.

## Known limitations

- Image generation is not implemented. The Asset Manager imports and configures existing artwork.
- Animation authoring is limited to inspecting controllers and assigning them; there is no keyframe/timeline editor.
- The UI is native IMGUI and is not covered by automated UI tests.
- Provider features and availability can change; the extension reports provider errors rather than hiding them.
- Historical information in old Git commits is not removed by editing files. See the repository privacy notes in the release summary.

## License

MIT. See [LICENSE](../../LICENSE).
