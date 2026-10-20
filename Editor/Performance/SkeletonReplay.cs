using System.Collections.Generic;
using System.Linq;
using Orbiters.Contracts;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// The avatar's pose exactly as VRChat showed it, from the capture kit's skeleton band (<c>skeleton.bin</c>): world
    /// rotations of the humanoid bones and the hips' world position, interpolated between frames and applied to the
    /// avatar copy parents first. Nothing to estimate: VRChat's IK, locomotion and animation are already in it.
    /// </summary>
    internal sealed class SkeletonReplay
    {
        /// <summary>Frames further apart than this are a gap (the band was unreadable): no pose there.</summary>
        public const double MaxGap = 0.25;

        readonly List<SkeletonFrame> frames;
        readonly (int bone, Transform transform)[] order;
        readonly Transform hips;

        public SkeletonReplay(List<SkeletonFrame> frames, Animator animator)
        {
            this.frames = frames.OrderBy(f => f.TimeNs).ToList();
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            // Parents before children, whatever the HumanBodyBones order says (UpperChest comes last there).
            order = Enumerable.Range(0, SkeletonPose.Bones)
                .Select(b => (bone: b, transform: animator.GetBoneTransform((HumanBodyBones)b)))
                .Where(x => x.transform != null)
                .OrderBy(x => Depth(x.transform))
                .ToArray();
        }

        public int Count => frames.Count;

        static int Depth(Transform t)
        {
            int d = 0;
            for (; t.parent != null; t = t.parent) d++;
            return d;
        }

        /// <summary>Poses the copy at session time <paramref name="seconds"/>; false in a gap.</summary>
        public bool Apply(double seconds)
        {
            if (frames.Count == 0) return false;
            ulong t = (ulong)System.Math.Max(0, System.Math.Round(seconds * 1e9));
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (frames[mid].TimeNs <= t) lo = mid;
                else hi = mid - 1;
            }
            var a = frames[lo];
            var b = lo + 1 < frames.Count ? frames[lo + 1] : a;
            if (a.TimeNs > t) b = a;
            double span = (b.TimeNs - a.TimeNs) * 1e-9;
            double away = a.TimeNs > t ? (a.TimeNs - t) * 1e-9 : (t - a.TimeNs) * 1e-9;
            if (span > MaxGap * 2 || (span == 0 && away > MaxGap)) return false;
            float k = span > 0 ? Mathf.Clamp01((float)((t - a.TimeNs) * 1e-9 / span)) : 0f;
            foreach (var (bone, transform) in order)
            {
                var qa = a.Pose.Rotations[bone];
                var qb = b.Pose.Rotations[bone];
                if (qa == null && qb == null) continue;
                var ra = qa != null ? new Quaternion(qa[0], qa[1], qa[2], qa[3]) : new Quaternion(qb[0], qb[1], qb[2], qb[3]);
                var rb = qb != null ? new Quaternion(qb[0], qb[1], qb[2], qb[3]) : ra;
                transform.rotation = Quaternion.Slerp(ra, rb, k);
            }
            if (hips != null)
                hips.position = Vector3.Lerp(new Vector3(a.Pose.HipsX, a.Pose.HipsY, a.Pose.HipsZ), new Vector3(b.Pose.HipsX, b.Pose.HipsY, b.Pose.HipsZ), k);
            return true;
        }
    }
}
