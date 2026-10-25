# Changelog

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
