using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    // Subscription credentials remain in the official Claude Code application.
    // This is an external-client handoff, never an OAuth/API token adapter.
    public static class ClaudeSubscriptionHandoff
    {
        public const string InstallUrl = "https://code.claude.com/docs/en/quickstart";
        public static bool IsBusy { get; private set; }
        public static string Status { get; private set; }
        static CancellationTokenSource _login;

        public static string FindExecutable()
        {
            string configured = BridgeSettings.Current.ClaudeExecutable;
            if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
            var directories = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
            foreach (var directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (var name in Application.platform == RuntimePlatform.WindowsEditor ? new[] { configured + ".exe", configured } : new[] { configured })
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        static void Notify() { MainThread.Run(ChatService.Raise); }

        // ProcessStartInfo argument quoting; no command is passed through a shell.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
                result.Append('\\', slashes).Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        static ProcessStartInfo StartInfo(string executable, string arguments)
        {
            var info = new ProcessStartInfo { FileName = executable, Arguments = arguments, WorkingDirectory = BridgePaths.ProjectRoot, UseShellExecute = false };
            // Do not silently select API/cloud billing when handing off a subscription.
            foreach (string variable in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" }) info.EnvironmentVariables.Remove(variable);
            return info;
        }

        public static void CancelLogin() { try { if (_login != null) _login.Cancel(); } catch (ObjectDisposedException) { } }

        public static void Login()
        {
            if (IsBusy) return;
            string executable = FindExecutable();
            if (executable == null) { Status = "Install the official Claude Code CLI, then sign in here. Use Claude API for replies inside Unity."; Notify(); return; }
            var info = StartInfo(executable, "auth login");
            info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            _login = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var attempt = _login; IsBusy = true;
            Status = "Complete sign-in in the browser opened by Claude Code. If it requests a pasted code, run claude auth login in a terminal.";
            Notify();
            Task.Run(() =>
            {
                try
                {
                    using (var process = new Process { StartInfo = info })
                    {
                        process.OutputDataReceived += (_, __) => { }; process.ErrorDataReceived += (_, __) => { };
                        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        using (attempt.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } })) process.WaitForExit();
                        Status = attempt.IsCancellationRequested ? "Claude Code sign-in cancelled or timed out."
                            : process.ExitCode == 0 ? "Signed in to the official Claude Code client. Install Unity Bridge for Claude Code, then Open Claude Code."
                            : "Claude Code could not finish browser sign-in. Run claude auth login in a terminal to complete it.";
                    }
                }
                catch (Exception e) { Status = "Could not start Claude Code: " + FileService.RedactSecrets(e.Message); }
                finally { IsBusy = false; if (ReferenceEquals(_login, attempt)) _login = null; attempt.Dispose(); Notify(); }
            });
        }

        public static void OpenClient()
        {
            string executable = FindExecutable();
            if (executable == null) { Status = "Install the official Claude Code CLI first, or use Claude API inside Unity."; Notify(); return; }
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) Process.Start(StartInfo(executable, ""));
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    EditorGUIUtility.systemCopyBuffer = "claude";
                    Process.Start(StartInfo("open", "-a Terminal " + Quote(BridgePaths.ProjectRoot)));
                    Status = "Run claude in the opened terminal; the command is copied to the clipboard.";
                }
                else
                {
                    string terminal = null;
                    foreach (var candidate in new[] { "konsole", "gnome-terminal", "x-terminal-emulator", "xterm" })
                        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                            if (terminal == null && !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, candidate))) terminal = Path.Combine(directory, candidate);
                    if (terminal == null) throw new BridgeException("Open a terminal in this project and run claude.");
                    Process.Start(StartInfo(terminal, (Path.GetFileName(terminal) == "gnome-terminal" ? "-- " : "-e ") + Quote(executable)));
                }
            }
            catch (Exception e) { Status = FileService.RedactSecrets(e.Message); }
            Notify();
        }
    }

    public sealed class ClaudeSubscriptionProvider : ILiveProvider
    {
        public string Id { get { return BridgeSettings.ProviderClaudeSubscription; } }
        public string DisplayName { get { return "Claude subscription (external)"; } }
        public string BillingNote { get { return "Subscription login runs in the official Claude Code client. Native Unity chat uses Claude API; third-party claude.ai OAuth requires Anthropic approval."; } }
        public bool IsConfigured(out string reason) { reason = "Use Sign in / Open Claude Code for subscription access, or choose Claude API for replies inside Unity."; return false; }
        public Task<List<ModelInfo>> GetModelsAsync(CancellationToken token) { return Task.FromResult(new List<ModelInfo>()); }
        public Task<string> TestConnectionAsync(CancellationToken token) { return Task.FromResult(BillingNote); }
        public Task<ProviderTurn> StreamTurnAsync(LlmRequest request, Action<StreamEvent> onEvent, CancellationToken token) { return Task.FromException<ProviderTurn>(new BridgeException(BillingNote)); }
    }
}
