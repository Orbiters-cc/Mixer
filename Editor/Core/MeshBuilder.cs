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
            if (doc.HasSkin && doc.Influences != null && doc.WeightEdited.Count > 0)
            {
                doc.BuildBoneWeights(out var perVertex, out var weights);
                try { mesh.SetBoneWeights(perVertex, weights); }
                finally { perVertex.Dispose(); weights.Dispose(); }
            }
            bool rebuildShapes = doc.DeletedShapes.Count > 0;
            foreach (var layer in doc.ShapeEdits.Values) if (!layer.IsNew && layer.Edited) { rebuildShapes = true; break; }
            var baseNormals = mesh.normals;
            if (rebuildShapes)
            {
                mesh.ClearBlendShapes();
                foreach (string name in doc.ShapeOrder)
                {
                    if (doc.DeletedShapes.Contains(name)) continue;
                    if (doc.ShapeEdits.TryGetValue(name, out var layer)) AddEdited(mesh, layer, baseNormals);
                    else CopyOriginalShape(original, mesh, name);
                }
            }
            else
            {
                foreach (string name in doc.ShapeOrder)
                    if (doc.ShapeEdits.TryGetValue(name, out var layer) && layer.IsNew) AddEdited(mesh, layer, baseNormals);
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

        // The shape's normals: those of the base with the deltas applied, minus the base's, so lighting follows the sculpt.
        private static void AddEdited(Mesh mesh, ShapeLayer layer, Vector3[] baseNormals)
        {
            int count = mesh.vertexCount;
            var vertices = mesh.vertices;
            var shaped = new Vector3[count];
            for (int v = 0; v < count; v++) shaped[v] = vertices[v] + layer.Deltas[v];
            var temp = new Mesh();
            temp.indexFormat = mesh.indexFormat;
            temp.vertices = shaped;
            temp.triangles = mesh.triangles;
            temp.RecalculateNormals();
            var shapedNormals = temp.normals;
            Object.DestroyImmediate(temp);
            var normalDeltas = new Vector3[count];
            bool anyDelta = false;
            for (int v = 0; v < count; v++)
            {
                if (layer.Deltas[v].sqrMagnitude > 0f) anyDelta = true;
                normalDeltas[v] = layer.Deltas[v].sqrMagnitude > 0f && baseNormals != null && baseNormals.Length == count ? shapedNormals[v] - baseNormals[v] : Vector3.zero;
            }
            mesh.AddBlendShapeFrame(layer.Name, 100f, layer.Deltas, anyDelta ? normalDeltas : null, null);
        }
    }
}
