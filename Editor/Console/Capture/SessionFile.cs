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
    /// times-R.bin      int64 per occurrence, appended                                    (R = LogStore.RowsGeneration)
    /// owners-R.bin     int32 per occurrence, appended
    /// head.bin         everything else (rewritten), with how much of each file is valid: written last, atomically
    /// </code>
    /// A save that stops half way leaves the previous head, which ignores the longer tails (they are cut at the next save).
    /// A new generation starts new files; the head names the ones it uses and the others are deleted after it. Texts are
    /// read lazily: a row, the details or a search reads the ones it needs (<see cref="TextTable.Restore"/>).
    /// <para>
    /// Any inconsistency makes <see cref="Load"/> return null with a reason: the caller starts from an empty store and
    /// Unity's console, and says so. Main thread, except the lazy text reads.
    /// </para>
    /// </summary>
    internal sealed class SessionFile
    {
        private const int Magic = 0x53474F4C; // "LOGS"
        private const int FormatVersion = 2;
        private const int IndexRecord = sizeof(long) + sizeof(int);
        private const int ChunkBytes = 1 << 20;
        private const string HeadName = "head.bin";

        private readonly string directory;
        // What the files hold, as of the last head this domain wrote or read.
        private long textGeneration;
        private int textsSaved;
        private long textBytes;
        private long rowsGeneration;
        private int rowsSaved;
        private SessionTextFile textFile;

        public SessionFile(string directory)
        {
            this.directory = directory;
        }

        private string HeadPath => Path.Combine(directory, HeadName);
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

            int count = store.Count;
            if (store.RowsGeneration != rowsGeneration)
            {
                rowsGeneration = store.RowsGeneration;
                rowsSaved = 0;
            }

            SaveRows(store, count);

            string head = HeadPath;
            string temporary = head + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream))
            {
                WriteHead(writer, store, info, processId, editorStart, textCount, count);
            }

            Replace(temporary, head);
            DeleteOthers();
        }

        private void SaveTexts(TextTable texts, int textCount)
        {
            if (textsSaved > textCount)
            {
                throw new InvalidDataException("the text table shrank");
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
                    index.Write(record, 0, IndexRecord);
                    offset += length + 4;
                }

                data.Flush();
                index.Flush();
                textBytes = offset;
                textsSaved = textCount;
            }
        }

        private void SaveRows(LogStore store, int count)
        {
            store.ExportColumns(out long[] times, out int[] owners, out _);
            using (var timesFile = OpenAppend(TimesPath(rowsGeneration), (long)rowsSaved * sizeof(long)))
            using (var ownersFile = OpenAppend(OwnersPath(rowsGeneration), (long)rowsSaved * sizeof(int)))
            {
                var buffer = new byte[ChunkBytes];
                WriteBlock(timesFile, times, rowsSaved * sizeof(long), (count - rowsSaved) * sizeof(long), buffer);
                WriteBlock(ownersFile, owners, rowsSaved * sizeof(int), (count - rowsSaved) * sizeof(int), buffer);
                timesFile.Flush();
                ownersFile.Flush();
            }

            rowsSaved = count;
        }

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

        private void WriteHead(BinaryWriter writer, LogStore store, SnapshotInfo info, int processId, long editorStart, int textCount, int count)
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

            writer.Write(textGeneration);
            writer.Write(textCount);
            writer.Write(textBytes);
            writer.Write(rowsGeneration);
            writer.Write(count);

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
            store.ExportColumns(out _, out _, out byte[] flags);
            writer.Write(flags, 0, count);

            var contexts = store.Contexts;
            writer.Write(contexts.Count);
            foreach (var pair in contexts)
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
            Close();
            textGeneration = rowsGeneration = 0;
            textsSaved = rowsSaved = 0;
            textBytes = 0;
            if (!File.Exists(HeadPath))
            {
                return null;
            }

            try
            {
                var store = Read(processId, editorStart, out info, out problem);
                if (store == null)
                {
                    Close();
                    textGeneration = rowsGeneration = 0;
                    textsSaved = rowsSaved = 0;
                    textBytes = 0;
                }

                return store;
            }
            catch (Exception exception)
            {
                Close();
                textGeneration = rowsGeneration = 0;
                textsSaved = rowsSaved = 0;
                textBytes = 0;
                info = null;
                problem = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        private LogStore Read(int processId, long editorStart, out SnapshotInfo info, out string problem)
        {
            info = null;
            problem = null;
            using (var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(HeadPath))))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
                {
                    // Another version of the Mixer console wrote it: nothing to tell.
                    return null;
                }

                int savedProcess = reader.ReadInt32();
                long savedStart = reader.ReadInt64();
                if (savedProcess != processId || Math.Abs(savedStart - editorStart) > TimeSpan.TicksPerMinute)
                {
                    return null;
                }

                var loaded = new SnapshotInfo
                {
                    UnityRows = reader.ReadInt32(),
                    UnityAnchor = reader.ReadString(),
                    HistoryFrom = reader.ReadInt32(),
                    HistoryTo = reader.ReadInt32(),
                    HistoryAnchor = reader.ReadString()
                };
                var grouping = (GroupingMode)reader.ReadByte();
                int maxOccurrences = reader.ReadInt32();
                long texts = reader.ReadInt64();
                int textCount = reader.ReadInt32();
                long bytes = reader.ReadInt64();
                long rows = reader.ReadInt64();
                int count = reader.ReadInt32();
                if (textCount < 1 || bytes < 0 || count < 0)
                {
                    problem = "bad counts";
                    return null;
                }

                var store = new LogStore(64) { MaxOccurrences = maxOccurrences };
                int sourceCount = reader.ReadInt32();
                for (int i = 0; i < sourceCount; i++)
                {
                    string key = reader.ReadString();
                    store.Sources.Restore(key, reader.ReadString());
                }

                int messageCount = reader.ReadInt32();
                var messages = new LogMessage[Math.Max(256, messageCount + messageCount / 4)];
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
                var groups = new LogGroup[Math.Max(256, groupCount + groupCount / 4)];
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

                int capacity = Math.Max(4096, count + count / 4);
                var flags = new byte[capacity];
                if (reader.Read(flags, 0, count) != count)
                {
                    throw new EndOfStreamException();
                }

                int contextCount = reader.ReadInt32();
                var contexts = new Dictionary<int, int>(Math.Max(0, contextCount));
                for (int i = 0; i < contextCount; i++)
                {
                    int key = reader.ReadInt32();
                    contexts[key] = reader.ReadInt32();
                }

                int eventCount = reader.ReadInt32();
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

                problem = CheckIds(messages, messageCount, groups, groupCount, textCount, count, store.Sources.Count, contexts);
                if (problem != null)
                {
                    return null;
                }

                // The columns, then the texts' index: only the parts the head knows of.
                var times = new long[capacity];
                var owners = new int[capacity];
                if (!ReadBlock(TimesPath(rows), times, (long)count * sizeof(long)) || !ReadBlock(OwnersPath(rows), owners, (long)count * sizeof(int)))
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
                if (!ReadIndex(IndexPath(texts), offsets, hashes, textCount, bytes))
                {
                    file.Close();
                    problem = "the texts' index is incomplete";
                    return null;
                }

                var table = TextTable.Restore(texts, textCount, hashes, offsets, file);
                store.Restore(table, rows, times, owners, flags, count, messages, messageCount, groups, groupCount, grouping, contexts, events);

                textFile = file;
                textGeneration = texts;
                textsSaved = textCount;
                textBytes = bytes;
                rowsGeneration = rows;
                rowsSaved = count;
                info = loaded;
                return store;
            }
        }

        private static string CheckIds(LogMessage[] messages, int messageCount, LogGroup[] groups, int groupCount, int textCount, int count,
            int sourceCount, Dictionary<int, int> contexts)
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

                if ((uint)message.Group >= (uint)groupCount || message.Count < 0 || message.First >= count || message.Last >= count)
                {
                    return "a message's counts don't match";
                }

                if (message.Source < 0 || message.Source >= sourceCount)
                {
                    message.Source = SourceCatalog.Unity;
                }

                total += message.Count;
            }

            if (total != count)
            {
                return "the messages' counts don't add up";
            }

            for (int i = 0; i < groupCount; i++)
            {
                if (!IsText(groups[i].Key, textCount) || groups[i].Latest >= messageCount || groups[i].Last >= count)
                {
                    return "a group doesn't match";
                }
            }

            foreach (var pair in contexts)
            {
                if ((uint)pair.Key >= (uint)count)
                {
                    return "a context doesn't match";
                }
            }

            return null;
        }

        private static bool IsText(int id, int textCount) => (uint)id < (uint)textCount;

        private static string CheckRows(int[] owners, int count, LogMessage[] messages, int messageCount)
        {
            for (int i = 0; i < count; i++)
            {
                if ((uint)owners[i] >= (uint)messageCount)
                {
                    return "an occurrence names a message that isn't saved";
                }
            }

            return null;
        }

        private static bool ReadIndex(string path, long[] offsets, int[] hashes, int textCount, long bytes)
        {
            long length = (long)(textCount - 1) * IndexRecord;
            var data = new byte[Math.Max(0, length)];
            if (!ReadBlock(path, data, length))
            {
                return false;
            }

            long previous = -1;
            for (int id = 1; id < textCount; id++)
            {
                int at = (id - 1) * IndexRecord;
                long offset = BitConverter.ToInt64(data, at);
                if (offset <= previous || offset + 4 > bytes)
                {
                    return false;
                }

                offsets[id] = offset;
                hashes[id] = BitConverter.ToInt32(data, at + 8);
                previous = offset;
            }

            return true;
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

        private static void Replace(string source, string destination)
        {
            if (File.Exists(destination))
            {
                try
                {
                    // Atomic on NTFS: a reader sees the old head or the new one, never half of one.
                    File.Replace(source, destination, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                    // A file system without it: the old head goes first, so a stop in between finds no head at all.
                }

                File.Delete(destination);
                File.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        // Files of earlier generations. One still open (a worker reading an old table) goes at a later save.
        private void DeleteOthers()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                HeadName,
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
            textGeneration = rowsGeneration = 0;
            textsSaved = rowsSaved = 0;
            textBytes = 0;
            try
            {
                if (File.Exists(HeadPath))
                {
                    File.Delete(HeadPath);
                }

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

        /// <summary>The text at <paramref name="offset"/>, or null when it can't be read. Callers hold the lock.</summary>
        public string Read(long offset)
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
                if (length < 0 || offset + 4 + bytes > valid)
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
