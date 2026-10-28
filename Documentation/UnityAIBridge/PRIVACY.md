# Privacy and repository history

Bridge credentials, conversations, approval journals and pending changes are stored outside the published plugin source. See [security and storage](SECURITY.md) for their locations and permissions. Generated-image previews use the project's `Library/UnityAIBridge/incoming` staging area; generated assets enter `Assets/` through the normal import approval flow.

The 2.4.0 code update publishes bridge source, tests and documentation. Validation uses disposable projects and localhost fixtures; live account credentials, conversation exports, private game assets and test logs are not part of the patch.

Updating the current repository tree does not remove contents from earlier Git commits. This release does not rewrite repository history and does not claim a comprehensive historical-content audit. Historical cleanup is a separate repository-maintenance operation with its own review and coordination requirements.

Provider availability, billing and account access are external operating constraints. They are documented in [setup](SETUP.md) and [image generation](IMAGE-GENERATION.md), rather than described as missing editor features.
