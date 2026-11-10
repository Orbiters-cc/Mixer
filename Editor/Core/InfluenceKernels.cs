using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Burst kernels over a document's influence slots (<see cref="MeshDocument.InfluenceArrays"/>). The managed arrays
    /// are pinned for the length of a job that completes before the call returns: nothing native outlives the call.
    /// <para>
    /// The effective influences, what Unity skins a vertex with once the slots are uploaded, are defined here once and
    /// used by the upload (<see cref="Pack"/>) and by both skinning paths (<see cref="SkinningMap.BlendedMatrix"/>,
    /// <see cref="MeshKernels.Skin"/>), so the world picture brushes aim at is the mesh on screen: a vertex without
    /// influences is bone 0 at full weight (Unity needs one per vertex), the others are their weights over their sum
    /// (each 1 / count when the sum is 0).
    /// </para>
    /// </summary>
    internal static unsafe class InfluenceKernels
    {
        /// <summary>The weight Unity gets for an influence of weight <paramref name="weight"/> on a vertex of <paramref name="count"/> influences summing to <paramref name="sum"/>.</summary>
        public static float Effective(float weight, float sum, int count) => sum > 0f ? weight / sum : 1f / count;

        /// <summary>
        /// Unity's skin arrays from the slots: per vertex its count (at least 1) and its effective influences, strongest
        /// first. <paramref name="packed"/> holds the sum of the counts.
        /// </summary>
        public static void Pack(int[] bones, float[] weights, byte[] counts, int slots, NativeArray<byte> perVertex, NativeArray<BoneWeight1> packed)
        {
            fixed (int* b = bones)
            fixed (float* w = weights)
            fixed (byte* c = counts)
            {
                PackInto(b, w, c, slots, counts.Length, (byte*)perVertex.GetUnsafePtr(), (BoneWeight1*)packed.GetUnsafePtr(), null, packed.Length);
            }
        }

        /// <summary><see cref="Pack"/> into raw memory, with where each vertex starts in <paramref name="start"/> (V + 1 entries) when given.</summary>
        public static void PackInto(int* bones, float* weights, byte* counts, int slots, int vertexCount, byte* perVertex, BoneWeight1* packed, int* start, int packedLength)
        {
            new PackJob
            {
                Bones = bones, Weights = weights, Counts = counts, Slots = slots, VertexCount = vertexCount,
                PerVertex = perVertex, Packed = packed, Start = start, PackedLength = packedLength,
            }.Run();
        }

        /// <summary>
        /// Writes vertex <paramref name="v"/>'s effective influences at <paramref name="packed"/> (as many as
        /// <see cref="EffectiveCount"/>): what <see cref="Pack"/> writes for it.
        /// </summary>
        public static void PackVertex(int* bones, float* weights, byte* counts, int slots, int v, BoneWeight1* packed)
        {
            int n = counts[v], at = v * slots;
            if (n == 0) { packed[0] = new BoneWeight1 { boneIndex = 0, weight = 1f }; return; }
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += weights[at + i];
            for (int i = 0; i < n; i++) packed[i] = new BoneWeight1 { boneIndex = bones[at + i], weight = Effective(weights[at + i], sum, n) };
        }

        /// <summary>How many influences Unity gets for a vertex of <paramref name="count"/> slots in use.</summary>
        public static int EffectiveCount(int count) => count > 0 ? count : 1;

        [BurstCompile]
        private struct PackJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public int* Bones;
            [NativeDisableUnsafePtrRestriction] public float* Weights;
            [NativeDisableUnsafePtrRestriction] public byte* Counts;
            [NativeDisableUnsafePtrRestriction] public byte* PerVertex;
            [NativeDisableUnsafePtrRestriction] public BoneWeight1* Packed;
            [NativeDisableUnsafePtrRestriction] public int* Start;
            public int Slots, VertexCount, PackedLength;

            public void Execute()
            {
                int cursor = 0;
                for (int v = 0; v < VertexCount; v++)
                {
                    int n = EffectiveCount(Counts[v]);
                    if (Start != null) Start[v] = cursor;
                    if (cursor + n > PackedLength) return;
                    PerVertex[v] = (byte)n;
                    PackVertex(Bones, Weights, Counts, Slots, v, Packed + cursor);
                    cursor += n;
                }
                if (Start != null) Start[VertexCount] = cursor;
            }
        }
    }
}
