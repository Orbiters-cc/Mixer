using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    /// <summary>
    /// The import of Unity's console history, on rows a test gives (<see cref="IConsoleSource"/>), against round 1's
    /// import (<see cref="ReferenceConsoleHistory"/>, verbatim): under the limits the store must hold exactly what
    /// round 1's import gave, however the import is split into updates, with live logs between updates and with a
    /// script reload in the middle; over the limits, the ceiling holds after every update, the newest history and
    /// the live logs are kept.
    /// </summary>
    public class ConsoleHistoryTests
    {
        internal static readonly DateTime EditorStart = new DateTime(2026, 11, 9, 23, 40, 0, DateTimeKind.Local);

        internal sealed class Row
        {
            public string Condition;
            public string Stack = string.Empty;
            public int Mode;
            public string File;
            public int Line;
            public int InstanceId;
            /// <summary>Seconds of the day, or -1 for a row without a time.</summary>
            public int Seconds = -1;
        }

        internal sealed class Rows : IConsoleRows
        {
            private readonly List<Row> rows;

            public Rows(List<Row> rows) => this.rows = rows;

            public int Count => rows.Count;

            private static string MessageOf(Row row) => row.Stack.Length > 0 ? row.Condition + "\n" + row.Stack : row.Condition;

            public string Lines(int row, int lines, out int mode)
            {
                var r = rows[row];
                mode = r.Mode;
                string message = MessageOf(r);
                int cut = -1;
                for (int i = 0, seen = 0; i < message.Length; i++)
                {
                    if (message[i] == '\n' && ++seen == lines)
                    {
                        cut = i;
                        break;
                    }
                }

                string shown = cut >= 0 ? message.Substring(0, cut) : message;
                if (r.Seconds < 0)
                {
                    return shown;
                }

                var time = TimeSpan.FromSeconds(r.Seconds);
                return "[" + time.Hours.ToString("00") + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00") + "] " + shown;
            }

            public bool TryRead(int row, out UnityConsoleRow result)
            {
                var r = rows[row];
                string message = MessageOf(r);
                result = new UnityConsoleRow
                {
                    Message = message,
                    CallstackStart = r.Stack.Length > 0 ? r.Condition.Length + 1 : message.Length,
                    Mode = r.Mode,
                    File = r.File,
                    Line = r.Line,
                    InstanceId = r.InstanceId
                };
                return true;
            }

            public void Dispose()
            {
            }
        }

        internal sealed class Source : IConsoleSource
        {
            public List<Row> RowList;

            public Source(List<Row> rows) => RowList = rows;

            public bool Available => true;
            public IConsoleRows Open() => new Rows(RowList);
            public bool TakeAll() => false;
            public void ReleaseAll()
            {
            }

            public bool WindowMayShow => false;
        }

        // A console under the limits: repeated messages, multi-line ones sharing their first two lines, one condition
        // from several stacks, compile messages, a midnight crossing, rows without a time, contexts.
        internal static List<Row> RandomConsole(int seed)
        {
            var random = new Random(seed);
            var rows = new List<Row>();
            int seconds = 23 * 3600 + 41 * 60 + random.Next(600);
            int count = random.Next(200, 900);
            for (int i = 0; i < count; i++)
            {
                seconds += random.Next(0, 4) == 0 ? 0 : random.Next(1, 20);
                if (seconds >= 86400)
                {
                    seconds -= 86400;
                }

                var row = new Row { Seconds = random.Next(25) == 0 ? -1 : seconds };
                switch (random.Next(8))
                {
                    case 0:
                        row.Condition = "Shared first line\nShared second line\nThird line " + random.Next(4);
                        break;
                    case 1:
                        row.Condition = "Same condition";
                        row.Stack = "Game:Tick () (at Assets/Game" + random.Next(3) + ".cs:" + random.Next(1, 4) + ")\n";
                        break;
                    case 2:
                        row.Condition = "Assets/A" + random.Next(3) + ".cs(3,4): error CS0103: The name 'x' does not exist";
                        row.Mode = UnityConsole.ModeScriptCompileError;
                        row.File = "Assets/A.cs";
                        row.Line = 3;
                        break;
                    case 3:
                        row.Condition = "Warning " + random.Next(5);
                        row.Mode = UnityConsole.ModeScriptingWarning;
                        row.Stack = "UnityEngine.Debug:LogWarning (object)\nTool:Run () (at Packages/orbiters.toolkit/Editor/Tool.cs:9)\n";
                        break;
                    case 4:
                        row.Condition = "Error " + random.Next(3);
                        row.Mode = UnityConsole.ModeScriptingError;
                        row.InstanceId = random.Next(3) == 0 ? 1000 + i : 0;
                        break;
                    default:
                        row.Condition = random.Next(3) == 0 ? "Distinct " + i : "Repeated " + random.Next(6);
                        break;
                }

                rows.Add(row);
            }

            return rows;
        }

        // Every occurrence as an export would read it: time, flags, level, source, texts, file, the message's count and
        // its group's key and count; then the messages with occurrences in id order (what breaks report ties).
        internal static string Content(LogStore store, bool messageOrder = true)
        {
            var text = new StringBuilder();
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                int m = store.MessageAt(id);
                ref var message = ref store.Message(m);
                store.TryGetContext(id, out int context);
                text.Append(store.TimeAt(id)).Append('|').Append((int)store.FlagsAt(id)).Append('|').Append(message.Variant).Append('|')
                    .Append(store.SourceOf(m).Name).Append('|').Append(store.Texts[message.Text]).Append('|').Append(store.Texts[message.Stack]).Append('|')
                    .Append(store.Texts[message.Callsite]).Append('|').Append(store.Texts[message.File]).Append(':').Append(message.Line).Append('|')
                    .Append(message.Count).Append('|').Append(store.Texts[store.Group(message.Group).Key]).Append('#').Append(store.Group(message.Group).Count)
                    .Append('|').Append(context).Append('\n');
            }

            if (messageOrder)
            {
                text.Append("--\n");
                for (int m = 0; m < store.MessageCount; m++)
                {
                    if (store.Message(m).Count > 0)
                    {
                        text.Append(store.Texts[store.Message(m).Text]).Append('|').Append(store.Texts[store.Message(m).Stack]).Append('\n');
                    }
                }
            }

            return text.ToString();
        }

        internal static LogStore Reference(List<Row> rows, Action<LogStore> live = null)
        {
            ReferenceConsoleHistory.EditorStartForTests = EditorStart;
            var store = new LogStore(LogStore.HistoryRoom);
            var import = new ReferenceConsoleHistory(0, rows.Count, ReferenceConsoleHistory.Placement.Before, 1000) { Source = new Source(rows) };
            while (!import.Step(store, 1e9))
            {
            }

            live?.Invoke(store);
            return store;
        }

        private static ConsoleHistory NewImport(List<Row> rows, int from, int to) =>
            new ConsoleHistory(from, to, ConsoleHistory.Placement.Before, 1000) { Source = new Source(rows), EditorStart = EditorStart };

        // The messages with occurrences, in id order, keeping only those `keep` selects (history or live ones).
        private static string MessageOrder(LogStore store, Func<string, bool> keep)
        {
            var text = new StringBuilder();
            for (int m = 0; m < store.MessageCount; m++)
            {
                string condition = store.Texts[store.Message(m).Text];
                if (store.Message(m).Count > 0 && keep(condition))
                {
                    text.Append(condition).Append('|').Append(store.Texts[store.Message(m).Stack]).Append('\n');
                }
            }

            return text.ToString();
        }

        private static bool IsLive(string condition) => condition.StartsWith("Live ", StringComparison.Ordinal);

        // A script reload through the real paths: the store through the session file (staged messages saved without
        // occurrences), the import through SnapshotInfo and the head, then ConsoleHistory.Resume.
        private static ConsoleHistory Reload(ref LogStore store, ConsoleHistory import, Source source, string folder)
        {
            var info = new SnapshotInfo();
            import.SaveTo(info, source.RowList.Count > 0 && import.To > 0 ? TextOf(source.RowList[import.To - 1]) : string.Empty);
            var saving = new SessionFile(folder);
            saving.Save(store, info, 1, 2);
            saving.Close();
            var loading = new SessionFile(folder);
            store = loading.Load(1, 2, out var restoredInfo, out string problem);
            Assert.IsNull(problem, problem);
            store.CompactInBackground = true;
            var resumed = ConsoleHistory.Resume(restoredInfo, source);
            return resumed;
        }

        // A row's anchor as Unity's console would give it (first two lines, no time).
        private static string TextOf(Row row)
        {
            string message = row.Stack.Length > 0 ? row.Condition + "\n" + row.Stack : row.Condition;
            int first = message.IndexOf('\n');
            int second = first >= 0 ? message.IndexOf('\n', first + 1) : -1;
            return second >= 0 ? message.Substring(0, second) : message;
        }

        private static string NewFolder() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mixer-session-" + Guid.NewGuid().ToString("N"));

        private static void DeleteFolder(string folder)
        {
            try
            {
                if (System.IO.Directory.Exists(folder) && System.IO.Path.GetFileName(folder).StartsWith("mixer-session-", StringComparison.Ordinal))
                {
                    System.IO.Directory.Delete(folder, true);
                }
            }
            catch (System.IO.IOException)
            {
                // A text read after its session closed reopened the file: the temporary folder stays.
            }
        }

        [Test]
        public void UnderTheLimitsTheImportGivesRound1sStore([Values(0, 1, 2, 3)] int mode)
        {
            for (int seed = 1; seed <= 30; seed++)
            {
                var rows = RandomConsole(seed);
                var random = new Random(seed * 7);
                // Mode 2: a reload while rows are read (one row per update, after one update per row of measure);
                // mode 3: while the console is measured.
                int readAt = random.Next(2, rows.Count);
                int reloadAt = mode == 2 ? rows.Count + readAt : mode == 3 ? random.Next(2, rows.Count) : -1;
                if (mode == 2)
                {
                    // A row without a time right after the reload point: its time must not move.
                    rows[readAt].Seconds = -1;
                }

                var source = new Source(rows);
                var liveTexts = new List<string>();
                var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
                var import = new ConsoleHistory(0, rows.Count, ConsoleHistory.Placement.Before, 1000) { Source = source, EditorStart = EditorStart };
                int updates = 0;
                string folder = NewFolder();
                try
                {
                    while (!import.Step(store, mode == 0 ? 1e9 : 0d))
                    {
                        updates++;
                        if (mode >= 1 && random.Next(4) == 0)
                        {
                            // Live logs between updates (never the history's texts: a message's file comes from whoever filed it first).
                            string live = "Live " + liveTexts.Count;
                            liveTexts.Add(live);
                            store.Add(TimeSpan.TicksPerDay * 800_000 + liveTexts.Count, live, string.Empty, LogVariant.Log);
                        }

                        if (updates == reloadAt)
                        {
                            import = Reload(ref store, import, source, folder);
                        }

                        store.StepMaintenance();
                        Assert.Less(updates, 100_000);
                    }
                }
                finally
                {
                    DeleteFolder(folder);
                }

                var reference = Reference(rows, s =>
                {
                    for (int i = 0; i < liveTexts.Count; i++)
                    {
                        s.Add(TimeSpan.TicksPerDay * 800_000 + 1 + i, liveTexts[i], string.Empty, LogVariant.Log);
                    }
                });
                string label = "seed " + seed + ", mode " + mode;
                Assert.IsTrue(import.Finished && !import.Failed, label);
                Assert.AreEqual(0, store.StagedCount);
                Assert.AreEqual(Content(reference, messageOrder: false), Content(store, messageOrder: false), label);
                if (liveTexts.Count == 0 && reloadAt < 0)
                {
                    Assert.AreEqual(Content(reference), Content(store), label);
                }
                else
                {
                    // Live logs interleave with the import's interning (and a reload re-interns): the history's messages keep
                    // their order among themselves, the live ones theirs.
                    Assert.AreEqual(MessageOrder(reference, c => !IsLive(c)), MessageOrder(store, c => !IsLive(c)), label);
                    Assert.AreEqual(MessageOrder(reference, IsLive), MessageOrder(store, IsLive), label);
                }

                if (mode >= 1)
                {
                    Assert.Greater(updates, 1, "split into many updates");
                }
            }
        }

        // A reload while the rows go in front (the newest ones are in the store already) imports the rest once, in order.
        [Test]
        public void AReloadWhileRowsGoInFrontImportsEachRowOnce()
        {
            var rows = new List<Row>();
            for (int i = 0; i < 3 * ConsoleHistory.MaxPublishedPerUpdate + 100; i++)
            {
                rows.Add(new Row { Condition = "Row " + (i % 50), Seconds = i == 2 * ConsoleHistory.MaxPublishedPerUpdate ? -1 : 23 * 3600 + 41 * 60 + i / 100 });
            }

            var source = new Source(rows);
            var store = new LogStore(LogStore.HistoryRoom);
            var import = new ConsoleHistory(0, rows.Count, ConsoleHistory.Placement.Before, 1000) { Source = source, EditorStart = EditorStart };
            // Read everything, then put one batch in front, then reload.
            while (store.StagedCount < rows.Count)
            {
                import.Step(store, 1e9);
            }

            import.Step(store, 0d);
            Assert.AreEqual(ConsoleHistory.MaxPublishedPerUpdate, store.Count, "one batch in front");
            Assert.AreEqual(rows.Count - ConsoleHistory.MaxPublishedPerUpdate, import.To);
            string folder = NewFolder();
            try
            {
                var resumed = Reload(ref store, import, source, folder);
                while (!resumed.Step(store, 1e9))
                {
                }
            }
            finally
            {
                DeleteFolder(folder);
            }

            Assert.AreEqual(Content(Reference(rows), messageOrder: false), Content(store, messageOrder: false));
        }

        // The rows Unity adds during the import are live logs, past the range; a console cleared (and refilled to as
        // many rows) between two updates fails the import cleanly.
        [Test]
        public void RowsAddedOrAConsoleClearedMeanwhile([Values(false, true)] bool refilled)
        {
            var rows = RandomConsole(5);
            int count = rows.Count;
            var store = new LogStore(LogStore.HistoryRoom);
            var import = NewImport(rows, 0, count);
            import.Step(store, 0d);
            rows.Add(new Row { Condition = "Added during the import" });
            while (!import.Step(store, 1e9))
            {
            }

            Assert.AreEqual(Content(Reference(rows.GetRange(0, count)), false), Content(store, false), "the added row is not history");

            var console = RandomConsole(6);
            var source = new Source(console);
            var other = new LogStore(LogStore.HistoryRoom);
            var failing = new ConsoleHistory(0, console.Count, ConsoleHistory.Placement.Before, 1000) { Source = source, EditorStart = EditorStart };
            // Measured whole, then some rows read and staged.
            for (int update = 0; update < console.Count + 20; update++)
            {
                failing.Step(other, 0d);
            }

            Assert.Greater(other.StagedCount, 0);
            var replacement = refilled ? RandomConsole(7) : new List<Row>();
            while (refilled && replacement.Count < console.Count)
            {
                replacement.AddRange(RandomConsole(8 + replacement.Count));
            }

            source.RowList = replacement;
            Assert.IsTrue(failing.Step(other, 1e9));
            Assert.IsTrue(failing.Failed, refilled ? "another console's rows are never combined with these" : "Unity's console was cleared");
            Assert.AreEqual(0, other.StagedCount, "staged rows let go");
            Assert.AreEqual(0, other.Count);
        }

        // Over the budget, with no live log: the measure starts the import where the newest rows fit, so nothing is trimmed
        // or compacted; times are those round 1's forward reading gives for the same rows; the ceiling holds throughout.
        [Test]
        public void AnOverBudgetConsoleStartsWhereItsNewestRowsFit()
        {
            var rows = new List<Row>();
            string padding = new string('h', 400);
            int seconds = 23 * 3600 + 41 * 60;
            for (int i = 0; i < 3000; i++)
            {
                seconds = (seconds + 3) % 86400;
                rows.Add(new Row { Condition = "History " + i + " " + padding, Seconds = seconds });
            }

            var store = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 18, CompactInBackground = true };
            var import = NewImport(rows, 0, rows.Count);
            // Now: shortly after the last row (the console's rows crossed midnight).
            import.Now = EditorStart.Date.AddDays(1).AddSeconds(seconds + 60);

            // Where it should start: the newest rows whose cost (the store's own InternCost) fits the budget less an eighth.
            long room = store.MaxTextChars - store.MaxTextChars / 8 - store.Texts.TotalChars;
            long used = 0;
            int expected = rows.Count;
            while (expected > 0 && used + store.InternCost(rows[expected - 1].Condition, string.Empty, LogVariant.Log) <= room)
            {
                used += store.InternCost(rows[expected - 1].Condition, string.Empty, LogVariant.Log);
                expected--;
            }

            int compactions = store.CompactionsStarted;
            int updates = 0;
            while (!import.Step(store, 0.05d))
            {
                Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling, "after update " + updates);
                Assert.Less(++updates, 100_000);
            }

            Assert.Greater(expected, 0, "the console is over the budget");
            Assert.AreEqual(expected, import.From, "the first row read");
            Assert.AreEqual(compactions, store.CompactionsStarted, "no compaction ran during the import");
            Assert.AreEqual(rows.Count - expected, store.Count, "every row that fits, none trimmed");
            Assert.LessOrEqual(store.Texts.TotalChars, store.MaxTextChars);

            // The same times as reading the whole console forward from the editor's start.
            var whole = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 26 };
            ReferenceConsoleHistory.EditorStartForTests = EditorStart;
            var reference = new ReferenceConsoleHistory(0, rows.Count, ReferenceConsoleHistory.Placement.Before, 1000) { Source = new Source(rows) };
            while (!reference.Step(whole, 1e9))
            {
            }

            for (int k = 0; k < store.Count; k++)
            {
                Assert.AreEqual(whole.TimeAt(whole.FirstId + expected + k), store.TimeAt(store.FirstId + k), "row " + (expected + k));
                Assert.AreEqual(rows[expected + k].Condition, store.Texts[store.Message(store.MessageAt(store.FirstId + k)).Text]);
            }
        }

        // Old long messages followed by newer short ones sharing their first two lines: the import keeps the oldest row
        // of each (the long one), so the measure must count that one. On this console the previous rule (the cost of the
        // newest row of each signature, the first one met going back) accepts every row, long ones included, and the
        // import would then go past the budget; the rule in use starts after some of them.
        [Test]
        public void TheMeasureCountsTheRowTheImportKeeps()
        {
            var rows = new List<Row>();
            int seconds = 36000;
            const int signatures = 10;
            for (int k = 0; k < signatures; k++)
            {
                rows.Add(new Row { Condition = "Sig " + k + " first\nSig " + k + " second\n" + new string('x', 8000), Seconds = seconds++ });
            }

            for (int i = 0; i < 430; i++)
            {
                rows.Add(new Row { Condition = "Filler " + i + " " + new string('f', 400), Seconds = seconds++ });
            }

            for (int k = 0; k < signatures; k++)
            {
                rows.Add(new Row { Condition = "Sig " + k + " first\nSig " + k + " second\nshort", Seconds = seconds++ });
            }

            var store = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 18, CompactInBackground = true };
            int previous = MeasureStart(store, rows, newestOnly: true);
            int current = MeasureStart(store, rows, newestOnly: false);
            Assert.AreEqual(0, previous, "the previous rule accepts every row, the long ones included");
            Assert.Greater(current, 0, "the rule in use leaves some long rows out");
            Assert.Less(current, signatures, "and keeps others: a signature crosses the boundary");
            // Signature `current`: its long row is inside the kept range, its newer short row was measured first.
            int shortRow = rows.FindIndex(r => r.Condition == "Sig " + current + " first\nSig " + current + " second\nshort");
            Assert.Greater(shortRow, current);

            var import = NewImport(rows, 0, rows.Count);
            import.Now = EditorStart.Date.AddDays(1).AddHours(12);
            int compactions = store.CompactionsStarted;
            int updates = 0;
            while (!import.Step(store, 0.05d))
            {
                Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling, "after update " + updates);
                Assert.Less(++updates, 100_000);
            }

            Assert.IsFalse(import.Failed);
            Assert.AreEqual(current, import.From, "the import starts where the rule in use says");
            Assert.AreEqual(compactions, store.CompactionsStarted, "no compaction started");
            Assert.LessOrEqual(store.Texts.TotalChars, store.MaxTextChars, "within the budget: nothing to trim");
            Assert.AreEqual(rows.Count - import.From, store.Count, "every row read is in the store");
        }

        // The measure over these rows by the rule in use (largest cost per signature) or the previous one (the cost of a
        // signature's newest row only), with the store's own InternCost: the first row the import would read.
        private static int MeasureStart(LogStore store, List<Row> rows, bool newestOnly)
        {
            long room = store.MaxTextChars - store.MaxTextChars / 8 - store.Texts.TotalChars;
            long rowRoom = store.MaxOccurrences - store.MaxOccurrences / 8 - (long)store.Total;
            var counted = new Dictionary<string, long>();
            long used = 0;
            long kept = 0;
            for (int r = rows.Count - 1; r >= 0; r--)
            {
                var row = rows[r];
                string signature = TextOf(row);
                long cost = store.InternCost(row.Condition, row.Stack, UnityConsole.VariantOf(row.Mode), row.File, row.Line);
                counted.TryGetValue(signature, out long before);
                long extra = newestOnly ? counted.ContainsKey(signature) ? 0 : cost : Math.Max(0, cost - before);
                if (used + extra > room || kept + 1 > rowRoom)
                {
                    return r + 1;
                }

                used += extra;
                counted[signature] = before + extra;
                kept++;
            }

            return 0;
        }

        // InternCost is never less than what Intern really adds to the texts, on random inputs: long paths, compiler
        // messages (location in the text or given), timing and command logs, stacks with many frames, rich text, empty
        // fields, both groupings, small and default budgets.
        [Test]
        public void InternCostIsNeverLessThanWhatInternAdds()
        {
            var random = new Random(3);
            string Path(int length) => "Assets/" + new string('p', Math.Max(1, length)) + ".cs";
            for (int i = 0; i < 2000; i++)
            {
                var store = new LogStore { MaxTextChars = random.Next(2) == 0 ? 1 << 16 : 1 << 24 };
                if (random.Next(2) == 0)
                {
                    store.SetGrouping(GroupingMode.SimilarText);
                }

                string condition = random.Next(10) == 0 ? string.Empty : (random.Next(3) == 0 ? "<b>rich</b> " : string.Empty) + "Message " + random.Next(100) +
                                   " at 0x7ff6a2b4c010 " + new string('m', random.Next(0, 5000));
                string stack = string.Empty;
                string file = null;
                int line = random.Next(0, 300);
                var variant = LogVariant.Log;
                switch (random.Next(6))
                {
                    case 0:
                        variant = LogVariant.CompileError;
                        condition = Path(random.Next(1, 12000)) + "(" + line + ",4): error CS0103: " + condition;
                        file = random.Next(2) == 0 ? Path(random.Next(1, 12000)) : null;
                        break;
                    case 1:
                        var profile = new TimingProfile { Kind = TimingKind.ScriptReload, TotalMilliseconds = random.Next(1, 9000) };
                        for (int p = 0; p < random.Next(0, 30); p++)
                        {
                            profile.Parts.Add(new TimingPart("Source " + p + new string('s', random.Next(0, 400)), "Phase", "Label " + p, random.Next(1, 500)));
                        }

                        condition = profile.Message();
                        stack = profile.Format();
                        break;
                    case 2:
                        stack = StackTraces.OutputPrefix + random.Next(1, 90) + " ms in " + Path(random.Next(1, 3000)) + "\n" + new string('o', random.Next(0, 3000));
                        break;
                    case 3:
                        var frames = new StringBuilder();
                        for (int f = 0; f < random.Next(1, 200); f++)
                        {
                            frames.Append("Ns.Type" + f + ":Method" + f + " () (at " + Path(random.Next(1, 4000)) + ":" + f + ")\n");
                        }

                        stack = frames.ToString();
                        break;
                    case 4:
                        file = Path(random.Next(1, 12000));
                        variant = LogVariant.Error;
                        break;
                    default:
                        stack = random.Next(2) == 0 ? string.Empty : "UnityEngine.Debug:Log (object)\n";
                        break;
                }

                long before = store.Texts.TotalChars;
                long cost = store.InternCost(condition, stack, variant, file, line, 4);
                store.Intern(condition, stack, variant, 0, file, line, 4);
                long growth = store.Texts.TotalChars - before;
                Assert.GreaterOrEqual(cost, growth, "input " + i + " (" + variant + ")");
            }

            // The reviewer's case: a one-character compiler message with a 10,000-character path, on a 65,536 budget.
            var small = new LogStore { MaxTextChars = 1 << 16 };
            string path = Path(10_000);
            long start = small.Texts.TotalChars;
            long counted = small.InternCost("x", string.Empty, LogVariant.CompileError, path, 1, 1);
            small.Intern("x", string.Empty, LogVariant.CompileError, 0, path, 1, 1);
            Assert.GreaterOrEqual(counted, small.Texts.TotalChars - start);
            Assert.LessOrEqual(small.Texts.TotalChars - start, small.MaxTextChars / 4, "one log's derived texts are cut too");
        }

        // A console cleared and refilled to the same count, ending with the same row, between two slices of a fresh import
        // (no Resume): the import fails cleanly, while measuring and while reading.
        [Test]
        public void AConsoleRefilledWithTheSameCountAndLastRowFails([Values(false, true)] bool reading)
        {
            var console = RandomConsole(11);
            var source = new Source(console);
            var store = new LogStore(LogStore.HistoryRoom);
            var import = new ConsoleHistory(0, console.Count, ConsoleHistory.Placement.Before, 1000) { Source = source, EditorStart = EditorStart };
            int updates = reading ? console.Count + 10 : 10;
            for (int update = 0; update < updates; update++)
            {
                Assert.IsFalse(import.Step(store, 0d));
            }

            Assert.AreEqual(reading, store.StagedCount > 0, reading ? "rows are being read" : "still measuring");
            var other = RandomConsole(12);
            while (other.Count < console.Count)
            {
                other.AddRange(RandomConsole(13 + other.Count));
            }

            other = other.GetRange(0, console.Count);
            other[other.Count - 1] = console[console.Count - 1];
            source.RowList = other;
            Assert.IsTrue(import.Step(store, 1e9));
            Assert.IsTrue(import.Failed, "another console's rows are never combined with these");
            Assert.AreEqual(0, store.StagedCount);
            Assert.AreEqual(0, store.Count);
        }

        // Live logs flooding during an over-budget import: the safety net (trim, synchronous compaction) keeps the ceiling,
        // the newest history and the live logs.
        [Test]
        public void AnImportLargerThanTheBudgetKeepsTheCeilingAndTheNewestLogs()
        {
            var rows = new List<Row>();
            string padding = new string('h', 400);
            for (int i = 0; i < 3000; i++)
            {
                rows.Add(new Row { Condition = "History " + i + " " + padding, Seconds = 36000 + i / 10 });
            }

            var store = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 18, CompactInBackground = true };
            var import = NewImport(rows, 0, rows.Count);
            import.Now = EditorStart.Date.AddDays(1).AddHours(12);
            var live = new List<int>();
            string livePadding = new string('l', 300);
            int steps = 0;
            while (!import.Step(store, 0.05d))
            {
                Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling, "after update " + steps);
                for (int i = 0; i < 5; i++)
                {
                    live.Add(store.Add(TimeSpan.TicksPerDay * 800_000 + steps * 100 + i, "Live " + steps + " " + i + " " + livePadding, string.Empty, LogVariant.Log));
                    Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling);
                }

                store.StepMaintenance();
                steps++;
                Assert.Less(steps, 100_000);
            }

            Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling);
            Assert.Greater(steps, 1, "the import went an update at a time");
            Assert.IsFalse(import.Failed);

            // Live logs: the newest are there, as logged (older ones may be trimmed: history put in front after a trim
            // takes their ids, and says so with Emptied, so only the newest ids are checked).
            for (int k = live.Count - 5; k < live.Count; k++)
            {
                Assert.IsTrue(store.Contains(live[k]));
                StringAssert.StartsWith("Live ", store.Texts[store.Message(store.MessageAt(live[k])).Text]);
            }

            // History: what is left is the newest, in order, before the live logs.
            int previousRow = -1;
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                string text = store.Texts[store.Message(store.MessageAt(id)).Text];
                if (text.StartsWith("History ", StringComparison.Ordinal))
                {
                    int row = int.Parse(text.Substring(8, text.IndexOf(' ', 8) - 8));
                    Assert.Greater(row, previousRow, "history in order");
                    previousRow = row;
                }

                if (id > store.FirstId)
                {
                    Assert.LessOrEqual(store.TimeAt(id - 1), store.TimeAt(id));
                }
            }

            Assert.AreEqual(rows.Count - 1, previousRow, "the newest history row is kept");
        }
    }
}
