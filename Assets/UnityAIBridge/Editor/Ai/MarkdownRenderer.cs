using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>Small streaming-safe Markdown renderer for IMGUI with copyable code blocks.</summary>
    public static class MarkdownRenderer
    {
        sealed class Block
        {
            public bool IsCode;
            public string Language;
            public string Text;
        }

        sealed class CacheEntry
        {
            public int Hash;
            public List<Block> Blocks;
        }

        static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        static GUIStyle _textStyle;
        static GUIStyle _codeStyle;
        static GUIStyle _codeBox;
        static Font _mono;

        public static void Draw(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool truncated = false;
            if (text.Length > 80000)
            {
                text = text.Substring(0, 80000);
                truncated = true;
            }

            var blocks = GetBlocks(text, key);
            foreach (var block in blocks)
            {
                if (block.IsCode) DrawCode(block);
                else DrawText(block.Text);
            }
            if (truncated) GUILayout.Label("... message display truncated ...", EditorStyles.miniLabel);
        }

        static List<Block> GetBlocks(string text, string key)
        {
            int hash = text.GetHashCode();
            CacheEntry entry;
            if (!string.IsNullOrEmpty(key) && Cache.TryGetValue(key, out entry) && entry.Hash == hash) return entry.Blocks;

            var blocks = Parse(text);
            if (!string.IsNullOrEmpty(key))
            {
                Cache[key] = new CacheEntry { Hash = hash, Blocks = blocks };
                if (Cache.Count > 80) Cache.Clear();
            }
            return blocks;
        }

        static List<Block> Parse(string text)
        {
            var blocks = new List<Block>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var current = new StringBuilder();
            var code = new StringBuilder();
            bool inCode = false;
            string language = null;

            Action flushText = () =>
            {
                if (current.Length > 0)
                {
                    blocks.Add(new Block { IsCode = false, Text = current.ToString() });
                    current.Length = 0;
                }
            };

            foreach (var line in lines)
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    if (!inCode)
                    {
                        flushText();
                        inCode = true;
                        language = line.Substring(3).Trim();
                        if (language.Length == 0) language = "code";
                        code.Length = 0;
                    }
                    else
                    {
                        blocks.Add(new Block { IsCode = true, Language = language, Text = code.ToString().TrimEnd('\n') });
                        inCode = false;
                        language = null;
                    }
                    continue;
                }
                if (inCode) code.Append(line).Append('\n');
                else current.Append(line).Append('\n');
            }

            if (inCode)
                blocks.Add(new Block { IsCode = true, Language = language ?? "code", Text = code.ToString().TrimEnd('\n') });
            else flushText();
            return blocks;
        }

        static void DrawText(string block)
        {
            if (_textStyle == null)
                _textStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            string[] lines = block.Split('\n');
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    GUILayout.Space(4f);
                    continue;
                }
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    GUILayout.Label(Inline(line.Substring(4)), new GUIStyle(EditorStyles.boldLabel) { richText = true, wordWrap = true });
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    GUILayout.Label(Inline(line.Substring(3)), new GUIStyle(EditorStyles.boldLabel) { richText = true, fontSize = 13, wordWrap = true });
                    continue;
                }
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    GUILayout.Label(Inline(line.Substring(2)), new GUIStyle(EditorStyles.boldLabel) { richText = true, fontSize = 14, wordWrap = true });
                    continue;
                }
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    GUILayout.Label("  • " + Inline(line.Substring(2)), _textStyle);
                    continue;
                }
                if (Regex.IsMatch(line, @"^\d+\.\s"))
                {
                    GUILayout.Label("  " + Inline(line), _textStyle);
                    continue;
                }
                if (line.StartsWith("> ", StringComparison.Ordinal))
                {
                    GUILayout.Label(Inline(line.Substring(2)), new GUIStyle(EditorStyles.miniLabel) { richText = true, wordWrap = true });
                    continue;
                }
                GUILayout.Label(Inline(line), _textStyle);
            }
        }

        static void DrawCode(Block block)
        {
            if (_mono == null)
            {
                try { _mono = Font.CreateDynamicFontFromOSFont(new[] { "JetBrains Mono", "DejaVu Sans Mono", "Liberation Mono", "Consolas", "monospace" }, 11); }
                catch { _mono = EditorStyles.label.font; }
            }
            if (_codeStyle == null)
            {
                _codeStyle = new GUIStyle(EditorStyles.label)
                {
                    font = _mono,
                    fontSize = 11,
                    wordWrap = true,
                    richText = false,
                    padding = new RectOffset(8, 8, 6, 8)
                };
                _codeStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.86f, 0.92f, 0.98f) : new Color(0.1f, 0.1f, 0.1f);
                _codeBox = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(6, 6, 4, 4) };
            }

            using (new EditorGUILayout.VerticalScope(_codeBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(string.IsNullOrEmpty(block.Language) ? "code" : block.Language, EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Copy code", EditorStyles.miniButton, GUILayout.Width(76f)))
                        EditorGUIUtility.systemCopyBuffer = block.Text;
                }
                GUILayout.Label(block.Text, _codeStyle);
            }
        }

        static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string safe = text.Replace("<", "&lt;").Replace(">", "&gt;");
            safe = Regex.Replace(safe, @"\*\*(.+?)\*\*", "<b>$1</b>");
            safe = Regex.Replace(safe, @"`([^`]+)`", "<color=#9CDCFE>$1</color>");
            safe = Regex.Replace(safe, @"\[([^\]]+)\]\(([^)]+)\)", "<color=#6CB6FF>$1</color>");
            return safe;
        }
    }
}
