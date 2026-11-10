using Unity.Profiling;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What a brush dab works on, for sculpt and weight strokes alike: the welded vertices within the radius of the
    /// brush in the world picture, each once (by its representative), with the falloff and the mask's hold. A Burst
    /// kernel (<see cref="StrokeKernels.Gather"/>) writes them into the target's stroke scratch: a dab allocates nothing
    /// but the BVH's pin.
    /// </summary>
    internal static class StrokeGather
    {
        private static readonly ProfilerMarker GatherMarker = new ProfilerMarker("Mixer.Stroke.Gather");

        /// <summary>
        /// Fills <see cref="StrokeScratch.GatherWelded"/> and <see cref="StrokeScratch.GatherWeight"/>; returns how many.
        /// </summary>
        /// <param name="ignoreMask">True for the mask brush itself (it paints the mask, the mask does not hold it).</param>
        public static int Sphere(EditTarget target, StrokeScratch scratch, Vector3 center, float radius, Falloff falloff, bool ignoreMask)
        {
            using (GatherMarker.Auto())
            {
                var doc = target.Document;
                var bvh = target.Bvh;
                int mark = scratch.NextMark();
                return StrokeKernels.Gather(bvh, doc.Triangles, doc.Neighbors, target.World, doc.Mask, scratch.GatherMark, mark,
                    center, radius, falloff, ignoreMask, scratch.GatherWelded, scratch.GatherWeight);
            }
        }
    }
}
