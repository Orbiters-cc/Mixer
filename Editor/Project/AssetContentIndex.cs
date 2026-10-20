using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What the project's text assets hold inside, for searching more than file names: animator controllers (layers,
    /// states, parameters, blend trees, state behaviours such as VRChat's parameter drivers), animation clips (animated
    /// paths, properties such as <c>blendShape.Smile</c>, events), override controllers, avatar masks, ScriptableObjects
    /// (VRChat's expression parameters and menus), prefabs and scenes (object names and the fields of their components:
    /// VRChat's, VRCFury's, any tool's). Names say what they name (a parameter, a layer, a state, an object). Built on a worker thread from the YAML Unity writes (binary assets are skipped), kept in
    /// <c>Library/Orbiters/Mixer/content-index.bin</c> by file date and size, and brought up to date after imports.
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
        }

        private const int Version = 3;
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
        private static bool loaded;
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
                try { running?.Wait(3000); } catch (Exception) { }
            };
        }

        /// <summary>True while the index is being built or brought up to date.</summary>
        public static bool Building => running != null && !running.IsCompleted;
        /// <summary>"1,234 / 5,000 files" while building.</summary>
        public static string Progress => Building ? done.ToString("N0") + " / " + total.ToString("N0") + " files" : string.Empty;
        public static int FileCount { get { lock (Gate) return files.Count; } }

        /// <summary>Starts building or updating the index when it is not up to date; never waits.</summary>
        public static void Refresh()
        {
            if (Building || !dirty) return;
            dirty = false;
            stopping = false;
            string root = projectRoot;
            running = Task.Run(() => Build(root));
            EditorApplication.update -= WatchBuild;
            EditorApplication.update += WatchBuild;
        }

        /// <summary>Reads what changed and waits for it, after any reading under way (tests).</summary>
        internal static void RefreshNow()
        {
            running?.Wait();
            dirty = true;
            Refresh();
            running?.Wait();
            WatchBuild();
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
        /// The assets whose content holds <paramref name="query"/> (any case), with what matched; at most one hit per
        /// asset and <paramref name="limit"/> assets. Starts an update when the index is out of date.
        /// </summary>
        public static List<Hit> Search(string query, bool includePackages, int limit = 300)
        {
            Refresh();
            var hits = new List<Hit>();
            if (string.IsNullOrWhiteSpace(query)) return hits;
            query = query.Trim();
            lock (Gate)
            {
                foreach (var pair in files)
                {
                    if (!includePackages && !pair.Key.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                    var entry = pair.Value;
                    for (int i = 0; i < entry.Values.Length; i++)
                    {
                        if (entry.Values[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
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

        private static void Build(string root)
        {
            if (!loaded) { Load(root); loaded = true; }
            var found = new List<(string asset, string full)>();
            foreach (string top in new[] { "Assets", "Packages" })
            {
                string full = Path.Combine(root, top);
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
                FileInfo info;
                try { info = new FileInfo(full); } catch (Exception) { continue; }
                if (known.TryGetValue(asset, out var entry) && entry.Ticks == info.LastWriteTimeUtc.Ticks && entry.Size == info.Length) { next[asset] = entry; continue; }
                entry = Parse(full, info);
                changed = true;
                if (entry == null) continue;
                next[asset] = entry;
                // Searches see each file as soon as it is read.
                lock (Gate) files[asset] = entry;
            }
            if (next.Count != known.Count) changed = true;
            lock (Gate) files = next;
            if (changed) Save(root);
        }

        // Unity's way: names starting with "." or ending in "~" are hidden from the project.
        private static void Walk(string full, string asset, List<(string, string)> found)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(full); } catch (Exception) { return; }
            foreach (string path in entries)
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                string child = asset + "/" + name;
                if (Directory.Exists(path)) { Walk(path, child, found); continue; }
                string ext = Path.GetExtension(name);
                if (Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) found.Add((child, path));
            }
        }

        private static FileEntry Parse(string full, FileInfo info)
        {
            try
            {
                using (var reader = new StreamReader(full, Encoding.UTF8))
                {
                    string first = reader.ReadLine();
                    if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal)) return null;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var keys = new List<string>();
                    var values = new List<string>();
                    string lastKey = null, document = null, section = null;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
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
                    return new FileEntry { Ticks = info.LastWriteTimeUtc.Ticks, Size = info.Length, Keys = keys.ToArray(), Values = values.ToArray() };
                }
            }
            catch (Exception) { return null; }
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

        // A value worth searching: words, not numbers, references, ids or empty containers.
        private static string Clean(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0]) value = value.Substring(1, value.Length - 2);
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
                        var entry = new FileEntry { Ticks = reader.ReadInt64(), Size = reader.ReadInt64() };
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
