using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAIBridge
{
    /// <summary>Read-only project understanding: structure, architecture, packages, gaps.</summary>
    public static class ProjectService
    {
        public static bool UsesUrp
        {
            get
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline == null) pipeline = GraphicsSettings.defaultRenderPipeline;
                return pipeline != null && pipeline.GetType().FullName != null &&
                       pipeline.GetType().FullName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static string RenderPipelineName
        {
            get
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline == null) pipeline = GraphicsSettings.defaultRenderPipeline;
                if (pipeline == null) return "Built-in Render Pipeline";
                string path = AssetDatabase.GetAssetPath(pipeline);
                return pipeline.GetType().Name + (string.IsNullOrEmpty(path) ? string.Empty : " (" + path + ")");
            }
        }

        public static string ProjectName
        {
            get { return new DirectoryInfo(BridgePaths.ProjectRoot).Name; }
        }

        public static JsonValue Summary()
        {
            var summary = JsonValue.NewObject();
            summary["project"] = ProjectName;
            summary["unity_version"] = Application.unityVersion;
            summary["platform"] = Application.platform.ToString();
            summary["render_pipeline"] = RenderPipelineName;
            summary["urp"] = UsesUrp;
            summary["project_root"] = BridgePaths.ProjectRoot;
            summary["active_scene"] = SceneService.ActiveScene.IsValid() ? SceneService.ActiveScene.name : "(none)";
            summary["compiler_error_count"] = ConsoleService.GetCompilerDiagnostics(200).Count;
            summary["console_error_count"] = ConsoleService.GetErrors(200, false).Count;

            var packages = JsonValue.NewArray();
            var manifest = ReadManifest();
            if (manifest != null && manifest["dependencies"].IsObject)
            {
                foreach (var kv in manifest["dependencies"].Properties)
                    packages.Add(JsonValue.Obj("name", kv.Key, "version", kv.Value.AsString()));
            }
            summary["packages"] = packages;

            var folders = JsonValue.NewArray();
            foreach (var folder in FileService.ListDirectories("Assets"))
            {
                var files = FileService.ListFiles(folder, true, 2000, null);
                folders.Add(JsonValue.Obj("path", folder, "files", files.Count));
            }
            summary["folders"] = folders;

            var counts = JsonValue.NewObject();
            counts["scripts"] = CountAssets("t:MonoScript");
            counts["scenes"] = CountAssets("t:SceneAsset");
            counts["prefabs"] = CountAssets("t:Prefab");
            counts["materials"] = CountAssets("t:Material");
            counts["sprites"] = CountAssets("t:Sprite");
            counts["textures"] = CountAssets("t:Texture2D");
            counts["scriptable_objects"] = CountAssets("t:ScriptableObject");
            counts["animator_controllers"] = CountAssets("t:AnimatorController");
            summary["counts"] = counts;

            var buildScenes = JsonValue.NewArray();
            foreach (var scene in EditorBuildSettings.scenes)
                buildScenes.Add(JsonValue.Obj("path", scene.path, "enabled", scene.enabled));
            summary["build_scenes"] = buildScenes;

            summary["missing"] = MissingAnalysis();
            return summary;
        }

        static JsonValue _manifestCache;
        static DateTime _manifestCacheTime;

        static JsonValue ReadManifest()
        {
            try
            {
                string path = Path.Combine(BridgePaths.ProjectRoot, "Packages", "manifest.json");
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (_manifestCache != null && _manifestCacheTime == info.LastWriteTimeUtc) return _manifestCache;
                JsonValue value;
                string error;
                if (JsonValue.TryParse(File.ReadAllText(path, Encoding.UTF8), out value, out error))
                {
                    _manifestCache = value;
                    _manifestCacheTime = info.LastWriteTimeUtc;
                    return value;
                }
            }
            catch { }
            return null;
        }

        static int CountAssets(string filter)
        {
            try { return AssetDatabase.FindAssets(filter).Length; }
            catch { return 0; }
        }

        public static JsonValue MissingAnalysis()
        {
            var findings = JsonValue.NewArray();
            int scenes = CountAssets("t:SceneAsset");
            int prefabs = CountAssets("t:Prefab");
            int sprites = CountAssets("t:Sprite");
            int scriptables = CountAssets("t:ScriptableObject");
            int asmdefs = CountAssets("t:AssemblyDefinitionAsset");
            var scripts = FileService.ListScripts(400);
            int scriptCount = scripts.Count;

            AddFinding(findings, "scenes", "info", scenes == 0 ? "No scenes found in the project." : scenes + " scene(s) found.");
            AddFinding(findings, "prefabs", "info", prefabs == 0 ? "No prefabs found. Prefabs can make reusable objects easier to manage." : "Prefab library present (" + prefabs + ").");
            AddFinding(findings, "sprites", "info", sprites == 0 ? "No sprites/textures are imported. Use the Asset Manager to import images when needed." : "Sprites present (" + sprites + ").");
            AddFinding(findings, "scriptableobjects", "info", scriptables == 0 ? "No ScriptableObject assets found. They are optional data containers." : "ScriptableObject data present (" + scriptables + ").");
            AddFinding(findings, "scripts", "info", scriptCount == 0 ? "No scripts yet (excluding the bridge)." : scriptCount + " script(s) found (excluding the bridge).");
            AddFinding(findings, "assembly_definitions", "low", asmdefs == 0 ? "No assembly definitions. Optional, but they can speed up compilation as the project grows." : asmdefs + " assembly definition(s) found.");
            AddFinding(findings, "build_scenes", "low", EditorBuildSettings.scenes.Length == 0 ? "No scenes are in Build Settings; builds may start empty." : "Build Settings has " + EditorBuildSettings.scenes.Length + " scene(s).");
            AddFinding(findings, "console", "high", ConsoleService.GetCompilerDiagnostics(50).Count > 0 ? "The project currently has compiler errors (see Console tab)." : "No compiler errors captured.");

            return findings;
        }

        static void AddFinding(JsonValue array, string area, string severity, string message)
        {
            array.Add(JsonValue.Obj("area", area, "severity", severity, "message", message));
        }

        public static JsonValue ListProjectTree(int maxDepth = 3, int maxEntries = 500)
        {
            var root = JsonValue.NewObject();
            root["root"] = "Assets";
            var entries = JsonValue.NewArray();
            int budget = maxEntries;
            WalkDirectory("Assets", 0, maxDepth, entries, ref budget);
            root["entries"] = entries;
            root["truncated"] = budget <= 0;
            return root;
        }

        static void WalkDirectory(string assetFolder, int depth, int maxDepth, JsonValue entries, ref int budget)
        {
            if (depth > maxDepth || budget <= 0) return;
            var folders = FileService.ListDirectories(assetFolder);
            foreach (var folder in folders)
            {
                if (budget-- <= 0) return;
                entries.Add(JsonValue.Obj("type", "folder", "path", folder, "depth", depth));
                WalkDirectory(folder, depth + 1, maxDepth, entries, ref budget);
            }
            var files = FileService.ListFiles(assetFolder, false, Math.Max(1, budget), null);
            foreach (var file in files)
            {
                if (budget-- <= 0) return;
                entries.Add(JsonValue.Obj("type", "file", "path", file.RelativePath, "size", file.Size, "depth", depth));
            }
        }

        public static JsonValue ScriptInventory(int max = 300)
        {
            var array = JsonValue.NewArray();
            var scripts = FileService.ListScripts(max);
            foreach (var script in scripts)
            {
                var entry = JsonValue.NewObject();
                entry["path"] = script.RelativePath;
                entry["lines"] = 0;
                entry["classes"] = JsonValue.NewArray();
                try
                {
                    string text = File.ReadAllText(script.FullPath, Encoding.UTF8);
                    entry["lines"] = FileService.CountLines(text);
                    var classes = JsonValue.NewArray();
                    foreach (Match match in Regex.Matches(text, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.None))
                        classes.Add(match.Groups[1].Value);
                    entry["classes"] = classes;
                }
                catch { }
                array.Add(entry);
            }
            return array;
        }

        static string _agentContextCache;
        static DateTime _agentContextCacheTime;
        static string _agentContextCacheKey;

        /// <summary>
        /// Lightweight context for live chat. Script source is omitted; the agent reads
        /// exact files through tools. The snapshot is cached briefly and invalidated on imports.
        /// </summary>
        public static string BuildAgentContext(BridgeSettings settings, List<string> selectedFiles)
        {
            bool hasAttachments = selectedFiles != null && selectedFiles.Count > 0;
            string key = (settings.IncludeSceneContext ? "s" : "-") +
                         (settings.IncludeConsoleContext ? "c" : "-") +
                         (settings.IncludeFileTree ? "f" : "-") +
                         "|" + settings.MaxContextCharacters + "|" + settings.ProjectNotes;
            if (!hasAttachments && _agentContextCache != null && _agentContextCacheKey == key &&
                (DateTime.UtcNow - _agentContextCacheTime).TotalSeconds < 30)
                return _agentContextCache;

            string result;
            try { result = BuildContext(settings, selectedFiles, false); }
            catch (Exception e) { return "(project context unavailable: " + e.Message + ")"; }
            if (!hasAttachments)
            {
                _agentContextCache = result;
                _agentContextCacheKey = key;
                _agentContextCacheTime = DateTime.UtcNow;
            }
            return result;
        }

        public static void InvalidateContextCache()
        {
            _agentContextCache = null;
            _agentContextCacheKey = null;
        }

        public static string BuildContext(BridgeSettings settings, List<string> selectedFiles, bool includeFileContents = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## PROJECT SNAPSHOT");
            sb.AppendLine("Project: " + ProjectName);
            sb.AppendLine("Unity: " + Application.unityVersion);
            sb.AppendLine("Render pipeline: " + RenderPipelineName);
            sb.AppendLine("Platform: " + Application.platform);
            sb.AppendLine("Active scene: " + (SceneService.ActiveScene.IsValid() ? SceneService.ActiveScene.name : "(none)"));
            sb.AppendLine("Writable root: Assets/ (all paths must stay inside this Unity project)");
            if (!string.IsNullOrEmpty(settings.ProjectNotes))
            {
                sb.AppendLine();
                sb.AppendLine("## PROJECT NOTES FROM THE USER");
                sb.AppendLine(settings.ProjectNotes);
            }

            var manifest = ReadManifest();
            if (manifest != null && manifest["dependencies"].IsObject)
            {
                sb.AppendLine();
                sb.AppendLine("## PACKAGES");
                foreach (var kv in manifest["dependencies"].Properties)
                    sb.AppendLine("- " + kv.Key + " " + kv.Value.AsString());
            }

            if (settings.IncludeFileTree)
            {
                sb.AppendLine();
                sb.AppendLine("## ASSET TREE (folders)");
                foreach (var folder in FileService.ListDirectories("Assets")) sb.AppendLine("- " + folder);
                var rootFiles = FileService.ListFiles("Assets", false, 100, null);
                if (rootFiles.Count > 0)
                {
                    sb.AppendLine("Root files:");
                    foreach (var file in rootFiles) sb.AppendLine("- " + file.RelativePath);
                }
            }

            var scripts = FileService.ListScripts(300);
            sb.AppendLine();
            sb.AppendLine("## PROJECT SCRIPTS (" + scripts.Count + ")");
            foreach (var script in scripts)
            {
                string names = string.Empty;
                try
                {
                    string text = File.ReadAllText(script.FullPath, Encoding.UTF8);
                    var matches = Regex.Matches(text, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)");
                    var list = new List<string>();
                    foreach (Match match in matches) list.Add(match.Groups[1].Value);
                    names = list.Count > 0 ? " classes: " + string.Join(", ", list.ToArray()) : string.Empty;
                }
                catch { }
                sb.AppendLine("- " + script.RelativePath + " (" + FileService.FormatSize(script.Size) + ")" + names);
            }

            if (settings.IncludeExistingScripts && includeFileContents && scripts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## EXISTING SCRIPT SOURCE (reuse these instead of duplicating)");
                int budget = Math.Min(30000, settings.MaxContextCharacters / 2);
                foreach (var script in scripts)
                {
                    if (script.Size > 40000) continue;
                    if (budget <= 0) break;
                    string text;
                    try { text = File.ReadAllText(script.FullPath, Encoding.UTF8); }
                    catch { continue; }
                    if (text.Length > budget) text = text.Substring(0, budget) + "\n// ... truncated ...";
                    budget -= text.Length;
                    sb.AppendLine("### " + script.RelativePath);
                    sb.AppendLine("```csharp");
                    sb.AppendLine(text);
                    sb.AppendLine("```");
                }
            }

            if (settings.IncludeSceneContext)
            {
                sb.AppendLine();
                sb.AppendLine("## SCENE HIERARCHY");
                sb.AppendLine(SceneService.DescribeSceneForPrompt(120));
            }

            if (settings.IncludeConsoleContext)
            {
                sb.AppendLine();
                sb.AppendLine("## UNITY CONSOLE / COMPILER STATE");
                sb.AppendLine(ConsoleService.ForPrompt(15));
            }

            if (selectedFiles != null && selectedFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## FILES THE USER ATTACHED");
                foreach (var file in selectedFiles)
                {
                    string error;
                    string text = FileService.ReadText(file, out error, 200000);
                    if (text == null)
                    {
                        sb.AppendLine("### " + file + " (could not read: " + error + ")");
                        continue;
                    }
                    sb.AppendLine("### " + file);
                    sb.AppendLine("```");
                    sb.AppendLine(text);
                    sb.AppendLine("```");
                }
            }

            string result = FileService.RedactSecrets(sb.ToString());
            if (result.Length > settings.MaxContextCharacters)
                result = result.Substring(0, settings.MaxContextCharacters) + "\n... context truncated ...";
            return result;
        }

        public static string MissingAnalysisText()
        {
            var findings = MissingAnalysis();
            var sb = new StringBuilder();
            foreach (var finding in findings.ArrayValue)
            {
                sb.AppendLine("[" + finding["severity"].AsString() + "] " + finding["area"].AsString() + ": " + finding["message"].AsString());
            }
            return sb.ToString();
        }
    }

    internal sealed class ProjectContextAssetWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            ProjectService.InvalidateContextCache();
        }
    }
}
