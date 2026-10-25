# Changelog

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
