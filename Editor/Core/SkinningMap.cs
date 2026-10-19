using System;
using Unity.Collections;
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
        public Matrix4x4[] ToMesh;
        /// <summary>The bone matrices the map was built from, to tell when the pose changed.</summary>
        private Matrix4x4[] boneMatrices;
        private Matrix4x4 transformMatrix;
        private NativeArray<BoneWeight1> weights;
        private NativeArray<byte> bonesPerVertex;
        private bool skinned;

        public static SkinningMap For(Renderer renderer, Mesh mesh)
        {
            var map = new SkinningMap();
            map.Rebuild(renderer, mesh, true);
            return map;
        }

        /// <summary>Recomputes the matrices when the pose or the transform moved; true when something changed.</summary>
        public bool Refresh(Renderer renderer, Mesh mesh) => Rebuild(renderer, mesh, false);

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
                transformMatrix = world;
                if (ToWorld == null || ToWorld.Length != count) { ToWorld = new Matrix4x4[count]; ToMesh = new Matrix4x4[count]; }
                var inverse = world.inverse;
                for (int v = 0; v < count; v++) { ToWorld[v] = world; ToMesh[v] = inverse; }
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
            if (!weights.IsCreated || weights.Length == 0)
            {
                weights = mesh.GetAllBoneWeights();
                bonesPerVertex = mesh.GetBonesPerVertex();
            }
            if (ToWorld == null || ToWorld.Length != count) { ToWorld = new Matrix4x4[count]; ToMesh = new Matrix4x4[count]; }
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
                ToMesh[v] = m.inverse;
            }
            return true;
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
