using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Makes the mesh a commit saves from a target's document. It starts from the working mesh, an exact copy of the
    /// original as the session began (so an FBX reimported meanwhile changes nothing: the commit is the mesh that was
    /// edited and shown), with the edited base positions, normals recomputed where the base moved (welded for normals:
    /// hard edges and double-sided sheets keep their sides), tangents there too, and its blendshapes: untouched ones
    /// copied natively (one Instantiate), new ones appended, and only when an existing shape was edited, renamed or
    /// deleted is the whole list rebuilt frame by frame (AddBlendShapeFrame costs more for every shape before it).
    /// </summary>
    internal static class MeshBuilder
    {
        private const string WorkingSuffix = " (Mixer working)";

        public static Mesh Build(EditTarget target)
        {
            var doc = target.Document;
            var source = doc.ShapeSource;
            var start = target.Working != null ? target.Working : target.Original;
            var mesh = Object.Instantiate(start);
            mesh.name = target.Original.name.Replace(WorkingSuffix, "");
            mesh.hideFlags = HideFlags.None;
            mesh.vertices = doc.Positions;
            if (doc.HasNormals)
            {
                var normals = (Vector3[])doc.OriginalNormals.Clone();
                List<int> ring = null;
                if (doc.MovedBase.Count > 0)
                {
                    ring = MovedRing(doc);
                    // The original's normals turned as their faces turned (as the session showed them), not recomputed:
                    // authored normals (smoothed, custom) keep their shading where the base hardly moved.
                    var welded = WeldedNormals(doc.Positions, doc);
                    var was = WeldedNormals(doc.OriginalPositions, doc);
                    foreach (int v in ring)
                    {
                        var n = normals[v] + (welded[v] - was[v]);
                        float length = Mathf.Sqrt(n.sqrMagnitude);
                        normals[v] = length > 1e-6f ? n / length : welded[v];
                    }
                }
                mesh.normals = normals;
                if (ring != null) RecalculateTangents(mesh, ring);
            }
            if (doc.HasSkin)
            {
                mesh.bindposes = doc.BindPoses;
                if (doc.Influences != null && doc.WeightEdited.Count > 0)
                {
                    doc.BuildBoneWeights(out var perVertex, out var weights);
                    try { mesh.SetBoneWeights(perVertex, weights); }
                    finally { perVertex.Dispose(); weights.Dispose(); }
                }
            }
            // Shapes only loaded (picked as a layer, sent by Blender unchanged) are not edits: they keep the original's frames.
            bool rebuildShapes = doc.DeletedShapes.Count > 0 || mesh.blendShapeCount != source.blendShapeCount;
            foreach (var layer in doc.ShapeEdits.Values)
            {
                if (!layer.IsNew && layer.Edited) rebuildShapes = true;
                // A new shape under a deleted original's name takes its place.
                if (layer.IsNew && source.GetBlendShapeIndex(layer.Name) >= 0) rebuildShapes = true;
                if (rebuildShapes) break;
            }
            Vector3[] weldedBase = null;
            Vector3[] WeldedBase() => weldedBase ?? (weldedBase = WeldedNormals(doc.Positions, doc));
            var added = new HashSet<string>();
            if (rebuildShapes)
            {
                var names = doc.LiveShapes();
                bool progress = names.Count > 40;
                try
                {
                    mesh.ClearBlendShapes();
                    for (int i = 0; i < names.Count; i++)
                    {
                        string name = names[i];
                        if (!added.Add(name)) continue;
                        if (progress && i % 8 == 0) EditorUtility.DisplayProgressBar("Mixer", "Writing the blendshapes of " + mesh.name + " (" + i + " of " + names.Count + ")", i / (float)names.Count);
                        AddShape(mesh, source, doc, name, WeldedBase);
                    }
                }
                finally { if (progress) EditorUtility.ClearProgressBar(); }
            }
            else
            {
                for (int i = 0; i < mesh.blendShapeCount; i++) added.Add(mesh.GetBlendShapeName(i));
                foreach (string name in doc.ShapeOrder)
                    if (doc.ShapeEdits.TryGetValue(name, out var layer) && layer.IsNew && added.Add(name)) AddShape(mesh, source, doc, name, WeldedBase);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddShape(Mesh mesh, Mesh source, MeshDocument doc, string name, Func<Vector3[]> weldedBase)
        {
            doc.ShapeEdits.TryGetValue(name, out var layer);
            string from = layer != null ? layer.Source : name;
            bool edited = layer != null && (layer.Edited || (layer.IsNew && from == null));
            if (!edited && from != null && source.GetBlendShapeIndex(from) >= 0) CopyShape(source, mesh, from, name);
            else
                foreach (var (weight, deltas) in FramesOf(doc, source, name))
                    mesh.AddBlendShapeFrame(name, weight, deltas, NormalDeltas(doc, deltas, weldedBase()), null);
        }

        // The original's frames as they are (deltas, normals, tangents), under a name of the commit's.
        private static void CopyShape(Mesh source, Mesh mesh, string from, string name)
        {
            int index = source.GetBlendShapeIndex(from);
            if (index < 0) return;
            int frames = source.GetBlendShapeFrameCount(index);
            int count = source.vertexCount;
            var d = new Vector3[count]; var n = new Vector3[count]; var t = new Vector3[count];
            for (int f = 0; f < frames; f++)
            {
                source.GetBlendShapeFrameVertices(index, f, d, n, t);
                mesh.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(index, f), d, n, t);
            }
        }

        /// <summary>
        /// Every frame of a shape (weight, deltas), first to last, as a commit makes it: the frames of the original shape
        /// it carries when it was not edited, else the edited deltas as the last frame and those in-betweens moved by the
        /// edit in proportion to their weight; a shape made from nothing is one frame at 100. Empty for an unknown shape.
        /// </summary>
        /// <param name="original">The mesh the frames come from: the document's <see cref="MeshDocument.ShapeSource"/> (or its original).</param>
        internal static List<(float weight, Vector3[] deltas)> FramesOf(MeshDocument doc, Mesh original, string name)
        {
            var frames = new List<(float, Vector3[])>();
            doc.ShapeEdits.TryGetValue(name, out var layer);
            string from = layer != null ? layer.Source : (doc.DeletedShapes.Contains(name) ? null : name);
            int index = from != null ? original.GetBlendShapeIndex(from) : -1;
            int count = index >= 0 ? original.GetBlendShapeFrameCount(index) : 0;
            bool edited = layer != null && (layer.Edited || (layer.IsNew && index < 0));
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

        /// <summary>
        /// Area-weighted vertex normals, shared by the vertices <see cref="MeshDocument.NormalWeld"/> welds (UV seams
        /// don't show; hard edges and the two sides of a double-sided sheet keep their own).
        /// </summary>
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
            var members = doc.NormalWeld.Members;
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

        /// <summary>The moved base vertices and the ring of vertices around them whose faces moved: where normals and tangents change.</summary>
        internal static List<int> MovedRing(MeshDocument doc)
        {
            var ring = new HashSet<int>();
            foreach (int v in doc.MovedBase) doc.NormalRing(v, ring);
            return new List<int>(ring);
        }

        // Unity's tangents for the vertices around the moved ones, the original's everywhere else.
        private static void RecalculateTangents(Mesh mesh, List<int> ring)
        {
            if (!mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent) || !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)) return;
            var before = mesh.tangents;
            mesh.RecalculateTangents();
            var after = mesh.tangents;
            if (before == null || after == null || before.Length != after.Length) return;
            var keep = (Vector4[])before.Clone();
            foreach (int v in ring) if (v >= 0 && v < keep.Length) keep[v] = after[v];
            mesh.tangents = keep;
        }
    }
}
