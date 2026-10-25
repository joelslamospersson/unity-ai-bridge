# Unity AI Bridge

**Open-source AI development assistant for the Unity Editor.**

An independent, MIT-licensed Unity Editor extension that connects AI assistants to your development workflow. Chat with ChatGPT, DeepSeek, OpenCode Go, OpenAI API, or Ollama, inspect project context, and manage scripts, scenes, prefabs, and assets through permission-controlled tools.

> Unity AI Bridge is independently developed. It is not affiliated with, endorsed by, or sponsored by Unity Technologies, OpenAI, DeepSeek, OpenCode, or Ollama.

## Features

- **UI Toolkit chat** inside **Tools → Unity AI Bridge**: virtualized `DynamicHeight` list, conversation sidebar, paged history, lazy older-message loading and a conversation search
- Responsive streaming at roughly 10–15 FPS, with Markdown, syntax-highlighted code blocks, reasoning, file cards and compact tool groups
- Token-aware context: recent turns plus a local older-context summary; cached project context and tool-based file reads instead of pasting the whole project
- **ChatGPT** via Sign in with ChatGPT (OAuth + PKCE, ChatGPT plan usage)
- **DeepSeek** API with model discovery, reasoning, streaming tool calls, usage and estimated cost
- **OpenCode Go** with OpenAI-compatible chat completions, model discovery and tool calling
- **OpenAI API** with streaming Responses API and function tools
- **Ollama** local inference with streaming and tool calling where supported
- 22 permission-controlled Unity tools for project inspection, scripts, scenes, GameObjects, prefabs, materials, sprites, ScriptableObjects, console diagnostics and asset refresh
- **Pending Changes** with diffs and human approval before anything is written
- Local **MCP server** with one-click installers (Codex, OpenCode, Cursor, VS Code, Claude Code), automatic-start checkbox, and manual setup for Claude Desktop/MCP Inspector
- **MCP catalog** for Context7, GitHub, MCP for Unity (Coplay), MCP for Blender and Playwright, with API-key/OAuth setup
- Works with Unity projects: 2D, 3D, mobile, desktop, VR and any folder layout; render-pipeline aware, with an explicit shader option for custom pipelines
- Sandboxed paths, backups, Unity Undo, GUID-preserving asset moves and secure credential storage

The assistant inspects the current project dynamically. It is not given any assumptions about genre, mechanics, architecture, target platform, render pipeline, input system, package set or folder layout.

## Requirements

- Unity **6.x** (developed and tested with **6000.6.7f1**)
- Linux, macOS or Windows Editor
- No additional Unity packages required

Earlier Unity versions are untested. The extension uses public Editor APIs (`AssetDatabase`, `PrefabUtility`, `EditorSceneManager`, `Undo`).

## Installation

1. Copy `Assets/UnityAIBridge` into your project's `Assets/` folder, keeping all `.meta` files.
2. Wait for Unity to import and compile.
3. Open **Tools → Unity AI Bridge**.

All code is Editor-only. No runtime components, build dependencies or game assets are included.

## Live chat examples

- "Inspect my current Unity scene."
- "Create a basic player controller."
- "Find and explain compilation errors."
- "Import a texture and configure it as a sprite."
- "Create a reusable prefab."
- "Review the project structure and suggest improvements."

## Project inspection and editing

| Area | Capabilities |
| --- | --- |
| Inspect | Project summary, files, scripts, scene hierarchy, components/properties, prefabs, materials, animator controllers, sprite import settings, packages, console and compiler diagnostics |
| Edit | Text files, folders, GameObjects, components, prefabs, materials, ScriptableObjects, PNG/JPG sprite import, sprite-sheet slicing, sprite assignment, scene save |
| Reuse | Existing scripts, assets, prefabs, materials and conventions are inspected before proposing changes |

Every write is staged as a diff in **Pending Changes** and requires approval.

## Permissions and safety

| Level | Behaviour |
| --- | --- |
| **Read Only** | Inspect only; write tools are not offered |
| **Ask Before Editing** (default) | Every change is staged and requires approval |
| **Project Write Access** | Approved writes under `Assets/`; optional auto-apply for non-destructive changes (off by default) |

- Project-relative path sandbox with symlink-escape rejection.
- Writes limited to `Assets/`; the extension's own source is protected by default.
- Backups before overwrite/delete; Unity Undo for scene operations; `.meta` GUID preservation for asset moves.
- Destructive changes require explicit confirmation; script changes warn about compilation.
- API keys and OAuth tokens are stored outside the project in `~/.config/unity-ai-bridge/<project-id>/secrets.json` (0600 on Unix).
- No browser cookies, scraping or unofficial provider endpoints.

See [Security](Documentation/UnityAIBridge/SECURITY.md).

## AI providers and billing

| Provider | Authentication | Billing / limits |
| --- | --- | --- |
| ChatGPT | Sign in with ChatGPT (OAuth + PKCE) | ChatGPT plan usage, subject to OpenAI eligibility and shared limits |
| DeepSeek | API key | Per-token DeepSeek API billing |
| OpenCode Go | API key | OpenCode Go subscription/usage |
| OpenAI API | Platform API key | Per-token OpenAI API billing |
| Ollama | Local URL | Local inference |

**ChatGPT plan usage is not API access and is not unlimited.** On Plus, usage shares a 5-hour limit across apps; Pro has no 5-hour limit. Availability is controlled by OpenAI. If plan usage is not granted, the provider is not marked connected; the OpenAI API provider can be used instead.

Real provider inference requires your own accounts and credentials. The test suite validates protocol, streaming, tool-call and refresh behavior against local mock servers; it does not claim live inference results without credentials.

See [Setup and authentication](Documentation/UnityAIBridge/SETUP.md).

## MCP integration

Unity AI Bridge can run a local MCP server at `http://127.0.0.1:7777/mcp` with a generated bearer token. It exposes the same project tools to compatible local clients, binds to localhost only, and is not publicly exposed.

See [MCP](Documentation/UnityAIBridge/MCP.md).

## Upgrade note

Existing installations are migrated automatically: configuration, credentials, pending changes, conversations, logs, backups and test artifacts are preserved. MCP client configurations must use the new server name `unity-ai-bridge`; existing ChatGPT OAuth registrations continue to work, and new registrations use the new agent name. The Editor window class was renamed, so a saved window layout may not restore automatically; open **Tools → Unity AI Bridge** once.

See [Setup](Documentation/UnityAIBridge/SETUP.md) and [Testing](Documentation/UnityAIBridge/TESTING.md).

## Testing

Run **Tools → Unity AI Bridge → Run Self Tests**. The suite covers file and asset operations, sandbox escapes, permissions, Undo, MCP, provider request/stream parsing, OAuth PKCE/JWT/refresh and the tool loop.

The current suite has 37 tests. See [Testing](Documentation/UnityAIBridge/TESTING.md) for details and current results.

## Known limitations

- Image generation is not implemented; artwork is imported/configured, not generated.
- Animation authoring is limited to inspecting controllers and assigning them.
- The UI is native IMGUI and is not covered by automated UI tests.
- Provider features and availability can change; the extension reports provider errors instead of hiding them.

## License

MIT. See [LICENSE](LICENSE).
