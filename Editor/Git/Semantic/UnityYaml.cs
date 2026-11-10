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

    /// <summary>
    /// One object of a Unity text asset: <c>--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;</c> then its type and fields. Its fields
    /// are parsed the first time <see cref="Body"/> is read: a compare parses only the objects that differ (by
    /// <see cref="Hash"/>) and the few others it needs a name or a parent from.
    /// </summary>
    internal sealed class UnityYamlDocument
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;
        public string TypeName = string.Empty;
        public int Line;
        // The file's text and where this object's type line and fields are in it: [Start, End), up to the next "--- ".
        internal string Source = string.Empty;
        internal int Start, BodyStart, End;
        /// <summary>A fingerprint of the type line and the fields (line breaks of any kind count as one): equal hashes, same object.</summary>
        internal ulong Hash;
        private YamlMap body;

        public YamlMap Body
        {
            get
            {
                YamlMap parsed = body;
                if (parsed != null) return parsed;
                parsed = UnityYaml.ParseBody(Source, BodyStart, End);
                // Two threads may parse it at once (the compare's worker, the view): both get the same fields.
                return System.Threading.Interlocked.CompareExchange(ref body, parsed, null) ?? parsed;
            }
            set { body = value; }
        }

        internal bool IsParsed => body != null;

        /// <summary>
        /// A top-level field's value as written on its own line ("{fileID: 123}"), read without parsing the object; null when
        /// the field is missing or its value continues on the next lines.
        /// </summary>
        internal string TopLevelValue(string key)
        {
            if (body != null || Source.Length == 0) return null;
            int line = BodyStart;
            while (line < End)
            {
                int next = UnityYaml.NextLine(Source, line, End, out int length);
                // "  key: value" at the fields' own indentation (two spaces).
                if (length > key.Length + 3 && Source[line] == ' ' && Source[line + 1] == ' ' && Source[line + 2] != ' ' &&
                    string.CompareOrdinal(Source, line + 2, key, 0, key.Length) == 0 && Source[line + 2 + key.Length] == ':')
                {
                    string value = Source.Substring(line + 3 + key.Length, length - 3 - key.Length).Trim();
                    bool open = value.StartsWith("{", StringComparison.Ordinal) && value.IndexOf('}') < 0 ||
                                value.StartsWith("[", StringComparison.Ordinal) && value.IndexOf(']') < 0;
                    return open ? null : value;
                }
                line = next;
            }
            return null;
        }
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

        /// <summary>
        /// The file's objects with their headers, types and fingerprints, in one pass over the text and without copying it;
        /// each object's fields are parsed when first read (<see cref="UnityYamlDocument.Body"/>).
        /// </summary>
        public static UnityYamlFile Parse(string text)
        {
            var file = new UnityYamlFile();
            if (string.IsNullOrEmpty(text)) return file;
            int length = text.Length;
            int position = 0, lineNumber = 0;
            UnityYamlDocument open = null;
            while (position < length)
            {
                int next = NextLine(text, position, length, out int lineLength);
                lineNumber++;
                if (lineLength >= 4 && string.CompareOrdinal(text, position, "--- ", 0, 4) == 0)
                {
                    // Any "--- " line ends the object before it.
                    if (open != null) Close(open, position);
                    open = null;
                    Match match = lineLength >= 7 && string.CompareOrdinal(text, position, "--- !u!", 0, 7) == 0
                        ? Header.Match(text.Substring(position, lineLength))
                        : Match.Empty;
                    if (match.Success)
                    {
                        open = new UnityYamlDocument
                        {
                            ClassId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                            FileId = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                            Stripped = match.Groups[3].Success,
                            Line = lineNumber,
                            Source = text,
                            Start = next,
                            BodyStart = next
                        };
                        // The type line ("GameObject:") right after the header, unless the object is empty.
                        if (next < length)
                        {
                            int afterType = NextLine(text, next, length, out int typeLength);
                            if (typeLength > 0 && !(typeLength >= 3 && string.CompareOrdinal(text, next, "---", 0, 3) == 0))
                            {
                                open.TypeName = text.Substring(next, typeLength).TrimEnd().TrimEnd(':');
                                open.BodyStart = afterType;
                                position = afterType;
                                lineNumber++;
                                file.Documents.Add(open);
                                file.ById[open.FileId] = open;
                                continue;
                            }
                        }
                        file.Documents.Add(open);
                        file.ById[open.FileId] = open;
                    }
                }
                position = next;
            }
            if (open != null) Close(open, length);
            return file;
        }

        private static void Close(UnityYamlDocument document, int end)
        {
            document.End = Math.Max(document.BodyStart, end);
            document.Hash = Fingerprint(document.Source, document.Start, document.End);
        }

        /// <summary>The start of the line after the one at <paramref name="position"/>; <paramref name="length"/>: that line
        /// without its break ("\r\n", "\r" or "\n").</summary>
        internal static int NextLine(string text, int position, int end, out int length)
        {
            int at = position;
            while (at < end && text[at] != '\n' && text[at] != '\r') at++;
            length = at - position;
            if (at >= end) return end;
            return text[at] == '\r' && at + 1 < end && text[at + 1] == '\n' ? at + 2 : at + 1;
        }

        // FNV-1a over the characters, every line break counted as one '\n' (as the fields are read).
        private static ulong Fingerprint(string text, int start, int end)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = start; i < end; i++)
            {
                char character = text[i];
                if (character == '\r')
                {
                    if (i + 1 < end && text[i + 1] == '\n') continue;
                    character = '\n';
                }
                hash ^= character;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        /// <summary>The fields of one object: the lines [<paramref name="start"/>, <paramref name="end"/>) of the text.</summary>
        internal static YamlMap ParseBody(string text, int start, int end)
        {
            if (string.IsNullOrEmpty(text) || end <= start) return new YamlMap();
            var lines = new List<string>();
            int position = start;
            while (position < end)
            {
                int next = NextLine(text, position, end, out int length);
                lines.Add(text.Substring(position, length));
                position = next;
            }
            return new Reader(lines.ToArray(), 0, lines.Count).Map(2);
        }

        /// <summary>An inline value ("{x: 0, y: 1}", "[1, 2]", a scalar) as the fields' reader reads it.</summary>
        public static YamlNode ParseInline(string text)
        {
            int position = 0;
            return Flow(text ?? string.Empty, ref position);
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
