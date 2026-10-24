using System;
using System.Collections.Generic;
using System.Text;

namespace Orbiters.Logger.Editor
{
    /// <summary>One distinct log: the same text, stack trace and type. Every time it is logged adds an occurrence.</summary>
    internal struct LogMessage
    {
        public int Text;
        public int Stack;
        /// <summary>Text id of the short "Type.Method · File.cs:12" shown under the message.</summary>
        public int Callsite;
        /// <summary>Text id of the project-relative file a double-click opens.</summary>
        public int File;
        public int Line;
        public int Column;
        /// <summary>Text id of the assembly a compiler message belongs to (0 when unknown).</summary>
        public int Assembly;
        public int Source;
        public int Group;
        public int Count;
        public int First;
        public int Last;
        /// <summary>-2 not looked up yet, -1 none, else an index in <see cref="LogExplanations"/>.</summary>
        public int Explanation;
        public LogVariant Variant;
        public MessageFlags Flags;

        public LogLevel Level => LogVariants.Level(Variant);
        public bool Resolved => (Flags & MessageFlags.Resolved) != 0;
    }

    /// <summary>Messages with the same (or similar) text and level, shown as one row when grouping.</summary>
    internal struct LogGroup
    {
        public int Key;
        public LogLevel Level;
        public int Count;
        public int First;
        public int Last;
        public int Latest;
        public int Variants;
    }

    internal readonly struct MessageKey : IEquatable<MessageKey>
    {
        public readonly int Text;
        public readonly int Stack;
        public readonly LogVariant Variant;

        public MessageKey(int text, int stack, LogVariant variant)
        {
            Text = text;
            Stack = stack;
            Variant = variant;
        }

        public bool Equals(MessageKey other) => Text == other.Text && Stack == other.Stack && Variant == other.Variant;
        public override bool Equals(object obj) => obj is MessageKey other && Equals(other);
        public override int GetHashCode() => unchecked((Text * 397) ^ (Stack * 7919) ^ (int)Variant);
    }

    /// <summary>What a worker thread reads: arrays and counts taken together on the main thread.</summary>
    internal readonly struct StoreView
    {
        public readonly long[] Times;
        public readonly int[] Owners;
        public readonly byte[] Flags;
        public readonly int Count;
        public readonly LogMessage[] Messages;
        public readonly int MessageCount;
        public readonly LogGroup[] Groups;
        public readonly int GroupCount;
        public readonly TextTable Texts;
        public readonly int Epoch;
        public readonly int MessagesVersion;

        public StoreView(long[] times, int[] owners, byte[] flags, int count, LogMessage[] messages, int messageCount,
            LogGroup[] groups, int groupCount, TextTable texts, int epoch, int messagesVersion)
        {
            Times = times;
            Owners = owners;
            Flags = flags;
            Count = count;
            Messages = messages;
            MessageCount = messageCount;
            Groups = groups;
            GroupCount = groupCount;
            Texts = texts;
            Epoch = epoch;
            MessagesVersion = messagesVersion;
        }
    }

    /// <summary>
    /// Every log of the session, column by column: a time, a message id and flags per occurrence (16 bytes), distinct
    /// texts stored once. Occurrences are appended in time order and only change all at once (clear, trim, history
    /// merge, regroup), which bumps <see cref="Epoch"/>. Only the main thread changes the store; worker threads read a
    /// <see cref="StoreView"/>.
    /// </summary>
    internal sealed class LogStore
    {
        public const int DefaultMaxOccurrences = 3_000_000;
        /// <summary>The source of timing logs (script reloads, Play Mode, avatar builds and uploads).</summary>
        public const string TimingSource = "Timings";
        public const OccurrenceFlags Superseded = (OccurrenceFlags)8;

        private long[] times;
        private int[] owners;
        private byte[] flags;
        private int count;
        private LogMessage[] messages = new LogMessage[256];
        private int messageCount;
        private readonly Dictionary<MessageKey, int> messageIndex = new Dictionary<MessageKey, int>();
        private LogGroup[] groups = new LogGroup[256];
        private int groupCount;
        private readonly Dictionary<long, int> groupIndex = new Dictionary<long, int>();
        private readonly Dictionary<int, int> contexts = new Dictionary<int, int>();
        private readonly List<SessionEvent> events = new List<SessionEvent>();
        private int maxOccurrences = DefaultMaxOccurrences;

        public LogStore(int capacity = 4096)
        {
            capacity = Math.Max(capacity, 64);
            times = new long[capacity];
            owners = new int[capacity];
            flags = new byte[capacity];
            Texts = new TextTable();
        }

        public TextTable Texts { get; private set; }
        public SourceCatalog Sources { get; } = new SourceCatalog();
        public GroupingMode Grouping { get; private set; }
        public int Count => count;
        public int MessageCount => messageCount;
        public int GroupCount => groupCount;
        /// <summary>Bumps on every append.</summary>
        public int Version { get; private set; }
        /// <summary>Bumps when occurrence or group ids change meaning: clear, trim, history merge, regroup.</summary>
        public int Epoch { get; private set; }
        /// <summary>Bumps when a message's flags change (compile errors resolved): filters must be recomputed.</summary>
        public int MessagesVersion { get; private set; }
        public long FirstTime => count > 0 ? times[0] : 0L;
        public long LastTime => count > 0 ? times[count - 1] : 0L;
        public IReadOnlyList<SessionEvent> Events => events;

        /// <summary>Occurrence ids moved by this much (history put in front: positive; oldest trimmed: negative).</summary>
        public event Action<int> Shifted;

        /// <summary>Everything was cleared: no earlier occurrence or group id means anything any more.</summary>
        public event Action Emptied;

        /// <summary>Messages were regrouped: group ids changed.</summary>
        public event Action Regrouped;

        public int MaxOccurrences
        {
            get => maxOccurrences;
            set => maxOccurrences = Math.Max(10_000, value);
        }

        public long TimeAt(int occurrence) => times[occurrence];
        public int MessageAt(int occurrence) => owners[occurrence];
        public OccurrenceFlags FlagsAt(int occurrence) => (OccurrenceFlags)flags[occurrence];
        public ref LogMessage Message(int id) => ref messages[id];
        public ref LogGroup Group(int id) => ref groups[id];
        public LogSource SourceOf(int message) => Sources[messages[message].Source];

        public StoreView View() => new StoreView(times, owners, flags, count, messages, messageCount, groups, groupCount, Texts, Epoch, MessagesVersion);

        public bool TryGetContext(int occurrence, out int instanceId) => contexts.TryGetValue(occurrence, out instanceId);

        public void SetContext(int occurrence, int instanceId)
        {
            if (occurrence >= 0 && occurrence < count && instanceId != 0)
            {
                contexts[occurrence] = instanceId;
            }
        }

        public void AddEvent(long time, SessionEventKind kind)
        {
            if (events.Count > 0 && time < events[events.Count - 1].Time)
            {
                time = events[events.Count - 1].Time;
            }

            events.Add(new SessionEvent(time, kind));
            if (events.Count > 4096)
            {
                events.RemoveRange(0, 1024);
            }

            Version++;
        }

        /// <summary>Records one log. Returns its occurrence index.</summary>
        public int Add(long time, string condition, string stack, LogVariant variant, OccurrenceFlags occurrenceFlags = OccurrenceFlags.None)
        {
            return Append(time, Intern(condition, stack, variant), occurrenceFlags);
        }

        /// <summary>The id of the message with this text, stack trace and type, created when new.</summary>
        public int Intern(string condition, string stack, LogVariant variant, int assembly = 0, string file = null, int line = 0, int column = 0)
        {
            int text = Texts.Intern(condition ?? string.Empty);
            int stackId = Texts.Intern(stack ?? string.Empty);
            var key = new MessageKey(text, stackId, variant);
            if (messageIndex.TryGetValue(key, out int id))
            {
                return id;
            }

            if (messageCount == messages.Length)
            {
                var next = new LogMessage[messages.Length * 2];
                Array.Copy(messages, next, messageCount);
                messages = next;
            }

            id = messageCount;
            var message = new LogMessage
            {
                Text = text,
                Stack = stackId,
                Variant = variant,
                Assembly = assembly,
                Explanation = -2,
                First = -1,
                Last = -1,
                Flags = LogVariants.IsCompile(variant) ? MessageFlags.Compile : MessageFlags.None
            };
            Describe(ref message, condition, stack, file, line, column);
            message.Group = GroupFor(text, message.Level);
            messages[id] = message;
            messageCount = id + 1;
            messageIndex[key] = id;
            groups[message.Group].Variants++;
            return id;
        }

        // Callsite, file to open and source, read once per distinct message.
        private void Describe(ref LogMessage message, string condition, string stack, string file, int line, int column)
        {
            if (LogVariants.IsCompile(message.Variant))
            {
                if (string.IsNullOrEmpty(file))
                {
                    StackTraces.TryParseCompilerLocation(condition, out file, out line, out column);
                }

                string path = StackTraces.NormalizePath(file);
                message.File = Texts.Intern(path);
                message.Line = line;
                message.Column = column;
                message.Callsite = string.IsNullOrEmpty(path) ? 0 : Texts.Intern(path + (line > 0 ? ":" + line : string.Empty));
                message.Source = SourceCatalog.Compiler;
                return;
            }

            if (TimingProfile.IsTimings(stack))
            {
                // A timing log: its largest shares under the message.
                message.Source = Sources.Get(TimingSource);
                var profile = TimingProfile.Parse(stack);
                message.Callsite = Texts.Intern(profile != null ? profile.Summary(4) : string.Empty);
                return;
            }

            if (StackTraces.IsOutput(stack))
            {
                // A command's log: its timing line under the message.
                message.Source = Sources.Resolve(message.Variant, condition, new List<StackFrame>(), -1, null);
                message.Callsite = Texts.Intern(RichText.FirstLine(stack.Substring(StackTraces.OutputPrefix.Length), 160));
                return;
            }

            var frames = StackTraces.Parse(stack);
            int callsite = StackTraces.FindCallsite(frames);
            message.Source = Sources.Resolve(message.Variant, condition, frames, callsite, null);
            if (callsite >= 0)
            {
                var frame = frames[callsite];
                string method = StackTraces.ShortMethod(frame.Method);
                if (frame.HasFile)
                {
                    string path = StackTraces.NormalizePath(frame.File);
                    message.File = Texts.Intern(path);
                    message.Line = frame.Line;
                    message.Callsite = Texts.Intern(method + "  ·  " + StackTraces.FileName(path) + ":" + frame.Line);
                }
                else
                {
                    message.Callsite = Texts.Intern(method);
                }
            }
            else if (!string.IsNullOrEmpty(file))
            {
                string path = StackTraces.NormalizePath(file);
                if (message.Source == SourceCatalog.Unity)
                {
                    // Unity cut the stack trace ("<message truncated>"): the file its console names says who logged it.
                    message.Source = Sources.ForFile(path);
                }

                message.File = Texts.Intern(path);
                message.Line = line;
                message.Callsite = Texts.Intern(StackTraces.FileName(path) + (line > 0 ? ":" + line : string.Empty));
            }
        }

        /// <summary>Adds an occurrence of <paramref name="message"/>. Times never go back: an earlier time is moved up to the last one.</summary>
        public int Append(long time, int message, OccurrenceFlags occurrenceFlags = OccurrenceFlags.None)
        {
            if (count == times.Length)
            {
                Grow(count * 2);
            }

            if (count > 0 && time < times[count - 1])
            {
                time = times[count - 1];
            }

            int index = count;
            times[index] = time;
            owners[index] = message;
            flags[index] = (byte)occurrenceFlags;
            ref var entry = ref messages[message];
            if (entry.Count == 0)
            {
                entry.First = index;
            }

            entry.Count++;
            entry.Last = index;
            ref var group = ref groups[entry.Group];
            if (group.Count == 0)
            {
                group.First = index;
            }

            group.Count++;
            group.Last = index;
            group.Latest = message;
            count = index + 1;
            Version++;
            if (count > maxOccurrences)
            {
                // The oldest fifth goes: the new occurrence is still the last one.
                Trim(maxOccurrences * 4 / 5);
                return count - 1;
            }

            return index;
        }

        /// <summary>A compiler message: a later compilation that reports it again replaces the earlier occurrence.</summary>
        public int AddCompile(long time, string text, bool error, int assembly, string file, int line, int column)
        {
            int id = Intern(text, string.Empty, error ? LogVariant.CompileError : LogVariant.CompileWarning, assembly, file, line, column);
            ref var message = ref messages[id];
            if (message.Resolved)
            {
                message.Flags &= ~MessageFlags.Resolved;
                MessagesVersion++;
            }

            if (message.Count > 0 && message.Last >= 0 && message.Last < count)
            {
                flags[message.Last] |= (byte)Superseded;
                MessagesVersion++;
            }

            return Append(time, id);
        }

        public bool HasActiveCompileMessage(string text, out int message)
        {
            message = -1;
            if (!Texts.TryFind(text, out int textId))
            {
                return false;
            }

            foreach (var variant in new[] { LogVariant.CompileError, LogVariant.CompileWarning })
            {
                if (messageIndex.TryGetValue(new MessageKey(textId, 0, variant), out int id) && !messages[id].Resolved)
                {
                    message = id;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Hides compiler messages a later compilation no longer reports (all assemblies when <paramref name="assembly"/> is -1).</summary>
        public int ResolveCompile(int assembly, bool errorsOnly)
        {
            int resolved = 0;
            for (int i = 0; i < messageCount; i++)
            {
                ref var message = ref messages[i];
                if ((message.Flags & MessageFlags.Compile) == 0 || message.Resolved)
                {
                    continue;
                }

                if (assembly >= 0 && message.Assembly != assembly)
                {
                    continue;
                }

                if (errorsOnly && message.Variant != LogVariant.CompileError)
                {
                    continue;
                }

                message.Flags |= MessageFlags.Resolved;
                resolved++;
            }

            if (resolved > 0)
            {
                MessagesVersion++;
            }

            return resolved;
        }

        public int AssemblyId(string assembly) => Texts.Intern(assembly ?? string.Empty);

        /// <summary>Empties the store. Compile errors still standing stay, as in Unity's console.</summary>
        public void Clear(long now)
        {
            var keep = new List<(string text, bool error, string assembly, string file, int line, int column)>();
            for (int i = 0; i < messageCount; i++)
            {
                var message = messages[i];
                if (message.Variant == LogVariant.CompileError && !message.Resolved && message.Count > 0)
                {
                    keep.Add((Texts[message.Text], true, Texts[message.Assembly], Texts[message.File], message.Line, message.Column));
                }
            }

            Reset(Math.Min(times.Length, 4096));
            foreach (var (text, error, assembly, file, line, column) in keep)
            {
                AddCompile(now, text, error, AssemblyId(assembly), file, line, column);
            }
        }

        private void Reset(int capacity)
        {
            times = new long[capacity];
            owners = new int[capacity];
            flags = new byte[capacity];
            count = 0;
            messages = new LogMessage[256];
            messageCount = 0;
            messageIndex.Clear();
            groups = new LogGroup[256];
            groupCount = 0;
            groupIndex.Clear();
            contexts.Clear();
            events.Clear();
            Texts = new TextTable();
            Epoch++;
            Version++;
            MessagesVersion++;
            Emptied?.Invoke();
        }

        public void SetGrouping(GroupingMode mode)
        {
            if (mode == Grouping)
            {
                return;
            }

            Grouping = mode;
            groups = new LogGroup[Math.Max(256, groupCount)];
            groupCount = 0;
            groupIndex.Clear();
            for (int i = 0; i < messageCount; i++)
            {
                messages[i].Group = GroupFor(messages[i].Text, messages[i].Level);
                groups[messages[i].Group].Variants++;
            }

            RecountGroups();
            Epoch++;
            Version++;
            Regrouped?.Invoke();
        }

        private int GroupFor(int text, LogLevel level)
        {
            int keyText = Grouping == GroupingMode.SameText ? AddressesFolded(text) : Texts.Intern(Similar(Texts.Plain(text)));
            long key = ((long)keyText << 2) | (long)level;
            if (groupIndex.TryGetValue(key, out int id))
            {
                return id;
            }

            if (groupCount == groups.Length)
            {
                var next = new LogGroup[groups.Length * 2];
                Array.Copy(groups, next, groupCount);
                groups = next;
            }

            id = groupCount;
            groups[id] = new LogGroup { Key = keyText, Level = level, First = -1, Last = -1, Latest = -1 };
            groupCount = id + 1;
            groupIndex[key] = id;
            return id;
        }

        // Memory addresses never tell two messages apart: Unity lists each leaked allocation with its own
        // ("Allocation of 49 bytes at 000002848112DD10"), dozens of rows of one message without this.
        private int AddressesFolded(int text)
        {
            string plain = Texts.Plain(text);
            string folded = FoldAddresses(plain);
            return ReferenceEquals(folded, plain) ? text : Texts.Intern(folded);
        }

        /// <summary>
        /// The text with memory addresses (16 hex digits, or 0x and 6 to 16 of them) replaced by <c>#</c>; the same string
        /// instance when it has none.
        /// </summary>
        public static string FoldAddresses(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            StringBuilder builder = null;
            int copied = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0 && char.IsLetterOrDigit(text[i - 1]))
                {
                    continue;
                }

                bool prefixed = i + 1 < text.Length && text[i] == '0' && (text[i + 1] == 'x' || text[i + 1] == 'X');
                int start = prefixed ? i + 2 : i, j = start;
                bool digit = false;
                while (j < text.Length && IsHex(text[j]))
                {
                    digit |= char.IsDigit(text[j]);
                    j++;
                }

                int length = j - start;
                bool whole = j == text.Length || !char.IsLetterOrDigit(text[j]);
                if (!whole || !digit || (prefixed ? length < 6 || length > 16 : length != 16))
                {
                    continue;
                }

                builder = builder ?? new StringBuilder(text.Length);
                builder.Append(text, copied, i - copied).Append('#');
                copied = j;
                i = j - 1;
            }

            return builder == null ? text : builder.Append(text, copied, text.Length - copied).ToString();
        }

        /// <summary>The text with numbers, hashes and ids replaced by <c>#</c>: "Loaded 12 assets in 3.4 ms" → "Loaded # assets in # ms".</summary>
        public static string Similar(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                bool boundary = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                if (boundary && IsHex(c))
                {
                    int j = i;
                    bool digit = false;
                    while (j < text.Length && (IsHex(text[j]) || text[j] == '-'))
                    {
                        digit |= char.IsDigit(text[j]);
                        j++;
                    }

                    if (j - i >= 8 && digit && (j == text.Length || !char.IsLetterOrDigit(text[j])))
                    {
                        builder.Append('#');
                        i = j;
                        continue;
                    }
                }

                if (char.IsDigit(c))
                {
                    while (i < text.Length && (char.IsDigit(text[i]) || (text[i] == '.' || text[i] == ',') && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                    {
                        i++;
                    }

                    builder.Append('#');
                    continue;
                }

                builder.Append(c);
                i++;
            }

            return builder.ToString();
        }

        private static bool IsHex(char c) => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';

        /// <summary>Keeps the newest <paramref name="keep"/> occurrences.</summary>
        public void Trim(int keep)
        {
            keep = Math.Max(0, Math.Min(keep, count));
            int drop = count - keep;
            if (drop <= 0)
            {
                return;
            }

            int capacity = Math.Max(64, Math.Min(times.Length, keep * 2));
            var nextTimes = new long[capacity];
            var nextOwners = new int[capacity];
            var nextFlags = new byte[capacity];
            Array.Copy(times, drop, nextTimes, 0, keep);
            Array.Copy(owners, drop, nextOwners, 0, keep);
            Array.Copy(flags, drop, nextFlags, 0, keep);
            times = nextTimes;
            owners = nextOwners;
            flags = nextFlags;
            count = keep;
            ShiftContexts(-drop);
            long first = count > 0 ? times[0] : 0L;
            events.RemoveAll(e => e.Time < first);
            RecountAll();
            Epoch++;
            Version++;
            Shifted?.Invoke(-drop);
        }

        /// <summary>
        /// Puts occurrences that happened before every occurrence in the store (Unity's console history) in front of them.
        /// </summary>
        public void Prepend(long[] historyTimes, int[] historyOwners, byte[] historyFlags, int historyCount, Dictionary<int, int> historyContexts)
        {
            if (historyCount <= 0)
            {
                return;
            }

            int total = historyCount + count;
            int capacity = Math.Max(total + total / 4, 1024);
            var nextTimes = new long[capacity];
            var nextOwners = new int[capacity];
            var nextFlags = new byte[capacity];
            Array.Copy(historyTimes, nextTimes, historyCount);
            Array.Copy(historyOwners, nextOwners, historyCount);
            Array.Copy(historyFlags, nextFlags, historyCount);
            Array.Copy(times, 0, nextTimes, historyCount, count);
            Array.Copy(owners, 0, nextOwners, historyCount, count);
            Array.Copy(flags, 0, nextFlags, historyCount, count);

            // History is older than what the store already holds: keep times in order.
            long limit = count > 0 ? times[0] : long.MaxValue;
            long previous = long.MinValue;
            for (int i = 0; i < historyCount; i++)
            {
                long t = Math.Min(nextTimes[i], limit);
                if (t < previous)
                {
                    t = previous;
                }

                nextTimes[i] = t;
                previous = t;
            }

            times = nextTimes;
            owners = nextOwners;
            flags = nextFlags;
            count = total;
            ShiftContexts(historyCount);
            if (historyContexts != null)
            {
                foreach (var pair in historyContexts)
                {
                    contexts[pair.Key] = pair.Value;
                }
            }

            RecountAll();
            Epoch++;
            Version++;
            Shifted?.Invoke(historyCount);
            if (count > maxOccurrences)
            {
                Trim(maxOccurrences * 4 / 5);
            }
        }

        private void ShiftContexts(int offset)
        {
            if (contexts.Count == 0)
            {
                return;
            }

            var shifted = new List<KeyValuePair<int, int>>(contexts);
            contexts.Clear();
            foreach (var pair in shifted)
            {
                int index = pair.Key + offset;
                if (index >= 0 && index < count)
                {
                    contexts[index] = pair.Value;
                }
            }
        }

        private void Grow(int capacity)
        {
            var nextTimes = new long[capacity];
            var nextOwners = new int[capacity];
            var nextFlags = new byte[capacity];
            Array.Copy(times, nextTimes, count);
            Array.Copy(owners, nextOwners, count);
            Array.Copy(flags, nextFlags, count);
            // Readers may still hold the old arrays: their prefix stays valid.
            flags = nextFlags;
            owners = nextOwners;
            times = nextTimes;
        }

        /// <summary>Recomputes every message's and group's counts from the occurrences.</summary>
        public void RecountAll()
        {
            for (int i = 0; i < messageCount; i++)
            {
                messages[i].Count = 0;
                messages[i].First = -1;
                messages[i].Last = -1;
            }

            for (int i = 0; i < count; i++)
            {
                ref var message = ref messages[owners[i]];
                if (message.Count == 0)
                {
                    message.First = i;
                }

                message.Count++;
                message.Last = i;
            }

            RecountGroups();
        }

        private void RecountGroups()
        {
            for (int i = 0; i < groupCount; i++)
            {
                ref var group = ref groups[i];
                group.Count = 0;
                group.First = -1;
                group.Last = -1;
                group.Latest = -1;
            }

            for (int i = 0; i < messageCount; i++)
            {
                var message = messages[i];
                if (message.Count == 0)
                {
                    continue;
                }

                ref var group = ref groups[message.Group];
                group.Count += message.Count;
                if (group.First < 0 || message.First < group.First)
                {
                    group.First = message.First;
                }

                if (message.Last > group.Last)
                {
                    group.Last = message.Last;
                    group.Latest = i;
                }
            }
        }

        /// <summary>The first occurrence at or after <paramref name="time"/> (binary search: times are in order).</summary>
        public int LowerBound(long time) => LowerBound(times, count, time);

        public static int LowerBound(long[] values, int length, long time)
        {
            int lo = 0;
            int hi = length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (values[mid] < time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        // ---- Session snapshot ------------------------------------------------------------------------------------

        internal void ExportColumns(out long[] exportTimes, out int[] exportOwners, out byte[] exportFlags)
        {
            exportTimes = times;
            exportOwners = owners;
            exportFlags = flags;
        }

        internal LogMessage[] MessagesArray => messages;
        internal Dictionary<int, int> Contexts => contexts;

        /// <summary>Rebuilds a store from saved columns (see <see cref="LogSnapshot"/>).</summary>
        internal void Import(long[] importTimes, int[] importOwners, byte[] importFlags, int importCount, LogMessage[] importMessages, int importMessageCount,
            GroupingMode grouping, Dictionary<int, int> importContexts, List<SessionEvent> importEvents)
        {
            times = importTimes;
            owners = importOwners;
            flags = importFlags;
            count = importCount;
            Grouping = grouping;
            messages = importMessages;
            messageCount = importMessageCount;
            messageIndex.Clear();
            groups = new LogGroup[Math.Max(256, importMessageCount)];
            groupCount = 0;
            groupIndex.Clear();
            for (int i = 0; i < messageCount; i++)
            {
                ref var message = ref messages[i];
                messageIndex[new MessageKey(message.Text, message.Stack, message.Variant)] = i;
                message.Group = GroupFor(message.Text, message.Level);
                message.Explanation = -2;
                groups[message.Group].Variants++;
            }

            contexts.Clear();
            if (importContexts != null)
            {
                foreach (var pair in importContexts)
                {
                    contexts[pair.Key] = pair.Value;
                }
            }

            events.Clear();
            if (importEvents != null)
            {
                events.AddRange(importEvents);
            }

            RecountAll();
            Epoch++;
            Version++;
            MessagesVersion++;
        }

        internal void ReplaceTexts(TextTable texts) => Texts = texts;
    }
}
