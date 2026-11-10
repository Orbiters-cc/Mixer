using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Sends a document's influences to its working mesh. Unity takes bone weights for the whole mesh only
    /// (<see cref="Mesh.SetBoneWeights"/>: there is no sub-range form), so that one native call stays O(V). What is kept
    /// small is the work before it: the packed arrays (Unity's layout) are kept from one upload to the next, and when
    /// the vertices painted since keep their number of influences (a stroke going over them again) only those are
    /// packed again in place; otherwise everything is packed again into the same storage (a Burst pass). Nothing is
    /// allocated per upload: the kept arrays are handed to Unity through views of their pinned memory.
    /// </summary>
    internal sealed unsafe class BoneWeightUploader
    {
        private byte[] perVertex;
        private int[] start;
        private BoneWeight1[] packed;
        private int total;
        private bool valid;

        /// <summary>The last upload rewrote only the painted vertices (false: everything was packed again).</summary>
        public bool LastWasPatch { get; private set; }
        /// <summary>Vertices packed by the last upload.</summary>
        public int LastPacked { get; private set; }

        /// <summary>Uploads <paramref name="doc"/>'s influences to <paramref name="mesh"/> and clears its painted pages.</summary>
        public void Upload(Mesh mesh, MeshDocument doc)
        {
            doc.InfluenceArrays(out var bones, out var weights, out var counts, out int slots);
            var pages = doc.WeightPages;
            int count = doc.VertexCount;
            bool patch = valid && perVertex.Length == count && !pages.All && SameCounts(counts, pages);
            if (!patch)
            {
                total = 0;
                for (int v = 0; v < count; v++) total += InfluenceKernels.EffectiveCount(counts[v]);
                if (perVertex == null || perVertex.Length != count) { perVertex = new byte[count]; start = new int[count + 1]; }
                if (packed == null || packed.Length < total) packed = new BoneWeight1[total + total / 4 + 16];
            }
            int done = 0;
            fixed (int* b = bones)
            fixed (float* w = weights)
            fixed (byte* c = counts)
            fixed (byte* p = perVertex)
            fixed (int* s = start)
            fixed (BoneWeight1* q = packed)
            {
                if (patch)
                {
                    int page = 0;
                    while (pages.NextRun(ref page, out int first, out int run))
                        for (int v = first, end = first + run; v < end; v++, done++) InfluenceKernels.PackVertex(b, w, c, slots, v, q + s[v]);
                }
                else
                {
                    InfluenceKernels.PackInto(b, w, c, slots, count, p, q, s, total);
                    done = count;
                    valid = true;
                }
                Send(mesh, p, count, q, total);
            }
            pages.Clear();
            LastWasPatch = patch;
            LastPacked = done;
        }

        /// <summary>The kept arrays no longer describe what the mesh holds: the next upload packs everything.</summary>
        public void Invalidate() => valid = false;

        // Every painted vertex still has as many influences as Unity holds for it: their ranges do not move.
        private bool SameCounts(byte[] counts, VertexPages pages)
        {
            int page = 0;
            while (pages.NextRun(ref page, out int first, out int run))
                for (int v = first, end = first + run; v < end; v++)
                    if (InfluenceKernels.EffectiveCount(counts[v]) != perVertex[v]) return false;
            return true;
        }

        private static void Send(Mesh mesh, byte* perVertex, int count, BoneWeight1* packed, int total)
        {
            var counts = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(perVertex, count, Allocator.None);
            var weights = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<BoneWeight1>(packed, total, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref counts, AtomicSafetyHandle.GetTempMemoryHandle());
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref weights, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            mesh.SetBoneWeights(counts, weights);
        }
    }
}
