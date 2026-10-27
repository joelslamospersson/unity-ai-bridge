# ChatGPT login and chat repair — 2026-10-26

Implemented in `Assets/UnityAIBridge`.

## Fixed

- OAuth kept creating abandoned listener accepts once per second. A single cancellable accept now stays pending while browser consent completes; unrelated requests and stale tabs do not swallow the real callback.
- Rejected access tokens could bypass forced refresh because their expiry had not passed. Forced renewal now respects rejection and serializes refresh-token rotation.
- Temporary renewal errors could erase a recoverable session. Only invalid credentials clear it. Sign-out clears credentials immediately and wins over in-flight renewal.
- Truncated Responses streams could be treated as successful. Success now requires `response.completed`.
- A stalled stream could ignore Stop after headers arrived. Cancellation now closes the underlying response stream.
- Reasoning controls previously reused DeepSeek settings and were not sent to OpenAI. ChatGPT/OpenAI now send reasoning effort and request summaries; encrypted reasoning is preserved between tool turns.
- An older model-list request could change connection state after switching providers. Requests now carry a generation and stale results are discarded.
- Scrolling upward could keep following the newest message. Following now pauses correctly, including already-scheduled automatic scrolling.
- Sending before setup could erase a draft. The composer only clears after the service accepts a request; it also retains unsent drafts across conversation switches in the current domain.

## Added

- Chat, Plan, and Agent modes, with runtime enforcement of the offered tools.
- Sign-in, cancel, and reopen-browser controls directly in the chat panel.
- Reasoning effort, elapsed progress, separate thinking summaries, and retry from recorded results.
- Interrupted tool-call history repair and context retention at complete user-turn boundaries.

## Verification

Unity 6000.6.7f1 batch compilation and integration suite: **59 passed, 0 failed**. The suite uses localhost mocks and an isolated project; no production account credentials were used. Native UI construction and controls are checked, but this run does not automate pixel layout or mouse/keyboard interaction. Browser consent and a real account's inference response require the user's sign-in.

## Design and protocol references

- [OpenAI: registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [OpenAI: models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [OpenAI: plan usage requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
- [Claude: model, effort, and thinking controls](https://support.claude.com/en/articles/8664678-change-the-model-effort-and-thinking-settings)

The interaction uses these patterns: a visible account connection, model and effort selection, persistent conversations, streamed progress, expandable thinking summaries, and a distinct tool-execution mode. It is a native Unity implementation.
