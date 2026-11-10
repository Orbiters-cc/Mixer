using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    /// <summary>
    /// The import of Unity's console history against the store's text budget and ceiling, on rows a test gives
    /// (<see cref="IConsoleSource"/>): it holds nothing across updates, checks the budget before interning, keeps the
    /// newest history, and live logs arriving meanwhile are kept and limited like any other.
    /// </summary>
    public class ConsoleHistoryTests
    {
        private sealed class Rows : IConsoleRows
        {
            private readonly List<string> rows;

            public Rows(List<string> rows) => this.rows = rows;

            public int Count => rows.Count;

            public string Lines(int row, int lines, out int mode)
            {
                mode = 0;
                return "[10:" + (row / 60 % 60).ToString("00") + ":" + (row % 60).ToString("00") + "] " + rows[row];
            }

            public bool TryRead(int row, out UnityConsoleRow result)
            {
                result = new UnityConsoleRow { Message = rows[row], CallstackStart = rows[row].Length };
                return true;
            }

            public void Dispose()
            {
            }
        }

        private sealed class Source : IConsoleSource
        {
            private readonly List<string> rows;

            public Source(List<string> rows) => this.rows = rows;

            public bool Available => true;
            public IConsoleRows Open() => new Rows(rows);
            public bool TakeAll() => false;
            public void ReleaseAll()
            {
            }

            public bool WindowMayShow => false;
        }

        [Test]
        public void AnImportLargerThanTheBudgetKeepsTheCeilingAndTheNewestLogs()
        {
            var rows = new List<string>();
            string padding = new string('h', 400);
            for (int i = 0; i < 3000; i++)
            {
                rows.Add("History " + i + " " + padding);
            }

            var store = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 18, CompactInBackground = true };
            var import = new ConsoleHistory(0, rows.Count, ConsoleHistory.Placement.Before, 1000, (rows.Count - 1, rows[rows.Count - 1]))
            {
                Source = new Source(rows)
            };
            var live = new List<int>();
            string livePadding = new string('l', 300);
            int steps = 0;
            while (!import.Step(store, 0.01d))
            {
                Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling, "after slice " + steps);
                for (int i = 0; i < 20; i++)
                {
                    live.Add(store.Add(TimeSpan.TicksPerDay * 1000 + steps * 100 + i, "Live " + steps + " " + i + " " + livePadding, string.Empty, LogVariant.Log));
                    Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling);
                }

                store.StepMaintenance();
                steps++;
                Assert.Less(steps, 100_000);
            }

            Assert.LessOrEqual(store.Texts.TotalChars, store.TextCeiling);
            Assert.Greater(steps, 1, "the import went a slice at a time");
            Assert.IsFalse(import.Failed);
            Assert.Less(import.Done, import.Total, "older history than fits was left out");

            // Live logs: the newest ones are all there, as they were logged.
            int newest = live[live.Count - 1];
            for (int k = live.Count - 1; k >= 0 && live[k] >= store.FirstId; k--)
            {
                Assert.IsTrue(store.Contains(live[k]));
                StringAssert.StartsWith("Live ", store.Texts[store.Message(store.MessageAt(live[k])).Text]);
            }

            Assert.IsTrue(store.Contains(newest));

            // History in front, newest history kept, in time order.
            bool sawHistory = false;
            for (int id = store.FirstId; id < store.EndId; id++)
            {
                string text = store.Texts[store.Message(store.MessageAt(id)).Text];
                if (text.StartsWith("History ", StringComparison.Ordinal))
                {
                    sawHistory = true;
                }

                if (id > store.FirstId)
                {
                    Assert.LessOrEqual(store.TimeAt(id - 1), store.TimeAt(id));
                }
            }

            if (sawHistory)
            {
                // Whatever history is left ends with the newest row (older rows go first, never newer ones).
                int lastHistory = -1;
                for (int id = store.FirstId; id < store.EndId; id++)
                {
                    if (store.Texts[store.Message(store.MessageAt(id)).Text].StartsWith("History ", StringComparison.Ordinal))
                    {
                        lastHistory = id;
                    }
                }

                Assert.AreEqual(rows[rows.Count - 1], store.Texts[store.Message(store.MessageAt(lastHistory)).Text]);
            }
        }

        [Test]
        public void TheImportStopsBeforeInterningWhatDoesNotFit()
        {
            var rows = new List<string>();
            for (int i = 0; i < 400; i++)
            {
                rows.Add("Row " + i + " " + new string('r', 2000));
            }

            var store = new LogStore(LogStore.HistoryRoom) { MaxTextChars = 1 << 16 };
            var import = new ConsoleHistory(0, rows.Count, ConsoleHistory.Placement.Before, 1000) { Source = new Source(rows) };
            while (!import.Step(store, 50d))
            {
                Assert.LessOrEqual(store.Texts.TotalChars, store.MaxTextChars, "admission keeps history within the budget itself");
            }

            Assert.LessOrEqual(store.Texts.TotalChars, store.MaxTextChars);
            Assert.Less(import.Done, rows.Count);
            Assert.AreEqual(import.Done, store.Count, "every row read is in the store");
            Assert.AreEqual(rows[rows.Count - 1], store.Texts[store.Message(store.MessageAt(store.EndId - 1)).Text], "the newest row is kept");
        }
    }
}
