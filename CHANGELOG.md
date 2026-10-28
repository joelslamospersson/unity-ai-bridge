# Changelog

## 2.3.0 — 2026-10-28

- Added a persistent activity panel for waiting, thinking, writing, preparing tools, reading, editing, review and Stop. It shows elapsed time, actual provider data age and last-progress age; heartbeats do not masquerade as model progress. Quiet-provider notices appear after 45 seconds without traffic or progress and do not mislabel approval waits.
- Fixed false stream timeouts: streaming deadlines now measure inactivity and reset on fragments, comments and heartbeats. Responses terminal events, chat `[DONE]`, Claude `message_stop` and Ollama `done` release the connection immediately. Header/error-body waits and user cancellation remain bounded.
- Added one safe timeout retry before output starts, preserving committed tool results. Partial text, reasoning and tool calls are never automatically replayed.
- Added approval-controlled `create_animation_clip` and `create_animator_controller`: transform curves, interpolation, sprite subasset frames, loop/sample settings, states/default state and timed transitions. Validation runs before staging and again before writing; existing assets and GUIDs are preserved on collisions. Animation inspection includes bounded curve/frame summaries.
- Added rendered pointer-interaction validation using Python standard library and Unity UI Toolkit. It exercises production Stop and approval callbacks in a rendered Unity window, including destructive confirmation and a compact window layout. Pointer events are simulated inside the panel; native desktop mouse automation remains unverified.
- Validation: 184 self-tests passed in Unity 6000.6.7f1; rendered layout and interaction results are documented in TESTING.md. No live paid inference was used.
- Remaining scope is explicit: no built-in image generation, visual animation timeline, rig authoring, blend trees or conditional state-machine authoring; cross-platform mouse automation and screenshot-diff testing remain open.

## 2.2.0

- Added native Claude API and Gemini API providers with private API-key storage, Save & test, paginated account model discovery, streaming text/thinking, native function tools and usage reporting. Signed thinking and function-call data survive tool-result replay; blocked or incomplete responses surface as failures.
- Added a Claude subscription handoff to the official Claude Code client: browser sign-in, launch controls and Unity MCP setup. Subscription credentials remain with that client; native Unity replies use Claude API.
- Tool groups now start collapsed and preserve the user's expansion choice during streaming. Full output remains available when opened.
- Fixed clipped live transcript rows, thinking summaries and tool outputs; retained paged history and throttled updates.
- Added conversation-scoped Auto accept all, inline Accept/Deny/Comment review, durable approval checkpoints and continuation after script reload.
- Added shared MCP client selection, Save & apply / Clear & apply credentials and configuration sync between Codex, OpenCode and other supported clients, preserving unrelated settings and reporting unsupported translations.
- Updated direct ChatGPT plan login, callback/refresh recovery, chat-model selection, consent confirmation and usage-limit reporting. No Codex process or automatic API-billing fallback is used.
- Expanded prefab inspection/reuse, bounded file reads, tool-result handling and agent recovery; added Chat/Plan/Agent modes and provider-specific reasoning controls.
- Validated in an isolated Unity 6000.6.7f1 project: 152 self-tests passed, 0 failed; rendered streaming layout passed. Claude/Gemini inference uses localhost protocol mocks; official Claude Code login/launch remains a real-client check.

## 2.1.0

- Replaced the chat transcript with a UI Toolkit `ListView` using dynamic item heights; added a conversation sidebar with search, new chat, rename, delete and clear history.
- Added paged on-disk history (50 messages per page), lazy older-page loading, crash-recovery checkpoints and one-time migration of legacy `chat-<provider>.json` sessions.
- Added a separate capped model context (120 messages) with recent-turn selection and a local older-context summary, plus cached project context and tool-based file reads.
- Throttled streaming UI updates to roughly 10–15 FPS and avoided rebuilding the transcript for each token.
- Added response modes (Concise/Balanced/Detailed), a separate output-token setting, Markdown with syntax-highlighted code blocks, structured file cards and compact tool groups.
- Added history, context, Markdown, UI-shell, activity/file-card and 100/500/1000-message load benchmarks to the self-test suite (44 tests total).
- Documented the UI Toolkit shell, legacy tabs and remaining UI automation limitations.

## 2.0.0

- Rebranded as **Unity AI Bridge** and removed project-specific content and terminology.
- Neutral, project-agnostic assistant system prompt; the assistant inspects each project dynamically.
- Generalized material creation and project analysis for any Unity project and render pipeline.
- Added automatic migration for prior settings, credentials, conversations, pending changes, backups and test artifacts.
- Renamed namespaces, classes, editor window, menu, MCP server name and configuration identifiers.
- Preserved ChatGPT OAuth client registrations and credentials; new registrations use the new agent name.
- Added OpenCode Go provider support (OpenAI-compatible chat completions, model discovery, required session header).
- Redesigned the Editor window with a compact sidebar, grouped Project tabs, simpler chat options and an MCP dashboard.
- Added one-click MCP client installers (Codex, OpenCode, Cursor, VS Code, Claude Code), backups and automatic MCP start/stop.
- Added an MCP server catalog: Context7, GitHub, MCP for Unity (Coplay), MCP for Blender and Playwright, with API-key/OAuth setup.
- Added windowed chat history rendering with scroll-up loading, reducing Editor lag on long conversations.
- Added privacy cleanup across documentation, tests, examples and UI strings.

## 1.0.0

- Initial public release with live chat, ChatGPT OAuth, DeepSeek, OpenAI API, Ollama, project tools, MCP, permissions and self tests.
