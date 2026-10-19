using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Makes the mesh a commit saves from a target's document: the original's data with the edited base positions,
    /// recomputed normals where it moved, and its blendshapes: untouched ones copied natively (one Instantiate), new
    /// ones appended, and only when an existing shape was edited or deleted is the whole list rebuilt frame by frame
    /// (AddBlendShapeFrame costs more for every shape before it).
    /// </summary>
    internal static class MeshBuilder
    {
        public static Mesh Build(EditTarget target)
        {
            var doc = target.Document;
            var original = target.Original;
            var mesh = Object.Instantiate(original);
            mesh.name = original.name;
            mesh.hideFlags = HideFlags.None;
            mesh.vertices = doc.Positions;
            if (doc.MovedBase.Count > 0) MeshNormals.Recalculate(mesh, doc.MovedBase);
            if (doc.HasSkin && doc.BindPosesEdited) mesh.bindposes = doc.BindPoses;
            if (doc.HasSkin && doc.Influences != null && doc.WeightEdited.Count > 0)
            {
                doc.BuildBoneWeights(out var perVertex, out var weights);
                try { mesh.SetBoneWeights(perVertex, weights); }
                finally { perVertex.Dispose(); weights.Dispose(); }
            }
            // Shapes only loaded (picked as a layer, sent by Blender unchanged) are not edits: they keep the original's frames.
            bool rebuildShapes = doc.DeletedShapes.Count > 0;
            foreach (var layer in doc.ShapeEdits.Values) if (!layer.IsNew && layer.Edited) { rebuildShapes = true; break; }
            Vector3[] weldedBase = null;
            Vector3[] WeldedBase() => weldedBase ?? (weldedBase = WeldedNormals(doc.Positions, doc));
            if (rebuildShapes)
            {
                mesh.ClearBlendShapes();
                foreach (string name in doc.ShapeOrder)
                {
                    if (doc.DeletedShapes.Contains(name)) continue;
                    if (doc.ShapeEdits.TryGetValue(name, out var layer) && (layer.Edited || layer.IsNew)) AddEdited(mesh, original, doc, layer, WeldedBase());
                    else CopyOriginalShape(original, mesh, name);
                }
            }
            else
            {
                foreach (string name in doc.ShapeOrder)
                    if (doc.ShapeEdits.TryGetValue(name, out var layer) && layer.IsNew) AddEdited(mesh, original, doc, layer, WeldedBase());
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void CopyOriginalShape(Mesh original, Mesh mesh, string name)
        {
            int index = original.GetBlendShapeIndex(name);
            if (index < 0) return;
            int frames = original.GetBlendShapeFrameCount(index);
            int count = original.vertexCount;
            var d = new Vector3[count]; var n = new Vector3[count]; var t = new Vector3[count];
            for (int f = 0; f < frames; f++)
            {
                original.GetBlendShapeFrameVertices(index, f, d, n, t);
                mesh.AddBlendShapeFrame(name, original.GetBlendShapeFrameWeight(index, f), d, n, t);
            }
        }

        /// <summary>
        /// A new or edited shape. An edited original keeps its frames and their weights: the edit (the last frame's
        /// change) is spread over the in-between frames in proportion to their weight, so in-betweens still lead to it.
        /// </summary>
        private static void AddEdited(Mesh mesh, Mesh original, MeshDocument doc, ShapeLayer layer, Vector3[] weldedBase)
        {
            foreach (var (weight, deltas) in FramesOf(doc, original, layer.Name))
                mesh.AddBlendShapeFrame(layer.Name, weight, deltas, NormalDeltas(doc, deltas, weldedBase), null);
        }

        /// <summary>
        /// Every frame of a shape (weight, deltas), first to last, as a commit makes it: the original's frames when the
        /// shape was not edited, else the edited deltas as the last frame and the original's in-betweens moved by the
        /// edit in proportion to their weight; a new shape is one frame at 100. Empty for an unknown shape.
        /// </summary>
        internal static List<(float weight, Vector3[] deltas)> FramesOf(MeshDocument doc, Mesh original, string name)
        {
            var frames = new List<(float, Vector3[])>();
            doc.ShapeEdits.TryGetValue(name, out var layer);
            int index = layer != null && layer.IsNew ? -1 : original.GetBlendShapeIndex(name);
            int count = index >= 0 ? original.GetBlendShapeFrameCount(index) : 0;
            bool edited = layer != null && (layer.Edited || layer.IsNew);
            if (!edited)
            {
                for (int f = 0; f < count; f++)
                {
                    var deltas = new Vector3[doc.VertexCount];
                    original.GetBlendShapeFrameVertices(index, f, deltas, null, null);
                    frames.Add((original.GetBlendShapeFrameWeight(index, f), deltas));
                }
                return frames;
            }
            if (count <= 1)
            {
                frames.Add((count == 1 ? original.GetBlendShapeFrameWeight(index, 0) : 100f, layer.Deltas));
                return frames;
            }
            var last = new Vector3[doc.VertexCount];
            original.GetBlendShapeFrameVertices(index, count - 1, last, null, null);
            float lastWeight = original.GetBlendShapeFrameWeight(index, count - 1);
            for (int f = 0; f < count - 1; f++)
            {
                float weight = original.GetBlendShapeFrameWeight(index, f);
                var frame = new Vector3[doc.VertexCount];
                original.GetBlendShapeFrameVertices(index, f, frame, null, null);
                float share = Mathf.Abs(lastWeight) > 1e-6f ? weight / lastWeight : 0f;
                for (int v = 0; v < frame.Length; v++) frame[v] += (layer.Deltas[v] - last[v]) * share;
                frames.Add((weight, frame));
            }
            frames.Add((lastWeight, layer.Deltas));
            return frames;
        }

        /// <summary>
        /// How a frame turns the normals: the welded normals of the shaped surface minus those of the base, both made
        /// the same way, so only the sculpt shows (not the difference between authored and recomputed normals, nor
        /// UV seams). Null when the frame moves nothing.
        /// </summary>
        private static Vector3[] NormalDeltas(MeshDocument doc, Vector3[] deltas, Vector3[] weldedBase)
        {
            int count = deltas.Length;
            bool any = false;
            for (int v = 0; v < count && !any; v++) any = deltas[v].sqrMagnitude > 0f;
            if (!any) return null;
            var shaped = new Vector3[count];
            for (int v = 0; v < count; v++) shaped[v] = doc.Positions[v] + deltas[v];
            var normals = WeldedNormals(shaped, doc);
            for (int v = 0; v < count; v++) normals[v] -= weldedBase[v];
            return normals;
        }

        /// <summary>Area-weighted vertex normals, shared by every vertex at the same place (UV seams don't show).</summary>
        internal static Vector3[] WeldedNormals(Vector3[] positions, MeshDocument doc)
        {
            var tris = doc.Triangles;
            var faceSum = new Vector3[positions.Length];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                faceSum[a] += n; faceSum[b] += n; faceSum[c] += n;
            }
            var result = new Vector3[positions.Length];
            var members = doc.Neighbors.Members;
            for (int w = 0; w < members.Length; w++)
            {
                var sum = Vector3.zero;
                foreach (int m in members[w]) sum += faceSum[m];
                // By hand: millimetre faces sum below Vector3.Normalize's threshold.
                float length = Mathf.Sqrt(sum.sqrMagnitude);
                if (length <= 0f) continue;
                sum /= length;
                foreach (int m in members[w]) result[m] = sum;
            }
            return result;
        }
    }
}
