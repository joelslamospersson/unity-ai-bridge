using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityAIBridge
{
    public static partial class SelfTests
    {
        static JsonValue AdvancedControllerFixture(string path)
        {
            string clip = TestFolder + "/Motion.anim";
            return JsonValue.Obj("type", "create_animator_controller", "path", path,
                "parameters", JsonValue.Arr(JsonValue.Obj("name", "Speed", "type", "Float", "default", 0.5),
                    JsonValue.Obj("name", "Grounded", "type", "Bool", "default", true),
                    JsonValue.Obj("name", "Tier", "type", "Int", "default", 2), JsonValue.Obj("name", "Jump", "type", "Trigger")),
                "states", JsonValue.Arr(JsonValue.Obj("name", "Idle", "clip", clip), JsonValue.Obj("name", "Locomotion", "blend_tree",
                    JsonValue.Obj("parameter", "Speed", "children", JsonValue.Arr(JsonValue.Obj("clip", clip, "threshold", 0), JsonValue.Obj("clip", clip, "threshold", 1))))),
                "transitions", JsonValue.Arr(JsonValue.Obj("from", "Idle", "to", "Locomotion", "conditions", JsonValue.Arr(
                    JsonValue.Obj("parameter", "Speed", "mode", "Greater", "threshold", 0.1), JsonValue.Obj("parameter", "Grounded", "mode", "If"),
                    JsonValue.Obj("parameter", "Tier", "mode", "Equals", "threshold", 2))),
                    JsonValue.Obj("from", "Locomotion", "to", "Idle", "conditions", JsonValue.Arr(JsonValue.Obj("parameter", "Jump", "mode", "If")))));
        }

        static void RunAdvancedAnimationTests()
        {
            FeatureCheck("animation_typed_parameters_conditions_and_blend_tree_persist", () =>
            {
                string path = TestFolder + "/Advanced.controller";
                var change = ActionRouter.StageAction(AdvancedControllerFixture(path), "selftest"); _testChangeIds.Add(change.Id);
                RequireStream(change.Status == ChangeStatus.Pending && change.Diff.Contains("blend_tree"), "Controller was not staged with a complete preview.");
                ChangeService.Approve(change.Id);
                RequireStream(ChangeService.Apply(change.Id) == null, "Advanced controller failed to apply.");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                RequireStream(controller.parameters.Length == 4 && controller.parameters.Single(p => p.name == "Speed").defaultFloat == 0.5f &&
                    controller.parameters.Single(p => p.name == "Grounded").defaultBool && controller.parameters.Single(p => p.name == "Tier").defaultInt == 2, "Typed defaults changed.");
                var machine = controller.layers[0].stateMachine;
                var idle = machine.states.Single(s => s.state.name == "Idle").state;
                var motion = machine.states.Single(s => s.state.name == "Locomotion").state;
                var tree = motion.motion as BlendTree;
                RequireStream(tree != null && tree.blendParameter == "Speed" && tree.blendType == BlendTreeType.Simple1D && tree.children.Length == 2 &&
                    tree.children[0].threshold == 0 && tree.children[1].threshold == 1 && AssetDatabase.GetAssetPath(tree) == path, "Blend children, thresholds or subasset persistence changed.");
                RequireStream(!idle.transitions[0].hasExitTime && idle.transitions[0].conditions.Length == 3 &&
                    idle.transitions[0].conditions.Any(c => c.parameter == "Tier" && c.mode == AnimatorConditionMode.Equals && c.threshold == 2), "Transition conditions changed.");
                var description = AssetService.InspectAsset(path)["animation"];
                RequireStream(description["parameters"].Count == 4 && description["states"].Count == 2, "Controller inspection omitted parameters or states.");
            });
            FeatureCheck("animation_conditions_reject_type_mismatch_and_missing_parameter", () =>
            {
                var spec = AdvancedControllerFixture(TestFolder + "/invalid-advanced.controller");
                spec["transitions"][0]["conditions"][0]["mode"] = "If";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-advanced.controller");
                spec["transitions"][0]["conditions"][0]["parameter"] = "Missing";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-advanced.controller");
                spec["transitions"][0]["conditions"][2]["threshold"] = 0.5;
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
            FeatureCheck("animation_blend_tree_rejects_invalid_parameter_and_thresholds", () =>
            {
                var spec = AdvancedControllerFixture(TestFolder + "/invalid-blend.controller");
                spec["states"][1]["blend_tree"]["parameter"] = "Grounded";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-blend.controller");
                spec["states"][1]["blend_tree"]["children"][1]["threshold"] = 0;
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-blend.controller");
                spec["states"][1]["clip"] = TestFolder + "/Motion.anim";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
            FeatureCheck("animation_parameter_defaults_and_duplicates_are_validated", () =>
            {
                var spec = AdvancedControllerFixture(TestFolder + "/invalid-parameters.controller");
                spec["parameters"][2]["default"] = 1.5;
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-parameters.controller");
                spec["parameters"][1]["name"] = "Speed";
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
                spec = AdvancedControllerFixture(TestFolder + "/invalid-parameters.controller");
                spec["parameters"][1]["default"] = 1;
                RejectsAnimation(() => ActionRouter.StageAction(spec, "selftest"));
            });
        }
    }
}
