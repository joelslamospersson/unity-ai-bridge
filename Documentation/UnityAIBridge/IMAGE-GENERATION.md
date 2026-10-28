# Generate artwork

Open **Tools → Unity AI Bridge → Generate Image**, or **Project → Assets → Generate image**. The generation window uses UI Toolkit and keeps previews separate from project imports.

1. Enable image generation.
2. Save an OpenAI API key, or reuse the key already saved for the OpenAI API provider. Credentials remain in the bridge's private configuration, outside the project.
3. Select an image model available to that API account. The initial model is `gpt-image-2.5-sunburst`; access is controlled by the provider.
4. Describe the artwork, choose a new `.png` destination and set size, quality and background.
5. Authorize charges for one image, then press **Generate one image (API billing)**.
6. Inspect the preview and review its import in Changes. Normal project approval/auto-apply rules govern the import.

The request uses OpenAI's [image generation endpoint](https://developers.openai.com/api/reference/resources/images/methods/generate), with PNG output and one image per request. App options are sizes `1024x1024`, `1536x1024` and `1024x1536`; qualities `auto`, `low`, `medium`, `high`; and backgrounds `transparent`, `opaque`, `auto`. These are a bounded subset of the [provider's image options](https://developers.openai.com/api/docs/guides/image-generation). PNG transparency is preserved and configured for sprite import.

This is OpenAI API billing, separate from ChatGPT plan image credits. The feature starts disabled. The native form requires a cost decision for each Generate click. Agent/MCP `generate_image` requests pause for an explicit in-editor billing decision, even when conversation auto acceptance is enabled. Cost consent and importing a generated image are separate decisions.

## Agent and MCP tool

Example arguments:

```json
{
  "prompt": "A small painted woodland shield icon, centered, transparent background",
  "path": "Assets/Art/Generated/WoodlandShield.png",
  "size": "1024x1024",
  "quality": "medium",
  "background": "transparent"
}
```

The bridge checks configuration, destination, prompt and options before requesting consent. It checks them again before submitting the request. An edited billing route/model/key or changed request requires another review. Tools run asynchronously, leaving the Editor responsive. Denying or commenting sends no image request.

The response must contain base64 PNG data; URL-only responses are rejected. PNGs are limited to 8 MB and edges up to 4096 pixels; the JSON response is capped at 16 MB. Previews are retained under `Library/UnityAIBridge/incoming`, while sprite imports remain staged until allowed by the project's approval policy. Creation refuses to overwrite existing files or `.meta` identities.

If the destination changes while generation is running, the paid preview is retained and the result reports `GeneratedNeedsImport`. The form offers **Stage this preview at the new destination**. An agent can reuse the returned preview with `import_sprite`; another paid generation is unnecessary.

## Cancellation and recovery

Stop cancels the local request and does not stage a new asset. A provider may already have processed a submitted request. No timeout, rate-limit error, cancellation or Unity reload automatically repeats paid generation. Submission is recorded before sending so recovery cannot silently bill a second request. After an interrupted submission, inspect pending imports/retained previews and the API account's usage before explicitly generating again.

Tests use localhost fixtures, including transparent PNG imports, denial, option changes, malformed payloads, collisions, provider errors, cancellation and reload replay guards. This release does not claim live paid generation or evaluate generated artwork quality. Image editing, inpainting and batch generation are not yet exposed.
