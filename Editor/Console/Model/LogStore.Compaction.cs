using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LogStore
    {
        // What landing a compaction may do on the editor's thread, at most: count this many late occurrences, copy this
        // many characters of their texts, apply this many late patches, and spend this long. More than any of these and a
        // worker catches up first (the landing is tried again at the next update).
        internal const int LandingLimit = 4096;
        internal const int LandingChars = 256 * 1024;
        internal const int LandingPatches = 256;
        internal const double LandingMilliseconds = 3d;

        /// <summary>
        /// The most characters of text the store ever holds outside an import: the budget plus a quarter. A compaction in
        /// the background is an optimisation the budget never depends on: past this, the oldest logs go and what they
        /// leave dead is compacted at once, on the editor's thread (the synchronous path), whatever runs in the background,
        /// imports included (they hold nothing across updates).
        /// Compactions share the store's strings (<see cref="TextTable.InternCopy"/>) and a cancelled one lets go of its
        /// tables at its next occurrence, so the store's own table is what holds the characters.
        /// </summary>
        internal long TextCeiling => maxTextChars + maxTextChars / 4;

        /// <summary>Tests: compactions cancelled before they landed, and times the ceiling compacted synchronously.</summary>
        internal int CompactionsCancelled { get; private set; }

        /// <summary>Tests: characters of texts the last bounded landing attempt copied on the editor's thread.</summary>
        internal long LastLandingChars { get; private set; }
        internal int CeilingCompactions { get; private set; }

        // While the ceiling's synchronous path runs: compactions land in the call that starts them.
        private bool compactNow;

        /// <summary>
        /// Messages none of whose logs are left (trimmed), and the texts only they used, go once they are many (or the
        /// texts are over budget): a stream of logs that never repeat would otherwise keep every text ever seen. Message,
        /// group and text ids change (new tables: a filter pass still reading the old ones is unaffected); occurrence ids
        /// stay. In the background the new tables are prepared on a worker and land in <see cref="StepMaintenance"/>.
        /// </summary>
        private void MaybeCompact()
        {
            if (!AllowCompaction || compaction != null || Texts.Faulted)
            {
                // A text could not be read back, and copying it would keep its placeholder as if it were the log
                // (LogCapture starts over instead).
                return;
            }

            int dead = zeroCount;
            // Many dead, or texts over budget: then any dead message's texts are worth reclaiming.
            if (dead == 0 || Texts.TotalChars <= maxTextChars && (dead < 1024 || dead * 4 < messageCount))
            {
                return;
            }

            compaction = new StoreCompaction(this);
            if (CompactInBackground && !compactNow)
            {
                compaction.Start();
                return;
            }

            compaction.CatchUp();
            TryLand(bounded: false);
        }

        // The ceiling: cancels what runs in the background and keeps the budget the way a store without workers does,
        // now (trims and synchronous compactions).
        private void EnforceCeiling()
        {
            if (Texts.TotalChars <= TextCeiling || Texts.Faulted || compactNow)
            {
                return;
            }

            CancelCompaction();
            CeilingCompactions++;
            compactNow = true;
            try
            {
                KeepTextBudget();
            }
            finally
            {
                compactNow = false;
            }
        }

        /// <summary>
        /// Main thread, each editor update: lands a compaction prepared in the background once what arrived meanwhile is
        /// little (see <see cref="LandingLimit"/> and the other bounds), else lets the worker catch up again. A field read
        /// when there is none.
        /// </summary>
        public void StepMaintenance()
        {
            var running = compaction;
            if (running == null || running.Busy)
            {
                return;
            }

            if (running.Failed)
            {
                // A bug or a damaged text: dropped; the next trim tries again (a damaged table is never compacted).
                compaction = null;
                return;
            }

            TryLand(bounded: true);
        }

        /// <summary>
        /// Tests: waits for the worker and lands through <see cref="StepMaintenance"/>, the production path with its
        /// bounds, as many updates as it takes.
        /// </summary>
        internal void FinishMaintenance()
        {
            while (compaction != null)
            {
                compaction.Wait();
                StepMaintenance();
            }
        }

        /// <summary>Tests: waits for the worker preparing a compaction, without landing it.</summary>
        internal void WaitForCompactionWorker() => compaction?.Wait();

        /// <summary>Drops a compaction being prepared (scripts are about to reload).</summary>
        internal void StopMaintenance() => CancelCompaction();

        private void CancelCompaction()
        {
            if (compaction != null)
            {
                compaction.Cancel();
                compaction = null;
                CompactionsCancelled++;
            }
        }

        // Swaps the prepared tables in, in one step, once what is left fits the landing's bounds (all of it, unbounded,
        // on the synchronous path): between updates the store is the old one, whole, and the worker's tables are its own.
        private void TryLand(bool bounded)
        {
            var prepared = compaction;
            if (bounded && (end - prepared.Done > LandingLimit || prepared.LatePatches > LandingPatches))
            {
                prepared.Start();
                return;
            }

            // One budget for everything the editor's thread copies here: late occurrences' texts, then late patches. Each
            // adoption and each patch is checked before it is copied; what doesn't fit goes back to a worker, and what was
            // done stays done (a steady trickle still lands).
            long deadline = bounded ? Stopwatch.GetTimestamp() + (long)(LandingMilliseconds * Stopwatch.Frequency / 1000d) : long.MaxValue;
            var budget = new LandingBudget(deadline, bounded ? LandingChars : long.MaxValue);
            bool whole = prepared.CountTo(end, ref budget) && prepared.ApplyPatches(ref budget);
            if (bounded)
            {
                LastLandingChars = budget.Used;
            }

            if (!whole)
            {
                // Out of time or characters (large texts, a slow read from the session file): a worker goes on.
                prepared.Start();
                return;
            }

            if (Texts.Faulted || prepared.Failed || !ReferenceEquals(Texts, prepared.OldTexts))
            {
                // A text read back from the session file for the copy was damaged: the copy holds its placeholder.
                // Keep the old table untouched; LogCapture starts over from Unity's console.
                compaction = null;
                return;
            }

            // Profiles of the timings known when it started, remapped by the worker; one filed since is parsed again from
            // its text the first time it is asked for (TimingOf). The owner directory below is one reference per chunk
            // (65,536 occurrences): no occurrence is copied here.
            profiles = prepared.Profiles();
            Texts = prepared.Texts;
            messages = prepared.Messages;
            messageCount = prepared.MessageCount;
            messageIndex = prepared.MessageIndex;
            groups = prepared.Groups;
            groupCount = prepared.GroupCount;
            groupIndex = prepared.GroupIndex;
            ownerChunks = prepared.OwnerDirectory(ownerChunks.Length);
            zeroCount = 0;
            compaction = null;
            RowsGeneration = NewRowsGeneration();
            MessagesVersion++;
            Epoch++;
            Version++;
            Regrouped?.Invoke();
            if (Texts.TotalChars > maxTextChars)
            {
                // Still over budget: the next oldest fifth goes (and compacts in turn).
                KeepTextBudget();
            }
        }

        // What the editor's thread may still copy in one landing (the worker's is unlimited).
        private struct LandingBudget
        {
            private readonly long deadline;
            private readonly long maxChars;
            public long Used;

            public LandingBudget(long deadline, long maxChars)
            {
                this.deadline = deadline;
                this.maxChars = maxChars;
                Used = 0;
            }

            public static LandingBudget Unlimited => new LandingBudget(long.MaxValue, long.MaxValue);

            // Whether `chars` more fit, the time included; charged when they do.
            public bool Take(long chars)
            {
                if (maxChars != long.MaxValue && Used + chars > maxChars || deadline != long.MaxValue && Stopwatch.GetTimestamp() > deadline)
                {
                    return false;
                }

                Used += chars;
                return true;
            }
        }

        /// <summary>
        /// New message, group and text tables for the occurrences in the store, built from the occurrences themselves (a
        /// message is kept when it has one, in the order of its oldest) and the fields of a message that never change
        /// once it is filed. What changes later (a file Unity's console gave, an assembly, compile flags) is queued as a
        /// patch; the worker applies them, the landing only the few that came since. Occurrences are counted on a worker
        /// up to what was filed when it looked, then the rest on the editor's thread at landing, within its bounds.
        /// Anything that moves occurrences or drops messages (trim, clear, regroup, history, a pin, renumbering) cancels
        /// it; a cancelled worker stops at its next occurrence.
        /// </summary>
        private sealed class StoreCompaction
        {
            private readonly LogStore store;
            private readonly GroupingMode grouping;
            private readonly int chunkBase;
            // Messages changed since it started (any order, repeats possible): drained, deduplicated, then applied.
            private readonly ConcurrentQueue<int> patches = new ConcurrentQueue<int>();
            private readonly HashSet<int> patchBatch = new HashSet<int>();
            // The timings' profiles when it started, and remapped once counted (the worker's own).
            private readonly List<KeyValuePair<int, TimingProfile>> startProfiles;
            // Old message id + 1 -> new message id + 1 (0: not seen yet).
            private int[] map = new int[1024];
            private int[][] owners;
            private volatile bool cancelled;
            private volatile bool busy;
            private volatile bool failed;
            private int done;
            private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);

            public StoreCompaction(LogStore store)
            {
                this.store = store;
                OldTexts = store.Texts;
                grouping = store.Grouping;
                chunkBase = store.chunkBase;
                done = store.first;
                owners = new int[store.ownerChunks.Length][];
                startProfiles = new List<KeyValuePair<int, TimingProfile>>(store.profiles);
                Texts = new TextTable(Math.Max(1024, OldTexts.Count - store.zeroCount));
                Messages = new LogMessage[Math.Max(256, store.messageCount - store.zeroCount)];
                MessageIndex = new Dictionary<MessageKey, int>(Math.Max(16, store.messageCount - store.zeroCount));
                Groups = new LogGroup[Math.Max(256, Math.Min(store.groupCount, store.messageCount))];
                GroupIndex = new Dictionary<long, int>();
            }

            public TextTable OldTexts { get; }
            public TextTable Texts { get; }
            public LogMessage[] Messages;
            public int MessageCount;
            public Dictionary<MessageKey, int> MessageIndex { get; }
            public LogGroup[] Groups;
            public int GroupCount;
            public Dictionary<long, int> GroupIndex { get; }

            /// <summary>Occurrences counted: ids from the first up to this one.</summary>
            public int Done => Volatile.Read(ref done);

            /// <summary>Patches queued and not applied yet.</summary>
            public int LatePatches => patches.Count;

            public bool Busy => busy;
            public bool Failed => failed;

            /// <summary>A worker counts what was filed meanwhile and applies the patches (main thread).</summary>
            public void Start()
            {
                busy = true;
                idle.Reset();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        CatchUp();
                    }
                    catch (Exception)
                    {
                        // The store keeps its old tables; the next trim starts another compaction.
                        failed = true;
                    }
                    finally
                    {
                        busy = false;
                        idle.Set();
                    }
                });
            }

            public void Wait() => idle.Wait();

            public void Cancel() => cancelled = true;

            /// <summary>
            /// Counts what the store filed since and applies the patches, a few rounds while logs keep coming, until little
            /// is left for the editor's thread (any thread: it only reads what the store published before its last
            /// occurrence id, and messages' fields after their patch was queued).
            /// </summary>
            public void CatchUp()
            {
                for (int round = 0; round < 64 && !cancelled; round++)
                {
                    int to = Volatile.Read(ref store.end);
                    var unlimited = LandingBudget.Unlimited;
                    CountTo(to, ref unlimited);
                    ApplyPatches(ref unlimited);
                    if (Volatile.Read(ref store.end) - to <= LandingLimit / 2 && patches.Count <= LandingPatches / 2)
                    {
                        break;
                    }
                }

                RemapProfiles();
            }

            /// <summary>
            /// Counts the occurrences up to <paramref name="to"/>, adopting each message at its first one; false when the
            /// next adoption doesn't fit <paramref name="budget"/> (checked before it copies anything, so a text too large
            /// for what is left goes to the worker) or the time is up. What it counted stays counted.
            /// </summary>
            public bool CountTo(int to, ref LandingBudget budget)
            {
                // Read after the occurrence id: whatever the store published before it is in these.
                var directory = Volatile.Read(ref store.ownerChunks);
                var oldMessages = Volatile.Read(ref store.messages);
                if (owners.Length < directory.Length)
                {
                    Array.Resize(ref owners, directory.Length);
                }

                int id = done;
                while (id < to)
                {
                    int slot = id - chunkBase;
                    int chunk = slot >> ChunkShift;
                    int offset = slot & ChunkMask;
                    int run = Math.Min(to - id, ChunkSize - offset);
                    var column = directory[chunk];
                    var remapped = owners[chunk] ?? (owners[chunk] = new int[ChunkSize]);
                    for (int i = offset; i < offset + run; i++, id++)
                    {
                        if (cancelled)
                        {
                            failed = true;
                            Volatile.Write(ref done, id);
                            return false;
                        }

                        int old = column[i];
                        int message = NewId(old);
                        if (message < 0)
                        {
                            if (!budget.Take(CharsOf(oldMessages[old])))
                            {
                                Volatile.Write(ref done, id);
                                return false;
                            }

                            message = Adopt(old, oldMessages);
                        }
                        else if (!budget.Take(0))
                        {
                            Volatile.Write(ref done, id);
                            return false;
                        }

                        remapped[i] = message;
                        Count(message, id);
                    }

                    Volatile.Write(ref done, id);
                }

                return true;
            }

            // What adopting a message may copy: its texts' lengths (read without loading them).
            private long CharsOf(in LogMessage message) =>
                (long)OldTexts.LengthOf(message.Text) + OldTexts.LengthOf(message.Stack) + OldTexts.LengthOf(message.Callsite) +
                OldTexts.LengthOf(message.File) + OldTexts.LengthOf(message.Assembly);

            /// <summary>The new id of an old message, -1 when it had no occurrence (it goes).</summary>
            public int NewId(int old) => old >= 0 && old < map.Length ? map[old] - 1 : -1;

            /// <summary>A message's later change (main thread, after the change is written).</summary>
            public void Patched(int old) => patches.Enqueue(old);

            /// <summary>
            /// Copies what changed in patched messages since they were adopted, each once, within <paramref name="budget"/>
            /// (checked before each); false when one didn't fit: it and the rest wait in the queue. A message not adopted
            /// yet needs none: it is adopted from its current fields.
            /// </summary>
            public bool ApplyPatches(ref LandingBudget budget)
            {
                patchBatch.Clear();
                var order = new List<int>();
                while (patches.TryDequeue(out int queued))
                {
                    if (patchBatch.Add(queued))
                    {
                        order.Add(queued);
                    }
                }

                var current = Volatile.Read(ref store.messages);
                for (int k = 0; k < order.Count; k++)
                {
                    int old = order[k];
                    int moved = NewId(old);
                    if (moved < 0 || old >= current.Length)
                    {
                        continue;
                    }

                    var source = current[old];
                    long chars = (long)OldTexts.LengthOf(source.File) + OldTexts.LengthOf(source.Callsite) + OldTexts.LengthOf(source.Assembly);
                    if (!budget.Take(chars))
                    {
                        for (int rest = k; rest < order.Count; rest++)
                        {
                            patches.Enqueue(order[rest]);
                        }

                        return false;
                    }

                    ref var target = ref Messages[moved];
                    target.Flags = source.Flags;
                    target.Line = source.Line;
                    target.File = Copy(source.File);
                    target.Callsite = Copy(source.Callsite);
                    target.Assembly = Copy(source.Assembly);
                }

                return true;
            }

            /// <summary>The timings' profiles under their new message ids.</summary>
            public Dictionary<int, TimingProfile> Profiles() => remappedProfiles ?? new Dictionary<int, TimingProfile>();

            private Dictionary<int, TimingProfile> remappedProfiles;

            private void RemapProfiles()
            {
                var remapped = new Dictionary<int, TimingProfile>(startProfiles.Count);
                foreach (var pair in startProfiles)
                {
                    int moved = NewId(pair.Key);
                    if (moved >= 0)
                    {
                        remapped[moved] = pair.Value;
                    }
                }

                remappedProfiles = remapped;
            }

            /// <summary>The owner chunks with the new message ids, in a directory of <paramref name="size"/>.</summary>
            public int[][] OwnerDirectory(int size)
            {
                var directory = new int[Math.Max(size, owners.Length)][];
                Array.Copy(owners, directory, owners.Length);
                return directory;
            }

            private int Adopt(int old, LogMessage[] oldMessages)
            {
                if (old >= map.Length)
                {
                    Array.Resize(ref map, Math.Max(old + 1, map.Length * 2));
                }

                int moved;

                // A copy: the store changes the counters of its message meanwhile, never its text ids or its type.
                var message = oldMessages[old];
                message.Text = Copy(message.Text);
                message.Stack = Copy(message.Stack);
                message.Callsite = Copy(message.Callsite);
                message.File = Copy(message.File);
                message.Assembly = Copy(message.Assembly);
                message.Count = 0;
                message.First = message.Last = -1;
                message.Group = GroupFor(Texts, grouping, ref Groups, ref GroupCount, GroupIndex, message.Text, message.Level);
                Groups[message.Group].Variants++;
                if (MessageCount == Messages.Length)
                {
                    Array.Resize(ref Messages, Messages.Length * 2);
                }

                moved = MessageCount++;
                Messages[moved] = message;
                MessageIndex[new MessageKey(message.Text, message.Stack, message.Variant)] = moved;
                map[old] = moved + 1;
                return moved;
            }

            // The same strings as the store's table (no new characters held).
            private int Copy(int text) => text <= 0 ? 0 : Texts.InternCopy(OldTexts, text);

            // As LogStore.CountIn, in the new tables (occurrences come in id order).
            private void Count(int message, int id)
            {
                ref var entry = ref Messages[message];
                if (entry.Count == 0)
                {
                    entry.First = id;
                }

                entry.Count++;
                entry.Last = id;
                ref var group = ref Groups[entry.Group];
                if (group.Count == 0)
                {
                    group.First = id;
                }

                group.Count++;
                group.Last = id;
                group.Latest = message;
            }
        }
    }
}
