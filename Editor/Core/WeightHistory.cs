using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The influences of several vertices in one flat block: vertex i's are <c>this[i]</c> (strongest first). Two arrays
    /// however many vertices, where an array per vertex was garbage for every vertex a stroke touched.
    /// </summary>
    internal sealed class InfluenceBlock
    {
        /// <summary>Where vertex i's influences start in <see cref="Flat"/>; one more entry than vertices.</summary>
        public readonly int[] Start;
        public readonly MeshDocument.Influence[] Flat;

        public InfluenceBlock(int[] start, MeshDocument.Influence[] flat) { Start = start; Flat = flat; }

        public int Length => Start.Length - 1;
        public ArraySegment<MeshDocument.Influence> this[int i] => new ArraySegment<MeshDocument.Influence>(Flat, Start[i], Start[i + 1] - Start[i]);
        public long Bytes => Start.Length * 4L + Flat.Length * 8L + 32;

        /// <summary>The influences the document has now for <paramref name="vertices"/>.</summary>
        public static InfluenceBlock Of(MeshDocument doc, IReadOnlyList<int> vertices)
        {
            var start = new int[vertices.Count + 1];
            for (int i = 0; i < vertices.Count; i++) start[i + 1] = start[i] + doc.InfluenceCount(vertices[i]);
            var flat = new MeshDocument.Influence[start[vertices.Count]];
            for (int i = 0; i < vertices.Count; i++)
                for (int k = 0, n = start[i + 1] - start[i]; k < n; k++) flat[start[i] + k] = doc.InfluenceAt(vertices[i], k);
            return new InfluenceBlock(start, flat);
        }
    }

    /// <summary>Bone influences of vertices changed: each vertex's influences before and after.</summary>
    internal sealed class WeightEdit : HistoryEntry
    {
        public int[] Indices;
        public InfluenceBlock Before, After;

        public override long Bytes => 64 + Indices.Length * 4L + Before.Bytes + After.Bytes;

        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(InfluenceBlock values)
        {
            var doc = Target.Document;
            for (int i = 0; i < Indices.Length; i++) doc.SetInfluences(Indices[i], values.Flat, values.Start[i], values.Start[i + 1] - values.Start[i]);
            doc.Revision++;
            // Undone with others (a jump in the Undo History window), the upload and skinning wait for the last one.
            if (!MixerHistory.Applying) Target.FlushWeights(force: true);
        }
    }

    /// <summary>A vertex group's weights changed.</summary>
    internal sealed class GroupWeightEdit : HistoryEntry
    {
        public string Group;
        public int[] Indices;
        public float[] Before, After;

        public override long Bytes => 64 + Indices.Length * 12L;
        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(float[] values)
        {
            var doc = Target.Document;
            var group = doc.Group(Group);
            if (group == null) { group = new VertexGroup { Name = Group }; doc.Groups.Add(group); }
            for (int i = 0; i < Indices.Length; i++)
            {
                if (values[i] > 0f) group.Weights[Indices[i]] = values[i]; else group.Weights.Remove(Indices[i]);
            }
            doc.Revision++;
            Target.GroupsChanged();
        }
    }

    /// <summary>A vertex group created, removed or renamed.</summary>
    internal sealed class GroupListEdit : HistoryEntry
    {
        public List<VertexGroup> Before, After;
        public override long Bytes => 128;

        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(List<VertexGroup> groups)
        {
            var doc = Target.Document;
            doc.Groups.Clear();
            doc.Groups.AddRange(groups);
            doc.Revision++;
            Target.GroupsChanged();
        }
    }

    /// <summary>Records the influences a weight stroke touches (first touch keeps the influences before).</summary>
    internal sealed class WeightRecorder
    {
        // The touched vertices in order, with their influences before in one flat list (see InfluenceBlock).
        private readonly HashSet<int> touched = new HashSet<int>();
        private readonly List<int> order = new List<int>();
        private readonly List<int> beforeStart = new List<int> { 0 };
        private readonly List<MeshDocument.Influence> beforeFlat = new List<MeshDocument.Influence>();
        private readonly Dictionary<int, float> groupBefore = new Dictionary<int, float>();
        private readonly EditTarget target;
        private readonly string group;
        public readonly string Label;

        public WeightRecorder(EditTarget target, string group, string label)
        {
            this.target = target;
            this.group = group;
            Label = label;
        }

        public bool Empty => order.Count == 0 && groupBefore.Count == 0;

        /// <summary>The mesh vertices touched so far (bone weights only), in order.</summary>
        public IReadOnlyList<int> Touched => order;

        public void Touch(int v)
        {
            if (group != null)
            {
                if (groupBefore.ContainsKey(v)) return;
                var g = target.Document.Group(group);
                groupBefore[v] = g != null && g.Weights.TryGetValue(v, out float w) ? w : 0f;
                return;
            }
            if (!touched.Add(v)) return;
            var doc = target.Document;
            order.Add(v);
            for (int i = 0, n = doc.InfluenceCount(v); i < n; i++) beforeFlat.Add(doc.InfluenceAt(v, i));
            beforeStart.Add(beforeFlat.Count);
        }

        public HistoryEntry Finish()
        {
            if (group != null)
            {
                if (groupBefore.Count == 0) return null;
                var g = target.Document.Group(group);
                var edit = new GroupWeightEdit { Target = target, Label = Label, Group = group, Indices = new int[groupBefore.Count], Before = new float[groupBefore.Count], After = new float[groupBefore.Count] };
                int k = 0;
                foreach (var pair in groupBefore)
                {
                    edit.Indices[k] = pair.Key;
                    edit.Before[k] = pair.Value;
                    edit.After[k] = g != null && g.Weights.TryGetValue(pair.Key, out float w) ? w : 0f;
                    k++;
                }
                return edit;
            }
            if (order.Count == 0) return null;
            return new WeightEdit
            {
                Target = target, Label = Label, Indices = order.ToArray(),
                Before = new InfluenceBlock(beforeStart.ToArray(), beforeFlat.ToArray()),
                After = InfluenceBlock.Of(target.Document, order),
            };
        }
    }
}
