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
