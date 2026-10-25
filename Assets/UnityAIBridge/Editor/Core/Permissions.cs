using System;

namespace UnityAIBridge
{
    /// <summary>
    /// Central authorisation gate. Every write path checks the level twice: when the
    /// change is proposed and again when it is applied, so downgrading the level
    /// immediately blocks pending work.
    /// </summary>
    public static class Permissions
    {
        public static BridgeSettings.AccessMode Level { get { return BridgeSettings.Current.Level; } }

        public static bool CanPropose { get { return Level != BridgeSettings.AccessMode.ReadOnly; } }
        public static bool CanApply { get { return Level != BridgeSettings.AccessMode.ReadOnly; } }

        public static string LevelName
        {
            get
            {
                switch (Level)
                {
                    case BridgeSettings.AccessMode.ReadOnly: return "Read Only";
                    case BridgeSettings.AccessMode.ProjectWrite: return "Project Write Access";
                    default: return "Ask Before Editing";
                }
            }
        }

        public static string DescribeLevel
        {
            get
            {
                switch (Level)
                {
                    case BridgeSettings.AccessMode.ReadOnly:
                        return "The assistant can inspect the project but cannot change anything.";
                    case BridgeSettings.AccessMode.ProjectWrite:
                        return "Approved changes may be written inside Assets/. Destructive operations still require confirmation.";
                    default:
                        return "Every change is staged as a diff and must be approved in the Pending Changes tab.";
                }
            }
        }

        public static void SetLevel(BridgeSettings.AccessMode mode, bool persist = true)
        {
            BridgeSettings.Current.AccessLevel = (int)mode;
            if (persist) BridgeSettings.Save();
        }

        /// <summary>Returns null when the write is allowed, otherwise a human readable reason.</summary>
        public static string CheckWrite(string projectRelative, out string fullPath)
        {
            return CheckWrite(projectRelative, out fullPath, false, false);
        }

        public static string CheckWrite(string projectRelative, out string fullPath, bool allowFolder, bool allowAsset)
        {
            fullPath = null;
            if (Level == BridgeSettings.AccessMode.ReadOnly)
                return "The bridge is in Read Only mode. Switch to 'Ask Before Editing' to propose changes.";

            string error;
            if (!BridgePaths.TryResolveForWrite(projectRelative, out fullPath, out error, allowFolder, allowAsset)) return error;
            return null;
        }

        public static string CheckRead(string projectRelative, out string fullPath)
        {
            string error;
            if (!BridgePaths.TryResolve(projectRelative, out fullPath, out error)) return error;
            return null;
        }

        public static bool ShouldAutoApply
        {
            get
            {
                return Level == BridgeSettings.AccessMode.ProjectWrite &&
                       BridgeSettings.Current.AutoApplyWithWriteAccess;
            }
        }

        public static bool RequiresScriptWarning(string projectRelative)
        {
            return BridgeSettings.Current.ConfirmScriptCompilation &&
                   projectRelative != null &&
                   projectRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
