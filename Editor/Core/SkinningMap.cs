using System;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Where each vertex of a renderer is in the world: the skinning of a <see cref="SkinnedMeshRenderer"/> (the bones'
    /// matrices through the bind poses, blended by the vertex's weights) or the transform of a static mesh. Brushes aim
    /// at the world positions and push mesh-space positions back through the inverse, so a posed avatar is sculpted
    /// where it is seen.
    /// The map keeps one matrix per bone, not one per vertex: a vertex's blended matrix is made when something asks for
    /// that vertex (the few a brush touches), and the whole world picture comes from <see cref="WorldOf"/>, a job over
    /// every vertex. The weights are the document's own (<see cref="MeshDocument.InfluenceArrays"/>), read where they are.
    /// </summary>
    internal sealed class SkinningMap
    {
        private readonly MeshDocument document;
        /// <summary>Per bone, the matrix that takes mesh space to the world (bone to world times bind pose), as of the last refresh.</summary>
        private Matrix4x4[] boneMatrices;
        private Matrix4x4 transformMatrix;
        private bool skinned;
        private bool weightsStale, stale;

        // The renderer's bones and the mesh's bind poses, as last read: both properties allocate a new array on every read,
        // and the pose is checked several times a second while the pointer is over an edited mesh. Read again when the
        // map is forced (a weight upload, new bind poses: MarkStale).
        private Transform[] cachedBones;
        private Matrix4x4[] cachedBindposes;
        // The renderer's bone list read with a bone missing: read again only when forced (a missing bone stays missing).
        private bool readWithMissing;
        // The bone matrices of a check, swapped with boneMatrices when the pose changed.
        private Matrix4x4[] spare;
        private bool built;

        private SkinningMap(MeshDocument document) { this.document = document; }

        public static SkinningMap For(Renderer renderer, Mesh mesh, MeshDocument document)
        {
            var map = new SkinningMap(document);
            map.Rebuild(renderer, mesh, true);
            return map;
        }

        /// <summary>True when the vertices follow bones (else the renderer's transform alone).</summary>
        public bool Skinned => skinned;

        /// <summary>Recomputes the bone matrices when the pose or the transform moved; true when the world picture must follow.</summary>
        public bool Refresh(Renderer renderer, Mesh mesh) => Rebuild(renderer, mesh, weightsStale || stale);

        /// <summary>The document's bone weights changed: the next refresh says the world picture must follow.</summary>
        public void InvalidateWeights() => weightsStale = true;

        /// <summary>The bind poses changed: the next refresh recomputes the matrices.</summary>
        public void MarkStale() => stale = true;

        private bool Rebuild(Renderer renderer, Mesh mesh, bool force)
        {
            var skinnedRenderer = renderer as SkinnedMeshRenderer;
            // A bone destroyed since the list was read (an undone deletion, a reverted armature): the renderer's list now
            // holds the objects that replaced it.
            if (!force && !readWithMissing && cachedBones != null)
                foreach (var bone in cachedBones) if (bone == null) { force = true; break; }
            if (force || cachedBones == null || skinnedRenderer == null)
            {
                cachedBones = skinnedRenderer != null ? skinnedRenderer.bones : null;
                cachedBindposes = skinnedRenderer != null ? mesh.bindposes : null;
                readWithMissing = cachedBones != null && Array.Exists(cachedBones, b => b == null);
            }
            var bones = cachedBones;
            var bindposes = cachedBindposes;
            bool useSkin = bones != null && bindposes != null && bones.Length > 0 && bindposes.Length == bones.Length
                && document.HasSkin && mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            var world = renderer.transform.localToWorldMatrix;
            if (!useSkin)
            {
                if (!force && built && !skinned && transformMatrix == world) return false;
                skinned = false;
                stale = weightsStale = false;
                transformMatrix = world;
                built = true;
                return true;
            }

            if (spare == null || spare.Length != bones.Length) spare = new Matrix4x4[bones.Length];
            var current = spare;
            for (int i = 0; i < bones.Length; i++)
                current[i] = bones[i] != null ? bones[i].localToWorldMatrix * bindposes[i] : world;
            if (!force && built && skinned && boneMatrices != null && boneMatrices.Length == current.Length && transformMatrix == world)
            {
                bool same = true;
                for (int i = 0; i < current.Length && same; i++) same = current[i] == boneMatrices[i];
                if (same) return false;
            }
            skinned = true;
            spare = boneMatrices;
            boneMatrices = current;
            transformMatrix = world;
            stale = weightsStale = false;
            built = true;
            return true;
        }

        /// <summary>
        /// The matrix that takes vertex <paramref name="v"/> from mesh space to the world: its bones' blended by its
        /// effective weights (<see cref="InfluenceKernels"/>: what Unity skins it with once uploaded).
        /// </summary>
        public Matrix4x4 BlendedMatrix(int v)
        {
            if (!skinned) return transformMatrix;
            document.InfluenceArrays(out var bones, out var weights, out var counts, out int slots);
            var m = Matrix4x4.zero;
            float total = 0f;
            int n = counts[v], at = v * slots;
            if (n == 0)
            {
                // No influence: bone 0 at full weight, as the upload gives it.
                if (boneMatrices.Length == 0) return transformMatrix;
                return boneMatrices[0];
            }
            float sum = 0f;
            for (int k = 0; k < n; k++) sum += weights[at + k];
            for (int k = 0; k < n; k++)
            {
                int bone = bones[at + k];
                float w = InfluenceKernels.Effective(weights[at + k], sum, n);
                if (bone < 0 || bone >= boneMatrices.Length || w <= 0f) continue;
                Accumulate(ref m, boneMatrices[bone], w);
                total += w;
            }
            if (total <= 1e-6f) return transformMatrix;
            if (Math.Abs(total - 1f) > 1e-4f) Scale(ref m, 1f / total);
            return m;
        }

        /// <summary>
        /// What <see cref="BlendedMatrix"/> reads, for a kernel that places many vertices (<see cref="StrokeKernels.Move"/>):
        /// whether the vertices follow bones, the bone matrices, the transform and the document's influence slots.
        /// </summary>
        internal void SkinArrays(out bool isSkinned, out Matrix4x4[] bones, out Matrix4x4 transform, out int[] boneOf, out float[] weightOf, out byte[] counts, out int slots)
        {
            isSkinned = skinned;
            bones = skinned ? boneMatrices : null;
            transform = transformMatrix;
            if (skinned) document.InfluenceArrays(out boneOf, out weightOf, out counts, out slots);
            else { boneOf = null; weightOf = null; counts = null; slots = 0; }
        }

        /// <summary>A mesh-space point of vertex <paramref name="v"/> in the world.</summary>
        public Vector3 PointToWorld(int v, Vector3 p) => BlendedMatrix(v).MultiplyPoint3x4(p);

        /// <summary>A mesh-space direction at vertex <paramref name="v"/> in the world (not normalized).</summary>
        public Vector3 VectorToWorld(int v, Vector3 d) => BlendedMatrix(v).MultiplyVector(d);

        /// <summary>A world-space move of vertex <paramref name="v"/> in mesh space: through the inverse of its blended matrix.</summary>
        public Vector3 ToMeshVector(int v, Vector3 world) => Inverse(BlendedMatrix(v)).MultiplyVector(world);

        /// <summary>
        /// Every vertex in the world: <paramref name="positions"/> (mesh space, plus <paramref name="offset"/> when given)
        /// through the skinning, into <paramref name="world"/>.
        /// </summary>
        public void WorldOf(Vector3[] positions, Vector3[] offset, Vector3[] world)
        {
            if (!skinned) { MeshKernels.Transform(positions, offset, transformMatrix, world); return; }
            document.InfluenceArrays(out var bones, out var weights, out var counts, out int slots);
            MeshKernels.Skin(positions, offset, boneMatrices, transformMatrix, bones, weights, counts, slots, world);
        }

        // Blends of affine matrices are affine: the cheaper affine inverse, the full one when it does not apply.
        private static Matrix4x4 Inverse(Matrix4x4 m)
        {
            var result = Matrix4x4.identity;
            if (m.m30 == 0f && m.m31 == 0f && m.m32 == 0f && Mathf.Abs(m.m33 - 1f) < 1e-5f && Matrix4x4.Inverse3DAffine(m, ref result)) return result;
            return m.inverse;
        }

        private static void Accumulate(ref Matrix4x4 m, Matrix4x4 b, float w)
        {
            m.m00 += b.m00 * w; m.m01 += b.m01 * w; m.m02 += b.m02 * w; m.m03 += b.m03 * w;
            m.m10 += b.m10 * w; m.m11 += b.m11 * w; m.m12 += b.m12 * w; m.m13 += b.m13 * w;
            m.m20 += b.m20 * w; m.m21 += b.m21 * w; m.m22 += b.m22 * w; m.m23 += b.m23 * w;
            m.m30 += b.m30 * w; m.m31 += b.m31 * w; m.m32 += b.m32 * w; m.m33 += b.m33 * w;
        }

        private static void Scale(ref Matrix4x4 m, float s)
        {
            m.m00 *= s; m.m01 *= s; m.m02 *= s; m.m03 *= s;
            m.m10 *= s; m.m11 *= s; m.m12 *= s; m.m13 *= s;
            m.m20 *= s; m.m21 *= s; m.m22 *= s; m.m23 *= s;
            m.m30 *= s; m.m31 *= s; m.m32 *= s; m.m33 *= s;
        }
    }
}
