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
    /// list, capped in memory. Each entry has a serial number and the anchor holds the serial of the newest entry in
    /// effect: when Unity undoes or redoes the anchor, every entry above that serial is undone and every one at or below
    /// it redone. Entries of a committed or reverted target are dropped without disturbing the others.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerHistory
    {
        private sealed class Anchor : ScriptableObject
        {
            public int revision;
        }

        private const string CapPref = "Orbiters.Mixer.HistoryMegabytes";
        private const string SerialKey = "Orbiters.Mixer.HistorySerial";
        private const int DefaultCapMegabytes = 512;

        private static readonly List<HistoryEntry> entries = new List<HistoryEntry>();
        private static int cursor;
        private static int nextSerial = SessionState.GetInt(SerialKey, 0);
        // Serials at or below this were dropped (memory cap) or lost to a script reload: undoing to them changes nothing.
        private static int floor = nextSerial;
        private static long bytes;
        private static Anchor anchor;
        private static bool applying;
        private static readonly HashSet<EditTarget> touched = new HashSet<EditTarget>();
        // Unity's redo list as far as its undo events tell (names, the next redo last), and the newest undo step's name
        // after the last undo or redo: when that changed, a new step was made and Unity's redo list is empty.
        private static readonly List<string> redoNames = new List<string>();
        private static string nameAfterEvent;
        private const string StepPrefix = "Mixer: ";

        /// <summary>Raised after the history changed: an entry pushed, undone, redone or dropped.</summary>
        public static event Action Changed;
        /// <summary>Raised when Unity undid past what Mixer still holds.</summary>
        public static event Action<string> Notice;

        static MixerHistory()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            Undo.undoRedoEvent += OnUndoRedoEvent;
            AssemblyReloadEvents.beforeAssemblyReload += () => { entries.Clear(); cursor = 0; SessionState.SetInt(SerialKey, nextSerial); };
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
        /// <summary>True while entries are being undone or redone (an entry's refresh can wait for <see cref="HistoryEntry.Refresh"/>).</summary>
        public static bool Applying => applying;

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
                if (anchor.revision > nextSerial) nextSerial = anchor.revision;
                if (anchor.revision > floor) floor = anchor.revision;
                return anchor;
            }
            anchor = ScriptableObject.CreateInstance<Anchor>();
            anchor.name = "Mixer undo anchor";
            anchor.hideFlags = HideFlags.HideAndDontSave;
            anchor.revision = nextSerial;
            return anchor;
        }

        /// <summary>Adds an entry already applied to its target, as one Unity undo step named after it (or inside the current step when <paramref name="joinCurrentGroup"/>).</summary>
        public static void Push(HistoryEntry entry, bool joinCurrentGroup = false)
        {
            if (entry == null) return;
            var a = GetAnchor();
            // Redo branch gone, as in every undo system.
            while (entries.Count > cursor) { bytes -= entries[entries.Count - 1].Bytes; entries.RemoveAt(entries.Count - 1); }
            entry.Serial = ++nextSerial;
            entries.Add(entry);
            // A new step: Unity's redo list is empty now.
            redoNames.Clear();
            bytes += entry.Bytes;
            cursor = entries.Count;
            if (!joinCurrentGroup) { Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Mixer: " + entry.Label); }
            Undo.RegisterCompleteObjectUndo(a, "Mixer: " + entry.Label);
            a.revision = entry.Serial;
            if (!joinCurrentGroup) Undo.IncrementCurrentGroup();
            Trim();
            Changed?.Invoke();
        }

        /// <summary>The newest entry, when it is the one being extended by a gesture still going on.</summary>
        public static HistoryEntry Last => cursor > 0 && cursor == entries.Count ? entries[cursor - 1] : null;

        /// <summary>Forgets every entry of a target (it was committed or reverted); Unity's steps stay, as no-ops.</summary>
        /// <summary>Whether a step of <paramref name="target"/>'s can still be redone.</summary>
        internal static bool HasRedo(EditTarget target)
        {
            for (int i = cursor; i < entries.Count; i++) if (entries[i].Target == target) return true;
            return false;
        }

        public static void Forget(EditTarget target)
        {
            bool any = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Target != target) continue;
                bytes -= entries[i].Bytes;
                entries.RemoveAt(i);
                if (i < cursor) cursor--;
                any = true;
            }
            if (any) Changed?.Invoke();
        }

        public static void Clear()
        {
            entries.Clear();
            bytes = 0;
            cursor = 0;
            floor = nextSerial;
            Changed?.Invoke();
        }

        /// <summary>
        /// Unity's Undo, so the two stacks never diverge, when the newest step is one of Mixer's; another tool's newest
        /// step (a move, a material change) is not undone from Mixer's button: a notice says to use Edit › Undo.
        /// </summary>
        public static void PerformUndo()
        {
            string newest = Undo.GetCurrentGroupName() ?? "";
            if (newest.StartsWith(StepPrefix, StringComparison.Ordinal)) { Undo.PerformUndo(); return; }
            Notice?.Invoke(newest.Length > 0 ? "The newest undo step is \"" + newest + "\", not Mixer's: Edit › Undo (Ctrl+Z) undoes it." : "Nothing of Mixer's to undo.");
        }

        /// <summary>Unity's Redo, when the next step to redo is one of Mixer's (see <see cref="PerformUndo"/>).</summary>
        public static void PerformRedo()
        {
            // A step made since the last undo emptied Unity's redo list.
            if ((Undo.GetCurrentGroupName() ?? "") != nameAfterEvent) redoNames.Clear();
            string next = redoNames.Count > 0 ? redoNames[redoNames.Count - 1] : null;
            if (next != null && next.StartsWith(StepPrefix, StringComparison.Ordinal)) { Undo.PerformRedo(); return; }
            Notice?.Invoke(next != null ? "The next redo step is \"" + next + "\", not Mixer's: Edit › Redo (Ctrl+Y) redoes it." : "Nothing of Mixer's to redo.");
        }

        private static void OnUndoRedoEvent(in UndoRedoInfo info)
        {
            string name = info.undoName ?? "";
            if (!info.isRedo)
            {
                // The step undone was the newest: when it is not what was newest after the last event, steps were made
                // in between and Unity's redo list started again from this one.
                if (nameAfterEvent == null || name != nameAfterEvent) redoNames.Clear();
                redoNames.Add(name);
            }
            else if (redoNames.Count > 0) redoNames.RemoveAt(redoNames.Count - 1);
            nameAfterEvent = Undo.GetCurrentGroupName() ?? "";
        }

        private static void Trim()
        {
            long cap = Cap;
            while (entries.Count > 1 && bytes > cap && cursor > 0)
            {
                bytes -= entries[0].Bytes;
                floor = Math.Max(floor, entries[0].Serial);
                entries.RemoveAt(0);
                cursor--;
            }
        }

        private static void OnUndoRedo()
        {
            if (applying) return;
            if (anchor == null)
            {
                // After a script reload: Unity's steps on the anchor still come back (the list is gone).
                foreach (var found in Resources.FindObjectsOfTypeAll<Anchor>()) if (found != null) { anchor = found; break; }
                if (anchor == null) return;
                if (anchor.revision > nextSerial) nextSerial = anchor.revision;
            }
            int serial = anchor.revision;
            // Entries at or below the anchor's serial are in effect, the others not.
            int target = 0;
            while (target < entries.Count && entries[target].Serial <= serial) target++;
            if (target == cursor && serial >= floor) return;
            applying = true;
            touched.Clear();
            try
            {
                while (cursor > target) { var e = entries[--cursor]; touched.Add(e.Target); e.Undo(); }
                while (cursor < target) { var e = entries[cursor++]; touched.Add(e.Target); e.Redo(); }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                applying = false;
            }
            // Each target's world picture and uploads once, however many steps a jump in the Undo History window took.
            foreach (var t in touched)
            {
                try { if (t != null && t.Alive) HistoryEntry.Refresh(t); }
                catch (Exception e) { Debug.LogException(e); }
            }
            touched.Clear();
            if (serial < floor && serial < nextSerial) Notice?.Invoke("Older Mixer edits are no longer in memory, so this undo step changed nothing in Mixer.");
            Changed?.Invoke();
            SceneView.RepaintAll();
        }
    }
}
