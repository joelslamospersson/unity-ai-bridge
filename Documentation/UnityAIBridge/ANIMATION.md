# Animation authoring

The agent and MCP clients can now create animation assets through the same staged approval flow as other project edits. These tools use Unity's [float-curve API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AnimationUtility.SetEditorCurve.html), [sprite/object-reference curve API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AnimationUtility.SetObjectReferenceCurve.html) and [Animator Controller API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Animations.AnimatorController.CreateAnimatorControllerAtPath.html).

## Keyframed clips

Example `create_animation_clip` arguments:

```json
{
  "path": "Assets/Animations/Hover.anim",
  "frame_rate": 30,
  "loop": true,
  "curves": [
    {
      "path": "Visual",
      "property": "m_LocalPosition.y",
      "interpolation": "smooth",
      "keys": [
        { "time": 0, "value": 0 },
        { "time": 0.5, "value": 0.2 },
        { "time": 1, "value": 0 }
      ]
    }
  ]
}
```

Bindings are relative to the Animator root; an empty binding path targets that root. Supported transform properties are `m_LocalPosition.x/y/z`, `m_LocalScale.x/y/z` and `localEulerAnglesRaw.x/y/z`. Interpolation can be `linear`, `constant` or `smooth`. Times are seconds, strictly increasing from zero or later, up to 3600 seconds. Clips accept at most 64 curves, 1000 keys per curve and 4096 total keys. Sample rate is 1–240 FPS.

For sprites, supply `sprite_target` (relative SpriteRenderer path) and `sprite_frames` entries containing `time`, `sprite_path` and optional `sprite_name`. Multi-sprite sheets require an exact subasset name from `inspect_asset`. Frame references persist as Unity object references. Include a final repeated frame key if it must be held through a particular ending time; the tool preserves the supplied timing.

Approval previews show the complete declarative specification. Validation checks destination permissions, existing files/meta files, relative bindings, finite values, ordering and source sprite identities before staging and again when applied. No animation events or model-supplied scripts are executed.

## Controllers

Apply clip creation first, then call `create_animator_controller`:

```json
{
  "path": "Assets/Animations/Character.controller",
  "default_state": "Idle",
  "states": [
    { "name": "Idle", "clip": "Assets/Animations/Idle.anim" },
    { "name": "Move", "clip": "Assets/Animations/Move.anim" }
  ],
  "transitions": [
    { "from": "Idle", "to": "Move", "exit_time": 1, "duration": 0.15 }
  ]
}
```

State names must be unique. Timed transitions use normalized `exit_time` (default 1 cycle) and a fixed duration in seconds (default 0.1). The controller accepts at most 64 states and 128 transitions. After approval, assign it to an existing Animator with `update_component`, or add an Animator through the existing tools. The controller tool does not modify scene objects automatically.

### Parameters, conditions and 1D blend trees

Controllers accept up to 32 `parameters` with `name`, `type` (`Float`, `Int`, `Bool`, `Trigger`) and an optional typed `default`. Numeric values are bounded to ±1,000,000; integer parameters require integer defaults and thresholds.

Each transition may include up to 16 `conditions`. Conditions name a `parameter` and compatible `mode`: Float supports `Greater`/`Less`, Int also supports `Equals`/`NotEqual`, Bool supports `If`/`IfNot`, and Trigger supports `If`. Numeric conditions require `threshold`. Conditions are combined by Unity as an AND requirement. `has_exit_time` defaults to false with conditions and true without them; a transition must have conditions or an exit time.

A state may replace `clip` with `blend_tree`, containing `parameter` (an existing Float parameter), optional `type: "1D"` and 2–32 `children`. Children contain an existing `clip` and strictly increasing `threshold`. The tree is saved as a subasset of its controller; it does not create separate orphan assets. `inspect_asset` returns typed parameters, state motions, blend children and transition conditions within bounded output limits.

Existing `.anim` and `.controller` assets are never overwritten by these creation tools. Editing an existing asset, a visual timeline, 2D/nested blend trees, humanoid rigging and retargeting remain outside this tool surface. Existing Unity animation tools remain available for those workflows.
