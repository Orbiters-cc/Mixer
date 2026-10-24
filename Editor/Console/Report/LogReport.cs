using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>One distinct message among the most important of the session (README and AI diagnosis).</summary>
    internal sealed class LogDigestGroup
    {
        public string Id;
        public LogLevel Level;
        public LogVariant Variant;
        public int Count;
        public string Source;
        public string Text;
        public string Stack;
        public long FirstSeen;
        public long LastSeen;
        public string Known;
        internal int TextId;
        internal int StackId;
    }

    /// <summary>The session's logs in numbers, with the messages that matter most.</summary>
    internal sealed class LogDigest
    {
        public int Total;
        public int Errors;
        public int Warnings;
        public int Infos;
        public long From;
        public long To;
        public readonly List<LogDigestGroup> Groups = new List<LogDigestGroup>();
    }

    /// <summary>
    /// The logs part of a report: every log in order (<c>logs.txt</c>), every distinct message with its count, times
    /// and stack trace (<c>messages.txt</c>), and the end of Unity's own Editor.log and the previous session's.
    /// </summary>
    internal static class LogReport
    {
        internal const int MaxErrorGroups = 25;
        internal const int MaxWarningGroups = 12;
        internal const int MaxInfoGroups = 3;
        private const int MaxText = 1500;
        private const int MaxKnown = 200;
        private const int MaxStack = 2000;
        private const int MaxStackLines = 14;
        private const long EditorLogTail = 32L * 1024 * 1024;

        /// <summary>Counts by level, in occurrences of the logs the store holds.</summary>
        internal static void Count(LogStore store, out int errors, out int warnings, out int infos)
        {
            errors = warnings = infos = 0;
            if (store == null)
            {
                return;
            }

            for (int i = 0; i < store.MessageCount; i++)
            {
                ref var message = ref store.Message(i);
                if (message.Resolved)
                {
                    continue;
                }

                switch (message.Level)
                {
                    case LogLevel.Error:
                        errors += message.Count;
                        break;
                    case LogLevel.Warning:
                        warnings += message.Count;
                        break;
                    default:
                        infos += message.Count;
                        break;
                }
            }
        }

        /// <summary>
        /// The numbers and the most important messages, on the main thread: compile errors first, then the errors that
        /// happen most, then warnings, then the latest plain logs. Same text counts once. Texts are masked, then cut.
        /// </summary>
        internal static LogDigest Digest(LogStore store, ReportPrivacy privacy)
        {
            var digest = new LogDigest();
            if (store == null || store.Count == 0)
            {
                return digest;
            }

            Count(store, out digest.Errors, out digest.Warnings, out digest.Infos);
            digest.Total = store.Count;
            digest.From = store.FirstTime;
            digest.To = store.LastTime;
            var byText = new Dictionary<int, LogDigestGroup>();
            var counts = new int[store.MessageCount];
            var first = new long[store.MessageCount];
            var last = new long[store.MessageCount];
            for (int occurrence = 0; occurrence < store.Count; occurrence++)
            {
                int message = store.MessageAt(occurrence);
                long time = store.TimeAt(occurrence);
                if (counts[message]++ == 0)
                {
                    first[message] = time;
                }

                last[message] = time;
            }

            for (int i = 0; i < store.MessageCount; i++)
            {
                ref var message = ref store.Message(i);
                if (counts[i] == 0 || message.Resolved)
                {
                    continue;
                }

                if (byText.TryGetValue(message.Text, out var known))
                {
                    known.Count += counts[i];
                    known.FirstSeen = Math.Min(known.FirstSeen, first[i]);
                    known.LastSeen = Math.Max(known.LastSeen, last[i]);
                    continue;
                }

                byText[message.Text] = new LogDigestGroup
                {
                    Level = message.Level,
                    Variant = message.Variant,
                    Count = counts[i],
                    Source = store.SourceOf(i).Name,
                    TextId = message.Text,
                    StackId = message.Stack,
                    FirstSeen = first[i],
                    LastSeen = last[i]
                };
            }

            var all = byText.Values.ToList();
            var chosen = new List<LogDigestGroup>();
            chosen.AddRange(all.Where(g => g.Level == LogLevel.Error)
                .OrderByDescending(g => LogVariants.IsCompile(g.Variant)).ThenByDescending(g => g.Count).ThenByDescending(g => g.LastSeen).Take(MaxErrorGroups));
            chosen.AddRange(all.Where(g => g.Level == LogLevel.Warning)
                .OrderByDescending(g => LogVariants.IsCompile(g.Variant)).ThenByDescending(g => g.Count).ThenByDescending(g => g.LastSeen).Take(MaxWarningGroups));
            chosen.AddRange(all.Where(g => g.Level == LogLevel.Info).OrderByDescending(g => g.LastSeen).Take(MaxInfoGroups));
            for (int i = 0; i < chosen.Count; i++)
            {
                var group = chosen[i];
                group.Id = "g" + i;
                // Only the chosen ones are read and masked; masked before cutting, so a secret cut in half can't leak.
                string text = store.Texts.Plain(group.TextId);
                string masked = privacy.Mask(text);
                string stack = TopOfStack(store.Texts.Plain(group.StackId));
                int explanation = LogExplanations.Match(text, stack, group.Level);
                // The title's placeholders are filled from the masked text, then the title is masked as a whole too.
                string known = explanation >= 0 ? LogExplanations.Explain(explanation, masked)?.Title : null;
                group.Known = known != null ? privacy.MaskAndClip(known, MaxKnown) : null;
                group.Text = Clip(masked, MaxText);
                group.Stack = privacy.MaskAndClip(stack, MaxStack);
                group.Source = privacy.Mask(group.Source);
                digest.Groups.Add(group);
            }

            return digest;
        }

        /// <summary>What the worker needs from the store, taken on the main thread.</summary>
        internal sealed class Snapshot
        {
            public StoreView View;
            public string[] Sources;
            public int[] SourceOf;
            public string EditorLog;
        }

        internal static Snapshot Take(LogStore store)
        {
            var snapshot = new Snapshot { EditorLog = ReloadTimings.LogPath };
            if (store == null)
            {
                return snapshot;
            }

            snapshot.View = store.View();
            snapshot.Sources = store.Sources.All.Select(s => s.Name).ToArray();
            snapshot.SourceOf = new int[snapshot.View.MessageCount];
            for (int i = 0; i < snapshot.View.MessageCount; i++)
            {
                snapshot.SourceOf[i] = snapshot.View.Messages[i].Source;
            }

            return snapshot;
        }

        /// <summary>Writes the logs files; safe on a worker thread. Returns a line for the progress.</summary>
        internal static string Write(ReportContext context, Snapshot snapshot, Action<float> progress)
        {
            var view = snapshot.View;
            int written = 0;
            if (view.Count > 0)
            {
                var counts = new int[view.MessageCount];
                var first = new long[view.MessageCount];
                var last = new long[view.MessageCount];
                using (var writer = context.OpenText("logs/logs.txt", "Every log of the session, oldest first (stack traces in messages.txt)"))
                {
                    writer.Line("# " + LoggerUi.Plural(view.Count, "log") + " from " + Stamp(view.Times[0]) + " to " + Stamp(view.Times[view.Count - 1]) +
                                ". ~ marks a time read back from Unity's console (to the second).");
                    writer.Line();
                    var line = new StringBuilder(256);
                    for (int i = 0; i < view.Count; i++)
                    {
                        int id = view.Owners[i];
                        long time = view.Times[i];
                        if (counts[id]++ == 0)
                        {
                            first[id] = time;
                        }

                        last[id] = time;
                        ref var message = ref view.Messages[id];
                        line.Clear();
                        line.Append(Stamp(time)).Append(((OccurrenceFlags)view.Flags[i] & OccurrenceFlags.ApproximateTime) != 0 ? "~ " : "  ");
                        line.Append(LevelTag(message.Variant)).Append("  ");
                        line.Append(SourceName(snapshot, id)).Append("  ");
                        AppendIndented(line, view.Texts.Plain(message.Text), "    ");
                        writer.Line(line.ToString());
                        written++;
                        if ((i & 0x3FFF) == 0)
                        {
                            progress?.Invoke(0.75f * i / view.Count);
                        }
                    }
                }

                WriteMessages(context, snapshot, counts, first, last);
            }

            progress?.Invoke(0.8f);
            string editorLog = snapshot.EditorLog;
            bool copied = CopyTail(context, editorLog, "logs/Editor.log", "The end of Unity's Editor.log (this session)");
            if (!string.IsNullOrEmpty(editorLog))
            {
                string previous = Path.Combine(Path.GetDirectoryName(editorLog) ?? string.Empty, "Editor-prev.log");
                CopyTail(context, previous, "logs/Editor-prev.log", "The end of Unity's Editor.log from the previous session");
            }

            progress?.Invoke(1f);
            return LoggerUi.Plural(written, "log") + (copied ? " · Editor.log" : string.Empty);
        }

        private static void WriteMessages(ReportContext context, Snapshot snapshot, int[] counts, long[] first, long[] last)
        {
            var view = snapshot.View;
            var order = Enumerable.Range(0, view.MessageCount).Where(i => counts[i] > 0)
                .OrderByDescending(i => (int)view.Messages[i].Level).ThenByDescending(i => counts[i]).ToList();
            using (var writer = context.OpenText("logs/messages.txt", "Every distinct message with its count, first and last time and stack trace"))
            {
                writer.Line("# " + LoggerUi.Plural(order.Count, "distinct message") + ", errors first, then by how often they happened.");
                foreach (int id in order)
                {
                    ref var message = ref view.Messages[id];
                    writer.Line();
                    writer.Line(new string('─', 100));
                    writer.Line(LevelTag(message.Variant).Trim() + " · " + LogVariants.Label(message.Variant) + " · ×" + counts[id].ToString("N0", CultureInfo.InvariantCulture) +
                                " · " + SourceName(snapshot, id) + (message.Resolved ? " · fixed by a later compilation" : string.Empty));
                    writer.Line("first " + Stamp(first[id]) + " · last " + Stamp(last[id]));
                    writer.Line();
                    writer.Line(view.Texts.Plain(message.Text));
                    string stack = view.Texts.Plain(message.Stack);
                    if (!string.IsNullOrWhiteSpace(stack))
                    {
                        writer.Line();
                        writer.Line(stack.TrimEnd());
                    }
                }
            }
        }

        // The end of a log file that may be hundreds of megabytes, cut at a line start.
        private static bool CopyTail(ReportContext context, string path, string name, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1 << 16))
                using (var writer = context.OpenText(name, label))
                {
                    bool cut = stream.Length > EditorLogTail;
                    if (cut)
                    {
                        stream.Seek(-EditorLogTail, SeekOrigin.End);
                        reader.DiscardBufferedData();
                        reader.ReadLine();
                        writer.Line("[The first " + (stream.Length - EditorLogTail) / (1024 * 1024) + " MB of this file were left out.]");
                    }

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        writer.Line(line);
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string SourceName(Snapshot snapshot, int message)
        {
            int source = snapshot.SourceOf[message];
            return source >= 0 && source < snapshot.Sources.Length ? snapshot.Sources[source] : "Unity";
        }

        internal static string LevelTag(LogVariant variant)
        {
            switch (LogVariants.Level(variant))
            {
                case LogLevel.Error:
                    return "ERROR";
                case LogLevel.Warning:
                    return "WARN ";
                default:
                    return "LOG  ";
            }
        }

        internal static string Stamp(long utcTicks) => utcTicks <= 0
            ? "----------  --:--:--.---"
            : LoggerUi.Local(utcTicks).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static void AppendIndented(StringBuilder line, string text, string indent)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            text = text.TrimEnd();
            int start = 0;
            while (start <= text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    line.Append(text, start, text.Length - start);
                    return;
                }

                line.Append(text, start, end - start).Append('\n').Append(indent);
                start = end + 1;
            }
        }

        // The frames that tell where it happened: the first lines, without Unity's own logging frames (not cut: masked first).
        internal static string TopOfStack(string stack)
        {
            if (string.IsNullOrWhiteSpace(stack))
            {
                return string.Empty;
            }

            var kept = new StringBuilder();
            int lines = 0;
            foreach (string raw in stack.Split('\n'))
            {
                string frame = raw.TrimEnd('\r');
                if (frame.Length == 0 || frame.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) ||
                    frame.StartsWith("UnityEngine.Logger", StringComparison.Ordinal) || frame.StartsWith("UnityEngine.DebugLogHandler", StringComparison.Ordinal))
                {
                    continue;
                }

                kept.Append(frame).Append('\n');
                if (++lines >= MaxStackLines)
                {
                    break;
                }
            }

            return kept.ToString().TrimEnd();
        }

        internal static string Clip(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
