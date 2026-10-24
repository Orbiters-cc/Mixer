using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>What the snapshot remembers besides the logs.</summary>
    internal sealed class SnapshotInfo
    {
        /// <summary>Entries in Unity's console when the snapshot was saved: rows after it arrived during the reload.</summary>
        public int UnityRows;
        /// <summary>The last of those rows (first two lines), to check the console wasn't cleared since.</summary>
        public string UnityAnchor = string.Empty;
        /// <summary>Console history not imported yet when scripts reloaded (rows <see cref="HistoryFrom"/> to <see cref="HistoryTo"/>).</summary>
        public int HistoryFrom = -1;
        public int HistoryTo = -1;
        public string HistoryAnchor = string.Empty;
    }

    /// <summary>
    /// Keeps the session's logs across script reloads (which wipe managed memory): the store is written before a reload
    /// and read back after it, a few milliseconds for a hundred thousand logs. The file belongs to this editor process;
    /// another session (or a crash) leaves one that is ignored and replaced.
    /// </summary>
    internal static class LogSnapshot
    {
        private const int Magic = 0x524C474F; // "OGLR"
        private const int FormatVersion = 1;
        private const int ChunkBytes = 1 << 20;

        public static string FilePath => Path.Combine("Library", "Orbiters", "Logger", "session.bin");

        private static long EditorStartTicks => DateTime.UtcNow.Ticks - (long)(EditorApplication.timeSinceStartup * TimeSpan.TicksPerSecond);

        private static int ProcessId
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().Id;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public static void Save(LogStore store, SnapshotInfo info)
        {
            string path = FilePath;
            string temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream))
            {
                Write(writer, store, info, ProcessId, EditorStartTicks);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporary, path);
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception)
            {
                // A locked file is replaced at the next save.
            }
        }

        /// <summary>The store saved by this editor session, or null.</summary>
        public static LogStore TryLoad(out SnapshotInfo info)
        {
            info = null;
            string path = FilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var reader = new BinaryReader(stream))
                {
                    var store = Read(reader, ProcessId, EditorStartTicks, out info);
                    if (store == null)
                    {
                        reader.Close();
                        Delete();
                    }

                    return store;
                }
            }
            catch (Exception)
            {
                Delete();
                return null;
            }
        }

        internal static void Write(BinaryWriter writer, LogStore store, SnapshotInfo info, int processId, long editorStart)
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

            var texts = store.Texts;
            writer.Write(texts.Count);
            for (int i = 1; i < texts.Count; i++)
            {
                writer.Write(texts[i]);
            }

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
                writer.Write((byte)message.Variant);
                writer.Write((byte)message.Flags);
            }

            store.ExportColumns(out long[] times, out int[] owners, out byte[] flags);
            int count = store.Count;
            writer.Write(count);
            var buffer = new byte[ChunkBytes];
            WriteBlock(writer, times, count * sizeof(long), buffer);
            WriteBlock(writer, owners, count * sizeof(int), buffer);
            WriteBlock(writer, flags, count, buffer);

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
        }

        internal static LogStore Read(BinaryReader reader, int processId, long editorStart, out SnapshotInfo info)
        {
            info = null;
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            int savedProcess = reader.ReadInt32();
            long savedStart = reader.ReadInt64();
            if (savedProcess != processId || Math.Abs(savedStart - editorStart) > TimeSpan.TicksPerMinute)
            {
                return null;
            }

            info = new SnapshotInfo
            {
                UnityRows = reader.ReadInt32(),
                UnityAnchor = reader.ReadString(),
                HistoryFrom = reader.ReadInt32(),
                HistoryTo = reader.ReadInt32(),
                HistoryAnchor = reader.ReadString()
            };
            var grouping = (GroupingMode)reader.ReadByte();
            int maxOccurrences = reader.ReadInt32();

            int textCount = reader.ReadInt32();
            var texts = new TextTable(Math.Max(16, textCount + textCount / 4));
            for (int i = 1; i < textCount; i++)
            {
                texts.Intern(reader.ReadString());
            }

            var store = new LogStore(64) { MaxOccurrences = maxOccurrences };
            store.ReplaceTexts(texts);
            int sourceCount = reader.ReadInt32();
            for (int i = 0; i < sourceCount; i++)
            {
                string key = reader.ReadString();
                string name = reader.ReadString();
                store.Sources.Restore(key, name);
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
                    Variant = (LogVariant)reader.ReadByte(),
                    Flags = (MessageFlags)reader.ReadByte(),
                    Explanation = -2
                };
            }

            int count = reader.ReadInt32();
            int capacity = Math.Max(4096, count + count / 4);
            var times = new long[capacity];
            var owners = new int[capacity];
            var flags = new byte[capacity];
            var buffer = new byte[ChunkBytes];
            ReadBlock(reader, times, count * sizeof(long), buffer);
            ReadBlock(reader, owners, count * sizeof(int), buffer);
            ReadBlock(reader, flags, count, buffer);

            int contextCount = reader.ReadInt32();
            var contexts = new Dictionary<int, int>(contextCount);
            for (int i = 0; i < contextCount; i++)
            {
                int key = reader.ReadInt32();
                contexts[key] = reader.ReadInt32();
            }

            int eventCount = reader.ReadInt32();
            var events = new List<SessionEvent>(eventCount);
            for (int i = 0; i < eventCount; i++)
            {
                long time = reader.ReadInt64();
                events.Add(new SessionEvent(time, (SessionEventKind)reader.ReadByte()));
            }

            for (int i = 0; i < messageCount; i++)
            {
                if (messages[i].Source < 0 || messages[i].Source >= store.Sources.Count)
                {
                    messages[i].Source = SourceCatalog.Unity;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if ((uint)owners[i] >= (uint)messageCount)
                {
                    return null;
                }
            }

            store.Import(times, owners, flags, count, messages, messageCount, grouping, contexts, events);
            return store;
        }

        private static void WriteBlock(BinaryWriter writer, Array source, int bytes, byte[] buffer)
        {
            int offset = 0;
            while (offset < bytes)
            {
                int length = Math.Min(buffer.Length, bytes - offset);
                Buffer.BlockCopy(source, offset, buffer, 0, length);
                writer.Write(buffer, 0, length);
                offset += length;
            }
        }

        private static void ReadBlock(BinaryReader reader, Array destination, int bytes, byte[] buffer)
        {
            int offset = 0;
            while (offset < bytes)
            {
                int length = Math.Min(buffer.Length, bytes - offset);
                int read = reader.Read(buffer, 0, length);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                Buffer.BlockCopy(buffer, 0, destination, offset, read);
                offset += read;
            }
        }
    }
}
