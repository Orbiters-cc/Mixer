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
    /// the result as a native mesh asset; <see cref="Revert"/> puts the original back. Sessions survive script reloads,
    /// Play, closing and reopening their scene, a deleted object brought back by Undo, and a crash (see
    /// MixerSession.Lifecycle.cs); scene saves and uploads always get the originals.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class MixerSession
    {
        private static readonly List<EditTarget> targets = new List<EditTarget>();

        /// <summary>Raised when a target is added, committed or reverted, or an edit changed what the window shows.</summary>
        public static event Action Changed;
        /// <summary>Short messages for the window's toasts (text, isError).</summary>
        public static event Action<string, bool> Notice;

        static MixerSession()
        {
            EditorApplication.update += Tick;
            MixerHistory.Notice += text => Notice?.Invoke(text, false);
            MixerMeshGuard.AdoptCopy = AdoptCopy;
            HookLifecycle();
        }

        public static IReadOnlyList<EditTarget> Targets => targets;
        public static bool Active => targets.Count > 0;
        public static EditTarget First => targets.Count > 0 ? targets[0] : null;

        public static EditTarget TargetOf(Renderer renderer)
        {
            if (renderer == null) return null;
            foreach (var t in targets) if (t.Renderer == renderer) return t;
            return null;
        }

        /// <summary>Whether <paramref name="renderer"/> can be edited: a skinned or static mesh renderer with a readable mesh.</summary>
        public static bool CanEdit(Renderer renderer, out string reason)
        {
            reason = null;
            if (renderer == null) { reason = "No renderer."; return false; }
            // A prefab asset's renderer (the Project window): Mixer edits objects of a scene.
            if (EditorUtility.IsPersistent(renderer)) { reason = "Mixer edits meshes in scenes: put the prefab in a scene and edit it there."; return false; }
            // Play mode's objects go when it ends, and the edits with them.
            if (EditorApplication.isPlayingOrWillChangePlaymode) { reason = "Mixer edits meshes outside Play mode: stop Play first."; return false; }
            var mesh = MeshOf(renderer);
            if (mesh == null) { reason = "No mesh."; return false; }
            // Prefab Mode saves the prefab on its own (auto-save): it would write the working copy, which is never saved.
            if (PrefabStageUtility.GetPrefabStage(renderer.gameObject) != null) { reason = "Mixer edits meshes in scenes: close Prefab Mode and edit an instance of the prefab instead."; return false; }
            if (MixerMeshGuard.IsWorking(mesh)) return true;
            if (mesh.vertexCount == 0) { reason = "The mesh has no vertices."; return false; }
            return true;
        }

        // The mesh a renderer shows (a MeshRenderer without a MeshFilter shows none: no fake-null component reached).
        private static Mesh MeshOf(Renderer renderer) => MixerModes.MeshOf(renderer);

        /// <summary>Starts editing <paramref name="renderer"/> (or returns its target when it already is).</summary>
        public static EditTarget Edit(Renderer renderer)
        {
            var existing = TargetOf(renderer);
            if (existing != null) return existing;
            // A renderer left on a working mesh no session shows (an undo, a copy) gets the right mesh first, and a
            // waiting session of it comes back with its edits rather than a new one starting over them.
            if (renderer != null) MixerMeshGuard.RepairUnder(renderer.gameObject);
            if (waiting.Count > 0) TryRecover();
            existing = TargetOf(renderer);
            if (existing != null) return existing;
            if (!CanEdit(renderer, out string reason)) throw new InvalidOperationException(reason);
            existing = ResumeParked(renderer);
            if (existing != null) return existing;
            var shown = MeshOf(renderer);
            // A copy of an edited object shows its source's working mesh: it gets a session of its own with the same edits.
            var owner = targets.FirstOrDefault(t => t.Working == shown);
            if (owner != null && AdoptCopy(renderer, shown)) return TargetOf(renderer);
            var original = MixerMeshGuard.OriginalOf(shown) ?? shown;
            Mesh working;
            if (original != shown) working = shown;
            else working = NewWorking(original);
            var target = new EditTarget(renderer, original, working);
            if (original == shown) target.Show(working);
            MixerMeshGuard.Shown(working, original, renderer);
            MixerSidecar.Load(original, target.Document);
            targets.Add(target);
            SaveState();
            MixerLinkService.ClaimForUnity(target);
            NotePreviewed(target.Renderer);
            Changed?.Invoke();
            return target;
        }

        // Other tools following the avatar's meshes (My Avatar's Painter link): it shows Mixer's working copies now, or
        // its own meshes again. They wait for the edits to pause.
        private static void NotePreviewed(Renderer renderer)
        {
            var root = RootOf(renderer);
            if (root != null) AvatarMeshEdits.NotifyPreviewed(root);
        }

        private static Mesh NewWorking(Mesh from)
        {
            var working = Object.Instantiate(from);
            working.name = from.name.Replace(" (Mixer working)", "") + " (Mixer working)";
            working.hideFlags = HideFlags.HideAndDontSave;
            return working;
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

        // ---- Revert and Commit -------------------------------------------------------------------------------------

        /// <summary>
        /// Puts the original back and drops every edit of the target: a rest-pose edit's bones go back where they were
        /// (rig mates keep their skin where it is, so their rest pose goes back too) and leaf bones it added leave.
        /// </summary>
        public static void Revert(EditTarget target)
        {
            if (target == null || !targets.Contains(target)) return;
            if (target.Renderer != null)
            {
                var moved = RevertRestPose(target);
                RemoveAddedBones(target);
                target.ShowChannel(WeightChannel.None);
                target.RestoreWeights();
                target.Show(target.Original);
                NotePreviewed(target.Renderer);
                // Revert cannot be undone: Unity's steps of the moves it took back (bone drags, their bind poses) go
                // too, else a Ctrl+Z would move those bones again under the original mesh.
                foreach (var bone in moved.bones) if (bone != null) Undo.ClearUndo(bone);
                foreach (var mate in moved.mates) ArmatureEdit.ClearUndoOf(mate);
                ArmatureEdit.ClearUndoOf(target);
            }
            EndSession(target, target.Original, deleteAutosave: true);
        }

        // The bones this target's rest-pose edits moved go back where the original bind poses want them; the rig mates
        // still being edited keep their skin where it is through the move (their bind poses follow, as in rest-pose
        // editing), which gives them their original rest pose back as well.
        private static (List<Transform> bones, List<EditTarget> mates) RevertRestPose(EditTarget target)
        {
            var none = (new List<Transform>(), new List<EditTarget>());
            if (!RestPosePlacement.TryRigOf(target, out var rig)) return none;
            // Mates whose bind poses the same rest-pose edits changed follow; a mesh posed by the move (opened without
            // rest-pose editing) simply goes back with the bones.
            var mates = targets.Where(t => t != target && t.Alive && t.Skinned != null && t.Document.HasSkin && t.Document.BindPosesEdited).ToList();
            var rigs = new List<RestPosePlacement.Rig> { rig };
            rigs.AddRange(RestPosePlacement.RigsOf(mates.Where(m => m.Skinned.bones.Any(b => b != null && rig.Bones.Contains(b)))));
            var place = RestPosePlacement.AsOriginal(rigs);
            if (place.Count == 0) return none;
            var worldsBefore = mates.ToDictionary(m => m, m => m.Skinned.bones.Select(b => b != null ? b.localToWorldMatrix : Matrix4x4.identity).ToArray());
            RestPosePlacement.Apply(place);
            foreach (var mate in mates)
            {
                var bones = mate.Skinned.bones;
                var before = worldsBefore[mate];
                var doc = mate.Document;
                int changed = 0;
                for (int i = 0; i < bones.Length && i < doc.BindPoses.Length; i++)
                {
                    if (bones[i] == null) continue;
                    var after = bones[i].localToWorldMatrix;
                    if (RestPosePlacement.Same(after, before[i]) || after.determinant == 0f) continue;
                    doc.BindPoses[i] = after.inverse * before[i] * doc.BindPoses[i];
                    changed++;
                }
                if (changed == 0) continue;
                doc.BindPosesDirty = true;
                doc.BindPosesEdited = true;
                doc.Revision++;
                ArmatureEdit.StoreBindPoses(mate);
                mate.FlushBindposes();
            }
            foreach (var t in targets) if (t != target) ArmatureEdit.Snapshot(t);
            NoteArmatureEdited();
            return (place.Keys.ToList(), mates);
        }

        // Leaf bones the session added leave the renderer, and the scene when nothing else uses them.
        private static void RemoveAddedBones(EditTarget target)
        {
            var added = ArmatureEdit.AddedBones(target);
            if (added.Count == 0) return;
            var skinned = target.Skinned;
            skinned.bones = skinned.bones.Take(target.Document.OriginalBoneCount).ToArray();
            foreach (var bone in added)
            {
                if (bone == null || bone.childCount > 0 || bone.GetComponents<Component>().Length > 1) continue;
                bool used = false;
                foreach (var other in bone.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (other != skinned && other.bones.Contains(bone)) { used = true; break; }
                if (!used) Object.DestroyImmediate(bone.gameObject);
            }
        }

        /// <summary>
        /// Builds the final mesh, saves it as an asset and shows it on the renderer, in one Undo step (Ctrl+Z shows the
        /// mesh the renderer had before the session; Ctrl+Y the committed one). Rig mates whose rest pose was edited with it
        /// are committed in the same step, so the bones they share stay right for all of them. Nothing different from the
        /// original (edits all undone): the session closes, nothing is saved and null is returned.
        /// </summary>
        public static Mesh Commit(EditTarget target, string owner = null)
        {
            if (target == null || !targets.Contains(target) || !target.Alive) return null;
            if (MixerLinkService.IsBlenderOwned(target))
            {
                Say(target.Name + " is being edited in Blender: leave its edit mode there first, then commit.", true);
                return null;
            }
            if (!target.Document.HasDifferences())
            {
                Revert(target);
                Say("Nothing to commit on " + target.Name + ": it is as it was.", false);
                return null;
            }
            var batch = new List<EditTarget> { target };
            if (target.Document.BindPosesEdited && target.Skinned != null)
            {
                var bones = new HashSet<Transform>(target.Skinned.bones.Where(b => b != null));
                foreach (var t in targets)
                    if (t != target && t.Alive && t.Skinned != null && t.Document.BindPosesEdited && t.Skinned.bones.Any(b => b != null && bones.Contains(b))
                        && !MixerLinkService.IsBlenderOwned(t) && t.Document.HasDifferences())
                        batch.Add(t);
            }
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: Commit " + target.Name + (batch.Count > 1 ? " and " + (batch.Count - 1) + " more" : ""));
            Mesh result = null;
            var saved = new List<string>();
            foreach (var t in batch)
            {
                var asset = CommitOne(t, t == target ? owner : null);
                if (t == target) result = asset;
                if (asset != null) saved.Add(AssetDatabase.GetAssetPath(asset));
            }
            Undo.IncrementCurrentGroup();
            Notice?.Invoke("Saved " + string.Join(", ", saved), false);
            Changed?.Invoke();
            return result;
        }

        private static Mesh CommitOne(EditTarget target, string owner)
        {
            var doc = target.Document;
            string name = target.Original.name.Replace(" (Mixer working)", "");
            owner = owner ?? OwnerName(target.Renderer);
            if (!SameMeshAsStart(target))
                Say(target.Name + "'s mesh changed on disk while it was edited (reimported?): the commit keeps the mesh as you edited it.", false);
            var built = MeshBuilder.Build(target);
            // A mesh Mixer saved before is updated in place (what points at it follows), unless something else uses it
            // too (another avatar, another session): then the edit gets a file of its own and the others keep theirs.
            bool inPlace = MeshAssets.IsOwnedBy(target.Original, "Mixer") && !UsedElsewhere(target);
            Mesh asset;
            if (inPlace)
            {
                long expected = MixerAutosave.FingerprintOf(doc.Positions);
                asset = MeshAssets.Replace(built, target.Original);
                // The asset must hold the edits now; if it does not, they go to a file of their own rather than nowhere.
                if (asset.vertexCount != doc.VertexCount || MixerAutosave.FingerprintOf(asset.vertices) != expected)
                {
                    Debug.LogWarning("[Mixer] '" + AssetDatabase.GetAssetPath(asset) + "' could not be updated in place: the commit of " + target.Name + " goes to a file of its own.");
                    asset = MeshAssets.Save(MeshBuilder.Build(target), MeshAssets.Folder("Mixer", owner), name);
                }
            }
            else asset = MeshAssets.Save(built, MeshAssets.Folder("Mixer", owner), name);
            MixerSidecar.Save(AssetDatabase.GetAssetPath(asset), doc);
            target.ShowChannel(WeightChannel.None);
            target.RestoreWeights();
            var weights = target.WeightsByName();
            // The step remembers the renderer as it was before the session (the original, its weights): the working mesh
            // is destroyed below, and an undo must never put a renderer on it. Rest-pose edits moved bones and may have
            // added leaf bones: the step remembers the bones where the original's bind poses want them and the original's
            // bone list, so Ctrl+Z shows the original as it was, not skinned by moved bones (Ctrl+Y the commit).
            List<RestPosePlacement.Pose> edited = null;
            if (RestPosePlacement.TryRigOf(target, out var rig))
            {
                var place = RestPosePlacement.AsOriginal(new[] { rig });
                if (place.Count > 0)
                {
                    edited = RestPosePlacement.Apply(place);
                    Undo.RecordObjects(place.Keys.Where(b => b != null).ToArray(), "Mixer: Commit " + name);
                }
            }
            var skinnedNow = target.Skinned;
            Transform[] allBones = null;
            if (skinnedNow != null && skinnedNow.bones.Length > doc.OriginalBoneCount)
            {
                allBones = skinnedNow.bones;
                skinnedNow.bones = allBones.Take(doc.OriginalBoneCount).ToArray();
            }
            target.Show(target.Original);
            Object holder = target.Filter != null ? (Object)target.Filter : target.Renderer;
            Undo.RecordObject(holder, "Mixer: Commit " + name);
            if (allBones != null) skinnedNow.bones = allBones;
            RestPosePlacement.Restore(edited);
            target.Show(asset);
            // The renderer's weights go with the shapes by name (a deleted or renamed shape moves the others' indices).
            var skinned = target.Skinned;
            if (skinned != null)
                for (int i = 0; i < asset.blendShapeCount; i++)
                    skinned.SetBlendShapeWeight(i, weights.TryGetValue(asset.GetBlendShapeName(i), out float w) ? w : 0f);
            EditorUtility.SetDirty(holder);
            if (target.Renderer.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(target.Renderer.gameObject.scene);
            var root = RootOf(target.Renderer);
            EndSession(target, asset, deleteAutosave: true, notify: false);
            AvatarMeshEdits.NotifyApplied(root);
            return asset;
        }

        // Another renderer of the open scenes (or of the target's own scene, a preview one too), or another session,
        // shows this Mixer asset too.
        private static bool UsedElsewhere(EditTarget target)
        {
            var mesh = target.Original;
            foreach (var t in targets) if (t != target && t.Original == mesh) return true;
            bool Uses(SkinnedMeshRenderer r) => r != null && r != target.Renderer && r.sharedMesh == mesh;
            bool UsesFilter(MeshFilter f) => f != null && f != target.Filter && f.sharedMesh == mesh;
            foreach (var r in Object.FindObjectsOfType<SkinnedMeshRenderer>(true)) if (Uses(r)) return true;
            foreach (var f in Object.FindObjectsOfType<MeshFilter>(true)) if (UsesFilter(f)) return true;
            var scene = target.Renderer.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
                foreach (var go in scene.GetRootGameObjects())
                {
                    foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (Uses(r)) return true;
                    foreach (var f in go.GetComponentsInChildren<MeshFilter>(true)) if (UsesFilter(f)) return true;
                }
            return false;
        }

        // The original still has the vertices the session started from (not reimported with other ones).
        private static bool SameMeshAsStart(EditTarget target)
        {
            var original = target.Original;
            if (original == null || original.vertexCount != target.Document.VertexCount) return false;
            return MixerAutosave.FingerprintOf(original.vertices) == MixerAutosave.FingerprintOf(target.Document.OriginalPositions);
        }

        /// <summary>
        /// Ends a session: its history and link claim go, the guard learns what replaces its working mesh (wherever an
        /// undo brings it back), the working mesh is destroyed.
        /// </summary>
        private static void EndSession(EditTarget target, Mesh replacement, bool deleteAutosave, bool notify = true)
        {
            MixerHistory.Forget(target);
            MixerLinkService.ReleaseForUnity(target);
            ArmatureEdit.Forget(target);
            // Also when something else destroyed the working mesh: the guard still knows its id.
            if ((object)target.Working != null) MixerMeshGuard.Released(target.Working, replacement);
            if (target.Working != null) Object.DestroyImmediate(target.Working);
            if (deleteAutosave) MixerAutosave.Delete(target.Id);
            targets.Remove(target);
            SaveState();
            if (notify) Changed?.Invoke();
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
            if (target != null) NotePreviewed(target.Renderer);
            Changed?.Invoke();
        }

        /// <summary>Mixer's history undid or redid steps: the next autosave writes what the documents hold now (a crash
        /// must not bring back edits that were undone).</summary>
        internal static void NoteHistoryApplied() => dirtySinceAutosave = true;

        /// <summary>Bind poses changed (a rest-pose edit, its undo): once per change however many rig mates it touched.</summary>
        public static void NoteArmatureEdited()
        {
            dirtySinceAutosave = true;
            MixerLinkService.NoteArmatureChanged();
            foreach (var root in targets.Where(t => t.Alive).Select(t => RootOf(t.Renderer)).Distinct()) if (root != null) AvatarMeshEdits.NotifyPreviewed(root);
            Changed?.Invoke();
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
            int index = doc.ShapeOrder.IndexOf(shape);
            doc.DeleteShape(shape);
            MixerHistory.Push(new ShapeListEdit { Target = target, Label = "Delete shape " + shape, Name = shape, Layer = layer, Created = false, LayerBefore = before, OrderIndex = index });
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
            var original = doc.OriginalPositions;
            // The same tolerance the mirror map pairs vertices with: within it a vertex is on the centre line.
            float centre = Mathf.Max(1e-6f, doc.Original.bounds.size.magnitude * 1e-4f);
            var indices = new List<int>();
            var before = new List<Vector3>();
            var after = new List<Vector3>();
            for (int w = 0; w < n.WeldedCount; w++)
            {
                int rep = n.Representative[w];
                float x = original[rep].x;
                if (Mathf.Abs(x) <= centre)
                {
                    // On the centre line a symmetric shape does not move sideways.
                    foreach (int v in n.Members[w])
                    {
                        var d = layer.Deltas[v];
                        if (d.x == 0f) continue;
                        var centred = new Vector3(0f, d.y, d.z);
                        indices.Add(v);
                        before.Add(doc.Positions[v] + d);
                        after.Add(doc.Positions[v] + centred);
                        layer.Deltas[v] = centred;
                    }
                    continue;
                }
                bool destination = fromPositiveX ? x < 0f : x > 0f;
                if (!destination) continue;
                int source = mirror.Of[w];
                if (source < 0) continue;
                var sd = layer.Deltas[n.Representative[source]];
                var flipped = new Vector3(-sd.x, sd.y, sd.z);
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
