using System;
using System.Collections.Generic;
using System.IO;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// The session's logs on disk, so they survive script reloads (which wipe managed memory). Texts and occurrences are
    /// only ever appended between a clear, trim, history merge or compaction, so the files are too: before a reload only
    /// what came since the last save is written, and after it nothing is decoded, hashed or regrouped.
    /// <code>
    /// texts-G.bin      [char count int32][UTF-16 units]… per text id from 1, appended   (G = TextTable.Generation)
    /// textindex-G.bin  [offset int64][hash int32] per text id from 1, appended
    /// times-R.bin      int64 per occurrence id from the file's first, appended           (R: new for each LogStore.RowsGeneration)
    /// owners-R.bin     int32 per occurrence, appended
    /// head-N.bin       everything else (rewritten), with how much of each file is valid: written last, under a new
    ///                  number (a new file moved into place), the older head deleted after it
    /// </code>
    /// Only the newest head counts. A save that stops half way leaves the previous head, which ignores the longer tails
    /// (they are cut at the next save). Nothing is ever replaced in place, so no file system can lose a committed head.
    /// A new generation starts new files; the head names the ones it uses and the others are deleted after it. Texts are
    /// read lazily: a row, the details or a search reads the ones it needs (<see cref="TextTable.Restore"/>).
    /// Occurrence ids are saved as they are (the head says the first one), so whatever remembers an id across a reload
    /// still finds its log; a trim only moves the first id, and the rows files are rewritten once most of them is trimmed.
    /// <para>
    /// Any inconsistency makes <see cref="Load"/> return null with a reason: the caller starts from an empty store and
    /// Unity's console, and says so. Main thread, except the lazy text reads.
    /// </para>
    /// </summary>
    internal sealed class SessionFile
    {
        private const int Magic = 0x53474F4C; // "LOGS"
        // 4: occurrence ids kept (first id, the rows files' first id). A session of another version is never converted: an
        // earlier one of this editor goes through LogCapture's one recovery (Unity's console read again).
        private const int FormatVersion = 4;
        // A text's offset, hash and length.
        private const int IndexRecord = sizeof(long) + sizeof(int) + sizeof(int);
        private const int ChunkBytes = 1 << 20;
        // A head is a few hundred bytes per message: one this big is damaged (or not ours).
        private const long MaxHeadBytes = 1L << 30;
        private const int MessageRecord = 12 * sizeof(int) + 2;
        private const int GroupRecord = 6 * sizeof(int) + 1;

        private readonly string directory;
        // What the files hold, as of the last head this domain wrote or read.
        private long textGeneration;
        private int textsSaved;
        private long textBytes;
        // The rows files' key, the store generation they follow, their first occurrence id and the end of what they hold.
        private long rowsGeneration;
        private long storeRows;
        private int rowsFirst;
        private int rowsSaved;
        private int headNumber;
        private SessionTextFile textFile;

        public SessionFile(string directory)
        {
            this.directory = directory;
        }

        private string HeadPath(int number) => Path.Combine(directory, "head-" + number.ToString("D10") + ".bin");

        // The newest head's number, 0 when there is none.
        private int NewestHead()
        {
            int newest = 0;
            if (!System.IO.Directory.Exists(directory))
            {
                return 0;
            }

            foreach (string path in System.IO.Directory.GetFiles(directory, "head-*.bin"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(name.Substring(5), out int number) && number > newest)
                {
                    newest = number;
                }
            }

            return newest;
        }
        private string TextsPath(long generation) => Path.Combine(directory, "texts-" + generation.ToString("x16") + ".bin");
        private string IndexPath(long generation) => Path.Combine(directory, "textindex-" + generation.ToString("x16") + ".bin");
        private string TimesPath(long generation) => Path.Combine(directory, "times-" + generation.ToString("x16") + ".bin");
        private string OwnersPath(long generation) => Path.Combine(directory, "owners-" + generation.ToString("x16") + ".bin");

        /// <summary>Writes what changed since the last save, then the head. Throws on any IO error: the caller then <see cref="Delete"/>s.</summary>
        public void Save(LogStore store, SnapshotInfo info, int processId, long editorStart)
        {
            System.IO.Directory.CreateDirectory(directory);
            var texts = store.Texts;
            int textCount = texts.Count;
            if (texts.Generation != textGeneration)
            {
                textGeneration = texts.Generation;
                textsSaved = 1;
                textBytes = 0;
            }

            SaveTexts(texts, textCount);

            var view = store.View();
            // A new store generation, or a trim past what was saved or of most of the file: new rows files from the first id.
            if (rowsGeneration == 0 || store.RowsGeneration != storeRows || view.First > rowsSaved || view.First < rowsFirst ||
                view.First - (long)rowsFirst > view.Count)
            {
                storeRows = store.RowsGeneration;
                rowsGeneration = NewFileKey();
                rowsFirst = view.First;
                rowsSaved = view.First;
            }

            SaveRows(view);

            // A new name, moved into place once whole: the previous head stays until then (and if anything fails).
            int number = Math.Max(headNumber, NewestHead()) + 1;
            string temporary = Path.Combine(directory, "head.tmp");
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream))
            {
                WriteHead(writer, store, view, info, processId, editorStart, textCount);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, HeadPath(number));
            headNumber = number;
            DeleteOthers();
        }

        private void SaveTexts(TextTable texts, int textCount)
        {
            if (textsSaved > textCount)
            {
                throw new InvalidDataException("the text table shrank");
            }

            if (texts.Faulted)
            {
                // A text could not be read back: never save the placeholder as if it were the log.
                throw new InvalidDataException("a text could not be read back");
            }

            using (var data = OpenAppend(TextsPath(textGeneration), textBytes))
            using (var index = OpenAppend(IndexPath(textGeneration), (long)(textsSaved - 1) * IndexRecord))
            {
                var chars = new char[1 << 15];
                var bytes = new byte[chars.Length * 2 + 4];
                var record = new byte[IndexRecord];
                long offset = textBytes;
                for (int id = textsSaved; id < textCount; id++)
                {
                    // The UTF-16 units as they are: a lone surrogate (a message Unity cut) must come back unchanged.
                    string text = texts[id];
                    int length = text.Length * 2;
                    if (chars.Length < text.Length)
                    {
                        chars = new char[Math.Max(text.Length, chars.Length * 2)];
                        bytes = new byte[chars.Length * 2 + 4];
                    }

                    text.CopyTo(0, chars, 0, text.Length);
                    WriteInt(bytes, 0, text.Length);
                    Buffer.BlockCopy(chars, 0, bytes, 4, length);
                    data.Write(bytes, 0, length + 4);

                    WriteLong(record, 0, offset);
                    WriteInt(record, 8, texts.HashAt(id));
                    WriteInt(record, 12, text.Length);
                    index.Write(record, 0, IndexRecord);
                    offset += length + 4;
                }

                data.Flush();
                index.Flush();
                textBytes = offset;
                textsSaved = textCount;
            }
        }

        private void SaveRows(StoreView view)
        {
            long saved = (long)rowsSaved - rowsFirst;
            using (var timesFile = OpenAppend(TimesPath(rowsGeneration), saved * sizeof(long)))
            using (var ownersFile = OpenAppend(OwnersPath(rowsGeneration), saved * sizeof(int)))
            {
                var buffer = new byte[ChunkBytes];
                WriteColumn(timesFile, view.TimeChunks, view, rowsSaved, view.End, sizeof(long), buffer);
                WriteColumn(ownersFile, view.OwnerChunks, view, rowsSaved, view.End, sizeof(int), buffer);
                timesFile.Flush();
                ownersFile.Flush();
            }

            rowsSaved = view.End;
        }

        // Ids [from, to) of one column, chunk by chunk.
        private static void WriteColumn(Stream stream, Array[] chunks, StoreView view, int from, int to, int size, byte[] buffer)
        {
            for (int id = from; id < to;)
            {
                int run = view.Run(id, to, out int chunk, out int offset);
                WriteBlock(stream, chunks[chunk], offset * size, run * size, buffer);
                id += run;
            }
        }

        private static long NewFileKey() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue | 1L;

        // A file cut to what the head knows of (a tail from a save that failed goes), positioned at its end.
        private static FileStream OpenAppend(string path, long valid)
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 16);
            try
            {
                if (stream.Length < valid)
                {
                    throw new InvalidDataException(Path.GetFileName(path) + " is shorter than saved");
                }

                if (stream.Length != valid)
                {
                    stream.SetLength(valid);
                }

                stream.Position = valid;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void WriteHead(BinaryWriter writer, LogStore store, StoreView view, SnapshotInfo info, int processId, long editorStart, int textCount)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(processId);
            writer.Write(editorStart);
            writer.Write(info.UnityRows);
            writer.Write(info.UnityAnchor ?? string.Empty);
            writer.Write(info.HistoryFrom);
            writer.Write(info.HistoryTo);
            writer.Write(info.HistoryAnchor ?? string.Empty);
            writer.Write((byte)store.Grouping);
            writer.Write(store.MaxOccurrences);
            writer.Write(store.InitialFirstId);

            writer.Write(textGeneration);
            writer.Write(textCount);
            writer.Write(textBytes);
            writer.Write(rowsGeneration);
            writer.Write(storeRows);
            writer.Write(rowsFirst);
            writer.Write(store.LowestIssuedId);
            writer.Write(view.First);
            writer.Write(view.Count);

            var sources = store.Sources.All;
            writer.Write(sources.Count);
            foreach (var source in sources)
            {
                writer.Write(source.Key);
                writer.Write(source.Name);
            }

            var messages = store.MessagesArray;
            writer.Write(store.MessageCount);
            for (int i = 0; i < store.MessageCount; i++)
            {
                var message = messages[i];
                writer.Write(message.Text);
                writer.Write(message.Stack);
                writer.Write(message.Callsite);
                writer.Write(message.File);
                writer.Write(message.Line);
                writer.Write(message.Column);
                writer.Write(message.Assembly);
                writer.Write(message.Source);
                writer.Write(message.Group);
                writer.Write(message.Count);
                writer.Write(message.First);
                writer.Write(message.Last);
                writer.Write((byte)message.Variant);
                writer.Write((byte)message.Flags);
            }

            var groups = store.GroupsArray;
            writer.Write(store.GroupCount);
            for (int i = 0; i < store.GroupCount; i++)
            {
                var group = groups[i];
                writer.Write(group.Key);
                writer.Write((byte)group.Level);
                writer.Write(group.Count);
                writer.Write(group.First);
                writer.Write(group.Last);
                writer.Write(group.Latest);
                writer.Write(group.Variants);
            }

            // Flags change in place (a compile message superseded): small enough to write whole.
            for (int id = view.First; id < view.End;)
            {
                int run = view.Run(id, view.End, out int chunk, out int offset);
                writer.Write(view.FlagChunks[chunk], offset, run);
                id += run;
            }

            var contexts = store.ContextPairs();
            writer.Write(contexts.Count);
            foreach (var pair in contexts)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }

            // Commands' outputs: an occurrence and a text id each (the texts are in the texts file).
            var outputs = store.PayloadPairs();
            writer.Write(outputs.Count);
            foreach (var pair in outputs)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }

            var events = store.Events;
            writer.Write(events.Count);
            foreach (var sessionEvent in events)
            {
                writer.Write(sessionEvent.Time);
                writer.Write((byte)sessionEvent.Kind);
            }

            // A head cut short never reads as whole.
            writer.Write(Magic);
        }

        /// <summary>
        /// The store this editor process saved, or null: with <paramref name="problem"/> null when there is simply none
        /// (or it belongs to another editor), else what was wrong with it.
        /// </summary>
        public LogStore Load(int processId, long editorStart, out SnapshotInfo info, out string problem)
        {
            info = null;
            problem = null;
            string skipped = null;
            // Newest first; the first one that reads back whole wins (a newer one that doesn't is said, not hidden).
            foreach (int number in Heads())
            {
                Forget();
                headNumber = number;
                LogStore store = null;
                string why;
                try
                {
                    store = Read(processId, editorStart, out info, out why);
                }
                catch (Exception exception)
                {
                    why = exception.GetType().Name + ": " + exception.Message;
                }

                if (store != null)
                {
                    problem = skipped ?? (File.Exists(FailedMarker)
                        ? "the save before this reload failed; the logs since the reload before it come from Unity's console"
                        : null);
                    return store;
                }

                info = null;
                if (why == null)
                {
                    // Another editor's or another version's: none of the older ones is ours either.
                    break;
                }

                skipped = skipped ?? why;
            }

            Forget();
            problem = skipped;
            return null;
        }

        private string FailedMarker => Path.Combine(directory, "save-failed");

        /// <summary>
        /// A save failed: the last committed head and its files stay as they are (the next domain reads them, then
        /// Unity's console for what came after), and the next load says so. Never throws.
        /// </summary>
        public void MarkSaveFailed()
        {
            Close();
            try
            {
                File.WriteAllText(FailedMarker, string.Empty);
            }
            catch (Exception)
            {
                // Without the marker the next load only misses the warning.
            }
        }

        // Head numbers present, newest first.
        private List<int> Heads()
        {
            var numbers = new List<int>();
            if (System.IO.Directory.Exists(directory))
            {
                foreach (string path in System.IO.Directory.GetFiles(directory, "head-*.bin"))
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(path).Substring(5), out int number) && number > 0)
                    {
                        numbers.Add(number);
                    }
                }
            }

            numbers.Sort((x, y) => y.CompareTo(x));
            return numbers;
        }

        private void Forget()
        {
            Close();
            textGeneration = rowsGeneration = storeRows = 0;
            rowsFirst = 0;
            textsSaved = rowsSaved = 0;
            textBytes = 0;
        }

        private LogStore Read(int processId, long editorStart, out SnapshotInfo info, out string problem)
        {
            info = null;
            problem = null;
            string headPath = HeadPath(headNumber);
            if (new FileInfo(headPath).Length > MaxHeadBytes)
            {
                problem = "the head is too big";
                return null;
            }

            using (var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(headPath))))
            {
                if (reader.ReadInt32() != Magic)
                {
                    // Not a head of the Mixer console: nothing to tell.
                    return null;
                }

                // Every version starts with its number, the editor process and its start.
                int version = reader.ReadInt32();
                int savedProcess = reader.ReadInt32();
                long savedStart = reader.ReadInt64();
                if (savedProcess != processId || Math.Abs(savedStart - editorStart) > TimeSpan.TicksPerMinute)
                {
                    return null;
                }

                if (version != FormatVersion)
                {
                    // This editor's session, saved by another version of the Mixer console (scripts reloaded into this one):
                    // never converted, LogCapture's one recovery reads Unity's console again.
                    problem = "it was saved by another version of the Mixer console";
                    return null;
                }

                var loaded = new SnapshotInfo
                {
                    UnityRows = reader.ReadInt32(),
                    UnityAnchor = ReadText(reader),
                    HistoryFrom = reader.ReadInt32(),
                    HistoryTo = reader.ReadInt32(),
                    HistoryAnchor = ReadText(reader)
                };
                var grouping = (GroupingMode)reader.ReadByte();
                int maxOccurrences = reader.ReadInt32();
                int initialFirst = reader.ReadInt32();
                long texts = reader.ReadInt64();
                int textCount = reader.ReadInt32();
                long bytes = reader.ReadInt64();
                long rows = reader.ReadInt64();
                long storeGeneration = reader.ReadInt64();
                int filesFirst = reader.ReadInt32();
                int lowestId = reader.ReadInt32();
                int firstId = reader.ReadInt32();
                int count = reader.ReadInt32();
                long skipped = (long)firstId - filesFirst;
                // Every count is checked against the bytes that must hold it before anything is allocated for it.
                if (textCount < 1 || bytes < 0 || count < 0 || !Fits(reader, count, 1) || initialFirst < 0 || firstId < 0 || skipped < 0 ||
                    lowestId < 0 || lowestId > firstId ||
                    (long)firstId + count > int.MaxValue - LogStore.ChunkSize ||
                    FileLength(IndexPath(texts)) < (textCount - 1L) * IndexRecord || FileLength(TextsPath(texts)) < bytes ||
                    FileLength(TimesPath(rows)) < (skipped + count) * sizeof(long) || FileLength(OwnersPath(rows)) < (skipped + count) * sizeof(int))
                {
                    problem = "the files are shorter than the head says";
                    return null;
                }

                var store = new LogStore(initialFirst) { MaxOccurrences = maxOccurrences };
                int sourceCount = reader.ReadInt32();
                if (!Fits(reader, sourceCount, 2))
                {
                    problem = "bad source count";
                    return null;
                }

                for (int i = 0; i < sourceCount; i++)
                {
                    string key = ReadText(reader);
                    store.Sources.Restore(key, ReadText(reader));
                }

                int messageCount = reader.ReadInt32();
                if (!Fits(reader, messageCount, MessageRecord))
                {
                    problem = "bad message count";
                    return null;
                }

                var messages = new LogMessage[Capacity(messageCount, 256)];
                for (int i = 0; i < messageCount; i++)
                {
                    messages[i] = new LogMessage
                    {
                        Text = reader.ReadInt32(),
                        Stack = reader.ReadInt32(),
                        Callsite = reader.ReadInt32(),
                        File = reader.ReadInt32(),
                        Line = reader.ReadInt32(),
                        Column = reader.ReadInt32(),
                        Assembly = reader.ReadInt32(),
                        Source = reader.ReadInt32(),
                        Group = reader.ReadInt32(),
                        Count = reader.ReadInt32(),
                        First = reader.ReadInt32(),
                        Last = reader.ReadInt32(),
                        Variant = (LogVariant)reader.ReadByte(),
                        Flags = (MessageFlags)reader.ReadByte(),
                        Explanation = -2
                    };
                }

                int groupCount = reader.ReadInt32();
                if (!Fits(reader, groupCount, GroupRecord))
                {
                    problem = "bad group count";
                    return null;
                }

                var groups = new LogGroup[Capacity(groupCount, 256)];
                for (int i = 0; i < groupCount; i++)
                {
                    groups[i] = new LogGroup
                    {
                        Key = reader.ReadInt32(),
                        Level = (LogLevel)reader.ReadByte(),
                        Count = reader.ReadInt32(),
                        First = reader.ReadInt32(),
                        Last = reader.ReadInt32(),
                        Latest = reader.ReadInt32(),
                        Variants = reader.ReadInt32()
                    };
                }

                if (!Fits(reader, count, 1))
                {
                    problem = "bad occurrence count";
                    return null;
                }

                var flags = LogStore.NewChunks<byte>(count);
                for (int at = 0; at < count; at += LogStore.ChunkSize)
                {
                    int run = Math.Min(LogStore.ChunkSize, count - at);
                    if (reader.Read(flags[at >> LogStore.ChunkShift], 0, run) != run)
                    {
                        throw new EndOfStreamException();
                    }
                }

                int contextCount = reader.ReadInt32();
                if (!Fits(reader, contextCount, 8))
                {
                    problem = "bad context count";
                    return null;
                }

                var contexts = new List<KeyValuePair<int, int>>(Math.Max(0, contextCount));
                for (int i = 0; i < contextCount; i++)
                {
                    int key = reader.ReadInt32();
                    contexts.Add(new KeyValuePair<int, int>(key, reader.ReadInt32()));
                }

                int outputCount = reader.ReadInt32();
                if (!Fits(reader, outputCount, 8))
                {
                    problem = "bad output count";
                    return null;
                }

                var outputs = new List<KeyValuePair<int, int>>(Math.Max(0, outputCount));
                for (int i = 0; i < outputCount; i++)
                {
                    int key = reader.ReadInt32();
                    int text = reader.ReadInt32();
                    if (key < firstId || key >= firstId + count || !IsText(text, textCount))
                    {
                        problem = "an output doesn't match";
                        return null;
                    }

                    outputs.Add(new KeyValuePair<int, int>(key, text));
                }

                int eventCount = reader.ReadInt32();
                if (!Fits(reader, eventCount, 9))
                {
                    problem = "bad event count";
                    return null;
                }

                var events = new List<SessionEvent>(Math.Max(0, eventCount));
                for (int i = 0; i < eventCount; i++)
                {
                    long time = reader.ReadInt64();
                    events.Add(new SessionEvent(time, (SessionEventKind)reader.ReadByte()));
                }

                if (reader.ReadInt32() != Magic)
                {
                    problem = "the head is incomplete";
                    return null;
                }

                problem = CheckIds(messages, messageCount, groups, groupCount, textCount, firstId, firstId + count, store.Sources.Count, contexts);
                if (problem != null)
                {
                    return null;
                }

                // The columns, then the texts' index: only the parts the head knows of.
                var times = LogStore.NewChunks<long>(count);
                var owners = LogStore.NewChunks<int>(count);
                if (!ReadColumn(TimesPath(rows), times, skipped * sizeof(long), count, sizeof(long)) ||
                    !ReadColumn(OwnersPath(rows), owners, skipped * sizeof(int), count, sizeof(int)))
                {
                    problem = "the occurrences are incomplete";
                    return null;
                }

                problem = CheckRows(owners, count, messages, messageCount);
                if (problem != null)
                {
                    return null;
                }

                var file = new SessionTextFile(TextsPath(texts), bytes);
                if (file.Length < bytes)
                {
                    file.Close();
                    problem = "the texts are incomplete";
                    return null;
                }

                var offsets = new long[textCount];
                var hashes = new int[textCount];
                var lengths = new int[textCount];
                if (!ReadIndex(IndexPath(texts), offsets, hashes, lengths, textCount, bytes))
                {
                    file.Close();
                    problem = "the texts' index is incomplete";
                    return null;
                }

                // Each text is its length (4 bytes) and two bytes per UTF-16 unit.
                var table = TextTable.Restore(texts, textCount, (bytes - 4L * (textCount - 1)) / 2, hashes, offsets, lengths, file);
                store.Restore(table, storeGeneration, firstId, lowestId, times,
 owners, flags, count, messages, messageCount, groups, groupCount, grouping, contexts,
                    outputs, events);

                textFile = file;
                textGeneration = texts;
                textsSaved = textCount;
                textBytes = bytes;
                rowsGeneration = rows;
                storeRows = storeGeneration;
                rowsFirst = filesFirst;
                rowsSaved = firstId + count;
                info = loaded;
                return store;
            }
        }

        private static string CheckIds(LogMessage[] messages, int messageCount, LogGroup[] groups, int groupCount, int textCount, int firstId, int end,
            int sourceCount, List<KeyValuePair<int, int>> contexts)
        {
            long total = 0;
            for (int i = 0; i < messageCount; i++)
            {
                ref var message = ref messages[i];
                if (!IsText(message.Text, textCount) || !IsText(message.Stack, textCount) || !IsText(message.Callsite, textCount) ||
                    !IsText(message.File, textCount) || !IsText(message.Assembly, textCount))
                {
                    return "a message names a text that isn't saved";
                }

                if ((uint)message.Group >= (uint)groupCount || message.Count < 0 ||
                    message.Count > 0 && (message.First < 0 || message.Last < firstId || message.Last < message.First || message.Last >= end))
                {
                    return "a message's counts don't match";
                }

                if (message.Source < 0 || message.Source >= sourceCount)
                {
                    message.Source = SourceCatalog.Unity;
                }

                total += message.Count;
            }

            if (total != (long)end - firstId)
            {
                return "the messages' counts don't add up";
            }

            for (int i = 0; i < groupCount; i++)
            {
                var group = groups[i];
                if (!IsText(group.Key, textCount) || group.Latest >= messageCount ||
                    group.Count > 0 && (group.Latest < 0 || group.First < 0 || group.Last < firstId || group.Last < group.First || group.Last >= end))
                {
                    return "a group doesn't match";
                }
            }

            foreach (var pair in contexts)
            {
                if (pair.Key < firstId || pair.Key >= end)
                {
                    return "a context doesn't match";
                }
            }

            return null;
        }

        private static bool IsText(int id, int textCount) => (uint)id < (uint)textCount;

        // Whether `count` records of `recordBytes` each can still be in the head.
        private static bool Fits(BinaryReader reader, long count, long recordBytes) =>
            count >= 0 && count <= (reader.BaseStream.Length - reader.BaseStream.Position) / Math.Max(1L, recordBytes);

        // Room for a quarter more, without overflowing.
        private static int Capacity(int count, int minimum) => (int)Math.Max(minimum, Math.Min(int.MaxValue - 64L, count + (long)count / 4));

        private static long FileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        // BinaryWriter's string format, with its length checked against the head before anything is allocated.
        private static string ReadText(BinaryReader reader)
        {
            int length = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (shift > 28)
                {
                    throw new InvalidDataException("bad text length");
                }

                byte part = reader.ReadByte();
                length |= (part & 0x7F) << shift;
                if ((part & 0x80) == 0)
                {
                    break;
                }
            }

            if (!Fits(reader, length, 1))
            {
                throw new InvalidDataException("bad text length");
            }

            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static string CheckRows(int[][] owners, int count, LogMessage[] messages, int messageCount)
        {
            for (int i = 0; i < count; i++)
            {
                if ((uint)owners[i >> LogStore.ChunkShift][i & LogStore.ChunkMask] >= (uint)messageCount)
                {
                    return "an occurrence names a message that isn't saved";
                }
            }

            return null;
        }

        // `count` elements of `size` bytes from `offset` in the file into the chunks, from slot 0; false when it is shorter.
        private static bool ReadColumn(string path, Array[] chunks, long offset, int count, int size)
        {
            if (count == 0)
            {
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                if (stream.Length < offset + (long)count * size)
                {
                    return false;
                }

                stream.Position = offset;
                var buffer = new byte[Math.Min(ChunkBytes, LogStore.ChunkSize * size)];
                for (int at = 0; at < count; at += LogStore.ChunkSize)
                {
                    int bytes = Math.Min(LogStore.ChunkSize, count - at) * size;
                    var chunk = chunks[at >> LogStore.ChunkShift];
                    for (int done = 0; done < bytes;)
                    {
                        int read = stream.Read(buffer, 0, Math.Min(buffer.Length, bytes - done));
                        if (read <= 0)
                        {
                            return false;
                        }

                        Buffer.BlockCopy(buffer, 0, chunk, done, read);
                        done += read;
                    }
                }
            }

            return true;
        }

        // Every record must start where the one before ends, and the last end where the texts file's valid part does.
        private static bool ReadIndex(string path, long[] offsets, int[] hashes, int[] lengths, int textCount, long bytes)
        {
            long length = (long)(textCount - 1) * IndexRecord;
            var data = new byte[Math.Max(0, length)];
            if (!ReadBlock(path, data, length))
            {
                return false;
            }

            long next = 0;
            for (int id = 1; id < textCount; id++)
            {
                int at = (id - 1) * IndexRecord;
                long offset = BitConverter.ToInt64(data, at);
                int textLength = BitConverter.ToInt32(data, at + 12);
                if (offset != next || textLength < 0)
                {
                    return false;
                }

                offsets[id] = offset;
                hashes[id] = BitConverter.ToInt32(data, at + 8);
                lengths[id] = textLength;
                next = offset + 4 + 2L * textLength;
                if (next > bytes)
                {
                    return false;
                }
            }

            return next == bytes;
        }

        // The first `bytes` of the file into the array; false when the file is shorter.
        private static bool ReadBlock(string path, Array destination, long bytes)
        {
            if (bytes == 0)
            {
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                if (stream.Length < bytes)
                {
                    return false;
                }

                var buffer = new byte[(int)Math.Min(ChunkBytes, bytes)];
                long offset = 0;
                while (offset < bytes)
                {
                    int length = (int)Math.Min(buffer.Length, bytes - offset);
                    int read = stream.Read(buffer, 0, length);
                    if (read <= 0)
                    {
                        return false;
                    }

                    Buffer.BlockCopy(buffer, 0, destination, (int)offset, read);
                    offset += read;
                }
            }

            return true;
        }

        private static void WriteBlock(Stream stream, Array source, int from, int bytes, byte[] buffer)
        {
            int offset = 0;
            while (offset < bytes)
            {
                int length = Math.Min(buffer.Length, bytes - offset);
                Buffer.BlockCopy(source, from + offset, buffer, 0, length);
                stream.Write(buffer, 0, length);
                offset += length;
            }
        }

        private static void WriteInt(byte[] buffer, int at, int value)
        {
            buffer[at] = (byte)value;
            buffer[at + 1] = (byte)(value >> 8);
            buffer[at + 2] = (byte)(value >> 16);
            buffer[at + 3] = (byte)(value >> 24);
        }

        private static void WriteLong(byte[] buffer, int at, long value)
        {
            WriteInt(buffer, at, (int)value);
            WriteInt(buffer, at + 4, (int)(value >> 32));
        }

        // Files of earlier generations. One still open (a worker reading an old table) goes at a later save.
        private void DeleteOthers()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileName(HeadPath(headNumber)),
                Path.GetFileName(TextsPath(textGeneration)),
                Path.GetFileName(IndexPath(textGeneration)),
                Path.GetFileName(TimesPath(rowsGeneration)),
                Path.GetFileName(OwnersPath(rowsGeneration))
            };
            foreach (string path in System.IO.Directory.GetFiles(directory))
            {
                if (keep.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // In use: the next save tries again.
                }
            }
        }

        /// <summary>Closes the texts file (a later lazy read opens it again).</summary>
        public void Close()
        {
            textFile?.Close();
            textFile = null;
        }

        /// <summary>Forgets the session: the head first, so nothing half deleted can be read back.</summary>
        public void Delete()
        {
            Close();
            textGeneration = rowsGeneration = storeRows = 0;
            rowsFirst = 0;
            textsSaved = rowsSaved = 0;
            textBytes = 0;
            try
            {
                foreach (string head in System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory, "head-*.bin") : new string[0])
                {
                    File.Delete(head);
                }

                headNumber = 0;

                if (System.IO.Directory.Exists(directory))
                {
                    foreach (string path in System.IO.Directory.GetFiles(directory))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception)
                        {
                            // Replaced at the next save.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A locked head is replaced at the next save; without its texts it doesn't load.
            }
        }
    }

    /// <summary>A session's texts file, read one text at a time by <see cref="TextTable"/> (callers lock on it).</summary>
    internal sealed class SessionTextFile
    {
        private readonly string path;
        private readonly long valid;
        private FileStream stream;
        private byte[] buffer = new byte[4096];
        private char[] chars = new char[2048];

        public SessionTextFile(string path, long valid)
        {
            this.path = path;
            this.valid = valid;
        }

        /// <summary>The file's length now (0 when it can't be opened).</summary>
        public long Length
        {
            get
            {
                lock (this)
                {
                    return Open() ? stream.Length : 0L;
                }
            }
        }

        /// <summary>Texts that could not be read back (shown empty).</summary>
        public int Failures { get; private set; }

        /// <summary>
        /// The text at <paramref name="offset"/>, or null when it can't be read or its record is not the
        /// <paramref name="expected"/> length the index gives. Callers hold the lock.
        /// </summary>
        public string Read(long offset, int expected)
        {
            try
            {
                if (!Open() || offset < 0 || offset + 4 > valid)
                {
                    Failures++;
                    return null;
                }

                stream.Position = offset;
                if (!Fill(4))
                {
                    Failures++;
                    return null;
                }

                int length = BitConverter.ToInt32(buffer, 0);
                long bytes = (long)length * 2;
                if (length != expected || length < 0 || offset + 4 + bytes > valid)
                {
                    Failures++;
                    return null;
                }

                if (buffer.Length < bytes)
                {
                    buffer = new byte[(int)Math.Max(bytes, buffer.Length * 2L)];
                }

                if (chars.Length < length)
                {
                    chars = new char[Math.Max(length, chars.Length * 2)];
                }

                if (!Fill((int)bytes))
                {
                    Failures++;
                    return null;
                }

                Buffer.BlockCopy(buffer, 0, chars, 0, (int)bytes);
                return new string(chars, 0, length);
            }
            catch (Exception)
            {
                Failures++;
                return null;
            }
        }

        public void Close()
        {
            lock (this)
            {
                stream?.Dispose();
                stream = null;
            }
        }

        private bool Open()
        {
            if (stream != null)
            {
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            // Shared for writing: the next save appends to this very file while it stays open.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return true;
        }

        private bool Fill(int bytes)
        {
            int offset = 0;
            while (offset < bytes)
            {
                int read = stream.Read(buffer, offset, bytes - offset);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }
    }
}
