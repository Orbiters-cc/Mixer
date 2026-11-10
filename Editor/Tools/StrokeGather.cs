using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What a brush dab works on, for sculpt and weight strokes alike: the welded vertices within the radius of the
    /// brush in the world picture, each once (by its representative), with the falloff and the mask's hold. Everything
    /// comes from the target's stroke scratch: a dab allocates nothing.
    /// </summary>
    internal static class StrokeGather
    {
        private static readonly ProfilerMarker GatherMarker = new ProfilerMarker("Mixer.Stroke.Gather");

        /// <param name="ignoreMask">True for the mask brush itself (it paints the mask, the mask does not hold it).</param>
        public static void Sphere(EditTarget target, StrokeScratch scratch, Vector3 center, float radius, Falloff falloff, bool ignoreMask, List<int> welded, List<float> weights)
        {
            using (GatherMarker.Auto())
            {
                var doc = target.Document;
                var n = doc.Neighbors;
                int mark = scratch.NextMark();
                var gathered = scratch.GatherMark;
                var triangles = scratch.Triangles;
                target.Bvh.TrianglesNear(center, radius, triangles);
                welded.Clear();
                weights.Clear();
                var world = target.World;
                var tris = doc.Triangles;
                float r2 = radius * radius;
                float inverse = 1f / radius;
                foreach (int t in triangles)
                {
                    for (int k = t * 3, end = k + 3; k < end; k++)
                    {
                        int w = n.Weld[tris[k]];
                        if (gathered[w] == mark) continue;
                        gathered[w] = mark;
                        int rep = n.Representative[w];
                        var p = world[rep];
                        float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 > r2) continue;
                        float weight = Brush.Curve(falloff, (float)Math.Sqrt(d2) * inverse);
                        if (!ignoreMask) weight *= doc.Free(rep);
                        if (weight <= 0f) continue;
                        welded.Add(w);
                        weights.Add(weight);
                    }
                }
            }
        }
    }
}
