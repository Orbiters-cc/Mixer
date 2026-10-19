using System;
using System.Linq;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Mixer.Editor.VRChat
{
    /// <summary>
    /// Mixer's helpers never ship: the build copy of an avatar loses the objects Mixer put under it (named with
    /// <see cref="MixerInfo.HelperPrefix"/>) and shows the original meshes where a Mixer session shows its working
    /// copies, meshes that are never saved and that an upload could not carry. Runs before every avatar tool.
    /// </summary>
    internal sealed class MixerBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        // Before the Toolkit's gizmo hook (-20000) and every avatar tool (VRCFury runs at -10000).
        public int callbackOrder => -20001;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            var helpers = avatarRoot.GetComponentsInChildren<Transform>(true)
                .Where(t => t != null && t.name.StartsWith(MixerInfo.HelperPrefix, StringComparison.Ordinal)).ToArray();
            foreach (var helper in helpers)
                if (helper != null) UnityEngine.Object.DestroyImmediate(helper.gameObject);
            foreach (var skinned in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var original = MixerMeshGuard.OriginalOf(skinned.sharedMesh);
                if (original != null) skinned.sharedMesh = original;
            }
            foreach (var filter in avatarRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                var original = MixerMeshGuard.OriginalOf(filter.sharedMesh);
                if (original != null) filter.sharedMesh = original;
            }
            return true;
        }
    }
}
