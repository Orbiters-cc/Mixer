using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// Lip sync the way the avatar showed it: VRChat syncs the dominant viseme (<c>Viseme</c>, 0–14: sil, PP, FF, TH,
    /// DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou) and the voice level (<c>Voice</c>); the avatar's lip sync settings say
    /// what moves (viseme blend shapes, one mouth-open blend shape, or a jaw bone). Changes are eased like VRChat's
    /// transitions so the mouth does not snap between shapes. Avatars whose animator reads <c>Viseme</c> themselves
    /// (Viseme Parameter Only) are animated by the layer stack instead.
    /// </summary>
    internal sealed class VisemeEmulator
    {
        public const int Count = 15;
        /// <summary>Seconds to cover most of the way to a new shape.</summary>
        public const float Ease = 0.06f;

        readonly VRCAvatarDescriptor descriptor;
        readonly SkinnedMeshRenderer mesh;
        readonly int[] shapeIndices = new int[Count];
        readonly int mouthOpen = -1;
        readonly float[] weights = new float[Count];
        float open;

        public VisemeEmulator(VRCAvatarDescriptor descriptor, Transform copyRoot)
        {
            this.descriptor = descriptor;
            for (int i = 0; i < Count; i++) shapeIndices[i] = -1;
            // The descriptor points at the original's renderer; find the same one on the copy.
            if (descriptor.VisemeSkinnedMesh != null) mesh = Counterpart(descriptor.transform, copyRoot, descriptor.VisemeSkinnedMesh.transform)?.GetComponent<SkinnedMeshRenderer>();
            if (mesh == null || mesh.sharedMesh == null) return;
            var shapes = descriptor.VisemeBlendShapes;
            if (shapes != null)
                for (int i = 0; i < Count && i < shapes.Length; i++)
                    shapeIndices[i] = string.IsNullOrEmpty(shapes[i]) ? -1 : mesh.sharedMesh.GetBlendShapeIndex(shapes[i]);
            if (!string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName)) mouthOpen = mesh.sharedMesh.GetBlendShapeIndex(descriptor.MouthOpenBlendShapeName);
        }

        public VRC_AvatarDescriptor.LipSyncStyle Style => descriptor.lipSync;

        /// <summary>The renderer the visemes move, on the copy (null when lip sync drives no blend shape).</summary>
        public SkinnedMeshRenderer Mesh => mesh;

        /// <summary>Eases toward the shape of <paramref name="viseme"/> at the given voice level.</summary>
        public void Step(int viseme, float voice, float dt)
        {
            float k = 1f - Mathf.Exp(-dt / (Ease / 3f));
            for (int i = 0; i < Count; i++)
            {
                // sil (0) is a shape too: VRChat raises it in silence.
                float target = i == viseme ? 1f : 0f;
                weights[i] += (target - weights[i]) * k;
            }
            open += (Mathf.Clamp01(voice) - open) * k;
        }

        public float Weight(int viseme) => weights[viseme];

        /// <summary>Writes the current shapes onto the copy (after the animator, like VRChat's lip sync).</summary>
        public void Apply(Transform copyRoot, Transform jawOnCopy)
        {
            switch (descriptor.lipSync)
            {
                case VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape:
                    if (mesh == null) return;
                    for (int i = 0; i < Count; i++)
                        if (shapeIndices[i] >= 0)
                            mesh.SetBlendShapeWeight(shapeIndices[i], weights[i] * 100f);
                    return;
                case VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape:
                    if (mesh != null && mouthOpen >= 0) mesh.SetBlendShapeWeight(mouthOpen, open * 100f);
                    return;
                case VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone:
                    if (jawOnCopy != null) jawOnCopy.localRotation = Quaternion.Slerp(descriptor.lipSyncJawClosed, descriptor.lipSyncJawOpen, open);
                    return;
            }
        }

        /// <summary>The transform at the same path under <paramref name="copyRoot"/> as <paramref name="target"/> under
        /// <paramref name="originalRoot"/>.</summary>
        public static Transform Counterpart(Transform originalRoot, Transform copyRoot, Transform target)
        {
            if (target == null) return null;
            if (target == originalRoot) return copyRoot;
            string path = UnityEditor.AnimationUtility.CalculateTransformPath(target, originalRoot);
            return string.IsNullOrEmpty(path) ? null : copyRoot.Find(path);
        }
    }
}
