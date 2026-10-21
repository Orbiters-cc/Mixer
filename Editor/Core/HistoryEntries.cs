using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>One step of Mixer's own history: what Ctrl+Z puts back, kept as the sparse data that changed.</summary>
    internal abstract class HistoryEntry
    {
        public string Label;
        public EditTarget Target;
        /// <summary>Where the entry is in Unity's undo order (given by <see cref="MixerHistory.Push"/>).</summary>
        public int Serial;
        /// <summary>Memory the entry holds, for the cap.</summary>
        public abstract long Bytes { get; }
        public abstract void Undo();
        public abstract void Redo();
        /// <summary>The history let go of the entry (memory cap, a new step over its redo, a clear, a script reload).</summary>
        public virtual void Dropped() { }

        /// <summary>
        /// Brings a target's world picture, uploads and overlay up to date after entries changed its document. Entries
        /// undone or redone by <see cref="MixerHistory"/> leave this to it (once per target, however many steps a jump
        /// took); an entry undone directly (a cancelled stroke) refreshes at once.
        /// </summary>
        public static void Refresh(EditTarget target)
        {
            target.FlushBindposes();
            target.FlushWeights(force: true);
            target.AfterEdit(Array.Empty<int>(), fullRefresh: true);
            target.GroupsChanged();
        }

        protected void RefreshUnlessBatched()
        {
            if (!MixerHistory.Applying && Target != null && Target.Alive) Refresh(Target);
        }
    }

    /// <summary>
    /// Vertices of a layer moved: their positions before and after, and the normals the stroke changed as they were
    /// before it (around the moved vertices), so an undo shows the mesh exactly as it was; a redo recomputes them as
    /// the stroke did.
    /// </summary>
    internal sealed class PositionEdit : HistoryEntry
    {
        public string Layer;
        public int[] Indices;
        public Vector3[] Before, After;
        public int[] NormalIndices;
        public Vector3[] NormalsBefore;

        public override long Bytes => Indices.Length * (4L + 24L) + (NormalIndices != null ? NormalIndices.Length * (4L + 12L) : 0L) + 64;

        public override void Undo()
        {
            Apply(Before);
            if (NormalIndices == null || NormalsBefore == null) { Target.RefreshNormals(Indices); return; }
            var doc = Target.Document;
            for (int i = 0; i < NormalIndices.Length; i++) { doc.Normals[NormalIndices[i]] = NormalsBefore[i]; doc.Dirty.Add(NormalIndices[i]); }
        }

        public override void Redo()
        {
            Apply(After);
            Target.RefreshNormals(Indices);
        }

        private void Apply(Vector3[] positions)
        {
            var doc = Target.Document;
            if (Layer != null && !doc.HasShape(Layer)) doc.RestoreShape(Layer, null);
            bool switched = doc.Layer != Layer;
            doc.Layer = Layer;
            for (int i = 0; i < Indices.Length; i++) doc.SetLayerPosition(Layer, Indices[i], positions[i]);
            doc.MarkEdited(Indices);
            // The step was made on another layer: show that one, with the renderer's weights to match.
            if (switched) Target.AfterLayerChange();
            else RefreshUnlessBatched();
        }
    }

    /// <summary>
    /// What an in-place commit wrote into a Mixer asset, before and after (copies kept in memory). Unity's Undo puts a mesh
    /// asset's content back, but its redo leaves the content as the undo made it (Unity 2022.3): the history writes it
    /// both ways. No target (the session ended with the commit), so forgetting the session keeps it.
    /// </summary>
    internal sealed class InPlaceCommitEdit : HistoryEntry
    {
        public Mesh Asset, Before, After;

        public override long Bytes => 256 + Weight(Before) + Weight(After);

        private static long Weight(Mesh mesh) => mesh == null ? 0 : mesh.vertexCount * (64L + mesh.blendShapeCount * 36L);

        public override void Undo() => Write(Before);
        public override void Redo() => Write(After);

        // Written only when the asset does not hold it already (Unity's own undo of the step does put it back).
        private void Write(Mesh content)
        {
            if (Asset == null || content == null) return;
            if (Asset.vertexCount == content.vertexCount && MixerAutosave.FingerprintOf(Asset.vertices) == MixerAutosave.FingerprintOf(content.vertices)) return;
            MeshAssets.Replace(Object.Instantiate(content), Asset);
        }

        public override void Dropped()
        {
            if (Before != null) Object.DestroyImmediate(Before);
            if (After != null) Object.DestroyImmediate(After);
            Before = After = null;
        }
    }

    /// <summary>A shape created or deleted.</summary>
    internal sealed class ShapeListEdit : HistoryEntry
    {
        public string Name;
        public ShapeLayer Layer;
        /// <summary>True when the entry created the shape (undo deletes it), false when it deleted it (undo restores it).</summary>
        public bool Created;
        public string LayerBefore;
        /// <summary>Where the shape was in the order before it was deleted.</summary>
        public int OrderIndex = -1;

        public override long Bytes => Layer != null && Layer.Deltas != null ? Layer.Deltas.Length * 12L : 64;

        public override void Undo()
        {
            var doc = Target.Document;
            if (Created) doc.DeleteShape(Name); else doc.RestoreShape(Name, Layer, OrderIndex);
            doc.Layer = LayerBefore != null && doc.HasShape(LayerBefore) ? LayerBefore : null;
            Target.AfterLayerChange();
        }

        public override void Redo()
        {
            var doc = Target.Document;
            if (Created) { doc.RestoreShape(Name, Layer, OrderIndex); doc.Layer = Name; } else { doc.DeleteShape(Name); }
            Target.AfterLayerChange();
        }
    }

    /// <summary>Mask values painted: before and after for the touched vertices.</summary>
    internal sealed class MaskEdit : HistoryEntry
    {
        public int[] Indices;
        public float[] Before, After;

        public override long Bytes => Indices.Length * 12L + 64;
        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(float[] values)
        {
            var mask = Target.Document.EnsureMask();
            for (int i = 0; i < Indices.Length; i++) mask[Indices[i]] = values[i];
            Target.Document.Revision++;
        }
    }

    /// <summary>A shape renamed, duplicated or otherwise rearranged: the shape structure before and after.</summary>
    internal sealed class ShapeStateEdit : HistoryEntry
    {
        public MeshDocument.ShapeState Before, After;
        public override long Bytes => 256;

        public override void Undo() { Target.Document.RestoreShapes(Before); Target.AfterLayerChange(); }
        public override void Redo() { Target.Document.RestoreShapes(After); Target.AfterLayerChange(); }
    }

    /// <summary>Records the positions a stroke touches: the first touch of a vertex keeps its position before.</summary>
    internal sealed class StrokeRecorder
    {
        // Marks per mesh vertex (and per normal-welded vertex) instead of dictionaries: a large brush touches tens of
        // thousands of vertices in a step. A mark equal to the serial means kept: a sculpt stroke uses its target's
        // stroke scratch (a new serial each stroke, nothing allocated), a recorder that lives longer (the Blender link's)
        // arrays of its own.
        private readonly int[] kept, normalKept, ringKept;
        private readonly int serial;
        private readonly List<int> indices, normalIndices;
        private readonly List<Vector3> before, normalsBefore;
        private readonly EditTarget target;
        private readonly string layer;
        public readonly string Label;

        public StrokeRecorder(EditTarget target, string label)
        {
            this.target = target;
            layer = target.Document.Layer;
            Label = label;
            int count = target.Document.VertexCount;
            kept = new int[count];
            normalKept = new int[count];
            ringKept = new int[target.Document.NormalWeld.WeldedCount];
            serial = 1;
            indices = new List<int>(); normalIndices = new List<int>();
            before = new List<Vector3>(); normalsBefore = new List<Vector3>();
        }

        /// <summary>A recorder for one sculpt stroke, in the target's stroke scratch (only one stroke records at a time).</summary>
        public StrokeRecorder(EditTarget target, string label, StrokeScratch scratch)
        {
            this.target = target;
            layer = target.Document.Layer;
            Label = label;
            serial = scratch.NextRecord();
            kept = scratch.Kept; normalKept = scratch.NormalKept; ringKept = scratch.RingKept;
            indices = scratch.KeptIndices; normalIndices = scratch.KeptNormalIndices;
            before = scratch.KeptPositions; normalsBefore = scratch.KeptNormals;
        }

        public bool Empty => indices.Count == 0;
        public int Count => indices.Count;

        /// <summary>
        /// Call before moving <paramref name="v"/>: its position and the normals its move will change are kept. True the
        /// first time <paramref name="v"/> is touched.
        /// </summary>
        public bool Touch(int v)
        {
            if (kept[v] == serial) return false;
            kept[v] = serial;
            var doc = target.Document;
            indices.Add(v);
            before.Add(doc.GetLayerPosition(layer, v));
            // RefreshNormals recomputes the normal-welded vertex and its neighbours: those normals are the ones an undo
            // puts back. The members of one welded vertex share that ring: it is kept once for all of them.
            var weld = doc.NormalWeld;
            int w = weld.Weld[v];
            if (ringKept[w] == serial) return true;
            ringKept[w] = serial;
            var normals = doc.Normals;
            foreach (int m in weld.Members[w]) KeepNormal(m, normals);
            foreach (int a in weld.Adjacent[w]) foreach (int m in weld.Members[a]) KeepNormal(m, normals);
            return true;
        }

        private void KeepNormal(int m, Vector3[] normals)
        {
            if (normalKept[m] == serial) return;
            normalKept[m] = serial;
            normalIndices.Add(m);
            normalsBefore.Add(normals[m]);
        }

        public PositionEdit Finish()
        {
            if (indices.Count == 0) return null;
            var doc = target.Document;
            var now = new Vector3[indices.Count];
            for (int i = 0; i < now.Length; i++) now[i] = doc.GetLayerPosition(layer, indices[i]);
            return new PositionEdit { Target = target, Label = Label, Layer = layer, Indices = indices.ToArray(), Before = before.ToArray(), After = now, NormalIndices = normalIndices.ToArray(), NormalsBefore = normalsBefore.ToArray() };
        }
    }
}
