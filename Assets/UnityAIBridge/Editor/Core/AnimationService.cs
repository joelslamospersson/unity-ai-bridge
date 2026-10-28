using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>Bounded, declarative animation authoring; never executes model-supplied scripts or animation events.</summary>
    public static class AnimationService
    {
        static readonly HashSet<string> TransformProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z",
            "localEulerAnglesRaw.x", "localEulerAnglesRaw.y", "localEulerAnglesRaw.z"
        };

        static string NewAssetPath(string path, string extension)
        {
            string full;
            string error = Permissions.CheckWrite(path, out full, false, true);
            if (error != null) throw new BridgeException(error);
            if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("Animation destination must end with " + extension);
            if (File.Exists(full) || Directory.Exists(full) || File.Exists(full + ".meta") || AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new BridgeException("Animation destination already exists; choose a new path: " + path);
            return BridgePaths.ToProjectRelative(full);
        }

        static string ChildPath(JsonValue value)
        {
            string path = value.AsString("");
            if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("\\"))
                throw new BridgeException("Animation binding paths must be relative to the Animator root.");
            if (path.Length > 0)
                foreach (string segment in path.Split('/'))
                    if (segment == "." || segment == ".." || segment.Length == 0)
                        throw new BridgeException("Invalid animation child path: " + path);
            return path;
        }

        static float Number(JsonValue value, string field, double min, double max)
        {
            double number = value.AsNumber(double.NaN);
            if (!value.IsNumber || double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max)
                throw new BridgeException(field + " must be a finite number between " + min + " and " + max + ".");
            return (float)number;
        }

        static void Array(JsonValue array, string field, int maximum, bool required)
        {
            if (array.IsNull && !required) return;
            if (!array.IsArray || array.Count > maximum || (required && array.Count == 0))
                throw new BridgeException(field + " must be an array containing " + (required ? "1–" : "0–") + maximum + " entries.");
        }

        static Sprite SpriteFrame(JsonValue frame)
        {
            string full;
            string error = Permissions.CheckRead(frame["sprite_path"].AsString(), out full);
            if (error != null) throw new BridgeException(error);
            string name = frame["sprite_name"].AsString("");
            Sprite found = null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(BridgePaths.ToProjectRelative(full)))
            {
                var sprite = asset as Sprite;
                if (sprite == null || (name.Length > 0 && sprite.name != name)) continue;
                if (found != null) throw new BridgeException("Sprite sheet has multiple matching frames. Supply an exact sprite_name from inspect_asset.");
                found = sprite;
            }
            if (found == null) throw new BridgeException("Sprite frame not found: " + frame["sprite_path"].AsString() + " " + name);
            return found;
        }

        public static void ValidateClip(string path, JsonValue spec)
        {
            NewAssetPath(path, ".anim");
            if (!spec["frame_rate"].IsNull) Number(spec["frame_rate"], "frame_rate", 1, 240);
            var curves = spec["curves"]; var frames = spec["sprite_frames"];
            Array(curves, "curves", 64, false); Array(frames, "sprite_frames", 1000, false);
            if (curves.Count == 0 && frames.Count == 0) throw new BridgeException("Supply transform curves, sprite_frames, or both.");
            var bindings = new HashSet<string>(StringComparer.Ordinal);
            int totalKeys = frames.Count;
            if (curves.IsArray) foreach (var curve in curves.ArrayValue)
            {
                string binding = ChildPath(curve["path"]) + "|" + curve["property"].AsString();
                if (!TransformProperties.Contains(curve["property"].AsString(""))) throw new BridgeException("Unsupported transform property. Use local position, scale, or localEulerAnglesRaw axes.");
                if (!bindings.Add(binding)) throw new BridgeException("Duplicate animation curve: " + binding);
                string interpolation = curve["interpolation"].AsString("linear");
                if (interpolation != "linear" && interpolation != "constant" && interpolation != "smooth") throw new BridgeException("Interpolation must be linear, constant or smooth.");
                Array(curve["keys"], "keys", 1000, true);
                totalKeys += curve["keys"].Count;
                if (totalKeys > 4096) throw new BridgeException("Animation clips are limited to 4096 total keyframes.");
                float previous = -1;
                foreach (var key in curve["keys"].ArrayValue)
                {
                    float time = Number(key["time"], "key time", 0, 3600);
                    if (time <= previous) throw new BridgeException("Keyframe times must be strictly increasing.");
                    previous = time;
                    Number(key["value"], "key value", -1000000, 1000000);
                }
            }
            ChildPath(spec["sprite_target"]);
            float last = -1;
            if (frames.IsArray) foreach (var frame in frames.ArrayValue)
            {
                float time = Number(frame["time"], "sprite time", 0, 3600);
                if (time <= last) throw new BridgeException("Sprite frame times must be strictly increasing.");
                last = time;
                SpriteFrame(frame);
            }
        }

        public static void CreateClip(string path, JsonValue spec)
        {
            ValidateClip(path, spec); // Repeat validation at apply time, including source sprites and destination collisions.
            path = NewAssetPath(path, ".anim");
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = (float)spec["frame_rate"].AsNumber(30) };
            bool created = false;
            try
            {
                if (spec["curves"].IsArray) foreach (var curveSpec in spec["curves"].ArrayValue)
                {
                    var keys = new List<Keyframe>();
                    foreach (var key in curveSpec["keys"].ArrayValue) keys.Add(new Keyframe((float)key["time"].AsNumber(), (float)key["value"].AsNumber()));
                    var curve = new AnimationCurve(keys.ToArray());
                    string interpolation = curveSpec["interpolation"].AsString("linear");
                    var mode = interpolation == "constant" ? AnimationUtility.TangentMode.Constant : interpolation == "smooth" ? AnimationUtility.TangentMode.ClampedAuto : AnimationUtility.TangentMode.Linear;
                    for (int i = 0; i < curve.length; i++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, i, mode);
                        AnimationUtility.SetKeyRightTangentMode(curve, i, mode);
                    }
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(ChildPath(curveSpec["path"]), typeof(Transform), curveSpec["property"].AsString()), curve);
                }
                if (spec["sprite_frames"].Count > 0)
                {
                    var keys = new List<ObjectReferenceKeyframe>();
                    foreach (var frame in spec["sprite_frames"].ArrayValue)
                        keys.Add(new ObjectReferenceKeyframe { time = (float)frame["time"].AsNumber(), value = SpriteFrame(frame) });
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(ChildPath(spec["sprite_target"]), typeof(SpriteRenderer), "m_Sprite"), keys.ToArray());
                }
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = spec["loop"].AsBool(false);
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssetService.EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                AssetDatabase.CreateAsset(clip, path);
                created = true;
                AssetDatabase.SaveAssets();
            }
            catch
            {
                if (created) AssetDatabase.DeleteAsset(path);
                else UnityEngine.Object.DestroyImmediate(clip);
                throw;
            }
        }

        static AnimationClip ReadClip(string path)
        {
            string full; string error = Permissions.CheckRead(path, out full);
            if (error != null) throw new BridgeException(error);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(BridgePaths.ToProjectRelative(full));
            if (clip == null) throw new BridgeException("Animation clip not found: " + path + ". Apply clip creation first.");
            return clip;
        }

        public static void ValidateController(string path, JsonValue spec)
        {
            NewAssetPath(path, ".controller");
            var parameters = ControllerParameters(spec);
            Array(spec["states"], "states", 64, true);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in spec["states"].ArrayValue)
            {
                string name = state["name"].AsString("");
                if (string.IsNullOrWhiteSpace(name) || name.Contains(".") || name.Contains("/") || !names.Add(name)) throw new BridgeException("State names must be unique, non-empty and contain no slash or dot.");
                if (state["blend_tree"].IsObject)
                {
                    if (!state["clip"].IsNull) throw new BridgeException("A state accepts either a clip or a blend_tree, not both.");
                    var tree = state["blend_tree"];
                    AnimatorControllerParameterType type;
                    if (!parameters.TryGetValue(tree["parameter"].AsString(""), out type) || type != AnimatorControllerParameterType.Float)
                        throw new BridgeException("A 1D blend tree requires an existing Float parameter.");
                    if (tree["type"].AsString("1D") != "1D") throw new BridgeException("Only 1D blend trees are supported.");
                    Array(tree["children"], "blend_tree children", 32, true);
                    if (tree["children"].Count < 2) throw new BridgeException("A blend tree needs at least two child clips.");
                    float previous = float.NegativeInfinity;
                    foreach (var child in tree["children"].ArrayValue)
                    {
                        ReadClip(child["clip"].AsString());
                        float threshold = Number(child["threshold"], "blend threshold", -1000000, 1000000);
                        if (threshold <= previous) throw new BridgeException("Blend thresholds must be strictly increasing.");
                        previous = threshold;
                    }
                }
                else ReadClip(state["clip"].AsString());
            }
            string defaultState = spec["default_state"].AsString(spec["states"][0]["name"].AsString());
            if (!names.Contains(defaultState)) throw new BridgeException("default_state must name one of the controller's states.");
            Array(spec["transitions"], "transitions", 128, false);
            if (spec["transitions"].IsArray) foreach (var transition in spec["transitions"].ArrayValue)
            {
                if (!names.Contains(transition["from"].AsString("")) || !names.Contains(transition["to"].AsString(""))) throw new BridgeException("Every transition must reference existing states.");
                if (!transition["exit_time"].IsNull) Number(transition["exit_time"], "exit_time", 0, 100);
                if (!transition["duration"].IsNull) Number(transition["duration"], "duration", 0, 10);
                Array(transition["conditions"], "transition conditions", 16, false);
                if (!transition["has_exit_time"].AsBool(transition["conditions"].Count == 0) && transition["conditions"].Count == 0)
                    throw new BridgeException("A transition needs conditions or an exit time.");
                if (transition["conditions"].IsArray) foreach (var condition in transition["conditions"].ArrayValue) ValidateCondition(condition, parameters);
            }
        }

        static Dictionary<string, AnimatorControllerParameterType> ControllerParameters(JsonValue spec)
        {
            var result = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            Array(spec["parameters"], "parameters", 32, false);
            if (spec["parameters"].IsArray) foreach (var parameter in spec["parameters"].ArrayValue)
            {
                string name = parameter["name"].AsString("");
                if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name)) throw new BridgeException("Controller parameter names must be non-empty and unique.");
                string typeName = parameter["type"].AsString("");
                AnimatorControllerParameterType type;
                if ((typeName != "Float" && typeName != "Int" && typeName != "Bool" && typeName != "Trigger") || !Enum.TryParse(typeName, out type))
                    throw new BridgeException("Parameter type must be Float, Int, Bool or Trigger.");
                if (!parameter["default"].IsNull)
                {
                    if (type == AnimatorControllerParameterType.Float || type == AnimatorControllerParameterType.Int)
                    {
                        float value = Number(parameter["default"], "parameter default", -1000000, 1000000);
                        if (type == AnimatorControllerParameterType.Int && value != Math.Floor(value)) throw new BridgeException("Int parameter default must be an integer.");
                    }
                    else if (!parameter["default"].IsBool) throw new BridgeException("Bool/Trigger parameter default must be a boolean.");
                }
                result.Add(name, type);
            }
            return result;
        }

        static AnimatorConditionMode ValidateCondition(JsonValue condition, Dictionary<string, AnimatorControllerParameterType> parameters)
        {
            AnimatorControllerParameterType type;
            if (!parameters.TryGetValue(condition["parameter"].AsString(""), out type)) throw new BridgeException("Transition condition references a missing parameter.");
            string mode = condition["mode"].AsString("");
            bool allowed = type == AnimatorControllerParameterType.Float ? mode == "Greater" || mode == "Less"
                : type == AnimatorControllerParameterType.Int ? mode == "Greater" || mode == "Less" || mode == "Equals" || mode == "NotEqual"
                : type == AnimatorControllerParameterType.Bool ? mode == "If" || mode == "IfNot" : mode == "If";
            if (!allowed) throw new BridgeException("Condition mode is incompatible with its parameter type.");
            if (type == AnimatorControllerParameterType.Float || type == AnimatorControllerParameterType.Int)
            {
                float threshold = Number(condition["threshold"], "condition threshold", -1000000, 1000000);
                if (type == AnimatorControllerParameterType.Int && threshold != Math.Floor(threshold)) throw new BridgeException("Int conditions require an integer threshold.");
            }
            return (AnimatorConditionMode)Enum.Parse(typeof(AnimatorConditionMode), mode);
        }

        public static void CreateController(string path, JsonValue spec)
        {
            ValidateController(path, spec);
            path = NewAssetPath(path, ".controller");
            AssetService.EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AnimatorController controller = null;
            try
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                if (controller == null) throw new BridgeException("Unity could not create the Animator Controller.");
                if (spec["parameters"].IsArray) foreach (var entry in spec["parameters"].ArrayValue)
                {
                    var parameter = new AnimatorControllerParameter { name = entry["name"].AsString(), type = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), entry["type"].AsString()),
                        defaultFloat = (float)entry["default"].AsNumber(), defaultInt = entry["default"].AsInt(), defaultBool = entry["default"].AsBool() };
                    controller.AddParameter(parameter);
                }
                var machine = controller.layers[0].stateMachine;
                var states = new Dictionary<string, AnimatorState>(StringComparer.Ordinal);
                foreach (var entry in spec["states"].ArrayValue)
                {
                    string name = entry["name"].AsString();
                    var state = machine.AddState(name);
                    if (entry["blend_tree"].IsObject)
                    {
                        var source = entry["blend_tree"];
                        var tree = new BlendTree { name = name + " Blend", blendType = BlendTreeType.Simple1D, blendParameter = source["parameter"].AsString(), useAutomaticThresholds = false };
                        AssetDatabase.AddObjectToAsset(tree, controller);
                        foreach (var child in source["children"].ArrayValue) tree.AddChild(ReadClip(child["clip"].AsString()), (float)child["threshold"].AsNumber());
                        state.motion = tree;
                    }
                    else state.motion = ReadClip(entry["clip"].AsString());
                    states.Add(name, state);
                }
                machine.defaultState = states[spec["default_state"].AsString(spec["states"][0]["name"].AsString())];
                if (spec["transitions"].IsArray) foreach (var entry in spec["transitions"].ArrayValue)
                {
                    var transition = states[entry["from"].AsString()].AddTransition(states[entry["to"].AsString()]);
                    transition.hasExitTime = entry["has_exit_time"].AsBool(entry["conditions"].Count == 0);
                    transition.exitTime = (float)entry["exit_time"].AsNumber(1);
                    transition.hasFixedDuration = true;
                    transition.duration = (float)entry["duration"].AsNumber(0.1);
                    if (entry["conditions"].IsArray) foreach (var condition in entry["conditions"].ArrayValue)
                        transition.AddCondition((AnimatorConditionMode)Enum.Parse(typeof(AnimatorConditionMode), condition["mode"].AsString()),
                            (float)condition["threshold"].AsNumber(), condition["parameter"].AsString());
                }
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
            }
            catch { if (controller != null) AssetDatabase.DeleteAsset(path); throw; }
        }

        public static JsonValue DescribeClip(AnimationClip clip)
        {
            var curves = JsonValue.NewArray();
            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var binding in bindings)
            {
                if (curves.Count >= 64) break;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                var keys = JsonValue.NewArray();
                for (int i = 0; i < Math.Min(curve.length, 16); i++) keys.Add(JsonValue.Obj("time", curve[i].time, "value", curve[i].value));
                curves.Add(JsonValue.Obj("path", binding.path, "property", binding.propertyName, "key_count", curve.length, "keys", keys, "truncated", curve.length > 16));
            }
            var spriteCurves = JsonValue.NewArray();
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (spriteCurves.Count >= 64) break;
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                var keys = JsonValue.NewArray();
                for (int i = 0; i < Math.Min(frames.Length, 16); i++) keys.Add(JsonValue.Obj("time", frames[i].time,
                    "sprite_path", AssetDatabase.GetAssetPath(frames[i].value), "sprite_name", frames[i].value != null ? frames[i].value.name : ""));
                spriteCurves.Add(JsonValue.Obj("path", binding.path, "property", binding.propertyName, "key_count", frames.Length, "frames", keys, "truncated", frames.Length > 16));
            }
            return JsonValue.Obj("length", clip.length, "frame_rate", clip.frameRate, "loop", AnimationUtility.GetAnimationClipSettings(clip).loopTime,
                "curve_count", bindings.Length, "curves", curves, "sprite_curves", spriteCurves, "truncated", bindings.Length > 64);
        }

        public static JsonValue DescribeController(AnimatorController controller)
        {
            var parameters = JsonValue.NewArray();
            foreach (var parameter in controller.parameters)
            {
                if (parameters.Count >= 32) break;
                parameters.Add(JsonValue.Obj("name", parameter.name, "type", parameter.type.ToString(), "default_float", parameter.defaultFloat,
                    "default_int", parameter.defaultInt, "default_bool", parameter.defaultBool));
            }
            var states = JsonValue.NewArray();
            foreach (var layer in controller.layers) foreach (var child in layer.stateMachine.states)
            {
                if (states.Count >= 64) break;
                var state = child.state;
                var entry = JsonValue.Obj("name", state.name, "layer", layer.name, "default", layer.stateMachine.defaultState == state);
                var tree = state.motion as BlendTree;
                if (tree != null)
                {
                    var children = JsonValue.NewArray();
                    foreach (var motion in tree.children)
                    {
                        if (children.Count >= 32) break;
                        children.Add(JsonValue.Obj("clip", AssetDatabase.GetAssetPath(motion.motion), "threshold", motion.threshold));
                    }
                    entry["blend_tree"] = JsonValue.Obj("type", tree.blendType.ToString(), "parameter", tree.blendParameter, "children", children);
                }
                else entry["clip"] = AssetDatabase.GetAssetPath(state.motion);
                var transitions = JsonValue.NewArray();
                foreach (var transition in state.transitions)
                {
                    if (transitions.Count >= 128) break;
                    var conditions = JsonValue.NewArray();
                    foreach (var condition in transition.conditions)
                    {
                        if (conditions.Count >= 16) break;
                        conditions.Add(JsonValue.Obj("parameter", condition.parameter, "mode", condition.mode.ToString(), "threshold", condition.threshold));
                    }
                    transitions.Add(JsonValue.Obj("to", transition.destinationState != null ? transition.destinationState.name : "", "has_exit_time", transition.hasExitTime,
                        "exit_time", transition.exitTime, "duration", transition.duration, "conditions", conditions));
                }
                entry["transitions"] = transitions; states.Add(entry);
            }
            return JsonValue.Obj("parameters", parameters, "states", states, "bounded", true);
        }
    }
}
