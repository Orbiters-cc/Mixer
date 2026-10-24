using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Reads entries already in Unity's console (logged before the Mixer console listened) into the store, a slice per editor
    /// update so a console with millions of entries never freezes the editor. Each row costs one short read (its first
    /// two lines and timestamp); the full entry is only read the first time a message is seen.
    /// <para>
    /// <see cref="Placement.Before"/> collects into its own columns and puts them in front of the store at the end;
    /// <see cref="Placement.After"/> appends directly (the few logs Unity received during a script reload).
    /// </para>
    /// </summary>
    internal sealed class ConsoleHistory
    {
        internal enum Placement
        {
            Before,
            After
        }

        private readonly int from;
        private readonly int to;
        private readonly (int row, string text)[] anchors;
        private readonly Placement placement;
        private readonly long fallbackTime;
        private readonly DateTime startDay;
        private readonly int startSeconds;
        private readonly Dictionary<(LogVariant variant, string text), int> signatures = new Dictionary<(LogVariant variant, string text), int>();
        private int next;
        private bool verified;
        private int lastSeconds = -1;
        private long lastTicks;
        private int dayOffset;
        private long[] times = new long[0];
        private int[] owners = new int[0];
        private byte[] flags = new byte[0];
        private int count;
        private readonly Dictionary<int, int> contexts = new Dictionary<int, int>();

        public ConsoleHistory(int from, int to, Placement placement, long fallbackTime, params (int row, string text)[] anchors)
        {
            this.from = Math.Max(0, from);
            this.to = Math.Max(this.from, to);
            this.placement = placement;
            this.fallbackTime = fallbackTime;
            this.anchors = anchors ?? new (int, string)[0];
            next = this.from;
            var editorStart = DateTime.Now - TimeSpan.FromSeconds(UnityEditor.EditorApplication.timeSinceStartup);
            startDay = editorStart.Date;
            startSeconds = (int)editorStart.TimeOfDay.TotalSeconds;
            if (placement == Placement.Before)
            {
                int capacity = Math.Min(this.to - this.from, 1 << 20);
                times = new long[capacity];
                owners = new int[capacity];
                flags = new byte[capacity];
            }
        }

        public int From => from;
        public int To => to;
        public bool Prepends => placement == Placement.Before;

        /// <summary>The anchor text of the last row read, saved with the session when scripts reload mid-import.</summary>
        public string EndAnchor
        {
            get
            {
                foreach (var (row, text) in anchors)
                {
                    if (row == to - 1)
                    {
                        return text;
                    }
                }

                return string.Empty;
            }
        }
        public int Total => to - from;
        public int Done => next - from;
        public bool Finished { get; private set; }
        public bool Failed { get; private set; }
        public float Progress => Total > 0 ? (float)Done / Total : 1f;

        /// <summary>The first two lines of a row without its timestamp: checks the console still holds the same entries.</summary>
        public static string Anchor(int row)
        {
            if (row < 0 || !UnityConsole.Available)
            {
                return string.Empty;
            }

            using (new UnityConsole.ShowAllScope(timestamps: false))
            using (var reader = UnityConsole.Reader.Open())
            {
                if (reader == null || row >= reader.Count)
                {
                    return string.Empty;
                }

                UnityConsole.TrySplitTimestamp(reader.Lines(row, 2, out _), out _, out string text);
                return text;
            }
        }

        /// <summary>Reads rows for about <paramref name="budgetMs"/> milliseconds. True when finished (or failed).</summary>
        public bool Step(LogStore store, double budgetMs)
        {
            if (Finished)
            {
                return true;
            }

            if (!UnityConsole.Available || to <= from)
            {
                return Finish(store, failed: !UnityConsole.Available);
            }

            var clock = Stopwatch.StartNew();
            using (var scope = new UnityConsole.ShowAllScope(timestamps: true))
            using (var reader = UnityConsole.Reader.Open())
            {
                if (reader == null || reader.Count < to)
                {
                    // Unity's console was cleared since: those entries are gone.
                    return Finish(store, failed: true);
                }

                if (!verified)
                {
                    foreach (var (row, text) in anchors)
                    {
                        if (row < 0 || string.IsNullOrEmpty(text))
                        {
                            continue;
                        }

                        UnityConsole.TrySplitTimestamp(reader.Lines(row, 2, out _), out _, out string current);
                        if (!string.Equals(current, text, StringComparison.Ordinal))
                        {
                            return Finish(store, failed: true);
                        }
                    }

                    verified = true;
                }

                // Changing the console's filters costs Unity a pass over its entries: take bigger slices then.
                double budget = scope.Changed ? Math.Max(budgetMs, 40d) : budgetMs;
                while (next < to)
                {
                    ReadRow(store, reader, next);
                    next++;
                    if ((next & 255) == 0 && clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        break;
                    }
                }
            }

            return next >= to ? Finish(store, failed: false) : false;
        }

        private void ReadRow(LogStore store, UnityConsole.Reader reader, int row)
        {
            string lines = reader.Lines(row, 2, out int mode);
            long time = fallbackTime;
            var occurrenceFlags = OccurrenceFlags.ApproximateTime | OccurrenceFlags.FromUnityConsole;
            if (UnityConsole.TrySplitTimestamp(lines, out int seconds, out string text))
            {
                time = TimeOf(seconds);
            }
            else
            {
                text = lines;
            }

            var variant = UnityConsole.VariantOf(mode);
            var signature = (variant, text);
            int instanceId = 0;
            if (!signatures.TryGetValue(signature, out int message))
            {
                if (reader.TryRead(row, out var entry))
                {
                    instanceId = entry.InstanceId;
                    message = store.Intern(entry.Condition, LogVariants.IsCompile(variant) ? string.Empty : entry.Stack, variant, 0, entry.File, entry.Line, entry.Column);
                }
                else
                {
                    message = store.Intern(text, string.Empty, variant);
                }

                signatures[signature] = message;
            }

            if (placement == Placement.After)
            {
                int occurrence = store.Append(time, message, occurrenceFlags);
                if (instanceId != 0)
                {
                    store.SetContext(occurrence, instanceId);
                }

                return;
            }

            if (count == times.Length)
            {
                int size = Math.Max(1024, times.Length * 2);
                Array.Resize(ref times, size);
                Array.Resize(ref owners, size);
                Array.Resize(ref flags, size);
            }

            if (instanceId != 0)
            {
                contexts[count] = instanceId;
            }

            times[count] = time;
            owners[count] = message;
            flags[count] = (byte)occurrenceFlags;
            count++;
        }

        // Unity's console timestamps are local times of day: rows are in order, so a jump back means midnight passed.
        private long TimeOf(int seconds)
        {
            if (seconds == lastSeconds)
            {
                return lastTicks;
            }

            if (lastSeconds < 0)
            {
                dayOffset = seconds + 300 < startSeconds ? 1 : 0;
            }
            else if (seconds + 3600 < lastSeconds)
            {
                dayOffset++;
            }

            lastSeconds = seconds;
            var local = startDay.AddDays(dayOffset).AddSeconds(seconds);
            lastTicks = local.ToUniversalTime().Ticks;
            return lastTicks;
        }

        private bool Finish(LogStore store, bool failed)
        {
            Finished = true;
            Failed = failed;
            if (!failed && placement == Placement.Before && count > 0)
            {
                store.Prepend(times, owners, flags, count, contexts);
            }

            times = new long[0];
            owners = new int[0];
            flags = new byte[0];
            count = 0;
            return true;
        }
    }
}
