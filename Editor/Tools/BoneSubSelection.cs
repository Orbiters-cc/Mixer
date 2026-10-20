using System;
using System.Linq;
using Orbiters.Toolkit.Editor.XRay;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's bone selection in Weight Paint. A click on a bone of the painted mesh's rig, in the Toolkit's armature
    /// overlay or in the Mixer Hierarchy, sub-selects it: Mixer's own pick, while Unity's selection and the inspectors
    /// stay on the painted mesh. The bone becomes the painted bone and the one the weight overlay shows, is highlighted
    /// in the overlay and the hierarchy, and gets a move or rotate handle in the Scene view to pose it and try the weights.
    /// Leaving Weight Paint, a click on empty space, a vertex group picked or another selection clears it.
    /// </summary>
    [InitializeOnLoad]
    internal static class BoneSubSelection
    {
        private const string HandlerId = "orbiters.mixer.weight-paint";
        private static readonly int RingHint = "Orbiters.Mixer.BoneSubSelection.Ring".GetHashCode();
        private static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };

        private static Transform bone;
        private static bool hidingTools, toolsHiddenBefore;

        /// <summary>The sub-selection changed (picked, moved to another bone or cleared).</summary>
        public static event Action Changed;

        static BoneSubSelection()
        {
            XRayBonePickHandlers.Register(HandlerId, Pick, () => Bone);
            Selection.selectionChanged += OnSelectionChanged;
            // The painted mesh reverted, committed away or deleted: its rig is no longer what is painted.
            MixerSession.Changed += () => { if ((Bone != null || hidingTools) && !Accepts(Bone)) Clear(); };
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        /// <summary>The sub-selected bone, or null.</summary>
        public static Transform Bone => bone != null ? bone : null;

        private static EditTarget Target => MixerSceneTool.FocusTarget ?? MixerSession.First;

        // The mesh a click on a bone paints: the focus, else the session skinned to that bone, else the first.
        private static EditTarget TargetFor(Transform t)
        {
            var focus = MixerSceneTool.FocusTarget;
            if (focus != null || t == null) return focus ?? MixerSession.First;
            foreach (var target in MixerSession.Targets)
                if (target.Alive && target.Skinned != null && Array.IndexOf(target.Skinned.bones, t) >= 0) return target;
            return MixerSession.First;
        }

        /// <summary>
        /// Whether a click on <paramref name="t"/> sub-selects it: Weight Paint is on and <paramref name="t"/> is one of
        /// the painted mesh's bones, or a bone under its root bone (not an object with a renderer of its own).
        /// </summary>
        public static bool Accepts(Transform t)
        {
            if (t == null || MixerSceneTool.Mode != MixerMode.Weights) return false;
            var target = TargetFor(t);
            var skinned = target != null && target.Alive ? target.Skinned : null;
            if (skinned == null || t == skinned.transform) return false;
            if (Array.IndexOf(skinned.bones, t) >= 0) return true;
            var root = skinned.rootBone;
            return root != null && t.IsChildOf(root) && t.GetComponent<Renderer>() == null;
        }

        /// <summary>
        /// Sub-selects a bone of the painted rig and paints it (a bone the mesh is not skinned to can only be posed).
        /// Returns false, changing nothing, for anything <see cref="Accepts"/> refuses.
        /// </summary>
        public static bool Pick(Transform t)
        {
            if (!Accepts(t)) return false;
            var target = TargetFor(t);
            if (MixerSceneTool.FocusTarget == null) MixerSceneTool.FocusTarget = target;
            Set(t);
            int index = Array.IndexOf(target.Skinned.bones, t);
            if (index >= 0) MixerSceneTool.SetChannel(target, WeightChannel.OfBone(index));
            else MixerSession.Say(t.name + " is not one of " + target.Name + "'s bones: pose it here, its weights cannot be painted on this mesh.");
            return true;
        }

        public static void Clear() => Set(null);

        private static void Set(Transform t)
        {
            if (bone == t && (t != null || !hidingTools)) return;
            bone = t;
            HideUnityTools(t != null);
            Changed?.Invoke();
            XRayBonePickHandlers.NotifyChanged();
        }

        // Unity's own handle would move the selected mesh: while a bone is sub-selected, only the bone's handle shows.
        private static void HideUnityTools(bool hide)
        {
            if (hide == hidingTools) return;
            if (hide)
            {
                toolsHiddenBefore = Tools.hidden;
                Tools.hidden = true;
            }
            else
            {
                Tools.hidden = toolsHiddenBefore;
            }
            hidingTools = hide;
        }

        // Selecting something that is not the painted mesh (or an object above it) ends the sub-selection.
        private static void OnSelectionChanged()
        {
            if (Bone == null) { if (hidingTools) Clear(); return; }
            var target = Target;
            var painted = target != null && target.Alive ? target.Renderer.transform : null;
            if (painted == null || !Selection.transforms.Any(s => s != null && painted.IsChildOf(s))) Clear();
        }

        /// <summary>Frames the sub-selected bone in the last active Scene view (the mesh stays selected).</summary>
        public static void Frame()
        {
            var t = Bone;
            var view = SceneView.lastActiveSceneView;
            if (t == null || view == null) return;
            float size = 0.05f;
            foreach (Transform child in t) size = Mathf.Max(size, Vector3.Distance(child.position, t.position));
            view.Frame(new Bounds(t.position, Vector3.one * size * 3f), false);
        }

        /// <summary>
        /// The sub-selected bone's handle, run before Mixer's brush sees the event: Unity's move handle with the Move
        /// tool, rotation rings with the others (both with the Transform tool), along the toolbar's pivot rotation. The
        /// rings leave out Unity's free-rotation ball so a stroke can still start over the joint. Each change is recorded
        /// on the bone for Undo, as Unity's own tools do.
        /// </summary>
        internal static void OnSceneGui(SceneView view)
        {
            var t = Bone;
            if (t == null)
            {
                if (hidingTools) Clear();
                return;
            }
            if (Tools.current == Tool.View) return;
            bool local = Tools.pivotRotation == PivotRotation.Local;
            if (Tools.current == Tool.Move || Tools.current == Tool.Transform)
            {
                EditorGUI.BeginChangeCheck();
                var moved = Handles.PositionHandle(t.position, local ? t.rotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(t, "Move " + t.name);
                    t.position = moved;
                }
            }
            if (Tools.current != Tool.Move) RotationRings(t, local, view);
        }

        // Handles.Disc returns the rotation it was given at the press, turned by the drag about the press's axis: given
        // the bone's own rotation, the result is the bone's new rotation for a local or a global axis alike.
        private static void RotationRings(Transform t, bool local, SceneView view)
        {
            var position = t.position;
            var rotation = t.rotation;
            float size = HandleUtility.GetHandleSize(position);
            float snap = EditorSnapSettings.rotate;
            var colors = new[] { Handles.xAxisColor, Handles.yAxisColor, Handles.zAxisColor };
            var color = Handles.color;
            var result = rotation;
            bool changed = false;
            try
            {
                for (int i = 0; i < Axes.Length; i++)
                {
                    int id = GUIUtility.GetControlID(RingHint, FocusType.Passive);
                    Handles.color = colors[i];
                    EditorGUI.BeginChangeCheck();
                    var turned = Handles.Disc(id, rotation, position, local ? rotation * Axes[i] : Axes[i], size, true, snap);
                    if (EditorGUI.EndChangeCheck()) { result = turned; changed = true; }
                }
                var camera = view != null ? view.camera : null;
                if (camera != null)
                {
                    int id = GUIUtility.GetControlID(RingHint, FocusType.Passive);
                    Handles.color = Handles.centerColor;
                    EditorGUI.BeginChangeCheck();
                    var turned = Handles.Disc(id, rotation, position, camera.transform.forward, size * 1.1f, false, snap);
                    if (EditorGUI.EndChangeCheck()) { result = turned; changed = true; }
                }
            }
            finally
            {
                Handles.color = color;
            }
            if (!changed) return;
            Undo.RecordObject(t, "Rotate " + t.name);
            t.rotation = result;
        }
    }
}
