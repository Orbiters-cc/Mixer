using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Puts what Blender sends into the scene: positions and meshes of claimed objects go into their Mixer documents
    /// (shown at once; one history entry when Blender releases the object), weights and groups likewise, transforms
    /// and names onto the objects with Unity's Undo. Blender's vertices are the welded ones: each lands on every
    /// Unity vertex sharing its position.
    /// </summary>
    internal static class LinkApply
    {
        private sealed class BlenderEdit
        {
            public EditTarget Target;
            public StrokeRecorder Positions;
            public WeightRecorder Weights;
            public readonly List<int> Touched = new List<int>();
            public string Layer;
        }

        private static readonly Dictionary<string, BlenderEdit> edits = new Dictionary<string, BlenderEdit>();

        /// <summary>Every renderer showing a mesh with this mesh id, as edit targets (started when needed).</summary>
        private static List<EditTarget> TargetsOfMesh(string meshId)
        {
            var result = new List<EditTarget>();
            foreach (var target in MixerSession.Targets)
                if (target.Alive && MixerLinkService.MeshIdOf(target.Renderer) == meshId) result.Add(target);
            if (result.Count > 0) return result;
            var mesh = SceneGraphExporter.Resolve(meshId) as Mesh;
            if (mesh == null) return result;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                var shown = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if ((MixerMeshGuard.OriginalOf(shown) ?? shown) != mesh) continue;
                if (!MixerSession.CanEdit(renderer, out _)) continue;
                try { result.Add(MixerSession.Edit(renderer)); } catch (Exception) { }
                break; // one working copy per mesh asset is enough: the others share it on commit
            }
            return result;
        }

        private static BlenderEdit EditOf(EditTarget target)
        {
            if (!edits.TryGetValue(target.Id, out var edit))
            {
                edit = new BlenderEdit { Target = target, Layer = target.Document.Layer };
                edits[target.Id] = edit;
            }
            return edit;
        }

        public static void Positions(MeshDataCodec.PositionsFrame frame)
        {
            foreach (var target in TargetsOfMesh(frame.Id))
            {
                var doc = target.Document;
                var n = doc.Neighbors;
                if (frame.Positions.Length != n.WeldedCount)
                {
                    MixerLinkService.Status(target.Id, "Blender's mesh has " + frame.Positions.Length + " vertices, Unity's " + n.WeldedCount + ": the topology changed, which the link does not apply yet. Commit the mesh in Blender and export it as FBX instead.", "error");
                    continue;
                }
                var edit = EditOf(target);
                if (frame.Shape != null && !doc.HasShape(frame.Shape)) doc.Shape(frame.Shape, create: true);
                if (doc.Layer != frame.Shape) { doc.Layer = frame.Shape; target.AfterLayerChange(); }
                if (edit.Positions == null || edit.Layer != frame.Shape) { edit.Positions = new StrokeRecorder(target, "Blender edit of " + target.Name); edit.Layer = frame.Shape; }
                var moved = new List<int>();
                for (int w = 0; w < n.WeldedCount; w++)
                {
                    var p = frame.Positions[w];
                    foreach (int v in n.Members[w])
                    {
                        if (doc.GetLayerPosition(frame.Shape, v) == p) continue;
                        edit.Positions.Touch(v);
                        doc.SetLayerPosition(frame.Shape, v, p);
                        moved.Add(v);
                    }
                }
                if (moved.Count == 0) continue;
                doc.MarkEdited(moved);
                target.AfterEdit(moved);
                doc.RefreshNormals(moved, target.Display);
                MixerSession.NoteEdited(target);
            }
        }

        public static void Mesh(MeshDataCodec.Decoded data)
        {
            foreach (var target in TargetsOfMesh(data.Id))
            {
                var doc = target.Document;
                var n = doc.Neighbors;
                if (data.Vertices.Length != n.WeldedCount)
                {
                    MixerLinkService.Status(target.Id, "Blender's mesh has " + data.Vertices.Length + " vertices, Unity's " + n.WeldedCount + ": the topology changed, which the link does not apply yet.", "error");
                    continue;
                }
                Positions(new MeshDataCodec.PositionsFrame { Id = data.Id, Shape = null, Positions = data.Vertices });
                // Shapes: each known shape's deltas, new shapes created.
                foreach (var (name, indices, deltas) in data.Shapes)
                {
                    var layer = doc.Shape(name, create: true);
                    var full = new Vector3[n.WeldedCount];
                    for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < full.Length) full[indices[i]] = deltas[i];
                    var edit = EditOf(target);
                    var recorder = new StrokeRecorder(target, "Blender shape " + name);
                    var changed = new List<int>();
                    string previous = doc.Layer;
                    doc.Layer = name;
                    for (int w = 0; w < n.WeldedCount; w++)
                        foreach (int v in n.Members[w])
                        {
                            if (layer.Deltas[v] == full[w]) continue;
                            recorder.Touch(v);
                            doc.SetLayerPosition(name, v, doc.Positions[v] + full[w]);
                            changed.Add(v);
                        }
                    doc.Layer = previous;
                    if (changed.Count > 0)
                    {
                        doc.MarkEdited(changed);
                        var entry = recorder.Finish();
                        if (entry != null) MixerHistory.Push(entry);
                    }
                }
                if (data.Influences != null && doc.HasSkin && data.Bones != null)
                {
                    var skinned = target.Skinned;
                    var boneIndex = new int[data.Bones.Length];
                    for (int b = 0; b < data.Bones.Length; b++) boneIndex[b] = skinned != null ? Array.FindIndex(skinned.bones, t => t != null && t.name == data.Bones[b]) : -1;
                    var recorder = new WeightRecorder(target, null, "Blender weights of " + target.Name);
                    for (int w = 0; w < n.WeldedCount; w++)
                    {
                        var list = new List<MeshDocument.Influence>();
                        foreach (var inf in data.Influences[w]) if (inf.Bone >= 0 && inf.Bone < boneIndex.Length && boneIndex[inf.Bone] >= 0 && inf.Weight > 0f) list.Add(new MeshDocument.Influence(boneIndex[inf.Bone], inf.Weight));
                        foreach (int v in n.Members[w])
                        {
                            if (SameInfluences(doc.EnsureInfluences()[v], list)) continue;
                            recorder.Touch(v);
                            doc.SetInfluences(v, list.ToArray());
                        }
                    }
                    var entry = recorder.Finish();
                    if (entry != null) { target.FlushWeights(force: true); MixerHistory.Push(entry); }
                }
                foreach (var (name, indices, weights) in data.Groups)
                {
                    var group = doc.Group(name) ?? MixerSession.AddGroup(target, name);
                    if (group == null) continue;
                    var recorder = new WeightRecorder(target, name, "Blender group " + name);
                    var full = new float[n.WeldedCount];
                    for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < full.Length) full[indices[i]] = weights[i];
                    for (int w = 0; w < n.WeldedCount; w++)
                        foreach (int v in n.Members[w])
                        {
                            float current = group.Weights.TryGetValue(v, out float cw) ? cw : 0f;
                            if (Mathf.Abs(current - full[w]) < 1e-6f) continue;
                            recorder.Touch(v);
                            if (full[w] > 0f) group.Weights[v] = full[w]; else group.Weights.Remove(v);
                        }
                    var entry = recorder.Finish();
                    if (entry != null) { target.GroupsChanged(); MixerHistory.Push(entry); }
                }
                MixerSession.NoteEdited(target);
            }
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

        public static void Weights(MeshDataCodec.WeightsFrame frame)
        {
            foreach (var target in TargetsOfMesh(frame.Id))
            {
                var doc = target.Document;
                var n = doc.Neighbors;
                var edit = EditOf(target);
                if (frame.IsGroup)
                {
                    var group = doc.Group(frame.Channel) ?? MixerSession.AddGroup(target, frame.Channel);
                    if (group == null) continue;
                    if (edit.Weights == null) edit.Weights = new WeightRecorder(target, frame.Channel, "Blender group " + frame.Channel);
                    for (int i = 0; i < frame.Indices.Length; i++)
                    {
                        int w = frame.Indices[i];
                        if (w < 0 || w >= n.WeldedCount) continue;
                        foreach (int v in n.Members[w]) { edit.Weights.Touch(v); if (frame.Weights[i] > 0f) group.Weights[v] = frame.Weights[i]; else group.Weights.Remove(v); }
                    }
                    target.GroupsChanged();
                }
                else
                {
                    var skinned = target.Skinned;
                    int bone = skinned != null ? Array.FindIndex(skinned.bones, t => t != null && t.name == frame.Channel) : -1;
                    if (bone < 0) { MixerLinkService.Status(target.Id, "No bone named " + frame.Channel + " on " + target.Name + ".", "warning"); continue; }
                    if (edit.Weights == null) edit.Weights = new WeightRecorder(target, null, "Blender weights of " + target.Name);
                    for (int i = 0; i < frame.Indices.Length; i++)
                    {
                        int w = frame.Indices[i];
                        if (w < 0 || w >= n.WeldedCount) continue;
                        foreach (int v in n.Members[w]) { edit.Weights.Touch(v); doc.SetBoneWeight(v, bone, frame.Weights[i], normalize: false); }
                    }
                    target.FlushWeights(force: true);
                }
                doc.Revision++;
                MixerSession.NoteEdited(target);
            }
        }

        /// <summary>Blender released an object: what it changed becomes history entries.</summary>
        public static void EndBlenderEdit(string nodeId)
        {
            foreach (var pair in edits.ToList())
            {
                var edit = pair.Value;
                if (edit.Target.Id != nodeId && MixerLinkService.MeshIdOf(edit.Target.Renderer) != nodeId) continue;
                edits.Remove(pair.Key);
                if (!edit.Target.Alive) continue;
                var positions = edit.Positions?.Finish();
                if (positions != null) { edit.Target.FinishStroke(positions.Indices); MixerHistory.Push(positions); }
                var weights = edit.Weights?.Finish();
                if (weights != null) MixerHistory.Push(weights);
                MixerSession.NoteEdited(edit.Target);
            }
        }

        /// <summary>Transforms, names and active flags Blender changed, with Undo.</summary>
        public static void Delta(SceneDelta delta)
        {
            if (delta == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Blender: scene change");
            foreach (var t in delta.transforms)
            {
                var go = SceneGraphExporter.Resolve(t.id) as GameObject;
                if (go == null) continue;
                Undo.RecordObject(go.transform, "Blender: move " + go.name);
                go.transform.localPosition = SceneGraphExporter.ToVector(t.p);
                go.transform.localRotation = SceneGraphExporter.ToQuaternion(t.r);
                go.transform.localScale = t.s != null ? SceneGraphExporter.ToVector(t.s) : go.transform.localScale;
            }
            foreach (var r in delta.renamed)
            {
                var go = SceneGraphExporter.Resolve(r.id) as GameObject;
                if (go == null || string.IsNullOrEmpty(r.name)) continue;
                Undo.RecordObject(go, "Blender: rename");
                go.name = r.name;
            }
            foreach (var a in delta.active)
            {
                var go = SceneGraphExporter.Resolve(a.id) as GameObject;
                if (go == null) continue;
                Undo.RecordObject(go, "Blender: " + (a.active ? "show" : "hide"));
                go.SetActive(a.active);
            }
            foreach (var p in delta.reparented)
            {
                var go = SceneGraphExporter.Resolve(p.id) as GameObject;
                var parent = p.parent != null ? SceneGraphExporter.Resolve(p.parent) as GameObject : null;
                if (go == null || (p.parent != null && parent == null)) continue;
                Undo.SetTransformParent(go.transform, parent != null ? parent.transform : null, "Blender: reparent");
            }
        }
    }
}
