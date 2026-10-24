using System;
using System.Collections.Generic;
using System.Threading;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Every distinct text the Mixer console has seen (messages, stack traces, callsites), stored once and referred to by id.
    /// A log repeated a million times costs one string. Id 0 is the empty text.
    /// <para>
    /// Only the main thread adds texts. The arrays only grow by copying, so a worker thread that took
    /// <see cref="RawItems"/> and <see cref="Count"/> can read that prefix while texts keep being added.
    /// </para>
    /// </summary>
    internal sealed class TextTable
    {
        private string[] raw;
        private string[] plain;
        private int count;
        private readonly Dictionary<string, int> index;

        public TextTable(int capacity = 1024)
        {
            capacity = Math.Max(capacity, 16);
            raw = new string[capacity];
            plain = new string[capacity];
            index = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
            raw[0] = string.Empty;
            index[string.Empty] = 0;
            count = 1;
        }

        public int Count => count;

        /// <summary>The texts as logged; ids below <see cref="Count"/> never change.</summary>
        public string[] RawItems => raw;

        /// <summary>The texts without Unity rich text tags, or null where that is the raw text.</summary>
        public string[] PlainItems => plain;

        public string this[int id] => id > 0 && id < count ? raw[id] : string.Empty;

        /// <summary>The text without rich text tags (what search and copies use).</summary>
        public string Plain(int id) => id > 0 && id < count ? plain[id] ?? raw[id] : string.Empty;

        public int Intern(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            if (index.TryGetValue(text, out int id))
            {
                return id;
            }

            if (count == raw.Length)
            {
                // Copy into new arrays rather than resizing in place: readers keep a valid prefix.
                int size = raw.Length * 2;
                var nextRaw = new string[size];
                var nextPlain = new string[size];
                Array.Copy(raw, nextRaw, count);
                Array.Copy(plain, nextPlain, count);
                plain = nextPlain;
                raw = nextRaw;
            }

            id = count;
            raw[id] = text;
            plain[id] = RichText.StripOrNull(text);
            index[text] = id;
            Volatile.Write(ref count, id + 1);
            return id;
        }

        /// <summary>For worker threads: the count first, then arrays holding at least that many texts.</summary>
        public int Snapshot(out string[] rawItems, out string[] plainItems)
        {
            int snapshotCount = Volatile.Read(ref count);
            plainItems = Volatile.Read(ref plain);
            rawItems = Volatile.Read(ref raw);
            return snapshotCount;
        }

        public bool TryFind(string text, out int id)
        {
            if (string.IsNullOrEmpty(text))
            {
                id = 0;
                return true;
            }

            return index.TryGetValue(text, out id);
        }
    }
}
