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
    /// <see cref="Placement.Before"/> reads from the newest row back and puts each slice in front of the store at the end
    /// of its update: nothing is held across updates (no pin), so the store's text budget and ceiling hold during the
    /// whole import. Before a new message is interned, its texts must fit the budget; once they don't, the import stops:
    /// the older history goes, as the store's own trim would have decided (the newest logs win).
    /// <see cref="Placement.After"/> appends directly, oldest first (the few logs Unity received during a script reload).
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
        private readonly DateTime nowDay;
        private readonly int nowSeconds;
        private readonly Dictionary<(LogVariant variant, string text), int> signatures = new Dictionary<(LogVariant variant, string text), int>();
        // The store and epoch the cached message ids belong to (a compaction or a clear renumbers messages).
        private LogStore signaturesStore;
        private int signaturesEpoch = -1;
        // After: the next row to read (forward). Before: one past the next row to read (backward).
        private int next;
        private bool verified;
        private int lastSeconds = -1;
        private long lastTicks;
        private int dayOffset;
        // One slice of history (Before), newest first, put in front at the end of the update that read it.
        private readonly List<long> times = new List<long>();
        private readonly List<int> owners = new List<int>();
        private readonly List<byte> flags = new List<byte>();
        private readonly List<KeyValuePair<int, int>> contexts = new List<KeyValuePair<int, int>>();

        public ConsoleHistory(int from, int to, Placement placement, long fallbackTime, params (int row, string text)[] anchors)
        {
            this.from = Math.Max(0, from);
            this.to = Math.Max(this.from, to);
            this.placement = placement;
            this.fallbackTime = fallbackTime;
            this.anchors = anchors ?? new (int, string)[0];
            next = placement == Placement.Before ? this.to : this.from;
            var editorStart = DateTime.Now - TimeSpan.FromSeconds(UnityEditor.EditorApplication.timeSinceStartup);
            startDay = editorStart.Date;
            startSeconds = (int)editorStart.TimeOfDay.TotalSeconds;
            var now = DateTime.Now;
            nowDay = now.Date;
            nowSeconds = (int)now.TimeOfDay.TotalSeconds;
        }

        public int From => from;

        /// <summary>Where the rows come from: Unity's console (tests give their own).</summary>
        internal IConsoleSource Source { get; set; } = UnityConsoleSource.Instance;

        /// <summary>The end of the rows still to import (Before: what is left goes up to here).</summary>
        public int To => placement == Placement.Before ? next : to;

        public bool Prepends => placement == Placement.Before;

        /// <summary>
        /// The anchor text of the last row still to import, saved with the session when scripts reload mid-import (read
        /// from Unity's console now: rows above it are already in the store).
        /// </summary>
        public string EndAnchor
        {
            get
            {
                if (placement == Placement.Before && next < to)
                {
                    return next > from ? Anchor(next - 1) : string.Empty;
                }

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
        public int Done => placement == Placement.Before ? to - next : next - from;
        public bool Finished { get; private set; }
        public bool Failed { get; private set; }

        /// <summary>What stopped the import, when an exception did (reported once by its caller).</summary>
        public Exception Error { get; private set; }
        public float Progress => Total > 0 ? (float)Done / Total : 1f;

        /// <summary>
        /// The first two lines of a row without its timestamp: checks the console still holds the same entries. Empty
        /// (no check) when Unity's console doesn't list every entry right now: changing its settings just for this would
        /// cost it two passes over all its entries.
        /// </summary>
        public static string Anchor(int row)
        {
            if (row < 0 || !UnityConsole.RowsAreComplete)
            {
                return string.Empty;
            }

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

            if (!Source.Available || to <= from)
            {
                return Finish(failed: !Source.Available);
            }

            if (!ReferenceEquals(store, signaturesStore) || store.Epoch != signaturesEpoch)
            {
                signatures.Clear();
                signaturesStore = store;
                signaturesEpoch = store.Epoch;
            }

            var clock = Stopwatch.StartNew();
            // Held until the import ends (Finish), unless a Unity Console window may show the borrowed settings.
            bool changed = Source.TakeAll();
            try
            {
                bool over = ReadSlice(store, budgetMs, changed, clock, out bool failed);
                // The slice goes in at once: no message id is held across updates.
                PutInFront(store);
                // A full store would trim older history again at once: the import ends (the newest logs win).
                over |= placement == Placement.Before && store.Count >= store.MaxOccurrences;
                if (over)
                {
                    return Finish(failed);
                }
            }
            catch (Exception exception)
            {
                // Never retried: the same row would fail at every update. Finish gives the console settings back; the
                // caller reports Error once.
                Error = exception;
                times.Clear();
                owners.Clear();
                flags.Clear();
                contexts.Clear();
                return Finish(failed: true);
            }
            finally
            {
                if (!Finished && Source.WindowMayShow)
                {
                    Source.ReleaseAll();
                }
            }

            return false;
        }

        // True when the import is over (read to the end, the budget is full, or the console no longer holds its rows).
        private bool ReadSlice(LogStore store, double budgetMs, bool changed, Stopwatch clock, out bool failed)
        {
            failed = false;
            using (var reader = Source.Open())
            {
                if (reader == null || reader.Count < to)
                {
                    // Unity's console was cleared since: those entries are gone.
                    failed = true;
                    return true;
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
                            failed = true;
                            return true;
                        }
                    }

                    verified = true;
                }

                // Changing the console's filters costs Unity a pass over its entries: take bigger slices then.
                double budget = changed ? Math.Max(budgetMs, 40d) : budgetMs;
                int read = 0;
                if (placement == Placement.After)
                {
                    while (next < to)
                    {
                        ReadRow(store, reader, next);
                        next++;
                        if ((++read & 255) == 0 && clock.Elapsed.TotalMilliseconds >= budget)
                        {
                            break;
                        }
                    }

                    return next >= to;
                }

                while (next > from)
                {
                    if (!ReadRow(store, reader, next - 1))
                    {
                        // The store's texts are full: older history would only be trimmed again.
                        return true;
                    }

                    next--;
                    if ((++read & 255) == 0 && clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        break;
                    }
                }
            }

            return next <= from;
        }

        // False when the row's message is new and its texts don't fit the store's budget (nothing is interned then).
        private bool ReadRow(LogStore store, IConsoleRows reader, int row)
        {
            string lines = reader.Lines(row, 2, out int mode);
            long time = fallbackTime;
            var occurrenceFlags = OccurrenceFlags.ApproximateTime | OccurrenceFlags.FromUnityConsole;
            bool stamped = UnityConsole.TrySplitTimestamp(lines, out int seconds, out string text);
            if (!stamped)
            {
                text = lines;
            }

            var variant = UnityConsole.VariantOf(mode);
            var signature = (variant, text);
            int instanceId = 0;
            if (!signatures.TryGetValue(signature, out int message))
            {
                string condition = text;
                string stack = string.Empty;
                string file = null;
                int line = 0;
                int column = 0;
                if (reader.TryRead(row, out var entry))
                {
                    instanceId = entry.InstanceId;
                    condition = entry.Condition;
                    stack = LogVariants.IsCompile(variant) ? string.Empty : entry.Stack;
                    file = entry.File;
                    line = entry.Line;
                    column = entry.Column;
                }

                if (placement == Placement.Before && !store.HasRoomFor(condition, stack))
                {
                    return false;
                }

                message = store.Intern(condition, stack, variant, 0, file, line, column);
                signatures[signature] = message;
            }

            if (stamped)
            {
                time = placement == Placement.Before ? TimeBackOf(seconds) : TimeOf(seconds);
            }

            if (placement == Placement.After)
            {
                int occurrence = store.Append(time, message, occurrenceFlags);
                if (instanceId != 0)
                {
                    store.SetContext(occurrence, instanceId);
                }

                if (store.Epoch != signaturesEpoch)
                {
                    // The append compacted (the text ceiling): message ids changed.
                    signatures.Clear();
                    signaturesEpoch = store.Epoch;
                }

                return true;
            }

            if (instanceId != 0)
            {
                contexts.Add(new KeyValuePair<int, int>(times.Count, instanceId));
            }

            times.Add(time);
            owners.Add(message);
            flags.Add((byte)occurrenceFlags);
            return true;
        }

        // The slice read (newest first) in front of the store, oldest first.
        private void PutInFront(LogStore store)
        {
            int count = times.Count;
            if (count == 0)
            {
                return;
            }

            var sliceTimes = new long[count];
            var sliceOwners = new int[count];
            var sliceFlags = new byte[count];
            for (int i = 0; i < count; i++)
            {
                sliceTimes[i] = times[count - 1 - i];
                sliceOwners[i] = owners[count - 1 - i];
                sliceFlags[i] = flags[count - 1 - i];
            }

            Dictionary<int, int> sliceContexts = null;
            if (contexts.Count > 0)
            {
                sliceContexts = new Dictionary<int, int>(contexts.Count);
                foreach (var pair in contexts)
                {
                    sliceContexts[count - 1 - pair.Key] = pair.Value;
                }
            }

            times.Clear();
            owners.Clear();
            flags.Clear();
            contexts.Clear();
            store.Prepend(sliceTimes, sliceOwners, sliceFlags, count, sliceContexts);
            // Prepend may compact (budget, ceiling): cached message ids would name other messages.
            if (store.Epoch != signaturesEpoch)
            {
                signatures.Clear();
                signaturesEpoch = store.Epoch;
            }
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

        // The same read from the newest row back: the newest is today unless it is later than now (then yesterday), and a
        // jump forward going back means midnight passed.
        private long TimeBackOf(int seconds)
        {
            if (seconds == lastSeconds)
            {
                return lastTicks;
            }

            if (lastSeconds < 0)
            {
                dayOffset = seconds > nowSeconds + 300 ? -1 : 0;
            }
            else if (seconds > lastSeconds + 3600)
            {
                dayOffset--;
            }

            lastSeconds = seconds;
            var local = nowDay.AddDays(dayOffset).AddSeconds(seconds);
            lastTicks = local.ToUniversalTime().Ticks;
            return lastTicks;
        }

        private bool Finish(bool failed)
        {
            Finished = true;
            Failed = failed;
            Source.ReleaseAll();
            return true;
        }
    }
}
