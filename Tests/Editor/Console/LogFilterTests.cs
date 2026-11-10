using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Debug = UnityEngine.Debug;

namespace Orbiters.Logger.Editor.Tests
{
    public class LogFilterTests
    {
        private const string Stack = "UnityEngine.Debug:Log (object)\nGame:Update () (at Assets/Scripts/Game.cs:12)\n";
        private const string SdkStack = "VRC.SDKBase.Thing:Do () (at ./Packages/com.vrchat.base/Runtime/Thing.cs:5)\n";

        private static LogStore Sample()
        {
            var store = new LogStore();
            store.Add(TimeSpan.TicksPerSecond * 1, "Player spawned", Stack, LogVariant.Log);
            store.Add(TimeSpan.TicksPerSecond * 2, "Missing texture on Body", Stack, LogVariant.Warning);
            store.Add(TimeSpan.TicksPerSecond * 3, "NullReferenceException: Object reference not set", SdkStack, LogVariant.Exception);
            store.Add(TimeSpan.TicksPerSecond * 4, "Player spawned", Stack, LogVariant.Log);
            store.Add(TimeSpan.TicksPerSecond * 5, "<color=red>Player</color> died", Stack, LogVariant.Error);
            store.Add(TimeSpan.TicksPerSecond * 6, "Player spawned", Stack, LogVariant.Log);
            return store;
        }

        private static FilterState Run(LogStore store, FilterSpec spec, int buckets = 10) =>
            FilterState.Compute(store.View(), spec, store.Sources.All, null, store.FirstTime, store.LastTime, buckets, null);

        [Test]
        public void ATimeWindowLeavesOlderLogsOutOfTheChartOnly()
        {
            var store = Sample();
            long windowStart = TimeSpan.TicksPerSecond * 4;
            var windowed = FilterState.Compute(store.View(), new FilterSpec(), store.Sources.All, null, windowStart, store.LastTime, 10, null);
            Assert.IsTrue(windowed.ChartClips);
            Assert.AreEqual(3, Sum(windowed.Timeline), "only the logs at 4, 5 and 6 s are in the chart");
            Assert.AreEqual(6, windowed.RowCount, "the list still has every log");
            Assert.AreEqual(3, windowed.LevelCounts[(int)LogLevel.Info], "and so do the counts");

            var whole = Run(store, new FilterSpec());
            Assert.IsFalse(whole.ChartClips);
            Assert.AreEqual(6, Sum(whole.Timeline));
        }

        [Test]
        public void TheTimelineWindowStartsAtTheOldestLogOrSlides()
        {
            long now = TimeSpan.TicksPerMinute * 100;
            Assert.AreEqual(5L, TimelineWindow.From(5L, now, 0), "all history starts at the oldest log");
            Assert.AreEqual(now - TimeSpan.TicksPerMinute * 15, TimelineWindow.From(5L, now, 15), "a window slides with the end");
        }

        [Test]
        public void EverythingShowsByDefault()
        {
            var store = Sample();
            var state = Run(store, new FilterSpec());
            Assert.AreEqual(6, state.RowCount);
            Assert.AreEqual(3, state.LevelCounts[(int)LogLevel.Info]);
            Assert.AreEqual(1, state.LevelCounts[(int)LogLevel.Warning]);
            Assert.AreEqual(2, state.LevelCounts[(int)LogLevel.Error]);
            Assert.AreEqual(4, state.GroupRowCount, "Player spawned ×3, warning, exception, died");
            int spawned = store.Message(store.MessageAt(0)).Group;
            Assert.AreEqual(3, state.GroupCount[spawned]);
            Assert.AreEqual(5, state.GroupLast[spawned]);
            int total = 0;
            foreach (int value in state.Timeline)
            {
                total += value;
            }

            Assert.AreEqual(6, total, "every visible log lands in one timeline bucket");
        }

        [Test]
        public void LevelsKeepTheirCountsWhenHidden()
        {
            var store = Sample();
            var state = Run(store, new FilterSpec { LevelMask = 1 << (int)LogLevel.Error });
            Assert.AreEqual(2, state.RowCount);
            Assert.AreEqual(3, state.LevelCounts[(int)LogLevel.Info], "a hidden level still counts what it would show");
        }

        [Test]
        public void SearchPlainRegexCaseAndStack()
        {
            var store = Sample();
            Assert.AreEqual(4, Run(store, new FilterSpec { Query = new LogQuery("player", false, false, false) }).RowCount,
                "case-insensitive, and rich text tags don't hide a match");
            Assert.AreEqual(0, Run(store, new FilterSpec { Query = new LogQuery("player", false, true, false) }).RowCount);
            Assert.AreEqual(1, Run(store, new FilterSpec { Query = new LogQuery(@"^Missing \w+ on", true, false, false) }).RowCount);
            Assert.AreEqual(0, Run(store, new FilterSpec { Query = new LogQuery("Game.cs", false, false, false) }).RowCount);
            Assert.AreEqual(5, Run(store, new FilterSpec { Query = new LogQuery("Game.cs", false, false, true) }).RowCount, "stack traces when asked");
            var broken = new LogQuery("(unclosed", true, false, false);
            Assert.IsNotNull(broken.Error);
            Assert.IsTrue(broken.IsEmpty, "an invalid expression filters nothing");
            var ranges = new List<(int start, int length)>();
            new LogQuery("pl", false, false, false).FindMatches("Player played", ranges);
            Assert.AreEqual(2, ranges.Count);
        }

        [Test]
        public void SourcesMutesAndTimeRange()
        {
            var store = Sample();
            string sdk = store.SourceOf(store.MessageAt(2)).Key;
            var hidden = new FilterSpec();
            hidden.HiddenSources.Add(sdk);
            var state = Run(store, hidden);
            Assert.AreEqual(5, state.RowCount);
            Assert.AreEqual(1, state.SourceCounts[store.SourceOf(store.MessageAt(2)).Id], "a hidden source still counts its logs");

            var muted = new FilterSpec();
            muted.Muted.Add("Player spawned");
            Assert.AreEqual(3, Run(store, muted).RowCount);

            var range = new FilterSpec { From = TimeSpan.TicksPerSecond * 2, To = TimeSpan.TicksPerSecond * 4 };
            state = Run(store, range);
            Assert.AreEqual(3, state.RowCount);
            Assert.AreEqual(6, Sum(state.Timeline), "the chart keeps showing the whole session");
        }

        [Test]
        public void ExtendMatchesAFullPass()
        {
            var store = Sample();
            var spec = new FilterSpec { Query = new LogQuery("player", false, false, false), Sort = GroupSort.Count, Grouped = true };
            var state = Run(store, spec, 0);
            var random = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                int kind = random.Next(4);
                string text = kind == 0 ? "Player spawned" : kind == 1 ? "Player " + random.Next(3) + " joined" : kind == 2 ? "Other" : "<b>Player</b> left";
                store.Add(TimeSpan.TicksPerSecond * (10 + i), text, i % 3 == 0 ? SdkStack : Stack, (LogVariant)random.Next(3));
            }

            Assert.IsTrue(state.Extend(store, store.Sources.All));
            state.SortGroups(store.View());
            var full = Run(store, spec, 0);
            Assert.AreEqual(full.RowCount, state.RowCount);
            Assert.AreEqual(full.GroupRowCount, state.GroupRowCount);
            for (int i = 0; i < full.RowCount; i++)
            {
                Assert.AreEqual(full.Rows[i], state.Rows[i]);
            }

            for (int i = 0; i < full.GroupRowCount; i++)
            {
                Assert.AreEqual(full.GroupCount[full.GroupRows[i]], state.GroupCount[state.GroupRows[i]], "same order by count");
            }

            CollectionAssert.AreEqual(full.LevelCounts, state.LevelCounts);
        }

        [Test]
        public void ExtendRefusesAfterTheStoreChangesShape()
        {
            var store = Sample();
            var state = Run(store, new FilterSpec());
            store.Clear(100);
            Assert.IsFalse(state.Extend(store, store.Sources.All));
        }

        [Test]
        public void AMillionLogsFilterQuickly()
        {
            var store = new LogStore { MaxOccurrences = 2_000_000 };
            var stacks = new[] { Stack, SdkStack };
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 1_000_000; i++)
            {
                store.Add(i * 1000L, "Message number " + (i % 1000), stacks[i % 2], (LogVariant)(i % 3));
            }

            double ingest = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var all = Run(store, new FilterSpec(), 400);
            double full = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var searched = Run(store, new FilterSpec { Query = new LogQuery("number 42", false, false, false) }, 400);
            double search = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var regex = Run(store, new FilterSpec { Query = new LogQuery(@"number 4\d\d$", true, false, false) }, 400);
            double regexMs = clock.Elapsed.TotalMilliseconds;
            Debug.Log($"[Mixer console tests] 1M logs: ingest {ingest:0} ms, full pass {full:0} ms, search {search:0} ms, regex {regexMs:0} ms");

            Assert.AreEqual(1_000_000, all.RowCount);
            Assert.AreEqual(11 * 1000, searched.RowCount, "42 and 420-429, a thousand times each");
            Assert.AreEqual(100 * 1000, regex.RowCount);
            Assert.Less(full, 3000, "a full pass over a million logs stays well under a few seconds even on a slow machine");
        }

        [Test]
        public void WhatHidesAgreesWithAFullPass()
        {
            var store = Sample();
            var specs = new List<FilterSpec>
            {
                new FilterSpec(),
                new FilterSpec { LevelMask = (1 << (int)LogLevel.Warning) | (1 << (int)LogLevel.Error) },
                new FilterSpec { Query = new LogQuery("player", false, false, false) },
                new FilterSpec { Query = new LogQuery("Game.cs", false, false, true) },
                new FilterSpec { From = TimeSpan.TicksPerSecond * 2, To = TimeSpan.TicksPerSecond * 4 }
            };
            var source = new FilterSpec();
            source.HiddenSources.Add(store.SourceOf(store.MessageAt(2)).Key);
            specs.Add(source);
            var muted = new FilterSpec { LevelMask = 1 << (int)LogLevel.Info };
            muted.Muted.Add("Player spawned");
            specs.Add(muted);
            foreach (var spec in specs)
            {
                var state = Run(store, spec);
                for (int i = 0; i < store.Count; i++)
                {
                    Assert.AreEqual(state.RowOf(i) >= 0, spec.WhatHides(store, i) == HiddenBy.None, "occurrence " + i);
                }
            }
        }

        [Test]
        public void WhatHidesNamesTheFiltersToTurnOff()
        {
            // A successful upload is a log, a failed one a warning: with logs turned off, the newest bar of the uploads
            // chart pointed at a row the list didn't have.
            var store = new LogStore();
            var failed = new TimingProfile { Kind = TimingKind.AvatarUpload, TotalMilliseconds = 146000, Subject = "Rexouium", Outcome = "upload failed" };
            var uploaded = new TimingProfile { Kind = TimingKind.AvatarUpload, TotalMilliseconds = 131000, Subject = "Rexouium" };
            int failedAt = store.Add(TimeSpan.TicksPerSecond * 10, failed.Message(), failed.Format(), LogVariant.Warning);
            int uploadedAt = store.Add(TimeSpan.TicksPerSecond * 20, uploaded.Message(), uploaded.Format(), LogVariant.Log);
            var warningsAndErrors = new FilterSpec { LevelMask = (1 << (int)LogLevel.Warning) | (1 << (int)LogLevel.Error) };
            Assert.AreEqual(HiddenBy.None, warningsAndErrors.WhatHides(store, failedAt));
            Assert.AreEqual(HiddenBy.Level, warningsAndErrors.WhatHides(store, uploadedAt));

            var several = new FilterSpec { LevelMask = 1 << (int)LogLevel.Error, Query = new LogQuery("failed", false, false, false), From = 1, To = TimeSpan.TicksPerSecond * 15 };
            several.HiddenSources.Add(store.SourceOf(store.MessageAt(uploadedAt)).Key);
            several.Muted.Add(uploaded.Message());
            Assert.AreEqual(HiddenBy.Level | HiddenBy.Search | HiddenBy.Source | HiddenBy.Muted | HiddenBy.Range, several.WhatHides(store, uploadedAt));

            Assert.AreEqual(HiddenBy.Gone, new FilterSpec().WhatHides(store, store.Count), "not in the store");
            const string text = "Assets/A.cs(1,1): error CS0103: The name 'x' does not exist";
            int first = store.AddCompile(TimeSpan.TicksPerSecond * 30, text, true, store.AssemblyId("Asm"), null, 0, 0);
            int again = store.AddCompile(TimeSpan.TicksPerSecond * 40, text, true, store.AssemblyId("Asm"), null, 0, 0);
            Assert.AreEqual(HiddenBy.Gone, new FilterSpec().WhatHides(store, first), "reported again since");
            Assert.AreEqual(HiddenBy.None, new FilterSpec().WhatHides(store, again));
            store.ResolveCompile(-1, errorsOnly: false);
            Assert.AreEqual(HiddenBy.Gone, new FilterSpec().WhatHides(store, again), "fixed since");
        }

        [Test]
        public void AMovedChartAxisCountsLikeAFullPass()
        {
            var store = Sample();
            var spec = new FilterSpec { Grouped = true, LevelMask = (1 << (int)LogLevel.Info) | (1 << (int)LogLevel.Error) };
            var state = Run(store, spec, 12);
            long to = store.LastTime + TimeSpan.TicksPerSecond * 30;
            var pass = state.ComputeChart(store.View(), store.FirstTime, to, 20, null, null);

            // Logs that arrive while the chart pass runs are counted when it is taken.
            store.Add(TimeSpan.TicksPerSecond * 7, "Player spawned", Stack, LogVariant.Log);
            store.Add(TimeSpan.TicksPerSecond * 8, "A new error", Stack, LogVariant.Error);
            Assert.IsTrue(state.Extend(store, store.Sources.All));
            Assert.IsTrue(state.AdoptChart(pass, store.View(), out _));

            var full = FilterState.Compute(store.View(), spec, store.Sources.All, null, store.FirstTime, to, 20, null);
            Assert.AreEqual(full.Buckets, state.Buckets);
            Assert.AreEqual(full.ChartTo, state.ChartTo);
            CollectionAssert.AreEqual(full.Timeline, state.Timeline);
            Assert.AreEqual(full.SparkGroups, state.SparkGroups);
            CollectionAssert.AreEqual(full.Sparks, state.Sparks);
            Assert.AreEqual(full.SparkMax, state.SparkMax);
            Assert.AreEqual(full.RowCount, state.RowCount, "rows were not touched");

            // A chart computed before the store changed shape is dropped.
            var stale = state.ComputeChart(store.View(), store.FirstTime, to, 20, null, null);
            store.Clear(0);
            Assert.IsFalse(state.AdoptChart(stale, store.View(), out _));
        }

        [Test]
        public void SearchResultsCarryOverWhileTyping()
        {
            var store = Sample();
            var known = new byte[] { 0, 2, 1, 2, 1 };
            var nul = new LogQuery("pla", false, false, false);
            var typedOn = new LogQuery("PLAY", false, false, false);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 0, 1 }, typedOn.Reuse(nul, known), "only former matches are tested again");
            var deleted = new LogQuery("p", false, false, false);
            CollectionAssert.AreEqual(new byte[] { 0, 2, 0, 2, 0 }, deleted.Reuse(nul, known), "former matches still match");
            CollectionAssert.AreEqual(known, new LogQuery("pla", false, false, false).Reuse(nul, known));
            Assert.IsNull(new LogQuery("xyz", false, false, false).Reuse(nul, known), "an unrelated query starts over");
            Assert.IsNull(new LogQuery("PLAY", false, true, false).Reuse(nul, known), "another case rule starts over");
            Assert.IsNull(new LogQuery("play", true, false, false).Reuse(nul, known), "so does a regular expression");

            // Typing on with real passes gives what a fresh search gives.
            var first = Run(store, new FilterSpec { Query = new LogQuery("pl", false, false, false) });
            var spec = new FilterSpec { Query = new LogQuery("player s", false, false, false) };
            var carried = FilterState.Compute(store.View(), spec, store.Sources.All, spec.Query.Reuse(first.Spec.Query, first.KnownTextMatches),
                store.FirstTime, store.LastTime, 10, null);
            var fresh = Run(store, spec);
            Assert.AreEqual(3, fresh.RowCount);
            Assert.AreEqual(fresh.RowCount, carried.RowCount);
            CollectionAssert.AreEqual(fresh.KnownTextMatches, carried.KnownTextMatches);
        }

        [Test]
        public void BigRegexPassesUseOneCompiledExpression()
        {
            var query = new LogQuery("pl(a)+yer", true, false, false);
            Assert.AreSame(query.Regex, query.RegexFor(10), "small passes interpret");
            var compiled = query.RegexFor(LogQuery.CompileAbove);
            Assert.AreNotSame(query.Regex, compiled);
            Assert.AreSame(compiled, new LogQuery("pl(a)+yer", true, false, true).RegexFor(LogQuery.CompileAbove), "compiled once per pattern");
            Assert.IsTrue(query.Matches("The PLAAYER spawned", compiled));
            Assert.IsFalse(query.Matches("The plyer spawned", compiled));
        }

        [Test]
        public void ParallelSearchFindsWhatTheSerialOneFinds()
        {
            var store = new LogStore();
            for (int i = 0; i < 25_000; i++)
            {
                store.Add(TimeSpan.TicksPerSecond + i, (i % 7 == 0 ? "Needle " : "Hay ") + i, i % 3 == 0 ? SdkStack : Stack, LogVariant.Log);
            }

            var spec = new FilterSpec { Query = new LogQuery("needle", false, false, true) };
            var serial = Run(store, spec);
            var parallel = FilterState.Compute(store.View(), spec, store.Sources.All, null, store.FirstTime, store.LastTime, 10, () => false);
            Assert.AreEqual((25_000 + 6) / 7, serial.RowCount);
            Assert.AreEqual(serial.RowCount, parallel.RowCount);
            CollectionAssert.AreEqual(serial.KnownTextMatches, parallel.KnownTextMatches);
        }

        [Test]
        public void GroupRowsAreFoundWithoutAScan()
        {
            var store = Sample();
            foreach (var sort in new[] { GroupSort.FirstSeen, GroupSort.LastSeen, GroupSort.Count, GroupSort.Level })
            {
                var state = Run(store, new FilterSpec { Grouped = true, Sort = sort });
                store.Add(TimeSpan.TicksPerSecond * 9, "A group that came later", Stack, LogVariant.Warning);
                Assert.IsTrue(state.Extend(store, store.Sources.All));
                state.SortGroups(store.View());
                for (int row = 0; row < state.GroupRowCount; row++)
                {
                    Assert.AreEqual(row, state.GroupRowOf(state.GroupRows[row]), sort.ToString());
                }

                Assert.AreEqual(-1, state.GroupRowOf(store.GroupCount + 5));
            }
        }

        [Test]
        public void RegexPassesNeverRunOnTheCallersThread()
        {
            var store = Sample();
            var runner = new FilterRunner();
            var spec = new FilterSpec { Query = new LogQuery("pl(a)+yer", true, false, false) };
            Assert.IsNull(runner.Request(store, spec, null, store.FirstTime, store.LastTime, 10, allowAsync: true), "even a tiny store goes to a worker");
            FilterState result = null;
            var watch = Stopwatch.StartNew();
            while (result == null && watch.ElapsedMilliseconds < 10_000)
            {
                System.Threading.Thread.Sleep(5);
                result = runner.TryTake();
            }

            Assert.IsNotNull(result);
            Assert.AreEqual(4, result.RowCount, "Player spawned ×3 and Player died");
            Assert.IsNotNull(runner.Request(store, new FilterSpec(), null, store.FirstTime, store.LastTime, 10, allowAsync: true), "plain small passes stay in place");
        }

        [Test]
        public void ASearchOutOfTimeGoesOnInSlicesInsteadOfMissing()
        {
            var store = new LogStore();
            for (int i = 0; i < 3000; i++)
            {
                store.Add(TimeSpan.TicksPerSecond + i, (i % 3 == 0 ? "Player " : "Other ") + i, Stack, LogVariant.Log);
            }

            var spec = new FilterSpec { Query = new LogQuery("pl(a)+yer", true, false, false) };
            var full = Run(store, spec);
            Assert.IsFalse(full.SearchPending);
            long saved = FilterState.SearchBudgetMs;
            try
            {
                FilterState.SearchBudgetMs = 0;
                var partial = Run(store, spec);
                Assert.IsTrue(partial.SearchPending, "nothing could be tested in time");
                Assert.Less(partial.RowCount, full.RowCount, "and nothing was taken for a miss either");
                for (int slice = 0; partial.SearchPending && slice < 10_000; slice++)
                {
                    Assert.IsTrue(partial.Extend(store, store.Sources.All));
                }

                Assert.IsFalse(partial.SearchPending);
                Assert.AreEqual(full.RowCount, partial.RowCount);
                Assert.AreEqual(full.Covered, partial.Covered);
                Assert.AreEqual(0, partial.SearchTimeouts);
            }
            finally
            {
                FilterState.SearchBudgetMs = saved;
            }
        }

        private static int Sum(int[] values)
        {
            int total = 0;
            foreach (int value in values)
            {
                total += value;
            }

            return total;
        }
    }
}
