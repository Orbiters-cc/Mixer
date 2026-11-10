using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Burst kernels over whole meshes: the world picture through the skinning, through a transform, and bounds. They
    /// run on the document's managed arrays, pinned for the length of a job that completes before the call returns, so
    /// nothing native outlives a call (no lifetimes to keep across domain reloads, parked sessions or undo). The first
    /// calls of an editor session may run before Burst compiled them: the same code then runs on Mono, slower, not
    /// different.
    /// </summary>
    internal static unsafe class MeshKernels
    {
        private const int Batch = 2048;

        /// <summary>
        /// world[v] = the vertex's bones' matrices blended by its normalized weights, applied to positions[v] (+ offset[v]):
        /// linear blend skinning, as <see cref="SkinningMap.BlendedMatrix"/> blends for one vertex. A vertex without a
        /// valid weight follows <paramref name="fallback"/> (the renderer's transform).
        /// </summary>
        public static void Skin(Vector3[] positions, Vector3[] offset, Matrix4x4[] boneMatrices, Matrix4x4 fallback,
            int[] bones, float[] weights, byte[] counts, int slots, Vector3[] world)
        {
            int count = Mathf.Min(positions.Length, world.Length);
            if (count == 0) return;
            fixed (Vector3* p = positions)
            fixed (Vector3* o = offset)
            fixed (Vector3* w = world)
            fixed (Matrix4x4* m = boneMatrices)
            fixed (int* b = bones)
            fixed (float* bw = weights)
            fixed (byte* c = counts)
            {
                new SkinJob
                {
                    Positions = (float3*)p, Offset = offset != null && offset.Length >= count ? (float3*)o : null, World = (float3*)w,
                    Bones = (float4x4*)m, BoneCount = boneMatrices.Length, Fallback = fallback,
                    BoneOf = b, WeightOf = bw, Counts = c, Slots = slots,
                }.Schedule(count, Batch).Complete();
            }
        }

        /// <summary>world[v] = <paramref name="matrix"/> applied to positions[v] (+ offset[v]).</summary>
        public static void Transform(Vector3[] positions, Vector3[] offset, Matrix4x4 matrix, Vector3[] world)
        {
            int count = Mathf.Min(positions.Length, world.Length);
            if (count == 0) return;
            fixed (Vector3* p = positions)
            fixed (Vector3* o = offset)
            fixed (Vector3* w = world)
            {
                new TransformJob
                {
                    Positions = (float3*)p, Offset = offset != null && offset.Length >= count ? (float3*)o : null, World = (float3*)w, Matrix = matrix,
                }.Schedule(count, Batch).Complete();
            }
        }

        /// <summary>The smallest box around <paramref name="points"/>; false (and zeros) for none.</summary>
        public static bool Bounds(Vector3[] points, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            if (points == null || points.Length == 0) return false;
            var result = stackalloc float3[2];
            fixed (Vector3* p = points)
            {
                new BoundsJob { Points = (float3*)p, Count = points.Length, Result = result }.Run();
            }
            min = result[0];
            max = result[1];
            return true;
        }

        [BurstCompile]
        private struct SkinJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public float3* Positions;
            [NativeDisableUnsafePtrRestriction] public float3* Offset;
            [NativeDisableUnsafePtrRestriction] public float3* World;
            [NativeDisableUnsafePtrRestriction] public float4x4* Bones;
            [NativeDisableUnsafePtrRestriction] public int* BoneOf;
            [NativeDisableUnsafePtrRestriction] public float* WeightOf;
            [NativeDisableUnsafePtrRestriction] public byte* Counts;
            public float4x4 Fallback;
            public int BoneCount, Slots;

            public void Execute(int v)
            {
                var p = new float4(Positions[v], 1f);
                if (Offset != null) p.xyz += Offset[v];
                float3 sum = float3.zero;
                float total = 0f;
                int at = v * Slots, n = Counts[v];
                for (int k = 0; k < n; k++)
                {
                    int bone = BoneOf[at + k];
                    float w = WeightOf[at + k];
                    if (bone < 0 || bone >= BoneCount || w <= 0f) continue;
                    sum += w * math.mul(Bones[bone], p).xyz;
                    total += w;
                }
                if (total <= 1e-6f) World[v] = math.mul(Fallback, p).xyz;
                else if (math.abs(total - 1f) > 1e-4f) World[v] = sum / total;
                else World[v] = sum;
            }
        }

        [BurstCompile]
        private struct TransformJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public float3* Positions;
            [NativeDisableUnsafePtrRestriction] public float3* Offset;
            [NativeDisableUnsafePtrRestriction] public float3* World;
            public float4x4 Matrix;

            public void Execute(int v)
            {
                var p = Positions[v];
                if (Offset != null) p += Offset[v];
                World[v] = math.mul(Matrix, new float4(p, 1f)).xyz;
            }
        }

        [BurstCompile]
        private struct BoundsJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public float3* Points;
            [NativeDisableUnsafePtrRestriction] public float3* Result;
            public int Count;

            public void Execute()
            {
                float3 min = Points[0], max = Points[0];
                for (int v = 1; v < Count; v++)
                {
                    min = math.min(min, Points[v]);
                    max = math.max(max, Points[v]);
                }
                Result[0] = min;
                Result[1] = max;
            }
        }
    }
}
