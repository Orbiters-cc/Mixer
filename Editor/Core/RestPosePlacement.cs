using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Where the bones a rest-pose edit moved must be for the original bind poses, and back. A rest-pose edit moves bones
    /// and changes bind poses so the skin stays; a scene save, an upload or Play gets the original meshes, so the bones
    /// they get are placed where the original bind poses put the skin where it shows now (for an edit alone: where the
    /// bones were before it), and a session that comes back from those (Play, a reopened scene, a crash) puts them where
    /// its edited bind poses want them again. Rig mates share bones: each bone is placed once.
    /// </summary>
    internal static class RestPosePlacement
    {
        public struct Pose
        {
            public Transform Bone;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        /// <summary>A skinned mesh's bones with its bind poses as edited and as the original has them.</summary>
        public struct Rig
        {
            public Transform[] Bones;
            public Matrix4x4[] Edited, Original;
        }

        /// <summary>The rig of a target whose bind poses were edited; false for any other.</summary>
        public static bool TryRigOf(EditTarget target, out Rig rig)
        {
            rig = default;
            if (target == null || !target.Alive || target.Skinned == null || !target.Document.HasSkin || !target.Document.BindPosesEdited) return false;
            rig = new Rig { Bones = target.Skinned.bones, Edited = target.Document.BindPoses, Original = target.Document.OriginalBindPoses };
            return true;
        }

        public static IEnumerable<Rig> RigsOf(IEnumerable<EditTarget> targets)
        {
            foreach (var t in targets) if (TryRigOf(t, out var rig)) yield return rig;
        }

        /// <summary>Each moved bone's world matrix under the original bind poses (the skin where the edited ones show it).</summary>
        public static Dictionary<Transform, Matrix4x4> AsOriginal(IEnumerable<Rig> rigs) => Place(rigs, toOriginal: true);

        /// <summary>Each bone's world matrix under the edited bind poses, from bones placed for the original ones.</summary>
        public static Dictionary<Transform, Matrix4x4> AsEdited(IEnumerable<Rig> rigs) => Place(rigs, toOriginal: false);

        private static Dictionary<Transform, Matrix4x4> Place(IEnumerable<Rig> rigs, bool toOriginal)
        {
            var result = new Dictionary<Transform, Matrix4x4>();
            foreach (var rig in rigs)
            {
                if (rig.Bones == null || rig.Edited == null || rig.Original == null) continue;
                int n = Mathf.Min(rig.Bones.Length, Mathf.Min(rig.Edited.Length, rig.Original.Length));
                for (int i = 0; i < n; i++)
                {
                    var bone = rig.Bones[i];
                    if (bone == null || result.ContainsKey(bone) || Same(rig.Edited[i], rig.Original[i])) continue;
                    var world = bone.localToWorldMatrix;
                    result[bone] = toOriginal ? world * rig.Edited[i] * rig.Original[i].inverse : world * rig.Original[i] * rig.Edited[i].inverse;
                }
            }
            return result;
        }

        /// <summary>Puts the bones at the given world matrices, parents first; returns what they had (for <see cref="Restore"/>).</summary>
        public static List<Pose> Apply(Dictionary<Transform, Matrix4x4> worlds)
        {
            var before = new List<Pose>(worlds.Count);
            foreach (var pair in worlds.OrderBy(p => Depth(p.Key)))
            {
                var bone = pair.Key;
                if (bone == null) continue;
                before.Add(new Pose { Bone = bone, Position = bone.localPosition, Rotation = bone.localRotation, Scale = bone.localScale });
                var local = bone.parent != null ? bone.parent.worldToLocalMatrix * pair.Value : pair.Value;
                bone.localPosition = local.GetColumn(3);
                bone.localRotation = local.rotation;
                bone.localScale = local.lossyScale;
            }
            return before;
        }

        public static void Restore(List<Pose> poses)
        {
            if (poses == null) return;
            foreach (var pose in poses)
            {
                if (pose.Bone == null) continue;
                pose.Bone.localPosition = pose.Position;
                pose.Bone.localRotation = pose.Rotation;
                pose.Bone.localScale = pose.Scale;
            }
        }

        public static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false;
            return true;
        }

        private static int Depth(Transform t) { int d = 0; for (var p = t != null ? t.parent : null; p != null; p = p.parent) d++; return d; }
    }
}
