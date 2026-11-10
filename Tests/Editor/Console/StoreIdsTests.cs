using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    /// <summary>
    /// Stable occurrence ids (trims and history never renumber) and compactions prepared on a worker while logs keep
    /// arriving, checked against brute-force recounts and a store that never compacts.
    /// </summary>
    public class StoreIdsTests
    {
        private const string Stack = "UnityEngine.Debug:Log (object)\nGame:Tick () (at Assets/Game.cs:12)\n";

        // What the n-th log of a run says: some repeat, most don't.
        private static string TextOf(int n) => n % 7 == 0 ? "Repeated every seventh" : n % 11 == 0 ? "Repeated <b>bold</b> " + (n % 3) : "Distinct log " + n;

        // Every counter of the store against a count from its occurrences, by brute force.
        private static void AssertCountersMatchARecount(LogStore store, bool firstExact)
        {
            var counts = new int[store.MessageCount];
            var firsts = new int[store.MessageCount];
            var lasts = new int[store.MessageCount];
            var groupCounts = new int[store.GroupCount];
            var groupFirsts = new int[store.GroupCount];
            var groupLasts = new int[store.GroupCount];
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                int m = store.MessageAt(id);
                if (counts[m]++ == 0)
                {
                    firsts[m] = id;
                }

                lasts[m] = id;
                int g = store.Message(m).Group;
                if (groupCounts[g]++ == 0)
                {
                    groupFirsts[g] = id;
                }

                groupLasts[g] = id;
            }

            for (int m = 0; m < store.MessageCount; m++)
            {
                ref var message = ref store.Message(m);
                Assert.AreEqual(counts[m], message.Count, "message " + m);
                if (counts[m] == 0)
                {
                    continue;
                }

                Assert.AreEqual(lasts[m], message.Last, "message " + m);
                Assert.GreaterOrEqual(message.First, 0, "message " + m);
                Assert.LessOrEqual(message.First, firsts[m], "never after its oldest log");
                if (firstExact)
                {
                    Assert.AreEqual(firsts[m], message.First, "message " + m);
                }
            }

            for (int g = 0; g < store.GroupCount; g++)
            {
                ref var group = ref store.Group(g);
                Assert.AreEqual(groupCounts[g], group.Count, "group " + g);
                if (groupCounts[g] == 0)
                {
                    continue;
                }

                Assert.AreEqual(groupLasts[g], group.Last, "group " + g);
                Assert.LessOrEqual(group.First, groupFirsts[g]);
                Assert.GreaterOrEqual(group.First, 0);
                Assert.AreEqual(group.Last, store.Message(group.Latest).Last, "the latest message has the latest log");
            }
        }

        [Test]
        public void IdsStayThroughTrimsAndHistory()
        {
            var store = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
            int emptied = 0;
            store.Emptied += () => emptied++;
            var remembered = new Dictionary<int, (string text, long time)>();
            for (int n = 0; n < 200_000; n++)
            {
                int id = store.Add(1000 + n, TextOf(n), Stack, LogVariant.Log);
                Assert.AreEqual(LogStore.HistoryRoom + n, id);
                if (n % 997 == 0)
                {
                    remembered[id] = (TextOf(n), 1000 + n);
                    store.SetContext(id, n + 1);
                }
            }

            // History in front takes the ids below, across chunks.
            var old = store.Intern("From Unity's console", string.Empty, LogVariant.Warning);
            const int history = 150_000;
            var times = new long[history];
            var owners = new int[history];
            for (int i = 0; i < history; i++)
            {
                times[i] = i;
                owners[i] = i % 2 == 0 ? old : store.MessageAt(store.FirstId);
            }

            int firstBefore = store.FirstId;
            store.Prepend(times, owners, new byte[history], history, new Dictionary<int, int> { { 5, 99 } });
            Assert.AreEqual(firstBefore - history, store.FirstId);
            Assert.AreEqual(0, emptied, "nothing was renumbered");
            Assert.IsTrue(store.TryGetContext(store.FirstId + 5, out int historyContext) && historyContext == 99);
            AssertCountersMatchARecount(store, firstExact: true);
            Assert.AreEqual(store.FirstId, store.Message(old).First, "history's oldest is exact");

            // Not on a chunk boundary, across several chunks: the history and some of the live logs go.
            store.Trim(123_457);
            firstBefore = store.FirstId;
            Assert.AreEqual(LogStore.HistoryRoom + 200_000 - 123_457, store.FirstId);
            Assert.AreEqual(0, emptied);
            AssertCountersMatchARecount(store, firstExact: false);

            foreach (var pair in remembered)
            {
                if (!store.Contains(pair.Key))
                {
                    Assert.Less(pair.Key, firstBefore, "only trimmed logs are gone");
                    continue;
                }

                Assert.AreEqual(pair.Value.text, store.Texts[store.Message(store.MessageAt(pair.Key)).Text]);
                Assert.AreEqual(pair.Value.time, store.TimeAt(pair.Key));
                Assert.IsTrue(store.TryGetContext(pair.Key, out int context));
                Assert.AreEqual(pair.Key - LogStore.HistoryRoom + 1, context);
            }

            // Brute-force order and lookups by time.
            for (int id = store.FirstId + 1; id < store.EndId; id++)
            {
                Assert.LessOrEqual(store.TimeAt(id - 1), store.TimeAt(id));
            }

            Assert.AreEqual(store.FirstId, store.LowerBound(long.MinValue));
            Assert.AreEqual(store.EndId, store.LowerBound(long.MaxValue));
            long middle = store.TimeAt(firstBefore + 10);
            int found = store.LowerBound(middle);
            Assert.AreEqual(middle, store.TimeAt(found));
            Assert.Less(store.TimeAt(found - 1), middle);

            // History after a trim takes ids trimmed logs had: whoever remembers ids forgets them; live ids stay.
            int live = store.EndId - 1;
            string liveText = store.Texts[store.Message(store.MessageAt(live)).Text];
            store.Prepend(new long[] { 0 }, new[] { old }, new byte[1], 1, null);
            Assert.AreEqual(1, emptied);
            Assert.AreEqual(liveText, store.Texts[store.Message(store.MessageAt(live)).Text]);
            AssertCountersMatchARecount(store, firstExact: false);
        }

        [Test]
        public void IdsStartOverOnlyWhenTheyRunOut()
        {
            var store = new LogStore(LogStore.MaxEnd - 10) { AllowCompaction = false };
            int emptied = 0;
            store.Emptied += () => emptied++;
            for (int n = 0; n < 30; n++)
            {
                store.Add(n, TextOf(n), Stack, LogVariant.Log);
            }

            Assert.AreEqual(1, emptied, "renumbered once, as a clear for whoever remembers ids");
            Assert.AreEqual(30, store.Count);
            Assert.Less(store.EndId, LogStore.MaxEnd);
            for (int n = 0; n < 30; n++)
            {
                int id = store.FirstId + n;
                Assert.AreEqual(TextOf(n), store.Texts[store.Message(store.MessageAt(id)).Text]);
                Assert.AreEqual(n, store.TimeAt(id));
            }

            AssertCountersMatchARecount(store, firstExact: true);
        }

        // Ids near the top of the range: a filter pass, its extension and a moved chart step through them without
        // overflowing, and count exactly what a store starting at 0 counts for the same logs.
        [Test]
        public void FilteringAndChartsWorkJustBelowTheLastIds()
        {
            int top = LogStore.MaxEnd - 100_000;
            var high = new LogStore(top) { AllowCompaction = false };
            var low = new LogStore { AllowCompaction = false };
            for (int n = 0; n < 99_000; n++)
            {
                var variant = n % 13 == 0 ? LogVariant.Error : n % 5 == 0 ? LogVariant.Warning : LogVariant.Log;
                high.Add(n * 1000L, TextOf(n % 3000), Stack, variant);
                low.Add(n * 1000L, TextOf(n % 3000), Stack, variant);
            }

            Assert.AreEqual(top, high.FirstId, "not renumbered");
            foreach (var query in new[] { LogQuery.Empty, new LogQuery("Distinct log 2", false, false, false) })
            {
                var spec = new FilterSpec { Query = query };
                var a = FilterState.Compute(high.View(), spec, high.Sources.All, null, high.FirstTime, high.LastTime, 50, () => false);
                var b = FilterState.Compute(low.View(), spec, low.Sources.All, null, low.FirstTime, low.LastTime, 50, () => false);
                AssertSameState(a, b, top);

                for (int n = 99_000; n < 99_500; n++)
                {
                    high.Add(n * 1000L, TextOf(n), Stack, LogVariant.Log);
                    low.Add(n * 1000L, TextOf(n), Stack, LogVariant.Log);
                }

                Assert.IsTrue(a.Extend(high, high.Sources.All));
                Assert.IsTrue(b.Extend(low, low.Sources.All));
                AssertSameState(a, b, top);

                var chartA = a.ComputeChart(high.View(), high.FirstTime, high.LastTime + 1000, 40, null, () => false);
                var chartB = b.ComputeChart(low.View(), low.FirstTime, low.LastTime + 1000, 40, null, () => false);
                CollectionAssert.AreEqual(chartB.Timeline, chartA.Timeline);
                Assert.AreEqual(chartB.Covered + top, chartA.Covered);
            }
        }

        private static void AssertSameState(FilterState a, FilterState b, int offset)
        {
            Assert.AreEqual(b.RowCount, a.RowCount);
            for (int i = 0; i < b.RowCount; i++)
            {
                Assert.AreEqual(b.Rows[i] + offset, a.Rows[i], "row " + i);
            }

            Assert.AreEqual(b.Covered + offset, a.Covered);
            CollectionAssert.AreEqual(b.LevelCounts, a.LevelCounts);
            CollectionAssert.AreEqual(b.Timeline, a.Timeline);
            Assert.AreEqual(b.GroupRowCount, a.GroupRowCount);
            for (int g = 0; g < b.GroupRowCount; g++)
            {
                int group = b.GroupRows[g];
                Assert.AreEqual(b.GroupCount[group], a.GroupCount[group]);
                Assert.AreEqual(b.GroupFirst[group] + offset, a.GroupFirst[group]);
                Assert.AreEqual(b.GroupLast[group] + offset, a.GroupLast[group]);
            }
        }

        [Test]
        public void ARandomSessionKeepsItsCountersRight()
        {
            var random = new Random(7);
            var store = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
            int n = 0;
            for (int step = 0; step < 300; step++)
            {
                int adds = random.Next(1, 3000);
                for (int i = 0; i < adds; i++, n++)
                {
                    store.Add(n, TextOf(random.Next(0, 5000)), Stack, LogVariant.Log);
                }

                if (random.Next(4) == 0)
                {
                    store.Trim(random.Next(0, store.Count + 1));
                }
            }

            AssertCountersMatchARecount(store, firstExact: false);
        }

        // ---- Compaction --------------------------------------------------------------------------------------

        // The same calls on two stores: the compacted one must read exactly as the one that never compacts.
        private static void AssertSameLogs(LogStore expected, LogStore actual)
        {
            Assert.AreEqual(expected.FirstId, actual.FirstId);
            Assert.AreEqual(expected.EndId, actual.EndId);
            for (int id = expected.FirstId; id < expected.EndId; id++)
            {
                Assert.AreEqual(expected.TimeAt(id), actual.TimeAt(id));
                Assert.AreEqual(expected.FlagsAt(id), actual.FlagsAt(id));
                ref var a = ref expected.Message(expected.MessageAt(id));
                ref var b = ref actual.Message(actual.MessageAt(id));
                string where = "occurrence " + id;
                Assert.AreEqual(expected.Texts[a.Text], actual.Texts[b.Text], where);
                Assert.AreEqual(expected.Texts.Plain(a.Text), actual.Texts.Plain(b.Text), where);
                Assert.AreEqual(expected.Texts[a.Stack], actual.Texts[b.Stack], where);
                Assert.AreEqual(expected.Texts[a.Callsite], actual.Texts[b.Callsite], where);
                Assert.AreEqual(expected.Texts[a.File], actual.Texts[b.File], where);
                Assert.AreEqual(expected.Texts[a.Assembly], actual.Texts[b.Assembly], where);
                Assert.AreEqual(a.Line, b.Line, where);
                Assert.AreEqual(a.Variant, b.Variant, where);
                Assert.AreEqual(a.Flags, b.Flags, where);
                Assert.AreEqual(a.Count, b.Count, where);
                Assert.AreEqual(a.Last, b.Last, where);
                Assert.AreEqual(expected.SourceOf(expected.MessageAt(id)).Name, actual.SourceOf(actual.MessageAt(id)).Name, where);
                Assert.AreEqual(expected.Group(a.Group).Count, actual.Group(b.Group).Count, where);
                Assert.AreEqual(expected.Texts[expected.Group(a.Group).Key], actual.Texts[actual.Group(b.Group).Key], where);
                Assert.AreEqual(expected.TryGetContext(id, out int ca), actual.TryGetContext(id, out int cb), where);
                Assert.AreEqual(ca, cb, where);
            }

            AssertCountersMatchARecount(actual, firstExact: false);
        }

        // What both stores receive while the compaction is prepared: new messages, a dead message logged again, a live
        // one, a compile error fixed and reported again (flags), a file from Unity's console and an assembly (patches).
        private static void Meanwhile(LogStore store, int round)
        {
            int baseTime = 100_000 + round * 1000;
            for (int i = 0; i < 300; i++)
            {
                store.Add(baseTime + i, "Arrived meanwhile " + round + " " + i, Stack, LogVariant.Log);
            }

            store.Add(baseTime + 400, "Distinct log 10", Stack, LogVariant.Log);
            store.Add(baseTime + 401, "Distinct log 5995", Stack, LogVariant.Log);
            int assembly = store.AssemblyId("Assembly-CSharp");
            store.AddCompile(baseTime + 402, "Assets/A.cs(3,4): error CS0103: The name 'x' does not exist", true, 0, null, 0, 0);
            store.ResolveCompile(-1, errorsOnly: true);
            int unknown = store.MessageAt(store.Add(baseTime + 403, "Engine error without a stack " + round, string.Empty, LogVariant.Error));
            store.SetFileIfUnknown(unknown, "Assets/Engine.cs", 77);
            if (store.HasActiveCompileMessage("Assets/A.cs(3,4): error CS0103: The name 'x' does not exist", out int compile))
            {
                store.SetAssembly(compile, assembly);
            }

            store.SetContext(store.EndId - 1, 4242);
        }

        private static void Fill(LogStore store)
        {
            for (int n = 0; n < 6000; n++)
            {
                store.Add(n, "Distinct log " + n, Stack, n % 5 == 0 ? LogVariant.Warning : LogVariant.Log);
            }

            store.AddCompile(6000, "Assets/A.cs(3,4): error CS0103: The name 'x' does not exist", true, 0, null, 0, 0);
        }

        [Test]
        public void ACompactionPreparedInTheBackgroundMatchesNoCompaction([Values(0, 1, 2)] int order)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var reference = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
                var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
                int regrouped = 0;
                store.Regrouped += () => regrouped++;
                Fill(reference);
                Fill(store);
                long generation = store.Texts.Generation;
                reference.Trim(1000);
                store.Trim(1000);
                Assert.IsTrue(store.Compacting, "5,000 dead messages: a compaction starts");
                if (order == 1)
                {
                    // Logs arrive only once the worker is done: the editor's thread counts them at landing.
                    store.WaitForCompactionWorker();
                }

                for (int round = 0; round < 3; round++)
                {
                    Meanwhile(reference, round);
                    Meanwhile(store, round);
                    if (order == 2)
                    {
                        store.StepMaintenance();
                    }
                }

                store.FinishMaintenance();
                Assert.IsFalse(store.Compacting);
                Assert.AreNotEqual(generation, store.Texts.Generation, "it landed");
                Assert.AreEqual(1, regrouped);
                Assert.Less(store.MessageCount, reference.MessageCount, "the dead messages went");
                AssertSameLogs(reference, store);
                AssertCountersMatchARecount(store, firstExact: true);

                // A message logged again after the compaction is the same message, found by its text.
                int again = store.Add(500_000, "Arrived meanwhile 2 299", Stack, LogVariant.Log);
                Assert.AreEqual(2, store.Message(store.MessageAt(again)).Count);
            }
        }

        // The landing is bounded on the editor's thread: many late logs, large late texts or many patches send it back to
        // a worker; it still lands, through StepMaintenance only, and reads as a store that never compacted.
        [Test]
        public void TheLandingStaysWithinItsBoundsAndStillLands([Values(0, 1, 2)] int load)
        {
            var reference = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
            var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
            Fill(reference);
            Fill(store);
            reference.Trim(1000);
            store.Trim(1000);
            Assert.IsTrue(store.Compacting);
            store.WaitForCompactionWorker();

            void Late(LogStore target)
            {
                switch (load)
                {
                    case 0:
                        // More late occurrences than the landing counts.
                        for (int i = 0; i < 3 * LogStore.LandingLimit; i++)
                        {
                            target.Add(200_000 + i, "Late " + i, Stack, LogVariant.Log);
                        }

                        break;
                    case 1:
                        // Few, but more characters than the landing copies.
                        string large = new string('x', LogStore.LandingChars / 8);
                        for (int i = 0; i < 20; i++)
                        {
                            target.Add(200_000 + i, "Late large " + i + " " + large, Stack, LogVariant.Log);
                        }

                        break;
                    default:
                        // More patches than the landing applies (each message patched twice).
                        int assembly = target.AssemblyId("Assembly-CSharp");
                        for (int round = 0; round < 2; round++)
                        {
                            for (int id = target.FirstId; id < target.FirstId + 3 * LogStore.LandingPatches; id++)
                            {
                                target.SetAssembly(target.MessageAt(id), assembly);
                            }
                        }

                        break;
                }
            }

            Late(reference);
            Late(store);
            store.StepMaintenance();
            Assert.IsTrue(store.Compacting, "too much for one landing: a worker catches up first");
            for (int update = 0; update < 1000 && store.Compacting; update++)
            {
                store.WaitForCompactionWorker();
                store.StepMaintenance();
            }

            Assert.IsFalse(store.Compacting, "it lands");
            Assert.Less(store.MessageCount, reference.MessageCount);
            AssertSameLogs(reference, store);
        }

        // Ten late messages of 100,000 characters: no landing step copies more than its character budget (checked before
        // each adoption, the last one included), and the compaction still lands over several steps.
        [Test]
        public void LargeLateMessagesNeverPassTheLandingsCharacterBudget()
        {
            var reference = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
            var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
            Fill(reference);
            Fill(store);
            reference.Trim(1000);
            store.Trim(1000);
            store.WaitForCompactionWorker();
            string large = new string('L', 100_000);
            for (int i = 0; i < 10; i++)
            {
                reference.Add(300_000 + i, "Late " + i + " " + large, string.Empty, LogVariant.Log);
                store.Add(300_000 + i, "Late " + i + " " + large, string.Empty, LogVariant.Log);
            }

            int steps = 0;
            while (store.Compacting)
            {
                // The editor's update, without waiting for the worker first: a landing attempt each time it is idle.
                store.StepMaintenance();
                Assert.LessOrEqual(store.LastLandingChars, LogStore.LandingChars, "step " + steps);
                if (steps == 0)
                {
                    Assert.IsTrue(store.Compacting, "a million characters don't fit one landing");
                    Assert.GreaterOrEqual(store.LastLandingChars, 100_000, "what fits is adopted (and stays adopted)");
                }

                if (store.Compacting)
                {
                    store.WaitForCompactionWorker();
                }

                Assert.Less(++steps, 1000);
            }

            AssertSameLogs(reference, store);
        }

        // The budget never depends on a compaction in the background: a flood of distinct logs whose compactions keep being
        // cancelled (count trims) or overtaken holds at most TextCeiling characters at any time.
        [Test]
        public void TheTextsNeverPassTheCeilingWhileCompactionsAreCancelled()
        {
            var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true, MaxOccurrences = 10_000, MaxTextChars = 1 << 20 };
            string padding = new string('p', 90);
            int n = 0;
            for (; n < 200_000; n++)
            {
                store.Add(n, "Distinct " + n + " " + padding, Stack, LogVariant.Log);
                Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling, "after log " + n);
                if (n % 3000 == 0)
                {
                    store.StepMaintenance();
                }
            }

            Assert.Greater(store.CompactionsCancelled, 0, "compactions were cancelled under count pressure");
            Assert.Greater(store.CeilingCompactions, 0, "the ceiling compacted synchronously");
            store.FinishMaintenance();
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                Assert.AreEqual("Distinct " + (id - LogStore.HistoryRoom) + " " + padding, store.Texts[store.Message(store.MessageAt(id)).Text]);
            }

            AssertCountersMatchARecount(store, firstExact: false);
        }

        [Test]
        public void ATrimOrHistoryCancelsACompactionBeingPrepared()
        {
            var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
            Fill(store);
            store.Trim(1000);
            Assert.IsTrue(store.Compacting);
            int imported = store.Intern("From Unity's console", string.Empty, LogVariant.Log);
            // Interned and put in front in one step, as an import's slice is: nothing can compact in between.
            store.Prepend(new long[] { 1 }, new[] { imported }, new byte[1], 1, null);
            Assert.IsFalse(store.Compacting, "history in front starts it over");
            Assert.AreEqual("From Unity's console", store.Texts[store.Message(store.MessageAt(store.FirstId)).Text]);
            store.Trim(400);
            store.FinishMaintenance();
            AssertCountersMatchARecount(store, firstExact: true);
            Assert.AreEqual("Distinct log 5999", store.Texts[store.Message(store.MessageAt(store.EndId - 2)).Text]);
        }

        // The editor's case: logs keep arriving on the main thread while workers prepare compactions that land a few
        // milliseconds at a time; trims wait for them. Every live log must still read as what was logged.
        [Test]
        public void LogsKeepArrivingWhileCompactionsRun()
        {
            var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true, MaxOccurrences = 10_000 };
            int landed = 0;
            store.Regrouped += () => landed++;
            int last = 0;
            for (int n = 0; n < 300_000; n++)
            {
                int id = store.Add(n, TextOf(n), Stack, LogVariant.Log);
                Assert.AreEqual(LogStore.HistoryRoom + n, id);
                if (n % 500 == 0)
                {
                    store.StepMaintenance();
                }

                last = id;
            }

            store.FinishMaintenance();
            Assert.Greater(landed, 0, "compactions landed while logs arrived");
            Assert.LessOrEqual(store.Count, 10_000 + 10_000 / 5 + 1, "trims waited for compactions, at most a fifth more logs");
            Assert.AreEqual(last + 1, store.EndId);
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                Assert.AreEqual(TextOf(id - LogStore.HistoryRoom), store.Texts[store.Message(store.MessageAt(id)).Text], "occurrence " + id);
                Assert.AreEqual(id - LogStore.HistoryRoom, store.TimeAt(id));
            }

            AssertCountersMatchARecount(store, firstExact: false);
            // Every live message is found by its text again.
            for (int m = 0; m < store.MessageCount; m++)
            {
                ref var message = ref store.Message(m);
                if (message.Count > 0)
                {
                    Assert.AreEqual(m, store.Intern(store.Texts[message.Text], store.Texts[message.Stack], message.Variant));
                }
            }

            Assert.Less(store.MessageCount, 40_000, "dead messages went (300,000 distinct-ish logs)");
        }

        [Test]
        public void ASessionKeepsItsIdsAcrossAReload()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mixer-session-" + Guid.NewGuid().ToString("N"));
            var saving = new SessionFile(folder);
            var loading = new SessionFile(folder);
            try
            {
                var store = new LogStore(LogStore.HistoryRoom) { AllowCompaction = false };
                for (int n = 0; n < 150_000; n++)
                {
                    store.Add(n, TextOf(n), Stack, LogVariant.Log);
                }

                store.SetContext(store.EndId - 3, 31);
                saving.Save(store, new SnapshotInfo(), 1, 2);
                // A trim of part of what is saved: the files keep their first rows, the head says where the store starts.
                store.Trim(100_000);
                store.Add(200_000, "After the trim", string.Empty, LogVariant.Warning);
                saving.Save(store, new SnapshotInfo(), 1, 2);
                saving.Close();

                var loaded = loading.Load(1, 2, out _, out string problem);
                Assert.IsNull(problem, problem);
                Assert.AreEqual(store.FirstId, loaded.FirstId);
                Assert.AreEqual(store.EndId, loaded.EndId);
                Assert.AreEqual(store.InitialFirstId, loaded.InitialFirstId);
                for (int id = store.FirstId; id < store.EndId; id++)
                {
                    Assert.AreEqual(store.TimeAt(id), loaded.TimeAt(id));
                    Assert.AreEqual(store.Texts[store.Message(store.MessageAt(id)).Text], loaded.Texts[loaded.Message(loaded.MessageAt(id)).Text]);
                }

                Assert.IsTrue(loaded.TryGetContext(store.EndId - 4, out int context) && context == 31);
                AssertCountersMatchARecount(loaded, firstExact: false);
                loading.Close();
            }
            finally
            {
                saving.Close();
                loading.Close();
                try
                {
                    if (Directory.Exists(folder) && Path.GetFileName(folder).StartsWith("mixer-session-", StringComparison.Ordinal))
                    {
                        Directory.Delete(folder, true);
                    }
                }
                catch (IOException)
                {
                    // A text read after its session closed reopened the file: the temporary folder stays.
                }
            }
        }

        [Test]
        public void ASessionOfAnotherVersionGoesThroughTheRecovery()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mixer-session-" + Guid.NewGuid().ToString("N"));
            var saving = new SessionFile(folder);
            try
            {
                var store = new LogStore();
                store.Add(100, "Saved by an earlier version", string.Empty, LogVariant.Log);
                saving.Save(store, new SnapshotInfo(), 1, 2);
                saving.Close();
                string head = Directory.GetFiles(folder, "head-*.bin")[0];
                byte[] bytes = File.ReadAllBytes(head);
                // The format number follows the magic: the session file of round 1 was format 3.
                BitConverter.GetBytes(3).CopyTo(bytes, 4);
                File.WriteAllBytes(head, bytes);

                Assert.IsNull(new SessionFile(folder).Load(1, 2, out _, out string problem), "never converted");
                Assert.IsNotNull(problem, "this editor's: LogCapture's one recovery, with its warning line");
                Assert.IsNull(new SessionFile(folder).Load(9, 2, out _, out string other));
                Assert.IsNull(other, "another editor's session is not a problem");
            }
            finally
            {
                saving.Close();
                try
                {
                    if (Directory.Exists(folder) && Path.GetFileName(folder).StartsWith("mixer-session-", StringComparison.Ordinal))
                    {
                        Directory.Delete(folder, true);
                    }
                }
                catch (IOException)
                {
                    // Left for the system's temporary folder cleanup.
                }
            }
        }
    }
}
