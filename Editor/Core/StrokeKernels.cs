using Orbiters.Toolkit.Meshes;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Burst kernels of a stroke step on the flat topology (<see cref="FlatWeld"/>): the gather under the brush, the move
    /// of the welded vertices into the document, the shown arrays and the world picture, and the normals around what
    /// moved. Each one is the managed loop it replaced written again operation for operation (same order of every
    /// addition, divisions where it divided, FloatMode.Strict so nothing is fused or reordered), so a stroke gives the
    /// same vertices and the same values as before; MixerKernelTests compares each with that managed loop.
    /// <para>
    /// Managed arrays are pinned for the length of a job that completes before the call returns, as in
    /// <see cref="MeshKernels"/>: nothing native outlives a call. Main thread only (jobs are scheduled). The first calls
    /// of an editor session may run before Burst compiled them: the same code then runs on Mono.
    /// </para>
    /// </summary>
    internal static unsafe class StrokeKernels
    {
        private const int Batch = 256;

        // ---- Falloff ------------------------------------------------------------------------------------------------

        /// <summary>The brush's falloff curve at a normalized distance (0 at the center, 1 at the edge): the one definition, managed and Burst.</summary>
        public static float Curve(int falloff, float t)
        {
            // Mathf.Clamp01, Mathf.Sqrt and Mathf.Max as the brushes used them (a float square root rounds the same way
            // whether it goes through double or not).
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            switch (falloff)
            {
                case (int)Falloff.Sphere: { float s = 1f - t * t; return math.sqrt(s > 0f ? s : 0f); }
                case (int)Falloff.Linear: return 1f - t;
                case (int)Falloff.Sharp: { float u = 1f - t; return u * u * u; }
                case (int)Falloff.Constant: return t < 1f ? 1f : 0f;
                default: { float u = 1f - t; return u * u * (3f - 2f * u); }
            }
        }

        // ---- Gather -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The welded vertices within <paramref name="radius"/> of <paramref name="center"/> in the world picture, each once
        /// (by its representative), with the falloff and (unless <paramref name="ignoreMask"/>) the mask's hold, into
        /// <paramref name="welded"/> and <paramref name="weights"/> (room for every welded vertex). In the order the managed
        /// gather found them: the BVH's leaves as its sphere query visits them, each leaf's triangles, their corners.
        /// Returns how many.
        /// </summary>
        public static int Gather(MeshBvh bvh, int[] triangles, MeshNeighbors neighbors, Vector3[] world, float[] mask, int[] gathered, int mark,
            Vector3 center, float radius, Falloff falloff, bool ignoreMask, int[] welded, float[] weights)
        {
            int count = 0;
            using (var pinned = bvh.Pin())
            {
                var view = pinned.View;
                if (view.NodeCount == 0) return 0;
                fixed (int* tris = triangles)
                fixed (int* weld = neighbors.Weld)
                fixed (int* rep = neighbors.Representative)
                fixed (Vector3* w = world)
                fixed (float* m = mask)
                fixed (int* g = gathered)
                fixed (int* outWelded = welded)
                fixed (float* outWeights = weights)
                {
                    new GatherJob
                    {
                        View = view, Triangles = tris, Weld = weld, Representative = rep, World = (float3*)w,
                        Mask = ignoreMask ? null : m, Gathered = g, Mark = mark, Center = center, Radius = radius,
                        Falloff = (int)falloff, Welded = outWelded, Weights = outWeights, Count = &count, Capacity = Mathf.Min(welded.Length, weights.Length),
                    }.Run();
                }
            }
            return count;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct GatherJob : IJob
        {
            public BvhView View;
            [NativeDisableUnsafePtrRestriction] public int* Triangles;
            [NativeDisableUnsafePtrRestriction] public int* Weld;
            [NativeDisableUnsafePtrRestriction] public int* Representative;
            [NativeDisableUnsafePtrRestriction] public float3* World;
            [NativeDisableUnsafePtrRestriction] public float* Mask;
            [NativeDisableUnsafePtrRestriction] public int* Gathered;
            [NativeDisableUnsafePtrRestriction] public int* Welded;
            [NativeDisableUnsafePtrRestriction] public float* Weights;
            [NativeDisableUnsafePtrRestriction] public int* Count;
            public float3 Center;
            public float Radius;
            public int Mark, Falloff, Capacity;

            public void Execute()
            {
                float r2 = Radius * Radius;
                float inverse = 1f / Radius;
                int found = 0;
                // MeshBvh.TrianglesNear's walk: the same stack order, so the triangles come in the same order.
                int* stack = stackalloc int[BvhKernels.StackSize];
                int top = 0;
                stack[top++] = 0;
                while (top > 0)
                {
                    var node = View.Nodes[stack[--top]];
                    if (BvhKernels.BoxDistance2(Center, node.Min, node.Max) > r2) continue;
                    if (node.Count > 0)
                    {
                        for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++)
                        {
                            var t = View.Corners[s];
                            if (BvhKernels.BoxDistance2(Center, math.min(t.A, math.min(t.B, t.C)), math.max(t.A, math.max(t.B, t.C))) > r2) continue;
                            int triangle = View.TriangleOfSlot[s];
                            for (int k = triangle * 3, end = k + 3; k < end; k++)
                            {
                                int w = Weld[Triangles[k]];
                                if (Gathered[w] == Mark) continue;
                                Gathered[w] = Mark;
                                int rep = Representative[w];
                                var p = World[rep];
                                float dx = p.x - Center.x, dy = p.y - Center.y, dz = p.z - Center.z;
                                float d2 = dx * dx + dy * dy + dz * dz;
                                if (d2 > r2) continue;
                                float weight = Curve(Falloff, math.sqrt(d2) * inverse);
                                if (Mask != null)
                                {
                                    // MeshDocument.Free: 1 minus the clamped mask.
                                    float held = Mask[rep];
                                    held = held < 0f ? 0f : held > 1f ? 1f : held;
                                    weight *= 1f - held;
                                }
                                if (weight <= 0f || found >= Capacity) continue;
                                Welded[found] = w;
                                Weights[found] = weight;
                                found++;
                            }
                        }
                    }
                    else { stack[top++] = node.LeftOrFirst; stack[top++] = node.LeftOrFirst + 1; }
                }
                *Count = found;
            }
        }

        // ---- Move ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Moves each of <paramref name="count"/> mesh vertices of <paramref name="vertices"/> (each once) by the move of its
        /// welded vertex, <paramref name="moveOf"/>[weld[v]]: on <paramref name="layer"/> (the active layer's deltas, or the
        /// base positions), on the shown positions, and into the world picture through <paramref name="skin"/> (the
        /// shown position plus <paramref name="offset"/>, the other shapes, when given), as
        /// <see cref="SkinningMap.PointToWorld"/> places one vertex.
        /// </summary>
        public static void Move(int[] vertices, int count, int[] weld, Vector3[] moveOf, Vector3[] layer, Vector3[] display, Vector3[] offset, Vector3[] world, SkinningMap skin)
        {
            if (count <= 0) return;
            skin.SkinArrays(out bool skinned, out var bones, out var transform, out var boneOf, out var weightOf, out var counts, out int slots);
            fixed (int* v = vertices)
            fixed (int* wd = weld)
            fixed (Vector3* mv = moveOf)
            fixed (Vector3* l = layer)
            fixed (Vector3* d = display)
            fixed (Vector3* o = offset)
            fixed (Vector3* w = world)
            fixed (Matrix4x4* b = bones)
            fixed (int* bo = boneOf)
            fixed (float* bw = weightOf)
            fixed (byte* c = counts)
            {
                var job = new MoveJob
                {
                    Vertices = v, Weld = wd, MoveOf = (float3*)mv, Layer = (float3*)l, Display = (float3*)d,
                    Offset = offset != null ? (float3*)o : null, World = (float3*)w,
                    Skin = new SkinPointers
                    {
                        Skinned = skinned ? 1 : 0, Bones = (float4x4*)b, BoneCount = bones != null ? bones.Length : 0, Transform = transform,
                        BoneOf = bo, WeightOf = bw, Counts = c, Slots = slots,
                    },
                };
                // Every vertex once and on its own: the vertices split over the cores without changing a result.
                if (count >= 4 * Batch) job.Schedule(count, Batch).Complete();
                else job.Run(count);
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct MoveJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public int* Vertices;
            [NativeDisableUnsafePtrRestriction] public int* Weld;
            [NativeDisableUnsafePtrRestriction] public float3* MoveOf;
            [NativeDisableUnsafePtrRestriction] public float3* Layer;
            [NativeDisableUnsafePtrRestriction] public float3* Display;
            [NativeDisableUnsafePtrRestriction] public float3* Offset;
            [NativeDisableUnsafePtrRestriction] public float3* World;
            public SkinPointers Skin;

            public void Execute(int i)
            {
                int v = Vertices[i];
                var d = MoveOf[Weld[v]];
                Layer[v] = Layer[v] + d;
                var p = Display[v] + d;
                Display[v] = p;
                if (Offset != null) p = p + Offset[v];
                World[v] = Skin.PointToWorld(v, p);
            }
        }

        /// <summary>A <see cref="SkinningMap"/>'s matrices and the document's influence slots, pinned.</summary>
        internal struct SkinPointers
        {
            public int Skinned;
            [NativeDisableUnsafePtrRestriction] public float4x4* Bones;
            public int BoneCount;
            public float4x4 Transform;
            [NativeDisableUnsafePtrRestriction] public int* BoneOf;
            [NativeDisableUnsafePtrRestriction] public float* WeightOf;
            [NativeDisableUnsafePtrRestriction] public byte* Counts;
            public int Slots;

            /// <summary><see cref="SkinningMap.BlendedMatrix"/> then <c>Matrix4x4.MultiplyPoint3x4</c>, operation for operation.</summary>
            public float3 PointToWorld(int v, float3 p)
            {
                var m = Blended(v);
                return new float3(
                    m.c0.x * p.x + m.c1.x * p.y + m.c2.x * p.z + m.c3.x,
                    m.c0.y * p.x + m.c1.y * p.y + m.c2.y * p.z + m.c3.y,
                    m.c0.z * p.x + m.c1.z * p.y + m.c2.z * p.z + m.c3.z);
            }

            private float4x4 Blended(int v)
            {
                if (Skinned == 0) return Transform;
                int n = Counts[v], at = v * Slots;
                if (n == 0) return BoneCount == 0 ? Transform : Bones[0];
                float sum = 0f;
                for (int k = 0; k < n; k++) sum += WeightOf[at + k];
                var m = float4x4.zero;
                float total = 0f;
                for (int k = 0; k < n; k++)
                {
                    int bone = BoneOf[at + k];
                    float w = InfluenceKernels.Effective(WeightOf[at + k], sum, n);
                    if (bone < 0 || bone >= BoneCount || w <= 0f) continue;
                    var b = Bones[bone];
                    m.c0 = m.c0 + b.c0 * w; m.c1 = m.c1 + b.c1 * w; m.c2 = m.c2 + b.c2 * w; m.c3 = m.c3 + b.c3 * w;
                    total += w;
                }
                if (total <= 1e-6f) return Transform;
                if (math.abs(total - 1f) > 1e-4f)
                {
                    float s = 1f / total;
                    m.c0 = m.c0 * s; m.c1 = m.c1 * s; m.c2 = m.c2 * s; m.c3 = m.c3 * s;
                }
                return m;
            }
        }

        // ---- Normals ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The normals of the normal-welded vertices <paramref name="ring"/>[0..<paramref name="count"/>) (each once; null:
        /// every welded vertex) for <paramref name="positions"/>: the original normals turned by how much their faces'
        /// normal moved from <paramref name="before"/> (see <see cref="MeshDocument.RefreshAllNormals"/>), into
        /// <paramref name="normals"/>. Each welded vertex writes only its own members: the ring splits over the cores.
        /// </summary>
        public static void Turn(FlatWeld weld, int[] triangles, int[] ring, int count, Vector3[] positions, Vector3[] before, Vector3[] originalNormals, Vector3[] normals)
        {
            if (count <= 0) return;
            var n = weld.Neighbors;
            fixed (int* wd = n.Weld)
            fixed (int* ms = weld.MemberStart)
            fixed (int* m = &weld.Members())
            fixed (int* ts = weld.TriangleStart)
            fixed (int* t = &weld.Triangles())
            fixed (int* tris = triangles)
            fixed (int* r = ring)
            fixed (Vector3* p = positions)
            fixed (Vector3* b = before)
            fixed (Vector3* on = originalNormals)
            fixed (Vector3* outNormals = normals)
            {
                var job = new TurnJob
                {
                    Weld = FlatWeld.Pin(wd, ms, m, null, null, ts, t, n.WeldedCount), MeshTriangles = tris, Ring = ring != null ? r : null,
                    Positions = (float3*)p, Before = (float3*)b, OriginalNormals = (float3*)on, Normals = (float3*)outNormals,
                };
                if (count >= 4 * Batch) job.Schedule(count, Batch).Complete();
                else job.Run(count);
            }
        }

        /// <summary>The faces' normal (area-weighted, normalized; zero for faces of no area) of every welded vertex of <paramref name="weld"/> for <paramref name="positions"/>, into <paramref name="result"/>.</summary>
        public static void FaceNormals(FlatWeld weld, int[] triangles, Vector3[] positions, Vector3[] result)
        {
            int count = weld.WeldedCount;
            if (count == 0) return;
            fixed (int* ts = weld.TriangleStart)
            fixed (int* t = &weld.Triangles())
            fixed (int* tris = triangles)
            fixed (Vector3* p = positions)
            fixed (Vector3* outNormals = result)
            {
                new FaceNormalJob
                {
                    Weld = FlatWeld.Pin(null, null, null, null, null, ts, t, count), MeshTriangles = tris, Positions = (float3*)p, Result = (float3*)outNormals,
                }.Schedule(count, Batch * 4).Complete();
            }
        }

        // MeshDocument.FaceNormal: the cross products summed in the triangles' order, divided by the length.
        private static float3 FaceNormal(in WeldPointers weld, int* meshTriangles, float3* positions, int w)
        {
            float x = 0f, y = 0f, z = 0f;
            for (int k = weld.TriangleStart[w], end = weld.TriangleStart[w + 1]; k < end; k++)
            {
                int i = weld.Triangles[k] * 3;
                float3 a = positions[meshTriangles[i]], b = positions[meshTriangles[i + 1]], c = positions[meshTriangles[i + 2]];
                float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
                float vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                x += uy * vz - uz * vy;
                y += uz * vx - ux * vz;
                z += ux * vy - uy * vx;
            }
            float length = math.sqrt(x * x + y * y + z * z);
            return length > 0f ? new float3(x / length, y / length, z / length) : float3.zero;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct FaceNormalJob : IJobParallelFor
        {
            public WeldPointers Weld;
            [NativeDisableUnsafePtrRestriction] public int* MeshTriangles;
            [NativeDisableUnsafePtrRestriction] public float3* Positions;
            [NativeDisableUnsafePtrRestriction] public float3* Result;

            public void Execute(int w) => Result[w] = FaceNormal(Weld, MeshTriangles, Positions, w);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct TurnJob : IJobParallelFor
        {
            public WeldPointers Weld;
            [NativeDisableUnsafePtrRestriction] public int* MeshTriangles;
            [NativeDisableUnsafePtrRestriction] public int* Ring;
            [NativeDisableUnsafePtrRestriction] public float3* Positions;
            [NativeDisableUnsafePtrRestriction] public float3* Before;
            [NativeDisableUnsafePtrRestriction] public float3* OriginalNormals;
            [NativeDisableUnsafePtrRestriction] public float3* Normals;

            // MeshDocument.Turn, operation for operation.
            public void Execute(int i)
            {
                int w = Ring != null ? Ring[i] : i;
                var now = FaceNormal(Weld, MeshTriangles, Positions, w);
                var was = Before[w];
                bool same = now.x == was.x && now.y == was.y && now.z == was.z;
                for (int k = Weld.MemberStart[w], end = Weld.MemberStart[w + 1]; k < end; k++)
                {
                    int m = Weld.Members[k];
                    if (same) { Normals[m] = OriginalNormals[m]; continue; }
                    var turn = now - was;
                    var n = OriginalNormals[m] + turn;
                    float length = math.sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                    Normals[m] = length > 1e-6f ? new float3(n.x / length, n.y / length, n.z / length) : now;
                }
            }
        }
    }
}
