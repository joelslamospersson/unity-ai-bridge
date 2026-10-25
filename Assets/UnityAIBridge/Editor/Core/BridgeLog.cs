using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityAIBridge
{
    public sealed class BridgeLogEntry
    {
        public DateTime TimeUtc;
        public string Level;
        public string Scope;
        public string Message;
        public string Stack;

        public string Format()
        {
            return "[" + TimeUtc.ToLocalTime().ToString("HH:mm:ss") + "] " + Level + " " + (Scope ?? "bridge") + ": " + Message;
        }
    }

    public static class BridgeLog
    {
        const int MaxEntries = 800;
        static readonly object Gate = new object();
        static readonly List<BridgeLogEntry> Entries = new List<BridgeLogEntry>();

        public static event Action Changed;

        public static void Info(string scope, string message) { Write("INFO", scope, message, null); }
        public static void Success(string scope, string message) { Write("OK", scope, message, null); }
        public static void Warn(string scope, string message) { Write("WARN", scope, message, null); }
        public static void Error(string scope, string message) { Write("ERROR", scope, message, null); }
        public static void Error(string scope, string message, Exception exception)
        {
            Write("ERROR", scope, message, exception == null ? null : exception.ToString());
        }

        public static void Write(string level, string scope, string message, string stack)
        {
            var entry = new BridgeLogEntry
            {
                TimeUtc = DateTime.UtcNow,
                Level = level,
                Scope = scope,
                Message = message ?? string.Empty,
                Stack = stack
            };

            lock (Gate)
            {
                Entries.Add(entry);
                if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }

            try
            {
                if (BridgeSettings.Current.LogToFile)
                {
                    BridgePaths.EnsureDirectory(BridgePaths.ProjectConfigDir);
                    var line = entry.Format() + (string.IsNullOrEmpty(stack) ? string.Empty : "\n" + stack) + "\n";
                    File.AppendAllText(BridgePaths.LogFile, line, new UTF8Encoding(false));
                }
            }
            catch { }

            var handler = Changed;
            if (handler != null)
            {
                try { handler(); } catch { }
            }
        }

        public static List<BridgeLogEntry> Snapshot()
        {
            lock (Gate) return new List<BridgeLogEntry>(Entries);
        }

        public static void Clear()
        {
            lock (Gate) Entries.Clear();
            var handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        public static string AsText(int max = 300)
        {
            var entries = Snapshot();
            var sb = new StringBuilder();
            int start = Math.Max(0, entries.Count - max);
            for (int i = start; i < entries.Count; i++)
            {
                sb.AppendLine(entries[i].Format());
                if (!string.IsNullOrEmpty(entries[i].Stack)) sb.AppendLine(entries[i].Stack);
            }
            return sb.ToString();
        }
    }

    public sealed class ConsoleError
    {
        public DateTime TimeUtc;
        public string Severity;
        public string Message;
        public string Stack;

        public string Format()
        {
            return TimeUtc.ToLocalTime().ToString("HH:mm:ss") + " [" + Severity + "] " + Message;
        }
    }

    public sealed class CompilerDiagnostic
    {
        public string File;
        public int Line;
        public int Column;
        public string Message;
        public string Severity;

        public string Format()
        {
            string file = string.IsNullOrEmpty(File) ? "(no file)" : File;
            return Severity + " " + file + "(" + Line + "," + Column + "): " + Message;
        }
    }

    /// <summary>
    /// Captures Unity console output (including compiler diagnostics) so the assistant
    /// can reason about the current compilation state.
    /// </summary>
    public static class ConsoleService
    {
        const int MaxErrors = 300;
        static readonly object Gate = new object();
        static readonly List<ConsoleError> Errors = new List<ConsoleError>();
        static readonly List<CompilerDiagnostic> Diagnostics = new List<CompilerDiagnostic>();
        static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            Application.logMessageReceivedThreaded += OnLogMessage;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += ScanEditorLogForCompileErrors;
        }

        static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            string severity;
            switch (type)
            {
                case LogType.Error: severity = "Error"; break;
                case LogType.Exception: severity = "Exception"; break;
                case LogType.Assert: severity = "Assert"; break;
                case LogType.Warning: severity = "Warning"; break;
                default: return;
            }

            lock (Gate)
            {
                Errors.Add(new ConsoleError
                {
                    TimeUtc = DateTime.UtcNow,
                    Severity = severity,
                    Message = condition,
                    Stack = stackTrace
                });
                if (Errors.Count > MaxErrors) Errors.RemoveRange(0, Errors.Count - MaxErrors);
            }
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;
            lock (Gate)
            {
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error) continue;
                    Diagnostics.Add(new CompilerDiagnostic
                    {
                        File = message.file,
                        Line = message.line,
                        Column = message.column,
                        Message = message.message,
                        Severity = "error"
                    });
                }
                if (Diagnostics.Count > 200) Diagnostics.RemoveRange(0, Diagnostics.Count - 200);
            }
        }

        /// <summary>Best effort: recover compiler errors logged before this domain loaded.</summary>
        static void ScanEditorLogForCompileErrors()
        {
            try
            {
                string logPath = Path.Combine(BridgePaths.ProjectRoot, "Logs", "Editor.log");
                if (!File.Exists(logPath)) return;
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - 512 * 1024);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        int added = 0;
                        lock (Gate)
                        {
                            for (int i = lines.Length - 1; i >= 0 && added < 40; i--)
                            {
                                string line = lines[i];
                                if (line.IndexOf("error CS", StringComparison.Ordinal) < 0) continue;
                                if (Diagnostics.Exists(d => d.Message != null && line.IndexOf(d.Message.Substring(0, Math.Min(40, d.Message.Length)), StringComparison.Ordinal) >= 0)) continue;
                                Diagnostics.Add(new CompilerDiagnostic
                                {
                                    File = string.Empty,
                                    Line = 0,
                                    Column = 0,
                                    Message = line.Trim(),
                                    Severity = "error"
                                });
                                added++;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static List<ConsoleError> GetErrors(int max = 40, bool includeWarnings = false)
        {
            var result = new List<ConsoleError>();
            lock (Gate)
            {
                for (int i = Errors.Count - 1; i >= 0 && result.Count < max; i--)
                {
                    if (!includeWarnings && string.Equals(Errors[i].Severity, "Warning", StringComparison.Ordinal)) continue;
                    result.Add(Errors[i]);
                }
            }
            result.Reverse();
            return result;
        }

        public static List<CompilerDiagnostic> GetCompilerDiagnostics(int max = 40)
        {
            lock (Gate)
            {
                if (Diagnostics.Count == 0) ScanEditorLogForCompileErrors();
                if (Diagnostics.Count <= max) return new List<CompilerDiagnostic>(Diagnostics);
                return Diagnostics.GetRange(Diagnostics.Count - max, max);
            }
        }

        public static int ErrorCount
        {
            get
            {
                lock (Gate)
                {
                    int count = 0;
                    foreach (var error in Errors)
                        if (!string.Equals(error.Severity, "Warning", StringComparison.Ordinal)) count++;
                    return count + Diagnostics.Count;
                }
            }
        }

        public static void ClearCaptured()
        {
            lock (Gate)
            {
                Errors.Clear();
                Diagnostics.Clear();
            }
        }

        public static string ForPrompt(int max = 12)
        {
            var sb = new StringBuilder();
            var diagnostics = GetCompilerDiagnostics(max);
            if (diagnostics.Count > 0)
            {
                sb.AppendLine("Compiler diagnostics:");
                foreach (var diagnostic in diagnostics) sb.AppendLine("- " + diagnostic.Format());
            }
            var errors = GetErrors(max, false);
            if (errors.Count > 0)
            {
                sb.AppendLine("Recent console errors:");
                foreach (var error in errors)
                {
                    sb.AppendLine("- " + error.Message);
                    if (!string.IsNullOrEmpty(error.Stack))
                    {
                        string[] stackLines = error.Stack.Split('\n');
                        for (int i = 0; i < Math.Min(3, stackLines.Length); i++) sb.AppendLine("  " + stackLines[i].Trim());
                    }
                }
            }
            if (sb.Length == 0) sb.AppendLine("No compile errors or console errors are currently captured.");
            return sb.ToString();
        }
    }
}
