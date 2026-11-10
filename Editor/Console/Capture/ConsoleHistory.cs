using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Reads entries already in Unity's console (logged before the Mixer console listened) into the store, a slice per editor
    /// update so a console with millions of entries never freezes the editor. Each row costs one short read (its first
    /// two lines and timestamp); the full entry is only read the first time a message is seen (the oldest row of a
    /// message gives its text and stack). Rows are read oldest first, times derived forward from the editor's start.
    /// <para>
    /// <see cref="Placement.Before"/>: each row read is handed to the store at once (<see cref="LogStore.Stage"/>): from
    /// then on the store counts it, so its message is never dead, the limits apply to it (history is the oldest: a trim
    /// drops its oldest rows first, so the newest history wins, and the ceiling holds at every moment) and a compaction
    /// keeps and renumbers it. Once every row is read, the rows go in front of the store's occurrences, the newest batch
    /// first, a bounded batch per update (<see cref="LogStore.PublishStaged"/>). Nothing is held here across updates
    /// but message ids in the signature cache, dropped when the store's messages are renumbered (a compaction or a
    /// clear changes its epoch): a dead message's id still names that message until then, so a row that finds it
    /// revives that very message, never another text.
    /// </para>
    /// <para>
    /// <see cref="Placement.After"/> appends directly, oldest first (the few logs Unity received during a script reload).
    /// </para>
    /// <para>
    /// What can happen meanwhile: rows Unity adds to its console during the import are past <c>to</c>, received live,
    /// never imported. A console cleared (or changed) meanwhile fails the import at its next update: rows already put
    /// in front stay, rows staged are let go, the rest are gone with Unity's console. A script reload while rows are
    /// read: the staged rows are not saved, and the next domain reads the same range again from its first row with the
    /// same time state (the editor's start), as round 1 did. A reload while rows go in front: the newest ones are in the
    /// store already; the next domain reads the rows before them again from the range's first row, so no row is in
    /// the store twice and times are derived as before.
    /// </para>
    /// </summary>
    internal sealed class ConsoleHistory
    {
        internal enum Placement
        {
            Before,
            After
        }

        // What one update may do: rows read until this long or until this many characters of new messages' texts
        // (checked before each row), then rows put in front, this many at most.
        internal const int MaxCharsPerUpdate = 1 << 20;
        internal const int MaxPublishedPerUpdate = 16_384;

        private readonly int from;
        private readonly int to;
        private readonly (int row, string text)[] anchors;
        private readonly Placement placement;
        private readonly long fallbackTime;
        private DateTime startDay;
        private int startSeconds;
        private readonly Dictionary<(LogVariant variant, string text), int> signatures = new Dictionary<(LogVariant variant, string text), int>();
        // The store and epoch the cached message ids belong to.
        private LogStore signaturesStore;
        private int signaturesEpoch = -1;
        private int next;
        // Before: rows from here to `to` are in front of the store already (put in, newest first).
        private int published;
        private bool verified;
        private int lastSeconds = -1;
        private long lastTicks;
        private int dayOffset;

        public ConsoleHistory(int from, int to, Placement placement, long fallbackTime, params (int row, string text)[] anchors)
        {
            this.from = Math.Max(0, from);
            this.to = Math.Max(this.from, to);
            this.placement = placement;
            this.fallbackTime = fallbackTime;
            this.anchors = anchors ?? new (int, string)[0];
            next = this.from;
            published = this.to;
            EditorStart = DateTime.Now - TimeSpan.FromSeconds(UnityEditor.EditorApplication.timeSinceStartup);
        }

        /// <summary>When the editor started (local time): what row times of day are anchored on (tests set it).</summary>
        internal DateTime EditorStart
        {
            get => startDay.AddSeconds(startSeconds);
            set
            {
                startDay = value.Date;
                startSeconds = (int)value.TimeOfDay.TotalSeconds;
            }
        }

        /// <summary>Where the rows come from: Unity's console (tests give their own).</summary>
        internal IConsoleSource Source { get; set; } = UnityConsoleSource.Instance;

        public int From => from;

        /// <summary>The end of the rows still to import: those from here on are in the store already.</summary>
        public int To => published;

        public bool Prepends => placement == Placement.Before;

        /// <summary>The anchor text of the last row still to import, saved with the session when scripts reload mid-import.</summary>
        public string EndAnchor
        {
            get
            {
                if (published < to)
                {
                    return published > from ? Anchor(published - 1) : string.Empty;
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
        public int Done => next - from;
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
                return Finish(store, failed: !Source.Available);
            }

            var clock = Stopwatch.StartNew();
            if (placement == Placement.Before && next >= to)
            {
                // Every row is read and staged: the newest go in front first.
                return Publish(store, budgetMs, clock);
            }

            // Held until the import ends (Finish), unless a Unity Console window may show the borrowed settings.
            bool changed = Source.TakeAll();
            try
            {
                if (ReadSlice(store, budgetMs, changed, clock, out bool failed))
                {
                    if (failed || placement == Placement.After)
                    {
                        return Finish(store, failed);
                    }
                }
            }
            catch (Exception exception)
            {
                // Never retried: the same row would fail at every update. Finish gives the console settings back and lets
                // go of the staged rows; the caller reports Error once.
                Error = exception;
                return Finish(store, failed: true);
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

        private bool Publish(LogStore store, double budgetMs, Stopwatch clock)
        {
            Source.ReleaseAll();
            while (store.StagedCount > 0)
            {
                int count = Math.Min(MaxPublishedPerUpdate, store.StagedCount);
                published -= count;
                store.PublishStaged(count);
                if (clock.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    break;
                }
            }

            if (store.StagedCount > 0)
            {
                return false;
            }

            // Rows trimmed while staged (older history beyond the limits) are not in the store: the import is complete.
            published = from;
            return Finish(store, failed: false);
        }

        // True when every row is read (or the console no longer holds its rows: failed).
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
                long chars = 0;
                bool first = true;
                while (next < to)
                {
                    // Before each row, never after: one row at most past the allowance (a row's texts are capped).
                    if (!first && (clock.Elapsed.TotalMilliseconds >= budget || chars >= MaxCharsPerUpdate))
                    {
                        break;
                    }

                    first = false;
                    chars += ReadRow(store, reader, next);
                    next++;
                }
            }

            return next >= to;
        }

        // Files one row (staged, or appended After); returns the characters of the new message's texts (0 when known).
        private long ReadRow(LogStore store, IConsoleRows reader, int row)
        {
            if (!ReferenceEquals(store, signaturesStore) || store.Epoch != signaturesEpoch)
            {
                // A compaction or a clear renumbered messages: cached ids would name others.
                signatures.Clear();
                signaturesStore = store;
                signaturesEpoch = store.Epoch;
            }

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
            long chars = 0;
            if (!signatures.TryGetValue(signature, out int message))
            {
                if (reader.TryRead(row, out var entry))
                {
                    instanceId = entry.InstanceId;
                    string stack = LogVariants.IsCompile(variant) ? string.Empty : entry.Stack;
                    message = store.Intern(entry.Condition, stack, variant, 0, entry.File, entry.Line, entry.Column);
                    chars = (entry.Condition?.Length ?? 0) + (long)stack.Length;
                }
                else
                {
                    message = store.Intern(text, string.Empty, variant);
                    chars = text.Length;
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
            }
            else
            {
                store.Stage(time, message, occurrenceFlags, instanceId);
            }

            return chars;
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
            Source.ReleaseAll();
            if (failed && placement == Placement.Before)
            {
                // Rows already in front stay; staged ones go (their console is gone or changed).
                store.DiscardStaged();
            }

            return true;
        }
    }
}
