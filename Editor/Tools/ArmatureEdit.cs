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
    /// where it is, in the same undo step as the move. Also a reset to the bind pose, leaf bones added under a bone,
    /// and mirror posing from the Toolkit.
    /// </summary>
    [InitializeOnLoad]
    internal static class ArmatureEdit
    {
        /// <summary>A rest-pose change: the bind poses of a target before and after.</summary>
        internal sealed class BindposeEdit : HistoryEntry
        {
            public Matrix4x4[] Before, After;
            public int UndoGroup;
            public override long Bytes => (Before.Length + After.Length) * 64L + 64;
            public override void Undo() => Apply(Before);
            public override void Redo() => Apply(After);

            private void Apply(Matrix4x4[] poses)
            {
                var doc = Target.Document;
                if (poses.Length != doc.BindPoses.Length) doc.BindPoses = new Matrix4x4[poses.Length];
                Array.Copy(poses, doc.BindPoses, poses.Length);
                doc.BindPosesDirty = true;
                doc.BindPosesEdited = true;
                doc.Revision++;
                Target.FlushBindposes();
                if (active) Snapshot(Target);
            }
        }

        private static bool active;
        private static bool applying;
        private static readonly HashSet<EditTarget> dirty = new HashSet<EditTarget>();
        // Where each target's skin is in the world while rest-pose editing is on: each bone's matrix times its bind
        // pose, taken when the mode starts and kept through the edits (a bone move changes the bind pose so this stays).
        private static readonly Dictionary<EditTarget, Matrix4x4[]> rest = new Dictionary<EditTarget, Matrix4x4[]>();
        private static int selectedBone = -1;

        public static event Action Changed;

        static ArmatureEdit()
        {
            Undo.postprocessModifications += OnModifications;
            EditorApplication.update += Tick;
            MixerSession.Changed += () =>
            {
                if (!active) return;
                foreach (var gone in rest.Keys.Where(t => !t.Alive).ToList()) rest.Remove(gone);
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
                if (active) foreach (var target in MixerSession.Targets) Snapshot(target);
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// Takes where the target's skin is now (each bone's matrix times its bind pose) as the placement rest-pose
        /// edits keep. Called when the mode starts, and after anything that moves the skin on purpose.
        /// </summary>
        internal static void Snapshot(EditTarget target)
        {
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin) return;
            var bones = target.Skinned.bones;
            var poses = target.Document.BindPoses;
            if (poses.Length != bones.Length) { rest.Remove(target); return; }
            var combined = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) combined[i] = bones[i] != null ? bones[i].localToWorldMatrix * poses[i] : poses[i];
            rest[target] = combined;
        }

        /// <summary>The bone the page works on (index into the focus target's bones), -1 for none.</summary>
        public static int SelectedBone
        {
            get => selectedBone;
            set { selectedBone = value; Changed?.Invoke(); }
        }

        public static void Leave() { Active = false; dirty.Clear(); }

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

        private static void Tick()
        {
            if (dirty.Count == 0) return;
            var targets = dirty.ToList();
            dirty.Clear();
            foreach (var target in targets) if (target.Alive) Recompute(target, joinCurrentGroup: true);
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
            var before = (Matrix4x4[])doc.BindPoses.Clone();
            int changed = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                var world = bones[i].localToWorldMatrix;
                if (world.determinant == 0f) continue;
                var bind = world.inverse * combined[i];
                if (Same(bind, doc.BindPoses[i])) continue;
                doc.BindPoses[i] = bind;
                changed++;
            }
            if (changed == 0) return 0;
            doc.BindPosesDirty = true;
            doc.BindPosesEdited = true;
            doc.Revision++;
            applying = true;
            try
            {
                target.FlushBindposes();
                int group = Undo.GetCurrentGroup();
                // A drag records many modifications: the entry of this gesture grows instead of piling up.
                if (joinCurrentGroup && MixerHistory.Last is BindposeEdit last && last.Target == target && last.UndoGroup == group)
                {
                    last.After = (Matrix4x4[])doc.BindPoses.Clone();
                }
                else
                {
                    MixerHistory.Push(new BindposeEdit { Target = target, Label = "Rest pose of " + target.Name, Before = before, After = (Matrix4x4[])doc.BindPoses.Clone(), UndoGroup = group }, joinCurrentGroup);
                }
            }
            finally { applying = false; }
            MixerSession.NoteEdited(target);
            MixerLinkService.NoteArmatureChanged();
            return changed;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false;
            return true;
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
            var ordered = Enumerable.Range(0, bones.Length).Where(i => bones[i] != null).OrderBy(i => Depth(bones[i])).ToList();
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
            target.RefreshWorld();
            if (active) Snapshot(target);
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
            if (parentIndex < 0) return null;
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
            var bones = skinned.bones.ToList();
            bones.Add(go.transform);
            skinned.bones = bones.ToArray();
            var combinedParent = target.Skin.Combined != null && parentIndex < target.Skin.Combined.Length ? target.Skin.Combined[parentIndex] : target.Renderer.transform.localToWorldMatrix;
            var poses = doc.BindPoses.ToList();
            poses.Add(go.transform.worldToLocalMatrix * combinedParent);
            var before = (Matrix4x4[])doc.BindPoses.Clone();
            doc.BindPoses = poses.ToArray();
            doc.BindPosesDirty = true;
            doc.BindPosesEdited = true;
            doc.Revision++;
            target.FlushBindposes();
            MixerHistory.Push(new BindposeEdit { Target = target, Label = "Add bone " + go.name, Before = before, After = (Matrix4x4[])doc.BindPoses.Clone(), UndoGroup = Undo.GetCurrentGroup() }, joinCurrentGroup: true);
            MixerSession.NoteEdited(target);
            MixerLinkService.NoteArmatureChanged();
            if (active) Snapshot(target);
            Selection.activeGameObject = go;
            return go.transform;
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
