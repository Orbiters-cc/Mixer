using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>Bone influences of vertices changed: the full influence lists before and after.</summary>
    internal sealed class WeightEdit : HistoryEntry
    {
        public int[] Indices;
        public MeshDocument.Influence[][] Before, After;

        public override long Bytes
        {
            get
            {
                long bytes = 64 + Indices.Length * 4L;
                for (int i = 0; i < Indices.Length; i++) bytes += (Before[i].Length + After[i].Length) * 8L + 32;
                return bytes;
            }
        }

        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(MeshDocument.Influence[][] values)
        {
            var doc = Target.Document;
            for (int i = 0; i < Indices.Length; i++) doc.SetInfluences(Indices[i], values[i]);
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

    /// <summary>Records the influences a weight stroke touches (first touch keeps the list before).</summary>
    internal sealed class WeightRecorder
    {
        private readonly Dictionary<int, MeshDocument.Influence[]> before = new Dictionary<int, MeshDocument.Influence[]>();
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

        public bool Empty => before.Count == 0 && groupBefore.Count == 0;

        public void Touch(int v)
        {
            if (group != null)
            {
                if (groupBefore.ContainsKey(v)) return;
                var g = target.Document.Group(group);
                groupBefore[v] = g != null && g.Weights.TryGetValue(v, out float w) ? w : 0f;
                return;
            }
            if (before.ContainsKey(v)) return;
            before[v] = target.Document.EnsureInfluences()[v].ToArray();
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
            if (before.Count == 0) return null;
            var weights = new WeightEdit { Target = target, Label = Label, Indices = new int[before.Count], Before = new MeshDocument.Influence[before.Count][], After = new MeshDocument.Influence[before.Count][] };
            int i = 0;
            var lists = target.Document.EnsureInfluences();
            foreach (var pair in before)
            {
                weights.Indices[i] = pair.Key;
                weights.Before[i] = pair.Value;
                weights.After[i] = lists[pair.Key].ToArray();
                i++;
            }
            return weights;
        }
    }
}
