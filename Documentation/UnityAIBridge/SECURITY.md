# Security and permissions

The bridge is designed so that an AI can be useful **without** being trusted with the machine.

## Permission levels

| Level | Read | Stage | Apply | Notes |
| --- | --- | --- | --- | --- |
| Read Only | yes | no | no | Chat/inspection still work |
| Ask Before Editing (default) | yes | yes | only after human approval | Every write is a diff in Pending Changes |
| Project Write Access | yes | yes | after approval; optional auto-apply for non-destructive changes | Destructive changes always need confirmation |

Checks happen twice: when a change is staged and again when it is applied. Downgrading to Read Only immediately blocks pending work.

## Filesystem sandbox

All paths pass through `BridgePaths.TryResolve`:

1. Normalise `\` to `/`, strip `res://`, reject null bytes.
2. Build an absolute path from the project root and verify it is inside the project.
3. Canonicalise the nearest existing ancestor with `realpath(3)` (Linux/macOS) and re-check containment. A symlink that points outside the project is rejected.
4. Deny dangerous roots for reads and writes: `.git`, `.plastic`, `.vs`, `.idea`, `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/`, `Build/`, `Builds/`, `MemoryCaptures/`.
5. Deny sensitive names: `id_rsa*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `credentials*`, `secrets*`, `.env`, `*.token`, `*password*`, `*.kdbx`, `keystore`, `.npmrc`, `.netrc`, and any `.meta` file.
6. Writes are **only** allowed inside `Assets/`, and only for an extension allowlist (`.cs`, `.txt`, `.json`, `.md`, `.shader`, `.compute`, `.hlsl`, `.asmdef`, `.uxml`, `.uss`, `.xml`, `.yaml`, `.csv`, `.inputactions`, …). Binary assets can only arrive through the sprite importer.
7. The bridge's own source (`Assets/UnityAIBridge/`) is write-protected unless you enable “Allow assistant to modify the bridge's own source”.
8. Deleting/moving uses `AssetDatabase.DeleteAsset`/`MoveAsset` for assets so `.meta` GUIDs and references stay intact. The bridge never writes `.meta` files directly.

On Windows there is no `realpath`; the code falls back to `Path.GetFullPath` plus `FileAttributes.ReparsePoint` checks. Linux is the tested platform.

## What the assistant cannot do

- Read outside the Unity project (home directory, SSH keys, browser data, other projects).
- Read `.git`, `Library`, `Temp`, `Logs`, `UserSettings`, or secret-looking files.
- Write outside `Assets/` (no `ProjectSettings`, `Packages`, build scripts, or the OS).
- Run shell commands, spawn processes, install packages, or open arbitrary URLs.
- Delete or overwrite without an explicit confirmation and a backup.
- Apply a change that a human has not approved (unless you deliberately enabled auto-apply in Project Write Access, and even then destructive changes are excluded).
- Learn your API key: keys are stored in `secrets.json` (0600) and are only sent to the selected provider as an HTTP `Authorization` header. Project context is redacted for `sk-…`, bearer tokens and `api_key=/token=/password=` patterns before it is sent.

## Backups

Before a file is overwritten or deleted, it is copied to:

```
Library/UnityAIBridge/Backups/<timestamp>-<random>/<original project path>
```

`Library/` is git-ignored and local. Restore from **Pending Changes → Restore backup**. Backups are never uploaded. Backups are not made for newly created files (nothing to lose).

## Undo

Scene operations use Unity's Undo system (`Undo.RegisterCreatedObjectUndo`, `Undo.RecordObject`, `Undo.AddComponent`, one group per change), so Ctrl+Z reverts them. File writes use backups instead, because Undo does not cover the filesystem.

## AI-written code

Approving a `.cs` change triggers a clear warning: Unity compiles the script and may execute editor code (for example `[InitializeOnLoad]`, static constructors, `AssetPostprocessor`). The assistant cannot run code directly through the bridge; compilation is the user's approval decision. Read the diff.

## AI credentials and OAuth tokens

- DeepSeek and OpenAI API keys live in `secrets.json` (0600) outside the project. They are sent only as the `Authorization: Bearer ...` header to the selected provider.
- Sign in with ChatGPT uses OAuth 2.0 Authorization Code + PKCE, not cookies or scraping. The bridge never sees your ChatGPT password.
  - Authorization happens in your system browser at `https://auth.openai.com`.
  - The local loopback callback is bound to `127.0.0.1` on ports 1455–1465 and validates the OAuth `state`.
  - The OIDC ID token is validated (RS256 signature against OpenAI JWKS, issuer, audience, expiry, nonce) before the connection is marked successful.
  - Access token (1 h), rotating refresh token (30 days) and the retained ID token are stored in `secrets.json` (0600).
  - Refresh is serialized and persisted atomically; a failed refresh clears unusable tokens and asks you to sign in again.
  - Sign out revokes the renewable session through the OIDC discovery `revocation_endpoint` when reachable, then clears local tokens. The issued client ID is retained for a later sign-in; tokens are not.
  - Authorization URLs (which may contain an `id_token_hint`) and tokens are never logged or included in chat history.
- ChatGPT plan usage is governed by OpenAI's eligibility and shared usage limits. It is not API billing, and the bridge does not treat it as unlimited API access.
- If plan usage is not granted, the provider is not considered connected, even if identity sign-in succeeded.

## MCP server

- Binds to `127.0.0.1` only, never `0.0.0.0`.
- Requires a 256-bit random bearer token. Comparison is constant-time; regenerate it in the UI to revoke clients.
- Read tools can expose project source to any local process that has the token; treat the token like a password.
- Write tools only stage changes. `apply_approved_changes` applies only human-approved changes.
- If you expose it through a tunnel, the tunnel/provider becomes part of the trust boundary. Prefer header auth over URL tokens; rotate after use.
- MCP output/logs are recorded in `bridge.log`; they contain file paths and tool names, not secrets.

## Limits and residual risks

- **Prompt injection**: project files, asset names or console output could try to manipulate the assistant into proposing a harmful change. The diff/approval gate is the defence; auto-apply removes it, so keep auto-apply off.
- **Same-user local attacker**: a process running as your user can read `secrets.json`, the project and the MCP token. OS account separation is required for stronger isolation.
- **Secrets at rest** are protected by file permissions, not encryption. Encrypt your home directory/disk if that matters.
- **Clipboard**: “Copy token” and copy buttons put data in the system clipboard; other applications can read it.
- **Third-party providers** receive the project context sent with each request. Do not put secrets in project notes or prompts; the bridge redacts common token/key patterns before sending tool output, but cannot guarantee every secret format.
- **Undo vs. file edits**: file-level changes are not undone by Ctrl+Z; use the backup.
- **ChatGPT connectors** are controlled by OpenAI. Adding a custom MCP connector may be unavailable on your plan/workspace; the bridge does not bypass that.

## If something goes wrong

1. Switch the access level to **Read Only**.
2. Stop the MCP server and regenerate its token.
3. Revoke the API key at the provider and clear it in Settings.
4. Use **Pending Changes → Restore backup**, or `git diff` / `git checkout` for tracked files.
5. Review `~/.config/unity-ai-bridge/<project-id>/bridge.log` for the action history.
6. Delete `~/.config/unity-ai-bridge/<project-id>/` to remove keys, history and pending changes (backups stay in the project).
