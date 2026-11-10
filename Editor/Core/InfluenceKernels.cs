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
    /// </summary>
    internal static unsafe class InfluenceKernels
    {
        /// <summary>
        /// Unity's skin arrays from the slots: per vertex its count (at least 1) and its influences normalized, strongest
        /// first; a vertex without any gets bone 0 at full weight. <paramref name="packed"/> holds the sum of the counts.
        /// </summary>
        public static void Pack(int[] bones, float[] weights, byte[] counts, int slots, NativeArray<byte> perVertex, NativeArray<BoneWeight1> packed)
        {
            fixed (int* b = bones)
            fixed (float* w = weights)
            fixed (byte* c = counts)
            {
                new PackJob
                {
                    Bones = b, Weights = w, Counts = c, Slots = slots, VertexCount = counts.Length,
                    PerVertex = (byte*)perVertex.GetUnsafePtr(), Packed = (BoneWeight1*)packed.GetUnsafePtr(), PackedLength = packed.Length,
                }.Run();
            }
        }

        [BurstCompile]
        private struct PackJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public int* Bones;
            [NativeDisableUnsafePtrRestriction] public float* Weights;
            [NativeDisableUnsafePtrRestriction] public byte* Counts;
            [NativeDisableUnsafePtrRestriction] public byte* PerVertex;
            [NativeDisableUnsafePtrRestriction] public BoneWeight1* Packed;
            public int Slots, VertexCount, PackedLength;

            public void Execute()
            {
                int cursor = 0;
                for (int v = 0; v < VertexCount; v++)
                {
                    int n = Counts[v], at = v * Slots;
                    if (n == 0)
                    {
                        if (cursor >= PackedLength) return;
                        PerVertex[v] = 1;
                        Packed[cursor++] = new BoneWeight1 { boneIndex = 0, weight = 1f };
                        continue;
                    }
                    float sum = 0f;
                    for (int i = 0; i < n; i++) sum += Weights[at + i];
                    PerVertex[v] = (byte)n;
                    if (cursor + n > PackedLength) return;
                    for (int i = 0; i < n; i++)
                        Packed[cursor++] = new BoneWeight1 { boneIndex = Bones[at + i], weight = sum > 0f ? Weights[at + i] / sum : 1f / n };
                }
            }
        }
    }
}
