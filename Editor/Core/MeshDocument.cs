using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        /// <summary>Not in the original under this name: made, duplicated or renamed in this session.</summary>
        public bool IsNew;
        /// <summary>Changed since it was loaded or created.</summary>
        public bool Edited;
        /// <summary>
        /// The original's shape whose frames (in-betweens, frame weights, normals and tangents) this one carries: its own
        /// name for a shape loaded from the original, the shape it was renamed or duplicated from, null for a new one.
        /// </summary>
        public string Source;
        // What loading Source gives (its last frame), kept once read: the shape source never changes during a session.
        internal Vector3[] Baseline;
        internal string BaselineOf;
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
    /// Everything read from the original is read when the document is made (or from <see cref="ShapeSource"/>, a copy
    /// taken then), so an FBX reimported during the session changes nothing under the edits.
    /// </summary>
    internal sealed partial class MeshDocument
    {
        public readonly Mesh Original;
        /// <summary>
        /// Where shapes are loaded from: the working mesh, an exact copy of the original as the session began (Mixer never
        /// changes its blendshapes), or the original itself when there is none.
        /// </summary>
        public readonly Mesh ShapeSource;
        public readonly int VertexCount;
        /// <summary>Base positions (the mesh's vertices), in mesh space.</summary>
        public readonly Vector3[] Positions;
        /// <summary>The normals as shown (the active layer's).</summary>
        public readonly Vector3[] Normals;
        /// <summary>The original's positions, normals and bind poses as the session began.</summary>
        public readonly Vector3[] OriginalPositions, OriginalNormals;
        public readonly Matrix4x4[] OriginalBindPoses;
        /// <summary>
        /// Which version of the mesh the session started from (<see cref="MixerAutosave.FingerprintOf(Mesh)"/>: positions,
        /// triangles, UVs, bind poses, weights and blendshape names), taken when the document is made.
        /// </summary>
        public readonly long OriginalFingerprint;
        /// <summary>Whether the original has normals of its own (else <see cref="OriginalNormals"/> are computed).</summary>
        public readonly bool HasNormals;
        /// <summary>Every submesh's triangles in one array (the mesh's <c>triangles</c>).</summary>
        public readonly int[] Triangles;
        /// <summary>Shape names in the order they are committed: the original's, then the new ones. Each name once.</summary>
        public readonly List<string> ShapeOrder = new List<string>();
        public readonly Dictionary<string, ShapeLayer> ShapeEdits = new Dictionary<string, ShapeLayer>();
        /// <summary>The original's shapes left out of the commit (deleted, or renamed to another name).</summary>
        public readonly HashSet<string> DeletedShapes = new HashSet<string>();
        public readonly List<VertexGroup> Groups = new List<VertexGroup>();
        /// <summary>
        /// The shape being edited, or null for the base mesh. Picking a shape loads its deltas at once, so what shows,
        /// what brushes aim at and what undo records are the shape from the first stroke on.
        /// </summary>
        public string Layer
        {
            get => layer;
            set { if (layer != value) LayerWeight = 1f; layer = value; if (value != null) Shape(value); }
        }
        private string layer;
        /// <summary>
        /// How much of the edited shape shows (1: all of it, as strokes need; the shape key panel's slider tries others).
        /// Back to 1 whenever another layer is picked.
        /// </summary>
        public float LayerWeight = 1f;
        /// <summary>Bumped by every change, edits and display alike (a picked layer, the mask): what views refresh on.</summary>
        public int Revision;
        /// <summary>
        /// Mesh vertices the document changed since the last flush to the working mesh (by page): the flush works their
        /// shown positions out again and uploads them.
        /// </summary>
        public readonly VertexPages Dirty;
        /// <summary>
        /// Vertices whose shown arrays were written straight (a stroke's moves, recomputed normals): the next flush
        /// uploads those pages as they are.
        /// </summary>
        public readonly VertexPages UnflushedPages;
        /// <summary>The shown arrays changed in no particular place (every normal recomputed): the next flush uploads them all.</summary>
        public bool Unflushed;
        /// <summary>Base vertices moved since the session started (what the commit recomputes normals for).</summary>
        public readonly HashSet<int> MovedBase = new HashSet<int>();
        public bool BoundsDirty;
        /// <summary>Per-vertex mask (0: free, 1: held still), null until painted.</summary>
        public float[] Mask;
        /// <summary>Vertices whose influences differ from the original's.</summary>
        public readonly HashSet<int> WeightEdited = new HashSet<int>();
        /// <summary>The working mesh needs the influences uploaded.</summary>
        public bool WeightsDirty;
        /// <summary>Bind poses per bone, edited by the armature tools; uploaded to the working mesh when dirty.</summary>
        public Matrix4x4[] BindPoses;
        public bool BindPosesDirty;
        public bool BindPosesEdited;
        public int BoneCount => BindPoses != null ? BindPoses.Length : 0;
        /// <summary>Bones the original has (more on the renderer are leaf bones added in this session).</summary>
        public int OriginalBoneCount => OriginalBindPoses.Length;
        public bool HasSkin => BoneCount > 0;

        /// <summary>The named group, or null.</summary>
        public VertexGroup Group(string name)
        {
            foreach (var g in Groups) if (g.Name == name) return g;
            return null;
        }

        // ---- What changed ------------------------------------------------------------------------------------------

        // The groups as loaded (from the sidecar), to tell a change of the groups alone.
        private List<(string name, Dictionary<int, float> weights)> loadedGroups = new List<(string, Dictionary<int, float>)>();
        private int groupsCheckedRevision = -1;
        private bool groupsDiffer;

        /// <summary>The groups as they are now are what the document started with (after <see cref="MixerSidecar.Load"/>).</summary>
        public void MarkGroupsLoaded()
        {
            loadedGroups = new List<(string, Dictionary<int, float>)>();
            foreach (var g in Groups) loadedGroups.Add((g.Name, new Dictionary<int, float>(g.Weights)));
            groupsCheckedRevision = -1;
        }

        private bool GroupsDiffer()
        {
            if (groupsCheckedRevision == Revision) return groupsDiffer;
            groupsCheckedRevision = Revision;
            groupsDiffer = false;
            if (loadedGroups.Count != Groups.Count) return groupsDiffer = true;
            for (int i = 0; i < Groups.Count; i++)
            {
                var (name, weights) = loadedGroups[i];
                var g = Groups[i];
                if (g.Name != name || g.Weights.Count != weights.Count) return groupsDiffer = true;
                foreach (var pair in g.Weights)
                    if (!weights.TryGetValue(pair.Key, out float w) || Mathf.Abs(w - pair.Value) > 1e-6f) return groupsDiffer = true;
            }
            return false;
        }

        /// <summary>
        /// True once an edit a commit would write was made: moved vertices, shapes made, edited, renamed or deleted,
        /// weights, bind poses, vertex groups. The mask and picking a layer are not edits.
        /// </summary>
        public bool Changed
        {
            get
            {
                if (MovedBase.Count > 0 || DeletedShapes.Count > 0 || WeightEdited.Count > 0 || BindPosesEdited) return true;
                foreach (var l in ShapeEdits.Values) if (l.IsNew || l.Edited) return true;
                return GroupsDiffer();
            }
        }

        /// <summary>
        /// Whether a commit would write anything that differs from the original, compared value by value (edits all
        /// undone again are no difference): what <see cref="MixerSession.Commit"/> checks before it saves an asset.
        /// </summary>
        public bool HasDifferences()
        {
            foreach (int v in MovedBase) if (Differs(Positions[v], OriginalPositions[v])) return true;
            if (DeletedShapes.Count > 0) return true;
            foreach (var l in ShapeEdits.Values)
            {
                if (l.IsNew) return true;
                if (!l.Edited) continue;
                var baseline = BaselineDeltas(l);
                for (int v = 0; v < VertexCount; v++) if (Differs(l.Deltas[v], baseline[v])) return true;
            }
            foreach (int v in WeightEdited) if (!InfluencesAreOriginal(v)) return true;
            if (BindPosesEdited)
            {
                if (BindPoses.Length != OriginalBindPoses.Length) return true;
                for (int i = 0; i < BindPoses.Length; i++) if (BindPoses[i] != OriginalBindPoses[i]) return true;
            }
            return GroupsDiffer();
        }

        /// <summary>Exactly unequal (Unity's == calls vectors a hundredth of a millimetre apart equal).</summary>
        public static bool Differs(Vector3 a, Vector3 b) => a.x != b.x || a.y != b.y || a.z != b.z;

        /// <summary>What a layer would hold freshly loaded: its source shape's last frame, or zeros for a new shape. Read only (cached).</summary>
        public Vector3[] BaselineDeltas(ShapeLayer layer)
        {
            if (layer != null && layer.Baseline != null && layer.BaselineOf == layer.Source) return layer.Baseline;
            var deltas = new Vector3[VertexCount];
            int index = layer?.Source != null ? ShapeSource.GetBlendShapeIndex(layer.Source) : -1;
            if (index >= 0) ShapeSource.GetBlendShapeFrameVertices(index, ShapeSource.GetBlendShapeFrameCount(index) - 1, deltas, null, null);
            if (layer != null) { layer.Baseline = deltas; layer.BaselineOf = layer.Source; }
            return deltas;
        }

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

        /// <param name="shapeSource">The working mesh (a copy of the original made when the session began), where shapes load from.</param>
        public MeshDocument(Mesh original, Mesh shapeSource = null)
        {
            Original = original ?? throw new ArgumentNullException(nameof(original));
            // The editor reads meshes whether or not Read/Write is on (the flag matters in builds only).
            Positions = original.vertices;
            if (Positions == null || Positions.Length == 0) throw new InvalidOperationException("'" + original.name + "' has no vertex data the editor can read.");
            VertexCount = Positions.Length;
            Dirty = new VertexPages(VertexCount);
            UnflushedPages = new VertexPages(VertexCount);
            ShapeSource = shapeSource != null && shapeSource.vertexCount == VertexCount && shapeSource.blendShapeCount == original.blendShapeCount ? shapeSource : original;
            OriginalPositions = (Vector3[])Positions.Clone();
            Triangles = original.triangles;
            var normals = original.normals;
            HasNormals = normals != null && normals.Length == VertexCount;
            OriginalNormals = HasNormals ? normals : new Vector3[VertexCount];
            Normals = (Vector3[])OriginalNormals.Clone();
            for (int i = 0; i < original.blendShapeCount; i++) ShapeOrder.Add(original.GetBlendShapeName(i));
            bool skinned = original.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            OriginalBindPoses = skinned ? original.bindposes : new Matrix4x4[0];
            BindPoses = (Matrix4x4[])OriginalBindPoses.Clone();
            if (skinned && OriginalBindPoses.Length > 0)
            {
                originalPerVertex = original.GetBonesPerVertex().ToArray();
                originalWeights = original.GetAllBoneWeights().ToArray();
                originalStart = new int[VertexCount + 1];
                for (int i = 0; i < VertexCount; i++) originalStart[i + 1] = originalStart[i] + (i < originalPerVertex.Length ? originalPerVertex[i] : 0);
            }
            OriginalFingerprint = MixerAutosave.FingerprintOf(original, OriginalPositions, Triangles, OriginalBindPoses, originalPerVertex, originalWeights);
            weldTolerance = WeldTolerance(original);
            if (!HasNormals) { var computed = MeshBuilder.WeldedNormals(OriginalPositions, this); Array.Copy(computed, OriginalNormals, VertexCount); Array.Copy(computed, Normals, VertexCount); }
        }

        private readonly float weldTolerance;

        /// <summary>The vertices welded by position, with their neighbours: what brushes move together (built on first use).</summary>
        public MeshNeighbors Neighbors => neighbors ?? (neighbors = Built(ref neighborsTask, BuildNeighbors));
        private MeshNeighbors neighbors;
        private Task<MeshNeighbors> neighborsTask;

        /// <summary>
        /// The vertices welded for normals: at one place and with original normals less than 60 degrees apart, so UV seams
        /// share a normal while hard edges stay hard and sheets that only touch (double-sided cloth, closed lips) keep
        /// their own sides. Built on first use.
        /// </summary>
        public MeshNeighbors NormalWeld => normalWeld ?? (normalWeld = Built(ref normalWeldTask, BuildNormalWeld));
        private MeshNeighbors normalWeld;
        private Task<MeshNeighbors> normalWeldTask;

        private MeshNeighbors BuildNeighbors() => MeshNeighbors.Build(OriginalPositions, Triangles, weldTolerance);
        private MeshNeighbors BuildNormalWeld() => MeshNeighbors.Build(OriginalPositions, Triangles, weldTolerance, HasNormals ? OriginalNormals : null, HasNormals ? 0.5f : -1f);

        /// <summary>
        /// Starts building <see cref="Neighbors"/> and <see cref="NormalWeld"/> on worker threads (they read only the
        /// original's arrays), so the first stroke does not wait half a second for each on a body.
        /// </summary>
        public void Prewarm()
        {
            if (neighbors == null && neighborsTask == null) neighborsTask = Task.Run(BuildNeighbors);
            if (normalWeld == null && normalWeldTask == null) normalWeldTask = Task.Run(BuildNormalWeld);
        }

        /// <summary>Neighbours started in the background prewarm when they ran (waited for), else built here.</summary>
        internal Task<MeshNeighbors> NeighborsTask => neighbors != null ? Task.FromResult(neighbors) : neighborsTask;
        internal Task<MeshNeighbors> NormalWeldTask => normalWeld != null ? Task.FromResult(normalWeld) : normalWeldTask;

        private static MeshNeighbors Built(ref Task<MeshNeighbors> task, Func<MeshNeighbors> build)
        {
            var started = task;
            task = null;
            if (started != null)
            {
                try { return started.Result; }
                catch (Exception) { /* built again below, where its exception shows */ }
            }
            return build();
        }

        /// <summary>
        /// The vertices as the Blender link welds them: by place, normal, skin and shape motion
        /// (<see cref="MeshDataCodec.LinkWeld"/>), so sheets that only touch (double-sided cloth, closed lips, seams that
        /// open in a shape) stay apart in Blender. Built from the original, so its indices hold through the session and
        /// match what was sent before the mesh was opened in Mixer.
        /// </summary>
        public MeshNeighbors LinkWeld => linkWeld ?? (linkWeld = MeshDataCodec.LinkWeld(OriginalPositions, Triangles, HasNormals ? OriginalNormals : new Vector3[0], weldTolerance, MeshDataCodec.LinkWeldKeys(Original)));
        private MeshNeighbors linkWeld;

        internal static float WeldTolerance(Mesh mesh)
        {
            float size = mesh.bounds.size.magnitude;
            return Mathf.Max(1e-6f, size * 1e-6f);
        }

        // ---- Shapes ------------------------------------------------------------------------------------------------

        /// <summary>Whether the original has a shape of this name.</summary>
        public bool InSource(string name) => !string.IsNullOrEmpty(name) && ShapeSource.GetBlendShapeIndex(name) >= 0;

        /// <summary>
        /// The shape layer by name, loaded from the original on first use; null for an unknown name. With
        /// <paramref name="create"/> an unknown name, or the name of a deleted shape, gets a new empty shape.
        /// </summary>
        public ShapeLayer Shape(string name, bool create = false)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (ShapeEdits.TryGetValue(name, out var layer)) return layer;
            bool deleted = DeletedShapes.Contains(name);
            int index = deleted ? -1 : ShapeSource.GetBlendShapeIndex(name);
            if (index < 0)
            {
                if (!create) return null;
                // A new shape; under a deleted shape's name it takes that shape's place in the order.
                layer = new ShapeLayer { Name = name, Deltas = new Vector3[VertexCount], IsNew = true };
                DeletedShapes.Remove(name);
                if (!ShapeOrder.Contains(name)) ShapeOrder.Add(name);
            }
            else
            {
                layer = new ShapeLayer { Name = name, Deltas = new Vector3[VertexCount], Source = name };
                int frame = ShapeSource.GetBlendShapeFrameCount(index) - 1;
                ShapeSource.GetBlendShapeFrameVertices(index, frame, layer.Deltas, null, null);
            }
            ShapeEdits[name] = layer;
            return layer;
        }

        public bool HasShape(string name) => !string.IsNullOrEmpty(name) && !DeletedShapes.Contains(name) && (ShapeEdits.ContainsKey(name) || ShapeSource.GetBlendShapeIndex(name) >= 0);

        /// <summary>The shapes a commit would write, in order.</summary>
        public List<string> LiveShapes()
        {
            var names = new List<string>(ShapeOrder.Count);
            foreach (string n in ShapeOrder) if (HasShape(n)) names.Add(n);
            return names;
        }

        /// <summary>Whether some live shape other than <paramref name="except"/> carries the original shape <paramref name="source"/>.</summary>
        public bool IsCarried(string source, string except = null)
        {
            foreach (var l in ShapeEdits.Values) if (l.Source == source && l.Name != except && !DeletedShapes.Contains(l.Name)) return true;
            return false;
        }

        /// <summary>Removes a shape from the commit (an original one is left out of the rebuilt mesh).</summary>
        public void DeleteShape(string name)
        {
            if (!HasShape(name)) return;
            ShapeEdits.Remove(name);
            if (InSource(name)) DeletedShapes.Add(name); else ShapeOrder.Remove(name);
            if (Layer == name) Layer = null;
            Revision++;
        }

        /// <summary>Puts a deleted shape back (an undo), at <paramref name="orderIndex"/> in the order when it had left it.</summary>
        public void RestoreShape(string name, ShapeLayer layer, int orderIndex = -1)
        {
            DeletedShapes.Remove(name);
            if (layer != null) ShapeEdits[name] = layer;
            if (!ShapeOrder.Contains(name))
            {
                if (orderIndex >= 0 && orderIndex <= ShapeOrder.Count) ShapeOrder.Insert(orderIndex, name); else ShapeOrder.Add(name);
            }
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

        /// <summary>
        /// Gives a shape a new name, in the same place of the order: an original shape is left out of the commit and its
        /// frames go on under the new name (renaming back gives the original shape back).
        /// </summary>
        public void RenameShape(string from, string to)
        {
            if (!HasShape(from) || string.IsNullOrEmpty(to) || from == to || HasShape(to)) return;
            var layer = Shape(from);
            ShapeEdits.Remove(from);
            int index = ShapeOrder.IndexOf(from);
            if (InSource(from)) DeletedShapes.Add(from);
            else ShapeOrder.Remove(from);
            // The new name may be a deleted original's: it leaves its own place for the renamed shape's.
            int existing = ShapeOrder.IndexOf(to);
            if (existing >= 0) { ShapeOrder.RemoveAt(existing); if (existing < index) index--; }
            bool backToSource = layer.Source != null && layer.Source == to;
            ShapeEdits[to] = new ShapeLayer { Name = to, Deltas = layer.Deltas, IsNew = !backToSource, Edited = layer.Edited, Source = layer.Source };
            DeletedShapes.Remove(to);
            if (index >= 0 && index <= ShapeOrder.Count) ShapeOrder.Insert(index, to); else ShapeOrder.Add(to);
            if (Layer == from) Layer = to;
            Revision++;
        }

        /// <summary>A new shape with a copy of another's deltas (and its frames), added at the end.</summary>
        public ShapeLayer DuplicateShape(string from, string to)
        {
            if (!HasShape(from) || string.IsNullOrEmpty(to) || HasShape(to)) return null;
            var source = Shape(from);
            var copy = new ShapeLayer { Name = to, Deltas = (Vector3[])source.Deltas.Clone(), IsNew = true, Edited = source.Edited, Source = source.Source };
            ShapeEdits[to] = copy;
            DeletedShapes.Remove(to);
            ShapeOrder.Remove(to);
            ShapeOrder.Add(to);
            Revision++;
            return copy;
        }

        // ---- Positions ---------------------------------------------------------------------------------------------

        /// <summary>The position shown for a vertex on the active layer.</summary>
        public Vector3 LayerPosition(int v)
        {
            var p = Positions[v];
            if (Layer != null && ShapeEdits.TryGetValue(Layer, out var layer)) p += layer.Deltas[v] * LayerWeight;
            return p;
        }

        /// <summary>Positions of the active layer (at <see cref="LayerWeight"/>) for every vertex, into <paramref name="into"/>.</summary>
        public void DisplayPositions(Vector3[] into) => DisplayPositions(into, 0, VertexCount);

        /// <summary><see cref="DisplayPositions(Vector3[])"/> for vertices <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> - 1.</summary>
        public void DisplayPositions(Vector3[] into, int start, int count)
        {
            Array.Copy(Positions, start, into, start, count);
            if (Layer == null || !ShapeEdits.TryGetValue(Layer, out var layer)) return;
            var d = layer.Deltas;
            float weight = LayerWeight;
            int end = start + count;
            if (weight == 1f) for (int v = start; v < end; v++) into[v] += d[v];
            else for (int v = start; v < end; v++) into[v] += d[v] * weight;
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

        /// <summary>
        /// The positions of a layer (null: the base) resolved once, for loops over many vertices (the per-vertex accessors
        /// look the layer up by name at every call). Valid until the layer is replaced (created, renamed, deleted).
        /// </summary>
        public LayerView View(string layerName, float weight = 1f) =>
            new LayerView(Positions, layerName != null && ShapeEdits.TryGetValue(layerName, out var layer) ? layer.Deltas : null, weight);

        /// <summary>The positions shown: the active layer at <see cref="LayerWeight"/>.</summary>
        public LayerView ShownView() => View(Layer, LayerWeight);

        public readonly struct LayerView
        {
            private readonly Vector3[] positions, deltas;
            private readonly float weight;

            public LayerView(Vector3[] positions, Vector3[] deltas, float weight) { this.positions = positions; this.deltas = deltas; this.weight = weight; }

            public Vector3 this[int v] => deltas == null ? positions[v] : weight == 1f ? positions[v] + deltas[v] : positions[v] + deltas[v] * weight;
        }

        /// <summary><see cref="SetLayerPosition"/> for many vertices, the layer looked up once.</summary>
        public void SetLayerPositions(string layerName, int[] indices, Vector3[] positions)
        {
            if (layerName == null)
            {
                for (int i = 0; i < indices.Length; i++) { int v = indices[i]; Positions[v] = positions[i]; MovedBase.Add(v); Dirty.Add(v); }
                return;
            }
            var layer = Shape(layerName, create: true);
            layer.Edited = true;
            var deltas = layer.Deltas;
            for (int i = 0; i < indices.Length; i++) { int v = indices[i]; deltas[v] = positions[i] - Positions[v]; Dirty.Add(v); }
        }

        public Vector3 GetLayerPosition(string layerName, int v)
        {
            var p = Positions[v];
            if (layerName != null && ShapeEdits.TryGetValue(layerName, out var layer)) p += layer.Deltas[v];
            return p;
        }

        public void MarkEdited(IReadOnlyList<int> vertices)
        {
            for (int i = 0; i < vertices.Count; i++) Dirty.Add(vertices[i]);
            Revision++;
            BoundsDirty = true;
        }

        /// <summary>Every vertex goes to the working mesh at the next flush.</summary>
        public void MarkAllDirty()
        {
            Dirty.AddAll();
            Revision++;
            BoundsDirty = true;
        }

        /// <summary>
        /// Recomputes the normals of the given vertices and the ring around them from the active layer's positions,
        /// welded as <see cref="NormalWeld"/> says (each side of a hard edge or of a double-sided sheet from its own faces).
        /// </summary>
        public void RefreshNormals(IEnumerable<int> vertices, Vector3[] display)
        {
            var weld = NormalWeld;
            int stamp = NextNormalStamp(weld);
            var welded = new List<int>();
            foreach (int v in vertices)
            {
                int w = weld.Weld[v];
                if (normalStamp[w] == stamp) continue;
                normalStamp[w] = stamp;
                welded.Add(w);
            }
            RefreshNormalsWelded(welded, display);
        }

        /// <summary>
        /// <see cref="RefreshNormals"/> for vertices given by their <see cref="NormalWeld"/> index (each once): those and
        /// their neighbours get normals from <paramref name="display"/>, uploaded at the next flush.
        /// </summary>
        public void RefreshNormalsWelded(List<int> welded, Vector3[] display)
        {
            if (welded.Count == 0) return;
            var weld = NormalWeld;
            int stamp = NextNormalStamp(weld);
            var ring = normalRing;
            ring.Clear();
            foreach (int w in welded)
            {
                if (normalStamp[w] != stamp) { normalStamp[w] = stamp; ring.Add(w); }
                foreach (int a in weld.Adjacent[w])
                    if (normalStamp[a] != stamp) { normalStamp[a] = stamp; ring.Add(a); }
            }
            // The pages the ring's normals land on are the ones the flush uploads.
            var members = weld.Members;
            foreach (int w in ring) foreach (int m in members[w]) UnflushedPages.Add(m);
            // Each welded vertex writes only its own members: a large stroke splits the ring over the cores.
            if (ring.Count < 4096) RecomputeNormals(ring, 0, ring.Count, weld, display);
            else Parallel.ForEach(Partitioner.Create(0, ring.Count, 2048), range => RecomputeNormals(ring, range.Item1, range.Item2, weld, display));
        }

        private void RecomputeNormals(List<int> ring, int from, int to, MeshNeighbors weld, Vector3[] display)
        {
            var before = FaceNormalsOfOriginal(weld);
            for (int k = from; k < to; k++) Turn(ring[k], weld, display, before);
        }

        /// <summary>
        /// The normals of every vertex for the shown positions: the original's wherever their faces did not move, turned
        /// by how much their faces turned elsewhere.
        /// </summary>
        public void RefreshAllNormals(Vector3[] display)
        {
            var weld = NormalWeld;
            var before = FaceNormalsOfOriginal(weld);
            Parallel.ForEach(Partitioner.Create(0, weld.WeldedCount, 4096), range =>
            {
                for (int w = range.Item1; w < range.Item2; w++) Turn(w, weld, display, before);
            });
            Unflushed = true;
        }

        // A welded vertex's normals for the shown positions. The original's (authored, often smoothed or custom) turned by
        // the change of the faces' normal, not the faces' normal itself: recomputing the normals outright changed the
        // shading wherever a brush touched the surface, even where it hardly moved (dents and blotches after Smooth), and
        // flattened the members' own normals (soft edges up to 60 degrees apart) into one.
        private void Turn(int w, MeshNeighbors weld, Vector3[] display, Vector3[] before)
        {
            var now = FaceNormal(w, weld, display);
            var was = before[w];
            foreach (int m in weld.Members[w])
            {
                if (!Differs(now, was)) { Normals[m] = OriginalNormals[m]; continue; }
                var n = OriginalNormals[m] + (now - was);
                float length = (float)Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                Normals[m] = length > 1e-6f ? n / length : now;
            }
        }

        // The area-weighted normal of a welded vertex's faces (zero for faces of no area).
        private Vector3 FaceNormal(int w, MeshNeighbors weld, Vector3[] positions)
        {
            var triangles = Triangles;
            float x = 0f, y = 0f, z = 0f;
            foreach (int t in weld.TrianglesOf[w])
            {
                int i = t * 3;
                Vector3 a = positions[triangles[i]], b = positions[triangles[i + 1]], c = positions[triangles[i + 2]];
                float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
                float vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                x += uy * vz - uz * vy;
                y += uz * vx - ux * vz;
                z += ux * vy - uy * vx;
            }
            float length = (float)Math.Sqrt(x * x + y * y + z * z);
            return length > 0f ? new Vector3(x / length, y / length, z / length) : Vector3.zero;
        }

        // The faces' normal of each welded vertex on the original, made the same way as the shown ones (so an untouched
        // vertex compares exactly equal). Made once, on the first refresh.
        private Vector3[] originalFaceNormals;
        private readonly object originalFaceNormalsLock = new object();

        private Vector3[] FaceNormalsOfOriginal(MeshNeighbors weld)
        {
            lock (originalFaceNormalsLock)
            {
                if (originalFaceNormals != null && originalFaceNormals.Length == weld.WeldedCount) return originalFaceNormals;
                var result = new Vector3[weld.WeldedCount];
                for (int w = 0; w < result.Length; w++) result[w] = FaceNormal(w, weld, OriginalPositions);
                return originalFaceNormals = result;
            }
        }

        // Normal refreshes mark the welded vertices they visit with a stamp instead of filling sets (a stroke step visits thousands).
        private int[] normalStamp;
        private int normalStampValue;
        private readonly List<int> normalRing = new List<int>();

        private int NextNormalStamp(MeshNeighbors weld)
        {
            if (normalStamp == null || normalStamp.Length != weld.WeldedCount) { normalStamp = new int[weld.WeldedCount]; normalStampValue = 0; }
            if (++normalStampValue == int.MaxValue) { Array.Clear(normalStamp, 0, normalStamp.Length); normalStampValue = 1; }
            return normalStampValue;
        }

        /// <summary>The mesh vertices whose normals <see cref="RefreshNormals"/> recomputes for a move of <paramref name="v"/>.</summary>
        public void NormalRing(int v, ICollection<int> into)
        {
            var weld = NormalWeld;
            int w = weld.Weld[v];
            foreach (int m in weld.Members[w]) into.Add(m);
            foreach (int a in weld.Adjacent[w]) foreach (int m in weld.Members[a]) into.Add(m);
        }
    }
}
