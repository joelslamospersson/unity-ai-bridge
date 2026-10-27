using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UnityAIBridge
{
    /// <summary>Scene inspection and editing through the official Editor APIs (Undo aware).</summary>
    public static class SceneService
    {
        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        public static Scene ActiveScene { get { return SceneManager.GetActiveScene(); } }

        public static string GetPath(GameObject go)
        {
            if (go == null) return null;
            var stack = new List<string>();
            Transform current = go.transform;
            while (current != null)
            {
                stack.Add(current.name);
                current = current.parent;
            }
            stack.Reverse();
            return string.Join("/", stack.ToArray());
        }

        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            Scene scene = ActiveScene;
            if (!scene.IsValid()) return null;

            GameObject current = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (string.Equals(root.name, parts[0], StringComparison.Ordinal)) { current = root; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                Transform found = null;
                for (int c = 0; c < current.transform.childCount; c++)
                {
                    var child = current.transform.GetChild(c);
                    if (string.Equals(child.name, parts[i], StringComparison.Ordinal)) { found = child; break; }
                }
                if (found == null) return null;
                current = found.gameObject;
            }
            return current;
        }

        public static JsonValue DescribeHierarchy(int maxDepth = 8, int maxNodes = 600, bool includeComponents = true)
        {
            var result = JsonValue.NewObject();
            Scene scene = ActiveScene;
            result["scene"] = scene.IsValid() ? scene.name : "(no scene)";
            result["scene_path"] = scene.IsValid() ? scene.path : string.Empty;
            result["dirty"] = scene.IsValid() && scene.isDirty;
            var roots = JsonValue.NewArray();
            result["root"] = roots;

            if (!scene.IsValid()) return result;

            int budget = Math.Max(1, maxNodes);
            foreach (var go in scene.GetRootGameObjects())
            {
                if (budget <= 0) break;
                roots.Add(DescribeNode(go, 0, maxDepth, includeComponents, ref budget));
            }
            result["truncated"] = budget <= 0;
            return result;
        }

        internal static JsonValue DescribeNode(GameObject go, int depth, int maxDepth, bool includeComponents, ref int budget)
        {
            budget--;
            var node = JsonValue.NewObject();
            node["name"] = go.name;
            node["path"] = GetPath(go);
            node["active"] = go.activeSelf;
            node["tag"] = go.tag;
            node["layer"] = go.layer;
            node["position"] = Vector(go.transform.localPosition);
            node["rotation"] = Vector(go.transform.localEulerAngles);
            node["scale"] = Vector(go.transform.localScale);

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (!string.IsNullOrEmpty(prefabPath)) node["prefab"] = prefabPath;

            if (includeComponents)
            {
                var components = JsonValue.NewArray();
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) { components.Add("(missing script)"); continue; }
                    components.Add(component.GetType().Name);
                }
                node["components"] = components;
            }

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = JsonValue.NewArray();
                for (int i = 0; i < go.transform.childCount && budget > 0; i++)
                    children.Add(DescribeNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, includeComponents, ref budget));
                node["children"] = children;
            }
            else if (go.transform.childCount > 0)
            {
                node["child_count"] = go.transform.childCount;
            }

            return node;
        }

        public static JsonValue DescribeGameObject(GameObject go, bool includeProperties = true)
        {
            if (go == null) return JsonValue.NewObject();
            var node = JsonValue.NewObject();
            node["name"] = go.name;
            node["path"] = GetPath(go);
            node["active"] = go.activeSelf;
            node["tag"] = go.tag;
            node["layer"] = go.layer;
            node["scene"] = go.scene.name;

            var components = JsonValue.NewArray();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    components.Add(JsonValue.Obj("type", "(missing script)"));
                    continue;
                }
                var entry = JsonValue.NewObject();
                entry["type"] = component.GetType().Name;
                entry["full_type"] = component.GetType().FullName;
                if (includeProperties)
                {
                    entry["enabled"] = JsonValue.Of(component is Behaviour ? ((Behaviour)component).enabled : true);
                    entry["properties"] = DescribeSerializedProperties(component);
                }
                components.Add(entry);
            }
            node["components"] = components;
            return node;
        }

        static JsonValue DescribeSerializedProperties(Component component)
        {
            var result = JsonValue.NewObject();
            try
            {
                var serialized = new SerializedObject(component);
                var iterator = serialized.GetIterator();
                int count = 0;
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        if (count++ > 30) break;
                        var value = ReadProperty(iterator);
                        if (value != null) result[iterator.propertyPath] = value;
                    }
                    while (iterator.NextVisible(false));
                }
            }
            catch { }
            return result;
        }

        static JsonValue ReadProperty(SerializedProperty property)
        {
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: return JsonValue.Of((double)property.intValue);
                    case SerializedPropertyType.Boolean: return JsonValue.Of(property.boolValue);
                    case SerializedPropertyType.Float: return JsonValue.Of((double)property.floatValue);
                    case SerializedPropertyType.String: return JsonValue.Of(property.stringValue);
                    case SerializedPropertyType.Enum: return JsonValue.Of(property.enumNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length ? property.enumNames[property.enumValueIndex] : property.intValue.ToString());
                    case SerializedPropertyType.Vector2: return Vector(property.vector2Value);
                    case SerializedPropertyType.Vector3: return Vector(property.vector3Value);
                    case SerializedPropertyType.Vector4: return JsonValue.Arr(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                    case SerializedPropertyType.Color: return ColorJson(property.colorValue);
                    case SerializedPropertyType.ObjectReference:
                        {
                            var obj = property.objectReferenceValue;
                            return obj == null ? JsonValue.Of("(none)") : JsonValue.Of(AssetDatabase.GetAssetPath(obj));
                        }
                    case SerializedPropertyType.Rect: return JsonValue.Arr(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                    default: return null;
                }
            }
            catch { return null; }
        }

        public static JsonValue Vector(Vector3 v) { return JsonValue.Arr(v.x, v.y, v.z); }
        public static JsonValue Vector(Vector2 v) { return JsonValue.Arr(v.x, v.y); }
        public static JsonValue ColorJson(Color c) { return JsonValue.Arr(c.r, c.g, c.b, c.a); }

        public static string DescribeSceneForPrompt(int maxNodes = 80)
        {
            Scene scene = ActiveScene;
            if (!scene.IsValid()) return "(no active scene)";
            var sb = new StringBuilder();
            sb.AppendLine("Active scene: " + scene.name + (scene.isDirty ? " (unsaved changes)" : string.Empty));
            var roots = scene.GetRootGameObjects();
            int count = 0;
            foreach (var root in roots)
            {
                AppendNode(root.transform, 0, sb, ref count, maxNodes);
                if (count >= maxNodes) break;
            }
            if (count >= maxNodes) sb.AppendLine("... hierarchy truncated ...");
            return sb.ToString();
        }

        static void AppendNode(Transform transform, int depth, StringBuilder sb, ref int count, int maxNodes)
        {
            if (count++ >= maxNodes) return;
            sb.Append(new string(' ', depth * 2)).Append("- ").Append(transform.name);
            var types = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();
                if (type == typeof(Transform) || type == typeof(RectTransform)) continue;
                types.Add(type.Name);
            }
            if (types.Count > 0) sb.Append(" [").Append(string.Join(", ", types.ToArray())).Append(']');
            sb.AppendLine();
            for (int i = 0; i < transform.childCount; i++)
            {
                AppendNode(transform.GetChild(i), depth + 1, sb, ref count, maxNodes);
                if (count >= maxNodes) return;
            }
        }

        public static List<string> ListGameObjectPaths(int max = 500)
        {
            var paths = new List<string>();
            Scene scene = ActiveScene;
            if (!scene.IsValid()) return paths;
            foreach (var root in scene.GetRootGameObjects()) Collect(root.transform, paths, max);
            return paths;
        }

        static void Collect(Transform transform, List<string> paths, int max)
        {
            if (paths.Count >= max) return;
            paths.Add(GetPath(transform.gameObject));
            for (int i = 0; i < transform.childCount; i++) Collect(transform.GetChild(i), paths, max);
        }

        public static Type ResolveComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type cached;
            if (TypeCache.TryGetValue(name, out cached)) return cached;

            foreach (var type in UnityEditor.TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (type == null || type.IsAbstract) continue;
                if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase))
                {
                    TypeCache[name] = type;
                    return type;
                }
            }
            return null;
        }

        public static GameObject CreateGameObject(JsonValue spec, out List<string> warnings)
        {
            warnings = new List<string>();
            string name = spec["name"].AsString("GameObject");
            string parentPath = spec["parent"].AsString();
            string primitive = spec["primitive"].AsString().ToLowerInvariant();

            GameObject go;
            switch (primitive)
            {
                case "cube": go = GameObject.CreatePrimitive(PrimitiveType.Cube); break;
                case "sphere": go = GameObject.CreatePrimitive(PrimitiveType.Sphere); break;
                case "capsule": go = GameObject.CreatePrimitive(PrimitiveType.Capsule); break;
                case "cylinder": go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); break;
                case "plane": go = GameObject.CreatePrimitive(PrimitiveType.Plane); break;
                case "quad": go = GameObject.CreatePrimitive(PrimitiveType.Quad); break;
                default: go = new GameObject(name); break;
            }

            Undo.RegisterCreatedObjectUndo(go, "Unity AI Bridge: create " + name);
            go.name = name;

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FindByPath(parentPath);
                if (parent == null)
                {
                    warnings.Add("Parent not found: " + parentPath + " (object created at scene root).");
                }
                else
                {
                    Undo.SetTransformParent(go.transform, parent.transform, "Unity AI Bridge: parent");
                    go.transform.SetParent(parent.transform, false);
                }
            }

            var position = ToVector3(spec["position"], go.transform.localPosition);
            var rotation = ToVector3(spec["rotation"], go.transform.localEulerAngles);
            var scale = ToVector3(spec["scale"], go.transform.localScale);
            go.transform.localPosition = position;
            go.transform.localEulerAngles = rotation;
            go.transform.localScale = scale;

            string tag = spec["tag"].AsString();
            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; } catch { warnings.Add("Unknown tag '" + tag + "'."); }
            }
            if (!spec["layer"].IsNull)
            {
                int layer = spec["layer"].AsInt(0);
                if (layer >= 0 && layer < 32) go.layer = layer;
            }

            var components = spec["components"];
            if (components.IsArray)
            {
                foreach (var componentName in components.ArrayValue)
                {
                    string typeName = componentName.AsString();
                    if (string.IsNullOrEmpty(typeName)) continue;
                    if (string.Equals(typeName, "Transform", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(typeName, "RectTransform", StringComparison.OrdinalIgnoreCase)) continue;
                    var type = ResolveComponentType(typeName);
                    if (type == null) { warnings.Add("Component type not found: " + typeName); continue; }
                    if (go.GetComponent(type) == null)
                    {
                        if (typeof(Graphic).IsAssignableFrom(type)) EnsureCanvas(go);
                        Undo.AddComponent(go, type);
                    }
                }
            }

            string spritePath = spec["sprite"].AsString();
            if (!string.IsNullOrEmpty(spritePath))
            {
                string error;
                var sprite = AssetService.LoadSprite(spritePath, out error);
                if (sprite == null) warnings.Add(error);
                else
                {
                    var renderer = go.GetComponent<SpriteRenderer>() ?? Undo.AddComponent<SpriteRenderer>(go);
                    Undo.RecordObject(renderer, "Unity AI Bridge: assign sprite");
                    renderer.sprite = sprite;
                }
            }

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return go;
        }

        public static bool EnsureCanvas(GameObject child)
        {
            if (child == null) return false;
            if (child.GetComponentInParent<Canvas>() != null) return false;

            var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Unity AI Bridge: create Canvas");
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            Undo.SetTransformParent(child.transform, canvasObject.transform, "Unity AI Bridge: move to Canvas");
            EditorSceneManager.MarkSceneDirty(child.scene);
            return true;
        }

        public static JsonValue AddComponent(GameObject go, string componentName, JsonValue properties, out string error)
        {
            error = null;
            var type = ResolveComponentType(componentName);
            if (type == null)
            {
                error = "Component type not found: " + componentName;
                return JsonValue.NewObject();
            }
            if (typeof(Graphic).IsAssignableFrom(type)) EnsureCanvas(go);

            Component component = go.GetComponent(type);
            if (component == null) component = Undo.AddComponent(go, type);
            if (component == null)
            {
                error = "Unity refused to add component " + componentName + " (it may require a specific component or DisallowMultipleComponent).";
                return JsonValue.NewObject();
            }

            if (properties != null && properties.IsObject)
            {
                foreach (var kv in properties.Properties)
                {
                    string propertyError;
                    if (!SetProperty(component, kv.Key, kv.Value, out propertyError))
                        BridgeLog.Warn("scene", "Property '" + kv.Key + "' on " + componentName + ": " + propertyError);
                }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            return DescribeGameObject(go, false);
        }

        public static bool SetProperty(UnityEngine.Object target, string propertyPath, JsonValue value, out string error)
        {
            error = null;
            if (target == null) { error = "Target object is null."; return false; }

            Undo.RecordObject(target, "Unity AI Bridge: set " + propertyPath);
            var serialized = new SerializedObject(target);
            string mapped = MapPropertyPath(propertyPath);
            var property = serialized.FindProperty(mapped);
            if (property == null)
            {
                error = "Property not found: " + propertyPath;
                return false;
            }

            if (!WriteProperty(property, value, out error)) return false;

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return true;
        }

        static string MapPropertyPath(string name)
        {
            switch (name)
            {
                case "position":
                case "localPosition": return "m_LocalPosition";
                case "rotation":
                case "localRotation": return "m_LocalRotation";
                case "euler":
                case "localEulerAngles": return "m_LocalEulerAnglesHint";
                case "scale":
                case "localScale": return "m_LocalScale";
                case "name": return "m_Name";
                default: return name;
            }
        }

        static bool WriteProperty(SerializedProperty property, JsonValue value, out string error)
        {
            error = null;
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.intValue = value.AsInt();
                        return true;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = value.AsBool();
                        return true;
                    case SerializedPropertyType.Float:
                        property.floatValue = (float)value.AsNumber();
                        return true;
                    case SerializedPropertyType.String:
                        property.stringValue = value.AsString(string.Empty);
                        return true;
                    case SerializedPropertyType.Color:
                        {
                            Color color;
                            if (!TryParseColor(value, out color)) { error = "Expected [r,g,b,a] or #RRGGBB."; return false; }
                            property.colorValue = color;
                            return true;
                        }
                    case SerializedPropertyType.Vector2:
                        property.vector2Value = ToVector2(value, property.vector2Value);
                        return true;
                    case SerializedPropertyType.Vector3:
                        property.vector3Value = ToVector3(value, property.vector3Value);
                        return true;
                    case SerializedPropertyType.Vector4:
                        {
                            var v = ToVector4(value, property.vector4Value);
                            property.vector4Value = v;
                            return true;
                        }
                    case SerializedPropertyType.Enum:
                        {
                            if (value.IsString)
                            {
                                string name = value.AsString();
                                int index = Array.IndexOf(property.enumNames, name);
                                if (index < 0) { error = "Unknown enum value '" + name + "'. Options: " + string.Join(", ", property.enumNames); return false; }
                                property.enumValueIndex = index;
                            }
                            else property.enumValueIndex = value.AsInt();
                            return true;
                        }
                    case SerializedPropertyType.ObjectReference:
                        {
                            string path = value.AsString();
                            UnityEngine.Object asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                            if (!string.IsNullOrEmpty(path) && asset == null) { error = "Asset not found: " + path; return false; }
                            property.objectReferenceValue = asset;
                            return true;
                        }
                    default:
                        error = "Unsupported property type: " + property.propertyType;
                        return false;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public static bool TryParseColor(JsonValue value, out Color color)
        {
            color = Color.white;
            if (value.IsString)
            {
                string hex = value.AsString();
                if (!hex.StartsWith("#", StringComparison.Ordinal)) hex = "#" + hex;
                Color parsed;
                if (ColorUtility.TryParseHtmlString(hex, out parsed)) { color = parsed; return true; }
                return false;
            }
            if (value.IsArray && value.Count >= 3)
            {
                color = new Color((float)value[0].AsNumber(), (float)value[1].AsNumber(), (float)value[2].AsNumber(), value.Count > 3 ? (float)value[3].AsNumber() : 1f);
                return true;
            }
            if (value.IsObject)
            {
                color = new Color((float)value["r"].AsNumber(1), (float)value["g"].AsNumber(1), (float)value["b"].AsNumber(1), (float)value["a"].AsNumber(1));
                return true;
            }
            return false;
        }

        public static Vector3 ToVector3(JsonValue value, Vector3 fallback)
        {
            if (value == null || value.IsNull) return fallback;
            if (value.IsArray && value.Count >= 3)
                return new Vector3((float)value[0].AsNumber(), (float)value[1].AsNumber(), (float)value[2].AsNumber());
            if (value.IsNumber) return Vector3.one * (float)value.AsNumber();
            if (value.IsObject)
                return new Vector3((float)value["x"].AsNumber(fallback.x), (float)value["y"].AsNumber(fallback.y), (float)value["z"].AsNumber(fallback.z));
            return fallback;
        }

        public static Vector2 ToVector2(JsonValue value, Vector2 fallback)
        {
            if (value == null || value.IsNull) return fallback;
            if (value.IsArray && value.Count >= 2) return new Vector2((float)value[0].AsNumber(), (float)value[1].AsNumber());
            if (value.IsNumber) return Vector2.one * (float)value.AsNumber();
            if (value.IsObject) return new Vector2((float)value["x"].AsNumber(fallback.x), (float)value["y"].AsNumber(fallback.y));
            return fallback;
        }

        public static Vector4 ToVector4(JsonValue value, Vector4 fallback)
        {
            if (value == null || value.IsNull) return fallback;
            if (value.IsArray && value.Count >= 4)
                return new Vector4((float)value[0].AsNumber(), (float)value[1].AsNumber(), (float)value[2].AsNumber(), (float)value[3].AsNumber());
            if (value.IsNumber) return Vector4.one * (float)value.AsNumber();
            return fallback;
        }

        public static bool SaveActiveScene(out string error)
        {
            error = null;
            Scene scene = ActiveScene;
            if (!scene.IsValid()) { error = "There is no active scene to save."; return false; }
            if (string.IsNullOrEmpty(scene.path)) { error = "The active scene has never been saved. Use File > Save As first."; return false; }
            try
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                if (!saved) { error = "Unity reported that the scene was not saved."; return false; }
                BridgeLog.Success("scene", "Saved scene " + scene.path);
                return true;
            }
            catch (Exception e)
            {
                error = "Could not save scene: " + e.Message;
                return false;
            }
        }
    }
}
