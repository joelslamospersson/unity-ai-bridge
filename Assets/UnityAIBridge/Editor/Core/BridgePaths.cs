using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>
    /// All filesystem access funnels through here. Paths are normalised, canonicalised
    /// (symlinks resolved) and checked against the project root before use.
    /// </summary>
    public static class BridgePaths
    {
        // Identifiers from the previous public release. Used only to migrate existing
        // settings, credentials, conversations, backups and test artifacts in place.
        const string ConfigDirName = "unity-ai-bridge";
        const string LegacyConfigDirName = "unity-kingdom-ai-bridge";
        const string LibraryDirName = "UnityAIBridge";
        const string LegacyLibraryDirName = "KingdomAIBridge";

        static string _projectRoot;
        static string _assetsRoot;
        static string _configRoot;
        static string _libraryRoot;

        public static string ProjectRoot
        {
            get
            {
                if (_projectRoot == null)
                    _projectRoot = Canonicalize(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
                return _projectRoot;
            }
        }

        public static string AssetsRoot
        {
            get
            {
                if (_assetsRoot == null) _assetsRoot = Canonicalize(Path.GetFullPath(Application.dataPath));
                return _assetsRoot;
            }
        }

        public static string ConfigRoot
        {
            get
            {
                if (_configRoot == null)
                {
                    string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                    string home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string baseDir;
                    if (IsWindows)
                        baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    else
                        baseDir = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(home, ".config");
                    string current = Path.Combine(baseDir, ConfigDirName);
                    string legacy = Path.Combine(baseDir, LegacyConfigDirName);
                    MigrateDirectory(legacy, current);
                    _configRoot = current;
                    Directory.CreateDirectory(_configRoot);
                    TrySetUnixMode(_configRoot, 448); // 0700
                }
                return _configRoot;
            }
        }

        public static string ProjectId
        {
            get
            {
                string name = Sanitize(Path.GetFileName(ProjectRoot));
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(ProjectRoot));
                    var sb = new StringBuilder();
                    for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
                    return name + "-" + sb;
                }
            }
        }

        public static string ProjectConfigDir { get { return Path.Combine(ConfigRoot, ProjectId); } }
        public static string SettingsFile { get { return Path.Combine(ProjectConfigDir, "settings.json"); } }
        public static string SecretsFile { get { return Path.Combine(ProjectConfigDir, "secrets.json"); } }
        public static string PendingFile { get { return Path.Combine(ProjectConfigDir, "pending-changes.json"); } }
        public static string ExportsDir { get { return Path.Combine(ProjectConfigDir, "exports"); } }
        public static string LibraryRoot { get { return ResolveLibraryRoot(); } }

        /// <summary>Run configuration/library migrations early during Editor startup.</summary>
        public static void EnsureMigrated()
        {
            string config = ConfigRoot;
            string library = LibraryRoot;
            if (config == null || library == null) { }
        }
        public static string IncomingDir { get { return Path.Combine(LibraryRoot, "incoming"); } }
        public static string HistoryFile { get { return Path.Combine(ProjectConfigDir, "chat-history.json"); } }
        public static string ChatSessionFile(string provider) { return Path.Combine(ProjectConfigDir, "chat-" + Sanitize(provider) + ".json"); }
        public static string LogFile { get { return Path.Combine(ProjectConfigDir, "bridge.log"); } }

        public static string BackupsDir { get { return Path.Combine(LibraryRoot, "Backups"); } }
        public static string TestResultsFile { get { return Path.Combine(LibraryRoot, "test-results.json"); } }
        public static string TestFlagFile { get { return Path.Combine(LibraryRoot, "run-tests.flag"); } }

        public static string TempDir
        {
            get
            {
                string dir = Path.Combine(LibraryRoot, "tmp");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        static string ResolveLibraryRoot()
        {
            if (_libraryRoot != null) return _libraryRoot;
            string current = Path.Combine(ProjectRoot, "Library", LibraryDirName);
            string legacy = Path.Combine(ProjectRoot, "Library", LegacyLibraryDirName);
            MigrateDirectory(legacy, current);
            Directory.CreateDirectory(current);
            _libraryRoot = current;
            return _libraryRoot;
        }

        /// <summary>Move a legacy directory if the new one does not exist; otherwise merge missing entries.</summary>
        static void MigrateDirectory(string legacy, string current)
        {
            if (!Directory.Exists(legacy)) return;
            if (!Directory.Exists(current))
            {
                try
                {
                    Directory.Move(legacy, current);
                    return;
                }
                catch { }
            }
            MergeDirectory(legacy, current);
        }

        static void MergeDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            try
            {
                foreach (string file in Directory.GetFiles(source))
                {
                    string destination = Path.Combine(target, Path.GetFileName(file));
                    if (!File.Exists(destination)) File.Copy(file, destination, false);
                }
                foreach (string directory in Directory.GetDirectories(source))
                {
                    string name = Path.GetFileName(directory);
                    MergeDirectory(directory, Path.Combine(target, name));
                }
            }
            catch { }
        }

        /// <summary>Map a persisted backup path from the legacy Library folder to the new one when needed.</summary>
        public static string RemapLegacyLibraryPath(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return path;
            string[] legacySegments =
            {
                Path.DirectorySeparatorChar + LegacyLibraryDirName + Path.DirectorySeparatorChar,
                "/" + LegacyLibraryDirName + "/",
                "\\" + LegacyLibraryDirName + "\\"
            };
            foreach (string segment in legacySegments)
            {
                int index = path.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                string replacement = segment.Replace(LegacyLibraryDirName, LibraryDirName);
                string candidate = path.Substring(0, index) + replacement + path.Substring(index + segment.Length);
                if (File.Exists(candidate)) return candidate;
            }
            return path;
        }

        static readonly string[] WriteAllowedExtensions =
        {
            ".cs", ".txt", ".json", ".md", ".shader", ".compute", ".cginc", ".hlsl", ".asmdef", ".uxml", ".uss", ".xml", ".yaml", ".yml", ".csv", ".inputactions", ".preset"
        };

        static readonly string[] GeneratedAssetExtensions =
        {
            ".mat", ".asset", ".prefab", ".unity", ".anim", ".controller", ".overridecontroller", ".spriteatlas", ".renderTexture", ".rendertexture", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".exr", ".gif"
        };

        static readonly string[] SensitiveNameFragments =
        {
            "id_rsa", "id_ed25519", "id_dsa", "id_ecdsa", ".pem", ".key", ".pfx", ".p12",
            "credentials", "secrets.json", "secrets.yaml", "secrets.yml", ".env", ".npmrc", ".netrc", "keystore", ".kdbx"
        };

        public static bool IsWindows
        {
            get { return Application.platform == RuntimePlatform.WindowsEditor; }
        }

        static StringComparison PathComparison
        {
            get { return IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; }
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "project";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.Length == 0 ? "project" : sb.ToString().ToLowerInvariant();
        }

        public static string NormalizeRelative(string input)
        {
            if (input == null) return string.Empty;
            string value = input.Trim().Replace('\\', '/');
            if (value.StartsWith("./", StringComparison.Ordinal)) value = value.Substring(2);
            if (value.Equals(".", StringComparison.Ordinal)) value = string.Empty;
            if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(6);
            return value;
        }

        /// <summary>Resolve a project-relative path for reading.</summary>
        public static bool TryResolve(string input, out string fullPath, out string error)
        {
            return TryResolveInternal(input, false, out fullPath, out error);
        }

        /// <summary>Resolve a path for writing. Only Assets/ is writable.</summary>
        public static bool TryResolveForWrite(string input, out string fullPath, out string error)
        {
            return TryResolveInternal(input, true, out fullPath, out error);
        }

        public static bool TryResolveForWrite(string input, out string fullPath, out string error, bool allowFolder, bool allowAsset)
        {
            return TryResolveInternal(input, true, out fullPath, out error, allowFolder, allowAsset);
        }

        static bool TryResolveInternal(string input, bool forWrite, out string fullPath, out string error, bool allowFolder = false, bool allowAsset = false)
        {
            fullPath = null;
            error = null;

            if (string.IsNullOrEmpty(input) || input.Trim().Length == 0)
            {
                error = "Path is empty.";
                return false;
            }
            if (input.IndexOf('\0') >= 0)
            {
                error = "Path contains a null character.";
                return false;
            }

            string normalized = NormalizeRelative(input);
            string candidate;
            if (Path.IsPathRooted(normalized))
            {
                candidate = Path.GetFullPath(normalized);
            }
            else
            {
                candidate = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
            }

            if (!IsWithin(candidate, ProjectRoot))
            {
                error = "Path escapes the Unity project root and was rejected: " + input;
                return false;
            }

            string relative = ToProjectRelative(candidate);
            string blocked;
            if (IsDeniedRelative(relative, forWrite, out blocked))
            {
                error = "Access to '" + blocked + "' is blocked by the bridge sandbox.";
                return false;
            }

            string canonical = Canonicalize(candidate);
            if (!IsWithin(canonical, ProjectRoot))
            {
                error = "Path resolves through a symlink outside the project and was rejected.";
                return false;
            }

            if (forWrite)
            {
                if (!IsWithin(canonical, AssetsRoot))
                {
                    error = "Writes are only allowed inside the project's Assets folder.";
                    return false;
                }
                if (IsSensitiveName(canonical))
                {
                    error = "Refusing to write to a potentially sensitive file name.";
                    return false;
                }
                string extension = Path.GetExtension(canonical).ToLowerInvariant();
                bool extensionAllowed = Array.IndexOf(WriteAllowedExtensions, extension) >= 0;
                if (allowFolder && extension.Length == 0) extensionAllowed = true;
                if (allowAsset && Array.IndexOf(GeneratedAssetExtensions, extension) >= 0) extensionAllowed = true;
                if (!extensionAllowed)
                {
                    error = "Writing '" + extension + "' files as text is not allowed. Use the sprite/texture importer for binary assets.";
                    return false;
                }
                if (!BridgeSettings.Current.AllowSelfModification && IsWithin(canonical, Path.Combine(AssetsRoot, "UnityAIBridge")))
                {
                    error = "Refusing to let the assistant modify the Unity AI Bridge's own source. Enable 'Allow self modification' in Settings if you really need this.";
                    return false;
                }
            }

            fullPath = canonical;
            return true;
        }

        public static bool IsWithin(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            string p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string c = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(c, p, PathComparison)) return true;
            return c.StartsWith(p + Path.DirectorySeparatorChar, PathComparison);
        }

        public static bool IsDeniedRelative(string relative, bool forWrite, out string blocked)
        {
            blocked = null;
            if (string.IsNullOrEmpty(relative) || relative == ".") return false;
            string[] parts = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, ".plastic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, ".vs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, ".idea", StringComparison.OrdinalIgnoreCase))
                {
                    blocked = part;
                    return true;
                }
            }

            string top = parts[0];
            if (string.Equals(top, "Library", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "Temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "Logs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "UserSettings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "Build", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "Builds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(top, "MemoryCaptures", StringComparison.OrdinalIgnoreCase))
            {
                blocked = top + "/";
                return true;
            }

            if (forWrite)
            {
                if (string.Equals(top, "ProjectSettings", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(top, "Packages", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(top, "Documentation", StringComparison.OrdinalIgnoreCase))
                {
                    blocked = top + "/";
                    return true;
                }
            }
            else
            {
                if (IsSensitiveName(relative)) { blocked = relative; return true; }
            }

            return false;
        }

        public static bool IsSensitiveName(string path)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (var fragment in SensitiveNameFragments)
            {
                if (lower.IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            if (lower.EndsWith(".meta", StringComparison.Ordinal)) return true;
            return false;
        }

        public static string ToAbsolute(string projectRelative)
        {
            return Path.GetFullPath(Path.Combine(ProjectRoot, NormalizeRelative(projectRelative)));
        }

        public static string ToProjectRelative(string absolute)
        {
            string full = Path.GetFullPath(absolute);
            if (!IsWithin(full, ProjectRoot)) return full.Replace('\\', '/');
            string rel = full.Substring(ProjectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace('\\', '/');
        }

        public static string ToAssetPath(string absolute)
        {
            string full = Path.GetFullPath(absolute);
            if (!IsWithin(full, AssetsRoot)) return null;
            string rel = full.Substring(AssetsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "Assets/" + rel.Replace('\\', '/');
        }

        public static string GetFullPathSafe(string projectRelative)
        {
            return Path.Combine(ProjectRoot, NormalizeRelative(projectRelative));
        }

        public static string Canonicalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return path; }

            if (IsWindows)
            {
                // Windows: rely on GetFullPath plus reparse-point rejection at use time.
                return full.TrimEnd(Path.DirectorySeparatorChar);
            }

            string existing = full;
            var suffix = new Stack<string>();
            while (!Directory.Exists(existing) && !File.Exists(existing))
            {
                string parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, PathComparison)) break;
                suffix.Push(Path.GetFileName(existing));
                existing = parent;
            }

            string canonical = RealPath(existing);
            if (string.IsNullOrEmpty(canonical)) canonical = existing;
            while (suffix.Count > 0) canonical = Path.Combine(canonical, suffix.Pop());
            return canonical.TrimEnd(Path.DirectorySeparatorChar);
        }

        public static bool IsSymlink(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        public static void EnsureDirectory(string directory)
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        }

        public static string WriteSecretFile(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                TrySetUnixMode(dir, 448); // 0700
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            TrySetUnixMode(path, 384); // 0600
            return null;
        }

        public static void TrySetUnixMode(string path, int mode)
        {
            if (IsWindows) return;
            try { chmod(path, mode); } catch { }
        }

#if !UNITY_EDITOR_WIN
        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr realpath(string path, IntPtr resolved);

        [DllImport("libc")]
        static extern void free(IntPtr pointer);

        static string RealPath(string path)
        {
            IntPtr pointer = IntPtr.Zero;
            try
            {
                pointer = realpath(path, IntPtr.Zero);
                if (pointer == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(pointer);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (pointer != IntPtr.Zero) free(pointer);
            }
        }
#else
        static string RealPath(string path) { return null; }
        static void chmod(string path, int mode) { }
#endif
    }
}
