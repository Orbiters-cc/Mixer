using System;
using System.Collections.Generic;
using System.IO;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The Project window's favourites, as Unity's: folders and assets (kept by GUID, so moves and renames follow) and saved
    /// searches, per project and per person in <c>UserSettings/Orbiters/Mixer/favourites.json</c>. A project starts with
    /// Unity's own three (all materials, models and prefabs).
    /// </summary>
    internal static class ProjectFavourites
    {
        private static readonly string FilePath = Path.Combine("UserSettings", "Orbiters", "Mixer", "favourites.json");

        [Serializable]
        internal sealed class Entry
        {
            /// <summary>A folder or an asset, or null for a saved search.</summary>
            public string guid;
            public string name;
            public string query;
            public bool regex, matchCase;

            public bool IsSearch => string.IsNullOrEmpty(guid);
            public string AssetPath => IsSearch ? null : AssetDatabase.GUIDToAssetPath(guid);
        }

        [Serializable] private sealed class Store { public List<Entry> entries = new List<Entry>(); }

        private static List<Entry> entries;

        /// <summary>Raised when an entry was added, removed or moved.</summary>
        public static event Action Changed;

        /// <summary>The favourites in their order; assets that no longer exist are left out.</summary>
        public static List<Entry> Entries
        {
            get
            {
                var result = new List<Entry>();
                foreach (var entry in Load())
                    if (entry.IsSearch || !string.IsNullOrEmpty(entry.AssetPath)) result.Add(entry);
                return result;
            }
        }

        private static List<Entry> Load()
        {
            if (entries != null) return entries;
            bool unreadable = false;
            try
            {
                if (File.Exists(FilePath)) { entries = JsonUtility.FromJson<Store>(File.ReadAllText(FilePath))?.entries; unreadable = entries == null; }
            }
            catch (Exception) { entries = null; unreadable = true; }
            // A file that could not be read (a hand edit, a merge) is kept beside before the next save replaces it.
            if (unreadable)
            {
                try { File.Copy(FilePath, FilePath + ".unreadable", true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (entries == null)
            {
                // A project's first favourites: Unity's own.
                entries = new List<Entry>
                {
                    new Entry { name = "All Materials", query = "t:Material" },
                    new Entry { name = "All Models", query = "t:Model" },
                    new Entry { name = "All Prefabs", query = "t:Prefab" },
                };
            }
            return entries;
        }

        public static bool Contains(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return !string.IsNullOrEmpty(guid) && Load().Exists(e => e.guid == guid);
        }

        public static Entry FindSearch(string query, bool regex, bool matchCase) =>
            Load().Find(e => e.IsSearch && e.query == query && e.regex == regex && e.matchCase == matchCase);

        /// <summary>Adds folders or assets (already favourite ones stay where they are); true when one was added.</summary>
        public static bool AddAssets(IEnumerable<string> assetPaths)
        {
            bool added = false;
            foreach (string path in assetPaths)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid) || Load().Exists(e => e.guid == guid)) continue;
                Load().Add(new Entry { guid = guid, name = Path.GetFileNameWithoutExtension(path) });
                added = true;
            }
            if (added) Save();
            return added;
        }

        public static Entry AddSearch(string query, bool regex, bool matchCase)
        {
            var found = FindSearch(query, regex, matchCase);
            if (found != null) return found;
            var entry = new Entry { name = query, query = query, regex = regex, matchCase = matchCase };
            Load().Add(entry);
            Save();
            return entry;
        }

        public static void Remove(Entry entry)
        {
            if (entry != null && Load().Remove(entry)) Save();
        }

        public static void RemoveAsset(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid) && Load().RemoveAll(e => e.guid == guid) > 0) Save();
        }

        /// <summary>Moves an entry up or down the list.</summary>
        public static void Move(Entry entry, int by)
        {
            var list = Load();
            int at = list.IndexOf(entry);
            if (at < 0) return;
            int to = Mathf.Clamp(at + by, 0, list.Count - 1);
            if (to == at) return;
            list.RemoveAt(at);
            list.Insert(to, entry);
            Save();
        }

        private static void Save()
        {
            try { SafePaths.ReplaceFile(FilePath, JsonUtility.ToJson(new Store { entries = Load() }, true)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Changed?.Invoke();
        }
    }
}
