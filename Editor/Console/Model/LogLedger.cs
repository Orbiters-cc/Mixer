using System;
using System.Collections.Generic;
using System.IO;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// When each message was first and last seen, across editor sessions and clears: how long a log has been appearing
    /// (and, with <see cref="GitHistory"/>, since which commit). Messages are told apart by their text. Kept in
    /// Library/Orbiters/Logger/ledger.bin, at most <see cref="MaxEntries"/> messages (the longest unseen go first).
    /// Main thread.
    /// </summary>
    internal static class LogLedger
    {
        private const int Magic = 0x4C444752; // "RGDL"
        private const int FormatVersion = 1;
        private const int MaxEntries = 100_000;
        private const int MaxHashedChars = 2000;

        private struct Entry
        {
            public long First;
            public long Last;
        }

        private static Dictionary<ulong, Entry> entries;
        private static bool dirty;
        private static int scanned;
        private static int scannedEpoch = -1;
        private static LogStore scannedStore;

        public static string FilePath => Path.Combine("Library", "Orbiters", "Logger", "ledger.bin");

        /// <summary>Notes the messages that appeared since the last call.</summary>
        public static void Track(LogStore store)
        {
            if (store == null)
            {
                return;
            }

            EnsureLoaded();
            if (!ReferenceEquals(store, scannedStore) || store.Epoch != scannedEpoch)
            {
                // Cleared, trimmed or merged with history: every message again (a few thousand at most).
                scannedStore = store;
                scannedEpoch = store.Epoch;
                scanned = 0;
            }

            for (int i = scanned; i < store.MessageCount; i++)
            {
                Note(store, i);
            }

            scanned = store.MessageCount;
        }

        /// <summary>When the message with this text was first and last seen, if it ever was.</summary>
        public static bool TryGet(string text, out DateTime firstUtc, out DateTime lastUtc)
        {
            EnsureLoaded();
            if (entries.TryGetValue(Hash(text), out var entry))
            {
                firstUtc = new DateTime(entry.First, DateTimeKind.Utc);
                lastUtc = new DateTime(entry.Last, DateTimeKind.Utc);
                return true;
            }

            firstUtc = lastUtc = default;
            return false;
        }

        /// <summary>Updates every message's last time and writes the file (before a reload, on quit, now and then).</summary>
        public static void Save(LogStore store)
        {
            if (entries == null)
            {
                return;
            }

            if (store != null)
            {
                for (int i = 0; i < store.MessageCount; i++)
                {
                    Note(store, i);
                }
            }

            if (!dirty)
            {
                return;
            }

            if (entries.Count > MaxEntries)
            {
                var oldest = new List<KeyValuePair<ulong, Entry>>(entries);
                oldest.Sort((a, b) => a.Value.Last.CompareTo(b.Value.Last));
                for (int i = 0; i < oldest.Count - MaxEntries * 4 / 5; i++)
                {
                    entries.Remove(oldest[i].Key);
                }
            }

            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(entries.Count);
                    foreach (var pair in entries)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.First);
                        writer.Write(pair.Value.Last);
                    }
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporary, path);
                dirty = false;
            }
            catch (Exception)
            {
                // Kept in memory; the next save tries again.
            }
        }

        private static void Note(LogStore store, int messageId)
        {
            ref var message = ref store.Message(messageId);
            if (message.Count == 0 || message.First < 0 || message.Last < 0 || message.First >= store.Count || message.Last >= store.Count)
            {
                return;
            }

            ulong key = Hash(store.Texts.Plain(message.Text));
            long first = store.TimeAt(message.First);
            long last = store.TimeAt(message.Last);
            if (entries.TryGetValue(key, out var entry))
            {
                if (first >= entry.First && last <= entry.Last)
                {
                    return;
                }

                entry.First = Math.Min(entry.First, first);
                entry.Last = Math.Max(entry.Last, last);
                entries[key] = entry;
            }
            else
            {
                entries[key] = new Entry { First = first, Last = last };
            }

            dirty = true;
        }

        private static void EnsureLoaded()
        {
            if (entries != null)
            {
                return;
            }

            entries = new Dictionary<ulong, Entry>();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                using (var reader = new BinaryReader(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
                    {
                        return;
                    }

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        ulong key = reader.ReadUInt64();
                        entries[key] = new Entry { First = reader.ReadInt64(), Last = reader.ReadInt64() };
                    }
                }
            }
            catch (Exception)
            {
                // A damaged file starts over.
            }
        }

        // FNV-1a over the text (its beginning for very long messages): stable across sessions.
        internal static ulong Hash(string text)
        {
            ulong hash = 14695981039346656037UL;
            if (text == null)
            {
                return hash;
            }

            int length = Math.Min(text.Length, MaxHashedChars);
            for (int i = 0; i < length; i++)
            {
                hash = (hash ^ text[i]) * 1099511628211UL;
            }

            return hash;
        }
    }
}
