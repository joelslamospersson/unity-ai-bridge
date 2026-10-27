# Setup, providers and authentication

Open **Tools → Unity AI Bridge → Settings** (or the gear in the chat header).

Credentials and bridge state live outside the Unity project:

```
~/.config/unity-ai-bridge/<project-id>/secrets.json   (0600)
```

`<project-id>` is a stable hash of the Unity project path, so separate projects have separate settings and conversations. On Windows, `%APPDATA%\unity-ai-bridge\` is used instead of `~/.config`.

Existing installations are migrated automatically from the previous configuration directory. Settings, credentials, pending changes, conversations and logs are preserved; backups are migrated from the previous plugin library folder to `Library/UnityAIBridge` by the bridge. No manual copy is required.

## DeepSeek

1. Create an API key at <https://platform.deepseek.com/api_keys>.
2. Provider: **DeepSeek**.
3. Paste the key in the account bar and press **Save & test** (or Settings → Save key → Test connection).
4. Choose a model from the toolbar. Models are discovered from `GET https://api.deepseek.com/models`; verified fallbacks are `deepseek-flash` (DeepSeek-V4.1-Flash) and `deepseek-v4-pro` (DeepSeek-V4-Pro).
5. Send a request. The assistant streams reasoning (when thinking is on), text, tool calls and a final answer.

Settings:

- **Base URL**: `https://api.deepseek.com`
- **Thinking**: enabled by default; effort `low`, `high`, or `max`.
- **Max output tokens**: default 16384 (the API allows larger values).
- Tool calls and thinking mode are supported together. DeepSeek requires previous `reasoning_content` to be replayed when tools are present; the bridge does this automatically.

Usage and cost:

- The chat shows prompt/completion/reasoning tokens and an estimated USD cost.
- Rates used (per 1M tokens, public rates at implementation time):
  - `deepseek-flash`: cache hit `$0.003`, cache miss `$0.15`, output `$0.60` off-peak; peak is double.
  - `deepseek-v4-pro`: cache hit `$0.022`, cache miss `$0.66`, output `$1.98` off-peak; peak is double.
- Peak: Monday–Friday, 01:00–04:00 and 06:00–10:00 UTC. Weekends are off-peak. The display is labeled an estimate; confirm current prices on the official pricing page.

Errors are surfaced as readable messages: 401 (bad key), 402 (insufficient balance), 429 (rate limit), 400 (request/tool/thinking constraints), and connection failures.

## OpenCode Go

OpenCode Go is an OpenCode subscription that provides an OpenAI-compatible API for coding models. It is not OpenAI or DeepSeek API billing; usage and limits follow your OpenCode plan.

1. Create a key at <https://opencode.ai/auth>.
2. Provider: **OpenCode Go**.
3. Paste the key in the account bar (or Settings) and press **Save & test**.
4. Select a model. Models are discovered from the base URL's `/models` endpoint.

Defaults and endpoints:

- Base URL: `https://opencode.ai/zen/go/v1`
- Alternate endpoint: `https://opencode.ai/inference/go/openai/v1` (used if the default is unavailable; you can set it manually)
- Chat endpoint: `/chat/completions`
- The bridge sends the required `x-opencode-session` header automatically for each conversation.
- Common models: `deepseek-v4.1-flash`, `deepseek-v4-pro`, `kimi-k3`, `glm-5.3`, `mimo-v2.6-flash`, and others listed by `/models`.

If you previously pasted an OpenCode Go key into the **DeepSeek** provider, it will be rejected by `api.deepseek.com`. Switch to **OpenCode Go** and use **Reuse the key saved for DeepSeek** (or paste it again). The DeepSeek provider is only for keys created at `platform.deepseek.com`.

Some OpenCode Go models use a Responses endpoint (for example GPT/Grok entries) and are not available through this chat-completions provider. Web search/model catalogs can change; the model dropdown reflects the live `/models` response.

## ChatGPT Plus / Pro (subscription login)

Eligible Plus and Pro subscribers can use their ChatGPT plan directly in Unity AI Bridge, without installing Codex or supplying an OpenAI API key. Unity AI Bridge uses OpenAI's official **open-source Sign in with ChatGPT** flow. It performs OAuth 2.0 Authorization Code + PKCE with a local loopback callback and validates the OIDC ID token against OpenAI's JWKS. It does not use browser cookies, scraping, or unofficial endpoints.

### Flow

1. Choose **ChatGPT Plus / Pro**, or press **Continue with ChatGPT** in Chat, MCP, or Settings. The button selects the ChatGPT subscription provider automatically.
2. Sign in with your ChatGPT account, select its workspace, and allow Unity AI Bridge to use your ChatGPT plan. You return through the local callback to Unity chat. Press **Continue with ChatGPT** directly above the conversation to begin. Use **Open sign-in page** if the browser needs to be reopened, or **Cancel sign-in** to cancel the attempt.
3. The extension:
   - starts a callback listener on `http://127.0.0.1:1455/auth/callback` (ports 1455–1465 are tried),
   - generates fresh `state`, OIDC `nonce`, and PKCE verifier/challenge,
   - persists a stable `ext_agent_host_id` (`urn:uuid:...`),
   - opens the system browser at `https://auth.openai.com/api/accounts/authorize` with:
     - `client_id=dynamic_agent_client` on first registration (or the issued `oaiapp_...` client ID afterwards),
     - `agent_name_hint=Unity AI Bridge` on first registration,
     - `scope=openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`,
     - `resource=https://api.openai.com/v1`,
     - `code_challenge_method=S256`.
4. OpenAI redirects to the loopback callback with `code`, `state` and the issued `client_id`.
5. The extension validates `state`, exchanges the code at `https://auth.openai.com/api/accounts/oauth/token`, and validates the ID token signature, issuer, audience, expiry and nonce.
6. It checks for `chatgpt.tokens.use.direct`. The browser only displays **ChatGPT connected** after tokens and identity are verified and plan permission is present. Unity focuses the chat and shows **Using ChatGPT plan**, a one-time dismissible confirmation, and **Manage usage**. If the scope is missing, you are signed in but **not connected** for inference; choose **Enable plan usage** to reopen consent.
7. Models are loaded from `GET https://api.openai.com/v1/models` using the OAuth token. The dropdown reflects the account.
8. Chat uses `POST https://api.openai.com/v1/responses` with `store:false`, `stream:true`, tools via an `additional_tools` input item, and no unsupported fields (`temperature`, `max_output_tokens`, etc. are omitted on this route).

### Chat models and conversation history

This provider calls OpenAI directly using the bridge's own chat and Unity tool loop. It does not start Codex. Its model picker excludes Codex-labelled models from both fresh account catalogs and cached catalogs. When choosing an initial/default model, it prefers an available GPT-6 chat model; it never invents access to a model absent from the account's model list. Other exposed chat models remain selectable.

Sign in with ChatGPT grants eligible plan inference and account identity, **not access to existing ChatGPT conversations or other account context**. Conversation history in this provider belongs to Unity AI Bridge. Existing ChatGPT website conversations are not synchronized by signing in. See the [official plan-usage overview](https://developers.openai.com/siwc/token-sharing-open-source).

The **Codex** row in the MCP tab configures the external Codex client. It is separate from this direct ChatGPT provider.

### Tokens, renewal and sign-out

- Access tokens last one hour; refresh tokens last 30 days and rotate on refresh.
- Refresh is automatic near expiry and serialized. A 401 forces renewal even when the saved expiry is still in the future. Temporary server errors preserve the saved session; invalid credentials require sign-in again.
- **Sign out** clears local tokens immediately, prevents an in-flight refresh from restoring them, and revokes the renewable session through the OIDC discovery `revocation_endpoint` when reachable. The issued client ID is retained for a later sign-in.
- **Reconnect** reuses the client ID and an `id_token_hint`.
- Tokens are stored in `secrets.json` (0600) and are never written to chat logs or sent anywhere except `auth.openai.com` / `api.openai.com`.

### Limits, eligibility and branding migration

- This flow is for **open-source clients**. Eligibility is controlled by OpenAI and may depend on account, workspace or region. The UI reports those errors; it never fakes a connection.
- **ChatGPT plan usage is not unlimited API access.** Plus usage shares a 5-hour limit across apps using the ChatGPT plan; Pro has no 5-hour limit. Users can review limits at <https://chatgpt.com/settings/usage>.
- The bridge does not start a Codex process. That implementation detail does not guarantee that OpenAI meters an independent allowance. OpenAI controls shared plan and per-app limits; inspect ChatGPT usage settings for the limits that apply to the account. See [accounts and usage](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions).
- If plan usage is unavailable, use **OpenAI API** with a platform key (separate billing) or another provider.
- Existing installations keep their issued OAuth client ID, tokens and account mapping. The new agent name is used for **new** registrations. An existing ChatGPT registration may continue to display its original name until you disconnect/reconnect the app or update it in ChatGPT settings. Signing out is not required for the upgrade.

Official references:

- Overview / OSS plan usage: <https://developers.openai.com/siwc/token-sharing-open-source>
- Registration and sign-in: <https://developers.openai.com/siwc/token-sharing-open-source/sign-in>
- Models and inference: <https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference>
- Token reference: <https://developers.openai.com/siwc/token-sharing-open-source/token-reference>
- Errors and recovery: <https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery>
- Preview limitations: <https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations>

## OpenAI API

Provider: **OpenAI API**.

- Base URL: `https://api.openai.com/v1`
- Paste a platform key from <https://platform.openai.com/api-keys> and save.
- Models are discovered from `/models`.
- Uses the Responses API with streaming and function tools.
- **ChatGPT plans do not include API usage.** Tokens are billed by OpenAI to the platform account.

## Ollama

Provider: **Ollama**.

- URL: `http://127.0.0.1:11434`
- Install a model with `ollama pull <model>`.
- Models are discovered from `/api/tags`.
- Streaming and function calling depend on the model; models without tool support answer in text only.

## Model selection

- The toolbar dropdown is populated from the provider's model list. Empty lists are reported as errors, and older requests cannot overwrite results after switching connections.
- The selection is saved per provider and per project.
- **Refresh** reloads models; if the current model disappears, the first available model is used.
- ChatGPT models require a successful sign-in with plan usage granted.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| DeepSeek 401 | Wrong/revoked key; paste a new one |
| DeepSeek 402 | Insufficient balance at platform.deepseek.com |
| DeepSeek 429 | Rate limit; retry later |
| DeepSeek 400 with thinking/tools | Disable thinking or switch model; the error body is shown |
| ChatGPT sign-in does not open | Check the system default browser |
| Callback timeout | A firewall/security tool may block `127.0.0.1:1455-1465` |
| ChatGPT says plan usage not granted | Press **Reconnect & allow plan usage**, or use OpenAI API |
| ChatGPT 401 | Token expired and refresh failed; sign in again |
| ChatGPT 429 / `subscription_sharing_usage_limit_exceeded` | Shared plan limit reached; check ChatGPT Settings → Usage |
| ChatGPT 403 / `subscription_sharing_user_not_eligible` | Account/workspace/region not eligible for plan usage |
| OpenCode Go 401 | Use a key from opencode.ai/auth; a DeepSeek key will not work. Switch provider and reuse the saved key |
| OpenCode Go 404/model error | Verify the base URL and model; some OpenCode Go models use a Responses endpoint and are not supported here |
| “No model” | Refresh models, sign in, or check the API key |
| MCP port in use | Change the MCP port in the MCP tab |
| No Unity recompile | Focus the Editor or press Refresh in Explorer |

Reset bridge settings/secrets with **Tools → Unity AI Bridge → Reset Settings** (pending changes and backups are kept).

## What is verified by the automated tests

- DeepSeek/OpenAI/ChatGPT request building, streaming parsing, tool-call assembly and the tool loop are verified against local mock HTTP servers.
- PKCE, authorization URL parameters, ID-token signature/nonce/audience validation and refresh-token exchange are verified locally.
- A real ChatGPT sign-in, real DeepSeek inference, real OpenAI API inference, and real Ollama inference require your credentials/accounts and are not claimed as pre-tested.

## Chat and reasoning controls

Use Chat, Plan, or Agent beside the composer. Chat offers no tools; Plan offers inspection tools; Agent offers project tools subject to the selected permissions. Tool calls outside the selected mode are rejected by the runner.

ChatGPT/OpenAI reasoning is configured independently of the DeepSeek settings and sent through the Responses API's `reasoning` field. Summaries and encrypted reasoning items are retained for stateless tool continuations. The plan usage route still omits unsupported parameters, including `temperature` and `max_output_tokens`.

A model list verifies catalog access. A reply is successful only after the Responses `response.completed` event; truncated streams and provider failures remain errors even if some text arrived. Browser consent and live model access depend on your signed-in account.


## Claude API

Choose **Claude API**, then open **Account & settings**. Enter an API key from the [Anthropic Console](https://platform.claude.com/settings/keys) and press **Save & test**. This saves the masked key outside the project and retrieves the account's paginated model catalog. Choose a discovered model in Chat. **Clear key** removes the saved API key. The default API base URL is `https://api.anthropic.com/v1`.

Claude API supports streaming text, thinking when enabled for a compatible model, native function tools and usage counts. Signed thinking blocks and tool-use IDs are retained for tool-result continuations. **Off** disables optional thinking; Low/Medium/High selects effort on adaptive-thinking models and enables budgeted thinking on older supported models. Model availability and thinking support are controlled by Anthropic. API usage is billed separately from Claude Pro/Max subscriptions. See the official [Messages streaming contract](https://platform.claude.com/docs/en/build-with-claude/streaming).

## Claude subscription (external)

Anthropic's [third-party integration guidance](https://code.claude.com/docs/en/agent-sdk/overview) requires prior approval for third-party Claude.ai subscription login. This bridge therefore offers a handoff to the **official Claude Code client**, rather than importing its subscription tokens into native Unity chat.

1. [Install Claude Code](https://code.claude.com/docs/en/quickstart). Set **Claude executable** in Account & settings if `claude` is not on the Unity Editor's PATH.
2. Choose **Claude subscription (external)** and press **Sign in to Claude Code**. The official CLI runs `claude auth login` and owns the browser sign-in and credential storage. If browser sign-in needs a pasted code, complete `claude auth login` in a terminal.
3. Press **Install Unity Bridge for Claude Code** to start the local MCP server and add it to Claude Code's project configuration.
4. Press **Open Claude Code** to work in the official client with its subscription and Unity MCP tools. Linux requires a supported terminal emulator; on macOS, run the copied `claude` command in the opened Terminal.

The Unity chat composer is disabled for this external provider. Use **Claude API** for streamed replies inside Unity. The handoff does not translate subscription tokens into API credentials, and it removes inherited API/cloud billing environment overrides from the processes it starts. Subscription eligibility and limits follow the official client's account and policy. See [Claude Code authentication](https://code.claude.com/docs/en/authentication).

## Gemini API

Choose **Gemini API**, then open **Account & settings**. Get a key from [Google AI Studio](https://aistudio.google.com/apikey), paste it in the masked field, and press **Save & test**. Select a discovered generation model in Chat. The default base URL is `https://generativelanguage.googleapis.com/v1beta`. Keys are sent in the `x-goog-api-key` header, not in the request URL.

Gemini supports streaming text, thought summaries on compatible models, native function tools and usage counts. Thought signatures and function-call IDs are preserved when returning tool results. **Default** leaves thought-summary behavior to the model; **Summaries** requests them on supported Gemini models. API usage follows Google API billing and quotas, separately from Gemini app subscriptions. See the official [Gemini generation API](https://ai.google.dev/api/generate-content).

Claude and Gemini models are discovered from each account; no model or entitlement is invented when discovery fails. The automated tests use localhost fixtures. Live authentication/inference and the external Claude Code handoff require the user's installed client and accounts and have not been exercised in this run.
