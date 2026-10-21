using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Puts what Blender sends into the scene: positions and meshes of the meshes Blender claimed go into their Mixer
    /// documents (shown at once; one undo step when Blender releases the mesh), weights and groups likewise, transforms
    /// and names onto the objects with Unity's Undo. Blender's vertices are the welded ones: each lands on every Unity
    /// vertex sharing its position. Nothing is applied to a mesh Blender has not claimed (or whose claim Unity refused),
    /// nor to a document whose topology is not the one Blender's copy was made for.
    /// </summary>
    internal static class LinkApply
    {
        private sealed class BlenderEdit
        {
            public EditTarget Target;
            public string MeshId;
            /// <summary>The base positions before Blender's edit (made on the base layer).</summary>
            public StrokeRecorder Base;
            /// <summary>Per shape edited: its deltas before Blender's edit (deltas, so moving the base meanwhile does not matter).</summary>
            public readonly Dictionary<string, DeltaRecorder> Shapes = new Dictionary<string, DeltaRecorder>();
            /// <summary>"" for the bones' influences, else "g:" and the group's name.</summary>
            public readonly Dictionary<string, WeightRecorder> Weights = new Dictionary<string, WeightRecorder>();
            /// <summary>Shapes and groups Blender's edit made (undone after its positions and weights).</summary>
            public readonly List<HistoryEntry> Created = new List<HistoryEntry>();
            /// <summary>Shapes and groups Blender's edit removed (undone first).</summary>
            public readonly List<HistoryEntry> Removed = new List<HistoryEntry>();
        }

        private static readonly Dictionary<string, BlenderEdit> edits = new Dictionary<string, BlenderEdit>();

        /// <summary>
        /// The documents a frame of Blender's for this mesh goes into: every session showing the mesh, when Blender holds
        /// the mesh's claim and its copy has the document's topology (and <paramref name="count"/> welded vertices, unless -1).
        /// Never starts a session: Blender's claim does.
        /// </summary>
        private static List<EditTarget> TargetsOf(MixerLinkHost host, string meshId, ulong topology, int count)
        {
            var result = new List<EditTarget>();
            if (string.IsNullOrEmpty(meshId)) return result;
            if (!host.OwnsMesh(meshId))
            {
                host.NoteUnclaimed(meshId);
                return result;
            }
            string mismatch = null;
            foreach (var target in MixerSession.Targets)
            {
                if (!target.Alive || MixerLinkService.MeshIdOf(target) != meshId) continue;
                var doc = target.Document;
                int welded = doc.LinkWeld.WeldedCount;
                if (count >= 0 && count != welded)
                {
                    mismatch = "Blender's " + target.Name + " has " + count + " vertices, Unity's " + welded + ": the topology changed, which the link does not apply. What you change in this mode stays in Blender: take Unity's mesh from the Mixer panel (yours is kept in a copy), or export an FBX for it.";
                    continue;
                }
                if (topology != MeshDataCodec.Topology(doc))
                {
                    mismatch = "Blender's copy of " + target.Name + " was made for another topology of the mesh (faces or vertex order differ): what you change in this mode stays in Blender. Take Unity's mesh from the Mixer panel (yours is kept in a copy).";
                    continue;
                }
                result.Add(target);
            }
            // Nothing Blender sends under this claim can be applied: it ends, so Blender knows its edits stay there.
            if (result.Count == 0 && mismatch != null) host.EndClaim(meshId, mismatch, takeBack: false);
            return result;
        }

        private static BlenderEdit EditOf(EditTarget target, string meshId)
        {
            if (!edits.TryGetValue(target.Id, out var edit) || edit.Target != target)
            {
                edit = new BlenderEdit { Target = target, MeshId = meshId };
                edits[target.Id] = edit;
            }
            return edit;
        }

        // The base's recorder (made with the base as the document's layer, so it records the base), the shown layer kept.
        private static StrokeRecorder BaseOf(BlenderEdit edit)
        {
            if (edit.Base != null) return edit.Base;
            var doc = edit.Target.Document;
            string shown = doc.Layer;
            doc.Layer = null;
            edit.Base = new StrokeRecorder(edit.Target, "Blender edit of " + edit.Target.Name);
            doc.Layer = shown;
            return edit.Base;
        }

        private static DeltaRecorder ShapeOf(BlenderEdit edit, string shape)
        {
            if (!edit.Shapes.TryGetValue(shape, out var recorder))
                edit.Shapes[shape] = recorder = new DeltaRecorder(edit.Target, shape, "Blender edit of " + edit.Target.Name + ": " + shape);
            return recorder;
        }

        private static WeightRecorder WeightsOf(BlenderEdit edit, string group)
        {
            string key = group == null ? "" : "g:" + group;
            if (!edit.Weights.TryGetValue(key, out var recorder))
            {
                recorder = new WeightRecorder(edit.Target, group, group == null ? "Blender weights of " + edit.Target.Name : "Blender group " + group);
                edit.Weights[key] = recorder;
            }
            return recorder;
        }

        // A shape Blender has that Unity does not (never one Unity deleted: the callers skip those): made, with an entry so
        // the undo takes it away again.
        private static void EnsureShape(BlenderEdit edit, string name)
        {
            var doc = edit.Target.Document;
            if (doc.HasShape(name)) return;
            string before = doc.Layer;
            var layer = doc.Shape(name, create: true);
            doc.Revision++;
            edit.Created.Add(new ShapeListEdit { Target = edit.Target, Label = "Blender: new shape " + name, Name = name, Layer = layer, Created = true, LayerBefore = before });
        }

        private static VertexGroup EnsureGroup(BlenderEdit edit, string name)
        {
            var doc = edit.Target.Document;
            var group = doc.Group(name);
            if (group != null || string.IsNullOrEmpty(name)) return group;
            var before = new List<VertexGroup>(doc.Groups);
            group = new VertexGroup { Name = name };
            doc.Groups.Add(group);
            doc.Revision++;
            edit.Created.Add(new GroupListEdit { Target = edit.Target, Label = "Blender: new group " + name, Before = before, After = new List<VertexGroup>(doc.Groups) });
            return group;
        }

        public static void Positions(MixerLinkHost host, MeshDataCodec.PositionsFrame frame)
        {
            foreach (var target in TargetsOf(host, frame.Id, frame.Topology, frame.Positions.Length))
            {
                var doc = target.Document;
                var n = doc.LinkWeld;
                // Blender's in-between keys ("Smile @50") are frames of a shape, which stay in Blender (its Export for MCB
                // writes them); Unity edits a shape's last frame.
                if (frame.Shape != null && MeshDataCodec.IsInBetweenOf(doc, frame.Shape))
                {
                    host.StatusThrottled(target.Id, frame.Shape + " is an in-between frame: it stays in Blender (Export for MCB writes it), Unity edits " + MeshDataCodec.InBetweenBase(frame.Shape) + "'s last frame.", "warning");
                    continue;
                }
                // As for a whole mesh: a shape Unity deleted or renamed meanwhile is not brought back.
                if (frame.Shape != null && doc.DeletedShapes.Contains(frame.Shape))
                {
                    host.StatusThrottled(target.Id, "Unity deleted or renamed " + frame.Shape + " meanwhile: Blender's edits of it are not brought back.", "info");
                    continue;
                }
                var edit = EditOf(target, frame.Id);
                if (frame.Shape != null) EnsureShape(edit, frame.Shape);
                // Unity shows the layer Blender edits.
                if (doc.Layer != frame.Shape) { doc.Layer = frame.Shape; target.AfterLayerChange(); }
                var baseRecorder = frame.Shape == null ? BaseOf(edit) : null;
                var shapeRecorder = frame.Shape != null ? ShapeOf(edit, frame.Shape) : null;
                var moved = new List<int>();
                for (int w = 0; w < n.WeldedCount; w++)
                {
                    var p = frame.Positions[w];
                    foreach (int v in n.Members[w])
                    {
                        if (doc.GetLayerPosition(frame.Shape, v) == p) continue;
                        if (baseRecorder != null) baseRecorder.Touch(v); else shapeRecorder.Touch(v);
                        doc.SetLayerPosition(frame.Shape, v, p);
                        moved.Add(v);
                    }
                }
                if (moved.Count == 0) continue;
                doc.MarkEdited(moved);
                target.AfterEdit(moved);
                target.RefreshNormals(moved);
                MixerSession.NoteEdited(target);
            }
        }

        /// <summary>
        /// A whole mesh from Blender (sent when it leaves an edit mode, or with Send to Unity): base positions, every
        /// shape's deltas, the skin and the groups, recorded into the edit that ends with Blender's release. Shapes and
        /// groups Blender was sent and no longer has are deleted; ones Unity deleted or renamed meanwhile are not brought
        /// back. The layer Unity shows stays.
        /// </summary>
        public static void Mesh(MixerLinkHost host, MeshDataCodec.Decoded data)
        {
            // What Blender had before this mesh (what it deleted is what it had and no longer sends).
            var sentShapes = host.SentShapes(data.Id)?.ToList();
            var sentGroups = host.SentGroups(data.Id)?.ToList();
            HashSet<string> blenderShapes = null, blenderGroups = null;
            foreach (var target in TargetsOf(host, data.Id, data.Topology, data.Vertices.Length))
            {
                var doc = target.Document;
                var n = doc.LinkWeld;
                var edit = EditOf(target, data.Id);
                var touched = new HashSet<int>();

                var baseRecorder = BaseOf(edit);
                for (int w = 0; w < n.WeldedCount; w++)
                {
                    var p = data.Vertices[w];
                    foreach (int v in n.Members[w])
                    {
                        if (doc.Positions[v] == p) continue;
                        baseRecorder.Touch(v);
                        doc.SetLayerPosition(null, v, p);
                        touched.Add(v);
                    }
                }

                var named = new HashSet<string>();
                var notBack = new List<string>();
                bool shapesChanged = false;
                foreach (var (name, indices, deltas) in data.Shapes)
                {
                    if (string.IsNullOrEmpty(name) || MeshDataCodec.IsInBetweenOf(doc, name)) continue;
                    named.Add(name);
                    if (doc.DeletedShapes.Contains(name)) { notBack.Add(name); continue; }
                    if (!doc.HasShape(name)) { EnsureShape(edit, name); shapesChanged = true; }
                    var layer = doc.Shape(name);
                    var full = new Vector3[n.WeldedCount];
                    for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < full.Length) full[indices[i]] = deltas[i];
                    var recorder = ShapeOf(edit, name);
                    for (int w = 0; w < n.WeldedCount; w++)
                        foreach (int v in n.Members[w])
                        {
                            if (layer.Deltas[v] == full[w]) continue;
                            recorder.Touch(v);
                            layer.Deltas[v] = full[w];
                            layer.Edited = true;
                            doc.Dirty.Add(v);
                            touched.Add(v);
                        }
                }
                // Shapes Blender was sent and deleted.
                if (sentShapes != null)
                    foreach (string name in sentShapes)
                    {
                        if (named.Contains(name) || !doc.HasShape(name)) continue;
                        var layer = doc.Shape(name);
                        string before = doc.Layer;
                        doc.DeleteShape(name);
                        edit.Removed.Add(new ShapeListEdit { Target = target, Label = "Blender: delete shape " + name, Name = name, Layer = layer, Created = false, LayerBefore = before });
                        shapesChanged = true;
                    }

                if (data.Influences != null && doc.HasSkin && data.Bones != null)
                {
                    var bones = target.Skinned != null ? target.Skinned.bones : null;
                    var boneIndex = new int[data.Bones.Length];
                    for (int b = 0; b < data.Bones.Length; b++) boneIndex[b] = MeshDataCodec.ResolveBone(bones, b, data.Bones[b]);
                    var recorder = WeightsOf(edit, null);
                    bool any = false;
                    for (int w = 0; w < n.WeldedCount && w < data.Influences.Length; w++)
                    {
                        var list = new List<MeshDocument.Influence>();
                        foreach (var inf in data.Influences[w])
                            if (inf.Bone >= 0 && inf.Bone < boneIndex.Length && boneIndex[inf.Bone] >= 0 && inf.Weight > 0f) list.Add(new MeshDocument.Influence(boneIndex[inf.Bone], inf.Weight));
                        foreach (int v in n.Members[w])
                        {
                            if (SameInfluences(doc.EnsureInfluences()[v], list)) continue;
                            recorder.Touch(v);
                            doc.SetInfluences(v, list.ToArray());
                            any = true;
                        }
                    }
                    if (any) target.FlushWeights(force: true);
                }

                var groupNames = new HashSet<string>();
                bool groupsChanged = false;
                foreach (var (name, indices, weights) in data.Groups)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    groupNames.Add(name);
                    var group = EnsureGroup(edit, name);
                    var recorder = WeightsOf(edit, name);
                    var full = new float[n.WeldedCount];
                    for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < full.Length) full[indices[i]] = weights[i];
                    for (int w = 0; w < n.WeldedCount; w++)
                        foreach (int v in n.Members[w])
                        {
                            float current = group.Weights.TryGetValue(v, out float cw) ? cw : 0f;
                            if (Mathf.Abs(current - full[w]) < 1e-6f) continue;
                            recorder.Touch(v);
                            if (full[w] > 0f) group.Weights[v] = full[w]; else group.Weights.Remove(v);
                            groupsChanged = true;
                        }
                }
                if (sentGroups != null)
                    foreach (string name in sentGroups)
                    {
                        var group = groupNames.Contains(name) ? null : doc.Group(name);
                        if (group == null) continue;
                        var before = new List<VertexGroup>(doc.Groups);
                        doc.Groups.Remove(group);
                        doc.Revision++;
                        edit.Removed.Add(new GroupListEdit { Target = target, Label = "Blender: delete group " + name, Before = before, After = new List<VertexGroup>(doc.Groups) });
                        groupsChanged = true;
                    }

                if (touched.Count > 0)
                {
                    var list = touched.ToList();
                    doc.MarkEdited(list);
                    target.AfterEdit(list);
                    target.RefreshNormals(list);
                }
                if (shapesChanged) target.AfterLayerChange();
                if (groupsChanged) { doc.Revision++; target.GroupsChanged(); }
                if (notBack.Count > 0)
                    host.Status(target.Id, "Unity deleted or renamed " + string.Join(", ", notBack.Take(5)) + (notBack.Count > 5 ? " and " + (notBack.Count - 5) + " more" : "") + " meanwhile: Blender's older copies are not brought back.", "info");
                blenderShapes = named;
                blenderGroups = groupNames;
                MixerSession.NoteEdited(target);
            }
            // What Blender holds now: when Unity's shapes or groups differ from it, Blender gets the mesh again.
            if (blenderShapes != null) host.NoteBlenderHas(data.Id, blenderShapes, blenderGroups);
        }

        private static bool SameInfluences(List<MeshDocument.Influence> a, List<MeshDocument.Influence> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < b.Count && !found; j++) found = b[j].Bone == a[i].Bone && Mathf.Abs(b[j].Weight - a[i].Weight) < 1e-5f;
                if (!found) return false;
            }
            return true;
        }

        /// <summary>
        /// Live weights of one channel (only the vertices Blender changed): a group's, or a bone's (found by its index in
        /// the bone list Blender was sent, then by name). Blender's values are kept as they are: Unity normalizes them when
        /// it uploads the skin and on Commit, like every influence.
        /// </summary>
        public static void Weights(MixerLinkHost host, MeshDataCodec.WeightsFrame frame)
        {
            foreach (var target in TargetsOf(host, frame.Id, frame.Topology, -1))
            {
                var doc = target.Document;
                var n = doc.LinkWeld;
                var edit = EditOf(target, frame.Id);
                bool any = false;
                if (frame.IsGroup)
                {
                    var group = EnsureGroup(edit, frame.Channel);
                    if (group == null) continue;
                    var recorder = WeightsOf(edit, frame.Channel);
                    for (int i = 0; i < frame.Indices.Length; i++)
                    {
                        int w = frame.Indices[i];
                        if (w < 0 || w >= n.WeldedCount) continue;
                        float value = frame.Weights[i];
                        foreach (int v in n.Members[w])
                        {
                            float current = group.Weights.TryGetValue(v, out float cw) ? cw : 0f;
                            if (Mathf.Abs(current - value) < 1e-6f) continue;
                            recorder.Touch(v);
                            if (value > 0f) group.Weights[v] = value; else group.Weights.Remove(v);
                            any = true;
                        }
                    }
                    if (any) target.GroupsChanged();
                }
                else
                {
                    var bones = target.Skinned != null ? target.Skinned.bones : null;
                    int bone = MeshDataCodec.ResolveBone(bones, frame.Bone, frame.Channel);
                    if (bone < 0) { host.StatusThrottled(target.Id, "No bone named " + frame.Channel + " on " + target.Name + ".", "warning"); continue; }
                    var recorder = WeightsOf(edit, null);
                    for (int i = 0; i < frame.Indices.Length; i++)
                    {
                        int w = frame.Indices[i];
                        if (w < 0 || w >= n.WeldedCount) continue;
                        float value = Mathf.Clamp01(frame.Weights[i]);
                        foreach (int v in n.Members[w])
                        {
                            if (Mathf.Abs(doc.BoneWeight(v, bone) - value) < 1e-6f) continue;
                            recorder.Touch(v);
                            doc.SetBoneWeight(v, bone, value, normalize: false);
                            any = true;
                        }
                    }
                    if (any) target.FlushWeights(force: true);
                }
                if (!any) continue;
                doc.Revision++;
                MixerSession.NoteEdited(target);
            }
        }

        /// <summary>
        /// Blender released a mesh: what it changed becomes history entries, all in one Unity undo step (new shapes and
        /// groups first, then positions and weights, then what it deleted, so an undo takes them back in reverse).
        /// </summary>
        public static void EndBlenderEdit(string meshId)
        {
            foreach (var pair in edits.ToList())
            {
                var edit = pair.Value;
                if (edit.MeshId != meshId) continue;
                edits.Remove(pair.Key);
                if (!edit.Target.Alive || !MixerSession.Targets.Contains(edit.Target)) continue;
                var entries = EntriesOf(edit, out var moved);
                if (moved.Count > 0) edit.Target.FinishStroke(moved);
                if (entries.Count == 0) continue;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Mixer: Blender edit of " + edit.Target.Name);
                foreach (var entry in entries) MixerHistory.Push(entry, joinCurrentGroup: true);
                Undo.IncrementCurrentGroup();
                MixerSession.NoteEdited(edit.Target);
            }
        }

        // What a Blender edit changed, as history entries in the order they are pushed (new shapes and groups, positions,
        // deltas, weights, then what it deleted), and the vertices it moved.
        private static List<HistoryEntry> EntriesOf(BlenderEdit edit, out List<int> moved)
        {
            var entries = new List<HistoryEntry>(edit.Created);
            moved = new List<int>();
            var positions = edit.Base?.Finish();
            if (positions != null) { entries.Add(positions); moved.AddRange(positions.Indices); }
            foreach (var recorder in edit.Shapes.Values)
            {
                var deltas = recorder.Finish();
                if (deltas == null) continue;
                entries.Add(deltas);
                moved.AddRange(deltas.Indices);
            }
            foreach (var recorder in edit.Weights.Values)
            {
                var weights = recorder.Finish();
                if (weights != null) entries.Add(weights);
            }
            entries.AddRange(edit.Removed);
            return entries;
        }

        /// <summary>The mesh's edit ends without history (its session was committed or reverted in Unity meanwhile).</summary>
        public static void DropBlenderEdit(string meshId)
        {
            foreach (var pair in edits.ToList())
                if (pair.Value.MeshId == meshId) edits.Remove(pair.Key);
        }

        /// <summary>
        /// The mesh's edit ends without history, and what Blender changed in its documents so far is taken back (Mixer's
        /// Undo or Redo is about to change them: they are then what their history says; Blender keeps its own copy).
        /// </summary>
        public static void RevertBlenderEdit(string meshId)
        {
            foreach (var pair in edits.ToList())
            {
                var edit = pair.Value;
                if (edit.MeshId != meshId) continue;
                edits.Remove(pair.Key);
                if (!edit.Target.Alive || !MixerSession.Targets.Contains(edit.Target)) continue;
                var entries = EntriesOf(edit, out _);
                for (int i = entries.Count - 1; i >= 0; i--) entries[i].Undo();
            }
        }

        /// <summary>Transforms, names and active flags Blender changed, with Undo.</summary>
        public static void Delta(SceneDelta delta)
        {
            if (delta == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Blender: scene change");
            foreach (var t in delta.transforms ?? new List<SceneNodeTransform>())
            {
                var go = SceneGraphExporter.Resolve(t.id) as GameObject;
                if (go == null) continue;
                Undo.RecordObject(go.transform, "Blender: move " + go.name);
                go.transform.localPosition = SceneGraphExporter.ToVector(t.p);
                go.transform.localRotation = SceneGraphExporter.ToQuaternion(t.r);
                go.transform.localScale = t.s != null ? SceneGraphExporter.ToVector(t.s) : go.transform.localScale;
            }
            foreach (var r in delta.renamed ?? new List<SceneNodeName>())
            {
                var go = SceneGraphExporter.Resolve(r.id) as GameObject;
                if (go == null || string.IsNullOrEmpty(r.name)) continue;
                Undo.RecordObject(go, "Blender: rename");
                go.name = r.name;
            }
            foreach (var a in delta.active ?? new List<SceneNodeFlag>())
            {
                var go = SceneGraphExporter.Resolve(a.id) as GameObject;
                if (go == null) continue;
                Undo.RecordObject(go, "Blender: " + (a.active ? "show" : "hide"));
                go.SetActive(a.active);
            }
            foreach (var p in delta.reparented ?? new List<SceneNodeParent>())
            {
                var go = SceneGraphExporter.Resolve(p.id) as GameObject;
                var parent = p.parent != null ? SceneGraphExporter.Resolve(p.parent) as GameObject : null;
                if (go == null || (p.parent != null && parent == null)) continue;
                Undo.SetTransformParent(go.transform, parent != null ? parent.transform : null, "Blender: reparent");
            }
        }
    }

    /// <summary>A shape's deltas changed (from Blender): before and after per vertex, whatever the base does meanwhile.</summary>
    internal sealed class ShapeDeltaEdit : HistoryEntry
    {
        public string Shape;
        public int[] Indices;
        public Vector3[] Before, After;

        public override long Bytes => Indices.Length * (4L + 24L) + 64;
        public override void Undo() => Apply(Before);
        public override void Redo() => Apply(After);

        private void Apply(Vector3[] deltas)
        {
            var doc = Target.Document;
            if (!doc.HasShape(Shape)) doc.RestoreShape(Shape, null);
            var layer = doc.Shape(Shape, create: true);
            for (int i = 0; i < Indices.Length; i++) layer.Deltas[Indices[i]] = deltas[i];
            layer.Edited = true;
            doc.MarkEdited(Indices);
            Target.AfterEdit(Indices, fullRefresh: true);
            Target.RefreshNormals(Indices);
        }
    }

    /// <summary>Records the deltas of one shape an edit touches: the first touch of a vertex keeps its delta before.</summary>
    internal sealed class DeltaRecorder
    {
        private readonly Dictionary<int, Vector3> before = new Dictionary<int, Vector3>();
        private readonly EditTarget target;
        private readonly string shape;
        private readonly string label;

        public DeltaRecorder(EditTarget target, string shape, string label) { this.target = target; this.shape = shape; this.label = label; }

        public void Touch(int v)
        {
            if (before.ContainsKey(v)) return;
            var layer = target.Document.Shape(shape, create: true);
            before[v] = layer.Deltas[v];
        }

        public ShapeDeltaEdit Finish()
        {
            if (before.Count == 0) return null;
            var layer = target.Document.Shape(shape, create: true);
            var edit = new ShapeDeltaEdit { Target = target, Label = label, Shape = shape, Indices = new int[before.Count], Before = new Vector3[before.Count], After = new Vector3[before.Count] };
            int i = 0;
            foreach (var pair in before)
            {
                edit.Indices[i] = pair.Key;
                edit.Before[i] = pair.Value;
                edit.After[i] = layer.Deltas[pair.Key];
                i++;
            }
            return edit;
        }
    }
}
