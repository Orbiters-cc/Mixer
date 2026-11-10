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

        [Test]
        public void UnderTheLimitsTheImportGivesRound1sStore([Values(0, 1, 2, 3)] int mode)
        {
            for (int seed = 1; seed <= 30; seed++)
            {
                var rows = RandomConsole(seed);
                var random = new Random(seed * 7);
                var liveTexts = new List<string>();
                var store = new LogStore(LogStore.HistoryRoom) { CompactInBackground = true };
                var import = NewImport(rows, 0, rows.Count);
                int updates = 0;
                int reloadAt = mode == 2 ? random.Next(1, rows.Count) : mode == 3 ? rows.Count : -1;
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
                        // A script reload: what was staged is not saved; the next domain resumes the range left.
                        store.DiscardStaged();
                        import = NewImport(rows, import.From, import.To);
                    }

                    store.StepMaintenance();
                    Assert.Less(updates, 100_000);
                }

                var reference = Reference(rows, s =>
                {
                    foreach (string live in liveTexts)
                    {
                        s.Add(TimeSpan.TicksPerDay * 800_000 + 1 + liveTexts.IndexOf(live), live, string.Empty, LogVariant.Log);
                    }
                });
                Assert.IsTrue(import.Finished && !import.Failed, "seed " + seed);
                Assert.AreEqual(0, store.StagedCount);
                // Live logs interleave with the import's interning: message ids then differ, the logs don't.
                Assert.AreEqual(Content(reference, messageOrder: liveTexts.Count == 0), Content(store, messageOrder: liveTexts.Count == 0), "seed " + seed + ", mode " + mode);
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
                rows.Add(new Row { Condition = "Row " + (i % 50), Seconds = 23 * 3600 + 41 * 60 + i / 100 });
            }

            var store = new LogStore(LogStore.HistoryRoom);
            var import = NewImport(rows, 0, rows.Count);
            // Read everything, then put one batch in front, then reload.
            while (store.StagedCount < rows.Count)
            {
                import.Step(store, 1e9);
            }

            import.Step(store, 0d);
            Assert.AreEqual(ConsoleHistory.MaxPublishedPerUpdate, store.Count, "one batch in front");
            Assert.AreEqual(rows.Count - ConsoleHistory.MaxPublishedPerUpdate, import.To);
            store.DiscardStaged();
            var resumed = NewImport(rows, import.From, import.To);
            while (!resumed.Step(store, 1e9))
            {
            }

            Assert.AreEqual(Content(Reference(rows), messageOrder: false), Content(store, messageOrder: false));
        }

        // The rows Unity adds during the import are live logs, past the range; a console cleared meanwhile ends it.
        [Test]
        public void RowsAddedOrAConsoleClearedMeanwhile()
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

            var cleared = new List<Row>(RandomConsole(6));
            var source = new Source(cleared);
            var other = new LogStore(LogStore.HistoryRoom);
            var failing = new ConsoleHistory(0, cleared.Count, ConsoleHistory.Placement.Before, 1000) { Source = source, EditorStart = EditorStart };
            failing.Step(other, 0d);
            source.RowList = new List<Row>();
            Assert.IsTrue(failing.Step(other, 1e9));
            Assert.IsTrue(failing.Failed, "Unity's console was cleared: its rows are gone");
            Assert.AreEqual(0, other.StagedCount, "staged rows let go");
            Assert.AreEqual(0, other.Count);
        }

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
            int lastHistory = -1;
            int previousRow = -1;
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                string text = store.Texts[store.Message(store.MessageAt(id)).Text];
                if (text.StartsWith("History ", StringComparison.Ordinal))
                {
                    int row = int.Parse(text.Substring(8, text.IndexOf(' ', 8) - 8));
                    Assert.Greater(row, previousRow, "history in order");
                    previousRow = row;
                    lastHistory = id;
                }

                if (id > store.FirstId)
                {
                    Assert.LessOrEqual(store.TimeAt(id - 1), store.TimeAt(id));
                }
            }

            Assert.Greater(lastHistory, -1, "some history fits");
            Assert.AreEqual(rows.Count - 1, previousRow, "the newest history row is kept");
        }
    }
}
