using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// A <see cref="MeshNeighbors"/> weld as the Burst kernels read it: each relation (members, adjacent welded vertices,
    /// triangles around) as one array of values with where each welded vertex's run starts. The values are the Toolkit's
    /// own arrays, reached through the spans it hands out (pinned by the caller with <c>fixed</c> for the length of a job:
    /// see <see cref="Pin"/>); only the starts are made here, once per weld (W + 1 ints per relation).
    /// <para>
    /// The Toolkit keeps each relation as one array in welded order, so the span of a welded vertex begins where the one
    /// before it ends. That is checked once when the starts are made; should a Toolkit build ever lay them out otherwise,
    /// the values are copied here instead, so a kernel never reads past a span.
    /// </para>
    /// </summary>
    internal sealed class FlatWeld
    {
        private static readonly ConditionalWeakTable<MeshNeighbors, FlatWeld> table = new ConditionalWeakTable<MeshNeighbors, FlatWeld>();
        // What a pin points at for a relation no welded vertex has (a mesh without triangles): never read.
        private static readonly int[] none = { 0 };

        public readonly MeshNeighbors Neighbors;
        /// <summary>Where each welded vertex's run starts in its relation's values (W + 1 entries, the last one the total).</summary>
        public readonly int[] MemberStart, AdjacentStart, TriangleStart;
        private readonly Relation members, adjacent, triangles;

        /// <summary>The flat form of <paramref name="neighbors"/>, made on first use and kept as long as the weld lives.</summary>
        public static FlatWeld Of(MeshNeighbors neighbors) => table.GetValue(neighbors, n => new FlatWeld(n));

        public int WeldedCount => Neighbors.WeldedCount;

        private enum Kind { Members, Adjacent, Triangles }

        // A relation's values: the first welded vertex with a run (its span starts the Toolkit's array), or a copy.
        private readonly struct Relation
        {
            public readonly int First;
            public readonly int[] Copy;
            public Relation(int first, int[] copy) { First = first; Copy = copy; }
        }

        private FlatWeld(MeshNeighbors neighbors)
        {
            Neighbors = neighbors;
            members = Make(neighbors, Kind.Members, out MemberStart);
            adjacent = Make(neighbors, Kind.Adjacent, out AdjacentStart);
            triangles = Make(neighbors, Kind.Triangles, out TriangleStart);
        }

        private static ReadOnlySpan<int> Span(MeshNeighbors n, Kind kind, int w) =>
            kind == Kind.Members ? n.MembersOf(w) : kind == Kind.Adjacent ? n.AdjacentTo(w) : n.TrianglesAround(w);

        private static unsafe Relation Make(MeshNeighbors n, Kind kind, out int[] start)
        {
            int count = n.WeldedCount;
            start = new int[count + 1];
            int first = -1;
            for (int w = 0; w < count; w++)
            {
                int length = Span(n, kind, w).Length;
                if (length > 0 && first < 0) first = w;
                start[w + 1] = start[w] + length;
            }
            if (first < 0) return new Relation(-1, null);
            bool contiguous = true;
            fixed (int* origin = Span(n, kind, first))
            {
                for (int w = first; w < count && contiguous; w++)
                {
                    var span = Span(n, kind, w);
                    if (span.Length == 0) continue;
                    fixed (int* at = span) contiguous = at == origin + start[w];
                }
            }
            if (contiguous) return new Relation(first, null);
            var copy = new int[start[count]];
            for (int w = 0; w < count; w++) Span(n, kind, w).CopyTo(new Span<int>(copy, start[w], start[w + 1] - start[w]));
            return new Relation(-1, copy);
        }

        private ref int Base(Relation relation, Kind kind)
        {
            if (relation.Copy != null) return ref relation.Copy[0];
            if (relation.First < 0) return ref none[0];
            return ref MemoryMarshal.GetReference(Span(Neighbors, kind, relation.First));
        }

        /// <summary>The first member value: pin it (<c>fixed (int* m = &amp;weld.Members())</c>) to read every run through <see cref="MemberStart"/>.</summary>
        public ref int Members() => ref Base(members, Kind.Members);
        /// <summary>The first adjacency value (see <see cref="Members"/>).</summary>
        public ref int Adjacent() => ref Base(adjacent, Kind.Adjacent);
        /// <summary>The first triangle-around value (see <see cref="Members"/>).</summary>
        public ref int Triangles() => ref Base(triangles, Kind.Triangles);

        /// <summary>Every member value, as a kernel reads them (for checks: kernels pin <see cref="Members"/>).</summary>
        public ReadOnlySpan<int> AllMembers => MemoryMarshal.CreateReadOnlySpan(ref Members(), MemberStart[WeldedCount]);
        /// <summary>Every adjacency value, as a kernel reads them.</summary>
        public ReadOnlySpan<int> AllAdjacent => MemoryMarshal.CreateReadOnlySpan(ref Adjacent(), AdjacentStart[WeldedCount]);
        /// <summary>Every triangle-around value, as a kernel reads them.</summary>
        public ReadOnlySpan<int> AllTriangles => MemoryMarshal.CreateReadOnlySpan(ref Triangles(), TriangleStart[WeldedCount]);

        /// <summary>
        /// The weld's arrays as pointers for a kernel. Every argument must be pinned by the caller (fixed) for as long as
        /// the kernel runs: <c>fixed (int* weld = n.Weld) fixed (int* ms = flat.MemberStart) fixed (int* m = &amp;flat.Members()) …</c>.
        /// </summary>
        public static unsafe WeldPointers Pin(int* weld, int* memberStart, int* members, int* adjacentStart, int* adjacent, int* triangleStart, int* triangles, int weldedCount) =>
            new WeldPointers
            {
                Weld = weld, MemberStart = memberStart, Members = members, AdjacentStart = adjacentStart, Adjacent = adjacent,
                TriangleStart = triangleStart, Triangles = triangles, WeldedCount = weldedCount,
            };
    }

    /// <summary>A pinned <see cref="FlatWeld"/> (see <see cref="FlatWeld.Pin"/>): valid only inside the caller's fixed block.</summary>
    internal unsafe struct WeldPointers
    {
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* Weld;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* MemberStart;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* Members;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* AdjacentStart;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* Adjacent;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* TriangleStart;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public int* Triangles;
        public int WeldedCount;
    }
}
