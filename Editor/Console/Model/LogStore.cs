using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

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
        /// <summary>A timing or a command's log is never the same message as a plain log with that text.</summary>
        public readonly MessageFlags Kind;

        public MessageKey(int text, int stack, LogVariant variant, MessageFlags kind = MessageFlags.None)
        {
            Text = text;
            Stack = stack;
            Variant = variant;
            Kind = kind & LogStore.KindFlags;
        }

        /// <summary>The key a filed message is found by.</summary>
        public static MessageKey Of(in LogMessage message) => new MessageKey(message.Text, message.Stack, message.Variant, message.Flags);

        public bool Equals(MessageKey other) => Text == other.Text && Stack == other.Stack && Variant == other.Variant && Kind == other.Kind;
        public override bool Equals(object obj) => obj is MessageKey other && Equals(other);
        public override int GetHashCode() => unchecked((Text * 397) ^ (Stack * 7919) ^ (int)Variant ^ ((int)Kind << 24));
    }

    /// <summary>
    /// What a worker thread reads: the occurrence chunks and counts taken together on the main thread. Occurrences are
    /// read by id, from <see cref="First"/> to <see cref="End"/>; a chunk's slots for those ids never change but for
    /// their flags (a compile message superseded), so the view stays valid while logs keep arriving.
    /// </summary>
    internal readonly struct StoreView
    {
        public readonly long[][] TimeChunks;
        public readonly int[][] OwnerChunks;
        public readonly byte[][] FlagChunks;
        /// <summary>The id of chunk 0's first slot.</summary>
        public readonly int ChunkBase;
        public readonly int First;
        public readonly int End;
        public readonly LogMessage[] Messages;
        public readonly int MessageCount;
        public readonly LogGroup[] Groups;
        public readonly int GroupCount;
        public readonly TextTable Texts;
        public readonly int Epoch;
        public readonly int MessagesVersion;

        public StoreView(long[][] times, int[][] owners, byte[][] flags, int chunkBase, int first, int end, LogMessage[] messages, int messageCount,
            LogGroup[] groups, int groupCount, TextTable texts, int epoch, int messagesVersion)
        {
            TimeChunks = times;
            OwnerChunks = owners;
            FlagChunks = flags;
            ChunkBase = chunkBase;
            First = first;
            End = end;
            Messages = messages;
            MessageCount = messageCount;
            Groups = groups;
            GroupCount = groupCount;
            Texts = texts;
            Epoch = epoch;
            MessagesVersion = messagesVersion;
        }

        public int Count => End - First;

        public long TimeAt(int id)
        {
            int slot = id - ChunkBase;
            return TimeChunks[slot >> LogStore.ChunkShift][slot & LogStore.ChunkMask];
        }

        public int OwnerAt(int id)
        {
            int slot = id - ChunkBase;
            return OwnerChunks[slot >> LogStore.ChunkShift][slot & LogStore.ChunkMask];
        }

        public byte FlagsAt(int id)
        {
            int slot = id - ChunkBase;
            return FlagChunks[slot >> LogStore.ChunkShift][slot & LogStore.ChunkMask];
        }

        /// <summary>
        /// How many ids from <paramref name="id"/> (before <paramref name="to"/>) lie in one chunk, and where: scans walk a
        /// chunk's arrays directly.
        /// </summary>
        public int Run(int id, int to, out int chunk, out int offset)
        {
            int slot = id - ChunkBase;
            chunk = slot >> LogStore.ChunkShift;
            offset = slot & LogStore.ChunkMask;
            return Math.Min(to - id, LogStore.ChunkSize - offset);
        }
    }

    /// <summary>
    /// Every log of the session, column by column: a time, a message id and flags per occurrence (13 bytes), distinct
    /// texts stored once. Occurrences are appended in time order and keep their id for as long as they are in the store:
    /// ids run from <see cref="FirstId"/> to <see cref="EndId"/>, a trim moves the first one up (whole chunks of the
    /// columns go, nothing is copied) and history put in front takes the ids below it. Only a clear or a renumbering
    /// (<see cref="Emptied"/>) changes what an id means. Only the main thread changes the store; worker threads read a
    /// <see cref="StoreView"/>.
    /// </summary>
    internal sealed partial class LogStore
    {
        public const int ChunkShift = 16;
        /// <summary>Occurrences per chunk of the columns: what a trim drops at once.</summary>
        public const int ChunkSize = 1 << ChunkShift;
        public const int ChunkMask = ChunkSize - 1;
        /// <summary>
        /// The first id of LogCapture's stores: room for Unity's console history in front of the first live log without
        /// renumbering anything (a store that starts lower renumbers when history doesn't fit).
        /// </summary>
        public const int HistoryRoom = 1 << 30;
        // Past this the ids start again from the first one (about a billion logs later), as a clear would.
        internal const int MaxEnd = int.MaxValue - 2 * ChunkSize;
        public const int DefaultMaxOccurrences = 3_000_000;
        /// <summary>The texts kept alive at most (256 MB of UTF-16), whatever the number of logs: see <see cref="Append"/>.</summary>
        public const long DefaultMaxTextChars = 128L * 1024 * 1024;
        /// <summary>
        /// The longest text kept (2 MB of UTF-16): a longer message or stack trace keeps its start and says how much was
        /// left out, so one log can never take the whole <see cref="MaxTextChars"/> budget.
        /// </summary>
        public const int MaxTextLength = 1 << 20;
        /// <summary>The source of timing logs (script reloads, Play Mode, avatar builds and uploads).</summary>
        public const string TimingSource = "Timings";
        public const OccurrenceFlags Superseded = (OccurrenceFlags)8;

        // The columns, a chunk of ChunkSize occurrences each: chunk c holds ids chunkBase + c * ChunkSize onwards. A trim
        // drops whole chunks; a chunk's slots are written once (its flags excepted), so views taken earlier stay valid.
        // The directories are replaced, never changed in place below chunkCount.
        private long[][] timeChunks = new long[8][];
        private int[][] ownerChunks = new int[8][];
        private byte[][] flagChunks = new byte[8][];
        // The object each log is about (an instance id, 0 for none): allocated for a chunk on its first context.
        private int[][] contextChunks = new int[8][];
        private int chunkCount;
        private int chunkBase;
        private int first;
        private int end;
        private readonly int initialFirst;
        // The lowest id ever given since ids last changed meaning: history in front never takes an id a trimmed log had.
        private int lowestIssued;
        private LogMessage[] messages = new LogMessage[256];
        private int messageCount;
        // Messages without any occurrence (trimmed away, or interned for an import that hasn't put its logs in yet).
        private int zeroCount;
        private Dictionary<MessageKey, int> messageIndex = new Dictionary<MessageKey, int>();
        private LogGroup[] groups = new LogGroup[256];
        private int groupCount;
        private Dictionary<long, int> groupIndex = new Dictionary<long, int>();
        private readonly List<SessionEvent> events = new List<SessionEvent>();
        private int maxOccurrences = DefaultMaxOccurrences;
        private long maxTextChars = DefaultMaxTextChars;
        private StoreCompaction compaction;

        public LogStore() : this(0)
        {
        }

        /// <summary>A store whose first occurrence gets <paramref name="firstId"/> (see <see cref="HistoryRoom"/>).</summary>
        public LogStore(int firstId)
        {
            initialFirst = Math.Max(0, Math.Min(firstId, MaxEnd - 1));
            first = end = lowestIssued = initialFirst;
            Texts = new TextTable();
        }

        public TextTable Texts { get; private set; }
        public SourceCatalog Sources { get; } = new SourceCatalog();
        public GroupingMode Grouping { get; private set; }
        /// <summary>Occurrences in the store.</summary>
        public int Count => end - first;
        /// <summary>The oldest occurrence's id (the newest is <see cref="EndId"/> - 1).</summary>
        public int FirstId => first;
        /// <summary>The id the next occurrence gets.</summary>
        public int EndId => end;
        /// <summary>Where ids start after a clear.</summary>
        public int InitialFirstId => initialFirst;
        public int MessageCount => messageCount;
        public int GroupCount => groupCount;
        /// <summary>Bumps on every append.</summary>
        public int Version { get; private set; }
        /// <summary>
        /// Bumps when message or group ids change meaning (clear, regroup, compaction) or occurrence ids do (clear,
        /// renumbering). A trim or history put in front moves <see cref="FirstId"/> instead.
        /// </summary>
        public int Epoch { get; private set; }
        /// <summary>Bumps when occurrence ids change meaning: clear, renumbering, a store put back.</summary>
        public int IdEpoch { get; private set; }
        /// <summary>Bumps when a message's flags change (compile errors resolved): filters must be recomputed.</summary>
        public int MessagesVersion { get; private set; }
        /// <summary>
        /// Changes whenever an occurrence's time or message changes (clear, history merge, compaction, renumbering): until
        /// then occurrences are only appended (a trim only drops the oldest), so the session file appends them too.
        /// </summary>
        public long RowsGeneration { get; private set; } = NewRowsGeneration();
        public long FirstTime => end > first ? TimeAt(first) : 0L;
        public long LastTime => end > first ? TimeAt(end - 1) : 0L;
        public IReadOnlyList<SessionEvent> Events => events;

        /// <summary>Everything was cleared, or every occurrence renumbered: no earlier occurrence or group id means anything any more.</summary>
        public event Action Emptied;

        /// <summary>Messages were regrouped (or compacted): group ids changed.</summary>
        public event Action Regrouped;

        public int MaxOccurrences
        {
            get => maxOccurrences;
            set => maxOccurrences = Math.Max(10_000, value);
        }

        /// <summary>How many characters of distinct texts the store keeps before the oldest logs go (tests lower it).</summary>
        internal long MaxTextChars
        {
            get => maxTextChars;
            set
            {
                maxTextChars = Math.Max(1L << 16, value);
            }
        }

        /// <summary>
        /// Where compactions run: on a worker, landing a few milliseconds at a time through <see cref="StepMaintenance"/>
        /// (LogCapture's store), or right away in the call that made messages dead (the default, for tests and tools).
        /// </summary>
        internal bool CompactInBackground { get; set; }

        /// <summary>Tests: never compact (a reference store to compare a compacted one with).</summary>
        internal bool AllowCompaction { get; set; } = true;

        /// <summary>A compaction is being prepared (see <see cref="StepMaintenance"/>).</summary>
        internal bool Compacting => compaction != null;

        public bool Contains(int occurrence) => occurrence >= first && occurrence < end;

        public long TimeAt(int occurrence)
        {
            int slot = occurrence - chunkBase;
            return timeChunks[slot >> ChunkShift][slot & ChunkMask];
        }

        public int MessageAt(int occurrence)
        {
            int slot = occurrence - chunkBase;
            return ownerChunks[slot >> ChunkShift][slot & ChunkMask];
        }

        public OccurrenceFlags FlagsAt(int occurrence)
        {
            int slot = occurrence - chunkBase;
            return (OccurrenceFlags)flagChunks[slot >> ChunkShift][slot & ChunkMask];
        }

        public ref LogMessage Message(int id) => ref messages[id];
        public ref LogGroup Group(int id) => ref groups[id];
        public LogSource SourceOf(int message) => Sources[messages[message].Source];

        public StoreView View() => new StoreView(timeChunks, ownerChunks, flagChunks, chunkBase, first, end, messages, messageCount, groups, groupCount,
            Texts, Epoch, MessagesVersion);

        public bool TryGetContext(int occurrence, out int instanceId)
        {
            instanceId = 0;
            if (!Contains(occurrence))
            {
                return false;
            }

            int slot = occurrence - chunkBase;
            var chunk = contextChunks[slot >> ChunkShift];
            instanceId = chunk != null ? chunk[slot & ChunkMask] : 0;
            return instanceId != 0;
        }

        /// <summary>The message flags that make a kind of message (part of its key).</summary>
        public const MessageFlags KindFlags = MessageFlags.Timing | MessageFlags.Output;

        /// <summary>Commands' outputs kept, by occurrence (the oldest goes first): a tool that polls stays bounded.</summary>
        internal const int MaxPayloads = 2000;

        // Texts of one occurrence each (a command's output), by occurrence id, and the ids in the order they came.
        private Dictionary<int, int> payloads = new Dictionary<int, int>();
        private Queue<int> payloadOrder = new Queue<int>();

        /// <summary>What the command of this occurrence printed (<see cref="AddOutput"/>), or null when it isn't kept.</summary>
        public string OutputOf(int occurrence) => Contains(occurrence) && payloads.TryGetValue(occurrence, out int text) ? Texts[text] : null;

        private void SetPayload(int occurrence, int text)
        {
            if (!payloads.ContainsKey(occurrence))
            {
                payloadOrder.Enqueue(occurrence);
            }

            payloads[occurrence] = text;
            while (payloadOrder.Count > MaxPayloads)
            {
                payloads.Remove(payloadOrder.Dequeue());
            }
        }

        private void DropPayloadsBelow(int id)
        {
            while (payloadOrder.Count > 0 && payloadOrder.Peek() < id)
            {
                payloads.Remove(payloadOrder.Dequeue());
            }
        }

        public void SetContext(int occurrence, int instanceId)
        {
            if (!Contains(occurrence) || instanceId == 0)
            {
                return;
            }

            int slot = occurrence - chunkBase;
            ref var chunk = ref contextChunks[slot >> ChunkShift];
            chunk = chunk ?? new int[ChunkSize];
            chunk[slot & ChunkMask] = instanceId;
        }

        /// <summary>The file and line Unity's console gave a message whose stack trace named none (the mirror's).</summary>
        public void SetFileIfUnknown(int message, string file, int line)
        {
            ref var entry = ref messages[message];
            if (entry.File != 0 || string.IsNullOrEmpty(file))
            {
                return;
            }

            entry.File = Texts.Intern(StackTraces.NormalizePath(file));
            entry.Line = line;
            if (entry.Callsite == 0)
            {
                entry.Callsite = Texts.Intern(StackTraces.FileName(file) + (line > 0 ? ":" + line : string.Empty));
            }

            compaction?.Patched(message);
        }

        /// <summary>The assembly a compiler message belongs to, learnt after it was filed.</summary>
        public void SetAssembly(int message, int assembly)
        {
            messages[message].Assembly = assembly;
            compaction?.Patched(message);
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

        /// <summary>
        /// A timing log (a script reload, Play Mode, an avatar build or upload): the profile as it was measured, kept by
        /// the store for every window; its text form is the message's "stack trace", for copies, searches, the report and
        /// the session file (read back once after a reload, see <see cref="TimingOf"/>).
        /// </summary>
        public int AddTiming(long time, TimingProfile profile, LogVariant variant)
        {
            pendingProfile = profile;
            int id = Intern(profile.Message(), profile.Format(), variant, kind: MessageFlags.Timing);
            pendingProfile = null;
            return Append(time, id);
        }

        /// <summary>
        /// A command another tool ran (Mixer Git's): one message per command line and result, what it printed (its
        /// duration first) kept with this occurrence only, so runs that differ only by their output or time stay one message.
        /// </summary>
        public int AddOutput(long time, string condition, string output, LogVariant variant)
        {
            int id = Append(time, Intern(condition, string.Empty, variant, kind: MessageFlags.Output));
            if (Contains(id) && !string.IsNullOrEmpty(output))
            {
                SetPayload(id, Texts.Intern(Capped(output)));
            }

            return id;
        }

        /// <summary>The id of the message with this text, stack trace and type, created when new.</summary>
        public int Intern(string condition, string stack, LogVariant variant, int assembly = 0, string file = null, int line = 0, int column = 0,
            MessageFlags kind = MessageFlags.None)
        {
            int text = Texts.Intern(Capped(condition) ?? string.Empty);
            int stackId = Texts.Intern(Capped(stack) ?? string.Empty);
            var key = new MessageKey(text, stackId, variant, kind);
            if (messageIndex.TryGetValue(key, out int id))
            {
                return id;
            }

            if (messageCount == messages.Length)
            {
                var next = new LogMessage[messages.Length * 2];
                Array.Copy(messages, next, messageCount);
                Volatile.Write(ref messages, next);
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
                Flags = (LogVariants.IsCompile(variant) ? MessageFlags.Compile : MessageFlags.None) | (kind & KindFlags)
            };
            Describe(ref message, condition, stack, file, line, column);
            message.Group = GroupFor(text, message.Level);
            messages[id] = message;
            // Published after the message is whole: a compaction worker reads the messages before this count.
            Volatile.Write(ref messageCount, id + 1);
            zeroCount++;
            if ((message.Flags & MessageFlags.Timing) != 0 && pendingProfile != null)
            {
                profiles[id] = pendingProfile;
            }

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

            if ((message.Flags & MessageFlags.Timing) != 0)
            {
                // A timing log (filed by AddTiming, never told by its text): its largest shares under the message.
                message.Source = Sources.Get(TimingSource);
                message.Callsite = Texts.Intern(pendingProfile != null ? pendingProfile.Summary(4) : string.Empty);
                return;
            }

            if ((message.Flags & MessageFlags.Output) != 0)
            {
                // A command's log (AddOutput): each run's timing line comes from its own output (OutputOf).
                message.Source = Sources.Resolve(message.Variant, condition, -1);
                return;
            }


            var info = StackInfoOf(message.Stack, stack);
            message.Source = Sources.Resolve(message.Variant, condition, info.Source);
            if (info.HasCallsite)
            {
                message.File = info.File;
                message.Line = info.Line;
                message.Callsite = info.Callsite;
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

        // What a stack trace says about who logged, parsed once per distinct stack: a flood of logs whose text changes
        // (a counter, an id) from one line of code parses its stack once, not once per log.
        private struct StackInfo
        {
            public int Source;
            public bool HasCallsite;
            public int Callsite;
            public int File;
            public int Line;
        }

        private readonly Dictionary<int, StackInfo> stackInfos = new Dictionary<int, StackInfo>();

        // Timing logs' profiles by message id, parsed once for every window (a restored store parses them on first use).
        private readonly Dictionary<int, TimingProfile> profiles = new Dictionary<int, TimingProfile>();
        private TimingProfile pendingProfile;

        /// <summary>The profile of a timing log (<see cref="MessageFlags.Timing"/>), or null.</summary>
        public TimingProfile TimingOf(int message)
        {
            if (message < 0 || message >= messageCount || (messages[message].Flags & MessageFlags.Timing) == 0)
            {
                return null;
            }

            if (!profiles.TryGetValue(message, out var profile))
            {
                profile = TimingProfile.Parse(Texts[messages[message].Stack]);
                profiles[message] = profile;
            }

            return profile;
        }
        private TextTable stackInfoTexts;

        private StackInfo StackInfoOf(int stackId, string stack)
        {
            if (!ReferenceEquals(stackInfoTexts, Texts))
            {
                // Text ids changed meaning (cleared, compacted).
                stackInfos.Clear();
                stackInfoTexts = Texts;
            }

            if (stackInfos.TryGetValue(stackId, out var info))
            {
                return info;
            }

            var frames = StackTraces.Parse(stack);
            int callsite = StackTraces.FindCallsite(frames);
            info = new StackInfo { Source = Sources.FromFrames(frames, callsite), HasCallsite = callsite >= 0 };
            if (callsite >= 0)
            {
                var frame = frames[callsite];
                string method = StackTraces.ShortMethod(frame.Method);
                if (frame.HasFile)
                {
                    string path = StackTraces.NormalizePath(frame.File);
                    info.File = Texts.Intern(path);
                    info.Line = frame.Line;
                    info.Callsite = Texts.Intern(method + "  ·  " + StackTraces.FileName(path) + ":" + frame.Line);
                }
                else
                {
                    info.Callsite = Texts.Intern(method);
                }
            }

            stackInfos[stackId] = info;
            return info;
        }

        /// <summary>
        /// Adds an occurrence of <paramref name="message"/> and returns its id, which stays its id while it is in the
        /// store. Times never go back: an earlier time is moved up to the last one.
        /// </summary>
        public int Append(long time, int message, OccurrenceFlags occurrenceFlags = OccurrenceFlags.None)
        {
            if (end >= MaxEnd)
            {
                Renumber(Math.Min(initialFirst, HistoryRoom));
            }

            if (end > first)
            {
                long last = TimeAt(end - 1);
                if (time < last)
                {
                    time = last;
                }
            }

            int id = end;
            if (chunkCount == 0)
            {
                chunkBase = id;
            }

            int slot = id - chunkBase;
            int chunk = slot >> ChunkShift;
            if (chunk == chunkCount)
            {
                AddChunk();
            }

            timeChunks[chunk][slot & ChunkMask] = time;
            ownerChunks[chunk][slot & ChunkMask] = message;
            flagChunks[chunk][slot & ChunkMask] = (byte)occurrenceFlags;
            CountIn(message, id);
            // Published after the slot is written: a compaction worker reads the occurrences before this id.
            Volatile.Write(ref end, id + 1);
            Version++;
            if (Count > maxOccurrences && (compaction == null || Count > maxOccurrences + maxOccurrences / 5))
            {
                // The oldest fifth goes: the new occurrence is still the last one. While a compaction is prepared the trim
                // waits for it (it would start it over), up to a fifth more logs: then it goes ahead and the compaction
                // starts again from the trimmed store.
                Trim(maxOccurrences * 4 / 5);
            }
            else if (Texts.TotalChars > maxTextChars && pins == 0)
            {
                // Logs that never repeat (an id, a time in the text) keep a new text each: memory is bounded by their
                // size, not only their number.
                KeepTextBudget();
            }

            return id;
        }

        // A message's and its group's counters take occurrence `id`, the newest of both.
        private void CountIn(int message, int id)
        {
            ref var entry = ref messages[message];
            if (entry.Count == 0)
            {
                entry.First = id;
                zeroCount--;
            }

            entry.Count++;
            entry.Last = id;
            ref var group = ref groups[entry.Group];
            if (group.Count == 0)
            {
                group.First = id;
            }

            group.Count++;
            group.Last = id;
            group.Latest = message;
        }

        private void AddChunk()
        {
            if (chunkCount == timeChunks.Length)
            {
                // New directories: a view holds the old ones, whose first chunkCount entries stay as they are.
                int size = timeChunks.Length * 2;
                timeChunks = Resized(timeChunks, size);
                ownerChunks = Resized(ownerChunks, size);
                flagChunks = Resized(flagChunks, size);
                contextChunks = Resized(contextChunks, size);
            }

            timeChunks[chunkCount] = new long[ChunkSize];
            ownerChunks[chunkCount] = new int[ChunkSize];
            flagChunks[chunkCount] = new byte[ChunkSize];
            contextChunks[chunkCount] = null;
            chunkCount++;
        }

        private static T[] Resized<T>(T[] array, int size)
        {
            var next = new T[size];
            Array.Copy(array, next, Math.Min(array.Length, size));
            return next;
        }
        /// <summary>The text as stored: itself, or its first <see cref="MaxTextLength"/> characters and how many were left out.</summary>
        public static string Capped(string text)
        {
            if (text == null || text.Length <= MaxTextLength)
            {
                return text;
            }

            // The note included, never longer than the limit: capping a capped text changes nothing, whatever path
            // (the queue, the store, a history import) cut it first.
            int keep = MaxTextLength - CutNote(text.Length - MaxTextLength).Length;
            while (keep > 0 && keep + CutNote(text.Length - keep).Length > MaxTextLength)
            {
                keep--;
            }

            if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            return text.Substring(0, keep) + CutNote(text.Length - keep);
        }

        private static string CutNote(int left) =>
            "\n[… " + left.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " more characters left out by the Mixer console]";

        /// <summary>
        /// Keeps the texts within <see cref="MaxTextChars"/> when they grew without a new log (texts read back after a
        /// reload keep their stripped copies). Main thread; cheap when within the budget.
        /// </summary>
        public void KeepWithinTextBudget()
        {
            if (Texts.TotalChars > maxTextChars && pins == 0)
            {
                KeepTextBudget();
            }
        }

        // One fixed budget for every text kept (live logs and history alike, stripped copies included): the oldest
        // fifth of the logs goes until the texts fit, each trim compacting what it made dead. The newest log stays.
        // A compaction prepared in the background stops the loop: it lands, then checks the budget again (so the texts
        // can exceed it by what arrives while it is prepared, never by more).
        private void KeepTextBudget()
        {
            while (Texts.TotalChars > maxTextChars && Count > 1 && pins == 0 && !Texts.Faulted && compaction == null)
            {
                Trim(Count * 4 / 5);
            }
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
                compaction?.Patched(id);
            }

            if (message.Count > 0 && Contains(message.Last))
            {
                int slot = message.Last - chunkBase;
                flagChunks[slot >> ChunkShift][slot & ChunkMask] |= (byte)Superseded;
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
                compaction?.Patched(i);
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

            Reset();
            foreach (var (text, error, assembly, file, line, column) in keep)
            {
                AddCompile(now, text, error, AssemblyId(assembly), file, line, column);
            }
        }

        private void Reset()
        {
            CancelCompaction();
            timeChunks = new long[8][];
            ownerChunks = new int[8][];
            flagChunks = new byte[8][];
            contextChunks = new int[8][];
            chunkCount = 0;
            first = end = lowestIssued = initialFirst;
            messages = new LogMessage[256];
            messageCount = 0;
            zeroCount = 0;
            messageIndex.Clear();
            groups = new LogGroup[256];
            groupCount = 0;
            groupIndex.Clear();
            events.Clear();
            profiles.Clear();
            payloads.Clear();
            payloadOrder.Clear();
            pins = 0;
            Texts = new TextTable();
            RowsGeneration = NewRowsGeneration();
            Epoch++;
            IdEpoch++;
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

            CancelCompaction();
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

        private int GroupFor(int text, LogLevel level) => GroupFor(Texts, Grouping, ref groups, ref groupCount, groupIndex, text, level);

        /// <summary>The group of a message with this text and level in these tables, made when new (the compaction's too).</summary>
        internal static int GroupFor(TextTable texts, GroupingMode grouping, ref LogGroup[] groups, ref int groupCount, Dictionary<long, int> groupIndex,
            int text, LogLevel level)
        {
            int keyText = grouping == GroupingMode.SameText ? AddressesFolded(texts, text) : texts.Intern(Similar(texts.Plain(text)));
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
        private static int AddressesFolded(TextTable texts, int text)
        {
            string plain = texts.Plain(text);
            string folded = FoldAddresses(plain);
            return ReferenceEquals(folded, plain) ? text : texts.Intern(folded);
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

        private int pins;

        /// <summary>
        /// Keeps message ids meaning the same message until <see cref="Unpin"/> (no compaction): for a history import
        /// that interns messages long before it adds their occurrences. A clear forgets every pin.
        /// </summary>
        public void Pin()
        {
            pins++;
            // One being prepared would land with the import's messages dropped (they have no occurrence yet).
            CancelCompaction();
        }

        // No budget check here: an import unpins just before it puts its occurrences in (Prepend), and a compaction in
        // between would drop the messages it is about to use. Prepend and the next append check the budget.
        public void Unpin() => pins = Math.Max(0, pins - 1);

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> occurrences: the first id moves up, whole chunks below it go, and only
        /// the dropped occurrences are visited to fix the counters. A message left without occurrences reads Count 0
        /// (and First and Last -1); Last stays exact, and First becomes a bound: no occurrence of the message is older,
        /// though it can be (below <see cref="FirstId"/>) when the message had none among the dropped ones. A group's
        /// First likewise. Exact again once a compaction recounts.
        /// </summary>
        public void Trim(int keep)
        {
            keep = Math.Max(0, Math.Min(keep, Count));
            int newFirst = end - keep;
            if (newFirst <= first)
            {
                return;
            }

            // A compaction being prepared counted the occurrences that go: it starts over from the trimmed store.
            CancelCompaction();
            var owners = ownerChunks;
            for (int id = first; id < newFirst;)
            {
                int slot = id - chunkBase;
                int chunk = slot >> ChunkShift;
                int offset = slot & ChunkMask;
                int run = Math.Min(newFirst - id, ChunkSize - offset);
                var column = owners[chunk];
                for (int i = offset; i < offset + run; i++)
                {
                    CountOut(column[i], newFirst);
                }

                id += run;
            }

            first = newFirst;
            DropChunksBelow(first);
            DropPayloadsBelow(first);
            long firstTime = FirstTime;
            events.RemoveAll(e => e.Time < firstTime);
            Version++;
            MaybeCompact();
        }

        private void CountOut(int message, int newFirst)
        {
            ref var entry = ref messages[message];
            if (--entry.Count == 0)
            {
                entry.First = entry.Last = -1;
                zeroCount++;
            }
            else if (entry.First < newFirst)
            {
                entry.First = newFirst;
            }

            ref var group = ref groups[entry.Group];
            if (--group.Count == 0)
            {
                group.First = group.Last = group.Latest = -1;
            }
            else if (group.First < newFirst)
            {
                group.First = newFirst;
            }
        }

        // Chunks wholly below `id` go (new directories: views keep theirs).
        private void DropChunksBelow(int id)
        {
            int drop = Math.Min(chunkCount, (id - chunkBase) >> ChunkShift);
            if (drop <= 0)
            {
                return;
            }

            int left = chunkCount - drop;
            int size = Math.Max(8, left + 8);
            timeChunks = Shifted(timeChunks, drop, left, size);
            ownerChunks = Shifted(ownerChunks, drop, left, size);
            flagChunks = Shifted(flagChunks, drop, left, size);
            contextChunks = Shifted(contextChunks, drop, left, size);
            chunkCount = left;
            chunkBase += drop << ChunkShift;
        }

        private static T[] Shifted<T>(T[] array, int from, int length, int size)
        {
            var next = new T[size];
            Array.Copy(array, from, next, 0, length);
            return next;
        }

        /// <summary>
        /// Puts occurrences that happened before every occurrence in the store (Unity's console history) in front of them:
        /// they take the ids below <see cref="FirstId"/>, so no id in the store changes (a store without room for them
        /// below renumbers first). <paramref name="historyContexts"/> are keyed by position in the history.
        /// </summary>
        public void Prepend(long[] historyTimes, int[] historyOwners, byte[] historyFlags, int historyCount, Dictionary<int, int> historyContexts)
        {
            if (historyCount <= 0)
            {
                return;
            }

            CancelCompaction();
            if (first < historyCount)
            {
                Renumber(historyCount);
            }
            else if (first > lowestIssued)
            {
                // Logs were trimmed since: the ids below the first one were theirs. History takes them, so whatever
                // remembers one of those ids must forget it, as after a clear (a trim during an import, under a flood).
                ForgetIds();
            }

            int newFirst = first - historyCount;
            long limit = Count > 0 ? TimeAt(first) : long.MaxValue;
            PrepareFront(newFirst);
            long previous = long.MinValue;
            for (int i = 0; i < historyCount; i++)
            {
                // History is older than what the store already holds: keep times in order.
                long t = Math.Min(historyTimes[i], limit);
                if (t < previous)
                {
                    t = previous;
                }

                previous = t;
                int id = newFirst + i;
                int slot = id - chunkBase;
                int chunk = slot >> ChunkShift;
                timeChunks[chunk][slot & ChunkMask] = t;
                ownerChunks[chunk][slot & ChunkMask] = historyOwners[i];
                flagChunks[chunk][slot & ChunkMask] = historyFlags[i];
                CountInFront(historyOwners[i], id);
            }

            first = lowestIssued = newFirst;
            if (historyContexts != null)
            {
                foreach (var pair in historyContexts)
                {
                    if (pair.Key >= 0 && pair.Key < historyCount)
                    {
                        SetContext(newFirst + pair.Key, pair.Value);
                    }
                }
            }

            RowsGeneration = NewRowsGeneration();
            Version++;
            if (Count > maxOccurrences)
            {
                Trim(maxOccurrences * 4 / 5);
            }

            if (Texts.TotalChars > maxTextChars && pins == 0)
            {
                // A large history counts against the same budget as live logs.
                KeepTextBudget();
            }
        }

        // Fresh chunks for the ids from `newFirst` up to the first one (chunk 0 copied, never written in place: a view
        // taken before a trim may still read its old slots).
        private void PrepareFront(int newFirst)
        {
            if (chunkCount == 0)
            {
                chunkBase = newFirst;
                while (((end - 1 - chunkBase) >> ChunkShift) >= chunkCount)
                {
                    AddChunk();
                }

                return;
            }

            int before = newFirst >= chunkBase ? 0 : (int)(((long)chunkBase - newFirst + ChunkMask) >> ChunkShift);
            int size = Math.Max(8, before + chunkCount + 8);
            var times = new long[size][];
            var owners = new int[size][];
            var flags = new byte[size][];
            var contexts = new int[size][];
            for (int c = 0; c < before; c++)
            {
                times[c] = new long[ChunkSize];
                owners[c] = new int[ChunkSize];
                flags[c] = new byte[ChunkSize];
            }

            times[before] = (long[])timeChunks[0].Clone();
            owners[before] = (int[])ownerChunks[0].Clone();
            flags[before] = (byte[])flagChunks[0].Clone();
            contexts[before] = (int[])contextChunks[0]?.Clone();
            Array.Copy(timeChunks, 1, times, before + 1, chunkCount - 1);
            Array.Copy(ownerChunks, 1, owners, before + 1, chunkCount - 1);
            Array.Copy(flagChunks, 1, flags, before + 1, chunkCount - 1);
            Array.Copy(contextChunks, 1, contexts, before + 1, chunkCount - 1);
            timeChunks = times;
            ownerChunks = owners;
            flagChunks = flags;
            contextChunks = contexts;
            chunkCount += before;
            chunkBase -= before << ChunkShift;
        }

        // A history occurrence, older than every one the message and its group had.
        private void CountInFront(int message, int id)
        {
            ref var entry = ref messages[message];
            if (entry.Count == 0)
            {
                zeroCount--;
                entry.First = entry.Last = id;
            }
            else
            {
                entry.First = Math.Min(entry.First, id);
                entry.Last = Math.Max(entry.Last, id);
            }

            entry.Count++;
            ref var group = ref groups[entry.Group];
            if (group.Count == 0)
            {
                group.First = group.Last = id;
                group.Latest = message;
            }
            else
            {
                group.First = Math.Min(group.First, id);
                if (id > group.Last)
                {
                    group.Last = id;
                    group.Latest = message;
                }
            }

            group.Count++;
        }

        /// <summary>
        /// Gives the occurrences new ids from <paramref name="newFirst"/> on, all moved by the same amount: when the ids
        /// would run out (about a billion logs) or history doesn't fit below the first one. Like a clear for whoever
        /// remembers an id (<see cref="Emptied"/>, <see cref="IdEpoch"/>); the logs stay.
        /// </summary>
        private void Renumber(int newFirst)
        {
            int delta = newFirst - first;
            if (delta == 0)
            {
                return;
            }

            CancelCompaction();
            chunkBase += delta;
            first += delta;
            end += delta;
            for (int i = 0; i < messageCount; i++)
            {
                ref var message = ref messages[i];
                if (message.Count > 0)
                {
                    message.First += delta;
                    message.Last += delta;
                }
            }

            for (int i = 0; i < groupCount; i++)
            {
                ref var group = ref groups[i];
                if (group.Count > 0)
                {
                    group.First += delta;
                    group.Last += delta;
                }
            }

            var kept = PayloadPairs();
            payloads.Clear();
            payloadOrder.Clear();
            foreach (var pair in kept)
            {
                SetPayload(pair.Key + delta, pair.Value);
            }

            lowestIssued = first;
            RowsGeneration = NewRowsGeneration();
            Epoch++;
            ForgetIds();
        }

        // Occurrence ids remembered anywhere may now name other logs.
        private void ForgetIds()
        {
            IdEpoch++;
            Version++;
            Emptied?.Invoke();
        }

        /// <summary>The lowest id given since ids last changed meaning (saved with the session).</summary>
        internal int LowestIssuedId => lowestIssued;

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

        /// <summary>The first occurrence id at or after <paramref name="time"/> (binary search: times are in order); <see cref="EndId"/> when none.</summary>
        public int LowerBound(long time)
        {
            int lo = first;
            int hi = end;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (TimeAt(mid) < time)
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

        private static long NewRowsGeneration() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue;

        // ---- Session file ----------------------------------------------------------------------------------------

        internal LogMessage[] MessagesArray => messages;
        internal LogGroup[] GroupsArray => groups;

        /// <summary>Every occurrence's context, by id, oldest first.</summary>
        /// <summary>Every kept output, by occurrence id (and its text id), oldest first.</summary>
        internal List<KeyValuePair<int, int>> PayloadPairs()
        {
            var pairs = new List<KeyValuePair<int, int>>(payloadOrder.Count);
            foreach (int id in payloadOrder)
            {
                pairs.Add(new KeyValuePair<int, int>(id, payloads[id]));
            }

            return pairs;
        }

        internal List<KeyValuePair<int, int>> ContextPairs()

        {
            var pairs = new List<KeyValuePair<int, int>>();
            for (int c = 0; c < chunkCount; c++)
            {
                var chunk = contextChunks[c];
                if (chunk == null)
                {
                    continue;
                }

                int chunkFirst = chunkBase + (c << ChunkShift);
                for (int i = 0; i < ChunkSize; i++)
                {
                    int id = chunkFirst + i;
                    if (chunk[i] != 0 && Contains(id))
                    {
                        pairs.Add(new KeyValuePair<int, int>(id, chunk[i]));
                    }
                }
            }

            return pairs;
        }

        /// <summary>A chunk directory with room for <paramref name="count"/> occurrences from chunk 0's first slot.</summary>
        internal static T[][] NewChunks<T>(int count)
        {
            int chunks = (int)(((long)count + ChunkMask) >> ChunkShift);
            var directory = new T[chunks + 8][];
            for (int c = 0; c < chunks; c++)
            {
                directory[c] = new T[ChunkSize];
            }

            return directory;
        }

        /// <summary>
        /// Puts back a store saved by <see cref="SessionFile"/>: every column (chunks from <see cref="NewChunks{T}"/>, the
        /// first slot being <paramref name="firstId"/>), message and group as they were, counters included, so nothing is
        /// recounted, regrouped or read from the texts.
        /// </summary>
        internal void Restore(TextTable texts, long rowsGeneration, int firstId, int lowestId, long[][] restoredTimes, int[][] restoredOwners, byte[][] restoredFlags,
            int restoredCount, LogMessage[] restoredMessages, int restoredMessageCount, LogGroup[] restoredGroups, int restoredGroupCount, GroupingMode grouping,
            List<KeyValuePair<int, int>> restoredContexts, List<KeyValuePair<int, int>> restoredPayloads, List<SessionEvent> restoredEvents)
        {
            CancelCompaction();
            Texts = texts;
            RowsGeneration = rowsGeneration;
            profiles.Clear();
            timeChunks = restoredTimes;
            ownerChunks = restoredOwners;
            flagChunks = restoredFlags;
            contextChunks = new int[restoredTimes.Length][];
            chunkCount = (int)(((long)restoredCount + ChunkMask) >> ChunkShift);
            chunkBase = firstId;
            first = firstId;
            end = firstId + restoredCount;
            lowestIssued = Math.Min(lowestId, firstId);

            Grouping = grouping;
            messages = restoredMessages;
            messageCount = restoredMessageCount;
            groups = restoredGroups;
            groupCount = restoredGroupCount;
            messageIndex.Clear();
            zeroCount = 0;
            for (int i = 0; i < messageCount; i++)
            {
                ref var message = ref messages[i];
                messageIndex[MessageKey.Of(message)] = i;
                message.Explanation = -2;
                if (message.Count == 0)
                {
                    zeroCount++;
                }
            }

            groupIndex.Clear();
            for (int i = 0; i < groupCount; i++)
            {
                groupIndex[((long)groups[i].Key << 2) | (long)groups[i].Level] = i;
            }

            if (restoredContexts != null)
            {
                foreach (var pair in restoredContexts)
                {
                    SetContext(pair.Key, pair.Value);
                }
            }

            payloads.Clear();
            payloadOrder.Clear();
            if (restoredPayloads != null)
            {
                foreach (var pair in restoredPayloads)
                {
                    if (Contains(pair.Key))
                    {
                        SetPayload(pair.Key, pair.Value);
                    }
                }
            }

            events.Clear();
            if (restoredEvents != null)
            {
                events.AddRange(restoredEvents);
            }


            Epoch++;
            IdEpoch++;
            Version++;
            MessagesVersion++;
        }
    }
}
