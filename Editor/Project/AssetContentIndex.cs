using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What the project's text assets hold inside, for searching more than file names: animator controllers (layers,
    /// states, parameters, blend trees, state behaviours such as VRChat's parameter drivers), animation clips (animated
    /// paths, properties such as <c>blendShape.Smile</c>, events), override controllers, avatar masks, ScriptableObjects
    /// (VRChat's expression parameters and menus), prefabs and scenes (object names and the fields of their components:
    /// VRChat's, VRCFury's, any tool's). Names say what they name (a parameter, a layer, a state, an object). Built on a
    /// worker thread from the YAML Unity writes (binary assets are remembered as unread: <see cref="Unread"/> lists them for
    /// a lookup that must not miss them), in Assets and every
    /// package where it really is (embedded, cached or local), kept in <c>Library/Orbiters/Mixer/content-index.bin</c> by
    /// file date and size, and brought up to date after imports. Values are kept as Unity means them: its YAML escapes
    /// (<c>\u307E</c> for a Japanese letter, <c>''</c> in single quotes) are read back.
    /// </summary>
    [InitializeOnLoad]
    internal static class AssetContentIndex
    {
        public sealed class Hit
        {
            public string Path;
            /// <summary>What matched, readable: "parameter", "blendshape", "state or layer"...</summary>
            public string Label;
            public string Value;
        }

        private sealed class FileEntry
        {
            public long Ticks, Size;
            public string[] Keys, Values;
            /// <summary>Not YAML (binary serialization): nothing of it is known, which is not "it holds nothing".</summary>
            public bool Binary;
        }

        private const int Version = 5;
        // A line longer than this is read no further (a mesh's vertex data on one line): values past 200 characters are
        // never kept anyway.
        private const int LineLimit = 1024;
        private static readonly string[] Extensions = { ".anim", ".controller", ".overrideController", ".asset", ".prefab", ".unity", ".mask", ".playable" };
        // Unity's own fields worth a search; any other "m_" field is Unity's machinery (ids, flags, numbers).
        private static readonly HashSet<string> UnityKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Name", "m_ConditionEvent", "m_BlendParameter", "m_BlendParameterY", "m_TimeParameter", "m_SpeedParameter",
            "m_MirrorParameter", "m_CycleOffsetParameter", "m_ParameterName", "m_TagString",
        };
        private static readonly HashSet<string> SkipKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "serializedVersion", "fileFormatVersion", "guid", "fileID", "type", "m_Script", "m_EditorClassIdentifier",
        };

        private static readonly object Gate = new object();
        private static Dictionary<string, FileEntry> files = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        private static Task running;
        private static volatile bool dirty = true, stopping;
        private static volatile int done, total;
        private static volatile bool loaded;
        private static string projectRoot;

        /// <summary>Raised on the main thread when a build finished (what searches see changed).</summary>
        public static event Action Updated;

        static AssetContentIndex()
        {
            projectRoot = Directory.GetCurrentDirectory();
            // A script reload stops the reading where it is (what was read is kept); the next Refresh carries on from there.
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                stopping = true;
                try
                {
                    if (running != null && !running.Wait(3000)) UnityEngine.Debug.LogWarning("[Mixer] The project's content index was still reading a file when the scripts reloaded.");
                }
                catch (Exception) { }
            };
        }

        /// <summary>True while the index is being built or brought up to date.</summary>
        public static bool Building => running != null && !running.IsCompleted;
        /// <summary>"1,234 / 5,000 files" while building.</summary>
        public static string Progress => Building ? done.ToString("N0") + " / " + total.ToString("N0") + " files" : string.Empty;
        public static int FileCount { get { lock (Gate) return files.Count; } }
        /// <summary>True once the index was read and nothing changed since: an answer that finds nothing is final.</summary>
        public static bool UpToDate => loaded && !dirty && !Building;

        /// <summary>Starts building or updating the index when it is not up to date; never waits.</summary>
        public static void Refresh()
        {
            if (Building || !dirty) return;
            dirty = false;
            stopping = false;
            string root = projectRoot;
            var tops = Tops(root);
            running = Task.Run(() => Build(root, tops));
            EditorApplication.update -= WatchBuild;
            EditorApplication.update += WatchBuild;
        }

        // Assets, and each package where its files are (Packages/<name> for an embedded one, Library/PackageCache or a
        // local folder elsewhere), read on the main thread.
        private static List<(string asset, string full)> Tops(string root)
        {
            var tops = new List<(string, string)> { ("Assets", Path.Combine(root, "Assets")) };
            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                    if (!string.IsNullOrEmpty(package.assetPath) && !string.IsNullOrEmpty(package.resolvedPath)) tops.Add((package.assetPath, package.resolvedPath));
            }
            catch (Exception) { tops.Add(("Packages", Path.Combine(root, "Packages"))); }
            return tops;
        }

        /// <summary>Reads what changed and waits for it, after any reading under way (tests). Gives up after
        /// <paramref name="seconds"/> (false) rather than holding the editor.</summary>
        internal static bool RefreshNow(double seconds = 120)
        {
            var limit = TimeSpan.FromSeconds(seconds);
            if (running != null && !running.Wait(limit)) return false;
            dirty = true;
            Refresh();
            if (running != null && !running.Wait(limit)) return false;
            WatchBuild();
            return true;
        }

        private static void WatchBuild()
        {
            if (Building) return;
            EditorApplication.update -= WatchBuild;
            if (running != null && running.IsFaulted) UnityEngine.Debug.LogWarning("[Mixer] The project's content index could not be built: " + running.Exception?.GetBaseException().Message);
            Updated?.Invoke();
        }

        /// <summary>Assets changed: the next <see cref="Refresh"/> reads what changed again.</summary>
        internal static void MarkDirty() => dirty = true;

        private sealed class Watcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (imported.Concat(deleted).Concat(moved).Any(p => Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) MarkDirty();
            }
        }

        /// <summary>
        /// The assets whose content matches <paramref name="pattern"/> (text or a regular expression), with what matched;
        /// at most one hit per asset and <paramref name="limit"/> assets. Starts an update when the index is out of date.
        /// </summary>
        public static List<Hit> Search(SearchPattern pattern, Func<string, bool> inScope, int limit = 300)
        {
            Refresh();
            var hits = new List<Hit>();
            if (pattern == null || pattern.IsEmpty) return hits;
            lock (Gate)
            {
                foreach (var pair in files)
                {
                    // An expression found too slow matches nothing more: the rest is not walked.
                    if (pattern.TooSlow) break;
                    if (inScope != null && !inScope(pair.Key)) continue;
                    var entry = pair.Value;
                    for (int i = 0; i < entry.Values.Length; i++)
                    {
                        if (!pattern.Matches(entry.Values[i])) continue;
                        hits.Add(new Hit { Path = pair.Key, Label = Label(entry.Keys[i], entry.Values[i]), Value = Readable(entry.Values[i]) });
                        break;
                    }
                    if (hits.Count >= limit) break;
                }
            }
            return hits;
        }

        /// <summary>The indexed assets holding exactly <paramref name="key"/>: <paramref name="value"/> (e.g. attribute: blendShape.Smile).</summary>
        public static List<string> WithFact(string key, string value, string extension = null)
        {
            Refresh();
            var result = new List<string>();
            lock (Gate)
            {
                foreach (var pair in files)
                {
                    if (extension != null && !pair.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = pair.Value;
                    for (int i = 0; i < entry.Values.Length; i++)
                        if (entry.Keys[i] == key && entry.Values[i] == value) { result.Add(pair.Key); break; }
                }
            }
            return result;
        }

        /// <summary>
        /// The indexed assets that could not be read as text (binary serialization), with <paramref name="extension"/> when
        /// given: what a search of the facts cannot see, for a lookup to check with Unity's own API.
        /// </summary>
        public static List<string> Unread(string extension = null)
        {
            Refresh();
            var result = new List<string>();
            lock (Gate)
            {
                foreach (var pair in files)
                    if (pair.Value.Binary && (extension == null || pair.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) result.Add(pair.Key);
            }
            return result;
        }

        // ---- Labels ---------------------------------------------------------------------------------------------------

        private static string Label(string key, string value)
        {
            switch (key)
            {
                case "attribute": return value.StartsWith("blendShape.", StringComparison.Ordinal) ? "blendshape" : "property";
                case "path": return "animates";
                case "m_Name": return "name";
                case "functionName": return "event";
                case "m_ConditionEvent": case "m_BlendParameter": case "m_BlendParameterY": case "m_TimeParameter": case "m_SpeedParameter":
                case "m_MirrorParameter": case "m_CycleOffsetParameter": case "m_ParameterName": case "parameter": case "parameterName":
                    return "parameter";
                case "m_TagString": return "tag";
                default: return key;
            }
        }

        private static string Readable(string value) => value.StartsWith("blendShape.", StringComparison.Ordinal) ? value.Substring("blendShape.".Length) : value;

        // ---- Building (worker thread) ---------------------------------------------------------------------------------

        private static string CachePath(string root) => Path.Combine(root, "Library", "Orbiters", "Mixer", "content-index.bin");

        private static void Build(string root, List<(string asset, string full)> tops)
        {
            if (!loaded) { Load(root); loaded = true; }
            var found = new List<(string asset, string full)>();
            foreach (var (top, full) in tops)
            {
                if (stopping) { dirty = true; return; }
                if (!Directory.Exists(full)) continue;
                Walk(full, top, found);
            }
            total = found.Count;
            done = 0;
            var next = new Dictionary<string, FileEntry>(found.Count, StringComparer.Ordinal);
            Dictionary<string, FileEntry> known;
            lock (Gate) known = files;
            bool changed = false;
            var lastSave = DateTime.UtcNow;
            foreach (var (asset, full) in found)
            {
                if (stopping) { if (changed) Save(root); dirty = true; return; }
                // A first reading of a large project takes minutes: what was read so far is kept now and then.
                if (changed && (DateTime.UtcNow - lastSave).TotalSeconds > 20) { Save(root); lastSave = DateTime.UtcNow; }
                done++;
                long ticks, size;
                // Gone since the walk (a tool's temporary asset): left out.
                try { var info = new FileInfo(full); ticks = info.LastWriteTimeUtc.Ticks; size = info.Length; } catch (Exception) { continue; }
                if (known.TryGetValue(asset, out var entry) && entry.Ticks == ticks && entry.Size == size) { next[asset] = entry; continue; }
                entry = Parse(full, ticks, size);
                changed = true;
                if (stopping) { if (changed) Save(root); dirty = true; return; }
                if (entry == null) continue;
                next[asset] = entry;
                // Searches see each file as soon as it is read.
                lock (Gate) files[asset] = entry;
            }
            if (next.Count != known.Count) changed = true;
            lock (Gate) files = next;
            if (changed) Save(root);
        }

        // Unity's way: names starting with "." or ending in "~" are hidden from the project. Links (junctions) are not
        // followed: one pointing above itself would never end.
        private static void Walk(string full, string asset, List<(string, string)> found)
        {
            if (stopping) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(full); } catch (Exception) { return; }
            foreach (string path in entries)
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                string child = asset + "/" + name;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch (Exception) { continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) Walk(path, child, found);
                    continue;
                }
                string ext = Path.GetExtension(name);
                if (Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) found.Add((child, path));
            }
        }

        // Null when the file could not be read now (it is read again next time); a binary asset holds nothing.
        private static FileEntry Parse(string full, long ticks, long size)
        {
            try
            {
                using (var reader = new LineReader(full))
                {
                    string first = reader.ReadLine();
                    if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal))
                        return new FileEntry { Ticks = ticks, Size = size, Keys = Array.Empty<string>(), Values = Array.Empty<string>(), Binary = first != null };
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var keys = new List<string>();
                    var values = new List<string>();
                    string lastKey = null, document = null, section = null;
                    string line;
                    int lines = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // A large scene takes a while: a script reload does not wait for it.
                        if (++lines % 4096 == 0 && stopping) return null;
                        int start = 0;
                        while (start < line.Length && line[start] == ' ') start++;
                        if (start >= line.Length) continue;
                        // "AnimatorState:" opens an object's document, "  m_AnimatorParameters:" a section of it.
                        if (line.EndsWith(":", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) < 0)
                        {
                            if (start == 0) { document = line.Substring(0, line.Length - 1); section = null; }
                            else if (start == 2) section = line.Substring(2, line.Length - 3);
                        }
                        bool item = line[start] == '-' && start + 1 < line.Length && line[start + 1] == ' ';
                        if (item) start += 2;
                        int colon = line.IndexOf(": ", start, StringComparison.Ordinal);
                        string key, value;
                        if (colon < 0)
                        {
                            // A list's plain item ("- Head" under collisionTags): it belongs to the key above it.
                            if (!item || lastKey == null) { if (line.EndsWith(":", StringComparison.Ordinal)) lastKey = line.Substring(start, line.Length - start - 1); continue; }
                            key = lastKey;
                            value = line.Substring(start);
                        }
                        else
                        {
                            key = line.Substring(start, colon - start);
                            value = line.Substring(colon + 2);
                            lastKey = key;
                        }
                        if (!Keep(key)) continue;
                        key = Named(key, document, section);
                        value = Clean(value);
                        if (value == null) continue;
                        if (!seen.Add(key + "\n" + value)) continue;
                        keys.Add(key);
                        values.Add(value);
                    }
                    return new FileEntry { Ticks = ticks, Size = size, Keys = keys.ToArray(), Values = values.ToArray() };
                }
            }
            catch (Exception) { return null; }
        }

        // Lines of a text file, each cut at LineLimit characters (the rest of a long line is skipped, never held).
        private sealed class LineReader : IDisposable
        {
            private readonly StreamReader reader;
            private readonly char[] buffer = new char[65536];
            private readonly StringBuilder line = new StringBuilder(256);
            private int length, at;

            public LineReader(string path) { reader = new StreamReader(path, Encoding.UTF8); }

            public string ReadLine()
            {
                line.Clear();
                bool any = false;
                while (true)
                {
                    if (at >= length)
                    {
                        length = reader.Read(buffer, 0, buffer.Length);
                        at = 0;
                        if (length <= 0) return any ? line.ToString() : null;
                    }
                    any = true;
                    char c = buffer[at++];
                    if (c == '\n') return line.ToString();
                    if (c == '\r') continue;
                    if (line.Length < LineLimit) line.Append(c);
                }
            }

            public void Dispose() => reader.Dispose();
        }

        // What a name names, from where it is: a controller's parameter or layer, a state, an object, a menu's control.
        private static string Named(string key, string document, string section)
        {
            if (key == "m_Name")
            {
                switch (document)
                {
                    case "AnimatorController": return section == "m_AnimatorParameters" ? "parameter" : section == "m_AnimatorLayers" ? "layer" : key;
                    case "AnimatorState": return "state";
                    case "AnimatorStateMachine": return "state machine";
                    case "BlendTree": return "blend tree";
                    case "GameObject": return "object";
                    default: return key;
                }
            }
            if (key == "name" && document == "MonoBehaviour")
            {
                if (section == "parameters") return "parameter";
                if (section == "controls") return "menu control";
            }
            return key;
        }

        private static bool Keep(string key)
        {
            if (key.Length == 0 || SkipKeys.Contains(key)) return false;
            if (key.StartsWith("m_", StringComparison.Ordinal)) return UnityKeys.Contains(key);
            return true;
        }

        // A value worth searching: words, not numbers, references, ids or empty containers. Quoted, as Unity writes it.
        private static string Clean(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = Unescape(value.Substring(1, value.Length - 2));
            else if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'') value = value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value.Length == 0 || value.Length > 200) return null;
            char c = value[0];
            if (c == '{' || c == '[' || c == '&' || c == '*' || c == '!') return null;
            bool letter = false, hex = true;
            foreach (char ch in value)
            {
                if (char.IsLetter(ch)) letter = true;
                if (!Uri.IsHexDigit(ch)) hex = false;
            }
            if (!letter) return null;
            if (hex && value.Length >= 16) return null;
            return value;
        }

        // YAML's double-quoted escapes, as Unity writes non-ASCII text ("\u307E\u3070\u305F\u304D") and quotes.
        internal static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length) { result.Append(c); continue; }
                char e = text[++i];
                int digits = e == 'x' ? 2 : e == 'u' ? 4 : e == 'U' ? 8 : 0;
                if (digits > 0)
                {
                    if (TryHex(text, i + 1, digits, out int code))
                    {
                        if (code > 0xFFFF) result.Append(char.ConvertFromUtf32(code)); else result.Append((char)code);
                        i += digits;
                    }
                    else result.Append('\\').Append(e);
                    continue;
                }
                switch (e)
                {
                    case 'n': result.Append('\n'); break;
                    case 't': result.Append('\t'); break;
                    case 'r': result.Append('\r'); break;
                    case '0': result.Append('\0'); break;
                    case '"': case '\\': case '/': case ' ': result.Append(e); break;
                    default: result.Append('\\').Append(e); break;
                }
            }
            return result.ToString();
        }

        private static bool TryHex(string text, int start, int count, out int value)
        {
            value = 0;
            if (start + count > text.Length) return false;
            for (int k = 0; k < count; k++)
            {
                int d = Uri.IsHexDigit(text[start + k]) ? Uri.FromHex(text[start + k]) : -1;
                if (d < 0) return false;
                value = value * 16 + d;
            }
            return value <= 0x10FFFF;
        }

        // ---- The cache file ---------------------------------------------------------------------------------------------

        private static void Load(string root)
        {
            string path = CachePath(root);
            if (!File.Exists(path)) return;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (reader.ReadInt32() != Version) return;
                    int count = reader.ReadInt32();
                    var read = new Dictionary<string, FileEntry>(count, StringComparer.Ordinal);
                    for (int i = 0; i < count; i++)
                    {
                        string asset = reader.ReadString();
                        var entry = new FileEntry { Ticks = reader.ReadInt64(), Size = reader.ReadInt64(), Binary = reader.ReadBoolean() };
                        int facts = reader.ReadInt32();
                        entry.Keys = new string[facts];
                        entry.Values = new string[facts];
                        for (int f = 0; f < facts; f++) { entry.Keys[f] = string.Intern(reader.ReadString()); entry.Values[f] = reader.ReadString(); }
                        read[asset] = entry;
                    }
                    lock (Gate) files = read;
                }
            }
            catch (Exception) { lock (Gate) files = new Dictionary<string, FileEntry>(StringComparer.Ordinal); }
        }

        private static void Save(string root)
        {
            string path = CachePath(root);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                Dictionary<string, FileEntry> snapshot;
                lock (Gate) snapshot = new Dictionary<string, FileEntry>(files, StringComparer.Ordinal);
                using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
                {
                    writer.Write(Version);
                    writer.Write(snapshot.Count);
                    foreach (var pair in snapshot)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Ticks);
                        writer.Write(pair.Value.Size);
                        writer.Write(pair.Value.Binary);
                        writer.Write(pair.Value.Keys.Length);
                        for (int f = 0; f < pair.Value.Keys.Length; f++) { writer.Write(pair.Value.Keys[f]); writer.Write(pair.Value.Values[f]); }
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception) { }
        }
    }
}
