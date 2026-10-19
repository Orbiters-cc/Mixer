using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's undo history, driven by Unity's: every stroke registers a four-byte change on a hidden anchor object
    /// with Unity's Undo, so Ctrl+Z, Ctrl+Y, Edit › Undo and the Undo History window all land on it like on any
    /// other edit, while the geometry itself (positions of the touched vertices, before and after) stays in this
    /// list, capped in memory. When Unity undoes or redoes the anchor, its revision says where the list must be,
    /// and the entries between are undone or redone.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerHistory
    {
        private sealed class Anchor : ScriptableObject
        {
            public int revision;
        }

        private const string CapPref = "Orbiters.Mixer.HistoryMegabytes";
        private const int DefaultCapMegabytes = 512;

        private static readonly List<HistoryEntry> entries = new List<HistoryEntry>();
        private static int cursor;
        // Revision of entries[0]: pushes made before it were dropped (memory cap) or lost to a script reload.
        private static int dropped;
        private static long bytes;
        private static Anchor anchor;
        private static bool applying;

        /// <summary>Raised after the history changed: an entry pushed, undone, redone or dropped.</summary>
        public static event Action Changed;
        /// <summary>Raised when Unity undid past what Mixer still holds.</summary>
        public static event Action<string> Notice;

        static MixerHistory()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload += () => { entries.Clear(); cursor = 0; };
        }

        public static int Count => entries.Count;
        public static int Cursor => cursor;
        public static bool CanUndo => cursor > 0;
        public static bool CanRedo => cursor < entries.Count;
        public static long Bytes => bytes;
        public static long Cap => (long)EditorPrefs.GetInt(CapPref, DefaultCapMegabytes) * 1024L * 1024L;
        public static string UndoLabel => cursor > 0 ? entries[cursor - 1].Label : null;
        public static string RedoLabel => cursor < entries.Count ? entries[cursor].Label : null;
        public static IReadOnlyList<HistoryEntry> Entries => entries;

        public static void SetCapMegabytes(int megabytes)
        {
            EditorPrefs.SetInt(CapPref, Mathf.Clamp(megabytes, 16, 8192));
            Trim();
        }

        private static Anchor GetAnchor()
        {
            if (anchor != null) return anchor;
            foreach (var found in Resources.FindObjectsOfTypeAll<Anchor>())
            {
                if (found == null) continue;
                anchor = found;
                // After a reload the list is empty: the anchor's revision is where the list starts again.
                dropped = anchor.revision;
                return anchor;
            }
            anchor = ScriptableObject.CreateInstance<Anchor>();
            anchor.name = "Mixer undo anchor";
            anchor.hideFlags = HideFlags.HideAndDontSave;
            dropped = 0;
            return anchor;
        }

        /// <summary>Adds an entry already applied to its target, as one Unity undo step named after it.</summary>
        public static void Push(HistoryEntry entry)
        {
            if (entry == null) return;
            var a = GetAnchor();
            // Redo branch gone, as in every undo system.
            while (entries.Count > cursor) { bytes -= entries[entries.Count - 1].Bytes; entries.RemoveAt(entries.Count - 1); }
            entries.Add(entry);
            bytes += entry.Bytes;
            cursor = entries.Count;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: " + entry.Label);
            Undo.RegisterCompleteObjectUndo(a, "Mixer: " + entry.Label);
            a.revision = dropped + cursor;
            Undo.IncrementCurrentGroup();
            Trim();
            Changed?.Invoke();
        }

        /// <summary>Forgets every entry of a target (it was committed or reverted); Unity's steps stay, as no-ops.</summary>
        public static void Forget(EditTarget target)
        {
            bool any = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Target != target) continue;
                bytes -= entries[i].Bytes;
                entries.RemoveAt(i);
                if (i < cursor) cursor--; else { }
                any = true;
            }
            if (any)
            {
                // Keep the anchor and the list in step: the revision now means "cursor entries from the new start".
                var a = GetAnchor();
                dropped = a.revision - cursor;
                Changed?.Invoke();
            }
        }

        public static void Clear()
        {
            entries.Clear();
            bytes = 0;
            cursor = 0;
            var a = GetAnchor();
            dropped = a.revision;
            Changed?.Invoke();
        }

        /// <summary>Unity's Undo and Redo, so the two stacks never diverge.</summary>
        public static void PerformUndo() => Undo.PerformUndo();
        public static void PerformRedo() => Undo.PerformRedo();

        private static void Trim()
        {
            long cap = Cap;
            while (entries.Count > 1 && bytes > cap && cursor > 0)
            {
                bytes -= entries[0].Bytes;
                entries.RemoveAt(0);
                cursor--;
                dropped++;
            }
        }

        private static void OnUndoRedo()
        {
            if (applying || anchor == null) return;
            int target = anchor.revision - dropped;
            if (target == cursor) return;
            applying = true;
            try
            {
                bool clamped = false;
                if (target < 0) { target = 0; clamped = true; }
                if (target > entries.Count) { target = entries.Count; clamped = true; }
                while (cursor > target) entries[--cursor].Undo();
                while (cursor < target) entries[cursor++].Redo();
                if (clamped) Notice?.Invoke("Older Mixer edits are no longer in memory, so this undo step changed nothing in Mixer.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                applying = false;
            }
            Changed?.Invoke();
            SceneView.RepaintAll();
        }
    }
}
