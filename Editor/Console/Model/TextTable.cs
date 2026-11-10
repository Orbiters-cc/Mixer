using System;
using System.Threading;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Every distinct text the Mixer console has seen (messages, stack traces, callsites), stored once and referred to by id.
    /// A log repeated a million times costs one string. Id 0 is the empty text.
    /// <para>
    /// Only the main thread adds texts. The arrays only grow by copying, so a worker thread that read <see cref="Count"/>
    /// can read that prefix while texts keep being added.
    /// </para>
    /// <para>
    /// Each text's hash is kept beside it (a stable hash, saved with the session), so a table read back after a script
    /// reload rebuilds its lookup from the saved hashes without reading a single text. Such a table reads each text from
    /// the session file the first time it is asked for (any thread), see <see cref="Restore"/>.
    /// </para>
    /// </summary>
    internal sealed class TextTable
    {
        private string[] raw;
        private string[] plain;
        private int[] hashes;
        // Open addressing, linear probing: a text id, or 0 for a free slot (the empty text is never in it).
        private int[] slots;
        private int count;
        // Texts still in the session file: ids below fileCount whose raw entry is null.
        private readonly SessionTextFile file;
        private readonly long[] offsets;
        private readonly int fileCount;
        private long totalChars;
        // The last few strings interned, by reference: a flood of one log hands the same instances again (see LogCapture).
        private readonly string[] recentTexts = new string[4];
        private readonly int[] recentIds = new int[4];
        private int recentNext;

        public TextTable(int capacity = 1024) : this(NewGeneration(), Math.Max(capacity, 16), null, null, 0)
        {
            raw[0] = string.Empty;
            count = 1;
        }

        private TextTable(long generation, int capacity, SessionTextFile file, long[] offsets, int fileCount)
        {
            Generation = generation;
            raw = new string[capacity];
            plain = new string[capacity];
            hashes = new int[capacity];
            slots = new int[SlotsFor(capacity)];
            this.file = file;
            this.offsets = offsets;
            this.fileCount = fileCount;
        }

        /// <summary>
        /// A table whose first <paramref name="textCount"/> texts are in a session file: <paramref name="textHashes"/>
        /// and <paramref name="textOffsets"/> by id (index 0 unused).
        /// </summary>
        internal static TextTable Restore(long generation, int textCount, long textChars, int[] textHashes, long[] textOffsets, SessionTextFile source)
        {
            var table = new TextTable(generation, Math.Max(16, textCount + textCount / 4), source, textOffsets, textCount);
            table.raw[0] = string.Empty;
            Array.Copy(textHashes, table.hashes, textCount);
            for (int id = 1; id < textCount; id++)
            {
                table.Insert(id, table.hashes[id]);
            }

            table.count = textCount;
            table.totalChars = textChars;
            return table;
        }

        /// <summary>Tells this table apart from every other (a new one after a clear or a compaction): the session file follows it.</summary>
        public long Generation { get; }

        public int Count => count;

        /// <summary>The length of every text together: what the table keeps alive.</summary>
        public long TotalChars => totalChars;

        public string this[int id] => id > 0 && id < count ? Raw(id) : string.Empty;

        /// <summary>The text without rich text tags (what search and copies use).</summary>
        public string Plain(int id)
        {
            if (id <= 0 || id >= count)
            {
                return string.Empty;
            }

            string text = Raw(id);
            return Volatile.Read(ref plain[id]) ?? text;
        }

        /// <summary>The stable hash of a text (<see cref="Hash"/>), as saved with the session.</summary>
        internal int HashAt(int id) => hashes[id];

        /// <summary>True when the text is in memory (not waiting in the session file).</summary>
        internal bool IsLoaded(int id) => id >= 0 && id < count && Volatile.Read(ref raw[id]) != null;

        public int Intern(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            for (int i = 0; i < recentTexts.Length; i++)
            {
                if (ReferenceEquals(recentTexts[i], text))
                {
                    return recentIds[i];
                }
            }

            int hash = Hash(text);
            int id = Find(text, hash);
            if (id > 0)
            {
                Remember(text, id);
                return id;
            }

            if (count == raw.Length)
            {
                if (file != null)
                {
                    // A text being read from the file lands in the arrays being copied, never in a discarded one.
                    lock (file)
                    {
                        Grow();
                    }
                }
                else
                {
                    Grow();
                }
            }

            id = count;
            raw[id] = text;
            plain[id] = RichText.StripOrNull(text);
            hashes[id] = hash;
            if ((count + 1) * 2 > slots.Length)
            {
                Rehash(SlotsFor(count + 1));
            }

            Insert(id, hash);
            totalChars += text.Length;
            Volatile.Write(ref count, id + 1);
            Remember(text, id);
            return id;
        }

        private void Remember(string text, int id)
        {
            recentTexts[recentNext] = text;
            recentIds[recentNext] = id;
            recentNext = (recentNext + 1) & (recentTexts.Length - 1);
        }

        public bool TryFind(string text, out int id)
        {
            if (string.IsNullOrEmpty(text))
            {
                id = 0;
                return true;
            }

            id = Find(text, Hash(text));
            return id > 0;
        }

        /// <summary>FNV-1a over the UTF-16 units: the same in every domain and editor, unlike <see cref="string.GetHashCode"/>.</summary>
        internal static int Hash(string text)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < text.Length; i++)
            {
                hash = (hash ^ text[i]) * 16777619u;
            }

            return (int)hash;
        }

        // Copy into new arrays rather than resizing in place: readers keep a valid prefix.
        private void Grow()
        {
            int size = raw.Length * 2;
            var nextRaw = new string[size];
            var nextPlain = new string[size];
            Array.Copy(raw, nextRaw, count);
            Array.Copy(plain, nextPlain, count);
            Array.Resize(ref hashes, size);
            Volatile.Write(ref plain, nextPlain);
            Volatile.Write(ref raw, nextRaw);
        }

        private int Find(string text, int hash)
        {
            int mask = slots.Length - 1;
            for (int slot = hash & mask; ; slot = (slot + 1) & mask)
            {
                int id = slots[slot];
                if (id == 0)
                {
                    return 0;
                }

                if (hashes[id] == hash && string.Equals(Raw(id), text, StringComparison.Ordinal))
                {
                    return id;
                }
            }
        }

        private void Insert(int id, int hash)
        {
            int mask = slots.Length - 1;
            int slot = hash & mask;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            slots[slot] = id;
        }

        private void Rehash(int size)
        {
            slots = new int[size];
            for (int id = 1; id < count; id++)
            {
                Insert(id, hashes[id]);
            }
        }

        // At most half full.
        private static int SlotsFor(int texts)
        {
            int size = 32;
            while (size < texts * 2)
            {
                size <<= 1;
            }

            return size;
        }

        private string Raw(int id)
        {
            var items = Volatile.Read(ref raw);
            return Volatile.Read(ref items[id]) ?? Load(id);
        }

        // A text still in the session file: read once, by whichever thread asks first.
        private string Load(int id)
        {
            if (file == null || id >= fileCount)
            {
                return string.Empty;
            }

            lock (file)
            {
                var items = Volatile.Read(ref raw);
                string text = items[id];
                if (text != null)
                {
                    return text;
                }

                text = file.Read(offsets[id]) ?? string.Empty;
                // The plain text first: a reader that sees the raw text sees its plain text too.
                var plainItems = Volatile.Read(ref plain);
                Volatile.Write(ref plainItems[id], RichText.StripOrNull(text));
                Volatile.Write(ref items[id], text);
                return text;
            }
        }

        private static long generations;

        private static long NewGeneration()
        {
            // Unique across domains of one editor (statics reset at a reload) and across editors.
            long random = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x7FFF_FFFF_FFFF_0000L;
            return random | (Interlocked.Increment(ref generations) & 0xFFFF);
        }
    }
}
