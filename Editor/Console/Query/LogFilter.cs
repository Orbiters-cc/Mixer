using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orbiters.Logger.Editor
{
    internal enum GroupSort : byte
    {
        /// <summary>Stable: groups in the order they first appeared, like Unity's Collapse.</summary>
        FirstSeen,
        /// <summary>The most recently logged group last, next to the newest logs.</summary>
        LastSeen,
        /// <summary>The most frequent first.</summary>
        Count,
        /// <summary>Errors first, then warnings, then logs; the most frequent first within each.</summary>
        Level
    }

    /// <summary>Everything that decides which logs show. Copied for each filter run.</summary>
    internal sealed class FilterSpec
    {
        public int LevelMask = 7;
        public HashSet<string> HiddenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Muted = new HashSet<string>(StringComparer.Ordinal);
        public LogQuery Query = LogQuery.Empty;
        /// <summary>Time range brushed on the timeline (UTC ticks); <see cref="To"/> 0 = everything.</summary>
        public long From;
        public long To;
        public bool Grouped;
        public GroupSort Sort;

        public bool HasRange => To > From && To > 0;

        public FilterSpec Clone() => new FilterSpec
        {
            LevelMask = LevelMask,
            HiddenSources = new HashSet<string>(HiddenSources, StringComparer.OrdinalIgnoreCase),
            Muted = new HashSet<string>(Muted, StringComparer.Ordinal),
            Query = Query,
            From = From,
            To = To,
            Grouped = Grouped,
            Sort = Sort
        };

        /// <summary>
        /// What keeps one occurrence out of the list under this spec, by the same rules as a <see cref="FilterState"/> pass:
        /// <see cref="HiddenBy.None"/> when it shows, <see cref="HiddenBy.Gone"/> when no filter would bring it back.
        /// </summary>
        public HiddenBy WhatHides(LogStore store, int occurrence)
        {
            if (store == null || !store.Contains(occurrence) || (store.FlagsAt(occurrence) & LogStore.Superseded) != 0)
            {
                return HiddenBy.Gone;
            }

            int id = store.MessageAt(occurrence);
            ref var message = ref store.Message(id);
            if (message.Resolved)
            {
                return HiddenBy.Gone;
            }

            var hidden = HiddenBy.None;
            if (Muted.Count > 0 && Muted.Contains(store.Texts[store.Group(message.Group).Key]))
            {
                hidden |= HiddenBy.Muted;
            }

            if (!Query.IsEmpty)
            {
                bool found = message.Text > 0 && Query.Matches(store.Texts.Plain(message.Text)) ||
                             Query.IncludeStack && message.Stack > 0 && Query.Matches(store.Texts.Plain(message.Stack));
                if (!found)
                {
                    hidden |= HiddenBy.Search;
                }
            }

            if ((LevelMask & (1 << (int)message.Level)) == 0)
            {
                hidden |= HiddenBy.Level;
            }

            if (HiddenSources.Count > 0 && HiddenSources.Contains(store.SourceOf(id).Key))
            {
                hidden |= HiddenBy.Source;
            }

            long time = store.TimeAt(occurrence);
            if (HasRange && (time < From || time > To))
            {
                hidden |= HiddenBy.Range;
            }

            return hidden;
        }
    }

    /// <summary>What keeps a log out of the list (<see cref="FilterSpec.WhatHides"/>).</summary>
    [Flags]
    internal enum HiddenBy : byte
    {
        None = 0,
        Search = 1,
        Level = 2,
        Source = 4,
        /// <summary>Its message was hidden from the list ("Hide" on a row).</summary>
        Muted = 8,
        /// <summary>Outside the time range brushed on the timeline.</summary>
        Range = 16,
        /// <summary>Trimmed or cleared from the store, or a compiler message reported again or fixed since.</summary>
        Gone = 32
    }

    /// <summary>
    /// The filtered view of the store: rows (occurrence ids in time order), groups with their counts, counts per level
    /// and source for the toolbar, the timeline's buckets and each group's sparkline. Built by one pass over the
    /// occurrences (on a worker thread when the store is large) and then kept up to date as logs arrive.
    /// </summary>
    internal sealed class FilterState
    {
        public const int SparkBuckets = 24;
        private const int MaxSparkGroups = 50_000;
        // Messages a worker pass searches on several cores (fewer are not worth the hand-off).
        private const int ParallelSearchAbove = 20_000;
        // What one pass may spend on a regular expression in all (each text also has its own timeout): past it, the
        // messages left stay untested (not misses) and Extend tests them a slice at a time while the status says
        // "Searching…", so a pathological pattern typed or saved with the window can't keep a worker busy for minutes.
        // Settable for tests.
        internal static long SearchBudgetMs = 10_000;
        private const byte BaseBit = 1;
        private const byte LevelBit = 2;
        private const byte SourceBit = 4;
        private const byte KnownBit = 8;
        private const byte ViewBits = BaseBit | LevelBit | SourceBit;
        private const byte SupersededBit = (byte)LogStore.Superseded;

        public FilterSpec Spec;
        public int Epoch;
        public int MessagesVersion;
        public TextTable Texts;
        /// <summary>The store's first occurrence id when the pass ran: a trim or history put in front needs a new pass.</summary>
        public int FirstId;
        /// <summary>Occurrences with an id below this one are counted.</summary>
        public int Covered;
        public double Milliseconds;

        public int[] Rows = new int[256];
        public int RowCount;

        public int[] GroupCount = new int[64];
        public int[] GroupFirst = new int[64];
        public int[] GroupLast = new int[64];
        public int[] GroupRows = new int[64];
        public int GroupRowCount;
        // GroupRows inverted: a group's row plus one (0: no row), so finding a group's row is an array read.
        private int[] groupRowPlusOne = new int[64];
        public bool GroupOrderDirty;

        public readonly int[] LevelCounts = new int[3];
        public int[] SourceCounts = new int[16];

        public long ChartFrom;
        public long ChartTo;
        /// <summary>The chart starts after the oldest log (a time window): older logs stay out of its buckets and sparklines.</summary>
        public bool ChartClips;
        public int Buckets;
        public int[] Timeline;
        public int[] Sparks;
        public int SparkGroups;
        /// <summary>Highest count of one group in one spark bucket, for scaling sparklines.</summary>
        public int SparkMax = 1;

        private byte[] textMatch = new byte[0];
        private Regex regex;
        private long searchDeadline;
        private bool onEditorThread;
        // Messages from here on may not be tested yet (a pass that ran out of time): the occurrences after the first of
        // theirs wait (Covered stops there) until Extend tests them, a slice at a time.
        private int firstUnknown;
        private int searchTimeouts;
        // What one Extend (on the caller's thread) may spend on testing texts before it leaves the rest for the next one.
        private const double ExtendBudgetMs = 6d;

        // Where the next Extend goes on testing (it stops at its deadline, not only its matching).
        private int extendNext;

        /// <summary>Texts the editor's thread gave up on wait for a worker pass (the window asks for one).</summary>
        public bool WorkerPending { get; private set; }

        /// <summary>Messages are left to test: the list is still filling in (a slice per Extend).</summary>
        public bool SearchPending { get; private set; }

        /// <summary>Texts the search could not decide (the expression took too long on them): left out of the results.</summary>
        public int SearchTimeouts => Volatile.Read(ref searchTimeouts);
        private byte[] messagePass = new byte[0];
        private bool[] groupMuted = new bool[0];
        private int groupsKnown;
        private bool[] sourceHidden = new bool[0];

        public bool HasTimeline => Timeline != null && Buckets > 0;

        /// <summary>
        /// A full pass. Safe on a worker thread: it only reads <paramref name="view"/> and its own arrays.
        /// <paramref name="knownMatches"/> are search results from the previous run with the same query (or null).
        /// </summary>
        public static FilterState Compute(StoreView view, FilterSpec spec, IReadOnlyList<LogSource> sources, byte[] knownMatches,
            long chartFrom, long chartTo, int buckets, Func<bool> cancelled)
        {
            var clock = Stopwatch.StartNew();
            var state = new FilterState
            {
                Spec = spec,
                Epoch = view.Epoch,
                MessagesVersion = view.MessagesVersion,
                Texts = view.Texts,
                FirstId = view.First,
                Covered = view.First,
                ChartFrom = chartFrom,
                ChartTo = Math.Max(chartTo, chartFrom + TimeSpan.TicksPerSecond),
                ChartClips = view.Count > 0 && chartFrom > view.TimeAt(view.First),
                Buckets = Math.Max(0, buckets)
            };

            int textCount = view.Texts.Count;
            state.textMatch = new byte[Math.Max(textCount, 16)];
            if (knownMatches != null)
            {
                Array.Copy(knownMatches, state.textMatch, Math.Min(knownMatches.Length, state.textMatch.Length));
                foreach (byte known in state.textMatch)
                {
                    if (known == LogQuery.TimedOut)
                    {
                        state.searchTimeouts++;
                    }
                }
            }

            state.PrepareSources(sources);
            state.Grow(view.MessageCount, view.GroupCount);
            state.groupsKnown = 0;
            state.UpdateMutes(view);
            state.regex = spec.Query.RegexFor(view.MessageCount);
            state.searchDeadline = spec.Query.UseRegex ? Stopwatch.GetTimestamp() + SearchBudgetMs * Stopwatch.Frequency / 1000 : 0L;
            if (!spec.Query.IsEmpty && cancelled != null && view.MessageCount >= ParallelSearchAbove && !state.SearchInParallel(view, textCount, cancelled))
            {
                return null;
            }

            // A slow expression gets checked for cancellation every message, plain text every 1,024.
            int check = spec.Query.UseRegex ? 0 : 1023;
            for (int m = 0; m < view.MessageCount; m++)
            {
                if ((m & check) == 0 && cancelled != null && cancelled())
                {
                    return null;
                }

                state.messagePass[m] = state.Pass(ref view.Messages[m], textCount);
            }

            if (state.Buckets > 0)
            {
                state.Timeline = new int[3 * state.Buckets];
                if (view.GroupCount <= MaxSparkGroups)
                {
                    state.SparkGroups = view.GroupCount;
                    state.Sparks = new int[Math.Max(1, view.GroupCount) * SparkBuckets];
                }
            }

            state.FindUnknown(view.MessageCount, 0);
            int from = view.First;
            int step = 1 << 18;
            while (from < view.End)
            {
                if (cancelled != null && cancelled())
                {
                    return null;
                }

                // From the distance left: ids near the top of the range would overflow from + step.
                int to = view.End - from <= step ? view.End : from + step;
                int stop = state.Scan(view, from, to);
                from = stop;
                if (stop < to)
                {
                    // An occurrence of a message not tested yet: Extend goes on from here.
                    break;
                }
            }

            state.Covered = from;
            state.searchDeadline = 0L;
            state.SortGroups(view);
            state.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return state;
        }

        /// <summary>Search results by text id, reused by the next run when the query hasn't changed.</summary>
        public byte[] KnownTextMatches => textMatch;

        /// <summary>Adds what the store received since the last pass (main thread). False when a full pass is needed.</summary>
        public bool Extend(LogStore store, IReadOnlyList<LogSource> sources)
        {
            if (store.Epoch != Epoch || store.MessagesVersion != MessagesVersion || !ReferenceEquals(store.Texts, Texts) || store.FirstId != FirstId)
            {
                return false;
            }

            var view = store.View();
            if (view.End == Covered && firstUnknown >= view.MessageCount)
            {
                return true;
            }

            int textCount = view.Texts.Count;
            if (textCount > textMatch.Length)
            {
                Array.Resize(ref textMatch, Math.Max(textCount, textMatch.Length * 2));
            }

            PrepareSources(sources);
            Grow(view.MessageCount, view.GroupCount);
            UpdateMutes(view);
            // Only messages not tested yet, within a budget: a flood of new logs or a slow expression never stalls the
            // caller (the editor's thread); the rest waits for the next Extend.
            var query = Spec.Query;
            var foreground = regex;
            regex = query.ForegroundRegex;
            onEditorThread = true;
            searchDeadline = query.IsEmpty ? 0L : Stopwatch.GetTimestamp() + (long)(ExtendBudgetMs * Stopwatch.Frequency / 1000d);
            try
            {
                int m = extendNext > firstUnknown && extendNext < view.MessageCount ? extendNext : firstUnknown;
                for (; m < view.MessageCount; m++)
                {
                    if (searchDeadline != 0L && Stopwatch.GetTimestamp() > searchDeadline)
                    {
                        break;
                    }

                    if ((messagePass[m] & KnownBit) == 0)
                    {
                        messagePass[m] = Pass(ref view.Messages[m], textCount);
                    }
                }

                // Out of time: the next call goes on from here; through to the end: from the first untested again.
                extendNext = m < view.MessageCount ? m : 0;
            }
            finally
            {
                regex = foreground;
                searchDeadline = 0L;
                onEditorThread = false;
            }

            FindUnknown(view.MessageCount, firstUnknown);

            if (Sparks != null && view.GroupCount > SparkGroups)
            {
                if (view.GroupCount > MaxSparkGroups)
                {
                    Sparks = null;
                    SparkGroups = 0;
                }
                else
                {
                    Array.Resize(ref Sparks, view.GroupCount * SparkBuckets);
                    SparkGroups = view.GroupCount;
                }
            }

            if (Covered < view.End)
            {
                Covered = Scan(view, Covered, view.End);
            }

            return true;
        }

        private void PrepareSources(IReadOnlyList<LogSource> sources)
        {
            if (sourceHidden.Length < sources.Count)
            {
                sourceHidden = new bool[sources.Count + 8];
            }

            if (SourceCounts.Length < sourceHidden.Length)
            {
                Array.Resize(ref SourceCounts, sourceHidden.Length);
            }

            for (int i = 0; i < sources.Count; i++)
            {
                sourceHidden[i] = Spec.HiddenSources.Count > 0 && Spec.HiddenSources.Contains(sources[i].Key);
            }
        }

        private void Grow(int messageCount, int groupCount)
        {
            if (messagePass.Length < messageCount)
            {
                Array.Resize(ref messagePass, Math.Max(messageCount, messagePass.Length * 2) + 16);
            }

            if (GroupCount.Length < groupCount)
            {
                int size = Math.Max(groupCount, GroupCount.Length * 2) + 16;
                Array.Resize(ref GroupCount, size);
                Array.Resize(ref GroupFirst, size);
                Array.Resize(ref GroupLast, size);
                Array.Resize(ref groupMuted, size);
                Array.Resize(ref groupRowPlusOne, size);
            }

            if (groupMuted.Length < GroupCount.Length)
            {
                Array.Resize(ref groupMuted, GroupCount.Length);
            }
        }

        private void UpdateMutes(StoreView view)
        {
            for (int g = groupsKnown; g < view.GroupCount; g++)
            {
                groupMuted[g] = Spec.Muted.Count > 0 && Spec.Muted.Contains(view.Texts[view.Groups[g].Key]);
            }

            groupsKnown = view.GroupCount;
        }

        private byte Pass(ref LogMessage message, int textCount)
        {
            if (message.Resolved || message.Group < groupMuted.Length && groupMuted[message.Group])
            {
                return KnownBit;
            }

            var query = Spec.Query;
            if (!query.IsEmpty)
            {
                byte found = TextMatches(message.Text, query, textCount);
                if (found == Miss && query.IncludeStack && message.Stack > 0)
                {
                    found = TextMatches(message.Stack, query, textCount);
                }

                if (found == Untested)
                {
                    // Out of time: not a miss, tested by a later Extend.
                    return 0;
                }

                if (found != Hit)
                {
                    return KnownBit;
                }
            }

            byte bits = KnownBit | BaseBit;
            if ((Spec.LevelMask & (1 << (int)message.Level)) != 0)
            {
                bits |= LevelBit;
            }

            if (message.Source >= sourceHidden.Length || !sourceHidden[message.Source])
            {
                bits |= SourceBit;
            }

            return bits;
        }

        private const byte Untested = 0;
        private const byte Miss = 1;
        private const byte Hit = 2;

        // Hit, Miss, or Untested when the pass's time is up (then nothing is remembered).
        private byte TextMatches(int text, LogQuery query, int textCount)
        {
            if (text <= 0 || text >= textCount)
            {
                return Miss;
            }

            byte known = textMatch[text];
            if (known == LogQuery.ForWorker)
            {
                // Left for a worker pass by an earlier Extend: not retried on the editor's thread.
                return Untested;
            }

            if (known == Untested)
            {
                if (searchDeadline != 0L && Stopwatch.GetTimestamp() > searchDeadline)
                {
                    return Untested;
                }

                known = query.TryMatch(Texts.Plain(text), regex, out bool timedOut) ? Hit : Miss;
                if (timedOut && onEditorThread)
                {
                    // The short editor-thread timeout: the worker's compiled expression may well finish it.
                    textMatch[text] = LogQuery.ForWorker;
                    WorkerPending = true;
                    return Untested;
                }

                if (timedOut)
                {
                    // A worker gave up too: left out of the results, and said so in the status.
                    known = LogQuery.TimedOut;
                    Interlocked.Increment(ref searchTimeouts);
                }

                textMatch[text] = known;
            }

            return known == LogQuery.TimedOut ? Miss : known;
        }

        // The first message from `from` on not tested yet.
        private void FindUnknown(int messageCount, int from)
        {
            int m = Math.Max(0, from);
            while (m < messageCount && (messagePass[m] & KnownBit) != 0)
            {
                m++;
            }

            firstUnknown = m;
            SearchPending = m < messageCount;
        }

        // Fills the search results of every message's texts on several cores; the serial pass then only reads them.
        // Each text id has its own byte, so threads never share a write (two messages with one text write the same value).
        private bool SearchInParallel(StoreView view, int textCount, Func<bool> cancelled)
        {
            var query = Spec.Query;
            var messages = view.Messages;
            int chunk = 4096;
            int chunks = (view.MessageCount + chunk - 1) / chunk;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
            var result = Parallel.For(0, chunks, options, (c, loop) =>
            {
                int end = Math.Min(view.MessageCount, (c + 1) * chunk);
                for (int m = c * chunk; m < end; m++)
                {
                    if ((query.UseRegex || (m & 63) == 0) && (loop.ShouldExitCurrentIteration || cancelled()))
                    {
                        loop.Stop();
                        return;
                    }

                    ref var message = ref messages[m];
                    if (message.Resolved || message.Group < groupMuted.Length && groupMuted[message.Group])
                    {
                        continue;
                    }

                    if (TextMatches(message.Text, query, textCount) == Miss && query.IncludeStack && message.Stack > 0)
                    {
                        TextMatches(message.Stack, query, textCount);
                    }
                }
            });
            return result.IsCompleted && !cancelled();
        }

        // Adds occurrences from `from`; returns where it stopped: `to`, or the first occurrence of a message not tested yet.
        private int Scan(StoreView view, int from, int to)
        {
            int id = from;
            while (id < to)
            {
                int run = view.Run(id, to, out int chunk, out int offset);
                int stop = ScanRun(view, view.TimeChunks[chunk], view.OwnerChunks[chunk], view.FlagChunks[chunk], id - offset, offset, offset + run);
                if (stop < offset + run)
                {
                    to = id - offset + stop;
                    break;
                }

                id += run;
            }

            if (Spec.Sort != GroupSort.FirstSeen && to > from)
            {
                GroupOrderDirty = true;
            }

            return to;
        }

        // The slots [from, to) of one chunk, whose slot 0 is occurrence idBase; returns the slot it stopped at.
        private int ScanRun(StoreView view, long[] times, int[] owners, byte[] flags, int idBase, int from, int to)
        {
            var messages = view.Messages;
            long brushFrom = Spec.HasRange ? Spec.From : long.MinValue;
            long brushTo = Spec.HasRange ? Spec.To : long.MaxValue;
            bool chart = Timeline != null;
            long chartFrom = ChartFrom;
            long span = Math.Max(1L, ChartTo - ChartFrom);
            int buckets = Buckets;
            var sparks = Sparks;
            int sparkGroups = SparkGroups;
            for (int i = from; i < to; i++)
            {
                byte occurrenceFlags = flags[i];
                if ((occurrenceFlags & SupersededBit) != 0)
                {
                    continue;
                }

                int m = owners[i];
                byte pass = messagePass[m];
                if ((pass & KnownBit) == 0)
                {
                    to = i;
                    break;
                }

                if ((pass & BaseBit) == 0)
                {
                    continue;
                }

                long time = times[i];
                bool visible = (pass & ViewBits) == ViewBits;
                ref var message = ref messages[m];
                int level = (int)message.Level;
                if (visible && chart && (!ChartClips || time >= chartFrom))
                {
                    long offset = time - chartFrom;
                    int bucket = offset <= 0 ? 0 : offset >= span ? buckets - 1 : (int)(offset * buckets / span);
                    Timeline[level * buckets + bucket]++;
                    if (sparks != null && message.Group < sparkGroups)
                    {
                        int spark = offset <= 0 ? 0 : offset >= span ? SparkBuckets - 1 : (int)(offset * SparkBuckets / span);
                        int value = ++sparks[message.Group * SparkBuckets + spark];
                        if (value > SparkMax)
                        {
                            SparkMax = value;
                        }
                    }
                }

                if (time < brushFrom || time > brushTo)
                {
                    continue;
                }

                if ((pass & SourceBit) != 0)
                {
                    LevelCounts[level]++;
                }

                if ((pass & LevelBit) != 0)
                {
                    SourceCounts[message.Source]++;
                }

                if (!visible)
                {
                    continue;
                }

                if (RowCount == Rows.Length)
                {
                    Array.Resize(ref Rows, Rows.Length * 2);
                }

                int id = idBase + i;
                Rows[RowCount++] = id;
                int group = message.Group;
                if (GroupCount[group]++ == 0)
                {
                    GroupFirst[group] = id;
                    if (GroupRowCount == GroupRows.Length)
                    {
                        Array.Resize(ref GroupRows, GroupRows.Length * 2);
                    }

                    GroupRows[GroupRowCount++] = group;
                    groupRowPlusOne[group] = GroupRowCount;
                }
                else if (Spec.Sort != GroupSort.FirstSeen)
                {
                    GroupOrderDirty = true;
                }

                GroupLast[group] = id;
            }

            return to;
        }

        /// <summary>Puts group rows in the chosen order (first seen needs nothing: groups arrive in that order).</summary>
        public void SortGroups(StoreView view)
        {
            GroupOrderDirty = false;
            if (Spec.Sort == GroupSort.FirstSeen || GroupRowCount < 2)
            {
                return;
            }

            var counts = GroupCount;
            var last = GroupLast;
            var first = GroupFirst;
            var groups = view.Groups;
            Comparison<int> comparison;
            switch (Spec.Sort)
            {
                case GroupSort.LastSeen:
                    comparison = (a, b) => last[a].CompareTo(last[b]);
                    break;
                case GroupSort.Count:
                    comparison = (a, b) =>
                    {
                        int byCount = counts[b].CompareTo(counts[a]);
                        return byCount != 0 ? byCount : first[a].CompareTo(first[b]);
                    };
                    break;
                default:
                    comparison = (a, b) =>
                    {
                        int byLevel = ((int)groups[b].Level).CompareTo((int)groups[a].Level);
                        if (byLevel != 0)
                        {
                            return byLevel;
                        }

                        int byCount = counts[b].CompareTo(counts[a]);
                        return byCount != 0 ? byCount : first[a].CompareTo(first[b]);
                    };
                    break;
            }

            Array.Sort(GroupRows, 0, GroupRowCount, Comparer<int>.Create(comparison));
            for (int i = 0; i < GroupRowCount; i++)
            {
                groupRowPlusOne[GroupRows[i]] = i + 1;
            }
        }

        /// <summary>
        /// The chart and sparklines of this state for another time axis, computed from what the state already knows
        /// (which messages show): only the occurrences' times are read again, nothing is allocated but the count arrays,
        /// and rows, groups and search results stay. Safe on a worker thread (see <see cref="AdoptChart"/>).
        /// </summary>
        public ChartPass ComputeChart(StoreView view, long chartFrom, long chartTo, int buckets, int[] spareSparks, Func<bool> cancelled)
        {
            var pass = new ChartPass
            {
                Epoch = view.Epoch,
                MessagesVersion = view.MessagesVersion,
                Texts = view.Texts,
                FirstId = view.First,
                From = chartFrom,
                To = Math.Max(chartTo, chartFrom + TimeSpan.TicksPerSecond),
                Clips = view.Count > 0 && chartFrom > view.TimeAt(view.First),
                Buckets = Math.Max(1, buckets),
                Covered = Math.Min(view.End, Covered)
            };
            pass.Timeline = new int[3 * pass.Buckets];
            if (Sparks != null)
            {
                pass.SparkGroups = SparkGroups;
                int size = Math.Max(1, SparkGroups) * SparkBuckets;
                pass.Sparks = spareSparks != null && spareSparks.Length == size ? spareSparks : new int[size];
                Array.Clear(pass.Sparks, 0, pass.Sparks.Length);
            }

            var passes = messagePass;
            for (int from = view.First; from < pass.Covered;)
            {
                if (cancelled != null && cancelled())
                {
                    return null;
                }

                // From the distance left: ids near the top of the range would overflow from + step.
                int to = pass.Covered - from <= 1 << 18 ? pass.Covered : from + (1 << 18);
                ChartScan(view, passes, pass, from, to);
                from = to;
            }

            return pass;
        }

        /// <summary>
        /// Takes a chart computed by <see cref="ComputeChart"/> (main thread), adding the occurrences that arrived since.
        /// False when the store changed shape meanwhile: the pass is dropped. The old sparkline array is handed back for
        /// the next pass.
        /// </summary>
        public bool AdoptChart(ChartPass pass, StoreView view, out int[] spareSparks)
        {
            spareSparks = null;
            if (pass == null || pass.Epoch != Epoch || pass.MessagesVersion != MessagesVersion || !ReferenceEquals(pass.Texts, Texts) ||
                pass.FirstId != FirstId || view.Epoch != Epoch || view.First != FirstId || pass.Covered > Covered || Timeline == null)
            {
                return false;
            }

            if (Sparks == null)
            {
                pass.Sparks = null;
                pass.SparkGroups = 0;
            }
            else if (pass.Sparks == null || pass.SparkGroups < SparkGroups)
            {
                // Groups that appeared since: their sparklines start empty, as Extend makes them.
                Array.Resize(ref pass.Sparks, Math.Max(1, SparkGroups) * SparkBuckets);
                pass.SparkGroups = SparkGroups;
            }

            ChartScan(view, messagePass, pass, pass.Covered, Covered);
            spareSparks = Sparks;
            ChartFrom = pass.From;
            ChartTo = pass.To;
            ChartClips = pass.Clips;
            Buckets = pass.Buckets;
            Timeline = pass.Timeline;
            Sparks = pass.Sparks;
            SparkGroups = pass.SparkGroups;
            SparkMax = pass.SparkMax;
            return true;
        }

        // The chart half of Scan, by the same rules.
        private static void ChartScan(StoreView view, byte[] passes, ChartPass pass, int from, int to)
        {
            for (int id = from; id < to;)
            {
                int run = view.Run(id, to, out int chunk, out int offset);
                ChartRun(view, view.TimeChunks[chunk], view.OwnerChunks[chunk], view.FlagChunks[chunk], passes, pass, offset, offset + run);
                id += run;
            }
        }

        private static void ChartRun(StoreView view, long[] times, int[] owners, byte[] flags, byte[] passes, ChartPass pass, int from, int to)
        {
            var messages = view.Messages;
            long chartFrom = pass.From;
            long span = Math.Max(1L, pass.To - pass.From);
            int buckets = pass.Buckets;
            var timeline = pass.Timeline;
            var sparks = pass.Sparks;
            int sparkGroups = pass.SparkGroups;
            for (int i = from; i < to; i++)
            {
                if ((flags[i] & SupersededBit) != 0)
                {
                    continue;
                }

                int m = owners[i];
                if (m >= passes.Length || (passes[m] & ViewBits) != ViewBits)
                {
                    continue;
                }

                long time = times[i];
                if (pass.Clips && time < chartFrom)
                {
                    continue;
                }

                ref var message = ref messages[m];
                long offset = time - chartFrom;
                int bucket = offset <= 0 ? 0 : offset >= span ? buckets - 1 : (int)(offset * buckets / span);
                timeline[(int)message.Level * buckets + bucket]++;
                if (sparks != null && message.Group < sparkGroups)
                {
                    int spark = offset <= 0 ? 0 : offset >= span ? SparkBuckets - 1 : (int)(offset * SparkBuckets / span);
                    int value = ++sparks[message.Group * SparkBuckets + spark];
                    if (value > pass.SparkMax)
                    {
                        pass.SparkMax = value;
                    }
                }
            }
        }

        /// <summary>The row position of an occurrence (binary search: rows are in time order), or -1.</summary>
        public int RowOf(int occurrence)
        {
            int index = Array.BinarySearch(Rows, 0, RowCount, occurrence);
            return index >= 0 ? index : -1;
        }

        /// <summary>The first row at or after <paramref name="occurrence"/>.</summary>
        public int RowAtOrAfter(int occurrence)
        {
            int index = Array.BinarySearch(Rows, 0, RowCount, occurrence);
            return index >= 0 ? index : ~index;
        }

        public int GroupRowOf(int group) => group >= 0 && group < groupRowPlusOne.Length ? groupRowPlusOne[group] - 1 : -1;
    }

    /// <summary>A <see cref="FilterState"/>'s chart for a moved time axis (<see cref="FilterState.ComputeChart"/>).</summary>
    internal sealed class ChartPass
    {
        public int Epoch;
        public int MessagesVersion;
        public TextTable Texts;
        public int FirstId;
        public long From;
        public long To;
        public bool Clips;
        public int Buckets;
        public int Covered;
        public int[] Timeline;
        public int[] Sparks;
        public int SparkGroups;
        public int SparkMax = 1;
    }

    /// <summary>
    /// Runs filter passes: on the main thread when the store is small, on a worker thread otherwise, so typing in the
    /// search field never waits for a million logs. Only the newest request's result is kept.
    /// </summary>
    internal sealed class FilterRunner
    {
        private const int SynchronousLimit = 150_000;
        private int generation;
        private volatile FilterState ready;
        private int readyGeneration;
        private volatile bool running;

        public bool Busy => running;

        /// <summary>Starts a pass. The result comes from <see cref="TryTake"/> (or right away when computed in place).</summary>
        public FilterState Request(LogStore store, FilterSpec spec, FilterState previous, long chartFrom, long chartTo, int buckets, bool allowAsync)
        {
            int id = Interlocked.Increment(ref generation);
            Interlocked.Increment(ref chartGeneration);
            readyChart = null;
            var view = store.View();
            var sources = new List<LogSource>(store.Sources.All);
            // What the previous search found still holds for the same query, and in part for one typed on or back.
            byte[] known = previous != null && previous.Spec != null && ReferenceEquals(previous.Texts, store.Texts)
                ? spec.Query.Reuse(previous.Spec.Query, previous.KnownTextMatches)
                : null;
            long work = view.Count + (spec.Query.IsEmpty ? 0 : (spec.Query.IncludeStack ? 40L : 4L) * view.MessageCount);
            // A regular expression can be slow on any store: never on the editor's thread unless the caller needs it there.
            if (!allowAsync || work <= SynchronousLimit && !spec.Query.UseRegex)
            {
                running = false;
                ready = null;
                return FilterState.Compute(view, spec, sources, known, chartFrom, chartTo, buckets, null);
            }

            running = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var result = FilterState.Compute(view, spec, sources, known, chartFrom, chartTo, buckets, () => Volatile.Read(ref generation) != id);
                    if (result != null && Volatile.Read(ref generation) == id)
                    {
                        readyGeneration = id;
                        ready = result;
                    }
                }
                catch (Exception)
                {
                    // The main thread recomputes on its next request.
                }
                finally
                {
                    if (Volatile.Read(ref generation) == id)
                    {
                        running = false;
                    }
                }
            });
            return null;
        }

        private int chartGeneration;
        private volatile ChartPass readyChart;
        private int readyChartGeneration;
        private volatile bool charting;
        private int[] spareSparks;

        /// <summary>
        /// Moves the chart of <paramref name="state"/> to a new time axis without a filter pass. The result comes from
        /// <see cref="TryTakeChart"/>; a full <see cref="Request"/> drops it.
        /// </summary>
        public void RequestChart(LogStore store, FilterState state, long chartFrom, long chartTo, int buckets)
        {
            if (state == null || charting)
            {
                return;
            }

            int id = Interlocked.Increment(ref chartGeneration);
            int full = Volatile.Read(ref generation);
            var view = store.View();
            var spare = spareSparks;
            spareSparks = null;
            if (view.Count <= SynchronousLimit)
            {
                readyChartGeneration = id;
                readyChart = state.ComputeChart(view, chartFrom, chartTo, buckets, spare, null);
                return;
            }

            charting = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var result = state.ComputeChart(view, chartFrom, chartTo, buckets, spare,
                        () => Volatile.Read(ref chartGeneration) != id || Volatile.Read(ref generation) != full);
                    if (result != null && Volatile.Read(ref chartGeneration) == id)
                    {
                        readyChartGeneration = id;
                        readyChart = result;
                    }
                }
                catch (Exception)
                {
                    // The next request tries again.
                }
                finally
                {
                    charting = false;
                }
            });
        }

        /// <summary>Puts the latest chart into <paramref name="state"/> (main thread). True when it changed.</summary>
        public bool TryTakeChart(LogStore store, FilterState state)
        {
            var result = readyChart;
            if (result == null || readyChartGeneration != Volatile.Read(ref chartGeneration))
            {
                return false;
            }

            readyChart = null;
            if (state == null || !state.AdoptChart(result, store.View(), out int[] spare))
            {
                return false;
            }

            spareSparks = spare;
            return true;
        }

        /// <summary>The finished result of the latest request, once.</summary>
        public FilterState TryTake()
        {
            var result = ready;
            if (result == null || readyGeneration != Volatile.Read(ref generation))
            {
                return null;
            }

            ready = null;
            return result;
        }

        /// <summary>Drops a pending request (scripts are about to reload, the window closes).</summary>
        public void Cancel()
        {
            Interlocked.Increment(ref generation);
            Interlocked.Increment(ref chartGeneration);
            readyChart = null;
            ready = null;
            running = false;
        }
    }
}
