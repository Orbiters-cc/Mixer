using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>A blendshape of the working copy: the deltas as edited, or loaded from the original the first time it is edited.</summary>
    internal sealed class ShapeLayer
    {
        public string Name;
        /// <summary>Delta per mesh vertex, dense (the mesh's own vertex order).</summary>
        public Vector3[] Deltas;
        /// <summary>Made in this session (the original has no such shape).</summary>
        public bool IsNew;
        /// <summary>Changed since it was loaded or created.</summary>
        public bool Edited;
    }

    /// <summary>A named set of vertices with weights, as Blender's vertex groups; Unity has no such thing, so Mixer keeps them beside the mesh.</summary>
    internal sealed class VertexGroup
    {
        public string Name;
        public readonly Dictionary<int, float> Weights = new Dictionary<int, float>();
    }

    /// <summary>
    /// The working copy of a mesh: plain arrays no Unity object owns, edited by brushes and operations, shown on the
    /// renderer through its working <see cref="Mesh"/> once a frame (<see cref="MixerSession"/> flushes the dirty set).
    /// Positions are in the mesh's own space (bind space for a skinned mesh). The original mesh is never touched; the
    /// blendshapes it holds stay in it until a commit rebuilds the mesh, only the shapes being edited live here.
    /// </summary>
    internal sealed class MeshDocument
    {
        public readonly Mesh Original;
        public readonly int VertexCount;
        /// <summary>Base positions (the mesh's vertices), in mesh space.</summary>
        public readonly Vector3[] Positions;
        public readonly Vector3[] Normals;
        /// <summary>Every submesh's triangles in one array (the mesh's <c>triangles</c>).</summary>
        public readonly int[] Triangles;
        public readonly MeshNeighbors Neighbors;
        /// <summary>Shape names in the order they are committed: the original's, then the new ones.</summary>
        public readonly List<string> ShapeOrder = new List<string>();
        public readonly Dictionary<string, ShapeLayer> ShapeEdits = new Dictionary<string, ShapeLayer>();
        public readonly HashSet<string> DeletedShapes = new HashSet<string>();
        public readonly List<VertexGroup> Groups = new List<VertexGroup>();
        /// <summary>The shape being edited, or null for the base mesh.</summary>
        public string Layer;
        /// <summary>Bumped by every applied edit.</summary>
        public int Revision;
        /// <summary>Mesh vertices changed since the last flush to the working mesh.</summary>
        public readonly HashSet<int> Dirty = new HashSet<int>();
        /// <summary>Base vertices moved since the session started (what the commit recomputes normals for).</summary>
        public readonly HashSet<int> MovedBase = new HashSet<int>();
        public bool BoundsDirty;
        /// <summary>True once anything differs from the original.</summary>
        public bool Changed => Revision > 0;

        public MeshDocument(Mesh original)
        {
            Original = original ?? throw new ArgumentNullException(nameof(original));
            if (!original.isReadable) throw new InvalidOperationException("'" + original.name + "' is not readable: enable Read/Write on its import settings.");
            Positions = original.vertices;
            Normals = original.normals;
            if (Normals == null || Normals.Length != Positions.Length) Normals = new Vector3[Positions.Length];
            VertexCount = Positions.Length;
            Triangles = original.triangles;
            Neighbors = MeshNeighbors.Build(Positions, Triangles, WeldTolerance(original));
            for (int i = 0; i < original.blendShapeCount; i++) ShapeOrder.Add(original.GetBlendShapeName(i));
        }

        private static float WeldTolerance(Mesh mesh)
        {
            float size = mesh.bounds.size.magnitude;
            return Mathf.Max(1e-6f, size * 1e-6f);
        }

        /// <summary>The shape layer by name, loaded from the original on first use; null for an unknown name.</summary>
        public ShapeLayer Shape(string name, bool create = false)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (ShapeEdits.TryGetValue(name, out var layer)) return layer;
            int index = Original.GetBlendShapeIndex(name);
            if (index < 0)
            {
                if (!create) return null;
                layer = new ShapeLayer { Name = name, Deltas = new Vector3[VertexCount], IsNew = true };
                ShapeOrder.Add(name);
            }
            else
            {
                layer = new ShapeLayer { Name = name, Deltas = new Vector3[VertexCount] };
                int frame = Original.GetBlendShapeFrameCount(index) - 1;
                Original.GetBlendShapeFrameVertices(index, frame, layer.Deltas, null, null);
            }
            ShapeEdits[name] = layer;
            return layer;
        }

        public bool HasShape(string name) => !string.IsNullOrEmpty(name) && !DeletedShapes.Contains(name) && (ShapeEdits.ContainsKey(name) || Original.GetBlendShapeIndex(name) >= 0);

        /// <summary>Removes a shape from the commit (an original one is left out of the rebuilt mesh).</summary>
        public void DeleteShape(string name)
        {
            if (!HasShape(name)) return;
            if (ShapeEdits.TryGetValue(name, out var layer) && layer.IsNew) { ShapeEdits.Remove(name); ShapeOrder.Remove(name); }
            else { ShapeEdits.Remove(name); DeletedShapes.Add(name); }
            if (Layer == name) Layer = null;
            Revision++;
        }

        public void RestoreShape(string name, ShapeLayer layer)
        {
            DeletedShapes.Remove(name);
            if (layer != null) { ShapeEdits[name] = layer; if (!ShapeOrder.Contains(name)) ShapeOrder.Add(name); }
            Revision++;
        }

        /// <summary>The position shown for a vertex on the active layer.</summary>
        public Vector3 LayerPosition(int v)
        {
            var p = Positions[v];
            if (Layer != null && ShapeEdits.TryGetValue(Layer, out var layer)) p += layer.Deltas[v];
            return p;
        }

        /// <summary>Positions of the active layer for every vertex, into <paramref name="into"/>.</summary>
        public void DisplayPositions(Vector3[] into)
        {
            Array.Copy(Positions, into, VertexCount);
            if (Layer == null || !ShapeEdits.TryGetValue(Layer, out var layer)) return;
            var d = layer.Deltas;
            for (int v = 0; v < VertexCount; v++) into[v] += d[v];
        }

        /// <summary>Moves a vertex of the active layer by <paramref name="delta"/> (mesh space).</summary>
        public void Move(int v, Vector3 delta)
        {
            if (Layer == null) { Positions[v] += delta; MovedBase.Add(v); }
            else
            {
                var layer = Shape(Layer, create: true);
                layer.Deltas[v] += delta;
                layer.Edited = true;
            }
            Dirty.Add(v);
        }

        /// <summary>Sets the shown position of a vertex on a layer (undo and redo put recorded positions back).</summary>
        public void SetLayerPosition(string layerName, int v, Vector3 position)
        {
            if (layerName == null) { Positions[v] = position; MovedBase.Add(v); }
            else
            {
                var layer = Shape(layerName, create: true);
                layer.Deltas[v] = position - Positions[v];
                layer.Edited = true;
            }
            Dirty.Add(v);
        }

        public Vector3 GetLayerPosition(string layerName, int v)
        {
            var p = Positions[v];
            if (layerName != null && ShapeEdits.TryGetValue(layerName, out var layer)) p += layer.Deltas[v];
            return p;
        }

        public void MarkEdited(IEnumerable<int> vertices)
        {
            foreach (int v in vertices) Dirty.Add(v);
            Revision++;
            BoundsDirty = true;
        }

        /// <summary>Recomputes the normals of the dirty vertices and the ring around them from the active layer's positions.</summary>
        public void RefreshNormals(IEnumerable<int> vertices, Vector3[] display)
        {
            var welded = new HashSet<int>();
            foreach (int v in vertices)
            {
                int w = Neighbors.Weld[v];
                welded.Add(w);
                foreach (int a in Neighbors.Adjacent[w]) welded.Add(a);
            }
            foreach (int w in welded)
            {
                var n = Vector3.zero;
                foreach (int t in Neighbors.TrianglesOf[w])
                {
                    int i = t * 3;
                    Vector3 a = display[Triangles[i]], b = display[Triangles[i + 1]], c = display[Triangles[i + 2]];
                    n += Vector3.Cross(b - a, c - a);
                }
                if (n.sqrMagnitude < 1e-20f) continue;
                n.Normalize();
                foreach (int m in Neighbors.Members[w]) { Normals[m] = n; Dirty.Add(m); }
            }
        }
    }
}
