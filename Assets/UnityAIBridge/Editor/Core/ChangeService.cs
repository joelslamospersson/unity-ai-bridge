using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    public static class ChangeKinds
    {
        public const string CreateFile = "create_file";
        public const string ModifyFile = "modify_file";
        public const string DeleteFile = "delete_file";
        public const string CreateFolder = "create_folder";
        public const string MoveFile = "move_file";
        public const string CreateGameObject = "create_gameobject";
        public const string AddComponent = "add_component";
        public const string SetComponentProperty = "set_component_property";
        public const string CreatePrefab = "create_prefab";
        public const string CreateMaterial = "create_material";
        public const string ImportSprite = "import_sprite";
        public const string ConfigureSprite = "configure_sprite";
        public const string SliceSprite = "slice_sprite";
        public const string AssignSprite = "assign_sprite";
        public const string CreateScriptableObject = "create_scriptable_object";
        public const string SaveScene = "save_scene";
    }

    public static class ChangeStatus
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Applied = "Applied";
        public const string Failed = "Failed";
    }

    [Serializable]
    public class BridgeChange
    {
        public string Id;
        public string Kind;
        public string Title;
        public string TargetPath;
        public string Content;
        public string ParametersJson = "{}";
        public string Diff;
        public string Status = ChangeStatus.Pending;
        public string Error;
        public string Source = "chat";
        public string CreatedUtc;
        public string AppliedUtc;
        public string BackupPath;
        public bool Destructive;
        public bool AcknowledgedDestructive;

        [NonSerialized] public bool Selected;

        public JsonValue Parameters
        {
            get
            {
                JsonValue value;
                string error;
                if (JsonValue.TryParse(ParametersJson, out value, out error)) return value;
                return JsonValue.NewObject();
            }
        }

        public bool IsFileChange
        {
            get
            {
                return Kind == ChangeKinds.CreateFile || Kind == ChangeKinds.ModifyFile ||
                       Kind == ChangeKinds.DeleteFile || Kind == ChangeKinds.CreateFolder ||
                       Kind == ChangeKinds.MoveFile;
            }
        }
    }

    [Serializable]
    internal class BridgeChangeList
    {
        public List<BridgeChange> Items = new List<BridgeChange>();
    }

    /// <summary>
    /// Holds proposed changes, persists them across domain reloads, and only applies
    /// them once a human has approved them (unless explicitly configured otherwise for
    /// non-destructive changes in Project Write Access mode).
    /// </summary>
    public static class ChangeService
    {
        static List<BridgeChange> _changes;
        static bool _loaded;

        public static event Action Changed;

        public static List<BridgeChange> All
        {
            get { EnsureLoaded(); return _changes; }
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _changes = new List<BridgeChange>();
            try
            {
                if (File.Exists(BridgePaths.PendingFile))
                {
                    var list = JsonUtility.FromJson<BridgeChangeList>(File.ReadAllText(BridgePaths.PendingFile, Encoding.UTF8));
                    if (list != null && list.Items != null) _changes = list.Items;
                }
            }
            catch (Exception e)
            {
                BridgeLog.Warn("changes", "Could not load pending changes: " + e.Message);
            }

            bool repathed = false;
            foreach (var change in _changes)
            {
                string mapped = BridgePaths.RemapLegacyLibraryPath(change.BackupPath);
                if (!string.Equals(mapped, change.BackupPath, StringComparison.Ordinal))
                {
                    change.BackupPath = mapped;
                    repathed = true;
                }
            }
            if (repathed) Save();
        }

        static void Save()
        {
            try
            {
                BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
                var list = new BridgeChangeList { Items = _changes };
                string json = JsonUtility.ToJson(list, false);
                string temp = BridgePaths.PendingFile + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(BridgePaths.PendingFile)) File.Delete(BridgePaths.PendingFile);
                File.Move(temp, BridgePaths.PendingFile);
            }
            catch (Exception e)
            {
                BridgeLog.Warn("changes", "Could not persist pending changes: " + e.Message);
            }
        }

        static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        public static int PendingCount
        {
            get
            {
                int count = 0;
                foreach (var change in All) if (change.Status == ChangeStatus.Pending) count++;
                return count;
            }
        }

        public static int ApprovedCount
        {
            get
            {
                int count = 0;
                foreach (var change in All) if (change.Status == ChangeStatus.Approved) count++;
                return count;
            }
        }

        public static BridgeChange Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var change in All)
                if (change.Id == id) return change;
            return null;
        }

        public static BridgeChange StageFile(string kind, string path, string content, string source, string title = null)
        {
            return Stage(kind, title, path, content, JsonValue.NewObject(), source, false);
        }

        public static BridgeChange Stage(string kind, string title, string targetPath, string content, JsonValue parameters, string source, bool destructive)
        {
            EnsureLoaded();
            if (!Permissions.CanPropose)
                throw new BridgeException("The bridge is in Read Only mode. No changes can be proposed.");

            if (kind == ChangeKinds.CreateFile || kind == ChangeKinds.ModifyFile || kind == ChangeKinds.DeleteFile)
            {
                string full;
                string error = Permissions.CheckWrite(targetPath, out full);
                if (error != null) throw new BridgeException(error);

                bool exists = File.Exists(full);
                if (kind == ChangeKinds.CreateFile && exists)
                    throw new BridgeException("The file already exists, so it will not be overwritten: " + targetPath + ". Use modify_file if you really intend to change it.");
                if (kind == ChangeKinds.ModifyFile && !exists)
                    throw new BridgeException("Cannot modify a file that does not exist: " + targetPath + ". Use create_file instead.");
                if (kind == ChangeKinds.DeleteFile && !exists)
                    throw new BridgeException("Cannot delete a file that does not exist: " + targetPath + ".");

                if (kind == ChangeKinds.DeleteFile) destructive = true;
                if (kind == ChangeKinds.CreateFile) content = content ?? string.Empty;
            }
            else if (kind == ChangeKinds.CreateFolder)
            {
                string full;
                string error = Permissions.CheckWrite(targetPath, out full, true, false);
                if (error != null) throw new BridgeException(error);
                if (Directory.Exists(full) || AssetDatabase.IsValidFolder(targetPath))
                    throw new BridgeException("Folder already exists: " + targetPath);
            }
            else if (kind == ChangeKinds.MoveFile)
            {
                string sourcePath = parameters["from"].AsString();
                string full;
                string error = Permissions.CheckWrite(targetPath, out full, false, true);
                if (error != null) throw new BridgeException(error);
                string sourceFull;
                error = Permissions.CheckRead(sourcePath, out sourceFull);
                if (error != null) throw new BridgeException(error);
                if (!File.Exists(sourceFull)) throw new BridgeException("Move source does not exist: " + sourcePath);
                if (File.Exists(full)) throw new BridgeException("Move destination already exists: " + targetPath);
                destructive = true;
            }
            else if (kind == ChangeKinds.ImportSprite)
            {
                string full;
                string error = Permissions.CheckWrite(targetPath, out full, false, true);
                if (error != null) throw new BridgeException(error);
                string extension = Path.GetExtension(targetPath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".tga" && extension != ".bmp")
                    throw new BridgeException("Sprite import expects a PNG/JPG/TGA/BMP destination path.");
                if (File.Exists(full)) destructive = true;
            }
            else if (kind == ChangeKinds.CreateMaterial || kind == ChangeKinds.CreatePrefab || kind == ChangeKinds.CreateScriptableObject)
            {
                string full;
                string error = Permissions.CheckWrite(targetPath, out full, false, true);
                if (error != null) throw new BridgeException(error);
                if (File.Exists(full)) throw new BridgeException("Asset already exists: " + targetPath);
            }

            var change = new BridgeChange
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Kind = kind,
                Title = string.IsNullOrEmpty(title) ? BuildTitle(kind, targetPath) : title,
                TargetPath = targetPath ?? string.Empty,
                Content = content,
                ParametersJson = (parameters ?? JsonValue.NewObject()).ToJson(false),
                Source = source ?? "chat",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                Destructive = destructive,
                Status = ChangeStatus.Pending
            };

            if (change.IsFileChange && !string.IsNullOrEmpty(change.TargetPath))
                change.Diff = BuildDiff(change);

            _changes.Insert(0, change);
            TrimHistory();
            Save();
            RaiseChanged();
            BridgeLog.Info("changes", "Staged " + kind + " " + (string.IsNullOrEmpty(targetPath) ? change.Title : targetPath) + " (" + change.Id + ") from " + change.Source);

            if (Permissions.ShouldAutoApply && !change.Destructive)
            {
                change.Status = ChangeStatus.Approved;
                change.AcknowledgedDestructive = false;
                string error = Apply(change.Id);
                if (error != null) BridgeLog.Warn("changes", "Auto-apply failed: " + error);
            }

            return change;
        }

        static void TrimHistory()
        {
            int remove = _changes.Count - 120;
            if (remove <= 0) return;
            int removed = 0;
            for (int i = _changes.Count - 1; i >= 0 && removed < remove; i--)
            {
                if (_changes[i].Status == ChangeStatus.Pending || _changes[i].Status == ChangeStatus.Approved) continue;
                _changes.RemoveAt(i);
                removed++;
            }
        }

        static string BuildTitle(string kind, string path)
        {
            switch (kind)
            {
                case ChangeKinds.CreateFile: return "Create " + Path.GetFileName(path);
                case ChangeKinds.ModifyFile: return "Modify " + Path.GetFileName(path);
                case ChangeKinds.DeleteFile: return "Delete " + Path.GetFileName(path);
                case ChangeKinds.CreateFolder: return "Create folder " + Path.GetFileName(path);
                case ChangeKinds.MoveFile: return "Move " + Path.GetFileName(path);
                case ChangeKinds.CreateGameObject: return "Create GameObject";
                case ChangeKinds.AddComponent: return "Add component";
                case ChangeKinds.SetComponentProperty: return "Update component";
                case ChangeKinds.CreatePrefab: return "Create prefab " + Path.GetFileName(path);
                case ChangeKinds.CreateMaterial: return "Create material " + Path.GetFileName(path);
                case ChangeKinds.ImportSprite: return "Import sprite " + Path.GetFileName(path);
                case ChangeKinds.AssignSprite: return "Assign sprite";
                case ChangeKinds.SliceSprite: return "Slice sprite sheet " + Path.GetFileName(path);
                default: return kind;
            }
        }

        static string BuildDiff(BridgeChange change)
        {
            try
            {
                string full = BridgePaths.GetFullPathSafe(change.TargetPath);
                string oldText = File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : string.Empty;
                string newText = change.Kind == ChangeKinds.DeleteFile ? string.Empty : (change.Content ?? string.Empty);
                if (change.Kind == ChangeKinds.CreateFile && oldText.Length == 0)
                    return DiffUtil.Unified(string.Empty, newText, "/dev/null", "b/" + change.TargetPath, 3, 120000);
                return DiffUtil.Unified(oldText, newText, "a/" + change.TargetPath, "b/" + change.TargetPath, 3, 120000);
            }
            catch (Exception e)
            {
                return "Diff unavailable: " + e.Message;
            }
        }

        public static string Approve(string id, bool acknowledgeDestructive = false)
        {
            var change = Find(id);
            if (change == null) return "Change not found.";
            if (change.Status == ChangeStatus.Applied) return "Change is already applied.";
            if (change.Destructive && !acknowledgeDestructive)
                return "This change is destructive and requires explicit confirmation.";
            change.Status = ChangeStatus.Approved;
            change.AcknowledgedDestructive = acknowledgeDestructive;
            Save();
            RaiseChanged();
            BridgeLog.Info("changes", "Approved " + change.Id + " (" + change.Title + ")");
            return null;
        }

        public static void Reject(string id)
        {
            var change = Find(id);
            if (change == null) return;
            if (change.Status == ChangeStatus.Applied) return;
            change.Status = ChangeStatus.Rejected;
            Save();
            RaiseChanged();
            BridgeLog.Info("changes", "Rejected " + change.Id);
        }

        public static void ApproveAllPending(bool includeDestructive = false)
        {
            foreach (var change in All)
            {
                if (change.Status != ChangeStatus.Pending) continue;
                if (change.Destructive && !includeDestructive) continue;
                change.Status = ChangeStatus.Approved;
                change.AcknowledgedDestructive = includeDestructive;
            }
            Save();
            RaiseChanged();
        }

        public static List<string> ApplyAllApproved()
        {
            var results = new List<string>();
            var ids = new List<string>();
            foreach (var change in All)
                if (change.Status == ChangeStatus.Approved) ids.Add(change.Id);
            foreach (var id in ids)
            {
                string error = Apply(id);
                results.Add(Find(id).Title + ": " + (error ?? "applied"));
            }
            return results;
        }

        public static string Apply(string id)
        {
            var change = Find(id);
            if (change == null) return "Change not found.";
            if (change.Status == ChangeStatus.Applied) return null;
            if (change.Status != ChangeStatus.Approved) return "Change has not been approved by a human.";
            if (!Permissions.CanApply) return "The bridge is in Read Only mode.";
            if (change.Destructive && !change.AcknowledgedDestructive) return "Destructive change was not explicitly confirmed.";

            string error = null;
            try
            {
                if (change.Kind == ChangeKinds.CreateFile || change.Kind == ChangeKinds.ModifyFile)
                {
                    string full;
                    error = Permissions.CheckWrite(change.TargetPath, out full);
                    if (error == null && change.Kind == ChangeKinds.CreateFile && File.Exists(full))
                    {
                        error = "The file appeared after the change was staged; refusing to overwrite it: " + change.TargetPath;
                    }
                    if (error == null)
                    {
                        if (File.Exists(full))
                        {
                            change.BackupPath = FileService.BackupFile(full, "before " + change.Kind);
                        }
                        string writeError;
                        string written = FileService.WriteTextAtomic(change.TargetPath, change.Content, out writeError);
                        if (written == null) error = writeError ?? "Write failed.";
                        if (error == null && Permissions.RequiresScriptWarning(change.TargetPath))
                            BridgeLog.Warn("changes", "Script applied. Unity will compile it and may execute [InitializeOnLoad] code; review before entering Play Mode.");
                    }
                }
                else if (change.Kind == ChangeKinds.DeleteFile)
                {
                    string full;
                    error = Permissions.CheckWrite(change.TargetPath, out full);
                    if (error == null)
                    {
                        change.BackupPath = FileService.BackupFile(full, "before delete");
                        string assetPath = BridgePaths.ToAssetPath(full);
                        if (assetPath != null) AssetDatabase.DeleteAsset(assetPath);
                        else if (File.Exists(full)) File.Delete(full);
                    }
                }
                else if (change.Kind == ChangeKinds.CreateFolder)
                {
                    string full;
                    error = Permissions.CheckWrite(change.TargetPath, out full, true, false);
                    if (error == null)
                    {
                        string assetFolder = BridgePaths.ToAssetPath(full);
                        if (assetFolder != null) AssetService.EnsureAssetFolder(assetFolder);
                        else Directory.CreateDirectory(full);
                    }
                }
                else if (change.Kind == ChangeKinds.MoveFile)
                {
                    string from = change.Parameters["from"].AsString();
                    string fromFull;
                    error = Permissions.CheckRead(from, out fromFull);
                    if (error == null)
                    {
                        string assetFrom = BridgePaths.ToAssetPath(fromFull);
                        string assetTo = change.TargetPath;
                        if (assetFrom != null && assetTo.StartsWith("Assets/", StringComparison.Ordinal))
                        {
                            change.BackupPath = FileService.BackupFile(fromFull, "before move");
                            string moveError = AssetDatabase.MoveAsset(assetFrom, assetTo);
                            if (!string.IsNullOrEmpty(moveError)) error = moveError;
                        }
                        else
                        {
                            string toFull;
                            error = Permissions.CheckWrite(assetTo, out toFull, false, true);
                            if (error == null)
                            {
                                change.BackupPath = FileService.BackupFile(fromFull, "before move");
                                Directory.CreateDirectory(Path.GetDirectoryName(toFull));
                                File.Move(fromFull, toFull);
                            }
                        }
                    }
                }
                else
                {
                    error = UnityOpExecutor.Execute(change);
                }

                if (error == null)
                {
                    change.Status = ChangeStatus.Applied;
                    change.AppliedUtc = DateTime.UtcNow.ToString("o");
                    change.Error = null;
                    BridgeLog.Success("changes", "Applied " + change.Id + " (" + change.Title + ")");
                }
                else
                {
                    change.Status = ChangeStatus.Failed;
                    change.Error = error;
                    BridgeLog.Error("changes", "Apply failed for " + change.Id + ": " + error);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                change.Status = ChangeStatus.Failed;
                change.Error = error;
                BridgeLog.Error("changes", "Apply threw for " + change.Id, e);
            }

            if (error == null && (change.IsFileChange || change.Kind == ChangeKinds.ImportSprite))
            {
                try { AssetDatabase.Refresh(); } catch { }
            }

            ProjectService.InvalidateContextCache();
            Save();
            RaiseChanged();
            return error;
        }

        public static bool Restore(string id)
        {
            var change = Find(id);
            if (change == null || string.IsNullOrEmpty(change.BackupPath)) return false;
            if (!File.Exists(change.BackupPath)) return false;
            return FileService.RestoreBackup(change.BackupPath, change.TargetPath);
        }

        public static void ClearApplied()
        {
            EnsureLoaded();
            _changes.RemoveAll(c => c.Status == ChangeStatus.Applied || c.Status == ChangeStatus.Rejected || c.Status == ChangeStatus.Failed);
            Save();
            RaiseChanged();
        }

        public static void Remove(string id)
        {
            EnsureLoaded();
            var change = Find(id);
            if (change == null) return;
            _changes.Remove(change);
            Save();
            RaiseChanged();
        }

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
            RaiseChanged();
        }
    }
}
