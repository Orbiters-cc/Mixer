using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's Blender link and sessions for the other Orbiters tools: open the scene in Blender, know whether Blender
    /// is linked, see which renderers Blender or Mixer is editing, commit or revert those edits, keep objects out of the
    /// Blender project. MCB's creator mode drives the custom base through it. The rest of Mixer stays internal.
    /// </summary>
    public static class MixerLink
    {
        public enum RendererState { Untouched, EditedInMixer, EditedInBlender }

        /// <summary>Whether the link listens for Blender (Mixer's own setting; turning it on starts listening).</summary>
        public static bool Enabled { get => MixerLinkService.Enabled; set => MixerLinkService.Enabled = value; }

        /// <summary>The link's port is open and waits for Blender.</summary>
        public static bool Listening => MixerLinkService.Running;

        /// <summary>A Blender is connected.</summary>
        public static bool Connected => MixerLinkService.Connected;

        public static int Port => MixerLinkService.Port;

        /// <summary>The linked Blender's version text, or null while none is connected.</summary>
        public static string BlenderVersion => MixerLinkService.Peer?.blender;

        /// <summary>The .blend of the active scene's Mixer project, or null while the scene never opened in Blender.</summary>
        public static string BlendPath
        {
            get
            {
                var settings = MixerLinkService.SettingsFor(SceneManager.GetActiveScene(), create: false);
                return settings != null && !string.IsNullOrEmpty(settings.blendPath) ? settings.blendPath : null;
            }
        }

        /// <summary>The link's log lines, oldest first.</summary>
        public static IReadOnlyList<string> Log => MixerLinkService.Log;

        /// <summary>Raised when the link's state or Mixer's sessions change.</summary>
        public static event Action Changed
        {
            add { MixerLinkService.Changed += value; MixerSession.Changed += value; }
            remove { MixerLinkService.Changed -= value; MixerSession.Changed -= value; }
        }

        /// <summary>
        /// Starts Blender on the active scene's Mixer project (installing the Mixer extension when needed) and makes the
        /// link listen. Returns an error text, or null when Blender started.
        /// </summary>
        public static string OpenInBlender() => BlenderProjectLauncher.Open();

        /// <summary>Shows the Mixer window.</summary>
        public static void OpenMixer() => MixerWindow.Open();

        /// <summary>Whether Mixer shows a working copy of this renderer's mesh, and which side edits it.</summary>
        public static RendererState StateOf(Renderer renderer)
        {
            var target = renderer != null ? MixerSession.TargetOf(renderer) : null;
            if (target == null || !target.Alive) return RendererState.Untouched;
            return MixerLinkService.IsBlenderOwned(target) ? RendererState.EditedInBlender : RendererState.EditedInMixer;
        }

        /// <summary>The renderer's Mixer session holds edits that are not committed yet.</summary>
        public static bool HasEdits(Renderer renderer)
        {
            var target = renderer != null ? MixerSession.TargetOf(renderer) : null;
            return target != null && target.Alive && target.Document.Changed;
        }

        /// <summary>Opens a Mixer session on the renderer's mesh and shows it in the Mixer window. Returns an error text, or null.</summary>
        public static string Edit(Renderer renderer)
        {
            if (renderer == null) return "No renderer.";
            var target = MixerSession.TargetOf(renderer);
            if (target == null)
            {
                if (!MixerSession.CanEdit(renderer, out string reason)) return reason;
                target = MixerSession.Edit(renderer);
            }
            MixerSceneTool.FocusTarget = target;
            MixerWindow.Open();
            return null;
        }

        /// <summary>Saves the renderer's Mixer edits as a native mesh asset shown on the renderer (one Undo step). Null when nothing is being edited.</summary>
        public static Mesh Commit(Renderer renderer)
        {
            var target = renderer != null ? MixerSession.TargetOf(renderer) : null;
            return target != null && target.Alive ? MixerSession.Commit(target) : null;
        }

        /// <summary>Puts the renderer's original mesh back and closes its Mixer session. False when nothing was being edited.</summary>
        public static bool Revert(Renderer renderer)
        {
            var target = renderer != null ? MixerSession.TargetOf(renderer) : null;
            if (target == null) return false;
            MixerSession.Revert(target);
            return true;
        }

        /// <summary>Whether the object, or one above it, is kept out of the Blender project.</summary>
        public static bool IsKeptOutOfBlender(GameObject gameObject)
        {
            if (gameObject == null) return false;
            var settings = MixerLinkService.SettingsFor(gameObject.scene, create: false);
            if (settings == null || settings.excludedIds.Count == 0) return false;
            for (var t = gameObject.transform; t != null; t = t.parent)
                if (settings.excludedIds.Contains(SceneGraphExporter.IdOf(t.gameObject))) return true;
            return false;
        }

        /// <summary>Keeps objects (with their children) out of the Blender project, or sends them back.</summary>
        public static void KeepOutOfBlender(IEnumerable<GameObject> objects, bool keepOut) => MixerHierarchyWindow.SetExcluded(objects, keepOut);

        /// <summary>
        /// Names the project file (a Unity path, e.g. a custom base's target FBX) a renderer's mesh stands for, for the
        /// tool that sets it: Blender gets it as the node's targetFbx, and the Mixer extension's Export for MCB groups
        /// meshes by it. Null means unknown. Setting it sends the scene to Blender again.
        /// </summary>
        public static Func<Renderer, string> TargetFbxResolver
        {
            get => targetFbxResolver;
            set { targetFbxResolver = value; RefreshScene(); }
        }

        private static Func<Renderer, string> targetFbxResolver;

        /// <summary>Sends the scene graph to Blender again when it is connected (for a tool whose answers changed).</summary>
        public static void RefreshScene()
        {
            if (Connected) MixerLinkService.SendGraph(force: true);
        }

        /// <summary>A status Blender sent, as (id, message): the Mixer extension's Export for MCB reports "mcb.export" with the manifest's path.</summary>
        public static event Action<string, string> BlenderStatus;

        static MixerLink()
        {
            MixerLinkService.StatusReceived += status => BlenderStatus?.Invoke(status.id, status.message);
        }
    }
}
