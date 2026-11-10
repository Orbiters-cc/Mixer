using System;
using System.Collections.Generic;
using System.Threading;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LogStore
    {
        // Occurrences the editor's thread counts itself when a compaction lands; more than that and a worker catches up
        // first. Bounds what landing costs, whatever the store's size.
        internal const int LandingLimit = 4096;

        /// <summary>
        /// Messages none of whose logs are left (trimmed), and the texts only they used, go once they are many (or the
        /// texts are over budget): a stream of logs that never repeat would otherwise keep every text ever seen. Message,
        /// group and text ids change (new tables: a filter pass still reading the old ones is unaffected); occurrence ids
        /// stay. In the background the new tables are prepared on a worker and land in <see cref="StepMaintenance"/>.
        /// </summary>
        private void MaybeCompact()
        {
            if (!AllowCompaction || compaction != null || pins > 0 || Texts.Faulted)
            {
                // An import holds message ids that have no occurrence yet; or a text could not be read back, and copying
                // it would keep its placeholder as if it were the log (LogCapture starts over instead).
                return;
            }

            int dead = zeroCount;
            // Many dead, or texts over budget: then any dead message's texts are worth reclaiming.
            if (dead == 0 || Texts.TotalChars <= maxTextChars && (dead < 1024 || dead * 4 < messageCount))
            {
                return;
            }

            compaction = new StoreCompaction(this);
            if (CompactInBackground)
            {
                compaction.Start();
                return;
            }

            compaction.CatchUp();
            TryLand(int.MaxValue);
        }

        /// <summary>
        /// Main thread, each editor update: lands a compaction prepared in the background once what arrived meanwhile is
        /// little (at most <see cref="LandingLimit"/> occurrences to count here), else lets the worker catch up again.
        /// A field read when there is none.
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

            TryLand(LandingLimit);
        }

        /// <summary>Waits for a compaction in the background and lands it (tests).</summary>
        internal void FinishMaintenance()
        {
            while (compaction != null)
            {
                compaction.Wait();
                if (compaction.Failed)
                {
                    compaction = null;
                    return;
                }

                TryLand(int.MaxValue);
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
            }
        }

        // Swaps the prepared tables in when little is left to count, in one step: between updates the store is the old
        // one, whole, and the worker's tables are its own.
        private void TryLand(int limit)
        {
            var prepared = compaction;
            if (end - prepared.Done > limit)
            {
                prepared.Start();
                return;
            }

            prepared.CountTo(end);
            prepared.ApplyPatches(messages);
            if (Texts.Faulted || prepared.Failed || !ReferenceEquals(Texts, prepared.OldTexts))
            {
                // A text read back from the session file for the copy was damaged: the copy holds its placeholder.
                // Keep the old table untouched; LogCapture starts over from Unity's console.
                compaction = null;
                return;
            }

            var remapped = new Dictionary<int, TimingProfile>(profiles.Count);
            foreach (var pair in profiles)
            {
                int moved = prepared.NewId(pair.Key);
                if (moved >= 0)
                {
                    remapped[moved] = pair.Value;
                }
            }

            profiles.Clear();
            foreach (var pair in remapped)
            {
                profiles[pair.Key] = pair.Value;
            }

            var outputs = PayloadPairs();
            payloads.Clear();
            payloadOrder.Clear();
            foreach (var pair in outputs)
            {
                SetPayload(pair.Key, prepared.PayloadText(pair.Key, pair.Value));
            }

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
            if (Texts.TotalChars > maxTextChars && pins == 0)
            {
                // Still over budget: the next oldest fifth goes (and compacts in turn).
                KeepTextBudget();
            }
        }

        /// <summary>
        /// New message, group and text tables for the occurrences in the store, built from the occurrences themselves (a
        /// message is kept when it has one, in the order of its oldest) and the fields of a message that never change
        /// once it is filed. What changes later (a file Unity's console gave, an assembly, compile flags) is noted as a
        /// patch and copied when the tables land. Occurrences are counted on a worker up to what was filed when it
        /// looked, then the rest on the editor's thread at landing. Anything that moves occurrences or drops messages
        /// (trim, clear, regroup, history, a pin, renumbering) cancels it.
        /// </summary>
        private sealed class StoreCompaction
        {
            private readonly LogStore store;
            private readonly GroupingMode grouping;
            private readonly int chunkBase;
            private readonly int first;
            private readonly List<int> patches = new List<int>();
            // The outputs kept when it started (occurrence, old text id), and their texts in the new table once copied.
            private readonly List<KeyValuePair<int, int>> outputs;
            private readonly Dictionary<int, KeyValuePair<int, int>> copiedOutputs = new Dictionary<int, KeyValuePair<int, int>>();
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
                first = store.first;
                done = first;
                owners = new int[store.ownerChunks.Length][];
                outputs = store.PayloadPairs();
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

            public bool Busy => busy;
            public bool Failed => failed;

            /// <summary>A worker counts what was filed meanwhile (main thread).</summary>
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
            /// Counts what the store filed since, a few rounds while logs keep coming, until little is left for the
            /// editor's thread (any thread: it only reads what the store published before its last occurrence id).
            /// </summary>
            public void CatchUp()
            {
                if (copiedOutputs.Count == 0)
                {
                    foreach (var pair in outputs)
                    {
                        copiedOutputs[pair.Key] = new KeyValuePair<int, int>(pair.Value, Copy(pair.Value));
                    }
                }

                for (int round = 0; round < 64 && !cancelled; round++)
                {
                    int to = Volatile.Read(ref store.end);
                    CountTo(to);
                    if (Volatile.Read(ref store.end) - to <= LandingLimit)
                    {
                        return;
                    }
                }
            }

            /// <summary>Counts the occurrences up to <paramref name="to"/>, adopting each message at its first one.</summary>
            public void CountTo(int to)
            {
                // Read after the occurrence id: whatever the store published before it is in these.
                var directory = Volatile.Read(ref store.ownerChunks);
                var oldMessages = Volatile.Read(ref store.messages);
                if (owners.Length < directory.Length)
                {
                    Array.Resize(ref owners, directory.Length);
                }

                int id = done;
                while (id < to && !cancelled)
                {
                    int slot = id - chunkBase;
                    int chunk = slot >> ChunkShift;
                    int offset = slot & ChunkMask;
                    int run = Math.Min(to - id, ChunkSize - offset);
                    var column = directory[chunk];
                    var remapped = owners[chunk] ?? (owners[chunk] = new int[ChunkSize]);
                    for (int i = offset; i < offset + run; i++, id++)
                    {
                        int message = NewIdOrAdopt(column[i], oldMessages);
                        remapped[i] = message;
                        Count(message, id);
                    }

                    Volatile.Write(ref done, id);
                }

                if (cancelled)
                {
                    failed = true;
                }
            }

            /// <summary>The new table's id of an output's text (copied by the worker, or now when it came since; main thread).</summary>
            public int PayloadText(int occurrence, int oldText) =>
                copiedOutputs.TryGetValue(occurrence, out var copied) && copied.Key == oldText ? copied.Value : Copy(oldText);

            /// <summary>The new id of an old message, -1 when it had no occurrence (it goes).</summary>
            public int NewId(int old) => old >= 0 && old < map.Length ? map[old] - 1 : -1;

            /// <summary>A message's later change, copied when the tables land (main thread).</summary>
            public void Patched(int old) => patches.Add(old);

            /// <summary>Copies what changed in patched messages since they were adopted (main thread, at landing).</summary>
            public void ApplyPatches(LogMessage[] current)
            {
                foreach (int old in patches)
                {
                    int moved = NewId(old);
                    if (moved < 0)
                    {
                        continue;
                    }

                    ref var source = ref current[old];
                    ref var target = ref Messages[moved];
                    target.Flags = source.Flags;
                    target.Line = source.Line;
                    target.File = Copy(source.File);
                    target.Callsite = Copy(source.Callsite);
                    target.Assembly = Copy(source.Assembly);
                }
            }

            /// <summary>The owner chunks with the new message ids, in a directory of <paramref name="size"/>.</summary>
            public int[][] OwnerDirectory(int size)
            {
                var directory = new int[Math.Max(size, owners.Length)][];
                Array.Copy(owners, directory, owners.Length);
                return directory;
            }

            private int NewIdOrAdopt(int old, LogMessage[] oldMessages)
            {
                if (old >= map.Length)
                {
                    Array.Resize(ref map, Math.Max(old + 1, map.Length * 2));
                }

                int moved = map[old] - 1;
                if (moved >= 0)
                {
                    return moved;
                }

                // A copy: the store changes the counters of its message meanwhile, never its text ids or its type.
                var message = oldMessages[old];
                message.Text = Copy(message.Text);
                message.Stack = Copy(message.Stack);
                message.Callsite = Copy(message.Callsite);
                message.File = Copy(message.File);
                message.Assembly = Copy(message.Assembly);
                message.Count = 0;
                message.First = message.Last = -1;
                message.Explanation = -2;
                message.Group = GroupFor(Texts, grouping, ref Groups, ref GroupCount, GroupIndex, message.Text, message.Level);
                Groups[message.Group].Variants++;
                if (MessageCount == Messages.Length)
                {
                    Array.Resize(ref Messages, Messages.Length * 2);
                }

                moved = MessageCount++;
                Messages[moved] = message;
                MessageIndex[MessageKey.Of(message)] = moved;
                map[old] = moved + 1;
                return moved;
            }

            private int Copy(int text) => text <= 0 ? 0 : Texts.Intern(OldTexts[text]);

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
