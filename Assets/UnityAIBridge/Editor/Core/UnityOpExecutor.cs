using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>Executes the Unity-API side of an approved change. Returns null on success.</summary>
    public static class UnityOpExecutor
    {
        public static string Execute(BridgeChange change)
        {
            if (change == null) return "Change is null.";
            var p = change.Parameters;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity AI Bridge: " + change.Title);
            try
            {
                switch (change.Kind)
                {
                    case ChangeKinds.CreateGameObject:
                        {
                            List<string> warnings;
                            var go = SceneService.CreateGameObject(p, out warnings);
                            if (warnings != null && warnings.Count > 0)
                                foreach (var warning in warnings) BridgeLog.Warn("scene", warning);
                            return go == null ? "Unity failed to create the GameObject." : null;
                        }

                    case ChangeKinds.AddComponent:
                        {
                            string targetPath = FirstString(p, "target", "target_path", "path");
                            string componentName = FirstString(p, "component", "component_type", "type");
                            var go = SceneService.FindByPath(targetPath);
                            if (go == null) return "GameObject not found in the active scene: " + targetPath;
                            string error;
                            SceneService.AddComponent(go, componentName, p["properties"], out error);
                            return error;
                        }

                    case ChangeKinds.SetComponentProperty:
                        {
                            string targetPath = FirstString(p, "target", "target_path", "path");
                            var go = SceneService.FindByPath(targetPath);
                            if (go == null) return "GameObject not found in the active scene: " + targetPath;
                            string componentName = FirstString(p, "component", "component_type");
                            var type = SceneService.ResolveComponentType(componentName);
                            if (type == null) return "Component type not found: " + componentName;
                            var component = go.GetComponent(type);
                            if (component == null) return "GameObject has no " + componentName + " component.";

                            if (p["properties"].IsObject)
                            {
                                foreach (var kv in p["properties"].Properties)
                                {
                                    string error;
                                    if (!SceneService.SetProperty(component, kv.Key, kv.Value, out error)) return error;
                                }
                                return null;
                            }

                            string property = FirstString(p, "property", "property_path");
                            string singleError;
                            if (!SceneService.SetProperty(component, property, p["value"], out singleError)) return singleError;
                            return null;
                        }

                    case ChangeKinds.CreatePrefab:
                        return CreatePrefab(change, p);

                    case ChangeKinds.CreateAnimationClip:
                        AnimationService.CreateClip(change.TargetPath, p);
                        return null;

                    case ChangeKinds.CreateAnimatorController:
                        AnimationService.CreateController(change.TargetPath, p);
                        return null;

                    case ChangeKinds.CreateMaterial:
                        {
                            Color color = Color.white;
                            if (!p["color"].IsNull) SceneService.TryParseColor(p["color"], out color);
                            string texture = FirstString(p, "texture", "texture_path");
                            string shader = FirstString(p, "shader");
                            string error;
                            var created = AssetService.CreateMaterial(change.TargetPath, shader, color, texture, out error);
                            return created == null ? error : null;
                        }

                    case ChangeKinds.ImportSprite:
                        {
                            string source = p["incoming_path"].AsString();
                            if (string.IsNullOrEmpty(source)) source = p["source_path"].AsString();
                            if (string.IsNullOrEmpty(source)) return "No image source was provided.";

                            string sourceFull;
                            string error;
                            if (Path.IsPathRooted(source))
                            {
                                string stagingRoot = Path.Combine(BridgePaths.ProjectRoot, "Library", "UnityAIBridge");
                                bool staging = BridgePaths.IsWithin(source, stagingRoot);
                                bool existingAsset = BridgePaths.IsWithin(source, BridgePaths.AssetsRoot);
                                if (!staging && !existingAsset)
                                    return "Rooted image sources must be staged in Library/UnityAIBridge or already exist under Assets/.";
                                sourceFull = source;
                            }
                            else
                            {
                                error = Permissions.CheckRead(source, out sourceFull);
                                if (error != null) return error;
                            }

                            var options = SpriteImportOptions.FromJson(p["settings"].IsObject ? p["settings"] : p);
                            var imported = AssetService.ImportImage(sourceFull, change.TargetPath, options, out error);
                            return imported == null ? error : null;
                        }

                    case ChangeKinds.ConfigureSprite:
                        {
                            string path = string.IsNullOrEmpty(change.TargetPath) ? FirstString(p, "path", "asset_path") : change.TargetPath;
                            var options = SpriteImportOptions.FromJson(p["settings"].IsObject ? p["settings"] : p);
                            string error;
                            return AssetService.ConfigureSprite(path, options, out error) ? null : error;
                        }

                    case ChangeKinds.SliceSprite:
                        {
                            string path = string.IsNullOrEmpty(change.TargetPath) ? FirstString(p, "path", "asset_path") : change.TargetPath;
                            int rows = p["rows"].AsInt(4);
                            int columns = p["columns"].AsInt(p["cols"].AsInt(4));
                            string error;
                            return AssetService.SliceGrid(path, rows, columns, out error) ? null : error;
                        }

                    case ChangeKinds.AssignSprite:
                        {
                            string targetPath = FirstString(p, "target", "target_path", "gameobject");
                            string component = p["component"].AsString("SpriteRenderer");
                            string sprite = FirstString(p, "sprite", "sprite_path", "asset_path");
                            string error;
                            return AssetService.AssignSprite(targetPath, component, sprite, out error) ? null : error;
                        }

                    case ChangeKinds.CreateScriptableObject:
                        {
                            string typeName = FirstString(p, "type", "script", "type_name");
                            var type = ResolveScriptableObjectType(typeName);
                            if (type == null) return "ScriptableObject type not found: " + typeName;
                            if (AssetDatabase.LoadMainAssetAtPath(change.TargetPath) != null)
                                return "An asset already exists at " + change.TargetPath;

                            string folder = Path.GetDirectoryName(change.TargetPath).Replace('\\', '/');
                            AssetService.EnsureAssetFolder(folder);
                            var instance = ScriptableObject.CreateInstance(type);
                            if (instance == null) return "Could not instantiate " + type.FullName;
                            AssetDatabase.CreateAsset(instance, change.TargetPath);

                            if (p["properties"].IsObject)
                            {
                                foreach (var kv in p["properties"].Properties)
                                {
                                    string error;
                                    if (!SceneService.SetProperty(instance, kv.Key, kv.Value, out error))
                                        BridgeLog.Warn("asset", "Property '" + kv.Key + "': " + error);
                                }
                            }

                            AssetDatabase.SaveAssets();
                            AssetDatabase.Refresh();
                            BridgeLog.Success("asset", "Created ScriptableObject " + change.TargetPath);
                            return null;
                        }

                    case ChangeKinds.SaveScene:
                        {
                            string error;
                            return SceneService.SaveActiveScene(out error) ? null : error;
                        }

                    default:
                        return "Unknown change kind: " + change.Kind;
                }
            }
            catch (Exception e)
            {
                BridgeLog.Error("apply", "Operation failed: " + change.Kind, e);
                return e.Message;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        public static Type ResolveScriptableObjectType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var type in UnityEditor.TypeCache.GetTypesDerivedFrom<ScriptableObject>())
            {
                if (type == null || type.IsAbstract) continue;
                if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return type;
            }
            return null;
        }

        static string CreatePrefab(BridgeChange change, JsonValue p)
        {
            string destination;
            string error = Permissions.CheckWrite(change.TargetPath, out destination, false, true);
            if (error != null) return error;
            if (File.Exists(destination) || AssetDatabase.LoadMainAssetAtPath(change.TargetPath) != null)
                return "An asset already exists at " + change.TargetPath + ". It was not overwritten.";

            GameObject source = null;
            bool temporary = false;
            bool keep = p["keep_in_scene"].AsBool(false);
            bool retained = false;
            try
            {
                string assetPath = p["source_asset"].AsString();
                string target = FirstString(p, "target", "source", "gameobject", "target_path");
                if (!string.IsNullOrEmpty(assetPath))
                {
                    string sourceFull;
                    error = Permissions.CheckRead(assetPath, out sourceFull);
                    if (error != null) return error;
                    assetPath = BridgePaths.ToProjectRelative(sourceFull);
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (asset == null || !PrefabUtility.IsPartOfPrefabAsset(asset)) return "Source is not a prefab asset: " + assetPath;
                    source = PrefabUtility.InstantiatePrefab(asset, SceneService.ActiveScene) as GameObject;
                    temporary = true;
                }
                else if (!string.IsNullOrEmpty(target))
                {
                    source = SceneService.FindByPath(target);
                    if (source == null) return "Source GameObject not found in the active scene: " + target;
                }
                else
                {
                    List<string> warnings;
                    source = SceneService.CreateGameObject(p["spec"], out warnings);
                    temporary = true;
                    if (warnings != null) foreach (var warning in warnings) BridgeLog.Warn("prefab", warning);
                }
                if (source == null) return "No GameObject to turn into a prefab.";
                if (temporary) source.name = p["name"].AsString(Path.GetFileNameWithoutExtension(change.TargetPath));
                int missingScripts = AssetService.InspectPrefabScripts(source)["missing_script_count"].AsInt();
                if (missingScripts > 0)
                {
                    if (!temporary || !p["remove_missing_scripts"].AsBool())
                        return "The source contains " + missingScripts + " missing script reference(s). To clean only a new asset copy, stage source_asset with remove_missing_scripts:true; restore original gameplay scripts separately.";
                    if (PrefabUtility.IsPartOfPrefabInstance(source))
                        PrefabUtility.UnpackPrefabInstance(source, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    foreach (var transform in source.GetComponentsInChildren<Transform>(true))
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                }

                AssetService.EnsureAssetFolder(Path.GetDirectoryName(change.TargetPath).Replace('\\', '/'));
                bool success;
                var prefab = temporary && keep
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(source, change.TargetPath, InteractionMode.AutomatedAction, out success)
                    : PrefabUtility.SaveAsPrefabAsset(source, change.TargetPath, out success);
                if (!success || prefab == null) return "Unity could not save the prefab at " + change.TargetPath + ".";
                if (temporary && keep)
                {
                    Undo.RegisterCreatedObjectUndo(source, "Add " + source.name + " prefab");
                    EditorSceneManager.MarkSceneDirty(source.scene);
                    retained = true;
                }
                AssetDatabase.SaveAssets();
                BridgeLog.Success("prefab", "Created prefab " + change.TargetPath);
                return null;
            }
            finally
            {
                if (temporary && !retained && source != null) UnityEngine.Object.DestroyImmediate(source);
            }
        }

        public static string FirstString(JsonValue value, params string[] keys)
        {
            foreach (var key in keys)
            {
                var candidate = value[key];
                if (!candidate.IsNull)
                {
                    string text = candidate.AsString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            return string.Empty;
        }
    }
}
