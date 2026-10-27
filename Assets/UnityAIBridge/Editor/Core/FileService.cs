using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    public sealed class ProjectFileInfo
    {
        public string RelativePath;
        public string FullPath;
        public long Size;
        public DateTime ModifiedUtc;
        public string Extension;
    }

    public static class FileService
    {
        public const int MaxReadBytes = 2 * 1024 * 1024;
        public const int MaxWriteChars = 1500000;

        static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".txt", ".json", ".md", ".shader", ".compute", ".cginc", ".hlsl", ".asmdef",
            ".uxml", ".uss", ".xml", ".yaml", ".yml", ".csv", ".inputactions", ".preset", ".meta",
            ".unity", ".prefab", ".mat", ".asset", ".anim", ".controller", ".overrideController",
            ".physicmaterial", ".physicsMaterial2D", ".renderTexture", ".guiskin", ".fontsettings", ".mixer"
        };

        public static string ReadText(string projectRelative, out string error, int maxBytes = MaxReadBytes)
        {
            error = null;
            string full;
            error = Permissions.CheckRead(projectRelative, out full);
            if (error != null) return null;

            try
            {
                if (!File.Exists(full))
                {
                    error = "File not found: " + projectRelative;
                    return null;
                }
                var info = new FileInfo(full);
                if (info.Length > maxBytes)
                {
                    error = "File is too large to read (" + FormatSize(info.Length) + "). Increase the limit only if you trust the file.";
                    return null;
                }
                if (IsBinaryFile(full))
                {
                    error = "Refusing to read a binary file as text. Use inspect_asset for assets such as textures or models.";
                    return null;
                }
                return File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                error = "Could not read file: " + e.Message;
                return null;
            }
        }

        public static JsonValue ReadTextPage(string path, int startLine = 1, int lineCount = 200, int maxChars = 6000, int startColumn = 0)
        {
            string full;
            string error = Permissions.CheckRead(path, out full);
            if (error != null) throw new BridgeException(error);
            if (!File.Exists(full)) throw new BridgeException("File not found: " + path);
            if (IsBinaryFile(full)) throw new BridgeException("Use inspect_asset for binary assets; do not read them as text.");
            startLine = Math.Max(1, startLine);
            lineCount = Math.Max(1, Math.Min(500, lineCount));
            maxChars = Math.Max(1, Math.Min(8000, maxChars));
            startColumn = Math.Max(0, startColumn);
            int line = 1, column = 0, scanned = 0;
            var text = new StringBuilder();
            bool more;
            using (var reader = new StreamReader(full, Encoding.UTF8, true))
            {
                while (reader.Peek() >= 0)
                {
                    if (line >= startLine + (long)lineCount || text.Length >= maxChars) break;
                    if (++scanned > 16 * 1024 * 1024)
                        throw new BridgeException("The requested range is beyond the text scan budget. Inspect the asset with inspect_asset or choose an earlier range.");
                    char c = (char)reader.Read();
                    if (line >= startLine && (line > startLine || column >= startColumn)) text.Append(c);
                    if (c == '\n') { line++; column = 0; }
                    else column++;
                }
                more = reader.Peek() >= 0;
            }
            string content = RedactSecrets(text.ToString());
            return JsonValue.Obj("path", path, "start_line", startLine, "start_column", startColumn,
                "end_line", text.Length == 0 ? startLine - 1 : text[text.Length - 1] == '\n' ? line - 1 : line, "lines", CountLines(content), "content", content,
                "truncated", more, "next_start_line", more ? line : 0, "next_start_column", more ? column : 0,
                "hint", more ? "Read the next page with next_start_line and next_start_column, or use inspect_asset for prefab hierarchy." : "End of file.");
        }

        public static bool IsBinaryFile(string fullPath)
        {
            try
            {
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[8192];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) return false;
                    int control = 0;
                    for (int i = 0; i < read; i++)
                    {
                        byte b = buffer[i];
                        if (b == 0) return true;
                        if (b < 7 || (b > 13 && b < 32)) control++;
                    }
                    return control > read / 20;
                }
            }
            catch { return true; }
        }

        public static bool Exists(string projectRelative)
        {
            string full;
            if (Permissions.CheckRead(projectRelative, out full) != null) return false;
            return File.Exists(full) || Directory.Exists(full);
        }

        public static List<ProjectFileInfo> ListFiles(string projectRelativeDir, bool recursive, int max, string[] extensions)
        {
            var result = new List<ProjectFileInfo>();
            string fullDir;
            string error = Permissions.CheckRead(string.IsNullOrEmpty(projectRelativeDir) ? "Assets" : projectRelativeDir, out fullDir);
            if (error != null) throw new BridgeException(error);
            if (!Directory.Exists(fullDir)) return result;

            IEnumerable<string> files;
            try { files = EnumerateFilesSafe(fullDir, recursive); }
            catch (Exception e) { throw new BridgeException("Could not list files: " + e.Message); }

            foreach (string file in files)
            {
                if (result.Count >= max) break;
                string rel = BridgePaths.ToProjectRelative(file);
                string blocked;
                if (BridgePaths.IsDeniedRelative(rel, false, out blocked)) continue;
                string extension = Path.GetExtension(file);
                if (extensions != null && extensions.Length > 0 && Array.IndexOf(extensions, extension) < 0) continue;
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new ProjectFileInfo
                    {
                        RelativePath = rel,
                        FullPath = file,
                        Size = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc,
                        Extension = extension
                    });
                }
                catch { }
            }

            result.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Recurses without following directory symlinks, so listings cannot escape the project.</summary>
        static IEnumerable<string> EnumerateFilesSafe(string directory, bool recursive)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                yield return file;
            if (!recursive) yield break;

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string file in EnumerateFilesSafe(child, true)) yield return file;
            }
        }

        public static List<string> ListDirectories(string projectRelativeDir, int max = 400)
        {
            return ListDirectories(projectRelativeDir, false, max);
        }

        public static List<string> ListDirectories(string projectRelativeDir, bool recursive, int max = 400)
        {
            var result = new List<string>();
            string fullDir;
            string error = Permissions.CheckRead(string.IsNullOrEmpty(projectRelativeDir) ? "Assets" : projectRelativeDir, out fullDir);
            if (error != null) throw new BridgeException(error);
            if (!Directory.Exists(fullDir)) return result;

            CollectDirectories(fullDir, recursive, max, result, true);
            return result;
        }

        static void CollectDirectories(string fullDir, bool recursive, int max, List<string> result, bool top)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(fullDir); }
            catch (Exception e)
            {
                if (top) throw new BridgeException("Could not list directories: " + e.Message);
                return;
            }

            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                if (result.Count >= max) return;
                string rel = BridgePaths.ToProjectRelative(dir);
                string blocked;
                if (BridgePaths.IsDeniedRelative(rel, false, out blocked)) continue;
                try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; }
                catch { continue; }
                result.Add(rel);
                if (recursive) CollectDirectories(dir, true, max, result, false);
            }
        }

        public static List<ProjectFileInfo> ListScripts(int max = 300)
        {
            var files = ListFiles("Assets", true, max, new[] { ".cs" });
            files.RemoveAll(f => f.RelativePath.StartsWith("Assets/UnityAIBridge/", StringComparison.Ordinal));
            return files;
        }

        public static string WriteTextAtomic(string projectRelative, string content, out string error)
        {
            error = null;
            content = content ?? string.Empty;
            if (content.Length > MaxWriteChars)
            {
                error = "Refusing to write an extremely large file (" + content.Length + " chars).";
                return null;
            }

            string full;
            error = Permissions.CheckWrite(projectRelative, out full);
            if (error != null) return null;

            try
            {
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string temp = full + ".kab-tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(full)) File.Delete(full);
                File.Move(temp, full);
                return full;
            }
            catch (Exception e)
            {
                error = "Could not write file: " + e.Message;
                return null;
            }
        }

        public static string BackupFile(string fullPath, string reason)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;
                if (!BridgePaths.IsWithin(fullPath, BridgePaths.ProjectRoot)) return null;

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string relative = BridgePaths.ToProjectRelative(fullPath);
                string target = Path.Combine(BridgePaths.BackupsDir, stamp, relative.Replace('/', Path.DirectorySeparatorChar));
                BridgePaths.EnsureDirectory(Path.GetDirectoryName(target));
                File.Copy(fullPath, target, true);
                BridgeLog.Info("backup", "Backed up " + relative + " -> " + BridgePaths.ToProjectRelative(target) + " (" + (reason ?? "unspecified") + ")");
                return target;
            }
            catch (Exception e)
            {
                BridgeLog.Warn("backup", "Backup failed for " + fullPath + ": " + e.Message);
                return null;
            }
        }

        public static bool RestoreBackup(string backupFullPath, string projectRelative)
        {
            string target;
            string error = Permissions.CheckWrite(projectRelative, out target);
            if (error != null) throw new BridgeException(error);
            if (!File.Exists(backupFullPath)) throw new BridgeException("Backup file no longer exists: " + backupFullPath);
            if (!BridgePaths.IsWithin(backupFullPath, BridgePaths.ProjectRoot)) throw new BridgeException("Backup is outside the project.");

            File.Copy(backupFullPath, target, true);
            string assetPath = BridgePaths.ToAssetPath(target);
            if (assetPath != null) AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            BridgeLog.Success("backup", "Restored " + projectRelative + " from backup.");
            return true;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        public static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') count++;
            return count;
        }

        public static string StripComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            try
            {
                var noBlock = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
                return Regex.Replace(noBlock, @"//[^\n]*", string.Empty);
            }
            catch { return code; }
        }

        public static string RedactSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string result = Regex.Replace(text, @"sk-[A-Za-z0-9_\-]{12,}", "sk-***REDACTED***");
                result = Regex.Replace(result, @"AIza[A-Za-z0-9_\-]{20,}", "***REDACTED_GOOGLE_KEY***");
                result = Regex.Replace(result, @"(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}", "***REDACTED_GITHUB_TOKEN***");
                result = Regex.Replace(result, @"(?i)bearer\s+[A-Za-z0-9_\-\.]{16,}", "Bearer ***REDACTED***");
                result = Regex.Replace(result, @"(?i)(api[_-]?key|token|password|secret)\s*[:=]\s*[""']?[A-Za-z0-9_\-\.]{12,}[""']?", "$1=***REDACTED***");
                return result;
            }
            catch { return text; }
        }

        public static bool LooksLikeText(string extension)
        {
            return TextExtensions.Contains(extension ?? string.Empty);
        }
    }
}
