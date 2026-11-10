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
            if (store == null || occurrence < 0 || occurrence >= store.Count || (store.FlagsAt(occurrence) & LogStore.Superseded) != 0)
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
        public int Covered;
        public double Milliseconds;

        public int[] Rows = new int[256];
        public int RowCount;

        public int[] GroupCount = new int[64];
        public int[] GroupFirst = new int[64];
        public int[] GroupLast = new int[64];
        public int[] GroupRows = new int[64];
        public int GroupRowCount;
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
                ChartFrom = chartFrom,
                ChartTo = Math.Max(chartTo, chartFrom + TimeSpan.TicksPerSecond),
                ChartClips = view.Count > 0 && chartFrom > view.Times[0],
                Buckets = Math.Max(0, buckets)
            };

            int textCount = view.Texts.Count;
            state.textMatch = new byte[Math.Max(textCount, 16)];
            if (knownMatches != null)
            {
                Array.Copy(knownMatches, state.textMatch, Math.Min(knownMatches.Length, state.textMatch.Length));
            }

            state.PrepareSources(sources);
            state.Grow(view.MessageCount, view.GroupCount);
            state.groupsKnown = 0;
            state.UpdateMutes(view);
            state.regex = spec.Query.RegexFor(view.MessageCount);
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

            int from = 0;
            int step = 1 << 18;
            while (from < view.Count)
            {
                if (cancelled != null && cancelled())
                {
                    return null;
                }

                int to = Math.Min(view.Count, from + step);
                state.Scan(view, from, to);
                from = to;
            }

            state.Covered = view.Count;
            state.SortGroups(view);
            state.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return state;
        }

        /// <summary>Search results by text id, reused by the next run when the query hasn't changed.</summary>
        public byte[] KnownTextMatches => textMatch;

        /// <summary>Adds what the store received since the last pass (main thread). False when a full pass is needed.</summary>
        public bool Extend(LogStore store, IReadOnlyList<LogSource> sources)
        {
            if (store.Epoch != Epoch || store.MessagesVersion != MessagesVersion || !ReferenceEquals(store.Texts, Texts))
            {
                return false;
            }

            var view = store.View();
            if (view.Count == Covered && view.MessageCount <= messagePass.Length && (messagePass.Length == 0 || view.MessageCount == 0 || (messagePass[view.MessageCount - 1] & KnownBit) != 0))
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
            for (int m = 0; m < view.MessageCount; m++)
            {
                if ((messagePass[m] & KnownBit) == 0)
                {
                    messagePass[m] = Pass(ref view.Messages[m], textCount);
                }
            }

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

            if (Covered < view.Count)
            {
                Scan(view, Covered, view.Count);
                Covered = view.Count;
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
                bool found = TextMatches(message.Text, query, textCount);
                if (!found && query.IncludeStack && message.Stack > 0)
                {
                    found = TextMatches(message.Stack, query, textCount);
                }

                if (!found)
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

        private bool TextMatches(int text, LogQuery query, int textCount)
        {
            if (text <= 0 || text >= textCount)
            {
                return false;
            }

            byte known = textMatch[text];
            if (known == 0)
            {
                known = query.Matches(Texts.Plain(text), regex) ? (byte)2 : (byte)1;
                textMatch[text] = known;
            }

            return known == 2;
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

                    if (!TextMatches(message.Text, query, textCount) && query.IncludeStack && message.Stack > 0)
                    {
                        TextMatches(message.Stack, query, textCount);
                    }
                }
            });
            return result.IsCompleted && !cancelled();
        }

        private void Scan(StoreView view, int from, int to)
        {
            var times = view.Times;
            var owners = view.Owners;
            var flags = view.Flags;
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

                Rows[RowCount++] = i;
                int group = message.Group;
                if (GroupCount[group]++ == 0)
                {
                    GroupFirst[group] = i;
                    if (GroupRowCount == GroupRows.Length)
                    {
                        Array.Resize(ref GroupRows, GroupRows.Length * 2);
                    }

                    GroupRows[GroupRowCount++] = group;
                }
                else if (Spec.Sort != GroupSort.FirstSeen)
                {
                    GroupOrderDirty = true;
                }

                GroupLast[group] = i;
            }

            if (Spec.Sort != GroupSort.FirstSeen && to > from)
            {
                GroupOrderDirty = true;
            }
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
                From = chartFrom,
                To = Math.Max(chartTo, chartFrom + TimeSpan.TicksPerSecond),
                Clips = view.Count > 0 && chartFrom > view.Times[0],
                Buckets = Math.Max(1, buckets),
                Covered = Math.Min(view.Count, Covered)
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
            for (int from = 0; from < pass.Covered; from += 1 << 18)
            {
                if (cancelled != null && cancelled())
                {
                    return null;
                }

                ChartScan(view, passes, pass, from, Math.Min(pass.Covered, from + (1 << 18)));
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
                view.Epoch != Epoch || pass.Covered > Covered || Timeline == null)
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
            var times = view.Times;
            var owners = view.Owners;
            var flags = view.Flags;
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

        public int GroupRowOf(int group)
        {
            for (int i = 0; i < GroupRowCount; i++)
            {
                if (GroupRows[i] == group)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>A <see cref="FilterState"/>'s chart for a moved time axis (<see cref="FilterState.ComputeChart"/>).</summary>
    internal sealed class ChartPass
    {
        public int Epoch;
        public int MessagesVersion;
        public TextTable Texts;
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
            if (!allowAsync || work <= SynchronousLimit)
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
