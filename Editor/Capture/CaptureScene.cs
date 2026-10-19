using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>What the simulator reads from the open scene (never writes): bounds, its camera, names; plus output folders.</summary>
    internal static class CaptureScene
    {
        /// <summary>Hidden objects of the simulator start with this (cameras, overlays, post volumes).</summary>
        internal const string ObjectPrefix = MixerInfo.HelperPrefix + "Capture";

        /// <summary>Bounds of the visible renderers under <paramref name="root"/>, or of the whole scene when it is null.</summary>
        public static bool TryGetBounds(Transform root, Scene scene, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var renderers = root != null ? root.GetComponentsInChildren<Renderer>() : Roots(scene).SelectMany(r => r.GetComponentsInChildren<Renderer>());
            foreach (var renderer in renderers)
            {
                if (!Counts(renderer)) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!any && root != null)
            {
                bounds = new Bounds(root.position, Vector3.one * 0.5f);
                any = true;
            }
            return any;
        }

        private static bool Counts(Renderer renderer) =>
            renderer.enabled && renderer.gameObject.activeInHierarchy && (renderer.gameObject.hideFlags & HideFlags.HideInHierarchy) == 0 &&
            !(renderer is ParticleSystemRenderer) && !(renderer is TrailRenderer) && !(renderer is LineRenderer) &&
            !renderer.name.StartsWith(MixerInfo.HelperPrefix, StringComparison.Ordinal);

        private static GameObject[] Roots(Scene scene) => scene.IsValid() && scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<GameObject>();

        /// <summary>
        /// The camera whose settings the capture copies (culling mask, background, post-processing layer): the scene's
        /// MainCamera, else its first enabled camera. VRChat worlds keep their reference camera there.
        /// </summary>
        public static Camera ReferenceCamera(Scene scene)
        {
            Camera first = null;
            foreach (var root in Roots(scene))
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (camera.name.StartsWith(MixerInfo.HelperPrefix, StringComparison.Ordinal) || camera.targetTexture != null) continue;
                if (camera.CompareTag("MainCamera")) return camera;
                if (first == null && camera.isActiveAndEnabled) first = camera;
            }
            return first;
        }

        public static string DefaultOutputRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Orbiters", "Raw captures");

        /// <summary>A new folder name "&lt;scene&gt; &lt;yyyy-MM-dd HH-mm-ss&gt;" under the output root, unique.</summary>
        public static string NewCaptureFolder(string outputRoot, string sceneName, DateTime now)
        {
            string root = string.IsNullOrWhiteSpace(outputRoot) ? DefaultOutputRoot : outputRoot;
            string name = SafeName(string.IsNullOrEmpty(sceneName) ? "Untitled" : sceneName) + " " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            string folder = Path.Combine(root, name);
            for (int i = 2; Directory.Exists(folder) || File.Exists(folder); i++) folder = Path.Combine(root, name + " (" + i + ")");
            return folder;
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        }

        private const string AliveKey = "Orbiters.Mixer.Capture.RigsAlive";

        /// <summary>Counts live rigs across domain reloads, so a reload in the middle of a capture can clean up after it.</summary>
        internal static void RigCreated() => SessionState.SetInt(AliveKey, SessionState.GetInt(AliveKey, 0) + 1);

        internal static void RigDisposed() => SessionState.SetInt(AliveKey, Mathf.Max(0, SessionState.GetInt(AliveKey, 0) - 1));

        /// <summary>
        /// Destroys hidden simulator objects a domain reload left behind: the reload drops the managed side of a rig, not
        /// its native camera, textures and materials. Only looks when a rig was alive at the reload.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void CleanLeftovers()
        {
            if (SessionState.GetInt(AliveKey, 0) == 0) return;
            SessionState.EraseInt(AliveKey);
            foreach (var o in Resources.FindObjectsOfTypeAll<Object>())
            {
                if (o == null || o.hideFlags != HideFlags.HideAndDontSave || !o.name.StartsWith(ObjectPrefix, StringComparison.Ordinal)) continue;
                if (o is GameObject go && go.transform.parent == null) Object.DestroyImmediate(go);
                else if (o is RenderTexture rt) { rt.Release(); Object.DestroyImmediate(rt); }
                else if (o is Texture2D || o is Material) Object.DestroyImmediate(o);
            }
        }
    }
}
