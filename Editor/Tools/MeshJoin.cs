using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's Join for Unity renderers: several skinned or static meshes become one skinned mesh in the first
    /// one's space, bones united by transform (a static mesh rides its own transform as a bone), blendshapes united by
    /// name, material slots merged when they share a material. The result is saved as a native mesh asset on a new
    /// object next to the first source; the sources are hidden, all in one Undo step.
    /// </summary>
    internal static class MeshJoin
    {
        public sealed class Options
        {
            public bool MergeSameMaterials = true;
            public bool HideSources = true;
            public string Name;
        }

        public sealed class Result
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public int Vertices, Bones, Shapes, Slots;
        }

        private sealed class Part
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public Matrix4x4 X;              // source mesh space to target mesh space
            public int VertexBase;
            public int[] BoneMap;            // source bone index -> target bone index
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;
        }

        public static Result Join(IReadOnlyList<Renderer> sources, Options options)
        {
            options = options ?? new Options();
            var parts = new List<Part>();
            foreach (var r in sources)
            {
                var mesh = MeshOf(r);
                if (mesh == null || mesh.vertexCount == 0) continue;
                parts.Add(new Part { Renderer = r, Mesh = mesh });
            }
            if (parts.Count < 2) throw new InvalidOperationException("Pick at least two meshes to join.");
            var first = parts[0];
            var firstSkinned = first.Renderer as SkinnedMeshRenderer;
            // The target bones start as the first source's; its mesh space is the target space.
            var bones = new List<Transform>();
            var bindposes = new List<Matrix4x4>();
            var boneIndex = new Dictionary<Transform, List<int>>();
            void AddBone(Transform t, Matrix4x4 bind, out int index)
            {
                if (t != null && boneIndex.TryGetValue(t, out var existing))
                {
                    foreach (int i in existing) if (Same(bindposes[i], bind)) { index = i; return; }
                }
                index = bones.Count;
                bones.Add(t);
                bindposes.Add(bind);
                if (t != null) { if (!boneIndex.TryGetValue(t, out var list)) boneIndex[t] = list = new List<int>(); list.Add(index); }
            }
            Matrix4x4 W0 = MeshToWorld(first);
            foreach (var part in parts)
            {
                part.Bones = (part.Renderer as SkinnedMeshRenderer)?.bones ?? new Transform[0];
                part.Bindposes = part.Mesh.bindposes ?? new Matrix4x4[0];
                bool skinned = part.Bones.Length > 0 && part.Bindposes.Length == part.Bones.Length && part.Mesh.HasVertexAttribute(VertexAttribute.BlendWeight);
                if (part == first) part.X = Matrix4x4.identity;
                else
                {
                    // Through a bone both share when there is one (exact whatever the pose), else through the world as posed now.
                    part.X = Matrix4x4.zero;
                    if (skinned && firstSkinned != null)
                    {
                        for (int j = 0; j < part.Bones.Length && part.X == Matrix4x4.zero; j++)
                        {
                            int i0 = Array.IndexOf(firstSkinned.bones, part.Bones[j]);
                            if (i0 >= 0 && i0 < first.Bindposes.Length && part.Bones[j] != null) part.X = first.Bindposes[i0].inverse * part.Bindposes[j];
                        }
                    }
                    if (part.X == Matrix4x4.zero) part.X = W0.inverse * MeshToWorld(part);
                }
                part.BoneMap = new int[Math.Max(1, part.Bones.Length)];
                if (skinned)
                {
                    var xInverse = part.X.inverse;
                    for (int j = 0; j < part.Bones.Length; j++) AddBone(part.Bones[j], part.Bindposes[j] * xInverse, out part.BoneMap[j]);
                }
                else
                {
                    // A static mesh rides its own transform: full weight on one bone.
                    AddBone(part.Renderer.transform, part.Renderer.transform.worldToLocalMatrix * W0, out part.BoneMap[0]);
                }
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>(); var uv2 = new List<Vector2>(); var uv3 = new List<Vector2>();
            var colors = new List<Color32>();
            var perVertex = new List<byte>();
            var weights = new List<BoneWeight1>();
            bool anyColors = parts.Any(p => p.Mesh.colors32 != null && p.Mesh.colors32.Length == p.Mesh.vertexCount);
            bool anyUv1 = parts.Any(p => p.Mesh.uv2 != null && p.Mesh.uv2.Length == p.Mesh.vertexCount);
            bool anyUv2 = parts.Any(p => p.Mesh.uv3 != null && p.Mesh.uv3.Length == p.Mesh.vertexCount);
            bool anyUv3 = parts.Any(p => p.Mesh.uv4 != null && p.Mesh.uv4.Length == p.Mesh.vertexCount);
            var slots = new List<(Material material, List<int> indices)>();
            var shapeNames = new List<string>();
            foreach (var part in parts) for (int s = 0; s < part.Mesh.blendShapeCount; s++) { string n = part.Mesh.GetBlendShapeName(s); if (!shapeNames.Contains(n)) shapeNames.Add(n); }
            var shapeDeltas = shapeNames.ToDictionary(n => n, n => new List<Vector3>());
            var shapeNormals = shapeNames.ToDictionary(n => n, n => new List<Vector3>());
            foreach (var part in parts)
            {
                var mesh = part.Mesh;
                int count = mesh.vertexCount;
                part.VertexBase = positions.Count;
                var linear = part.X;
                linear.SetColumn(3, new Vector4(0, 0, 0, 1));
                var normalMatrix = linear.inverse.transpose;
                var v = mesh.vertices; var n = mesh.normals; var t = mesh.tangents;
                for (int i = 0; i < count; i++) positions.Add(part.X.MultiplyPoint3x4(v[i]));
                for (int i = 0; i < count; i++) normals.Add(n != null && n.Length == count ? normalMatrix.MultiplyVector(n[i]).normalized : Vector3.up);
                for (int i = 0; i < count; i++)
                {
                    if (t != null && t.Length == count) { var d = linear.MultiplyVector(new Vector3(t[i].x, t[i].y, t[i].z)).normalized; tangents.Add(new Vector4(d.x, d.y, d.z, t[i].w)); }
                    else tangents.Add(new Vector4(1, 0, 0, 1));
                }
                var u0 = mesh.uv; for (int i = 0; i < count; i++) uv0.Add(u0 != null && u0.Length == count ? u0[i] : Vector2.zero);
                if (anyUv1) { var u = mesh.uv2; for (int i = 0; i < count; i++) uv1.Add(u != null && u.Length == count ? u[i] : Vector2.zero); }
                if (anyUv2) { var u = mesh.uv3; for (int i = 0; i < count; i++) uv2.Add(u != null && u.Length == count ? u[i] : Vector2.zero); }
                if (anyUv3) { var u = mesh.uv4; for (int i = 0; i < count; i++) uv3.Add(u != null && u.Length == count ? u[i] : Vector2.zero); }
                if (anyColors) { var c = mesh.colors32; for (int i = 0; i < count; i++) colors.Add(c != null && c.Length == count ? c[i] : new Color32(255, 255, 255, 255)); }
                bool skinned = part.Bones.Length > 0 && mesh.HasVertexAttribute(VertexAttribute.BlendWeight);
                if (skinned)
                {
                    var bpv = mesh.GetBonesPerVertex();
                    var all = mesh.GetAllBoneWeights();
                    int cursor = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int k = i < bpv.Length ? bpv[i] : 0;
                        var list = new List<BoneWeight1>(k);
                        for (int q = 0; q < k; q++) { var bw = all[cursor + q]; if (bw.boneIndex >= 0 && bw.boneIndex < part.BoneMap.Length && bw.weight > 0f) list.Add(new BoneWeight1 { boneIndex = part.BoneMap[bw.boneIndex], weight = bw.weight }); }
                        cursor += k;
                        if (list.Count == 0) list.Add(new BoneWeight1 { boneIndex = part.BoneMap[0], weight = 1f });
                        list.Sort((a, b) => b.weight.CompareTo(a.weight));
                        perVertex.Add((byte)Math.Min(255, list.Count));
                        weights.AddRange(list.Take(255));
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++) { perVertex.Add(1); weights.Add(new BoneWeight1 { boneIndex = part.BoneMap[0], weight = 1f }); }
                }
                // Material slots.
                var materials = part.Renderer.sharedMaterials ?? new Material[0];
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var material = s < materials.Length ? materials[s] : null;
                    int slot = options.MergeSameMaterials && material != null ? slots.FindIndex(x => x.material == material) : -1;
                    if (slot < 0) { slots.Add((material, new List<int>())); slot = slots.Count - 1; }
                    var tris = mesh.GetTriangles(s);
                    var into = slots[slot].indices;
                    for (int i = 0; i < tris.Length; i++) into.Add(tris[i] + part.VertexBase);
                }
                // Blendshapes: the deltas of the shapes this part has, zeros for the others.
                var delta = new Vector3[count]; var deltaN = new Vector3[count];
                foreach (string name in shapeNames)
                {
                    int index = mesh.GetBlendShapeIndex(name);
                    if (index >= 0)
                    {
                        mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, delta, deltaN, null);
                        for (int i = 0; i < count; i++) { shapeDeltas[name].Add(linear.MultiplyVector(delta[i])); shapeNormals[name].Add(normalMatrix.MultiplyVector(deltaN[i])); }
                    }
                    else for (int i = 0; i < count; i++) { shapeDeltas[name].Add(Vector3.zero); shapeNormals[name].Add(Vector3.zero); }
                }
            }

            var joined = new Mesh { name = options.Name ?? (first.Renderer.name + " joined") };
            joined.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            joined.SetVertices(positions);
            joined.SetNormals(normals);
            joined.SetTangents(tangents);
            joined.SetUVs(0, uv0);
            if (anyUv1) joined.SetUVs(1, uv1);
            if (anyUv2) joined.SetUVs(2, uv2);
            if (anyUv3) joined.SetUVs(3, uv3);
            if (anyColors) joined.SetColors(colors);
            joined.subMeshCount = slots.Count;
            for (int s = 0; s < slots.Count; s++) joined.SetTriangles(slots[s].indices, s);
            joined.bindposes = bindposes.ToArray();
            using (var pv = new NativeArray<byte>(perVertex.ToArray(), Allocator.Temp))
            using (var ws = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                joined.SetBoneWeights(pv, ws);
            foreach (string name in shapeNames) joined.AddBlendShapeFrame(name, 100f, shapeDeltas[name].ToArray(), shapeNormals[name].ToArray(), null);
            joined.RecalculateBounds();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: Join " + parts.Count + " meshes");
            string owner = first.Renderer.transform.root != null ? first.Renderer.transform.root.name : first.Renderer.name;
            var asset = MeshAssets.Save(joined, MeshAssets.Folder("Mixer", owner), joined.name);
            var go = new GameObject(joined.name);
            Undo.RegisterCreatedObjectUndo(go, "Join meshes");
            go.transform.SetParent(first.Renderer.transform.parent, false);
            go.transform.localPosition = first.Renderer.transform.localPosition;
            go.transform.localRotation = first.Renderer.transform.localRotation;
            go.transform.localScale = first.Renderer.transform.localScale;
            var result = go.AddComponent<SkinnedMeshRenderer>();
            result.sharedMesh = asset;
            result.bones = bones.ToArray();
            result.rootBone = firstSkinned != null && firstSkinned.rootBone != null ? firstSkinned.rootBone : bones.FirstOrDefault(b => b != null);
            result.sharedMaterials = slots.Select(s => s.material).ToArray();
            result.localBounds = asset.bounds;
            if (firstSkinned != null)
                for (int s = 0; s < asset.blendShapeCount; s++) { int i = firstSkinned.sharedMesh.GetBlendShapeIndex(asset.GetBlendShapeName(s)); if (i >= 0) result.SetBlendShapeWeight(s, firstSkinned.GetBlendShapeWeight(i)); }
            if (options.HideSources)
                foreach (var part in parts) { Undo.RecordObject(part.Renderer.gameObject, "Hide joined source"); part.Renderer.gameObject.SetActive(false); }
            Selection.activeGameObject = go;
            return new Result { Renderer = result, Mesh = asset, Vertices = positions.Count, Bones = bones.Count, Shapes = shapeNames.Count, Slots = slots.Count };
        }

        private static Mesh MeshOf(Renderer r)
        {
            var shown = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            // A mesh being edited in Mixer joins as it is shown (its working copy holds the edits).
            return shown;
        }

        // Mesh space to world: for a skinned mesh, through its first bone and bind pose; for a static one, its transform.
        private static Matrix4x4 MeshToWorld(Part part)
        {
            if (part.Renderer is SkinnedMeshRenderer s && s.bones != null && s.bones.Length > 0 && part.Mesh.bindposes != null && part.Mesh.bindposes.Length == s.bones.Length)
                for (int i = 0; i < s.bones.Length; i++) if (s.bones[i] != null) return s.bones[i].localToWorldMatrix * part.Mesh.bindposes[i];
            return part.Renderer.transform.localToWorldMatrix;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }
    }
}
