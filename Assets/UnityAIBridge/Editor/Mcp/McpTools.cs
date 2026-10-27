using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    public sealed class McpTool
    {
        public string Name;
        public string Description;
        public JsonValue InputSchema;
        public Func<JsonValue, JsonValue> Handler;
        public bool IsWrite;
    }

    /// <summary>
    /// The MCP tool surface. Read tools execute immediately; write tools only stage a
    /// change for human approval (unless Project Write + auto-apply is explicitly on).
    /// </summary>
    public static class McpTools
    {
        public const string ServerVersion = "2.0.0";

        static List<McpTool> _tools;

        public static List<McpTool> Tools
        {
            get
            {
                if (_tools == null) _tools = Build();
                return _tools;
            }
        }

        /// <summary>Call from any thread. Unity API work is marshalled to the main thread.</summary>
        public static JsonValue InvokeFromAnyThread(string name, JsonValue arguments)
        {
            try
            {
                return MainThread.Invoke(() => InvokeOnMain(name, arguments), 120000);
            }
            catch (Exception e)
            {
                return Fail("Tool '" + name + "' failed: " + e.Message);
            }
        }

        public static JsonValue InvokeOnMain(string name, JsonValue arguments)
        {
            arguments = arguments ?? JsonValue.NewObject();
            McpTool tool = null;
            foreach (var candidate in Tools)
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) { tool = candidate; break; }
            }
            if (tool == null) return Fail("Unknown tool: " + name + ". Call tools/list to see the available tools.");

            if (tool.IsWrite && !Permissions.CanPropose)
                return Fail("The Unity bridge is in Read Only mode. Ask the user to switch to 'Ask Before Editing' in the Unity AI Bridge window.");

            try
            {
                var result = tool.Handler(arguments);
                if (result == null) return Ok(JsonValue.Obj("ok", true));
                // MCP requires every tool result to be a content envelope. Handlers that
                // already produce one (Ok/Fail) are passed through unchanged.
                if (result.IsObject && result["content"].IsArray) return result;
                return Ok(result);
            }
            catch (BridgeException e) { return Fail(e.Message); }
            catch (Exception e)
            {
                BridgeLog.Error("mcp", "Tool " + name + " failed.", e);
                return Fail("Tool '" + name + "' failed: " + e.Message);
            }
        }

        public static List<ToolSpec> GetToolSpecs(bool includeWrites = true)
        {
            var result = new List<ToolSpec>();
            foreach (var tool in Tools)
            {
                if (tool.IsWrite && !includeWrites) continue;
                result.Add(new ToolSpec
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Schema = tool.InputSchema
                });
            }
            return result;
        }

        public static JsonValue ToolsList()
        {
            var array = JsonValue.NewArray();
            foreach (var tool in Tools)
            {
                array.Add(JsonValue.Obj(
                    "name", tool.Name,
                    "description", tool.Description,
                    "inputSchema", tool.InputSchema));
            }
            return array;
        }

        public static string Instructions
        {
            get
            {
                return "Unity AI Bridge exposes this Unity project to ChatGPT. Start with project_summary, then inspect files/scene as needed. " +
                       "Use propose_file_changes (or create_gameobject/update_component/create_prefab/import_sprite) to change the project: these only STAGE changes inside the Unity Editor. " +
                       "A human must review the diff and approve them in Tools > Unity AI Bridge > Pending Changes; then call apply_approved_changes. " +
                       "Never claim something was applied before apply_approved_changes returns success. Use focus_editor_window when the user asks to open or focus the running Unity Editor. All paths are project-relative and must start with Assets/. " +
                       "Reuse existing scripts and assets. Never ask for API keys or secrets.";
            }
        }

        static JsonValue Ok(JsonValue structured)
        {
            return JsonValue.Obj(
                "content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", structured.ToJson(true))),
                "isError", false,
                "structuredContent", structured);
        }

        static JsonValue Fail(string message)
        {
            return JsonValue.Obj(
                "content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", message)),
                "isError", true);
        }

        static JsonValue Prop(string type, string description)
        {
            return JsonValue.Obj("type", type, "description", description);
        }

        static JsonValue ArrayProp(string itemType, string description)
        {
            return JsonValue.Obj("type", "array", "description", description, "items", JsonValue.Obj("type", itemType));
        }

        static JsonValue Schema(JsonValue properties, params string[] required)
        {
            var schema = JsonValue.Obj("type", "object", "properties", properties, "additionalProperties", false);
            if (required != null && required.Length > 0)
            {
                var req = JsonValue.NewArray();
                foreach (var name in required) req.Add(name);
                schema["required"] = req;
            }
            return schema;
        }

        static JsonValue StageAction(JsonValue action)
        {
            var change = ActionRouter.StageAction(action, "mcp");
            if (change == null)
                return JsonValue.Obj("ok", true, "status", "executed", "message", "Operation completed immediately.");

            var result = JsonValue.Obj(
                "id", change.Id,
                "kind", change.Kind,
                "title", change.Title,
                "target", change.TargetPath,
                "status", change.Status,
                "message", change.Status == ChangeStatus.Applied
                    ? "Applied automatically because Project Write Access + auto-apply is enabled."
                    : "Staged and waiting for human approval in Tools > Unity AI Bridge > Pending Changes. Call apply_approved_changes after the human approves.");
            if (!string.IsNullOrEmpty(change.Diff)) result["diff"] = Truncate(change.Diff, 20000);
            if (!string.IsNullOrEmpty(change.Error)) result["error"] = change.Error;
            return result;
        }

        static string Truncate(string text, int max)
        {
            if (text == null) return null;
            return text.Length <= max ? text : text.Substring(0, max) + "\n... truncated ...";
        }

        static List<McpTool> Build()
        {
            var tools = new List<McpTool>();

            tools.Add(new McpTool
            {
                Name = "project_summary",
                Description = "Use this first. Returns the Unity version, render pipeline, active scene, packages, asset counts, folder layout and a gap analysis of the Unity project.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.NewObject()),
                Handler = args => ProjectService.Summary()
            });

            tools.Add(new McpTool
            {
                Name = "list_project_files",
                Description = "List files and folders inside the Unity project. Includes matching subfolders even when a folder contains no files. Use this to discover scripts, prefabs, sprites or folder layout before editing.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Project-relative folder, e.g. Assets/Scripts. Defaults to Assets."),
                    "recursive", Prop("boolean", "Search subfolders (default true)."),
                    "extensions", ArrayProp("string", "Optional extension filter for files, e.g. [\".cs\",\".prefab\"]."),
                    "max", Prop("integer", "Maximum entries per list (default 200, hard cap 2000)."))),
                Handler = args =>
                {
                    string dir = args["path"].AsString("Assets");
                    bool recursive = args["recursive"].AsBool(true);
                    int max = Mathf.Clamp(args["max"].AsInt(200), 1, 2000);
                    string[] extensions = null;
                    if (args["extensions"].IsArray)
                    {
                        var list = new List<string>();
                        foreach (var item in args["extensions"].ArrayValue) list.Add(item.AsString());
                        if (list.Count > 0) extensions = list.ToArray();
                    }
                    var directories = FileService.ListDirectories(dir, recursive, max);
                    var folders = JsonValue.NewArray();
                    foreach (var folder in directories) folders.Add(folder);
                    var files = FileService.ListFiles(dir, recursive, max, extensions);
                    var array = JsonValue.NewArray();
                    foreach (var file in files)
                        array.Add(JsonValue.Obj("path", file.RelativePath, "size", file.Size, "modified", file.ModifiedUtc.ToString("o")));
                    return JsonValue.Obj(
                        "folder", dir,
                        "count", array.Count,
                        "files", array,
                        "directory_count", folders.Count,
                        "directories", folders);
                }
            });

            tools.Add(new McpTool
            {
                Name = "read_project_file",
                Description = "Read a bounded page of UTF-8 project text, including large files. Use start_line/line_count for exact ranges; follow next_start_line and next_start_column when truncated. Prefer inspect_asset for prefab hierarchy and create_prefab source_asset for prefab reuse; reading YAML is not required.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Project-relative file path."),
                    "start_line", Prop("integer", "First line, 1-based (default 1)."),
                    "start_column", Prop("integer", "Zero-based character within the first line, for long-line continuation (default 0)."),
                    "line_count", Prop("integer", "Maximum lines to return (default 200, maximum 500)."),
                    "max_chars", Prop("integer", "Maximum characters in this page (default 6000, maximum 8000).")), "path"),
                Handler = args => Ok(FileService.ReadTextPage(args["path"].AsString(), args["start_line"].AsInt(1),
                    args["line_count"].AsInt(200), args["max_chars"].AsInt(6000), args["start_column"].AsInt(0)))
            });

            tools.Add(new McpTool
            {
                Name = "propose_file_changes",
                Description = "Use this to create, overwrite or delete text project files. Provide COMPLETE file content. Changes are staged as diffs for human approval in the Unity Editor.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "changes", JsonValue.Obj("type", "array", "description", "List of changes.", "items", JsonValue.Obj(
                        "type", "object",
                        "properties", JsonValue.Obj(
                            "kind", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("create", "modify", "delete")),
                            "path", Prop("string", "Project-relative path under Assets/."),
                            "content", Prop("string", "Complete new file content (required for create/modify).")))))
                    , "changes"),
                Handler = args =>
                {
                    var results = JsonValue.NewArray();
                    var items = args["changes"];
                    var list = new List<JsonValue>();
                    if (items.IsArray) list.AddRange(items.ArrayValue);
                    else if (!args["path"].IsNull)
                    {
                        list.Add(JsonValue.Obj("kind", args["kind"].AsString("create"), "path", args["path"].AsString(), "content", args["content"].AsString()));
                    }
                    if (list.Count == 0) return Fail("Provide a non-empty 'changes' array.");

                    foreach (var item in list)
                    {
                        string kind = item["kind"].AsString("create").ToLowerInvariant();
                        string path = item["path"].AsString();
                        string content = item["content"].AsString();
                        try
                        {
                            BridgeChange change;
                            if (kind == "delete")
                                change = ChangeService.Stage(ChangeKinds.DeleteFile, "Delete " + Path.GetFileName(path), path, null, JsonValue.NewObject(), "mcp", true);
                            else if (kind == "modify")
                                change = ChangeService.StageFile(ChangeKinds.ModifyFile, path, content, "mcp");
                            else
                                change = ChangeService.StageFile(ChangeKinds.CreateFile, path, content, "mcp");

                            var entry = JsonValue.Obj("id", change.Id, "path", path, "status", change.Status, "title", change.Title);
                            if (!string.IsNullOrEmpty(change.Diff)) entry["diff"] = Truncate(change.Diff, 20000);
                            results.Add(entry);
                        }
                        catch (Exception e)
                        {
                            results.Add(JsonValue.Obj("path", path, "error", e.Message));
                        }
                    }
                    return JsonValue.Obj(
                        "count", results.Count,
                        "changes", results,
                        "next_step", "A human must approve these in Unity (Pending Changes tab), then call apply_approved_changes.");
                }
            });

            tools.Add(new McpTool
            {
                Name = "apply_approved_changes",
                Description = "Apply changes that a human already approved in the Unity Editor. Never assumes approval: unapproved changes are skipped.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj()),
                Handler = args =>
                {
                    var results = ChangeService.ApplyAllApproved();
                    var array = JsonValue.NewArray();
                    foreach (var result in results) array.Add(result);
                    return JsonValue.Obj("applied", results.Count, "results", array);
                }
            });

            tools.Add(new McpTool
            {
                Name = "request_approval",
                Description = "Request an in-chat review of specific existing pending changes by ID, for example when the user wants to finish a previously staged task. The chat waits for Accept, Deny, or Comment, then returns the decision and actual change status. Never approve changes on behalf of the user.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj("ids", ArrayProp("string", "Specific pending change IDs to present for review.")), "ids"),
                Handler = args =>
                {
                    if (!args["ids"].IsArray || args["ids"].Count == 0) throw new BridgeException("Supply specific pending change IDs.");
                    var changes = JsonValue.NewArray();
                    foreach (var item in args["ids"].ArrayValue)
                    {
                        var change = ChangeService.Find(item.AsString());
                        if (change == null || (change.Status != ChangeStatus.Pending && change.Status != ChangeStatus.Approved))
                            throw new BridgeException("Change is not pending review: " + item.AsString());
                        changes.Add(JsonValue.Obj("id", change.Id, "title", change.Title, "path", change.TargetPath, "status", change.Status));
                    }
                    return JsonValue.Obj("changes", changes, "status", "AwaitingReview");
                }
            });

            tools.Add(new McpTool
            {
                Name = "discard_pending_changes",
                Description = "Discard specific obsolete, unapproved proposals by ID when replacing your own failed approach or when the user asks. Does not undo applied changes or discard human-approved changes. Validate IDs with get_pending_changes first.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj("ids", ArrayProp("string", "IDs of Pending or Failed changes to discard.")), "ids"),
                Handler = args =>
                {
                    if (!args["ids"].IsArray || args["ids"].Count == 0) throw new BridgeException("Provide at least one pending change ID.");
                    var changes = new List<BridgeChange>();
                    foreach (var item in args["ids"].ArrayValue)
                    {
                        var change = ChangeService.Find(item.AsString());
                        if (change == null || (change.Status != ChangeStatus.Pending && change.Status != ChangeStatus.Failed))
                            throw new BridgeException("Only unapproved Pending or Failed changes can be discarded: " + item.AsString());
                        changes.Add(change);
                    }
                    var discarded = JsonValue.NewArray();
                    foreach (var change in changes) { ChangeService.Reject(change.Id); discarded.Add(change.Id); }
                    return JsonValue.Obj("discarded", discarded, "message", "Obsolete proposals discarded. No applied files or scene objects were changed.");
                }
            });

            tools.Add(new McpTool
            {
                Name = "get_pending_changes",
                Description = "List staged changes with their status, target path and optional diff. Use this to check whether the human approved something.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj(
                    "status", Prop("string", "Optional filter: Pending, Approved, Applied, Rejected, Failed."),
                    "include_diff", Prop("boolean", "Include unified diffs (default false)."))),
                Handler = args =>
                {
                    string filter = args["status"].AsString();
                    bool includeDiff = args["include_diff"].AsBool(false);
                    var array = JsonValue.NewArray();
                    foreach (var change in ChangeService.All)
                    {
                        if (!string.IsNullOrEmpty(filter) && !string.Equals(change.Status, filter, StringComparison.OrdinalIgnoreCase)) continue;
                        var entry = JsonValue.Obj(
                            "id", change.Id,
                            "kind", change.Kind,
                            "title", change.Title,
                            "target", change.TargetPath,
                            "status", change.Status,
                            "source", change.Source,
                            "created", change.CreatedUtc);
                        if (!string.IsNullOrEmpty(change.Error)) entry["error"] = change.Error;
                        if (includeDiff && !string.IsNullOrEmpty(change.Diff)) entry["diff"] = Truncate(change.Diff, 20000);
                        array.Add(entry);
                    }
                    return JsonValue.Obj("count", array.Count, "changes", array);
                }
            });

            tools.Add(new McpTool
            {
                Name = "inspect_scene",
                Description = "Inspect the active Unity scene hierarchy, including selected components and transforms. Use this before moving or parenting objects.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj(
                    "depth", Prop("integer", "Maximum hierarchy depth (default 6)."),
                    "max_nodes", Prop("integer", "Maximum nodes (default 500)."),
                    "include_components", Prop("boolean", "Include component type names (default true)."))),
                Handler = args => SceneService.DescribeHierarchy(
                    Mathf.Clamp(args["depth"].AsInt(6), 1, 20),
                    Mathf.Clamp(args["max_nodes"].AsInt(500), 1, 2000),
                    args["include_components"].AsBool(true))
            });

            tools.Add(new McpTool
            {
                Name = "list_gameobjects",
                Description = "List hierarchy paths of every GameObject in the active scene. Use the exact path for create_gameobject parent / update_component target.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj("max", Prop("integer", "Maximum paths (default 500)."))),
                Handler = args =>
                {
                    var paths = SceneService.ListGameObjectPaths(Mathf.Clamp(args["max"].AsInt(500), 1, 2000));
                    var array = JsonValue.NewArray();
                    foreach (var path in paths) array.Add(path);
                    return JsonValue.Obj("count", array.Count, "paths", array);
                }
            });

            tools.Add(new McpTool
            {
                Name = "create_gameobject",
                Description = "Stage creation of a GameObject in the active scene (primitive optional) with components and an optional sprite. Requires human approval.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "name", Prop("string", "GameObject name."),
                    "parent", Prop("string", "Optional hierarchy path, e.g. Example/Parent."),
                    "primitive", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("empty", "cube", "sphere", "capsule", "cylinder", "plane", "quad")),
                    "position", ArrayProp("number", "[x,y,z] local position."),
                    "rotation", ArrayProp("number", "[x,y,z] local Euler rotation."),
                    "scale", ArrayProp("number", "[x,y,z] local scale."),
                    "components", ArrayProp("string", "Component type names, e.g. [\"SpriteRenderer\",\"BoxCollider2D\"]."),
                    "sprite", Prop("string", "Optional sprite asset path.")), "name"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "create_gameobject";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "update_component",
                Description = "Stage a property change on a component (SerializedProperty path, e.g. m_SortingOrder) of a scene GameObject. Provide either 'property'+'value' or a 'properties' object.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "target", Prop("string", "Hierarchy path of the GameObject."),
                    "component", Prop("string", "Component type name, e.g. SpriteRenderer."),
                    "property", Prop("string", "SerializedProperty path or friendly name (position, scale, m_SortingOrder...)."),
                    "value", JsonValue.Obj("description", "New value: number, string, bool, array/object for vectors/colors."),
                    "properties", JsonValue.Obj("type", "object", "description", "Alternatively, multiple property/value pairs.")),
                    "target", "component"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "update_component";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "create_prefab",
                Description = "Stage a prefab directly from an existing source_asset prefab, a scene target, or a new spec. For asset reuse use source_asset + path + name + keep_in_scene:true; this creates the prefab and its scene instance on approval, without a setup script.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Destination path ending in .prefab."),
                    "source_asset", Prop("string", "Existing project prefab asset to reuse, e.g. Assets/Maps/Level.prefab. Does not require reading its YAML."),
                    "name", Prop("string", "Name of the new prefab root and optional scene instance. Defaults to the destination filename."),
                    "remove_missing_scripts", Prop("boolean", "Explicitly remove unresolved script references from a NEW source_asset copy only, when inspect_asset reports missing scripts. Defaults false; original assets and valid scripts are preserved. Does not restore gameplay."),
                    "target", Prop("string", "Alternatively, hierarchy path of a source scene GameObject."),
                    "keep_in_scene", Prop("boolean", "For source_asset/spec, also keep an Undo-supported scene instance connected to the NEW prefab (default false)."),
                    "spec", JsonValue.Obj("type", "object", "description", "Optional create_gameobject spec if there is no scene object.")),
                    "path"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "create_prefab";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "import_sprite",
                Description = "Stage importing an image as a Unity Sprite with platform-appropriate settings. Provide source_path (image already in the project) or image_base64 (PNG/JPEG you generated elsewhere). This copies the file; it does not generate art.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Destination asset path under Assets/, e.g. Assets/Art/Example.png."),
                    "source_path", Prop("string", "Existing image path inside the project."),
                    "image_base64", Prop("string", "Base64 PNG/JPEG data (max 8 MB)."),
                    "sprite_mode", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Single", "Multiple")),
                    "pixels_per_unit", Prop("number", "Pixels per unit (default 100)."),
                    "filter", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Point", "Bilinear", "Trilinear")),
                    "compression", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Uncompressed", "Compressed", "CompressedHQ", "CompressedLQ")),
                    "max_size", Prop("integer", "Maximum texture size (default 2048)."),
                    "alpha_is_transparency", Prop("boolean", "Enable alpha transparency.")), "path"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "import_sprite";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "set_sprite_import",
                Description = "Stage changing texture import settings (sprite mode, PPU, filter, compression, max size) of an existing image.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Image asset path."),
                    "sprite_mode", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Single", "Multiple")),
                    "pixels_per_unit", Prop("number", "Pixels per unit."),
                    "filter", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Point", "Bilinear", "Trilinear")),
                    "compression", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("Uncompressed", "Compressed", "CompressedHQ", "CompressedLQ")),
                    "max_size", Prop("integer", "Maximum texture size.")), "path"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "set_sprite_import";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "slice_sprite",
                Description = "Stage slicing a sprite sheet into a rows x columns grid using Unity's grid slicing.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Sprite sheet asset path."),
                    "rows", Prop("integer", "Grid rows."),
                    "columns", Prop("integer", "Grid columns.")), "path", "rows", "columns"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "slice_sprite";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "assign_sprite",
                Description = "Stage assigning a sprite to a SpriteRenderer or UI Image on a scene GameObject.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "target", Prop("string", "Hierarchy path."),
                    "component", JsonValue.Obj("type", "string", "enum", JsonValue.Arr("SpriteRenderer", "Image")),
                    "sprite", Prop("string", "Sprite asset path.")), "target", "sprite"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "assign_sprite";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "create_material",
                Description = "Stage creating a material with a base colour and optional texture using an available shader.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Destination .mat path."),
                    "color", Prop("string", "Hex colour, e.g. #4A90D9."),
                    "texture", Prop("string", "Optional texture asset path."),
                    "shader", Prop("string", "Optional shader name.")), "path"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "create_material";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "create_scriptable_object",
                Description = "Stage creating a ScriptableObject asset from an existing ScriptableObject-derived C# type, optionally setting serialized properties.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.Obj(
                    "path", Prop("string", "Destination .asset path."),
                    "scriptable_type", Prop("string", "Type name, e.g. UnitDefinition."),
                    "properties", JsonValue.Obj("type", "object", "description", "Serialized property/value pairs.")), "path", "scriptable_type"),
                Handler = args =>
                {
                    var action = JsonValue.Parse(args.ToJson(false));
                    action["type"] = "create_scriptable_object";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "inspect_asset",
                Description = "Inspect an asset's importer, sprite rects, prefab components, material shader/properties or animator states.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj("path", Prop("string", "Asset path."),
                    "depth", Prop("integer", "Prefab hierarchy depth (default 2, maximum 8)."),
                    "max_nodes", Prop("integer", "Prefab hierarchy node budget (default 60, maximum 200).")), "path"),
                Handler = args => Ok(AssetService.InspectAsset(args["path"].AsString(), args["depth"].AsInt(2), args["max_nodes"].AsInt(60)))
            });

            tools.Add(new McpTool
            {
                Name = "read_console_errors",
                Description = "Read current Unity compiler diagnostics and console errors. Use this after applying script changes to check for compile errors.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.Obj(
                    "max", Prop("integer", "Maximum entries (default 40)."),
                    "include_warnings", Prop("boolean", "Include warnings (default false)."))),
                Handler = args =>
                {
                    int max = Mathf.Clamp(args["max"].AsInt(40), 1, 200);
                    bool warnings = args["include_warnings"].AsBool(false);
                    var diagnostics = JsonValue.NewArray();
                    foreach (var diagnostic in ConsoleService.GetCompilerDiagnostics(max))
                        diagnostics.Add(JsonValue.Obj("severity", diagnostic.Severity, "file", diagnostic.File, "line", diagnostic.Line, "column", diagnostic.Column, "message", diagnostic.Message));
                    var errors = JsonValue.NewArray();
                    foreach (var error in ConsoleService.GetErrors(max, warnings))
                        errors.Add(JsonValue.Obj("severity", error.Severity, "time", error.TimeUtc.ToString("o"), "message", error.Message, "stack", Truncate(error.Stack, 4000)));
                    return JsonValue.Obj(
                        "compiling", EditorApplication.isCompiling,
                        "updating", EditorApplication.isUpdating,
                        "compiler_diagnostics", diagnostics,
                        "console_errors", errors);
                }
            });

            tools.Add(new McpTool
            {
                Name = "refresh_assets",
                Description = "Refresh the Unity AssetDatabase so newly written files are imported and scripts recompile. Safe to call repeatedly.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.NewObject()),
                Handler = args =>
                {
                    AssetDatabase.Refresh();
                    BridgeLog.Info("mcp", "AssetDatabase.Refresh() requested through MCP.");
                    return JsonValue.Obj("ok", true, "compiling", EditorApplication.isCompiling, "updating", EditorApplication.isUpdating);
                }
            });

            tools.Add(new McpTool
            {
                Name = "focus_editor_window",
                Description = "Bring the already-running Unity Editor window to the front and focus it. Use this when the user asks to open, show, or focus the Unity Editor or project.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.NewObject()),
                Handler = args => EditorFocusService.Focus()
            });

            tools.Add(new McpTool
            {
                Name = "save_scene",
                Description = "Stage saving the active scene. Requires human approval because it writes the scene asset.",
                IsWrite = true,
                InputSchema = Schema(JsonValue.NewObject()),
                Handler = args =>
                {
                    var action = JsonValue.NewObject();
                    action["type"] = "save_scene";
                    return StageAction(action);
                }
            });

            tools.Add(new McpTool
            {
                Name = "get_bridge_status",
                Description = "Return the current permission level, provider, MCP endpoint and pending-change counts.",
                IsWrite = false,
                InputSchema = Schema(JsonValue.NewObject()),
                Handler = args => JsonValue.Obj(
                    "version", ServerVersion,
                    "permission_level", Permissions.LevelName,
                    "permission_detail", Permissions.DescribeLevel,
                    "provider", ProviderFactory.DisplayName(BridgeSettings.Current.Provider),
                    "pending_changes", ChangeService.PendingCount,
                    "approved_changes", ChangeService.ApprovedCount,
                    "mcp_running", McpServer.Current != null && McpServer.Current.IsRunning,
                    "mcp_port", McpServer.Current != null ? McpServer.Current.Port : BridgeSettings.Current.McpPort,
                    "project", ProjectService.ProjectName,
                    "unity", Application.unityVersion,
                    "compiling", EditorApplication.isCompiling)
            });

            return tools;
        }
    }
}
