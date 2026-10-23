using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The project's assets used lately: selected (in any window) or opened, newest first, kept per project in
    /// <c>Library/Orbiters/Mixer/recent.json</c> (folders are places, not things used: they are left out).
    /// </summary>
    [InitializeOnLoad]
    internal static class RecentAssets
    {
        public const int Capacity = 150;
        private static readonly string FilePath = Path.Combine("Library", "Orbiters", "Mixer", "recent.json");

        [Serializable] private sealed class Entry { public string guid; public long ticks; }
        [Serializable] private sealed class Store { public List<Entry> entries = new List<Entry>(); }

        private static List<Entry> entries;
        private static bool saveQueued;

        /// <summary>Raised when the list changed.</summary>
        public static event Action Changed;

        static RecentAssets()
        {
            Selection.selectionChanged += OnSelectionChanged;
        }

        private static List<Entry> Entries
        {
            get
            {
                if (entries != null) return entries;
                entries = new List<Entry>();
                try
                {
                    if (File.Exists(FilePath)) entries = JsonUtility.FromJson<Store>(File.ReadAllText(FilePath))?.entries ?? new List<Entry>();
                }
                catch (Exception) { entries = new List<Entry>(); }
                return entries;
            }
        }

        /// <summary>The recent assets: (path, when), newest first, those that still exist.</summary>
        public static List<(string path, DateTime when)> List()
        {
            var result = new List<(string, DateTime)>();
            foreach (var entry in Entries)
            {
                string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                // A deleted asset's GUID still gives its old path.
                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets))) continue;
                result.Add((path, new DateTime(entry.ticks, DateTimeKind.Utc)));
            }
            return result;
        }

        /// <summary>Notes an asset as used now.</summary>
        public static void Note(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return;
            var list = Entries;
            list.RemoveAll(e => e.guid == guid);
            list.Insert(0, new Entry { guid = guid, ticks = DateTime.UtcNow.Ticks });
            if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
            Changed?.Invoke();
            QueueSave();
        }

        /// <summary>Forgets every entry (tests).</summary>
        internal static void Clear()
        {
            Entries.Clear();
            Changed?.Invoke();
            QueueSave();
        }

        private static void OnSelectionChanged()
        {
            foreach (var guid in Selection.assetGUIDs.Take(8)) Note(AssetDatabase.GUIDToAssetPath(guid));
        }

        // Opening an asset (a double-click anywhere) counts as a use; the asset still opens as usual.
        [OnOpenAsset(-10000)]
        private static bool OnOpen(int instanceId, int line)
        {
            string path = AssetDatabase.GetAssetPath(instanceId);
            if (!string.IsNullOrEmpty(path)) Note(path);
            return false;
        }

        // A moment later, once: a burst of selections writes the file once (an update, not delayCall, which waits for a
        // repaint while the editor is in the background).
        private static void QueueSave()
        {
            if (saveQueued) return;
            saveQueued = true;
            EditorApplication.update += Save;
        }

        private static void Save()
        {
            EditorApplication.update -= Save;
            saveQueued = false;
            try { SafePaths.ReplaceFile(FilePath, JsonUtility.ToJson(new Store { entries = Entries })); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
