# Thinking summary and approval flow — 2026-10-26

The thinking summary now has a separately scrollable viewport that grows with its content up to a limit based on the chat height. Its full text remains selectable and never shrinks into a recycled row. Expanding/collapsing it invalidates cached list-row heights. This prevents long summaries from being cut off while keeping the answer and composer reachable.

Agent-mode write tools now wait for an in-chat review instead of ending the request with manual approval instructions:

- **Accept & apply** approves and applies only the change IDs displayed in the prompt. The model receives actual application results and continues the same task.
- **Deny** leaves the proposal unapplied and lets the agent continue using a permitted approach.
- **Send comment** returns feedback for revision without granting permission or applying the proposal.
- **Stop / Esc** explicitly cancels the active run and any approval wait. Closing/reopening the bridge window does not deny the request.

Read Only first asks for a one-time grant to stage/manage the specific proposal. Actual edits then require review of the resulting changes. The global access level is restored immediately after each scoped operation. Destructive changes require an explicit confirmation checkbox. An unchanged denied operation is not re-prompted during the same run/retry.

There is no approval timeout and no model connection held open while the user decides. The approval journal and conversation checkpoint survive Unity assembly reloads. Applying a script checkpoints the tool result before releasing the assembly-reload lock; afterward the bridge resumes from recorded results, without reapplying the reviewed changes or asking again. Only the specific reviewed IDs are approved; unrelated proposals are left pending.

Existing proposals can be reviewed with the `request_approval` tool using their exact IDs. The Changes tab remains available for manual review.

## Verification

- **91 passed, 0 failed** in the Unity 6000.6.7f1 integration suite, including Accept/Deny/Comment continuation, exact approval scope, Read Only restoration, cancellation, persisted prompts, destructive confirmation, and UI construction.
- **Rendered layout validation passed** at 680×620: a 160-line thinking summary scrolls to its final line, and the approval buttons remain inside the window.
- **Actual assembly-reload validation passed**: a localhost mock provider requested a script, approval applied it, Unity reloaded assemblies, and the conversation continued to its final answer. The saved model input retained one original user request and the matching applied tool result. No paid model inference was used.

The standalone layout check can be run on an isolated project using `-batchmode -executeMethod UnityAIBridge.SelfTests.RunChatLayoutValidation` (with graphics enabled). Its result is written to `Library/UnityAIBridge/chat-layout-result.json`.

The reload harness is `UnityAIBridge.ChatApprovalReloadValidation.Start`; it requires an isolated `/tmp/unity-bridge-*` project and a localhost Responses mock on port 7860. Its result is `Library/UnityAIBridge/approval-reload-result.json`.

Implementation follows Unity's [dynamic ListView layout](https://docs.unity3d.com/6000.0/Documentation/Manual/UIE-uxml-element-ListView.html) and [assembly reload control](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/EditorApplication.LockReloadAssemblies.html).

## Conversation auto acceptance and live transcript — 2026-10-27

The composer includes **Auto accept all for this conversation**. Enabling it accepts the current waiting prompt and all future permission/review prompts in that conversation, including destructive changes. Turn it off to resume individual review. The choice is stored in conversation metadata and survives script compilation and reopening the chat. New conversations start with it disabled; global access settings and unrelated pending proposals are preserved. Automatic decisions use the same approval journal, scoped application and reload checkpoint flow as manual acceptance.

The transcript uses normally laid out rows inside a vertical ScrollView, with history still loaded in pages. Streaming updates refresh every changed message, including an assistant followed by tool/status rows. Rows grow with output instead of depending on cached virtualized heights. Thinking starts expanded. Tool activity starts collapsed and opens only when the user expands it; that choice survives live updates. Both remain collapsible. Tool output is retained separately from the compact context sent to the model and displayed without the previous 220-character clipping. Output labels render literal, selectable text. Existing saved activity entries can only show the summary retained by earlier versions.

The rendered validation now grows a live answer, thinking summary and multi-line tool result while the answer is still streaming and before any approval prompt exists. It checks that the same message row grows, tool and thinking content reach their final lines, and approval controls and the composer fit in a 680×620 window.

Validation for this update: **99 passed, 0 failed** in an isolated Unity 6000.6.7f1 project; the rendered streaming layout check also passed. Results: `/tmp/unity-bridge-chat-fix/Library/UnityAIBridge/test-results.json` and `chat-layout-result.json`. No paid inference was used.


## Paid image-generation consent

Conversation auto acceptance applies to project permissions and changes. Image API billing consent is always explicit and is separate from importing the generated PNG. The consent journal records submission before the network request; a reload does not submit that paid request again. If submission was interrupted, review retained previews, pending imports and provider usage before requesting another generation.
