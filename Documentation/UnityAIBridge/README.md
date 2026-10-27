# Unity AI Bridge

**Open-source AI development assistant for the Unity Editor.**

Unity AI Bridge is an independent, MIT-licensed Editor extension that connects AI assistants to your Unity development workflow. Chat with ChatGPT, Claude API, Gemini API, DeepSeek, OpenCode Go, OpenAI API, or Ollama, inspect project context, and manage scripts, scenes, prefabs, and assets through permission-controlled tools.

It is not affiliated with, endorsed by, or sponsored by Unity Technologies, OpenAI, Anthropic, Google, DeepSeek, OpenCode, or Ollama.

## Highlights

- **UI Toolkit chat** with growing transcript rows, conversation sidebar, paged history and lazy older-page loading
- **Responsive streaming** with buffered 10–15 FPS UI updates, periodic crash-recovery checkpoints, Markdown, syntax-highlighted code blocks, file cards and compact tool groups
- **Token-aware context**: recent turns plus an older-context summary; project context is cached and script source is read through tools instead of being pasted into every request
- **Response modes** (Concise/Balanced/Detailed) and a separately configurable output-token budget
- **ChatGPT** via Sign in with ChatGPT (OAuth + PKCE, ChatGPT plan usage), with sign-in and recovery controls directly in Chat
- **Claude API and Gemini API** with account model discovery, streamed answers/thinking, native function tools and usage
- **Claude subscription (external)** with sign-in and launch controls for the official Claude Code client, plus Unity MCP installation
- **Chat / Plan / Agent** modes and reasoning effort controls beside the composer
- **DeepSeek** API with model discovery, reasoning, streaming tool calls, usage and estimated cost
- **OpenCode Go** with OpenAI-compatible chat completions, model discovery and tool calling
- **OpenAI API** with streaming Responses API and function tools
- **Ollama** local inference with streaming and tool calling where supported
- **Permission-controlled project tools** for scripts, scenes, GameObjects, prefabs, materials, sprites, ScriptableObjects, console diagnostics and asset refresh
- **Prefab reuse** directly from existing prefab assets, with bounded hierarchy inspection, missing-script validation, and optional cleanup of new copies
- **Pending Changes** with diffs and in-chat review, plus optional **Auto accept all for this conversation**
- **MCP server** with one-click installers for Codex CLI, OpenCode, Cursor, VS Code and Claude Code; auto-start checkbox; manual setup for Claude Desktop and MCP Inspector
- **MCP catalog** for Context7, GitHub, MCP for Unity (Coplay), MCP for Blender and Playwright, with **Save & apply**, shared client selection and cross-client configuration sync
- **Any Unity project**: 2D, 3D, mobile, desktop, VR and any folder layout; render-pipeline aware, with an explicit shader option for custom pipelines

## Requirements

- Unity **6.x** (developed and tested with **6000.6.7f1**)
- Linux, macOS, or Windows Editor
- The project must include Unity UI (`com.unity.ugui`), used by the existing scene and asset tools

Earlier Unity versions are untested. The extension uses standard public Editor APIs (`AssetDatabase`, `PrefabUtility`, `EditorSceneManager`, `Undo`).

## Installation

1. Copy the `Assets/UnityAIBridge` folder into your Unity project's `Assets/` folder, keeping the `.meta` files.
2. Wait for Unity to import and compile.
3. Open **Tools → Unity AI Bridge**.

No runtime scripts or build dependencies are added; all code lives in an `Editor` folder.

## Live chat

Tool calls start collapsed; open a tool group to see its complete output, including while it is running. Your expansion choice persists through streaming updates. Long thinking summaries scroll independently. Agent requests pause for **Accept & apply**, **Deny**, or **Send comment** in the chat, then continue from your decision. One-time grants preserve your global access setting, and script compilation restores the conversation after assembly reload. Enable **Auto accept all for this conversation** to accept current and future prompts, including destructive changes; disable it to return to individual review. See [approval and summary behavior](APPROVALS-AND-SUMMARY.md).

Choose **ChatGPT** in the provider menu and press **Continue with ChatGPT** in the chat panel. Finish consent in the system browser, then return to Unity and select a model. **Open sign-in page** reopens an active browser flow; **Cancel sign-in** releases it. Account and sign-out options remain in **Account & settings**.

The composer includes three modes:

| Mode | Behavior |
| --- | --- |
| Chat | Answers with supplied project context; no tools execute. |
| Plan | Reads project files and scene state, then proposes a plan without staging changes. |
| Agent | Uses Unity tools, stages changes according to permissions, and continues through tool results. Review proposals in the chat approval prompt or Changes. |

**Reasoning** selects the model's effort for the next response. OpenAI/ChatGPT offer Default, Low, Medium, and High on recognized reasoning models; model support varies. Thinking summaries appear separately from the answer when the provider supplies them. Default lets the provider choose its effort. Reasoning is independent of the Chat/Plan/Agent mode.

Press **Enter** to send or **Shift+Enter** for a newline (Alt+Enter also works). **Stop** or **Esc** cancels generation, including stalled streams. **Retry response** continues a failed request from its recorded conversation and tool results, without inserting a duplicate user prompt. Unsent drafts are retained in memory when switching conversations and providers; they do not survive a Unity domain reload. Scrolling upward pauses following; **Jump to latest** resumes it.


The chat window includes a provider and model dropdown, connection status, permission mode, streaming replies, reasoning, stop generation, compact tool progress with file cards, and per-provider conversation history.

- Display history is stored on disk in pages of **50** messages. Opening a conversation reads only the latest page; **Load older messages** fetches earlier pages on demand.
- Model context is stored separately, kept to approximately **120** messages at complete user-turn boundaries, and trimmed to recent user turns under the configured character budget. Older turns are represented by a short local summary.
- Project context is a cached, lightweight summary; exact files are read through tools. Attach up to eight project files to include them explicitly.
- **New chat**, rename, delete, clear history and search are available in the conversation sidebar.
- File cards come from structured tool results/change IDs, not regex scraping. Clicking a change card opens it in **Pending Changes**; read cards ping the asset.
- Streamed UI updates are throttled to roughly 12 FPS; the transcript is not rebuilt for every token.

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

Write operations are staged as diffs in **Pending Changes**. They require review unless you have enabled the applicable automatic approval setting.

## Permissions and safety

| Level | Behaviour |
| --- | --- |
| **Read Only** | Inspect only; write tools are not offered |
| **Ask Before Editing** (default) | Every change is staged and requires approval |
| **Project Write Access** | Approved writes under `Assets/`; optional auto-apply for non-destructive changes (off by default) |

- All paths are project-relative and sandboxed to the project; symlink escapes are rejected.
- Writes are limited to `Assets/`; the bridge's own source is protected by default.
- Backups are created before overwrite/delete; scene operations use Unity Undo; asset moves/deletes preserve `.meta` GUIDs.
- Destructive changes require explicit confirmation unless **Auto accept all for this conversation** is enabled. Script changes warn that Unity will compile them.
- API keys and OAuth tokens are stored outside the project in `~/.config/unity-ai-bridge/<project-id>/secrets.json` (0600 on Unix).
- The bridge never uses browser cookies, scraping, or unofficial provider endpoints.

See [SECURITY.md](SECURITY.md).

## AI providers and billing

| Provider | Authentication | Billing / limits |
| --- | --- | --- |
| ChatGPT | Sign in with ChatGPT (OAuth + PKCE) | ChatGPT plan usage, subject to OpenAI eligibility and shared usage limits |
| Claude API | Anthropic Console API key | Anthropic API billing, separate from Claude subscriptions |
| Claude subscription (external) | Official Claude Code sign-in | Claude Code subscription limits; replies run in that external client |
| Gemini API | Google AI Studio API key | Google API billing/quotas, separate from Gemini app subscriptions |
| DeepSeek | API key | Per-token DeepSeek API billing |
| OpenCode Go | API key | OpenCode Go subscription/usage |
| OpenAI API | Platform API key | Per-token OpenAI API billing |
| Ollama | Local URL | Local inference |

**ChatGPT plan usage is not API access and is not unlimited.** On Plus, usage is subject to a shared 5-hour limit; Pro has no 5-hour limit. Availability is controlled by OpenAI. If plan usage is not granted, the provider is not marked connected and the OpenAI API provider can be used instead.

Claude subscription credentials stay with the official Claude Code client. Native Claude subscription OAuth is not offered: Anthropic requires prior approval for third-party Claude.ai sign-in. Use Claude API for replies inside Unity, or install the Unity MCP bridge and use the official external client.

Real provider sign-in and inference require your own accounts. The automated suite validates protocol, streaming, tool-call and refresh logic against local mock servers; it does not claim live inference results without credentials.

See [SETUP.md](SETUP.md).

## MCP integration

Select the MCP clients to configure, then **Install for selected clients**. Saving a catalog key applies it to every checked client. **Sync from** copies installations, static keys, commands and supported settings between clients; provider-managed OAuth sessions must still be authorized separately.

The extension can run a local MCP server at `http://127.0.0.1:7777/mcp` with a generated bearer token. It exposes the same project tools to compatible local clients. It binds to localhost only and is not publicly exposed.

See [MCP.md](MCP.md).

## Testing

Run **Tools → Unity AI Bridge → Run Self Tests**. The suite covers file and asset operations, sandbox escapes, permission failures, Undo, MCP, provider request/stream parsing, OAuth PKCE/JWT/refresh and the tool loop.

The latest isolated Unity 6000.6.7f1 run passed **152 tests**, and the rendered streaming layout check passed. Claude/Gemini protocol tests use localhost mocks; no live paid inference was performed.

See [TESTING.md](TESTING.md).

## Upgrade / migration

Existing installations are migrated automatically:

- Settings, credentials, pending changes, conversations and logs from the previous configuration directory are moved to `~/.config/unity-ai-bridge/`.
- Legacy `chat-<provider>.json` sessions are imported once into the paged `history/<provider>/<conversation>/` store; the old file is renamed with a `.migrated` suffix.
- Backups and test artifacts are moved from the previous plugin library folder to `Library/UnityAIBridge/`.
- Existing ChatGPT OAuth tokens and client registrations are retained. The new agent name is used for new registrations; an existing ChatGPT registration may continue to show its original display name until you disconnect and reconnect it in ChatGPT settings.
- Existing MCP client configurations use the new server name `unity-ai-bridge`; update those clients after upgrading.
- The Editor window class was renamed. A saved window layout entry from the previous version may not restore automatically; open **Tools → Unity AI Bridge** once after upgrading.

## Known limitations

- Image generation is not implemented. The Asset Manager imports and configures existing artwork.
- Animation authoring is limited to inspecting controllers and assigning them; there is no keyframe/timeline editor.
- The chat UI is UI Toolkit, while the Project/Changes/MCP/Settings/Logs tabs are still hosted through `IMGUIContainer`. The suite performs in-memory UI smoke tests and builds the full shell; it does not automate pixel layout or real mouse interaction.
- Provider features and availability can change; the extension reports provider errors rather than hiding them.
- Historical information in old Git commits is not removed by editing files. See the repository privacy notes in the release summary.

## License

MIT. See [LICENSE](../../LICENSE).
