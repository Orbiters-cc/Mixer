using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using Unity.Collections;
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
        /// <summary>
        /// The shape being edited, or null for the base mesh. Picking a shape loads its deltas at once, so what shows,
        /// what brushes aim at and what undo records are the shape from the first stroke on.
        /// </summary>
        public string Layer
        {
            get => layer;
            set { layer = value; if (value != null) Shape(value); }
        }
        private string layer;
        /// <summary>Bumped by every applied edit.</summary>
        public int Revision;
        /// <summary>Mesh vertices changed since the last flush to the working mesh.</summary>
        public readonly HashSet<int> Dirty = new HashSet<int>();
        /// <summary>Base vertices moved since the session started (what the commit recomputes normals for).</summary>
        public readonly HashSet<int> MovedBase = new HashSet<int>();
        public bool BoundsDirty;
        /// <summary>Per-vertex mask (0: free, 1: held still), null until painted.</summary>
        public float[] Mask;
        /// <summary>Bone influences per vertex, loaded from the original the first time weights are edited; null before.</summary>
        public List<Influence>[] Influences;
        /// <summary>Vertices whose influences differ from the original's.</summary>
        public readonly HashSet<int> WeightEdited = new HashSet<int>();
        /// <summary>The working mesh needs the influences uploaded.</summary>
        public bool WeightsDirty;
        /// <summary>Bind poses per bone, edited by the armature tools; uploaded to the working mesh when dirty.</summary>
        public Matrix4x4[] BindPoses;
        public bool BindPosesDirty;
        public bool BindPosesEdited;
        public int BoneCount => BindPoses != null ? BindPoses.Length : 0;
        public bool HasSkin => BoneCount > 0;

        public struct Influence
        {
            public int Bone;
            public float Weight;
            public Influence(int bone, float weight) { Bone = bone; Weight = weight; }
        }

        /// <summary>The influences, read from the original on first use.</summary>
        public List<Influence>[] EnsureInfluences()
        {
            if (Influences != null) return Influences;
            Influences = new List<Influence>[VertexCount];
            if (!HasSkin)
            {
                for (int v = 0; v < VertexCount; v++) Influences[v] = new List<Influence>(1);
                return Influences;
            }
            var perVertex = Original.GetBonesPerVertex();
            var all = Original.GetAllBoneWeights();
            int cursor = 0;
            for (int v = 0; v < VertexCount; v++)
            {
                int n = v < perVertex.Length ? perVertex[v] : 0;
                var list = new List<Influence>(n);
                for (int k = 0; k < n; k++) { var bw = all[cursor + k]; if (bw.weight > 0f) list.Add(new Influence(bw.boneIndex, bw.weight)); }
                cursor += n;
                Influences[v] = list;
            }
            return Influences;
        }

        public float BoneWeight(int v, int bone)
        {
            var list = Influences != null ? Influences[v] : null;
            if (list == null) return OriginalBoneWeight(v, bone);
            for (int i = 0; i < list.Count; i++) if (list[i].Bone == bone) return list[i].Weight;
            return 0f;
        }

        // Copies, not views into the mesh's memory: the original can be reimported or replaced during a session.
        private byte[] originalPerVertex;
        private BoneWeight1[] originalWeights;
        private int[] originalStart;

        private float OriginalBoneWeight(int v, int bone)
        {
            if (!HasSkin) return 0f;
            if (originalStart == null)
            {
                originalPerVertex = Original.GetBonesPerVertex().ToArray();
                originalWeights = Original.GetAllBoneWeights().ToArray();
                originalStart = new int[VertexCount + 1];
                for (int i = 0; i < VertexCount; i++) originalStart[i + 1] = originalStart[i] + (i < originalPerVertex.Length ? originalPerVertex[i] : 0);
            }
            for (int k = originalStart[v]; k < originalStart[v + 1]; k++) if (originalWeights[k].boneIndex == bone) return originalWeights[k].weight;
            return 0f;
        }

        /// <summary>
        /// Sets a bone's weight on a vertex. With <paramref name="normalize"/> the other bones are scaled so the sum stays 1
        /// (when they had any weight; a lone bone goes to 1). Weights under a thousandth are dropped.
        /// </summary>
        public void SetBoneWeight(int v, int bone, float weight, bool normalize)
        {
            var list = EnsureInfluences()[v];
            weight = Mathf.Clamp01(weight);
            int index = -1;
            float others = 0f;
            for (int i = 0; i < list.Count; i++) { if (list[i].Bone == bone) index = i; else others += list[i].Weight; }
            if (normalize)
            {
                if (others > 1e-6f)
                {
                    float scale = (1f - weight) / others;
                    for (int i = 0; i < list.Count; i++) if (list[i].Bone != bone) list[i] = new Influence(list[i].Bone, list[i].Weight * scale);
                }
                else weight = 1f;
            }
            if (index >= 0) list[index] = new Influence(bone, weight); else if (weight > 0f) list.Add(new Influence(bone, weight));
            for (int i = list.Count - 1; i >= 0; i--) if (list[i].Weight < 0.001f) list.RemoveAt(i);
            list.Sort(ByWeightDescending);
            WeightEdited.Add(v);
            WeightsDirty = true;
        }

        public void SetInfluences(int v, Influence[] influences)
        {
            var list = EnsureInfluences()[v];
            list.Clear();
            list.AddRange(influences);
            // Unity reads the strongest first (and keeps only the first four at the usual quality): Blender's order is any.
            list.Sort(ByWeightDescending);
            WeightEdited.Add(v);
            WeightsDirty = true;
        }

        private static readonly Comparison<Influence> ByWeightDescending = (a, b) => b.Weight.CompareTo(a.Weight);

        /// <summary>The influences as Unity's arrays (sorted by weight within a vertex), for the working mesh or the commit.</summary>
        public void BuildBoneWeights(out NativeArray<byte> perVertex, out NativeArray<BoneWeight1> weights)
        {
            var lists = EnsureInfluences();
            int total = 0;
            for (int v = 0; v < VertexCount; v++) total += Math.Max(1, lists[v].Count);
            perVertex = new NativeArray<byte>(VertexCount, Allocator.Temp);
            weights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
            int cursor = 0;
            for (int v = 0; v < VertexCount; v++)
            {
                var list = lists[v];
                if (list.Count == 0)
                {
                    // Unity needs at least one influence per vertex: the first bone at full weight.
                    perVertex[v] = 1;
                    weights[cursor++] = new BoneWeight1 { boneIndex = 0, weight = 1f };
                    continue;
                }
                float sum = 0f;
                for (int i = 0; i < list.Count; i++) sum += list[i].Weight;
                perVertex[v] = (byte)Math.Min(255, list.Count);
                for (int i = 0; i < list.Count && i < 255; i++)
                    weights[cursor++] = new BoneWeight1 { boneIndex = list[i].Bone, weight = sum > 0f ? list[i].Weight / sum : 1f / list.Count };
            }
        }

        /// <summary>The named group, or null.</summary>
        public VertexGroup Group(string name)
        {
            foreach (var g in Groups) if (g.Name == name) return g;
            return null;
        }
        /// <summary>True once anything differs from the original.</summary>
        public bool Changed => Revision > 0;

        public bool HasMask
        {
            get
            {
                if (Mask == null) return false;
                for (int v = 0; v < Mask.Length; v++) if (Mask[v] > 0f) return true;
                return false;
            }
        }

        public int MaskedCount
        {
            get
            {
                if (Mask == null) return 0;
                int n = 0;
                for (int v = 0; v < Mask.Length; v++) if (Mask[v] > 0.5f) n++;
                return n;
            }
        }

        /// <summary>How much of a brush a vertex takes: 1 minus its mask.</summary>
        public float Free(int v) => Mask == null ? 1f : 1f - Mathf.Clamp01(Mask[v]);

        public float[] EnsureMask() => Mask ?? (Mask = new float[VertexCount]);

        public MeshDocument(Mesh original)
        {
            Original = original ?? throw new ArgumentNullException(nameof(original));
            // The editor reads meshes whether or not Read/Write is on (the flag matters in builds only).
            Positions = original.vertices;
            if (Positions == null || Positions.Length == 0) throw new InvalidOperationException("'" + original.name + "' has no vertex data the editor can read.");
            Normals = original.normals;
            if (Normals == null || Normals.Length != Positions.Length) Normals = new Vector3[Positions.Length];
            VertexCount = Positions.Length;
            Triangles = original.triangles;
            Neighbors = MeshNeighbors.Build(Positions, Triangles, WeldTolerance(original));
            for (int i = 0; i < original.blendShapeCount; i++) ShapeOrder.Add(original.GetBlendShapeName(i));
            BindPoses = original.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight) ? original.bindposes : new Matrix4x4[0];
        }

        /// <summary>
        /// The vertices as the Blender link welds them: by place, normal, skin and shape motion
        /// (<see cref="MeshDataCodec.LinkWeld"/>), so sheets that only touch (double-sided cloth, closed lips, seams that
        /// open in a shape) stay apart in Blender. Built from the original, so its indices hold through the session and
        /// match what was sent before the mesh was opened in Mixer.
        /// </summary>
        public MeshNeighbors LinkWeld => linkWeld ?? (linkWeld = MeshDataCodec.LinkWeld(Original.vertices, Triangles, Original.normals, WeldTolerance(Original), MeshDataCodec.LinkWeldKeys(Original)));
        private MeshNeighbors linkWeld;

        internal static float WeldTolerance(Mesh mesh)
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

        /// <summary>The structure of the shapes (order, edited layers, deletions, the active layer), for undo of shape operations.</summary>
        public sealed class ShapeState
        {
            public List<string> Order;
            public Dictionary<string, ShapeLayer> Edits;
            public HashSet<string> Deleted;
            public string Layer;
        }

        public ShapeState CaptureShapes() => new ShapeState
        {
            Order = new List<string>(ShapeOrder),
            Edits = new Dictionary<string, ShapeLayer>(ShapeEdits),
            Deleted = new HashSet<string>(DeletedShapes),
            Layer = Layer,
        };

        public void RestoreShapes(ShapeState state)
        {
            ShapeOrder.Clear(); ShapeOrder.AddRange(state.Order);
            ShapeEdits.Clear(); foreach (var pair in state.Edits) ShapeEdits[pair.Key] = pair.Value;
            DeletedShapes.Clear(); foreach (string d in state.Deleted) DeletedShapes.Add(d);
            Layer = state.Layer != null && HasShape(state.Layer) ? state.Layer : null;
            Revision++;
        }

        /// <summary>Gives a shape a new name: an original shape is left out of the commit and re-added under the new name.</summary>
        public void RenameShape(string from, string to)
        {
            if (!HasShape(from) || string.IsNullOrEmpty(to) || from == to || HasShape(to)) return;
            var layer = Shape(from);
            int index = ShapeOrder.IndexOf(from);
            if (layer.IsNew) { ShapeEdits.Remove(from); ShapeOrder.Remove(from); }
            else { ShapeEdits.Remove(from); DeletedShapes.Add(from); }
            var renamed = new ShapeLayer { Name = to, Deltas = layer.Deltas, IsNew = true, Edited = true };
            ShapeEdits[to] = renamed;
            DeletedShapes.Remove(to);
            if (index >= 0 && index <= ShapeOrder.Count) ShapeOrder.Insert(index, to); else ShapeOrder.Add(to);
            if (Layer == from) Layer = to;
            Revision++;
        }

        /// <summary>A new shape with a copy of another's deltas.</summary>
        public ShapeLayer DuplicateShape(string from, string to)
        {
            if (!HasShape(from) || string.IsNullOrEmpty(to) || HasShape(to)) return null;
            var source = Shape(from);
            var copy = new ShapeLayer { Name = to, Deltas = (Vector3[])source.Deltas.Clone(), IsNew = true, Edited = true };
            ShapeEdits[to] = copy;
            DeletedShapes.Remove(to);
            ShapeOrder.Add(to);
            Revision++;
            return copy;
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
