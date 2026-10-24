using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>One step of Unity's undo history: what it was called, when it happened, a snapshot of the Scene view after it.</summary>
    internal sealed class UndoStep
    {
        public string Name;
        /// <summary>UTC ticks; 0 for steps made before the history was followed.</summary>
        public long Time;
        /// <summary>The Scene view after the step (<see cref="SceneThumbnails"/>), or -1.</summary>
        public int Thumbnail = -1;

        public bool IsSelection => Name.StartsWith("Select ", StringComparison.Ordinal) || Name == "Selection Change" || Name.StartsWith("Clear Selection", StringComparison.Ordinal);
    }

    /// <summary>
    /// Beta: follows Unity's undo history (the steps of Edit › Undo History) with when each step happened and a small
    /// snapshot of the Scene view after it, and moves through it on request: going to a step undoes or redoes, one
    /// step at a time, until the project is as it was right after that step. Off by default.
    /// </summary>
    [InitializeOnLoad]
    internal static class UndoHistory
    {
        internal const string EnabledPref = "Orbiters.Logger.UndoTimeline";
        private const int Magic = 0x55444F4C; // "LODU"
        private const int FormatVersion = 1;
        private const double SyncSeconds = 0.25d;
        // A step's snapshot waits for the editing to settle: nothing recorded for this long and no drag going on.
        private const double SettleSeconds = 0.4d;
        private const double TravelBudgetMs = 24d;

        private delegate void UndoListReader(List<string> names, out int cursor);

        private static readonly List<UndoStep> steps = new List<UndoStep>();
        private static readonly List<string> names = new List<string>();
        private static UndoListReader readUndoList;
        private static int cursor = -1;
        private static bool started;
        private static bool dirty;
        private static double nextSync;
        private static int target = int.MinValue;
        private static UndoStep settling;
        private static double settledAt;

        static UndoHistory()
        {
            if (Enabled)
            {
                Start();
            }
        }

        /// <summary>Bumps when steps or the current step change.</summary>
        public static int Version { get; private set; }

        public static IReadOnlyList<UndoStep> Steps => steps;

        /// <summary>The index of the last step done (-1: everything undone).</summary>
        public static int Cursor => cursor;

        /// <summary>Where a trip through the history is headed, or <see cref="int.MinValue"/>.</summary>
        public static int Target => target;

        public static bool Traveling => target != int.MinValue;

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, false);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            }
        }

        private static void Start()
        {
            if (started)
            {
                return;
            }

            if (readUndoList == null)
            {
                var method = typeof(Undo).GetMethod("GetUndoList", BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(List<string>), typeof(int).MakeByRefType() }, null);
                if (method == null)
                {
                    return;
                }

                readUndoList = (UndoListReader)Delegate.CreateDelegate(typeof(UndoListReader), method);
            }

            started = true;
            Undo.willFlushUndoRecord += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Save;
            SceneThumbnails.Start();
            Load();
            dirty = true;
        }

        private static void Stop()
        {
            if (!started)
            {
                return;
            }

            started = false;
            Undo.willFlushUndoRecord -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= Save;
            SceneThumbnails.Stop();
            settling = null;
            steps.Clear();
            target = int.MinValue;
            Version++;
            Delete();
        }

        private static void MarkDirty()
        {
            dirty = true;
            // Still editing (a drag, a slider): the newest step's snapshot waits until it stops.
            if (settling != null) settledAt = EditorApplication.timeSinceStartup + SettleSeconds;
        }

        private static void Update()
        {
            try
            {
                if (target != int.MinValue)
                {
                    Travel();
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (dirty && now >= nextSync)
                {
                    nextSync = now + SyncSeconds;
                    dirty = false;
                    Sync();
                }

                // The snapshot shows the project right after the step: once the editing settled, mouse released.
                if (settling != null && now >= settledAt && UnityEngine.GUIUtility.hotControl == 0)
                {
                    SceneThumbnails.Capture(settling);
                    settling = null;
                }
            }
            catch (Exception)
            {
                target = int.MinValue;
            }
        }

        // Unity's list against ours: the shared beginning keeps its times and snapshots; what follows is new.
        private static void Sync()
        {
            readUndoList(names, out int newCursor);
            int common = 0;
            while (common < steps.Count && common < names.Count && steps[common].Name == names[common])
            {
                common++;
            }

            if (common == 0 && steps.Count > 0 && names.Count > 0)
            {
                // Unity dropped its oldest steps (its history is limited): find where its first one is in ours.
                int offset = Offset();
                if (offset > 0)
                {
                    steps.RemoveRange(0, offset);
                    while (common < steps.Count && common < names.Count && steps[common].Name == names[common])
                    {
                        common++;
                    }
                }
            }

            bool changed = newCursor != cursor || common != steps.Count || names.Count != steps.Count;
            if (common < steps.Count)
            {
                steps.RemoveRange(common, steps.Count - common);
            }

            long time = DateTime.UtcNow.Ticks;
            bool known = started && Version > 0;
            for (int i = common; i < names.Count; i++)
            {
                steps.Add(new UndoStep { Name = names[i], Time = known ? time : 0L });
            }

            if (names.Count > common && known)
            {
                // A step still settling when the next one starts has no snapshot: the scene already moved on.
                settling = steps[steps.Count - 1];
                settledAt = EditorApplication.timeSinceStartup + SettleSeconds;
            }

            cursor = newCursor;
            if (changed || Version == 0)
            {
                Version++;
            }
        }

        private static int Offset()
        {
            int length = Math.Min(names.Count, 24);
            for (int offset = 1; offset + length <= steps.Count; offset++)
            {
                bool same = true;
                for (int i = 0; i < length && same; i++)
                {
                    same = steps[offset + i].Name == names[i];
                }

                if (same)
                {
                    return offset;
                }
            }

            return 0;
        }

        /// <summary>Goes to the project as it was right after step <paramref name="index"/> (-1: before every step).</summary>
        public static void GoTo(int index)
        {
            if (!started)
            {
                return;
            }

            target = Math.Max(-1, Math.Min(index, steps.Count - 1));
            Version++;
        }

        /// <summary>One step back (<paramref name="direction"/> -1) or forward (+1).</summary>
        public static void Step(int direction)
        {
            int from = target != int.MinValue ? target : cursor;
            GoTo(from + Math.Sign(direction));
        }

        // A few steps per editor update, so the window keeps drawing and the trip can be watched (or redirected).
        private static void Travel()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalMilliseconds < TravelBudgetMs)
            {
                readUndoList(names, out int current);
                cursor = current;
                if (target == int.MinValue || current == target || target > names.Count - 1)
                {
                    target = int.MinValue;
                    dirty = true;
                    Version++;
                    return;
                }

                if (current > target)
                {
                    Undo.PerformUndo();
                }
                else
                {
                    Undo.PerformRedo();
                }

                readUndoList(names, out int after);
                if (after == current)
                {
                    // Unity refused the step: stop there.
                    target = int.MinValue;
                    dirty = true;
                    Version++;
                    return;
                }

                cursor = after;
            }

            Version++;
        }

        // ---- Kept across script reloads (Unity keeps its undo history; this editor process only) --------------------

        private static string FilePath => Path.Combine("Library", "Orbiters", "Logger", "undo.bin");

        private static int ProcessId
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().Id;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var writer = new BinaryWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(ProcessId);
                    writer.Write(steps.Count);
                    foreach (var step in steps)
                    {
                        writer.Write(step.Name ?? string.Empty);
                        writer.Write(step.Time);
                        byte[] jpg = SceneThumbnails.Get(step.Thumbnail);
                        writer.Write(jpg?.Length ?? 0);
                        if (jpg != null)
                        {
                            writer.Write(jpg);
                        }
                    }
                }
            }
            catch (Exception)
            {
                Delete();
            }
        }

        private static void Load()
        {
            steps.Clear();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                using (var reader = new BinaryReader(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion || reader.ReadInt32() != ProcessId)
                    {
                        return;
                    }

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var step = new UndoStep { Name = reader.ReadString(), Time = reader.ReadInt64() };
                        int length = reader.ReadInt32();
                        if (length > 0)
                        {
                            step.Thumbnail = SceneThumbnails.Add(reader.ReadBytes(length));
                        }

                        steps.Add(step);
                    }
                }

                Version++;
            }
            catch (Exception)
            {
                steps.Clear();
            }
        }

        private static void Delete()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception)
            {
                // Replaced at the next save.
            }
        }
    }
}
