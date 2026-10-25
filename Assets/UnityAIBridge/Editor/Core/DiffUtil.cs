using System;
using System.Collections.Generic;
using System.Text;

namespace UnityAIBridge
{
    public static class DiffUtil
    {
        enum OpType { Equal, Delete, Insert }

        struct Op
        {
            public OpType Type;
            public string Line;
            public int OldLine;
            public int NewLine;
        }

        public static string Unified(string oldText, string newText, string oldLabel, string newLabel, int context = 3, int maxChars = 200000)
        {
            oldText = oldText ?? string.Empty;
            newText = newText ?? string.Empty;
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) return string.Empty;

            string[] oldLines = SplitLines(oldText);
            string[] newLines = SplitLines(newText);

            var ops = BuildOps(oldLines, newLines);

            int firstChange = -1, lastChange = -1;
            for (int i = 0; i < ops.Count; i++)
            {
                if (ops[i].Type == OpType.Equal) continue;
                if (firstChange < 0) firstChange = i;
                lastChange = i;
            }
            if (firstChange < 0) return string.Empty;

            var oldBefore = new int[ops.Count + 1];
            var newBefore = new int[ops.Count + 1];
            for (int i = 0; i < ops.Count; i++)
            {
                oldBefore[i + 1] = oldBefore[i] + (ops[i].Type == OpType.Insert ? 0 : 1);
                newBefore[i + 1] = newBefore[i] + (ops[i].Type == OpType.Delete ? 0 : 1);
            }

            var sb = new StringBuilder();
            sb.Append("--- ").Append(string.IsNullOrEmpty(oldLabel) ? "a" : oldLabel).Append('\n');
            sb.Append("+++ ").Append(string.IsNullOrEmpty(newLabel) ? "b" : newLabel).Append('\n');

            int index = firstChange;
            while (index < ops.Count)
            {
                if (ops[index].Type == OpType.Equal) { index++; continue; }

                int hunkStart = Math.Max(0, index - context);
                int hunkLastChange = index;
                int probe = index;
                while (probe < ops.Count)
                {
                    if (ops[probe].Type != OpType.Equal) { hunkLastChange = probe; probe++; continue; }
                    int runStart = probe;
                    while (probe < ops.Count && ops[probe].Type == OpType.Equal) probe++;
                    if (probe >= ops.Count) break;
                    if (probe - runStart > context * 2) break;
                }

                int hunkEnd = Math.Min(ops.Count - 1, hunkLastChange + context);
                int oldStart = oldBefore[hunkStart];
                int newStart = newBefore[hunkStart];
                int oldCount = oldBefore[hunkEnd + 1] - oldStart;
                int newCount = newBefore[hunkEnd + 1] - newStart;

                sb.Append("@@ -");
                sb.Append(oldCount == 0 ? oldStart : oldStart + 1);
                sb.Append(',').Append(oldCount);
                sb.Append(" +");
                sb.Append(newCount == 0 ? newStart : newStart + 1);
                sb.Append(',').Append(newCount);
                sb.Append(" @@\n");

                for (int i = hunkStart; i <= hunkEnd; i++)
                {
                    var op = ops[i];
                    switch (op.Type)
                    {
                        case OpType.Equal: sb.Append(' '); break;
                        case OpType.Delete: sb.Append('-'); break;
                        case OpType.Insert: sb.Append('+'); break;
                    }
                    sb.Append(op.Line).Append('\n');
                    if (sb.Length > maxChars)
                    {
                        sb.Append("... diff truncated ...\n");
                        return sb.ToString();
                    }
                }

                index = hunkEnd + 1;
            }

            return sb.ToString();
        }

        public static string Summary(string diff)
        {
            if (string.IsNullOrEmpty(diff)) return "no textual changes";
            int added = 0, removed = 0;
            string[] lines = diff.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("+++", StringComparison.Ordinal) || lines[i].StartsWith("---", StringComparison.Ordinal)) continue;
                if (lines[i].StartsWith("+", StringComparison.Ordinal)) added++;
                else if (lines[i].StartsWith("-", StringComparison.Ordinal)) removed++;
            }
            return "+" + added + " / -" + removed + " lines";
        }

        static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.EndsWith("\n", StringComparison.Ordinal)) normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized.Split('\n');
        }

        static List<Op> BuildOps(string[] oldLines, string[] newLines)
        {
            int n = oldLines.Length;
            int m = newLines.Length;

            var ops = new List<Op>(n + m);

            if ((long)n * m > 4000000L)
            {
                int prefix = 0;
                while (prefix < n && prefix < m && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal)) prefix++;
                int suffix = 0;
                while (suffix < n - prefix && suffix < m - prefix &&
                       string.Equals(oldLines[n - 1 - suffix], newLines[m - 1 - suffix], StringComparison.Ordinal)) suffix++;

                for (int i = 0; i < prefix; i++) ops.Add(new Op { Type = OpType.Equal, Line = oldLines[i], OldLine = i + 1, NewLine = i + 1 });
                for (int i = prefix; i < n - suffix; i++) ops.Add(new Op { Type = OpType.Delete, Line = oldLines[i], OldLine = i + 1, NewLine = prefix + 1 });
                for (int i = prefix; i < m - suffix; i++) ops.Add(new Op { Type = OpType.Insert, Line = newLines[i], OldLine = n - suffix + 1, NewLine = i + 1 });
                for (int i = 0; i < suffix; i++)
                {
                    int oi = n - suffix + i;
                    int ni = m - suffix + i;
                    ops.Add(new Op { Type = OpType.Equal, Line = oldLines[oi], OldLine = oi + 1, NewLine = ni + 1 });
                }
                return ops;
            }

            var table = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)) table[i, j] = table[i + 1, j + 1] + 1;
                    else table[i, j] = Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    ops.Add(new Op { Type = OpType.Equal, Line = oldLines[x], OldLine = x + 1, NewLine = y + 1 });
                    x++; y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    ops.Add(new Op { Type = OpType.Delete, Line = oldLines[x], OldLine = x + 1, NewLine = y + 1 });
                    x++;
                }
                else
                {
                    ops.Add(new Op { Type = OpType.Insert, Line = newLines[y], OldLine = x + 1, NewLine = y + 1 });
                    y++;
                }
            }
            while (x < n) { ops.Add(new Op { Type = OpType.Delete, Line = oldLines[x], OldLine = x + 1, NewLine = y + 1 }); x++; }
            while (y < m) { ops.Add(new Op { Type = OpType.Insert, Line = newLines[y], OldLine = x + 1, NewLine = y + 1 }); y++; }
            return ops;
        }
    }
}
