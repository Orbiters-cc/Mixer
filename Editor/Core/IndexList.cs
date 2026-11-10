using System;
using System.Collections;
using System.Collections.Generic;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// A list of indices over an array a kernel can pin (<see cref="Items"/>, the first <see cref="Count"/> in use): what a
    /// stroke step fills and hands to the BVH refit and the link without copying. Grows by doubling and never shrinks, so
    /// a list kept in a stroke scratch allocates nothing once it has reached a stroke's size.
    /// </summary>
    internal sealed class IndexList : IReadOnlyList<int>
    {
        public int[] Items;
        public int Count { get; private set; }

        public IndexList(int capacity = 256) { Items = new int[Math.Max(1, capacity)]; }

        public int this[int index] => (uint)index < (uint)Count ? Items[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public void Clear() => Count = 0;

        public void Add(int value)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = value;
        }

        /// <summary>Room for <paramref name="count"/> entries without growing (contents kept).</summary>
        public void Reserve(int count)
        {
            if (count > Items.Length) Array.Resize(ref Items, Math.Max(count, Items.Length * 2));
        }

        /// <summary>A copy of the entries in use.</summary>
        public int[] ToArray()
        {
            var result = new int[Count];
            Array.Copy(Items, result, Count);
            return result;
        }

        public IEnumerator<int> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return Items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
