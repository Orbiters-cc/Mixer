using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Posing;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's armature edit mode for Unity's skinned meshes: while it is on, moving a bone with Unity's own handles
    /// (or the Mixer Hierarchy) changes the rest pose, not the pose: every bind pose is recomputed so the skin stays
    /// where it is, in the same undo step as the move (the bind poses are recorded with Unity's Undo through a
    /// <see cref="MixerBindPoseHolder"/>, so Ctrl+Z gives bones and bind poses back together, after a script reload too).
    /// Also a reset to the bind pose, leaf bones added under a bone, and mirror posing from the Toolkit.
    /// </summary>
    [InitializeOnLoad]
    internal static class ArmatureEdit
    {
        /// <summary>A rest-pose change of a target (its label in Mixer's history); the bind poses themselves come back through Unity's Undo.</summary>
        internal sealed class BindposeEdit : HistoryEntry
        {
            public int Bones;
            public int UndoGroup;
            public override long Bytes => Bones * 128L + 64;
            public override void Undo() => SyncFromHolder(Target);
            public override void Redo() => SyncFromHolder(Target);
        }

        private static bool active;
        private static bool applying;
        private static readonly HashSet<EditTarget> dirty = new HashSet<EditTarget>();
        // Where each target's skin is relative to its avatar's root while rest-pose editing is on: each bone's matrix times
        // its bind pose, taken when the mode starts and kept through the edits (a bone move changes the bind pose so this
        // stays). Relative to the root, so moving the whole avatar is not undone on the mesh at the next bone edit.
        private static readonly Dictionary<EditTarget, Matrix4x4[]> rest = new Dictionary<EditTarget, Matrix4x4[]>();
        private static readonly Dictionary<EditTarget, MixerBindPoseHolder> holders = new Dictionary<EditTarget, MixerBindPoseHolder>();
        private static readonly Dictionary<EditTarget, int> recordedGroup = new Dictionary<EditTarget, int>();
        // Holders of sessions waiting for Play to stop, by session id.
        private static readonly Dictionary<string, MixerBindPoseHolder> waitingHolders = new Dictionary<string, MixerBindPoseHolder>();
        private static Dictionary<string, MixerBindPoseHolder> survivors;
        private static int selectedBone = -1;

        public static event Action Changed;

        static ArmatureEdit()
        {
            Undo.postprocessModifications += OnModifications;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += Tick;
            MixerSession.Changed += () =>
            {
                foreach (var gone in holders.Keys.Where(t => !MixerSession.Targets.Contains(t) && !MixerSession.IsParked(t)).ToList()) Forget(gone);
                if (!active) return;
                foreach (var gone in rest.Keys.Where(t => !t.Alive || !MixerSession.Targets.Contains(t)).ToList()) rest.Remove(gone);
                // A skinned mesh opened during the mode brings its rig mates along.
                if (MixerSession.Targets.Any(t => t.Alive && t.Skinned != null && t.Document.HasSkin && !rest.ContainsKey(t))) AddRigMates();
                foreach (var target in MixerSession.Targets) if (!rest.ContainsKey(target)) Snapshot(target);
            };
        }

        /// <summary>Rest-pose editing: bone moves change the bind poses instead of posing the mesh.</summary>
        public static bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                active = value;
                rest.Clear();
                if (active)
                {
                    AddRigMates();
                    foreach (var target in MixerSession.Targets) Snapshot(target);
                }
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        private static bool addingMates;

        /// <summary>
        /// Opens every skinned mesh that shares a bone with an edited one (clothes, hair, the other body parts of the
        /// avatar): a rest-pose change moves bones they use too, and a mesh left out would keep its bind poses and show
        /// the move as a pose, saved with the scene.
        /// </summary>
        internal static void AddRigMates()
        {
            if (addingMates) return;
            addingMates = true;
            var added = new List<string>();
            try
            {
                var bones = new HashSet<Transform>();
                var roots = new HashSet<Transform>();
                foreach (var target in MixerSession.Targets)
                {
                    if (!target.Alive || target.Skinned == null) continue;
                    foreach (var bone in target.Skinned.bones) if (bone != null) bones.Add(bone);
                    roots.Add(target.Renderer.transform.root);
                }
                var mates = new List<SkinnedMeshRenderer>();
                foreach (var root in roots)
                    foreach (var other in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (MixerSession.TargetOf(other) != null || other.sharedMesh == null) continue;
                        var otherBones = other.bones;
                        if (otherBones == null || !otherBones.Any(b => b != null && bones.Contains(b))) continue;
                        mates.Add(other);
                    }
                for (int i = 0; i < mates.Count; i++)
                {
                    var other = mates[i];
                    if (mates.Count > 3) EditorUtility.DisplayProgressBar("Mixer", "Rest-pose editing opens " + other.name + " (it shares the bones)", i / (float)mates.Count);
                    if (!MixerSession.CanEdit(other, out string reason))
                    {
                        Debug.LogWarning("[Mixer] Rest-pose editing cannot carry '" + other.name + "' (" + reason + "): moving the bones it shares will pose it.");
                        continue;
                    }
                    try { MixerSession.Edit(other); added.Add(other.name); }
                    catch (Exception e) { Debug.LogWarning("[Mixer] Rest-pose editing cannot carry '" + other.name + "': " + e.Message); }
                }
            }
            finally { addingMates = false; EditorUtility.ClearProgressBar(); }
            if (added.Count > 0)
                MixerSession.Say("Rest-pose editing also carries " + string.Join(", ", added) + " (they share its bones); they are committed with it.");
        }

        /// <summary>
        /// Takes where the target's skin is now (each bone's matrix times its bind pose, relative to the avatar's root) as
        /// the placement rest-pose edits keep. Called when the mode starts, and after anything that moves the skin on purpose.
        /// </summary>
        internal static void Snapshot(EditTarget target)
        {
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin) return;
            var bones = target.Skinned.bones;
            var poses = target.Document.BindPoses;
            if (poses.Length != bones.Length) { rest.Remove(target); return; }
            var toRoot = target.Renderer.transform.root.worldToLocalMatrix;
            var combined = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) combined[i] = toRoot * (bones[i] != null ? bones[i].localToWorldMatrix * poses[i] : poses[i]);
            rest[target] = combined;
        }

        /// <summary>The bone the page works on (index into the focus target's bones), -1 for none.</summary>
        public static int SelectedBone
        {
            get => selectedBone;
            set { selectedBone = value; Changed?.Invoke(); }
        }

        public static void Leave() { Active = false; dirty.Clear(); }

        // ---- Bind poses in Unity's Undo ------------------------------------------------------------------------------

        private static MixerBindPoseHolder HolderOf(EditTarget target, bool create)
        {
            if (target == null) return null;
            if (holders.TryGetValue(target, out var holder) && holder != null) return holder;
            // A session back from Play takes its holder again: Unity's steps of the bone moves before name that one.
            if (waitingHolders.TryGetValue(target.Id, out holder)) waitingHolders.Remove(target.Id);
            if (holder == null)
            {
                // After a script reload: the holders survive, the dictionary does not (looked for once per reload).
                if (survivors == null)
                {
                    survivors = new Dictionary<string, MixerBindPoseHolder>();
                    foreach (var found in Resources.FindObjectsOfTypeAll<MixerBindPoseHolder>())
                        if (found != null && !string.IsNullOrEmpty(found.targetId)) survivors[found.targetId] = found;
                }
                if (survivors.TryGetValue(target.Id, out holder)) survivors.Remove(target.Id);
            }
            if (holder == null)
            {
                if (!create) return null;
                holder = ScriptableObject.CreateInstance<MixerBindPoseHolder>();
                holder.name = "Mixer bind poses of " + target.Name;
                holder.hideFlags = HideFlags.HideAndDontSave;
                holder.targetId = target.Id;
                holder.bindPoses = (Matrix4x4[])target.Document.BindPoses.Clone();
            }
            holders[target] = holder;
            return holder;
        }

        /// <summary>Call before changing a target's bind poses: Unity's Undo keeps them as they are, once per undo step.</summary>
        internal static void RecordBindPoses(EditTarget target, string label)
        {
            var holder = HolderOf(target, create: true);
            // A holder made earlier (or found after a reload) may lag behind edits Mixer's own history undid.
            if (!Same(holder.bindPoses, target.Document.BindPoses)) holder.bindPoses = (Matrix4x4[])target.Document.BindPoses.Clone();
            int group = Undo.GetCurrentGroup();
            if (recordedGroup.TryGetValue(target, out int recorded) && recorded == group) return;
            Undo.RegisterCompleteObjectUndo(holder, "Mixer: " + label);
            recordedGroup[target] = group;
        }

        /// <summary>Call after changing a target's bind poses: what the undo step ends with.</summary>
        internal static void StoreBindPoses(EditTarget target)
        {
            var holder = HolderOf(target, create: true);
            holder.bindPoses = (Matrix4x4[])target.Document.BindPoses.Clone();
        }

        /// <summary>The session ended: its holder goes (undo steps that still name it change nothing).</summary>
        internal static void Forget(EditTarget target)
        {
            if (target == null) return;
            rest.Remove(target);
            dirty.Remove(target);
            recordedGroup.Remove(target);
            var holder = HolderOf(target, create: false);
            holders.Remove(target);
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
        }

        /// <summary>
        /// The session waits for Play to stop: its holder stays, kept by the session's id, for the session that comes back,
        /// so undoing a bone move made before Play still gives the bind poses back with the bone.
        /// </summary>
        internal static void Park(EditTarget target)
        {
            if (target == null) return;
            var holder = HolderOf(target, create: false);
            rest.Remove(target);
            dirty.Remove(target);
            recordedGroup.Remove(target);
            holders.Remove(target);
            if (holder != null) waitingHolders[target.Id] = holder;
        }

        /// <summary>A waiting session went for good: the holder kept for it goes too.</summary>
        internal static void ForgetWaiting(string targetId)
        {
            if (targetId == null || !waitingHolders.TryGetValue(targetId, out var holder)) return;
            waitingHolders.Remove(targetId);
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
        }

        /// <summary>For tests: what a script reload takes of a session here (its holder stays, found again by the session's id).</summary>
        internal static void DropForReload(EditTarget target)
        {
            rest.Remove(target);
            dirty.Remove(target);
            recordedGroup.Remove(target);
            holders.Remove(target);
            survivors = null;
        }

        /// <summary>Puts the bind poses Unity's Undo restored into the target's document; true when they changed.</summary>
        internal static bool SyncFromHolder(EditTarget target)
        {
            if (target == null || !target.Alive) return false;
            var holder = HolderOf(target, create: false);
            if (holder == null || holder.bindPoses == null) return false;
            var doc = target.Document;
            if (Same(holder.bindPoses, doc.BindPoses)) return false;
            // Only a pose list that fits the renderer's bones (an undone added bone takes its pose with it).
            var skinned = target.Skinned;
            if (skinned != null && skinned.bones.Length != holder.bindPoses.Length) return false;
            doc.BindPoses = (Matrix4x4[])holder.bindPoses.Clone();
            doc.BindPosesDirty = true;
            doc.BindPosesEdited = true;
            doc.Revision++;
            target.FlushBindposes();
            return true;
        }

        private static void OnUndoRedo()
        {
            bool any = false;
            foreach (var target in MixerSession.Targets.ToList()) any |= SyncFromHolder(target);
            // Bones and bind poses came back together: the skin is where it should stay from here on.
            if (active) foreach (var target in MixerSession.Targets) Snapshot(target);
            if (any) { MixerSession.NoteArmatureEdited(); SceneView.RepaintAll(); }
        }

        private static bool Same(Matrix4x4[] a, Matrix4x4[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- Rest-pose edits -----------------------------------------------------------------------------------------

        // Unity records the bone's change: the targets skinned to it get their bind poses recomputed on the next tick,
        // inside the same undo group.
        private static UndoPropertyModification[] OnModifications(UndoPropertyModification[] modifications)
        {
            if (!active || applying || !MixerSession.Active) return modifications;
            foreach (var modification in modifications)
            {
                var t = modification.currentValue?.target as Transform;
                if (t == null) continue;
                foreach (var target in MixerSession.Targets)
                {
                    if (!target.Alive || target.Skinned == null || !target.Document.HasSkin) continue;
                    if (Array.IndexOf(target.Skinned.bones, t) >= 0) dirty.Add(target);
                }
            }
            return modifications;
        }

        private static bool batching;

        // This gesture's entries (Unity's undo group) are the newest ones, one per rig mate: this mesh has one already.
        private static bool PushedInGroup(EditTarget target, int group)
        {
            foreach (var entry in MixerHistory.Newest())
            {
                if (!(entry is BindposeEdit edit) || edit.UndoGroup != group) return false;
                if (edit.Target == target) return true;
            }
            return false;
        }

        /// <summary>Unity's undo steps of this target's bind-pose holder go (Revert took the moves back for good).</summary>
        internal static void ClearUndoOf(EditTarget target)
        {
            var holder = HolderOf(target, create: false);
            if (holder != null) UnityEditor.Undo.ClearUndo(holder);
        }

        private static void Tick()
        {
            if (dirty.Count == 0) return;
            var targets = dirty.ToList();
            dirty.Clear();
            int changed = 0;
            batching = true;
            try { foreach (var target in targets) if (target.Alive) changed += Recompute(target, joinCurrentGroup: true); }
            finally { batching = false; }
            // One note for every rig mate of the drag, not one per mesh and frame.
            if (changed > 0) MixerSession.NoteArmatureEdited();
        }

        /// <summary>
        /// Makes the bind poses match the bones where they are now, so every vertex stays put: for each bone, the new
        /// bind pose is the bone's inverse world matrix times the combined matrix (bone times bind pose) the skin was
        /// placed with when rest-pose editing started. Returns the number of bones whose bind pose changed.
        /// </summary>
        public static int Recompute(EditTarget target, bool joinCurrentGroup)
        {
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin) return 0;
            var doc = target.Document;
            var bones = target.Skinned.bones;
            if (doc.BindPoses.Length != bones.Length) { rest.Remove(target); return 0; }
            if (!rest.TryGetValue(target, out var combined) || combined.Length != bones.Length) { Snapshot(target); return 0; }
            var fromRoot = target.Renderer.transform.root.localToWorldMatrix;
            var next = (Matrix4x4[])doc.BindPoses.Clone();
            int changed = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                var world = bones[i].localToWorldMatrix;
                if (world.determinant == 0f) continue;
                var bind = world.inverse * (fromRoot * combined[i]);
                if (RestPosePlacement.Same(bind, next[i])) continue;
                next[i] = bind;
                changed++;
            }
            if (changed == 0) return 0;
            applying = true;
            try
            {
                string label = "Rest pose of " + target.Name;
                RecordBindPoses(target, label);
                // The bone move's step carries the bind poses: it is named as Mixer's step.
                Undo.SetCurrentGroupName("Mixer: " + label);
                doc.BindPoses = next;
                doc.BindPosesDirty = true;
                doc.BindPosesEdited = true;
                doc.Revision++;
                StoreBindPoses(target);
                target.FlushBindposes();
                int group = Undo.GetCurrentGroup();
                // In the bone move's own undo step, so one Ctrl+Z gives the bone and the bind poses back. A drag records
                // many modifications: the entry of this gesture stays one.
                if (!(joinCurrentGroup && PushedInGroup(target, group)))
                    MixerHistory.Push(new BindposeEdit { Target = target, Label = label, Bones = bones.Length, UndoGroup = group }, joinCurrentGroup: true);
            }
            finally { applying = false; }
            if (!batching) MixerSession.NoteArmatureEdited();
            return changed;
        }

        /// <summary>Puts the bones back where the bind poses say (the pose the mesh was skinned in), with Undo.</summary>
        public static int ResetToBindPose(EditTarget target)
        {
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin) return 0;
            var bones = target.Skinned.bones;
            var doc = target.Document;
            // The mesh's world matrix at bind time is the renderer's transform (what a non-skinned mesh would use).
            var reference = target.Renderer.transform.localToWorldMatrix;
            // Parents first: a child's world matrix is set after its parent's.
            var ordered = Enumerable.Range(0, Mathf.Min(bones.Length, doc.BindPoses.Length)).Where(i => bones[i] != null).OrderBy(i => Depth(bones[i])).ToList();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: Reset to bind pose");
            Undo.RecordObjects(ordered.Select(i => (UnityEngine.Object)bones[i]).ToArray(), "Reset to bind pose");
            bool wasActive = active;
            active = false; // the bind poses are right already: moving the bones must not recompute them
            int moved = 0;
            try
            {
                foreach (int i in ordered)
                {
                    var world = reference * doc.BindPoses[i].inverse;
                    var bone = bones[i];
                    var position = world.GetColumn(3);
                    var rotation = world.rotation;
                    if (Vector3.Distance(bone.position, position) < 1e-6f && Quaternion.Angle(bone.rotation, rotation) < 1e-4f) continue;
                    bone.position = position;
                    bone.rotation = rotation;
                    moved++;
                }
            }
            finally { active = wasActive; dirty.Clear(); }
            foreach (var t in MixerSession.Targets) if (t.Alive) t.RefreshWorld();
            // The bones moved as a pose: every skin on them (the clothes too) is where it should stay from here on.
            if (active) foreach (var t in MixerSession.Targets) Snapshot(t);
            SceneView.RepaintAll();
            return moved;
        }

        private static int Depth(Transform t) { int d = 0; for (var p = t.parent; p != null; p = p.parent) d++; return d; }

        /// <summary>
        /// Adds a bone under <paramref name="parent"/> (a bone of the target) at an offset along the parent, and gives the
        /// target's renderer and document the new bone with a bind pose that keeps the parent's rest: weights can then be
        /// painted onto it. Returns the new bone.
        /// </summary>
        public static Transform AddLeafBone(EditTarget target, Transform parent, string name = null)
        {
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin || parent == null) return null;
            var skinned = target.Skinned;
            int parentIndex = Array.IndexOf(skinned.bones, parent);
            if (parentIndex < 0 || parentIndex >= target.Document.BindPoses.Length) return null;
            var doc = target.Document;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mixer: Add bone");
            var go = new GameObject(string.IsNullOrEmpty(name) ? parent.name + ".tip" : name);
            Undo.RegisterCreatedObjectUndo(go, "Add bone");
            go.transform.SetParent(parent, false);
            float length = 0.05f;
            foreach (Transform sibling in parent) if (sibling != go.transform) { length = Mathf.Max(length, sibling.localPosition.magnitude); }
            go.transform.localPosition = parent.childCount > 1 ? new Vector3(0f, length, 0f) : Vector3.up * length;
            go.transform.localRotation = Quaternion.identity;
            Undo.RecordObject(skinned, "Add bone");
            RecordBindPoses(target, "Add bone " + go.name);
            var bones = skinned.bones.ToList();
            bones.Add(go.transform);
            skinned.bones = bones.ToArray();
            // Where the parent's skin is now (bone times bind pose): the new bone keeps it.
            var combinedParent = parent.localToWorldMatrix * doc.BindPoses[parentIndex];
            var poses = doc.BindPoses.ToList();
            poses.Add(go.transform.worldToLocalMatrix * combinedParent);
            doc.BindPoses = poses.ToArray();
            doc.BindPosesDirty = true;
            doc.BindPosesEdited = true;
            doc.Revision++;
            StoreBindPoses(target);
            target.FlushBindposes();
            MixerHistory.Push(new BindposeEdit { Target = target, Label = "Add bone " + go.name, Bones = doc.BindPoses.Length, UndoGroup = Undo.GetCurrentGroup() }, joinCurrentGroup: true);
            MixerSession.NoteArmatureEdited();
            if (active) Snapshot(target);
            Selection.activeGameObject = go;
            return go.transform;
        }

        /// <summary>The leaf bones a session added (renderer bones past the original's), for a revert or a scene save to leave out.</summary>
        internal static List<Transform> AddedBones(EditTarget target)
        {
            var result = new List<Transform>();
            var skinned = target?.Skinned;
            if (skinned == null || target.Document.OriginalBoneCount == 0) return result;
            var bones = skinned.bones;
            for (int i = target.Document.OriginalBoneCount; i < bones.Length; i++) if (bones[i] != null) result.Add(bones[i]);
            return result;
        }

        /// <summary>Mirror posing of the target's rig through the Toolkit (left and right bones move together).</summary>
        public static bool Mirror
        {
            get => MirrorPoseService.Enabled;
            set
            {
                if (value) { var t = MixerSceneTool.FocusTarget ?? MixerSession.First; MirrorPoseService.Enable(t != null ? t.Renderer.transform.root : null, t?.Skinned); }
                else MirrorPoseService.Disable();
                Changed?.Invoke();
            }
        }
    }
}
