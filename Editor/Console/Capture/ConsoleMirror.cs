using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Follows Unity's console a step behind the live capture. Each new console row is matched to the log the Mixer console
    /// received: that adds what only Unity's console knows (the context object a click selects, the file of engine
    /// errors), and any row with no match is a log the Mixer console never received, added so nothing Unity shows is missing.
    /// Only rows that existed at the previous update are read, so logs from other threads have arrived by then.
    /// </summary>
    internal sealed class ConsoleMirror
    {
        private const int MaxPending = 20000;
        private const int MaxBehind = 20000;
        private static readonly long PendingLifetime = TimeSpan.FromSeconds(10).Ticks;

        private struct Pending
        {
            public int Occurrence;
            public string Condition;
            public LogLevel Level;
            public long Time;
        }

        private readonly List<Pending> pending = new List<Pending>();
        private int pendingStart;
        private int rows;
        private int previousTotal;

        public int Rows => rows;

        /// <summary>Starts following from <paramref name="unityRows"/>: earlier rows are history (or gone).</summary>
        public void Reset(int unityRows)
        {
            rows = Math.Max(0, unityRows);
            previousTotal = rows;
            pending.Clear();
            pendingStart = 0;
        }

        /// <summary>
        /// Remembers a log received live, to match it with its console row. It waits from when it was filed, not when
        /// it was logged: after the editor was busy for minutes, queued logs are old but their rows are still to come.
        /// </summary>
        public void Track(int occurrence, string condition, LogLevel level)
        {
            pending.Add(new Pending { Occurrence = occurrence, Condition = condition ?? string.Empty, Level = level, Time = DateTime.UtcNow.Ticks });
            if (pending.Count - pendingStart > MaxPending)
            {
                pendingStart += MaxPending / 4;
            }

            Compact();
        }

        /// <summary>Occurrence ids moved (history merged in front, oldest logs trimmed): pending matches follow them.</summary>
        public void Shift(int offset)
        {
            for (int i = pendingStart; i < pending.Count; i++)
            {
                var entry = pending[i];
                entry.Occurrence += offset;
                pending[i] = entry;
            }
        }

        public void Tick(LogStore store, long now, double budgetMs, Action<UnityConsoleRow, LogVariant> compileRow)
        {
            if (!UnityConsole.Available)
            {
                return;
            }

            int total = UnityConsole.Total();
            if (total < rows)
            {
                // Unity's console was cleared (its Clear button, Clear on Play, another tool): follow from here.
                Reset(total);
                return;
            }

            // Rows are only every entry in order when Unity's console shows everything.
            if (!UnityConsole.RowsAreComplete)
            {
                Reset(total);
                return;
            }

            int limit = Math.Min(total, previousTotal);
            previousTotal = total;
            if (limit - rows > MaxBehind)
            {
                // A flood of logs: matching them all would cost more than it gives. Skip ahead.
                Reset(total);
                return;
            }

            if (limit <= rows)
            {
                ExpirePending(now);
                return;
            }

            var clock = Stopwatch.StartNew();
            using (var reader = UnityConsole.Reader.Open())
            {
                if (reader == null)
                {
                    return;
                }

                limit = Math.Min(limit, reader.Count);
                while (rows < limit)
                {
                    if (reader.TryRead(rows, out var row))
                    {
                        Match(store, row, now, compileRow);
                    }

                    rows++;
                    if ((rows & 31) == 0 && clock.Elapsed.TotalMilliseconds >= budgetMs)
                    {
                        break;
                    }
                }
            }

            ExpirePending(now);
        }

        private void Match(LogStore store, UnityConsoleRow row, long now, Action<UnityConsoleRow, LogVariant> compileRow)
        {
            var variant = UnityConsole.VariantOf(row.Mode);
            if (LogVariants.IsCompile(variant))
            {
                compileRow?.Invoke(row, variant);
                return;
            }

            string condition = row.Condition;
            var level = LogVariants.Level(variant);
            int limit = Math.Min(pending.Count, pendingStart + 16);
            for (int i = pendingStart; i < limit; i++)
            {
                var candidate = pending[i];
                if (candidate.Level != level || !string.Equals(candidate.Condition, condition, StringComparison.Ordinal))
                {
                    continue;
                }

                if (candidate.Occurrence >= 0 && candidate.Occurrence < store.Count)
                {
                    if (row.InstanceId != 0)
                    {
                        store.SetContext(candidate.Occurrence, row.InstanceId);
                    }

                    ref var message = ref store.Message(store.MessageAt(candidate.Occurrence));
                    if (message.File == 0 && !string.IsNullOrEmpty(row.File))
                    {
                        message.File = store.Texts.Intern(StackTraces.NormalizePath(row.File));
                        message.Line = row.Line;
                        if (message.Callsite == 0)
                        {
                            message.Callsite = store.Texts.Intern(StackTraces.FileName(row.File) + (row.Line > 0 ? ":" + row.Line : string.Empty));
                        }
                    }
                }

                // Usually the oldest pending log. Others before the match came from the same burst: they keep waiting.
                if (i == pendingStart)
                {
                    pendingStart++;
                    Compact();
                }
                else
                {
                    pending.RemoveAt(i);
                }

                return;
            }

            // Unity shows a log the Mixer console never received live: add it so nothing is missing.
            int occurrence = store.Add(now, condition, row.Stack, variant, OccurrenceFlags.FromUnityConsole | OccurrenceFlags.ApproximateTime);
            if (row.InstanceId != 0)
            {
                store.SetContext(occurrence, row.InstanceId);
            }
        }

        private void ExpirePending(long now)
        {
            while (pendingStart < pending.Count && now - pending[pendingStart].Time > PendingLifetime)
            {
                pendingStart++;
            }

            Compact();
        }

        private void Compact()
        {
            if (pendingStart > 4096 && pendingStart * 2 > pending.Count)
            {
                pending.RemoveRange(0, pendingStart);
                pendingStart = 0;
            }
        }
    }
}
