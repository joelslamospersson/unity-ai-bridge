using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityAIBridge
{
    /// <summary>
    /// Converts an assistant action object into a staged change (or a safe immediate
    /// read/refresh). Nothing here writes to the project directly.
    /// </summary>
    public static class ActionRouter
    {
        public static BridgeChange StageAction(JsonValue action, string source)
        {
            if (action == null || !action.IsObject)
                throw new BridgeException("Action must be a JSON object.");

            string type = action["type"].AsString(action["action"].AsString()).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(type))
                throw new BridgeException("Action is missing a 'type'.");

            string reason = action["reason"].AsString();
            switch (type)
            {
                case "create_script":
                case "create_file":
                    {
                        string path = RequiredPath(action);
                        if (type == "create_script" && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            throw new BridgeException("create_script requires a .cs path: " + path);
                        string content = action["content"].IsNull ? null : action["content"].AsString();
                        if (content == null) throw new BridgeException("create_file/create_script requires 'content'.");
                        return ChangeService.StageFile(ChangeKinds.CreateFile, path, content, source, Title("Create", path, reason));
                    }

                case "modify_script":
                case "modify_file":
                case "update_file":
                    {
                        string path = RequiredPath(action);
                        string content = action["content"].AsString();
                        if (content == null) throw new BridgeException("modify_file requires 'content' with the complete new file.");
                        return ChangeService.StageFile(ChangeKinds.ModifyFile, path, content, source, Title("Modify", path, reason));
                    }

                case "delete_file":
                    {
                        string path = RequiredPath(action);
                        return ChangeService.Stage(ChangeKinds.DeleteFile, Title("Delete", path, reason), path, null, JsonValue.NewObject(), source, true);
                    }

                case "create_folder":
                    {
                        string path = RequiredPath(action);
                        return ChangeService.Stage(ChangeKinds.CreateFolder, Title("Create folder", path, reason), path, null, JsonValue.NewObject(), source, false);
                    }

                case "create_gameobject":
                    {
                        var parameters = action;
                        string name = action["name"].AsString("GameObject");
                        return ChangeService.Stage(ChangeKinds.CreateGameObject, "Create GameObject " + name, action["parent"].AsString(), null, parameters, source, false);
                    }

                case "add_component":
                    {
                        string target = First(action, "target", "target_path", "gameobject", "path");
                        string component = First(action, "component", "component_type", "type_name");
                        if (string.IsNullOrEmpty(target)) throw new BridgeException("add_component requires 'target' (hierarchy path).");
                        if (string.IsNullOrEmpty(component)) throw new BridgeException("add_component requires 'component'.");
                        return ChangeService.Stage(ChangeKinds.AddComponent, "Add " + component + " to " + target, target, null, action, source, false);
                    }

                case "set_component":
                case "set_component_property":
                case "update_component":
                    {
                        string target = First(action, "target", "target_path", "gameobject", "path");
                        if (string.IsNullOrEmpty(target)) throw new BridgeException("update_component requires 'target' (hierarchy path).");
                        return ChangeService.Stage(ChangeKinds.SetComponentProperty, "Update " + target, target, null, action, source, false);
                    }

                case "create_prefab":
                    {
                        string path = RequiredPath(action);
                        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("create_prefab path must end with .prefab");
                        string sourceAsset = action["source_asset"].AsString();
                        string target = action["target"].AsString();
                        if (!string.IsNullOrEmpty(sourceAsset))
                        {
                            if (!string.IsNullOrEmpty(target) || !action["spec"].IsNull)
                                throw new BridgeException("create_prefab accepts one source: source_asset, target, or spec.");
                            string full;
                            string error = Permissions.CheckRead(sourceAsset, out full);
                            if (error != null) throw new BridgeException(error);
                            sourceAsset = BridgePaths.ToProjectRelative(full);
                            if (AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(sourceAsset) == null)
                                throw new BridgeException("Prefab source asset not found: " + sourceAsset);
                            var sourcePrefab = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(sourceAsset);
                            int missingScripts = AssetService.InspectPrefabScripts(sourcePrefab)["missing_script_count"].AsInt();
                            if (missingScripts > 0 && !action["remove_missing_scripts"].AsBool())
                                throw new BridgeException("Source prefab contains " + missingScripts + " missing script reference(s), so Unity cannot save it. Inspect the prefab validation report. To create a visual copy, explicitly set remove_missing_scripts:true; broken references will be removed from the new copy only and gameplay is not restored.");
                            action["source_asset"] = sourceAsset;
                        }
                        else if (string.IsNullOrEmpty(target) && !action["spec"].IsObject)
                            throw new BridgeException("create_prefab requires source_asset, target, or spec. Use source_asset to reuse an existing prefab; no setup script is needed.");
                        foreach (var existing in ChangeService.All)
                        {
                            if (existing.Kind != ChangeKinds.CreatePrefab || existing.TargetPath != path ||
                                (existing.Status != ChangeStatus.Pending && existing.Status != ChangeStatus.Approved)) continue;
                            var before = existing.Parameters;
                            if (before["source_asset"].AsString() == action["source_asset"].AsString() &&
                                before["target"].AsString() == action["target"].AsString() &&
                                before["name"].AsString() == action["name"].AsString() &&
                                before["keep_in_scene"].AsBool() == action["keep_in_scene"].AsBool() &&
                                before["remove_missing_scripts"].AsBool() == action["remove_missing_scripts"].AsBool() &&
                                before["spec"].ToJson(false) == action["spec"].ToJson(false)) return existing;
                            throw new BridgeException("A different prefab change is already staged for " + path + ". Review the existing pending change first.");
                        }
                        return ChangeService.Stage(ChangeKinds.CreatePrefab, Title("Create prefab", path, reason), path, null, action, source, false);
                    }

                case "create_material":
                    {
                        string path = action["path"].AsString(action["asset_path"].AsString());
                        if (string.IsNullOrEmpty(path)) throw new BridgeException("create_material requires 'path'.");
                        if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) path += ".mat";
                        return ChangeService.Stage(ChangeKinds.CreateMaterial, "Create material " + Path.GetFileName(path), path, null, action, source, false);
                    }

                case "create_animation_clip":
                case "create_animator_controller":
                    return ChangeService.Stage(type, null, RequiredPath(action), null, action, source, false);

                case "assign_sprite":
                    {
                        string target = First(action, "target", "target_path", "gameobject");
                        string sprite = First(action, "sprite", "sprite_path");
                        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(sprite))
                            throw new BridgeException("assign_sprite requires 'target' and 'sprite'.");
                        return ChangeService.Stage(ChangeKinds.AssignSprite, "Assign sprite to " + target, target, null, action, source, false);
                    }

                case "import_sprite":
                    {
                        string destination = First(action, "path", "destination_path", "destination");
                        if (string.IsNullOrEmpty(destination)) throw new BridgeException("import_sprite requires 'path' (destination asset path).");

                        string base64 = action["image_base64"].AsString(action["image_base64_png"].AsString());
                        if (!string.IsNullOrEmpty(base64))
                        {
                            string incoming = StoreIncomingImage(base64);
                            var parameters = action;
                            parameters["incoming_path"] = incoming;
                            return ChangeService.Stage(ChangeKinds.ImportSprite, "Import sprite " + Path.GetFileName(destination), destination, null, parameters, source, false);
                        }

                        string sourcePath = First(action, "source_path", "source");
                        if (string.IsNullOrEmpty(sourcePath))
                            throw new BridgeException("import_sprite requires 'source_path' (an image inside the project) or 'image_base64'.");
                        return ChangeService.Stage(ChangeKinds.ImportSprite, "Import sprite " + Path.GetFileName(destination), destination, null, action, source, false);
                    }

                case "configure_sprite":
                case "set_sprite_import":
                    {
                        string path = First(action, "path", "asset_path");
                        if (string.IsNullOrEmpty(path)) throw new BridgeException("set_sprite_import requires 'path'.");
                        return ChangeService.Stage(ChangeKinds.ConfigureSprite, "Update sprite settings " + Path.GetFileName(path), path, null, action, source, false);
                    }

                case "slice_sprite":
                    {
                        string path = First(action, "path", "asset_path");
                        if (string.IsNullOrEmpty(path)) throw new BridgeException("slice_sprite requires 'path'.");
                        return ChangeService.Stage(ChangeKinds.SliceSprite, "Slice " + Path.GetFileName(path), path, null, action, source, false);
                    }

                case "create_scriptable_object":
                    {
                        string path = First(action, "path", "asset_path");
                        string typeName = First(action, "scriptable_type", "type_name", "type");
                        if (string.IsNullOrEmpty(path)) throw new BridgeException("create_scriptable_object requires 'path'.");
                        if (string.IsNullOrEmpty(typeName)) throw new BridgeException("create_scriptable_object requires the ScriptableObject type name.");
                        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("create_scriptable_object needs an .asset path, not a script.");
                        return ChangeService.Stage(ChangeKinds.CreateScriptableObject, "Create " + typeName + " asset", path, null, action, source, false);
                    }

                case "save_scene":
                    return ChangeService.Stage(ChangeKinds.SaveScene, "Save active scene", string.Empty, null, action, source, false);

                case "refresh_assets":
                    AssetDatabase.Refresh();
                    BridgeLog.Info("assets", "Asset database refreshed.");
                    return null;

                case "read_file":
                    throw new BridgeException("read_file is a read-only action and must be handled by the MCP tool layer, not staged.");

                default:
                    throw new BridgeException("Unsupported action type: '" + type + "'. Supported: create_script, modify_file, delete_file, create_folder, create_gameobject, add_component, update_component, create_prefab, create_material, assign_sprite, import_sprite, set_sprite_import, slice_sprite, create_scriptable_object, save_scene, refresh_assets.");
            }
        }

        static string RequiredPath(JsonValue action)
        {
            string path = First(action, "path", "file", "target_path");
            if (string.IsNullOrEmpty(path)) throw new BridgeException("Action requires a 'path'.");
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !Path.IsPathRooted(path))
                path = "Assets/" + path.TrimStart('/');
            return path;
        }

        /// <summary>
        /// Stage an import from an arbitrary local image chosen in the Editor UI. The image
        /// is copied into the project's Library staging folder first; the project itself is
        /// only touched when the change is applied.
        /// </summary>
        public static BridgeChange StageSpriteImportFromFile(string sourceFullPath, string destinationAssetPath, JsonValue settings, string source)
        {
            if (string.IsNullOrEmpty(sourceFullPath) || !File.Exists(sourceFullPath))
                throw new BridgeException("Image not found: " + sourceFullPath);
            string extension = Path.GetExtension(sourceFullPath).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".tga" && extension != ".bmp")
                throw new BridgeException("Unsupported image format: " + extension);

            BridgePaths.EnsureDirectory(BridgePaths.IncomingDir);
            string incoming = Path.Combine(BridgePaths.IncomingDir, Guid.NewGuid().ToString("N") + extension);
            File.Copy(sourceFullPath, incoming, true);

            var parameters = settings != null ? settings : JsonValue.NewObject();
            parameters["incoming_path"] = incoming;
            return ChangeService.Stage(ChangeKinds.ImportSprite, "Import " + Path.GetFileName(destinationAssetPath), destinationAssetPath, null, parameters, source ?? "ui", false);
        }

        static string Title(string verb, string path, string reason)
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(reason) ? verb + " " + name : verb + " " + name + " — " + reason;
        }

        static string First(JsonValue value, params string[] keys)
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

        static string StoreIncomingImage(string base64)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new BridgeException("image_base64 is not valid base64."); }
            if (bytes.Length == 0) throw new BridgeException("image_base64 decoded to an empty file.");
            if (bytes.Length > 8 * 1024 * 1024) throw new BridgeException("image_base64 is too large (limit 8 MB).");

            string extension = DetectImageExtension(bytes);
            if (extension == null) throw new BridgeException("image_base64 is not a recognized PNG or JPEG image.");

            BridgePaths.EnsureDirectory(BridgePaths.IncomingDir);
            string path = Path.Combine(BridgePaths.IncomingDir, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        static string DetectImageExtension(byte[] bytes)
        {
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            return null;
        }

        /// <summary>Parses a JSON action document (with an "actions" array or a single action).</summary>
        public static List<JsonValue> ExtractActions(JsonValue document)
        {
            var result = new List<JsonValue>();
            if (document == null) return result;
            if (document.IsArray)
            {
                foreach (var item in document.ArrayValue) if (item.IsObject) result.Add(item);
                return result;
            }
            if (!document.IsObject) return result;
            var actions = document["actions"];
            if (actions.IsArray)
            {
                foreach (var item in actions.ArrayValue) if (item.IsObject) result.Add(item);
                return result;
            }
            if (!document["type"].IsNull) result.Add(document);
            return result;
        }
    }
}
