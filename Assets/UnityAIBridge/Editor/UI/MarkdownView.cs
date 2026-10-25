using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    /// <summary>
    /// Small, bounded Markdown renderer for chat. It intentionally supports the
    /// subset the assistant uses (headings, lists, code, quotes, inline emphasis)
    /// and caches parsed documents so scrolling does not re-parse messages.
    /// </summary>
    internal static class MarkdownView
    {
        const int MaxCachedDocuments = 160;
        static readonly Dictionary<string, Document> Cache = new Dictionary<string, Document>(StringComparer.Ordinal);
        static readonly Queue<string> CacheOrder = new Queue<string>();

        static readonly Regex CodeFence = new Regex("^```(.*)$", RegexOptions.Compiled);
        static readonly Regex InlineCode = new Regex("`([^`]+)`", RegexOptions.Compiled);
        static readonly Regex Bold = new Regex(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
        static readonly Regex Italic = new Regex(@"(?<!\*)\*([^*\n]+)\*(?!\*)", RegexOptions.Compiled);
        static readonly Regex Link = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
        static readonly Regex Token = new Regex(
            @"(?<string>""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|(?<comment>//[^\n]*)|(?<keyword>\b(?:public|private|protected|internal|static|class|struct|interface|enum|void|int|float|double|bool|string|object|var|new|return|if|else|for|foreach|while|switch|case|break|continue|using|namespace|try|catch|finally|throw|async|await|true|false|null|this|base|override|virtual|abstract|sealed|readonly|const|get|set|yield|in|out|ref|params|where|select|from|let|join|on|equals|into|orderby|descending|ascending)\b)|(?<number>\b\d+(?:\.\d+)?[fFdDmM]?\b)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        internal enum BlockKind { Paragraph, Heading, Code, Bullet, Number, Quote, Rule, Table }
        internal sealed class Block
        {
            public BlockKind Kind;
            public string Text;
            public string Language;
            public int Level;
            public List<string> Lines = new List<string>();
            public string Highlighted;
        }

        internal sealed class Document
        {
            public List<Block> Blocks = new List<Block>();
        }

        public static void Render(VisualElement parent, string markdown)
        {
            var document = GetDocument(markdown);
            var pro = EditorGUIUtility.isProSkin;
            foreach (var block in document.Blocks)
            {
                switch (block.Kind)
                {
                    case BlockKind.Heading:
                        parent.Add(Text(Inline(block.Text), block.Level <= 2 ? 16 : 14, true, pro));
                        break;
                    case BlockKind.Code:
                        parent.Add(CodeBlock(block, pro));
                        break;
                    case BlockKind.Bullet:
                    case BlockKind.Number:
                        for (int i = 0; i < block.Lines.Count; i++)
                            parent.Add(ListRow(block.Lines[i], block.Kind == BlockKind.Number ? i + 1 : 0, pro));
                        break;
                    case BlockKind.Quote:
                        parent.Add(Quote(block.Text, pro));
                        break;
                    case BlockKind.Rule:
                        {
                            var rule = new VisualElement();
                            rule.style.height = 1;
                            rule.style.marginTop = 8;
                            rule.style.marginBottom = 8;
                            rule.style.backgroundColor = pro ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.8f, 0.8f, 0.8f);
                            parent.Add(rule);
                            break;
                        }
                    case BlockKind.Table:
                        parent.Add(Table(block.Lines, pro));
                        break;
                    default:
                        parent.Add(Paragraph(block.Text, pro));
                        break;
                }
            }
        }

        static Label Paragraph(string text, bool pro)
        {
            var label = Text(Inline(text), 12, false, pro);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 6;
            label.enableRichText = true;
            return label;
        }

        static Label Text(string text, int size, bool bold, bool pro)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = pro ? new Color(0.86f, 0.86f, 0.86f) : new Color(0.12f, 0.12f, 0.12f);
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        static VisualElement ListRow(string text, int number, bool pro)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 3 } };
            var bullet = new Label(number > 0 ? number + "." : "•");
            bullet.style.width = 16;
            bullet.style.unityTextAlign = TextAnchor.MiddleRight;
            bullet.style.color = pro ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f);
            row.Add(bullet);
            var label = Text(Inline(text), 12, false, pro);
            label.style.flexGrow = 1;
            label.style.marginLeft = 6;
            label.enableRichText = true;
            row.Add(label);
            return row;
        }

        static VisualElement Quote(string text, bool pro)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6 } };
            var bar = new VisualElement();
            bar.style.width = 3;
            bar.style.marginRight = 8;
            bar.style.backgroundColor = pro ? new Color(0.4f, 0.55f, 0.7f) : new Color(0.45f, 0.6f, 0.8f);
            row.Add(bar);
            var label = Text(Inline(text), 12, false, pro);
            label.style.flexGrow = 1;
            label.style.color = pro ? new Color(0.72f, 0.75f, 0.78f) : new Color(0.32f, 0.36f, 0.4f);
            label.style.unityFontStyleAndWeight = FontStyle.Italic;
            row.Add(label);
            return row;
        }

        static VisualElement Table(List<string> lines, bool pro)
        {
            var box = new VisualElement();
            box.style.backgroundColor = pro ? new Color(0.13f, 0.13f, 0.13f) : new Color(0.97f, 0.97f, 0.97f);
            box.style.borderLeftWidth = box.style.borderRightWidth = box.style.borderTopWidth = box.style.borderBottomWidth = 1;
            box.style.borderLeftColor = box.style.borderRightColor = box.style.borderTopColor = box.style.borderBottomColor = pro ? new Color(0.28f, 0.28f, 0.28f) : new Color(0.82f, 0.82f, 0.82f);
            box.style.paddingLeft = 8; box.style.paddingRight = 8; box.style.paddingTop = 6; box.style.paddingBottom = 6;
            box.style.marginBottom = 6;
            foreach (var line in lines)
            {
                var label = Text(line.Replace('|', ' ').Trim(), 11, false, pro);
                label.style.whiteSpace = WhiteSpace.NoWrap;
                box.Add(label);
            }
            return box;
        }

        static VisualElement CodeBlock(Block block, bool pro)
        {
            var box = new VisualElement();
            box.style.backgroundColor = pro ? new Color(0.11f, 0.11f, 0.11f) : new Color(0.96f, 0.96f, 0.96f);
            box.style.borderLeftWidth = box.style.borderRightWidth = box.style.borderTopWidth = box.style.borderBottomWidth = 1;
            box.style.borderLeftColor = box.style.borderRightColor = box.style.borderTopColor = box.style.borderBottomColor = pro ? new Color(0.26f, 0.26f, 0.26f) : new Color(0.85f, 0.85f, 0.85f);
            box.style.paddingLeft = 10; box.style.paddingRight = 10; box.style.paddingTop = 8; box.style.paddingBottom = 8;
            box.style.marginTop = 4; box.style.marginBottom = 8;

            if (!string.IsNullOrEmpty(block.Language))
            {
                var lang = new Label(block.Language.ToUpperInvariant());
                lang.style.fontSize = 9;
                lang.style.color = pro ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.45f, 0.45f, 0.45f);
                lang.style.marginBottom = 4;
                box.Add(lang);
            }

            var code = new Label(block.Highlighted ?? Escape(block.Text));
            code.enableRichText = true;
            code.style.whiteSpace = WhiteSpace.PreWrap;
            code.style.fontSize = 11;
            code.style.color = pro ? new Color(0.84f, 0.84f, 0.84f) : new Color(0.12f, 0.12f, 0.12f);
            box.Add(code);
            return box;
        }

        static Document GetDocument(string markdown)
        {
            markdown = markdown ?? string.Empty;
            Document document;
            if (Cache.TryGetValue(markdown, out document)) return document;
            document = Parse(markdown);
            Cache[markdown] = document;
            CacheOrder.Enqueue(markdown);
            while (CacheOrder.Count > MaxCachedDocuments)
            {
                string oldest = CacheOrder.Dequeue();
                if (oldest != markdown) Cache.Remove(oldest);
            }
            return document;
        }

        static Document Parse(string markdown)
        {
            var doc = new Document();
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            var paragraph = new StringBuilder();

            while (i < lines.Length)
            {
                string line = lines[i];
                var fence = CodeFence.Match(line.TrimStart());
                if (fence.Success)
                {
                    FlushParagraph(doc, paragraph);
                    string language = fence.Groups[1].Value.Trim();
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        if (code.Length > 0) code.Append('\n');
                        code.Append(lines[i]);
                        i++;
                    }
                    i++;
                    var block = new Block { Kind = BlockKind.Code, Text = code.ToString(), Language = language };
                    block.Highlighted = Highlight(block.Text, language);
                    doc.Blocks.Add(block);
                    continue;
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    FlushParagraph(doc, paragraph);
                    i++;
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    FlushParagraph(doc, paragraph);
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    doc.Blocks.Add(new Block { Kind = BlockKind.Heading, Level = Mathf.Clamp(level, 1, 4), Text = trimmed.Substring(level).Trim() });
                    i++;
                    continue;
                }

                if (trimmed == "---" || trimmed == "***")
                {
                    FlushParagraph(doc, paragraph);
                    doc.Blocks.Add(new Block { Kind = BlockKind.Rule });
                    i++;
                    continue;
                }

                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    FlushParagraph(doc, paragraph);
                    doc.Blocks.Add(new Block { Kind = BlockKind.Quote, Text = trimmed.Substring(1).Trim() });
                    i++;
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
                {
                    FlushParagraph(doc, paragraph);
                    var list = new Block { Kind = BlockKind.Bullet };
                    while (i < lines.Length)
                    {
                        string item = lines[i].Trim();
                        if (!(item.StartsWith("- ", StringComparison.Ordinal) || item.StartsWith("* ", StringComparison.Ordinal) || item.StartsWith("+ ", StringComparison.Ordinal))) break;
                        list.Lines.Add(item.Substring(2).Trim());
                        i++;
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                var numbered = Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
                if (numbered.Success)
                {
                    FlushParagraph(doc, paragraph);
                    var list = new Block { Kind = BlockKind.Number };
                    while (i < lines.Length)
                    {
                        var match = Regex.Match(lines[i].Trim(), @"^\d+[.)]\s+(.*)$");
                        if (!match.Success) break;
                        list.Lines.Add(match.Groups[1].Value.Trim());
                        i++;
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                if (trimmed.StartsWith("|", StringComparison.Ordinal))
                {
                    FlushParagraph(doc, paragraph);
                    var table = new Block { Kind = BlockKind.Table };
                    while (i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal))
                    {
                        table.Lines.Add(lines[i].Trim());
                        i++;
                    }
                    doc.Blocks.Add(table);
                    continue;
                }

                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(trimmed);
                i++;
            }
            FlushParagraph(doc, paragraph);
            if (doc.Blocks.Count == 0) doc.Blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = "…" });
            return doc;
        }

        static void FlushParagraph(Document doc, StringBuilder paragraph)
        {
            if (paragraph.Length == 0) return;
            doc.Blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = paragraph.ToString() });
            paragraph.Length = 0;
        }

        internal static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string value = Escape(text);
            value = InlineCode.Replace(value, m => "<color=#d7ba7d>" + m.Groups[1].Value + "</color>");
            value = Bold.Replace(value, "<b>$1</b>");
            value = Italic.Replace(value, "<i>$1</i>");
            value = Link.Replace(value, "$1");
            return value;
        }

        internal static string Highlight(string code, string language)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            var builder = new StringBuilder(code.Length + 64);
            int last = 0;
            foreach (Match match in Token.Matches(code))
            {
                if (match.Index > last) builder.Append(Escape(code.Substring(last, match.Index - last)));
                string text = Escape(match.Value);
                if (match.Groups["comment"].Success) builder.Append("<color=#6a9955>").Append(text).Append("</color>");
                else if (match.Groups["string"].Success) builder.Append("<color=#ce9178>").Append(text).Append("</color>");
                else if (match.Groups["keyword"].Success) builder.Append("<color=#569cd6>").Append(text).Append("</color>");
                else if (match.Groups["number"].Success) builder.Append("<color=#b5cea8>").Append(text).Append("</color>");
                else builder.Append(text);
                last = match.Index + match.Length;
            }
            if (last < code.Length) builder.Append(Escape(code.Substring(last)));
            return builder.ToString();
        }

        internal static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
