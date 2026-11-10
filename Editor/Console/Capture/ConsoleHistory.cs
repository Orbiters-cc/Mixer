using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Reads entries already in Unity's console (logged before the Mixer console listened) into the store, a slice per editor
    /// update so a console with millions of entries never freezes the editor. Each row costs one short read (its first
    /// two lines and timestamp); the full entry is only read the first time a message is seen (the oldest row of a
    /// message gives its text and stack). Rows are read oldest first, times derived forward.
    /// <para>
    /// <see cref="Placement.Before"/> goes in three phases, each bounded per update (time and characters checked before
    /// each row):
    /// <list type="number">
    /// <item>Measure (a new import only): from the newest row back, what the rows would cost the store (one row each
    /// against the log-count cap; the texts of each new signature, by the same signature and lengths the import uses)
    /// until the first row that no longer fits what the store can give the history: its free text budget and log count,
    /// less an eighth of each kept for live logs arriving during the import (a startup's logs are far fewer; past it
    /// the ordinary trim and the ceiling still apply). That row's successor is the first row read. When everything fits,
    /// nothing changes: every row is read, times anchored as round 1 did. Keeps hashes and counts, never texts; a reload
    /// while measuring restarts the measure cleanly (nothing of it is in the store).</item>
    /// <item>Read: each row is handed to the store at once (<see cref="LogStore.Stage"/>): from then on the store counts
    /// it, so its message is never dead, the limits apply to it and a compaction keeps it; since the measure left room,
    /// no trim or compaction runs unless live logs flood meanwhile (the safety net).</item>
    /// <item>Publish: the staged rows go in front of the store's occurrences, the newest batch first, as many batches as
    /// one update's time allows (<see cref="LogStore.PublishStaged"/>).</item>
    /// </list>
    /// The signature cache holds message ids across updates; it is dropped when the store's messages are renumbered
    /// (its epoch changes, checked before each row): until then a dead message's id still names that very message.
    /// </para>
    /// <para>
    /// <see cref="Placement.After"/> appends directly, oldest first (the few logs Unity received during a script reload).
    /// </para>
    /// <para>
    /// What can happen meanwhile. Rows Unity adds to its console during the import are past <c>to</c>: live logs,
    /// never imported. At every slice the console must still hold the same rows: at least <c>to</c> of them, the range's
    /// anchors, and the last row read still at its index; otherwise (cleared, or cleared and refilled) the import fails
    /// as for a cleared console: rows already put in front stay, staged rows are let go, the rest are gone with Unity's
    /// console. A script reload: the staged rows are not saved; <see cref="SaveTo"/> keeps the range left (from the first
    /// row that fits up to the rows already put in front), its anchors and the time state the import started from, and
    /// <see cref="Resume"/> reads that range again from its first row with that very state, so no row is in the store
    /// twice, none is skipped and times come out the same.
    /// </para>
    /// </summary>
    internal sealed class ConsoleHistory
    {
        internal enum Placement
        {
            Before,
            After
        }

        private enum Phase
        {
            Measure,
            Read,
            Publish
        }

        // What one update may read: until its time is up or this many characters of rows' texts (checked before each row).
        internal const int MaxCharsPerUpdate = 1 << 20;
        // Rows one batch puts in front (each batch also copies the store's front chunk).
        internal const int MaxPublishedPerUpdate = 16_384;

        private readonly int to;
        private readonly (int row, string text)[] anchors;
        private readonly Placement placement;
        private readonly Dictionary<(LogVariant variant, string text), int> signatures = new Dictionary<(LogVariant variant, string text), int>();
        private int from;
        private string firstAnchor;
        private long fallbackTime;
        // Times: rows' times of day are put on startDay (+ dayOffset). Without a measured day, the first timed row is the
        // next day when it is earlier than the editor's start (round 1's rule); with one, it is that day.
        private DateTime startDay;
        private int startSeconds;
        private bool anchorIsRowDay;
        private LogStore signaturesStore;
        private int signaturesEpoch = -1;
        private Phase phase;
        private int next;
        private int published;
        private bool verified;
        private bool borrowed;
        // Anchors came from a saved state: an empty one means it could not be read then.
        private bool resumed;
        // Both ends of the range, read from the console at the first slice (or given by Resume) and checked at every one.
        private int endHash;
        private int startHash;
        private int startRow;
        private bool ends;
        private string endText;
        // The row the measure last counted, and its hash: still there at the next slice, or another console.
        private int lastMeasured = -1;
        private int lastMeasuredHash;
        private int lastRead = -1;
        private int lastReadHash;
        private int lastSeconds = -1;
        private long lastTicks;
        private int dayOffset;
        // The measure: signatures seen (hashes), what they and the rows cost, and the day state counted back from now.
        // Each signature met and the cost counted for it (the largest of its rows' so far).
        private Dictionary<long, long> measuredSignatures;
        private long measuredChars;
        private int measuredRows;
        private int measureNext;
        private int backSeconds = -1;
        private int backDay;
        private DateTime measuredDay;
        private bool measuredTimed;

        public ConsoleHistory(int from, int to, Placement placement, long fallbackTime, params (int row, string text)[] anchors)
        {
            this.from = Math.Max(0, from);
            this.to = Math.Max(this.from, to);
            this.placement = placement;
            this.fallbackTime = fallbackTime;
            this.anchors = anchors ?? new (int, string)[0];
            next = this.from;
            published = this.to;
            phase = placement == Placement.Before ? Phase.Measure : Phase.Read;
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
                anchorIsRowDay = false;
            }
        }

        /// <summary>"Now" as the measure counts midnights back from (tests set it).</summary>
        internal DateTime Now { get; set; } = DateTime.Now;

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
                    if (row == to - 1 && !string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }

                // As read at the first slice (a fresh import's own anchor may have been unreadable as it started).
                return endText ?? string.Empty;
            }
        }

        public int Total => to - from;
        public int Done => next - from;
        public bool Finished { get; private set; }
        public bool Failed { get; private set; }

        /// <summary>What stopped the import, when an exception did (reported once by its caller).</summary>
        public Exception Error { get; private set; }
        public float Progress => Total > 0 ? (float)Done / Total : 1f;

        /// <summary>The import's state to save before a script reload (see <see cref="Resume"/>).</summary>
        public void SaveTo(SnapshotInfo info, string endAnchor)
        {
            bool measured = phase != Phase.Measure;
            info.HistoryFrom = from;
            info.HistoryTo = published;
            info.HistoryAnchor = endAnchor ?? string.Empty;
            info.HistoryFirstAnchor = measured ? firstAnchor ?? string.Empty : string.Empty;
            info.HistoryMeasured = measured;
            info.HistoryAnchorTicks = startDay.AddSeconds(startSeconds).Ticks;
            info.HistoryAnchorIsRowDay = anchorIsRowDay;
            info.HistoryFallback = fallbackTime;
        }

        /// <summary>The import a saved state goes on with: the range left, read again from its first row with its time state.</summary>
        public static ConsoleHistory Resume(SnapshotInfo info, IConsoleSource source = null)
        {
            var anchors = new List<(int, string)> { (info.HistoryTo - 1, info.HistoryAnchor) };
            if (info.HistoryMeasured)
            {
                anchors.Add((info.HistoryFrom, info.HistoryFirstAnchor));
            }

            var history = new ConsoleHistory(info.HistoryFrom, info.HistoryTo, Placement.Before, info.HistoryFallback, anchors.ToArray());
            if (source != null)
            {
                history.Source = source;
            }

            var anchor = new DateTime(info.HistoryAnchorTicks, DateTimeKind.Local);
            history.startDay = anchor.Date;
            history.startSeconds = (int)anchor.TimeOfDay.TotalSeconds;
            history.anchorIsRowDay = info.HistoryAnchorIsRowDay;
            history.firstAnchor = info.HistoryFirstAnchor;
            history.resumed = true;
            if (info.HistoryMeasured)
            {
                // Measured before the reload: the range starts at the first row that fit then.
                history.phase = Phase.Read;
            }

            return history;
        }

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
            if (phase == Phase.Publish)
            {
                return Publish(store, budgetMs, clock);
            }

            // Borrowed once for the whole import and given back once (Finish, a failure, LogCapture before a domain reload):
            // every borrow and give-back makes Unity filter its whole console again. A Unity Console window shown meanwhile
            // shows every entry with timestamps until the import ends.
            bool changed = false;
            if (!borrowed)
            {
                changed = Source.TakeAll();
                borrowed = true;
            }

            try
            {
                // Changing the console's filters costs Unity a pass over its entries: take bigger slices then.
                double budget = changed ? Math.Max(budgetMs, 40d) : budgetMs;
                using (var reader = Source.Open())
                {
                    if (!SameConsole(reader))
                    {
                        return Finish(store, failed: true);
                    }

                    if (phase == Phase.Measure)
                    {
                        Measure(store, reader, budget, clock);
                    }
                    else
                    {
                        ReadSlice(store, reader, budget, clock);
                    }
                }

                if (phase == Phase.Read && next >= to)
                {
                    if (placement == Placement.After)
                    {
                        return Finish(store, failed: false);
                    }

                    phase = Phase.Publish;
                }
            }
            catch (Exception exception)
            {
                // Never retried: the same row would fail at every update. Finish gives the console settings back and lets
                // go of the staged rows; the caller reports Error once.
                Error = exception;
                return Finish(store, failed: true);
            }
            return false;
        }

        // The console still holds the rows this import reads: at least `to`; the anchors it was given (an anchor that
        // could not be read, empty, fails rather than matching anything; a fresh import's own end anchor may be empty,
        // when Unity's console didn't show every entry as it started); both ends of the range as they were at the first
        // slice; and the last row measured or read at its index.
        private bool SameConsole(IConsoleRows reader)
        {
            if (reader == null || reader.Count < to)
            {
                return false;
            }

            if (!verified)
            {
                foreach (var (row, text) in anchors)
                {
                    if (row < 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(text) ? resumed : !string.Equals(TextOf(reader, row, out _), text, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                verified = true;
            }

            if (!ends)
            {
                startRow = from;
                startHash = RowHash(reader, startRow);
                endHash = RowHash(reader, to - 1);
                endText = TextOf(reader, to - 1, out _);
                ends = true;
            }
            else if (RowHash(reader, startRow) != startHash || RowHash(reader, to - 1) != endHash)
            {
                return false;
            }

            return (lastRead < 0 || RowHash(reader, lastRead) == lastReadHash) &&
                   (lastMeasured < 0 || RowHash(reader, lastMeasured) == lastMeasuredHash);
        }

        private static string TextOf(IConsoleRows reader, int row, out int mode)
        {
            UnityConsole.TrySplitTimestamp(reader.Lines(row, 2, out mode), out _, out string text);
            return text;
        }

        private static int RowHash(IConsoleRows reader, int row) => TextTable.Hash(reader.Lines(row, 2, out int mode)) ^ mode;

        // From the newest row back: where the import starts (see the class comment).
        private void Measure(LogStore store, IConsoleRows reader, double budget, Stopwatch clock)
        {
            if (measuredSignatures == null)
            {
                // Sized once from the rows (bounded): it never rehashes while measuring.
                measuredSignatures = new Dictionary<long, long>(Math.Min(to - from, 1 << 20));
                measureNext = to;
                var now = Now;
                measuredDay = now.Date;
            }

            long margin = store.MaxTextChars / 8;
            long charRoom = store.MaxTextChars - margin - store.Texts.TotalChars;
            long rowRoom = store.MaxOccurrences - store.MaxOccurrences / 8 - (long)store.Total;
            int nowSeconds = (int)Now.TimeOfDay.TotalSeconds;
            long chars = 0;
            bool first = true;
            while (measureNext > from)
            {
                if (!first && (clock.Elapsed.TotalMilliseconds >= budget || chars >= MaxCharsPerUpdate))
                {
                    return;
                }

                first = false;
                int row = measureNext - 1;
                string lines = reader.Lines(row, 2, out int mode);
                bool timed = UnityConsole.TrySplitTimestamp(lines, out int seconds, out string text);
                if (!timed)
                {
                    text = lines;
                }

                var variant = UnityConsole.VariantOf(mode);
                long signature = ((long)TextTable.Hash(text) << 8) ^ (long)variant;
                // Going back, a signature's last row met is the oldest in the range so far: the one the forward import
                // would keep for it if the range started here. Its cost replaces what was counted when it is more (the
                // largest is kept: conservative).
                long cost;
                if (reader.TryRead(row, out var entry))
                {
                    string stack = LogVariants.IsCompile(variant) ? string.Empty : entry.Stack;
                    cost = store.InternCost(entry.Condition, stack, entry.File);
                    chars += (entry.Condition?.Length ?? 0) + (long)stack.Length;
                }
                else
                {
                    cost = store.InternCost(text, string.Empty, null);
                    chars += text.Length;
                }

                measuredSignatures.TryGetValue(signature, out long counted);
                long extra = Math.Max(0, cost - counted);
                if (measuredChars + extra > charRoom || measuredRows + 1 > rowRoom)
                {
                    // This row and the older ones don't fit: the import starts after it.
                    StartAt(measureNext, reader);
                    return;
                }

                if (extra > 0)
                {
                    measuredSignatures[signature] = counted + extra;
                    measuredChars += extra;
                }

                measuredRows++;
                if (timed)
                {
                    // Midnights counted back from now: the newest timed row is today unless it is later than now.
                    if (backSeconds < 0)
                    {
                        backDay = seconds > nowSeconds + 300 ? -1 : 0;
                    }
                    else if (seconds > backSeconds + 3600)
                    {
                        backDay--;
                    }

                    backSeconds = seconds;
                    measuredTimed = true;
                }

                measureNext = row;
                lastMeasured = row;
                lastMeasuredHash = TextTable.Hash(lines) ^ mode;
            }

            StartAt(from, reader);
        }

        private void StartAt(int start, IConsoleRows reader)
        {
            if (start > from && measuredTimed)
            {
                // Rows before `start` are never read: the first timed row read gets the day the measure counted back for
                // the oldest timed row it kept (exact when every row has a time, best effort otherwise).
                startDay = measuredDay.AddDays(backDay);
                startSeconds = 0;
                anchorIsRowDay = true;
            }

            from = start;
            next = start;
            measuredSignatures = null;
            phase = Phase.Read;
            firstAnchor = start < to && start < reader.Count ? TextOf(reader, start, out _) : string.Empty;
        }

        private void ReadSlice(LogStore store, IConsoleRows reader, double budget, Stopwatch clock)
        {
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
                lastRead = next;
                lastReadHash = RowHash(reader, next);
                next++;
            }
        }

        private bool Publish(LogStore store, double budgetMs, Stopwatch clock)
        {
            if (borrowed)
            {
                // Unity's console is not read any more.
                Source.ReleaseAll();
                borrowed = false;
            }

            bool first = true;
            while (store.StagedCount > 0)
            {
                // Before each batch (its rows and the front chunk it copies), never after.
                if (!first && clock.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    return false;
                }

                first = false;
                int count = Math.Min(MaxPublishedPerUpdate, store.StagedCount);
                published -= count;
                store.PublishStaged(count);
            }

            // Rows trimmed while staged (live logs flooded meanwhile) are not in the store: the import is complete.
            published = from;
            return Finish(store, failed: false);
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
                dayOffset = anchorIsRowDay ? 0 : seconds + 300 < startSeconds ? 1 : 0;
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
            borrowed = false;
            if (failed && placement == Placement.Before)
            {
                // Rows already in front stay; staged ones go (their console is gone or changed).
                store.DiscardStaged();
            }

            return true;
        }
    }
}
