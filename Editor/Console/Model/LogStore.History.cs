using System;
using System.Collections.Generic;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LogStore
    {
        // History an import read and that waits to be put in front of the occurrences (oldest first): owned and counted by
        // the store from the moment it is read, so a message it references never counts as dead, the limits count it
        // (Total), a trim drops its oldest rows first (history is older than every occurrence: the newest history wins),
        // a compaction keeps its messages and renumbers them. Rows [stagedStart, stagedTimes.Count) are kept.
        private readonly List<long> stagedTimes = new List<long>();
        private readonly List<int> stagedOwners = new List<int>();
        private readonly List<byte> stagedFlags = new List<byte>();
        private readonly Dictionary<int, int> stagedContexts = new Dictionary<int, int>();
        private int stagedStart;
        // References from staged rows, by message id.
        private int[] stagedRefs = new int[0];

        /// <summary>History rows read and waiting to be put in front.</summary>
        internal int StagedCount => stagedTimes.Count - stagedStart;

        private int StagedRefs(int message) => message < stagedRefs.Length ? stagedRefs[message] : 0;

        /// <summary>
        /// One history row an import read (older than every occurrence and than every row staged after it): the store
        /// holds it from now on, under the same limits as everything else (checked here, as for a log).
        /// </summary>
        internal void Stage(long time, int message, OccurrenceFlags occurrenceFlags, int instanceId)
        {
            if (compaction != null)
            {
                // It renumbers messages it doesn't know staged rows reference: it starts over later, synchronously.
                CancelCompaction();
            }

            if (message >= stagedRefs.Length)
            {
                Array.Resize(ref stagedRefs, Math.Max(message + 1, Math.Max(256, stagedRefs.Length * 2)));
            }

            if (stagedRefs[message]++ == 0 && messages[message].Count == 0)
            {
                zeroCount--;
            }

            if (instanceId != 0)
            {
                stagedContexts[stagedTimes.Count] = instanceId;
            }

            // Times never go back (as Prepend keeps them): made monotonic here, row by row, they come out the same as
            // round 1's one Prepend of the whole history, however the rows are later put in front in batches.
            if (stagedTimes.Count > stagedStart && time < stagedTimes[stagedTimes.Count - 1])
            {
                time = stagedTimes[stagedTimes.Count - 1];
            }

            stagedTimes.Add(time);
            stagedOwners.Add(message);
            stagedFlags.Add((byte)occurrenceFlags);
            if (Total > maxOccurrences)
            {
                Trim(maxOccurrences * 4 / 5);
            }
            else if (Texts.TotalChars > maxTextChars)
            {
                KeepTextBudget();
            }

            EnforceCeiling();
        }

        /// <summary>
        /// Puts up to <paramref name="rows"/> of the newest staged rows in front of the occurrences (the staged rows left
        /// are older still). Returns how many went.
        /// </summary>
        internal int PublishStaged(int rows)
        {
            int count = Math.Min(rows, StagedCount);
            if (count <= 0)
            {
                return 0;
            }

            int from = stagedTimes.Count - count;
            var times = new long[count];
            var owners = new int[count];
            var flags = new byte[count];
            Dictionary<int, int> contexts = null;
            for (int i = 0; i < count; i++)
            {
                times[i] = stagedTimes[from + i];
                owners[i] = stagedOwners[from + i];
                flags[i] = stagedFlags[from + i];
                if (stagedContexts.TryGetValue(from + i, out int instance))
                {
                    contexts = contexts ?? new Dictionary<int, int>();
                    contexts[i] = instance;
                    stagedContexts.Remove(from + i);
                }
            }

            stagedTimes.RemoveRange(from, count);
            stagedOwners.RemoveRange(from, count);
            stagedFlags.RemoveRange(from, count);
            // Let go as staged, then counted as occurrences, with nothing between that could trim or compact (Prepend
            // counts them before it checks any limit).
            for (int i = 0; i < count; i++)
            {
                Release(owners[i]);
            }

            Prepend(times, owners, flags, count, contexts);

            return count;
        }

        /// <summary>Lets go of every staged row (a failed import, a reload: the import starts again from its rows).</summary>
        internal void DiscardStaged()
        {
            for (int k = stagedStart; k < stagedOwners.Count; k++)
            {
                Release(stagedOwners[k]);
            }

            ClearStaged();
        }

        // The oldest staged rows go (a trim).
        private void DropOldestStaged(int count)
        {
            for (int k = stagedStart; k < stagedStart + count; k++)
            {
                Release(stagedOwners[k]);
                stagedContexts.Remove(k);
            }

            stagedStart += count;
            if (stagedStart > 4096 && stagedStart * 2 > stagedTimes.Count)
            {
                // Compacted now and then; staged indices shift with it.
                var contexts = new Dictionary<int, int>(stagedContexts.Count);
                foreach (var pair in stagedContexts)
                {
                    contexts[pair.Key - stagedStart] = pair.Value;
                }

                stagedTimes.RemoveRange(0, stagedStart);
                stagedOwners.RemoveRange(0, stagedStart);
                stagedFlags.RemoveRange(0, stagedStart);
                stagedContexts.Clear();
                foreach (var pair in contexts)
                {
                    stagedContexts[pair.Key] = pair.Value;
                }

                stagedStart = 0;
            }
        }

        private void Release(int message)
        {
            if (message < stagedRefs.Length && stagedRefs[message] > 0 && --stagedRefs[message] == 0 && messages[message].Count == 0)
            {
                zeroCount++;
            }
        }

        private void ClearStaged()
        {
            stagedTimes.Clear();
            stagedOwners.Clear();
            stagedFlags.Clear();
            stagedContexts.Clear();
            stagedStart = 0;
            stagedRefs = new int[0];
        }

        // A synchronous compaction lands: staged rows name the new message ids.
        private void RemapStaged(StoreCompaction prepared)
        {
            var refs = new int[Math.Max(256, prepared.MessageCount + StagedCount)];
            for (int k = stagedStart; k < stagedOwners.Count; k++)
            {
                int moved = prepared.Keep(stagedOwners[k]);
                stagedOwners[k] = moved;
                if (moved >= refs.Length)
                {
                    Array.Resize(ref refs, Math.Max(moved + 1, refs.Length * 2));
                }

                refs[moved]++;
            }

            stagedRefs = refs;
        }
    }
}
