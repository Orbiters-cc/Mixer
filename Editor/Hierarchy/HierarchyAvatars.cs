using System.Collections.Generic;
using Orbiters.Toolkit.Armature;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>The bones an avatar's armature row selects in one click.</summary>
    internal sealed class QuickBones
    {
        public Transform Chest, Head, LeftHand, RightHand, LeftFoot, RightFoot;
    }

    /// <summary>
    /// The avatars the hierarchy shows: which object is each one's armature (the root's child above the hips) and its
    /// quick bones, found once and kept until the hierarchy changes.
    /// </summary>
    internal sealed class HierarchyAvatars
    {
        private readonly Dictionary<int, QuickBones> byArmature = new Dictionary<int, QuickBones>();
        private readonly HashSet<int> scanned = new HashSet<int>();
        private readonly HashSet<int> bones = new HashSet<int>();

        /// <summary>Forgets every avatar (objects were added, removed or moved).</summary>
        public void Clear()
        {
            byArmature.Clear();
            scanned.Clear();
            bones.Clear();
        }

        /// <summary>A VRChat avatar root: the object with the avatar descriptor (found by name, no SDK reference needed).</summary>
        public static bool IsVrchatAvatar(GameObject go) => go != null && go.GetComponent("VRCAvatarDescriptor") != null;

        /// <summary>
        /// Finds the armature of the avatar rooted at <paramref name="root"/> (a VRChat avatar, a humanoid Animator or a
        /// skinned model), once.
        /// </summary>
        public void Scan(GameObject root)
        {
            if (root == null || !scanned.Add(root.GetInstanceID())) return;
            // Bones: what the avatar's skinned meshes (clothing included) are skinned to, and the humanoid's.
            Transform shallowest = null;
            int shallowestDepth = int.MaxValue;
            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var bone in skinned.bones)
                {
                    if (bone == null || bone == root.transform) continue;
                    bones.Add(bone.gameObject.GetInstanceID());
                    if (!bone.IsChildOf(root.transform)) continue;
                    int depth = Depth(bone);
                    if (depth < shallowestDepth) { shallowest = bone; shallowestDepth = depth; }
                }
            var animator = AvatarBoneIndex.FindHumanoid(root.transform);
            var index = AvatarBoneIndex.Build(root.transform, root.GetComponentsInChildren<Transform>(true), animator);
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                var bone = index.Humanoid(id);
                if (bone != null && bone != root.transform) bones.Add(bone.gameObject.GetInstanceID());
            }
            // The hips (an Animator's, else found by name), else the bone nearest the root: a generic rig's armature too.
            var hips = index.Humanoid(HumanBodyBones.Hips);
            if (hips == null || hips == root.transform || !hips.IsChildOf(root.transform)) hips = shallowest;
            if (hips == null) return;
            var armature = hips;
            while (armature.parent != null && armature.parent != root.transform) armature = armature.parent;
            // The hips straight under the root are no armature object of their own: their row gets the buttons.
            byArmature[armature.gameObject.GetInstanceID()] = new QuickBones
            {
                Chest = index.Humanoid(HumanBodyBones.Chest) ?? index.Humanoid(HumanBodyBones.UpperChest) ?? index.Humanoid(HumanBodyBones.Spine),
                Head = index.Humanoid(HumanBodyBones.Head),
                LeftHand = index.Humanoid(HumanBodyBones.LeftHand),
                RightHand = index.Humanoid(HumanBodyBones.RightHand),
                LeftFoot = index.Humanoid(HumanBodyBones.LeftFoot),
                RightFoot = index.Humanoid(HumanBodyBones.RightFoot),
            };
        }

        /// <summary>Whether <paramref name="go"/> is a bone of an avatar scanned so far.</summary>
        public bool IsBone(GameObject go) => go != null && bones.Contains(go.GetInstanceID());

        /// <summary>The quick bones of the armature <paramref name="go"/>, or null when it is none.</summary>
        public QuickBones BonesOf(GameObject go) => go != null && byArmature.TryGetValue(go.GetInstanceID(), out var bones) ? bones : null;

        /// <summary>
        /// Whether <paramref name="go"/> roots an avatar whose armature is worth finding: a VRChat avatar, a humanoid
        /// Animator, or a model placed whole (a generic rig, imported without an Avatar) with skinned meshes.
        /// </summary>
        public static bool IsAvatarRoot(GameObject go)
        {
            if (go == null) return false;
            if (IsVrchatAvatar(go)) return true;
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid) return true;
            return PrefabUtility.IsAnyPrefabInstanceRoot(go) && PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Model &&
                   go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }
    }
}
