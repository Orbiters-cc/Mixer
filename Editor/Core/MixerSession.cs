using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The meshes being edited in this editor: each shows a working copy on its renderer while its document takes the
    /// edits, flushed to the copy once a frame. Nothing is written to assets until <see cref="Commit"/>, which saves
    /// the result as a native mesh asset; <see cref="Revert"/> puts the original back. The list survives script
    /// reloads (the working meshes do, the documents are rebuilt from them and the autosave).
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerSession
    {
        private const string StateKey = "Orbiters.Mixer.Session";
        private static readonly List<EditTarget> targets = new List<EditTarget>();
        private static readonly HashSet<string> pendingRestore = new HashSet<string>();
        private static double lastAutosave;
        private static bool dirtySinceAutosave;
        private static bool restoring;

        /// <summary>Raised when a target is added, committed or reverted, or an edit changed what the window shows.</summary>
        public static event Action Changed;
        /// <summary>Short messages for the window's toasts (text, isError).</summary>
        public static event Action<string, bool> Notice;

        static MixerSession()
        {
            EditorApplication.update += Tick;
            EditorSceneManager.sceneSaving += (scene, path) => { foreach (var t in targets) if (t.Alive) t.Show(t.Original); };
            EditorSceneManager.sceneSaved += scene => { foreach (var t in targets) if (t.Alive) t.Show(t.Working); };
            AssemblyReloadEvents.beforeAssemblyReload += SaveState;
            MixerHistory.Notice += text => Notice?.Invoke(text, false);
            LoadState();
        }

        public static IReadOnlyList<EditTarget> Targets => targets;
        public static bool Active => targets.Count > 0;
        public static EditTarget First => targets.Count > 0 ? targets[0] : null;

        public static EditTarget TargetOf(Renderer renderer)
        {
            foreach (var t in targets) if (t.Renderer == renderer) return t;
            return null;
        }

        /// <summary>Whether <paramref name="renderer"/> can be edited: a skinned or static mesh renderer with a readable mesh.</summary>
        public static bool CanEdit(Renderer renderer, out string reason)
        {
            reason = null;
            var mesh = MeshOf(renderer);
            if (mesh == null) { reason = "No mesh."; return false; }
            if (MixerMeshGuard.IsWorking(mesh)) return true;
            if (!mesh.isReadable) { reason = "The mesh is not readable: turn on Read/Write in its import settings."; return false; }
            if (mesh.vertexCount == 0) { reason = "The mesh has no vertices."; return false; }
            return true;
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (renderer is MeshRenderer) return renderer.GetComponent<MeshFilter>()?.sharedMesh;
            return null;
        }

        /// <summary>Starts editing <paramref name="renderer"/> (or returns its target when it already is).</summary>
        public static EditTarget Edit(Renderer renderer)
        {
            var existing = TargetOf(renderer);
            if (existing != null) return existing;
            if (!CanEdit(renderer, out string reason)) throw new InvalidOperationException(reason);
            var shown = MeshOf(renderer);
            var original = MixerMeshGuard.OriginalOf(shown) ?? shown;
            Mesh working;
            if (original != shown) working = shown;
            else
            {
                working = Object.Instantiate(original);
                working.name = original.name + " (Mixer working)";
                working.hideFlags = HideFlags.HideAndDontSave;
            }
            var target = new EditTarget(renderer, original, working);
            if (original == shown) target.Show(working);
            MixerMeshGuard.Shown(working, original);
            MixerSidecar.Load(original, target.Document);
            targets.Add(target);
            SaveState();
            MixerLinkService.ClaimForUnity(target);
            Changed?.Invoke();
            return target;
        }

        // ---- Vertex groups ----------------------------------------------------------------------------------------

        public static VertexGroup AddGroup(EditTarget target, string name)
        {
            if (target == null) return null;
            var doc = target.Document;
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            if (doc.Group(name) != null) { Say("A group named " + name + " exists already.", true); return doc.Group(name); }
            var before = new List<VertexGroup>(doc.Groups);
            var group = new VertexGroup { Name = name };
            doc.Groups.Add(group);
            doc.Revision++;
            MixerHistory.Push(new GroupListEdit { Target = target, Label = "New group " + name, Before = before, After = new List<VertexGroup>(doc.Groups) });
            NoteEdited(target);
            return group;
        }

        public static void RemoveGroup(EditTarget target, string name)
        {
            if (target == null) return;
            var doc = target.Document;
            var group = doc.Group(name);
            if (group == null) return;
            var before = new List<VertexGroup>(doc.Groups);
            doc.Groups.Remove(group);
            doc.Revision++;
            MixerHistory.Push(new GroupListEdit { Target = target, Label = "Remove group " + name, Before = before, After = new List<VertexGroup>(doc.Groups) });
            if (target.ShownChannel.IsGroup && target.ShownChannel.Group == name) target.ShowChannel(WeightChannel.None);
            NoteEdited(target);
        }

        public static void RenameGroup(EditTarget target, string from, string to)
        {
            if (target == null) return;
            var doc = target.Document;
            var group = doc.Group(from);
            to = (to ?? "").Trim();
            if (group == null || to.Length == 0 || to == from) return;
            if (doc.Group(to) != null) { Say("A group named " + to + " exists already.", true); return; }
            var before = new List<VertexGroup>(doc.Groups);
            var renamed = new VertexGroup { Name = to };
            foreach (var pair in group.Weights) renamed.Weights[pair.Key] = pair.Value;
            doc.Groups[doc.Groups.IndexOf(group)] = renamed;
            doc.Revision++;
            MixerHistory.Push(new GroupListEdit { Target = target, Label = "Rename group " + from, Before = before, After = new List<VertexGroup>(doc.Groups) });
            if (target.ShownChannel.IsGroup && target.ShownChannel.Group == from) target.ShowChannel(WeightChannel.OfGroup(to));
            NoteEdited(target);
        }

        /// <summary>Puts the original back and drops every edit of the target.</summary>
        public static void Revert(EditTarget target)
        {
            if (target == null || !targets.Contains(target)) return;
            MixerLinkService.ReleaseForUnity(target);
            MixerHistory.Forget(target);
            if (target.Renderer != null)
            {
                target.ShowChannel(WeightChannel.None);
                target.RestoreWeights();
                target.Show(target.Original);
            }
            MixerMeshGuard.Released(target.Working);
            if (target.Working != null) Object.DestroyImmediate(target.Working);
            MixerAutosave.Delete(target.Id);
            targets.Remove(target);
            SaveState();
            Changed?.Invoke();
        }

        /// <summary>Builds the final mesh, saves it as an asset and shows it on the renderer, in one Undo step.</summary>
        public static Mesh Commit(EditTarget target, string owner = null)
        {
            if (target == null || !targets.Contains(target) || !target.Alive) return null;
            var doc = target.Document;
            if (!doc.Changed)
            {
                Revert(target);
                return target.Original;
            }
            string name = target.Original.name;
            if (MixerMeshGuard.IsWorking(target.Original)) name = name.Replace(" (Mixer working)", "");
            owner = owner ?? OwnerName(target.Renderer);
            var built = MeshBuilder.Build(target);
            var asset = MeshAssets.Save(built, MeshAssets.Folder("Mixer", owner), name);
            MixerSidecar.Save(AssetDatabase.GetAssetPath(asset), doc);
            target.ShowChannel(WeightChannel.None);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: Commit " + name);
            target.RestoreWeights();
            Object holder = target.Filter != null ? (Object)target.Filter : target.Renderer;
            Undo.RecordObject(holder, "Mixer: Commit " + name);
            target.Show(asset);
            EditorUtility.SetDirty(holder);
            if (target.Renderer.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(target.Renderer.gameObject.scene);
            MixerHistory.Forget(target);
            MixerMeshGuard.Released(target.Working);
            Object.DestroyImmediate(target.Working);
            MixerAutosave.Delete(target.Id);
            targets.Remove(target);
            SaveState();
            MixerLinkService.ReleaseForUnity(target);
            AvatarMeshEdits.NotifyApplied(RootOf(target.Renderer));
            Notice?.Invoke("Saved " + AssetDatabase.GetAssetPath(asset), false);
            Changed?.Invoke();
            return asset;
        }

        private static Transform RootOf(Renderer renderer) => renderer != null ? renderer.transform.root : null;

        private static string OwnerName(Renderer renderer)
        {
            if (renderer == null) return "Scene";
            var root = renderer.transform.root;
            return root != null ? root.name : renderer.name;
        }

        /// <summary>Called after a stroke or operation changed a target.</summary>
        public static void NoteEdited(EditTarget target)
        {
            dirtySinceAutosave = true;
            if (!MixerLinkService.IsBlenderOwned(target)) MixerLinkService.NotePositions(target);
            Changed?.Invoke();
        }

        private static void Tick()
        {
            if (restoring) return;
            if (pendingRestore.Count > 0) RestorePending();
            bool any = false;
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                var t = targets[i];
                if (!t.Alive)
                {
                    // The renderer or its working mesh is gone (deleted, scene closed): nothing to show any more.
                    MixerHistory.Forget(t);
                    if (t.Working != null) { MixerMeshGuard.Released(t.Working); Object.DestroyImmediate(t.Working); }
                    targets.RemoveAt(i);
                    SaveState();
                    Changed?.Invoke();
                    continue;
                }
                any |= t.Flush();
            }
            if (any) SceneView.RepaintAll();
            double now = EditorApplication.timeSinceStartup;
            if (dirtySinceAutosave && now - lastAutosave > 3.0 && !MixerSceneTool.Stroking)
            {
                lastAutosave = now;
                dirtySinceAutosave = false;
                foreach (var t in targets) if (t.Alive && t.Document.Changed) MixerAutosave.Write(t);
            }
        }

        // ---- Reloads ---------------------------------------------------------------------------------------------

        private static void SaveState()
        {
            var lines = new List<string>();
            foreach (var t in targets)
            {
                if (!t.Alive) continue;
                lines.Add(t.Id + "\t" + t.Working.GetInstanceID() + "\t" + GlobalObjectId.GetGlobalObjectIdSlow(t.Original) + "\t" + (t.Document.Layer ?? ""));
            }
            SessionState.SetString(StateKey, string.Join("\n", lines));
        }

        private static void LoadState()
        {
            foreach (string line in SessionState.GetString(StateKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                pendingRestore.Add(line);
        }

        // After a script reload the working meshes still show on their renderers (HideAndDontSave objects survive);
        // the documents are rebuilt from the originals and the autosave, and the working meshes refreshed from them.
        private static void RestorePending()
        {
            restoring = true;
            try
            {
                foreach (string line in pendingRestore)
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (!GlobalObjectId.TryParse(parts[0], out var rendererId) || !GlobalObjectId.TryParse(parts[2], out var originalId)) continue;
                    var renderer = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(rendererId) as Renderer;
                    var original = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(originalId) as Mesh;
                    if (renderer == null || original == null) continue;
                    int workingId = int.Parse(parts[1]);
                    var working = EditorUtility.InstanceIDToObject(workingId) as Mesh;
                    if (working == null)
                    {
                        working = Object.Instantiate(original);
                        working.name = original.name + " (Mixer working)";
                        working.hideFlags = HideFlags.HideAndDontSave;
                    }
                    var target = new EditTarget(renderer, original, working);
                    target.Show(working);
                    MixerMeshGuard.Shown(working, original);
                    targets.Add(target);
                    string layer = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
                    if (MixerAutosave.Restore(target)) target.Document.Layer = layer != null && target.Document.HasShape(layer) ? layer : null;
                    target.AfterLayerChange();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                pendingRestore.Clear();
                restoring = false;
                SaveState();
                Changed?.Invoke();
            }
        }

        /// <summary>Picks the layer a target is edited on: null for the base mesh, else a shape (created when new).</summary>
        public static void SetLayer(EditTarget target, string shape, bool createIfMissing)
        {
            if (target == null) return;
            var doc = target.Document;
            if (shape != null && !doc.HasShape(shape))
            {
                if (!createIfMissing) return;
                string before = doc.Layer;
                var layer = doc.Shape(shape, create: true);
                doc.Layer = shape;
                doc.Revision++;
                MixerHistory.Push(new ShapeListEdit { Target = target, Label = "New shape " + shape, Name = shape, Layer = layer, Created = true, LayerBefore = before });
            }
            else doc.Layer = shape;
            target.AfterLayerChange();
            SaveState();
            NoteEdited(target);
        }

        public static void DeleteShape(EditTarget target, string shape)
        {
            if (target == null || !target.Document.HasShape(shape)) return;
            var doc = target.Document;
            var layer = doc.Shape(shape);
            string before = doc.Layer;
            doc.DeleteShape(shape);
            MixerHistory.Push(new ShapeListEdit { Target = target, Label = "Delete shape " + shape, Name = shape, Layer = layer, Created = false, LayerBefore = before });
            target.AfterLayerChange();
            SaveState();
            NoteEdited(target);
        }

        public static void Say(string text, bool error = false) => Notice?.Invoke(text, error);

        // ---- Shape and mask operations -------------------------------------------------------------------------------

        public static void RenameShape(EditTarget target, string from, string to)
        {
            if (target == null) return;
            var doc = target.Document;
            to = (to ?? "").Trim();
            if (!doc.HasShape(from) || to.Length == 0 || to == from) return;
            if (doc.HasShape(to)) { Say("A shape named " + to + " exists already.", true); return; }
            var before = doc.CaptureShapes();
            doc.RenameShape(from, to);
            MixerHistory.Push(new ShapeStateEdit { Target = target, Label = "Rename " + from + " to " + to, Before = before, After = doc.CaptureShapes() });
            target.AfterLayerChange();
            SaveState();
            NoteEdited(target);
        }

        public static void DuplicateShape(EditTarget target, string from, string to)
        {
            if (target == null) return;
            var doc = target.Document;
            to = (to ?? "").Trim();
            if (!doc.HasShape(from) || to.Length == 0) return;
            if (doc.HasShape(to)) { Say("A shape named " + to + " exists already.", true); return; }
            var before = doc.CaptureShapes();
            doc.DuplicateShape(from, to);
            doc.Layer = to;
            MixerHistory.Push(new ShapeStateEdit { Target = target, Label = "Duplicate " + from, Before = before, After = doc.CaptureShapes() });
            target.AfterLayerChange();
            SaveState();
            NoteEdited(target);
        }

        /// <summary>Copies one side of a shape onto the other across the mesh's X axis (<paramref name="fromPositiveX"/>: +X side is the source).</summary>
        public static void MirrorShape(EditTarget target, string name, bool fromPositiveX)
        {
            if (target == null || !target.Document.HasShape(name)) return;
            var doc = target.Document;
            var layer = doc.Shape(name);
            var mirror = target.Mirror;
            var n = doc.Neighbors;
            var original = doc.Original.vertices;
            var indices = new List<int>();
            var before = new List<Vector3>();
            var after = new List<Vector3>();
            for (int w = 0; w < n.WeldedCount; w++)
            {
                int rep = n.Representative[w];
                float x = original[rep].x;
                bool destination = fromPositiveX ? x < -1e-6f : x > 1e-6f;
                if (!destination) continue;
                int source = mirror.Of[w];
                if (source < 0) continue;
                var d = layer.Deltas[n.Representative[source]];
                var flipped = new Vector3(-d.x, d.y, d.z);
                foreach (int v in n.Members[w])
                {
                    if (layer.Deltas[v] == flipped) continue;
                    indices.Add(v);
                    before.Add(doc.Positions[v] + layer.Deltas[v]);
                    after.Add(doc.Positions[v] + flipped);
                    layer.Deltas[v] = flipped;
                }
            }
            if (indices.Count == 0) { Say("Both sides of " + name + " already match.", false); return; }
            layer.Edited = true;
            string previousLayer = doc.Layer;
            doc.Layer = name;
            doc.MarkEdited(indices);
            MixerHistory.Push(new PositionEdit { Target = target, Label = "Mirror " + name + (fromPositiveX ? " +X to -X" : " -X to +X"), Layer = name, Indices = indices.ToArray(), Before = before.ToArray(), After = after.ToArray() });
            if (previousLayer != name) target.AfterLayerChange(); else { target.AfterEdit(indices, fullRefresh: true); target.RecomputeAllNormals(); }
            SaveState();
            NoteEdited(target);
        }

        public static void ClearMask(EditTarget target) => SetMask(target, _ => 0f, "Clear mask");
        public static void InvertMask(EditTarget target) => SetMask(target, m => 1f - m, "Invert mask");

        private static void SetMask(EditTarget target, Func<float, float> change, string label)
        {
            if (target == null) return;
            var doc = target.Document;
            var mask = doc.EnsureMask();
            var indices = new List<int>(); var before = new List<float>(); var after = new List<float>();
            for (int v = 0; v < mask.Length; v++)
            {
                float next = Mathf.Clamp01(change(mask[v]));
                if (next == mask[v]) continue;
                indices.Add(v); before.Add(mask[v]); after.Add(next);
                mask[v] = next;
            }
            if (indices.Count == 0) return;
            doc.Revision++;
            MixerHistory.Push(new MaskEdit { Target = target, Label = label, Indices = indices.ToArray(), Before = before.ToArray(), After = after.ToArray() });
            NoteEdited(target);
            SceneView.RepaintAll();
        }
    }
}
