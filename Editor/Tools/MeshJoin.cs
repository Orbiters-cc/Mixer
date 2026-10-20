using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's Join for Unity renderers: several skinned or static meshes become one skinned mesh in the first
    /// one's space, bones united by transform (a static mesh rides its own transform as a bone), blendshapes united by
    /// name with their in-between frames, material slots merged when they share a material, every UV channel kept. A
    /// mesh being edited in Mixer joins as a commit would make it. The result is saved as a native mesh asset on a new
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
            /// <summary>A mesh built for the join (a Mixer session's edits), destroyed after it.</summary>
            public bool Temporary;
            public Matrix4x4 X;              // source mesh space to target mesh space
            public int VertexBase;
            public int[] BoneMap;            // source bone index -> target bone index
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;
            /// <summary>Skinned to its bones (bones, matching bind poses, weights); else it rides its own transform.</summary>
            public bool Skinned;
        }

        private const int UvChannels = 8;

        public static Result Join(IReadOnlyList<Renderer> sources, Options options)
        {
            options = options ?? new Options();
            var parts = new List<Part>();
            try
            {
                foreach (var r in sources)
                {
                    var part = PartOf(r);
                    if (part != null) parts.Add(part);
                }
                if (parts.Count < 2) throw new InvalidOperationException("Pick at least two meshes to join.");
                return Join(parts, options);
            }
            finally
            {
                foreach (var part in parts) if (part.Temporary && part.Mesh != null) Object.DestroyImmediate(part.Mesh);
            }
        }

        // A mesh being edited in Mixer joins as a commit would make it (base, shapes, weights, bind poses), not as its
        // working copy shows it (the active shape baked into the vertices).
        private static Part PartOf(Renderer r)
        {
            if (r == null) return null;
            var target = MixerSession.TargetOf(r);
            if (target != null && target.Alive)
            {
                var built = MeshBuilder.Build(target);
                built.hideFlags = HideFlags.HideAndDontSave;
                return new Part { Renderer = r, Mesh = built, Temporary = true };
            }
            var mesh = MixerModes.MeshOf(r);
            if (mesh == null || mesh.vertexCount == 0) return null;
            return new Part { Renderer = r, Mesh = mesh };
        }

        private static Result Join(List<Part> parts, Options options)
        {
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
            foreach (var part in parts)
            {
                part.Bones = (part.Renderer as SkinnedMeshRenderer)?.bones ?? new Transform[0];
                part.Bindposes = part.Mesh.bindposes ?? new Matrix4x4[0];
                // One answer for both the bones and the weights: a part whose bind poses do not match its bones rides
                // its own transform whole, instead of sending every vertex to the first bone.
                part.Skinned = part.Bones.Length > 0 && part.Bindposes.Length == part.Bones.Length && part.Mesh.HasVertexAttribute(VertexAttribute.BlendWeight);
            }
            Matrix4x4 W0 = MeshToWorld(first);
            foreach (var part in parts)
            {
                if (part == first) part.X = Matrix4x4.identity;
                else
                {
                    // Through a bone both share when there is one (exact whatever the pose), else through the world as posed now.
                    part.X = Matrix4x4.zero;
                    if (part.Skinned && first.Skinned && firstSkinned != null)
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
                if (part.Skinned)
                {
                    var xInverse = part.X.inverse;
                    for (int j = 0; j < part.Bones.Length; j++) AddBone(part.Bones[j], part.Bindposes[j] * xInverse, out part.BoneMap[j]);
                }
                else
                {
                    // A static mesh (or one whose skin does not fit) rides its own transform: full weight on one bone.
                    AddBone(part.Renderer.transform, part.Renderer.transform.worldToLocalMatrix * W0, out part.BoneMap[0]);
                }
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvDimension = new int[UvChannels];
            foreach (var part in parts)
                for (int c = 0; c < UvChannels; c++)
                    if (part.Mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + c)) uvDimension[c] = Math.Max(uvDimension[c], part.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + c));
            var uvs = new List<Vector4>[UvChannels];
            for (int c = 0; c < UvChannels; c++) if (uvDimension[c] > 0) uvs[c] = new List<Vector4>();
            var colors = new List<Color32>();
            var perVertex = new List<byte>();
            var weights = new List<BoneWeight1>();
            bool anyColors = parts.Any(p => p.Mesh.HasVertexAttribute(VertexAttribute.Color));
            var slots = new List<(Material material, List<int> indices)>();
            var shapeNames = new List<string>();
            foreach (var part in parts) for (int s = 0; s < part.Mesh.blendShapeCount; s++) { string n = part.Mesh.GetBlendShapeName(s); if (!shapeNames.Contains(n)) shapeNames.Add(n); }
            // Every frame weight any part has for a shape: the joined shape gets them all.
            var frameWeights = shapeNames.ToDictionary(n => n, n => new SortedSet<float>());
            foreach (var part in parts)
                for (int s = 0; s < part.Mesh.blendShapeCount; s++)
                    for (int f = 0; f < part.Mesh.GetBlendShapeFrameCount(s); f++) frameWeights[part.Mesh.GetBlendShapeName(s)].Add(part.Mesh.GetBlendShapeFrameWeight(s, f));
            var shapeFrames = shapeNames.ToDictionary(n => n, n => frameWeights[n].Select(_ => (deltas: new List<Vector3>(), normals: new List<Vector3>(), tangents: new List<Vector3>())).ToArray());
            var uvScratch = new List<Vector4>();
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
                // A mirrored part (negative scale) flips its handedness: the tangent's sign follows.
                float handedness = linear.determinant < 0f ? -1f : 1f;
                for (int i = 0; i < count; i++)
                {
                    if (t != null && t.Length == count) { var d = linear.MultiplyVector(new Vector3(t[i].x, t[i].y, t[i].z)).normalized; tangents.Add(new Vector4(d.x, d.y, d.z, t[i].w * handedness)); }
                    else tangents.Add(new Vector4(1, 0, 0, 1));
                }
                for (int c = 0; c < UvChannels; c++)
                {
                    if (uvs[c] == null) continue;
                    uvScratch.Clear();
                    if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + c)) mesh.GetUVs(c, uvScratch);
                    for (int i = 0; i < count; i++) uvs[c].Add(i < uvScratch.Count ? uvScratch[i] : Vector4.zero);
                }
                if (anyColors) { var col = mesh.colors32; for (int i = 0; i < count; i++) colors.Add(col != null && col.Length == count ? col[i] : new Color32(255, 255, 255, 255)); }
                if (part.Skinned)
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
                // Material slots; a mirrored part's triangles are turned back so they face out.
                var materials = part.Renderer.sharedMaterials ?? new Material[0];
                bool flip = linear.determinant < 0f;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var material = s < materials.Length ? materials[s] : null;
                    int slot = options.MergeSameMaterials && material != null ? slots.FindIndex(x => x.material == material) : -1;
                    if (slot < 0) { slots.Add((material, new List<int>())); slot = slots.Count - 1; }
                    var tris = mesh.GetTriangles(s);
                    var into = slots[slot].indices;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        into.Add(tris[i] + part.VertexBase);
                        into.Add(tris[flip ? i + 2 : i + 1] + part.VertexBase);
                        into.Add(tris[flip ? i + 1 : i + 2] + part.VertexBase);
                    }
                }
                // Blendshapes: every frame of every shape; a part without the shape (or the frame) adds zeros or its
                // own frames interpolated as Unity blends them.
                var delta = new Vector3[count]; var deltaN = new Vector3[count]; var deltaT = new Vector3[count];
                foreach (string name in shapeNames)
                {
                    var targetFrames = shapeFrames[name];
                    var weightsOfShape = frameWeights[name].ToArray();
                    int index = mesh.GetBlendShapeIndex(name);
                    for (int f = 0; f < weightsOfShape.Length; f++)
                    {
                        var frame = targetFrames[f];
                        if (index < 0) { for (int i = 0; i < count; i++) { frame.deltas.Add(Vector3.zero); frame.normals.Add(Vector3.zero); frame.tangents.Add(Vector3.zero); } continue; }
                        FrameAt(mesh, index, weightsOfShape[f], delta, deltaN, deltaT);
                        for (int i = 0; i < count; i++)
                        {
                            frame.deltas.Add(linear.MultiplyVector(delta[i]));
                            frame.normals.Add(normalMatrix.MultiplyVector(deltaN[i]));
                            frame.tangents.Add(linear.MultiplyVector(deltaT[i]));
                        }
                    }
                }
            }

            var joined = new Mesh { name = options.Name ?? (first.Renderer.name + " joined") };
            joined.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            joined.SetVertices(positions);
            joined.SetNormals(normals);
            joined.SetTangents(tangents);
            for (int c = 0; c < UvChannels; c++)
            {
                if (uvs[c] == null) continue;
                if (uvDimension[c] <= 2) joined.SetUVs(c, uvs[c].Select(u => new Vector2(u.x, u.y)).ToList());
                else if (uvDimension[c] == 3) joined.SetUVs(c, uvs[c].Select(u => new Vector3(u.x, u.y, u.z)).ToList());
                else joined.SetUVs(c, uvs[c]);
            }
            if (anyColors) joined.SetColors(colors);
            joined.subMeshCount = slots.Count;
            for (int s = 0; s < slots.Count; s++) joined.SetTriangles(slots[s].indices, s);
            joined.bindposes = bindposes.ToArray();
            using (var pv = new NativeArray<byte>(perVertex.ToArray(), Allocator.Temp))
            using (var ws = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                joined.SetBoneWeights(pv, ws);
            foreach (string name in shapeNames)
            {
                var weightsOfShape = frameWeights[name].ToArray();
                var frames = shapeFrames[name];
                for (int f = 0; f < weightsOfShape.Length; f++)
                    joined.AddBlendShapeFrame(name, weightsOfShape[f], frames[f].deltas.ToArray(), frames[f].normals.ToArray(), frames[f].tangents.ToArray());
            }
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
            // A skinned renderer's bounds are in its root bone's space, from the posed mesh.
            SkinnedMeshBounds.Refresh(result);
            // Each shape's weight as the first part that has it shows it; a part in a session by its document's names (a
            // shape renamed there is not yet on its working mesh).
            var sessionWeights = new Dictionary<Renderer, Dictionary<string, float>>();
            foreach (var part in parts)
                if (MixerSession.TargetOf(part.Renderer) is EditTarget edited && edited.Alive) sessionWeights[part.Renderer] = edited.WeightsByName();
            for (int s = 0; s < asset.blendShapeCount; s++)
            {
                string name = asset.GetBlendShapeName(s);
                foreach (var part in parts)
                {
                    if (!(part.Renderer is SkinnedMeshRenderer source) || source.sharedMesh == null) continue;
                    if (sessionWeights.TryGetValue(source, out var byName))
                    {
                        if (!byName.TryGetValue(name, out float held)) continue;
                        result.SetBlendShapeWeight(s, held);
                        break;
                    }
                    int i = source.sharedMesh.GetBlendShapeIndex(name);
                    if (i < 0) continue;
                    var sessionOf = MixerSession.TargetOf(source);
                    float weight = source.GetBlendShapeWeight(i);
                    if (sessionOf != null && sessionOf.HeldWeights != null && i < sessionOf.HeldWeights.Length && !float.IsNaN(sessionOf.HeldWeights[i])) weight = sessionOf.HeldWeights[i];
                    result.SetBlendShapeWeight(s, weight);
                    break;
                }
            }
            if (options.HideSources)
                foreach (var part in parts) { Undo.RecordObject(part.Renderer.gameObject, "Hide joined source"); part.Renderer.gameObject.SetActive(false); }
            Selection.activeGameObject = go;
            return new Result { Renderer = result, Mesh = asset, Vertices = positions.Count, Bones = bones.Count, Shapes = shapeNames.Count, Slots = slots.Count };
        }

        // A shape's deltas at a weight: the frame at that weight, else between its frames (from zero before the first),
        // the last one past it.
        private static void FrameAt(Mesh mesh, int shape, float weight, Vector3[] deltas, Vector3[] normals, Vector3[] tangents)
        {
            int frames = mesh.GetBlendShapeFrameCount(shape);
            int upper = 0;
            while (upper < frames && mesh.GetBlendShapeFrameWeight(shape, upper) < weight - 1e-4f) upper++;
            if (upper >= frames) { mesh.GetBlendShapeFrameVertices(shape, frames - 1, deltas, normals, tangents); return; }
            float wUpper = mesh.GetBlendShapeFrameWeight(shape, upper);
            mesh.GetBlendShapeFrameVertices(shape, upper, deltas, normals, tangents);
            if (Mathf.Abs(wUpper - weight) <= 1e-4f) return;
            float wLower = 0f;
            var lowerD = new Vector3[deltas.Length]; var lowerN = new Vector3[deltas.Length]; var lowerT = new Vector3[deltas.Length];
            if (upper > 0) { wLower = mesh.GetBlendShapeFrameWeight(shape, upper - 1); mesh.GetBlendShapeFrameVertices(shape, upper - 1, lowerD, lowerN, lowerT); }
            float k = Mathf.Abs(wUpper - wLower) > 1e-6f ? (weight - wLower) / (wUpper - wLower) : 1f;
            for (int i = 0; i < deltas.Length; i++)
            {
                deltas[i] = Vector3.LerpUnclamped(lowerD[i], deltas[i], k);
                normals[i] = Vector3.LerpUnclamped(lowerN[i], normals[i], k);
                tangents[i] = Vector3.LerpUnclamped(lowerT[i], tangents[i], k);
            }
        }

        // Mesh space to world: for a skinned mesh, through its first bone and bind pose; for a static one, its transform.
        private static Matrix4x4 MeshToWorld(Part part)
        {
            if (part.Skinned)
                for (int i = 0; i < part.Bones.Length; i++) if (part.Bones[i] != null) return part.Bones[i].localToWorldMatrix * part.Bindposes[i];
            return part.Renderer.transform.localToWorldMatrix;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }
    }
}
