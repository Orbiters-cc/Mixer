using System;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Which vertices changed, by page of <see cref="PageSize"/>: one bit per page. Marking a vertex is a shift and an OR,
    /// marking all of them is a flag, and the changed part comes out as runs of whole pages, which is what the flush
    /// recomputes and uploads. (A set of vertex indices cost a hash insert per vertex, several megabytes for a whole
    /// mesh, and was only ever asked whether it was empty.)
    /// </summary>
    internal sealed class VertexPages
    {
        public const int PageShift = 10;
        public const int PageSize = 1 << PageShift;

        private readonly ulong[] bits;
        private readonly int vertexCount, pageCount;
        private bool any, all;

        public VertexPages(int vertexCount)
        {
            this.vertexCount = Math.Max(0, vertexCount);
            pageCount = (this.vertexCount + PageSize - 1) >> PageShift;
            bits = new ulong[(pageCount + 63) >> 6];
        }

        public int VertexCount => vertexCount;
        /// <summary>Some vertex is marked.</summary>
        public bool Any => any;
        /// <summary>Every vertex is marked.</summary>
        public bool All => all;

        public void Add(int v)
        {
            if ((uint)v >= (uint)vertexCount) return;
            int page = v >> PageShift;
            bits[page >> 6] |= 1UL << (page & 63);
            any = true;
        }

        /// <summary>Marks vertices <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> - 1.</summary>
        public void AddRange(int start, int count)
        {
            int end = Math.Min(vertexCount, start + count);
            start = Math.Max(0, start);
            if (end <= start) return;
            for (int page = start >> PageShift, last = (end - 1) >> PageShift; page <= last; page++) bits[page >> 6] |= 1UL << (page & 63);
            any = true;
        }

        public void AddAll()
        {
            if (vertexCount == 0) return;
            all = any = true;
        }

        public void UnionWith(VertexPages other)
        {
            if (other == null || !other.any) return;
            if (other.all) { AddAll(); return; }
            int words = Math.Min(bits.Length, other.bits.Length);
            for (int i = 0; i < words; i++) bits[i] |= other.bits[i];
            any = true;
        }

        public void Clear()
        {
            if (!any) return;
            Array.Clear(bits, 0, bits.Length);
            any = all = false;
        }

        private bool IsSet(int page) => all || (bits[page >> 6] & (1UL << (page & 63))) != 0;

        /// <summary>
        /// The next run of marked pages at or after page <paramref name="page"/>, in vertices (the last run ends at the
        /// vertex count); <paramref name="page"/> moves past it. False when no marked page is left. Start with 0.
        /// </summary>
        public bool NextRun(ref int page, out int start, out int count)
        {
            start = count = 0;
            if (!any) return false;
            while (page < pageCount && !IsSet(page))
            {
                // A whole empty word at once.
                if ((page & 63) == 0 && bits[page >> 6] == 0) { page += 64; continue; }
                page++;
            }
            if (page >= pageCount) return false;
            int first = page;
            while (page < pageCount && IsSet(page)) page++;
            start = first << PageShift;
            count = Math.Min(vertexCount, page << PageShift) - start;
            return true;
        }

        /// <summary>How many vertices the marked pages hold (whole pages; the last one as far as the vertex count).</summary>
        public int MarkedVertexCount()
        {
            int total = 0, page = 0;
            while (NextRun(ref page, out _, out int count)) total += count;
            return total;
        }
    }
}
