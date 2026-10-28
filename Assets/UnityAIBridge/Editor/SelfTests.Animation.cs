using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static JsonValue AnimationFixture(string path)
        {
            return JsonValue.Obj("type", "create_animation_clip", "path", path, "frame_rate", 24, "loop", true,
                "curves", JsonValue.Arr(JsonValue.Obj("property", "m_LocalPosition.x", "interpolation", "linear",
                    "keys", JsonValue.Arr(JsonValue.Obj("time", 0, "value", 0), JsonValue.Obj("time", 1, "value", 4)))));
        }

        static void RejectsAnimation(Action action)
        {
            bool rejected = false;
            try { action(); } catch (BridgeException) { rejected = true; }
            RequireStream(rejected, "Invalid animation specification was accepted.");
        }

        static void RunAnimationTests()
        {
            string path = TestFolder + "/Motion.anim";
            FeatureCheck("animation_creation_requires_approval_and_previews_keys", () =>
            {
                var change = ActionRouter.StageAction(AnimationFixture(path), "selftest");
                _testChangeIds.Add(change.Id);
                RequireStream(change.Status == ChangeStatus.Pending && change.Diff.Contains("m_LocalPosition.x") && !File.Exists(BridgePaths.ToAbsolute(path)), "Animation was written before approval or has no preview.");
                RequireStream(ChangeService.Apply(change.Id) != null, "Unapproved clip was applied.");
                ChangeService.Approve(change.Id);
                RequireStream(ChangeService.Apply(change.Id) == null, "Approved clip failed to apply.");
            });
            FeatureCheck("animation_keyframes_loop_and_interpolation_persist", () =>
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                RequireStream(clip != null && clip.frameRate == 24 && AnimationUtility.GetAnimationClipSettings(clip).loopTime, "Clip settings were lost.");
                var binding = AnimationUtility.GetCurveBindings(clip).First(b => b.propertyName == "m_LocalPosition.x");
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                RequireStream(Math.Abs(curve.Evaluate(0.25f) - 1f) < 0.01f, "Curve was not linearly interpolated.");
                RequireStream(AssetService.InspectAsset(path, 1, 10)["animation"]["curve_count"].AsInt() > 0, "Animation inspection omitted keyframes.");
            });
            FeatureCheck("animation_rejects_overwrite_and_wrong_extension", () =>
            {
                RejectsAnimation(() => ActionRouter.StageAction(AnimationFixture(path), "selftest"));
                RejectsAnimation(() => ActionRouter.StageAction(AnimationFixture(TestFolder + "/bad.txt"), "selftest"));
            });
            FeatureCheck("animation_rejects_unsafe_paths_and_readonly", () =>
            {
                RejectsAnimation(() => ActionRouter.StageAction(AnimationFixture("../bad.anim"), "selftest"));
                RejectsAnimation(() => ActionRouter.StageAction(AnimationFixture("Assets/UnityAIBridge/bad.anim"), "selftest"));
                int saved = BridgeSettings.Current.AccessLevel;
                try
                {
                    BridgeSettings.Current.AccessLevel = BridgeSettings.AccessReadOnly;
                    RejectsAnimation(() => ActionRouter.StageAction(AnimationFixture(TestFolder + "/blocked.anim"), "selftest"));
                }
                finally { BridgeSettings.Current.AccessLevel = saved; }
            });
            FeatureCheck("animation_rejects_invalid_keys_bindings_and_bounds", () =>
            {
                var fixture = AnimationFixture(TestFolder + "/invalid.anim");
                fixture["curves"][0]["keys"][1]["time"] = 0;
                RejectsAnimation(() => ActionRouter.StageAction(fixture, "selftest"));
                fixture = AnimationFixture(TestFolder + "/invalid.anim");
                fixture["curves"][0]["property"] = "m_Script";
                RejectsAnimation(() => ActionRouter.StageAction(fixture, "selftest"));
                fixture = AnimationFixture(TestFolder + "/invalid.anim");
                fixture["curves"][0]["path"] = "../outside";
                RejectsAnimation(() => ActionRouter.StageAction(fixture, "selftest"));
                fixture = AnimationFixture(TestFolder + "/invalid.anim");
                fixture["frame_rate"] = 0;
                RejectsAnimation(() => ActionRouter.StageAction(fixture, "selftest"));
            });
            FeatureCheck("animation_apply_rechecks_destination_collision", () =>
            {
                string racePath = TestFolder + "/Appeared.anim";
                var change = ActionRouter.StageAction(AnimationFixture(racePath), "selftest");
                _testChangeIds.Add(change.Id);
                var existing = new AnimationClip { name = "Keep this asset" };
                AssetDatabase.CreateAsset(existing, racePath);
                AssetDatabase.SaveAssets();
                string guid = AssetDatabase.AssetPathToGUID(racePath);
                byte[] before = File.ReadAllBytes(BridgePaths.ToAbsolute(racePath));
                ChangeService.Approve(change.Id);
                RequireStream(ChangeService.Apply(change.Id) != null && AssetDatabase.LoadAssetAtPath<AnimationClip>(racePath) == existing && AssetDatabase.AssetPathToGUID(racePath) == guid &&
                    before.SequenceEqual(File.ReadAllBytes(BridgePaths.ToAbsolute(racePath))),
                    "Apply overwrote an asset created after staging.");
            });
            FeatureCheck("animation_sprite_subassets_and_frames_persist", () =>
            {
                string sheet = TestFolder + "/test_texture_moved.png";
                var sprites = AssetDatabase.LoadAllAssetsAtPath(sheet).OfType<Sprite>().OrderBy(s => s.name).ToArray();
                RequireStream(sprites.Length >= 2, "Sprite-sheet fixture is missing.");
                string spritePath = TestFolder + "/SpriteMotion.anim";
                var spec = JsonValue.Obj("type", "create_animation_clip", "path", spritePath, "sprite_target", "Visual",
                    "sprite_frames", JsonValue.Arr(JsonValue.Obj("time", 0, "sprite_path", sheet, "sprite_name", sprites[0].name), JsonValue.Obj("time", 0.5, "sprite_path", sheet, "sprite_name", sprites[1].name)));
                var change = ActionRouter.StageAction(spec, "selftest"); _testChangeIds.Add(change.Id);
                ChangeService.Approve(change.Id);
                RequireStream(ChangeService.Apply(change.Id) == null, "Sprite clip failed to apply.");
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(spritePath);
                var binding = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                RequireStream(binding.path == "Visual" && frames.Length == 2 && frames[1].value == sprites[1] && frames[1].time == 0.5f, "Sprite frames or subassets changed.");
                spec["path"] = TestFolder + "/ambiguous.anim";
                spec["sprite_frames"][0]["sprite_name"] = "";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
            FeatureCheck("animation_controller_states_default_and_transitions", () =>
            {
                string controllerPath = TestFolder + "/Motion.controller";
                var spec = JsonValue.Obj("type", "create_animator_controller", "path", controllerPath, "default_state", "Idle",
                    "states", JsonValue.Arr(JsonValue.Obj("name", "Idle", "clip", path), JsonValue.Obj("name", "Move", "clip", path)),
                    "transitions", JsonValue.Arr(JsonValue.Obj("from", "Idle", "to", "Move", "exit_time", 1, "duration", 0.2)));
                var result = McpTools.InvokeOnMain("create_animator_controller", spec);
                RequireStream(!result["isError"].AsBool(), result.ToJson(false));
                string id = result["structuredContent"]["id"].AsString(); _testChangeIds.Add(id);
                RequireStream(!File.Exists(BridgePaths.ToAbsolute(controllerPath)), "Controller was written before approval.");
                ChangeService.Approve(id);
                RequireStream(ChangeService.Apply(id) == null, "Controller failed to apply.");
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                var machine = controller.layers[0].stateMachine;
                RequireStream(machine.states.Length == 2 && machine.defaultState.name == "Idle" && machine.defaultState.motion == AssetDatabase.LoadAssetAtPath<AnimationClip>(path) &&
                    machine.defaultState.transitions.Length == 1 && machine.defaultState.transitions[0].destinationState.name == "Move", "Controller lost states, clip references or transitions.");
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
            FeatureCheck("animation_controller_rejects_missing_clips_states_and_transitions", () =>
            {
                var spec = JsonValue.Obj("type", "create_animator_controller", "path", TestFolder + "/invalid.controller", "states", JsonValue.Arr(JsonValue.Obj("name", "Idle", "clip", TestFolder + "/missing.anim")));
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec["states"][0]["clip"] = path;
                spec["default_state"] = "Missing";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec["default_state"] = "Idle";
                spec["transitions"] = JsonValue.Arr(JsonValue.Obj("from", "Idle", "to", "Missing"));
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
        }
    }
}
