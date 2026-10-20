using System;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Where each vertex of a renderer is in the world: the skinning of a <see cref="SkinnedMeshRenderer"/> (the bones'
    /// matrices through the bind poses, blended by the vertex's weights) or the transform of a static mesh. Brushes aim
    /// at the world positions and push mesh-space positions back through the inverse, so a posed avatar is sculpted
    /// where it is seen.
    /// </summary>
    internal sealed class SkinningMap
    {
        public Matrix4x4[] ToWorld;
        /// <summary>The inverse of <see cref="ToWorld"/> per vertex, computed when first read after a change (only sculpting needs it).</summary>
        public Matrix4x4[] ToMesh
        {
            get
            {
                if (toMeshStale && ToWorld != null)
                {
                    if (toMesh == null || toMesh.Length != ToWorld.Length) toMesh = new Matrix4x4[ToWorld.Length];
                    for (int v = 0; v < ToWorld.Length; v++) toMesh[v] = Inverse(ToWorld[v]);
                    toMeshStale = false;
                }
                return toMesh;
            }
        }
        private Matrix4x4[] toMesh;
        private bool toMeshStale = true;
        /// <summary>Per bone, the matrix that takes mesh space to the world (bone to world times bind pose), as of the last refresh.</summary>
        public Matrix4x4[] Combined => boneMatrices;
        /// <summary>The bone matrices the map was built from, to tell when the pose changed.</summary>
        private Matrix4x4[] boneMatrices;
        private Matrix4x4 transformMatrix;
        // Copies of the mesh's influences. The arrays Unity returns are views into the mesh that die as soon as anything
        // touches it (the weight overlay's copy of the mesh, an upload): kept across refreshes they threw on the next pose.
        private BoneWeight1[] weights;
        private byte[] bonesPerVertex;
        private bool skinned;

        public static SkinningMap For(Renderer renderer, Mesh mesh)
        {
            var map = new SkinningMap();
            map.Rebuild(renderer, mesh, true);
            return map;
        }

        /// <summary>Recomputes the matrices when the pose or the transform moved; true when something changed.</summary>
        public bool Refresh(Renderer renderer, Mesh mesh) => Rebuild(renderer, mesh, weightsStale || stale);

        private bool weightsStale, stale;

        /// <summary>The mesh's bone weights changed: the next refresh reads them again.</summary>
        public void InvalidateWeights()
        {
            weightsStale = true;
            weights = null;
            bonesPerVertex = null;
        }

        /// <summary>The bind poses changed: the next refresh recomputes the matrices (the weights stay).</summary>
        public void MarkStale() => stale = true;

        private bool Rebuild(Renderer renderer, Mesh mesh, bool force)
        {
            int count = mesh.vertexCount;
            var skinnedRenderer = renderer as SkinnedMeshRenderer;
            var bones = skinnedRenderer != null ? skinnedRenderer.bones : null;
            var bindposes = skinnedRenderer != null ? mesh.bindposes : null;
            bool useSkin = bones != null && bindposes != null && bones.Length > 0 && bindposes.Length == bones.Length && mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            var world = renderer.transform.localToWorldMatrix;
            if (!useSkin)
            {
                if (!force && !skinned && ToWorld != null && transformMatrix == world) return false;
                skinned = false;
                stale = false;
                weightsStale = false;
                transformMatrix = world;
                if (ToWorld == null || ToWorld.Length != count) ToWorld = new Matrix4x4[count];
                for (int v = 0; v < count; v++) ToWorld[v] = world;
                toMeshStale = true;
                return true;
            }

            var current = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                current[i] = bones[i] != null ? bones[i].localToWorldMatrix * bindposes[i] : world;
            if (!force && skinned && boneMatrices != null && boneMatrices.Length == current.Length)
            {
                bool same = true;
                for (int i = 0; i < current.Length && same; i++) same = current[i] == boneMatrices[i];
                if (same) return false;
            }
            skinned = true;
            boneMatrices = current;
            weightsStale = false;
            stale = false;
            if (weights == null || weights.Length == 0)
            {
                weights = mesh.GetAllBoneWeights().ToArray();
                bonesPerVertex = mesh.GetBonesPerVertex().ToArray();
            }
            if (ToWorld == null || ToWorld.Length != count) ToWorld = new Matrix4x4[count];
            int cursor = 0;
            for (int v = 0; v < count; v++)
            {
                int n = bonesPerVertex.Length > v ? bonesPerVertex[v] : 0;
                Matrix4x4 m = Matrix4x4.zero;
                float total = 0f;
                for (int k = 0; k < n; k++)
                {
                    var bw = weights[cursor + k];
                    if (bw.boneIndex < 0 || bw.boneIndex >= current.Length || bw.weight <= 0f) continue;
                    Accumulate(ref m, current[bw.boneIndex], bw.weight);
                    total += bw.weight;
                }
                cursor += n;
                if (total <= 1e-6f) m = world; else if (Math.Abs(total - 1f) > 1e-4f) Scale(ref m, 1f / total);
                ToWorld[v] = m;
            }
            toMeshStale = true;
            return true;
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
            for (int i = 0; i < 16; i++) m[i] *= s;
        }
    }
}
