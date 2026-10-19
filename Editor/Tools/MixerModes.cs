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
        /// Switches the Scene view to a mode. For an edit mode without a session, the selected object's mesh (its own
        /// renderer, else the largest skinned mesh under it) is opened in Mixer first; with nothing to edit the mode stays
        /// and the Scene view says why. Returns false when the mode could not be entered.
        /// </summary>
        public static bool Enter(MixerMode mode)
        {
            if (mode == MixerMode.None)
            {
                MixerSceneTool.Mode = MixerMode.None;
                return true;
            }
            var target = MixerSceneTool.FocusTarget ?? TargetOfSelection();
            if (target == null)
            {
                var renderer = RendererOfSelection();
                if (renderer == null)
                {
                    if (MixerSession.First != null) target = MixerSession.First;
                    else { Notify("Select a mesh to edit first."); return false; }
                }
                else if (!MixerSession.CanEdit(renderer, out string reason)) { Notify(reason); return false; }
                else target = MixerSession.Edit(renderer);
            }
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.Mode = mode;
            Notify(NameOf(mode) + ": " + target.Name);
            return true;
        }

        /// <summary>The session of a selected object's renderer, when it has one.</summary>
        private static EditTarget TargetOfSelection()
        {
            foreach (var go in Selection.gameObjects)
            {
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    var target = MixerSession.TargetOf(renderer);
                    if (target != null && target.Alive) return target;
                }
            }
            return null;
        }

        /// <summary>The selected object's renderer, else the largest skinned mesh under it.</summary>
        internal static Renderer RendererOfSelection()
        {
            foreach (var go in Selection.gameObjects)
            {
                var own = go.GetComponent<Renderer>();
                if (own is SkinnedMeshRenderer s && s.sharedMesh != null) return own;
                if (own is MeshRenderer && go.GetComponent<MeshFilter>()?.sharedMesh != null) return own;
                var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                if (skinned != null) return skinned;
                var mesh = go.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.GetComponent<MeshFilter>()?.sharedMesh != null);
                if (mesh != null) return mesh;
            }
            return null;
        }

        /// <summary>A short note in the Scene view, where the user is looking.</summary>
        public static void Notify(string text)
        {
            var view = SceneView.lastActiveSceneView;
            if (view != null) view.ShowNotification(new GUIContent(text), 1.6);
            else MixerSession.Say(text, false);
        }
    }
}
