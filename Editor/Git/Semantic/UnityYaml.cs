using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbiters.UnitGit.Editor.Semantic
{
    internal abstract class YamlNode { }

    internal sealed class YamlScalar : YamlNode
    {
        public readonly string Value;
        public YamlScalar(string value) { Value = value ?? string.Empty; }
    }

    internal sealed class YamlMap : YamlNode
    {
        public readonly List<KeyValuePair<string, YamlNode>> Entries = new List<KeyValuePair<string, YamlNode>>();
        // Written inline ({x: 0, y: 1}): vectors, colours and object references. Compared and shown as one value.
        public bool Flow;

        public YamlNode this[string key]
        {
            get
            {
                foreach (var entry in Entries) if (entry.Key == key) return entry.Value;
                return null;
            }
        }

        public string Text(string key) => (this[key] as YamlScalar)?.Value;
    }

    internal sealed class YamlSequence : YamlNode
    {
        public readonly List<YamlNode> Items = new List<YamlNode>();
        public bool Flow;
    }

    /// <summary>One object of a Unity text asset: <c>--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;</c> then its type and fields.</summary>
    internal sealed class UnityYamlDocument
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;
        public string TypeName = string.Empty;
        public YamlMap Body = new YamlMap();
        public int Line;
    }

    internal sealed class UnityYamlFile
    {
        public readonly List<UnityYamlDocument> Documents = new List<UnityYamlDocument>();
        public readonly Dictionary<long, UnityYamlDocument> ById = new Dictionary<long, UnityYamlDocument>();
        // GameObject file ID → its Transform's, built on first use.
        internal Dictionary<long, long> TransformOf;

        public static bool LooksLikeUnityYaml(string text)
        {
            return text != null && text.StartsWith("%YAML", StringComparison.Ordinal) && text.IndexOf("--- !u!", StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>
    /// Reads the YAML subset Unity writes for scenes, prefabs and other text assets: block maps indented by two spaces,
    /// sequences at their key's indentation, inline maps that may wrap across lines, and wrapped plain or quoted strings.
    /// </summary>
    internal static class UnityYaml
    {
        private static readonly Regex Header = new Regex(@"^--- !u!(-?\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);

        public static UnityYamlFile Parse(string text)
        {
            var file = new UnityYamlFile();
            if (string.IsNullOrEmpty(text)) return file;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var match = Header.Match(lines[i]);
                if (!match.Success) { i++; continue; }
                var document = new UnityYamlDocument
                {
                    ClassId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    FileId = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    Stripped = match.Groups[3].Success,
                    Line = i + 1
                };
                i++;
                if (i < lines.Length && lines[i].Length > 0 && !lines[i].StartsWith("---", StringComparison.Ordinal))
                {
                    document.TypeName = lines[i].TrimEnd().TrimEnd(':');
                    i++;
                }
                int end = i;
                while (end < lines.Length && !lines[end].StartsWith("--- ", StringComparison.Ordinal)) end++;
                var reader = new Reader(lines, i, end);
                document.Body = reader.Map(2);
                i = end;
                file.Documents.Add(document);
                file.ById[document.FileId] = document;
            }
            return file;
        }

        private sealed class Reader
        {
            private readonly string[] lines;
            private readonly int end;
            private int line;

            public Reader(string[] lines, int start, int end)
            {
                this.lines = lines;
                line = start;
                this.end = end;
            }

            private static int Indent(string text)
            {
                int n = 0;
                while (n < text.Length && text[n] == ' ') n++;
                return n;
            }

            private bool Blank(int index) => lines[index].Trim().Length == 0;

            private void SkipBlank()
            {
                while (line < end && Blank(line)) line++;
            }

            public YamlMap Map(int indent)
            {
                var map = new YamlMap();
                SkipBlank();
                while (line < end)
                {
                    string text = lines[line];
                    int at = Indent(text);
                    if (at < indent || text.Substring(at).StartsWith("- ", StringComparison.Ordinal) || text.Substring(at) == "-") break;
                    if (at > indent) { line++; SkipBlank(); continue; }
                    line++;
                    Entry(map, text.Substring(at), indent);
                    SkipBlank();
                }
                return map;
            }

            // "key: value" at this indentation; the value may be inline, wrapped, or a nested block below.
            private void Entry(YamlMap map, string content, int indent)
            {
                int colon = KeySeparator(content);
                if (colon < 0)
                {
                    map.Entries.Add(new KeyValuePair<string, YamlNode>(content.Trim(), new YamlScalar(string.Empty)));
                    return;
                }
                string key = Unquote(content.Substring(0, colon).Trim());
                string rest = colon + 1 < content.Length ? content.Substring(colon + 1).Trim() : string.Empty;
                map.Entries.Add(new KeyValuePair<string, YamlNode>(key, Value(rest, indent)));
            }

            private YamlNode Value(string rest, int indent)
            {
                if (rest.Length == 0)
                {
                    SkipBlank();
                    if (line >= end) return new YamlScalar(string.Empty);
                    string next = lines[line];
                    int at = Indent(next);
                    bool item = next.Substring(at).StartsWith("- ", StringComparison.Ordinal) || next.Substring(at) == "-";
                    if (item && at >= indent) return Sequence(at);
                    if (at > indent) return Map(at);
                    return new YamlScalar(string.Empty);
                }
                if (rest[0] == '{' || rest[0] == '[')
                {
                    string flow = rest;
                    while (!Balanced(flow) && line < end) flow += " " + lines[line++].Trim();
                    int position = 0;
                    return Flow(flow, ref position);
                }
                if (rest[0] == '|' || rest[0] == '>')
                {
                    var block = new StringBuilder();
                    while (line < end && (Blank(line) || Indent(lines[line]) > indent))
                    {
                        if (block.Length > 0) block.Append('\n');
                        block.Append(lines[line].Trim());
                        line++;
                    }
                    return new YamlScalar(block.ToString());
                }
                string value = rest;
                if (rest[0] == '"' || rest[0] == '\'')
                {
                    while (!Closed(value) && line < end) value += " " + lines[line++].Trim();
                    return new YamlScalar(Unquote(value));
                }
                // Plain strings Unity wrapped: continuation lines are more indented and are not keys or items.
                while (line < end && !Blank(line) && Indent(lines[line]) > indent)
                {
                    string next = lines[line].Trim();
                    if (next.StartsWith("- ", StringComparison.Ordinal) || KeySeparator(next) >= 0) break;
                    value += " " + next;
                    line++;
                }
                return new YamlScalar(value);
            }

            private YamlSequence Sequence(int indent)
            {
                var sequence = new YamlSequence();
                while (line < end)
                {
                    SkipBlank();
                    if (line >= end) break;
                    string text = lines[line];
                    int at = Indent(text);
                    string trimmed = text.Substring(at);
                    if (at != indent || !(trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")) break;
                    line++;
                    string content = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
                    int inner = indent + 2;
                    if (content.Length == 0)
                    {
                        sequence.Items.Add(Value(string.Empty, indent));
                    }
                    else if (content[0] != '{' && content[0] != '[' && content[0] != '"' && content[0] != '\'' && KeySeparator(content) >= 0)
                    {
                        // "- key: value" starts a map whose other keys follow two spaces deeper.
                        var map = new YamlMap();
                        Entry(map, content, inner);
                        var more = Map(inner);
                        map.Entries.AddRange(more.Entries);
                        sequence.Items.Add(map);
                    }
                    else
                    {
                        sequence.Items.Add(Value(content, inner));
                    }
                }
                return sequence;
            }
        }

        // The ": " (or a trailing ":") that ends a key, outside quotes and brackets; -1 when the line is not "key: value".
        internal static int KeySeparator(string text)
        {
            bool single = false, dbl = false;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\'' && !dbl) single = !single;
                else if (c == '"' && !single) dbl = !dbl;
                else if (single || dbl) continue;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ':' && depth == 0 && (i + 1 == text.Length || text[i + 1] == ' ')) return i;
            }
            return -1;
        }

        private static bool Balanced(string text)
        {
            int depth = 0;
            bool single = false, dbl = false;
            foreach (char c in text)
            {
                if (c == '\'' && !dbl) single = !single;
                else if (c == '"' && !single) dbl = !dbl;
                else if (single || dbl) continue;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return depth <= 0;
        }

        private static bool Closed(string text)
        {
            char quote = text[0];
            if (text.Length < 2) return false;
            if (quote == '\'')
            {
                // '' is an escaped quote inside a single-quoted string.
                int count = 0;
                foreach (char c in text) if (c == '\'') count++;
                return count % 2 == 0 && text[text.Length - 1] == '\'';
            }
            for (int i = 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return true;
            }
            return false;
        }

        private static string Unquote(string text)
        {
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'') return text.Substring(1, text.Length - 2).Replace("''", "'");
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                return Regex.Unescape(text.Substring(1, text.Length - 2).Replace("\\u", "\\u"));
            return text;
        }

        private static YamlNode Flow(string text, ref int position)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length) return new YamlScalar(string.Empty);
            if (text[position] == '{')
            {
                var map = new YamlMap { Flow = true };
                position++;
                while (position < text.Length)
                {
                    SkipSpaces(text, ref position);
                    if (position < text.Length && text[position] == '}') { position++; break; }
                    string key = FlowToken(text, ref position, ":}");
                    if (position < text.Length && text[position] == ':') position++;
                    var value = Flow(text, ref position);
                    if (key.Length > 0) map.Entries.Add(new KeyValuePair<string, YamlNode>(key, value));
                    SkipSpaces(text, ref position);
                    if (position < text.Length && text[position] == ',') position++;
                }
                return map;
            }
            if (text[position] == '[')
            {
                var sequence = new YamlSequence { Flow = true };
                position++;
                while (position < text.Length)
                {
                    SkipSpaces(text, ref position);
                    if (position < text.Length && text[position] == ']') { position++; break; }
                    sequence.Items.Add(Flow(text, ref position));
                    SkipSpaces(text, ref position);
                    if (position < text.Length && text[position] == ',') position++;
                }
                return sequence;
            }
            return new YamlScalar(Unquote(FlowToken(text, ref position, ",}]")));
        }

        private static string FlowToken(string text, ref int position, string stops)
        {
            SkipSpaces(text, ref position);
            int start = position;
            if (position < text.Length && (text[position] == '"' || text[position] == '\''))
            {
                char quote = text[position++];
                while (position < text.Length && text[position] != quote) position += text[position] == '\\' ? 2 : 1;
                position = Math.Min(text.Length, position + 1);
                return text.Substring(start, position - start);
            }
            while (position < text.Length && stops.IndexOf(text[position]) < 0) position++;
            return text.Substring(start, position - start).Trim();
        }

        private static void SkipSpaces(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }
    }
}
