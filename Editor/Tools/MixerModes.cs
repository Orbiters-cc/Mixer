using System;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's mode switch for the Scene view: Object mode, Sculpt mode, Weight paint and Edit armature. Entering an
    /// edit mode opens a Mixer session on the selected mesh when none is open yet, so nothing needs the Mixer window.
    /// </summary>
    internal static class MixerModes
    {
        public static readonly (MixerMode Mode, string Name, IconGlyph Icon, string Help)[] All =
        {
            (MixerMode.None, "Object Mode", IconGlyph.Hierarchy, "Unity's own tools; Mixer's edits stay on the meshes until you commit or revert them"),
            (MixerMode.Sculpt, "Sculpt Mode", IconGlyph.Sculpt, "Sculpt the selected mesh with the Mixer brushes; blendshape layers in the Mixer window or the renderer's Shape keys"),
            (MixerMode.Weights, "Weight Paint", IconGlyph.Weights, "Paint bone weights and vertex groups of the selected mesh"),
            (MixerMode.Armature, "Edit Armature", IconGlyph.Armature, "Move bones with Unity's handles while the skin stays where it is"),
        };

        public static string NameOf(MixerMode mode) => All.First(m => m.Mode == mode).Name;
        public static IconGlyph IconOf(MixerMode mode) => All.First(m => m.Mode == mode).Icon;

        /// <summary>
        /// Switches the Scene view to a mode, on what is selected: the selection's edited mesh, else the selected object's
        /// mesh (its own renderer, else the largest skinned mesh under it), opened in Mixer first; with nothing selected
        /// (or a bone of the mesh being edited), the mesh being edited. With nothing to edit the mode stays and the Scene
        /// view says why. Returns false when the mode could not be entered.
        /// </summary>
        public static bool Enter(MixerMode mode)
        {
            if (mode == MixerMode.None)
            {
                MixerSceneTool.Mode = MixerMode.None;
                return true;
            }
            var target = TargetOfSelection();
            if (target == null)
            {
                var renderer = SelectionKeepsFocus() ? null : RendererOfSelection();
                if (renderer != null)
                {
                    if (!MixerSession.CanEdit(renderer, out string reason)) { Notify(reason); return false; }
                    target = MixerSession.Edit(renderer);
                }
                else target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                if (target == null) { Notify("Select a mesh to edit first."); return false; }
            }
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.Mode = mode;
            Notify(NameOf(mode) + ": " + target.Name);
            return true;
        }

        /// <summary>
        /// Switches the Scene view to a mode on <paramref name="target"/> (a mesh the caller names, such as the renderer
        /// an inspector shows, whatever is selected); without one, as <see cref="Enter(MixerMode)"/>.
        /// </summary>
        public static bool Enter(MixerMode mode, EditTarget target)
        {
            if (target == null || !target.Alive || !MixerSession.Targets.Contains(target)) return Enter(mode);
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.Mode = mode;
            if (mode != MixerMode.None) Notify(NameOf(mode) + ": " + target.Name);
            return true;
        }

        /// <summary>
        /// What the Scene view's Commit and Revert act on: the focused mesh, else the edited mesh of the selection; null
        /// otherwise (never a mesh the user did not open or pick, such as a rig mate opened by rest-pose editing).
        /// </summary>
        public static EditTarget CommandTarget() => MixerSceneTool.FocusTarget ?? TargetOfSelection();

        /// <summary>The edited mesh of the selection (the focused one when it is part of it), or null.</summary>
        internal static EditTarget TargetOfSelection()
        {
            var focus = MixerSceneTool.FocusTarget;
            EditTarget found = null;
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                if (focus != null && focus.Renderer.transform.IsChildOf(go.transform)) return focus;
                if (found != null) continue;
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    var target = MixerSession.TargetOf(renderer);
                    if (target != null && target.Alive) { found = target; break; }
                }
            }
            return found;
        }

        // A bone of the mesh being edited is selected (Weight Paint and Edit Armature pick bones): the mesh stays the one
        // edited, rather than a prop parented to the bone.
        internal static bool SelectionKeepsFocus()
        {
            var focus = MixerSceneTool.FocusTarget;
            var skinned = focus != null ? focus.Skinned : null;
            var picked = Selection.activeTransform;
            if (skinned == null || picked == null || picked.GetComponent<Renderer>() != null) return false;
            if (Array.IndexOf(skinned.bones, picked) >= 0) return true;
            return skinned.rootBone != null && picked.IsChildOf(skinned.rootBone);
        }

        /// <summary>The selected object's renderer, else the largest skinned mesh under it (Mixer's and the gizmos' helpers left out).</summary>
        internal static Renderer RendererOfSelection()
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                var own = go.GetComponent<Renderer>();
                if ((own is SkinnedMeshRenderer || own is MeshRenderer) && !IsHelper(own) && MeshOf(own) != null) return own;
                var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => !IsHelper(r) && r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                if (skinned != null) return skinned;
                var mesh = go.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => !IsHelper(r) && MeshOf(r) != null);
                if (mesh != null) return mesh;
            }
            return null;
        }

        /// <summary>
        /// The mesh a renderer draws: a skinned mesh renderer's, or its mesh filter's. Null for any other renderer, and
        /// without Unity's placeholder of a missing component (whose <c>sharedMesh</c> would throw).
        /// </summary>
        internal static Mesh MeshOf(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter)) return filter.sharedMesh;
            return null;
        }

        /// <summary>Mixer's own helper objects and the avatar gizmos' meshes, which are never edited, joined or listed.</summary>
        internal static bool IsHelper(Component component) =>
            component != null && (component.name.StartsWith(MixerInfo.HelperPrefix, StringComparison.Ordinal) || component.name.StartsWith("__XRayGizmos_", StringComparison.Ordinal));

        /// <summary>A short note in the Scene view, where the user is looking.</summary>
        public static void Notify(string text)
        {
            var view = SceneView.lastActiveSceneView;
            if (view != null) view.ShowNotification(new GUIContent(text), 1.6);
            else MixerSession.Say(text, false);
        }
    }
}
