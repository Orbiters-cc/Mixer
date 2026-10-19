using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>One step of Mixer's own history: what Ctrl+Z puts back, kept as the sparse data that changed.</summary>
    internal abstract class HistoryEntry
    {
        public string Label;
        public EditTarget Target;
        /// <summary>Memory the entry holds, for the cap.</summary>
        public abstract long Bytes { get; }
        public abstract void Undo();
        public abstract void Redo();
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
            doc.Layer = Layer;
            for (int i = 0; i < Indices.Length; i++) doc.SetLayerPosition(Layer, Indices[i], positions[i]);
            doc.MarkEdited(Indices);
            Target.AfterEdit(Indices, fullRefresh: true);
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

        public override long Bytes => Layer != null && Layer.Deltas != null ? Layer.Deltas.Length * 12L : 64;

        public override void Undo()
        {
            var doc = Target.Document;
            if (Created) doc.DeleteShape(Name); else doc.RestoreShape(Name, Layer);
            doc.Layer = LayerBefore != null && doc.HasShape(LayerBefore) ? LayerBefore : null;
            Target.AfterLayerChange();
        }

        public override void Redo()
        {
            var doc = Target.Document;
            if (Created) { doc.RestoreShape(Name, Layer); doc.Layer = Name; } else { doc.DeleteShape(Name); }
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
        private readonly Dictionary<int, Vector3> before = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Vector3> normalsBefore = new Dictionary<int, Vector3>();
        private readonly EditTarget target;
        private readonly string layer;
        public readonly string Label;

        public StrokeRecorder(EditTarget target, string label)
        {
            this.target = target;
            layer = target.Document.Layer;
            Label = label;
        }

        public bool Empty => before.Count == 0;
        public int Count => before.Count;

        /// <summary>Call before moving <paramref name="v"/>: its position and the normals its move will change are kept.</summary>
        public void Touch(int v)
        {
            if (before.ContainsKey(v)) return;
            var doc = target.Document;
            before[v] = doc.GetLayerPosition(layer, v);
            // RefreshNormals recomputes the welded vertex and its neighbours: those normals are the ones an undo puts back.
            var neighbors = doc.Neighbors;
            int w = neighbors.Weld[v];
            Keep(neighbors.Members[w]);
            foreach (int a in neighbors.Adjacent[w]) Keep(neighbors.Members[a]);
        }

        private void Keep(int[] members)
        {
            var normals = target.Document.Normals;
            foreach (int m in members) if (!normalsBefore.ContainsKey(m)) normalsBefore[m] = normals[m];
        }

        public PositionEdit Finish()
        {
            if (before.Count == 0) return null;
            var indices = new int[before.Count];
            var was = new Vector3[before.Count];
            var now = new Vector3[before.Count];
            int i = 0;
            foreach (var pair in before)
            {
                indices[i] = pair.Key;
                was[i] = pair.Value;
                now[i] = target.Document.GetLayerPosition(layer, pair.Key);
                i++;
            }
            var normalIndices = new int[normalsBefore.Count];
            var normals = new Vector3[normalsBefore.Count];
            int n = 0;
            foreach (var pair in normalsBefore) { normalIndices[n] = pair.Key; normals[n] = pair.Value; n++; }
            return new PositionEdit { Target = target, Label = Label, Layer = layer, Indices = indices, Before = was, After = now, NormalIndices = normalIndices, NormalsBefore = normals };
        }
    }
}
