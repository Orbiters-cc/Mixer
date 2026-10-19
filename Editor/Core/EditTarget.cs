using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// One renderer being edited: its original mesh (untouched), the working mesh shown in its place, the document
    /// the tools edit, and the world-space picture of it brushes aim at (positions through the skinning and the
    /// other blendshapes, indexed by a BVH).
    /// </summary>
    internal sealed class EditTarget
    {
        public readonly Renderer Renderer;
        public readonly MeshFilter Filter;
        public readonly Mesh Original;
        public readonly Mesh Working;
        public readonly MeshDocument Document;
        public readonly string Id;
        public SkinningMap Skin;
        public MeshBvh Bvh;
        /// <summary>World position of every vertex as shown (active layer + the other shapes' weights, skinned).</summary>
        public Vector3[] World;
        private readonly Vector3[] display;
        private Vector3[] shapeOffset;
        private int shapeOffsetHash;
        private float[] savedWeight;
        private MirrorMap mirror;
        private int flushedRevision = -1;
        private int flushes;

        public string Name => Renderer != null ? Renderer.name : "(gone)";
        public bool Alive => Renderer != null && Working != null;
        public SkinnedMeshRenderer Skinned => Renderer as SkinnedMeshRenderer;
        public MirrorMap Mirror => mirror ?? (mirror = MirrorMap.Build(Document.Original.vertices, Document.Neighbors, Document.Original.bounds.size.magnitude * 1e-4f));

        public EditTarget(Renderer renderer, Mesh original, Mesh working)
        {
            Renderer = renderer;
            Filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
            Original = original;
            Working = working;
            Id = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
            Document = new MeshDocument(original);
            display = new Vector3[Document.VertexCount];
            Document.DisplayPositions(display);
            World = new Vector3[Document.VertexCount];
            Skin = SkinningMap.For(renderer, working);
            RebuildWorld();
        }

        /// <summary>The mesh the renderer shows now.</summary>
        public Mesh Shown => Filter != null ? Filter.sharedMesh : Skinned != null ? Skinned.sharedMesh : null;

        public void Show(Mesh mesh)
        {
            if (Filter != null) Filter.sharedMesh = mesh; else if (Skinned != null) Skinned.sharedMesh = mesh;
        }

        /// <summary>Brings the world picture up to date with the pose and shape weights; true when it changed.</summary>
        public bool RefreshWorld()
        {
            bool changed = Skin.Refresh(Renderer, Working);
            changed |= RefreshShapeOffset();
            if (changed) RebuildWorld();
            return changed;
        }

        // The other blendshapes at their renderer weights, in mesh space, added before skinning as Unity does.
        private bool RefreshShapeOffset()
        {
            var skinned = Skinned;
            int hash = 17;
            var mesh = Working;
            int count = mesh != null ? mesh.blendShapeCount : 0;
            if (skinned != null)
            {
                for (int i = 0; i < count; i++)
                {
                    float w = skinned.GetBlendShapeWeight(i);
                    if (w != 0f) hash = unchecked(hash * 31 + i * 7919 + (int)(w * 1000f));
                }
            }
            hash = unchecked(hash * 31 + (Document.Layer != null ? Document.Layer.GetHashCode() : 0));
            if (shapeOffset != null && hash == shapeOffsetHash) return false;
            shapeOffsetHash = hash;
            if (shapeOffset == null) shapeOffset = new Vector3[Document.VertexCount];
            Array.Clear(shapeOffset, 0, shapeOffset.Length);
            if (skinned == null) return true;
            var deltas = new Vector3[Document.VertexCount];
            for (int i = 0; i < count; i++)
            {
                float w = skinned.GetBlendShapeWeight(i) / 100f;
                if (w == 0f) continue;
                string name = mesh.GetBlendShapeName(i);
                if (name == Document.Layer) continue;
                int frame = mesh.GetBlendShapeFrameCount(i) - 1;
                mesh.GetBlendShapeFrameVertices(i, frame, deltas, null, null);
                for (int v = 0; v < deltas.Length; v++) shapeOffset[v] += deltas[v] * w;
            }
            return true;
        }

        private void RebuildWorld()
        {
            Document.DisplayPositions(display);
            var toWorld = Skin.ToWorld;
            for (int v = 0; v < World.Length; v++)
            {
                var p = display[v];
                if (shapeOffset != null) p += shapeOffset[v];
                World[v] = toWorld[v].MultiplyPoint3x4(p);
            }
            if (Bvh == null) Bvh = MeshBvh.Build(World, Document.Triangles); else Bvh.Refit();
        }

        /// <summary>Applies the document's dirty vertices to the world picture (after a brush step or an undo).</summary>
        public void AfterEdit(IReadOnlyList<int> moved, bool fullRefresh = false)
        {
            if (fullRefresh)
            {
                RefreshShapeOffset();
                RebuildWorld();
                return;
            }
            var toWorld = Skin.ToWorld;
            for (int i = 0; i < moved.Count; i++)
            {
                int v = moved[i];
                var p = Document.LayerPosition(v);
                if (shapeOffset != null) p += shapeOffset[v];
                World[v] = toWorld[v].MultiplyPoint3x4(p);
            }
            Bvh.RefitVertices(moved, Document.Neighbors);
        }

        /// <summary>The active layer changed: the shown positions and the other shapes' weights are different now.</summary>
        public void AfterLayerChange()
        {
            ApplyLayerWeight();
            shapeOffsetHash = 0;
            RefreshShapeOffset();
            RebuildWorld();
            Document.MarkEdited(new int[0]);
            for (int v = 0; v < Document.VertexCount; v++) Document.Dirty.Add(v);
            Document.RefreshNormals(new List<int>(), display);
            RecomputeAllNormals();
        }

        // While a shape is edited the renderer shows it at 100 % through the working vertices, so its own weight is held
        // at 0 (else it would show twice); the weight it had comes back when another layer is picked.
        private void ApplyLayerWeight()
        {
            var skinned = Skinned;
            if (skinned == null) return;
            if (savedWeight == null) { savedWeight = new float[Working.blendShapeCount]; for (int i = 0; i < savedWeight.Length; i++) savedWeight[i] = float.NaN; }
            for (int i = 0; i < Working.blendShapeCount; i++)
            {
                string name = Working.GetBlendShapeName(i);
                if (name == Document.Layer)
                {
                    if (float.IsNaN(savedWeight[i])) savedWeight[i] = skinned.GetBlendShapeWeight(i);
                    skinned.SetBlendShapeWeight(i, 0f);
                }
                else if (!float.IsNaN(savedWeight[i]))
                {
                    skinned.SetBlendShapeWeight(i, savedWeight[i]);
                    savedWeight[i] = float.NaN;
                }
            }
        }

        /// <summary>Puts the renderer's blendshape weights back as they were before layers were edited.</summary>
        public void RestoreWeights()
        {
            var skinned = Skinned;
            if (skinned == null || savedWeight == null) return;
            for (int i = 0; i < savedWeight.Length && i < Working.blendShapeCount; i++)
                if (!float.IsNaN(savedWeight[i])) { skinned.SetBlendShapeWeight(i, savedWeight[i]); savedWeight[i] = float.NaN; }
        }

        public void RecomputeAllNormals()
        {
            var doc = Document;
            Document.DisplayPositions(display);
            var normals = doc.Normals;
            Array.Clear(normals, 0, normals.Length);
            var tris = doc.Triangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var n = Vector3.Cross(display[b] - display[a], display[c] - display[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            var neighbors = doc.Neighbors;
            for (int w = 0; w < neighbors.WeldedCount; w++)
            {
                var sum = Vector3.zero;
                foreach (int m in neighbors.Members[w]) sum += normals[m];
                if (sum.sqrMagnitude < 1e-20f) continue;
                sum.Normalize();
                foreach (int m in neighbors.Members[w]) normals[m] = sum;
            }
            for (int v = 0; v < normals.Length; v++) doc.Dirty.Add(v);
        }

        /// <summary>Uploads the dirty vertices to the working mesh (once a frame); true when something was sent.</summary>
        public bool Flush()
        {
            var doc = Document;
            if (doc.Dirty.Count == 0 && flushedRevision == doc.Revision) return false;
            if (Working == null) return false;
            if (doc.Dirty.Count > 0)
            {
                doc.DisplayPositions(display);
                Working.SetVertices(display, 0, display.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
                Working.SetNormals(doc.Normals, 0, doc.Normals.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
                doc.Dirty.Clear();
                flushes++;
                if (doc.BoundsDirty && flushes % 10 == 0) { Working.RecalculateBounds(); doc.BoundsDirty = false; }
            }
            flushedRevision = doc.Revision;
            return true;
        }

        /// <summary>At a stroke's end: bounds and the normals around what moved.</summary>
        public void FinishStroke(IEnumerable<int> moved)
        {
            Document.DisplayPositions(display);
            Document.RefreshNormals(moved, display);
            Working.RecalculateBounds();
            Document.BoundsDirty = false;
            Flush();
        }

        /// <summary>The display positions (active layer) as last computed.</summary>
        public Vector3[] Display => display;
    }
}
