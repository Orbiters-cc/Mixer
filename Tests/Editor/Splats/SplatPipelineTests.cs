using System;
using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Mixer.Splats;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats.Tests
{
    /// <summary>
    /// What the renderer decides on the CPU: the sort's dispatches (run here kernel by kernel as SplatSort.compute runs
    /// them, against Array.Sort), when an order still serves, and which quads the projection pass leaves out (against
    /// a brute-force look at every pixel).
    /// </summary>
    public sealed class SplatPipelineTests
    {
        /// <summary>The dispatches the renderer recorded before the schedule was taken out of it, step for step.</summary>
        static List<SplatSorting.Step> OldDispatches(int padded)
        {
            var steps = new List<SplatSorting.Step>
            {
                new SplatSorting.Step { Kernel = SplatSorting.Kernel.CalcKeys, Groups = (padded + 255) / 256 },
                new SplatSorting.Step { Kernel = SplatSorting.Kernel.SortLocal, Groups = padded / 512 },
            };
            for (int k = 1024; k <= padded; k <<= 1)
            {
                for (int j = k >> 1; j >= 512; j >>= 1)
                    steps.Add(new SplatSorting.Step { Kernel = SplatSorting.Kernel.MergeGlobal, K = k, J = j, Groups = (padded / 2 + 255) / 256 });
                steps.Add(new SplatSorting.Step { Kernel = SplatSorting.Kernel.MergeLocal, K = k, Groups = padded / 512 });
            }
            return steps;
        }

        [Test]
        public void TheScheduleIsTheOneRecordedBefore()
        {
            foreach (int padded in new[] { 512, 1024, 2048, 1 << 16, SplatCloud.MaxCount })
                Assert.That(SplatSorting.Schedule(padded), Is.EqualTo(OldDispatches(padded).ToArray()), "padded " + padded);
            Assert.That(SplatSorting.Padded(1), Is.EqualTo(512));
            Assert.That(SplatSorting.Padded(513), Is.EqualTo(1024));
            Assert.That(SplatSorting.Padded(SplatCloud.MaxCount), Is.EqualTo(SplatCloud.MaxCount));
        }

        // ---- SplatSort.compute's kernels, thread by thread (threads of one barrier step touch disjoint pairs, so running
        // them one after the other is what the GPU computes) ----

        static void SwapIf(uint[] keys, uint[] values, int a, int b, bool ascending)
        {
            if ((keys[a] > keys[b]) == ascending)
            {
                (keys[a], keys[b]) = (keys[b], keys[a]);
                (values[a], values[b]) = (values[b], values[a]);
            }
        }

        static void Run(SplatSorting.Step step, uint[] keys, uint[] values, uint[] distanceKeys, int count)
        {
            int padded = keys.Length;
            switch (step.Kernel)
            {
                case SplatSorting.Kernel.CalcKeys:
                    for (int i = 0; i < step.Groups * 256; i++)
                    {
                        if (i >= padded) continue;
                        keys[i] = i < count ? distanceKeys[i] : 0xffffffffu;
                        values[i] = (uint)i;
                    }
                    break;
                case SplatSorting.Kernel.SortLocal:
                    for (int g = 0; g < step.Groups; g++)
                    {
                        int b = g * 512;
                        for (int k = 2; k <= 512; k <<= 1)
                            for (int j = k >> 1; j > 0; j >>= 1)
                                for (int gi = 0; gi < 256; gi++)
                                {
                                    int i = ((gi & ~(j - 1)) << 1) | (gi & (j - 1));
                                    SwapIf(keys, values, b + i, b + (i | j), ((b + i) & k) == 0);
                                }
                    }
                    break;
                case SplatSorting.Kernel.MergeGlobal:
                    for (int t = 0; t < step.Groups * 256; t++)
                    {
                        if (t >= padded / 2) continue;
                        int i = ((t & ~(step.J - 1)) << 1) | (t & (step.J - 1));
                        SwapIf(keys, values, i, i | step.J, (i & step.K) == 0);
                    }
                    break;
                default:
                    for (int g = 0; g < step.Groups; g++)
                    {
                        int b = g * 512;
                        for (int j = 256; j > 0; j >>= 1)
                            for (int gi = 0; gi < 256; gi++)
                            {
                                int i = ((gi & ~(j - 1)) << 1) | (gi & (j - 1));
                                SwapIf(keys, values, b + i, b + (i | j), ((b + i) & step.K) == 0);
                            }
                    }
                    break;
            }
        }

        [Test]
        public void TheSortPutsTheFarthestFirstLikeArraySort()
        {
            var rng = new System.Random(23);
            foreach (int count in new[] { 1, 300, 512, 777, 2048, 5000, 9000 })
            {
                var distances = new float[count];
                for (int i = 0; i < count; i++)
                    distances[i] = rng.Next(0, 5) == 0 ? 3.5f : (float)(rng.NextDouble() * 400.0); // ties too
                var distanceKeys = Array.ConvertAll(distances, SplatSorting.Key);
                int padded = SplatSorting.Padded(count);
                var keys = new uint[padded];
                var values = new uint[padded];
                foreach (SplatSorting.Step step in SplatSorting.Schedule(padded))
                    Run(step, keys, values, distanceKeys, count);
                var reference = (float[])distances.Clone();
                Array.Sort(reference);
                Array.Reverse(reference);
                var seen = new bool[padded];
                for (int i = 0; i < padded; i++)
                {
                    Assert.That(seen[values[i]], Is.False, "each index once");
                    seen[values[i]] = true;
                }
                for (int i = 0; i < count; i++)
                {
                    Assert.That(values[i], Is.LessThan((uint)count), "the padding sorts last, at " + i);
                    Assert.That(distances[values[i]], Is.EqualTo(reference[i]), "back to front at " + i + " of " + count);
                }
            }
        }

        [Test]
        public void KeysPutFartherSplatsFirst()
        {
            var rng = new System.Random(29);
            for (int i = 0; i < 10000; i++)
            {
                float a = (float)(rng.NextDouble() * 1e4), b = (float)(rng.NextDouble() * 1e4);
                Assert.That(SplatSorting.Key(a) < SplatSorting.Key(b), Is.EqualTo(a > b), a + " vs " + b);
            }
            Assert.That(SplatSorting.Key(1e30f), Is.LessThan(0xffffffffu), "every splat before the padding");
        }

        /// <summary>The bound the re-sort distance rests on: moving the eye by δ, two splats swap only when their
        /// distances differed by less than |δ| times the largest angle between them seen along the way.</summary>
        [Test]
        public void SplatsSwapOnlyWithinTheBoundTheResortDistanceRestsOn()
        {
            var rng = new System.Random(31);
            double[] R(double s) => new[] { (rng.NextDouble() * 2 - 1) * s, (rng.NextDouble() * 2 - 1) * s, (rng.NextDouble() * 2 - 1) * s };
            double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            // The angle between a and b seen from e, in double (atan2 of the cross and dot products stays exact when small).
            double Angle(double[] a, double[] b, double[] e)
            {
                double ax = a[0] - e[0], ay = a[1] - e[1], az = a[2] - e[2], bx = b[0] - e[0], by = b[1] - e[1], bz = b[2] - e[2];
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz);
            }
            int swaps = 0;
            for (int trial = 0; trial < 40; trial++)
            {
                var points = new double[60][];
                for (int i = 0; i < points.Length; i++) points[i] = R(3);
                double[] eye = R(6);
                // Moves far larger than the renderer allows, so that pairs do swap.
                double[] direction = R(1);
                double length = (0.05 + rng.NextDouble() * 0.5) / Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]);
                double[] At(double t) => new[] { eye[0] + direction[0] * length * t, eye[1] + direction[1] * length * t, eye[2] + direction[2] * length * t };
                double move = Distance(At(1), eye);
                for (int i = 0; i < points.Length; i++)
                for (int j = i + 1; j < points.Length; j++)
                {
                    double before = Distance(points[i], eye) - Distance(points[j], eye);
                    double after = Distance(points[i], At(1)) - Distance(points[j], At(1));
                    if (Math.Sign(before) == Math.Sign(after))
                        continue;
                    swaps++;
                    double angle = 0;
                    for (int s = 0; s <= 256; s++)
                        angle = Math.Max(angle, Angle(points[i], points[j], At(s / 256.0)));
                    Assert.That(Math.Abs(before), Is.LessThanOrEqualTo(move * angle * 1.01 + 1e-12), "pair " + i + ", " + j);
                }
            }
            Assert.That(swaps, Is.GreaterThan(0), "the moves reordered something");
        }

        [Test]
        public void AnOrderServesUntilTheEyeMovesEnough()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            Matrix4x4 m = Matrix4x4.TRS(new Vector3(0f, 0f, 10f), Quaternion.Euler(0, 30, 0), Vector3.one);
            var eye = new Vector3(0f, 0f, -90f);
            float reach = SplatSorting.ResortDistance(bounds, m, eye);
            // About 99 m from the box: half a millimetre plus 0.1 % of that.
            Assert.That(reach, Is.EqualTo(0.0005f + 0.001f * 99f).Within(0.002f));
            Assert.That(SplatSorting.ResortDistance(bounds, m, new Vector3(0f, 0f, 10f)), Is.EqualTo(SplatSorting.ResortMinimum), "inside the cloud");
            var sorted = new SplatSorting.Sorted { Upload = 3, ObjectToWorld = m, Eye = eye, Stamp = 1 };
            Assert.That(sorted.Serves(3, m, eye, bounds), Is.True, "still, or only turning");
            Assert.That(sorted.Serves(3, m, eye + Vector3.right * reach * 0.9f, bounds), Is.True);
            Assert.That(sorted.Serves(3, m, eye + Vector3.right * reach * 1.1f, bounds), Is.False);
            Assert.That(sorted.Serves(4, m, eye, bounds), Is.False, "another upload");
            Matrix4x4 nudged = m;
            nudged.m03 += 1e-6f;
            Assert.That(sorted.Serves(3, nudged, eye, bounds), Is.False, "any change of the transform");
            Assert.That(sorted.Serves(3, m, new Vector3(float.NaN, 0f, 0f), bounds), Is.False);
        }

        /// <summary>Every quad the projection pass leaves out covers no pixel centre of the screen, whatever its
        /// orientation; and it leaves out every quad whose depth the GPU would clip.</summary>
        [Test]
        public void CulledQuadsCoverNoPixel()
        {
            var screen = new Vector2(64f, 48f);
            var rng = new System.Random(37);
            int kept = 0, culled = 0;
            for (int n = 0; n < 4000; n++)
            {
                float w = (float)(0.5 + rng.NextDouble() * 10);
                // Centres from well off screen to inside, in pixels, then into clip space.
                var centre = new Vector2((float)(rng.NextDouble() * 200 - 68), (float)(rng.NextDouble() * 180 - 66));
                var clip = new Vector4((centre.x / screen.x * 2f - 1f) * w, (centre.y / screen.y * 2f - 1f) * w, (float)(rng.NextDouble() * 1.6 - 0.8) * w, w);
                float angle = (float)(rng.NextDouble() * Math.PI * 2);
                var e1 = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                var axis1 = e1 * (float)(rng.NextDouble() * 40 + 0.3);
                var axis2 = new Vector2(-e1.y, e1.x) * (float)(rng.NextDouble() * 20 + 0.3);
                bool onScreen = SplatCulling.OnScreen(clip, axis1, axis2, screen);
                if (onScreen) kept++;
                else culled++;
                if (onScreen)
                    continue;
                float det = axis1.x * axis2.y - axis1.y * axis2.x;
                for (int y = 0; y < (int)screen.y; y++)
                for (int x = 0; x < (int)screen.x; x++)
                {
                    // The pixel centre in the quad's own coordinates (corners at ±1, ±1).
                    Vector2 d = new Vector2(x + 0.5f, y + 0.5f) - centre;
                    float a = (d.x * axis2.y - d.y * axis2.x) / det;
                    float b = (axis1.x * d.y - axis1.y * d.x) / det;
                    Assert.That(Mathf.Abs(a) > 1f || Mathf.Abs(b) > 1f, Is.True, "culled quad " + n + " covers pixel " + x + ", " + y);
                }
            }
            Assert.That(kept, Is.GreaterThan(500));
            Assert.That(culled, Is.GreaterThan(500));
            // Depth outside ±w: clipped by the GPU whatever covers the screen.
            Assert.That(SplatCulling.OnScreen(new Vector4(0f, 0f, 1.01f, 1f), Vector2.right * 10f, Vector2.up * 10f, screen), Is.False, "beyond the far plane");
            Assert.That(SplatCulling.OnScreen(new Vector4(0f, 0f, -1.01f, 1f), Vector2.right * 10f, Vector2.up * 10f, screen), Is.False, "nearer than the near plane");
            Assert.That(SplatCulling.OnScreen(new Vector4(0f, 0f, 0.5f, 1f), Vector2.right * 10f, Vector2.up * 10f, screen), Is.True);
            Assert.That(SplatCulling.OnScreen(new Vector4(0f, 0f, 0f, -1f), Vector2.right * 10f, Vector2.up * 10f, screen), Is.False, "behind the eye");
        }
    }
}
