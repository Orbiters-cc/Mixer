using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using Orbiters.Mixer.Splats;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats.Tests
{
    /// <summary>
    /// The cloud's GPU layout (<see cref="SplatLayout"/>) against references that do not share its code: Unity's own
    /// half conversion, a brute-force nearest half, the float values it quantises, and the managed PLY reader it
    /// replaced (re-written here as it was, so a difference in arithmetic shows).
    /// </summary>
    public sealed class SplatLayoutTests
    {
        [Test]
        public void TheRecordsAreAsLongAsTheShadersRead()
        {
            Assert.That(Marshal.SizeOf<SplatLayout.Splat>(), Is.EqualTo(SplatLayout.Stride));
            Assert.That(Marshal.SizeOf<SplatLayout.Seen>(), Is.EqualTo(SplatLayout.SeenStride));
            Assert.That(SplatLayout.Stride, Is.EqualTo(32));
            Assert.That(SplatLayout.SeenStride, Is.EqualTo(12));
        }

        /// <summary>The value IEEE 754 gives the half <paramref name="h"/> (finite ones), by its formula.</summary>
        static double HalfValue(uint h)
        {
            int e = (int)((h >> 10) & 0x1F);
            double m = h & 0x3FF;
            double v = e == 0 ? m / 1024.0 * Math.Pow(2, -14) : (1.0 + m / 1024.0) * Math.Pow(2, e - 15);
            return (h & 0x8000) != 0 ? -v : v;
        }

        [Test]
        public void HalvesReadAsIeeeSays()
        {
            for (uint h = 0; h < 65536; h++)
            {
                if (((h >> 10) & 0x1F) == 0x1F)
                    continue; // infinities and NaN are never written
                float got = SplatLayout.HalfToFloat(h);
                Assert.That((double)got, Is.EqualTo(HalfValue(h)), "half " + h.ToString("X4"));
                Assert.That(got < 0f || (got == 0f && 1f / got < 0f), Is.EqualTo((h & 0x8000) != 0), "sign of half " + h.ToString("X4"));
            }
        }

        /// <summary>Every float rounds to the nearest half (ties to the even one), as f32tof16 does, and saturates.</summary>
        [Test]
        public void FloatsRoundToTheNearestHalf()
        {
            // Every finite positive half's value, in order (the bit patterns of positive halves sort like their values).
            var values = new double[0x7C00];
            for (uint h = 0; h < 0x7C00; h++)
                values[h] = HalfValue(h);
            var rng = new System.Random(5);
            var probes = new List<float> { 0f, 1f, 0.5f, 65504f, 65519f, 6.1035156e-5f, 5.9604645e-8f, 2.9802322e-8f, 2.98023259e-8f, 1e-9f, 0.0037f, 123f, 400f, 2049f };
            // Exact midpoints between neighbours: the tie goes to the even pattern.
            for (int i = 0; i < 200; i++)
            {
                int h = rng.Next(0, 0x7BFF);
                probes.Add((float)((values[h] + values[h + 1]) / 2.0));
            }
            for (int i = 0; i < 4000; i++)
                probes.Add((float)Math.Exp(rng.NextDouble() * 30.0 - 19.0));
            foreach (float p in probes)
            {
                foreach (float f in new[] { p, -p })
                {
                    uint got = SplatLayout.FloatToHalf(f);
                    uint want = NearestHalf(values, Math.Abs((double)f)) | (f < 0f || (f == 0f && 1f / f < 0f) ? 0x8000u : 0u);
                    Assert.That(got, Is.EqualTo(want), "float " + f.ToString("R"));
                }
            }
            Assert.That(SplatLayout.FloatToHalf(70000f), Is.EqualTo(0x7BFFu), "saturated, not infinite");
            Assert.That(SplatLayout.FloatToHalf(-1e30f), Is.EqualTo(0xFBFFu));
        }

        static uint NearestHalf(double[] values, double x)
        {
            if (x >= 65504.0)
                return 0x7BFF;
            int lo = 0, hi = values.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (values[mid] <= x) lo = mid;
                else hi = mid;
            }
            double dl = x - values[lo], dh = values[hi] - x;
            if (dl < dh) return (uint)lo;
            if (dh < dl) return (uint)hi;
            return (lo & 1) == 0 ? (uint)lo : (uint)hi;
        }

        static IEnumerable<Quaternion> Rotations()
        {
            yield return Quaternion.identity;
            yield return new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
            yield return new Quaternion(-0.5f, 0.5f, -0.5f, 0.5f);
            yield return new Quaternion(0f, -1f, 0f, 0f);
            yield return new Quaternion(0.70710678f, 0f, 0f, -0.70710678f);
            yield return new Quaternion(0f, 0f, -0.70710678f, -0.70710678f);
            var rng = new System.Random(11);
            for (int i = 0; i < 2000; i++)
            {
                var q = new Quaternion((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (n > 1e-3f)
                    yield return new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
            }
        }

        static SplatLayout.Splat With(Quaternion q, Vector3 ln, bool sky = false) =>
            SplatLayout.Encode(1f, 2f, 3f, q.x, q.y, q.z, q.w, ln.x, ln.y, ln.z, 10, 20, 30, 40, sky);

        [Test]
        public void RotationsKeepEachComponentWithin3e6()
        {
            foreach (Quaternion q in Rotations())
            {
                Quaternion got = SplatLayout.RotationOf(With(q, Vector3.zero));
                // q and −q are the same rotation: compared with the one on the same side.
                float sign = Quaternion.Dot(q, got) < 0f ? -1f : 1f;
                for (int c = 0; c < 4; c++)
                    Assert.That(sign * got[c], Is.EqualTo(q[c]).Within(2.5e-6f), q + " component " + c);
            }
            Assert.That(SplatLayout.RotationOf(With(Quaternion.identity, Vector3.zero)), Is.EqualTo(Quaternion.identity), "the identity exactly");
        }

        [Test]
        public void ScalesKeepTheirSizeWithin20PartsPerMillion()
        {
            var rng = new System.Random(13);
            for (int i = 0; i < 5000; i++)
            {
                var ln = new Vector3((float)(rng.NextDouble() * 46 - 31), (float)(rng.NextDouble() * 46 - 31), (float)(rng.NextDouble() * 46 - 31));
                Vector3 got = SplatLayout.ScaleOf(With(Quaternion.identity, ln));
                for (int c = 0; c < 3; c++)
                {
                    double want = Math.Exp(ln[c]);
                    Assert.That(Math.Abs(got[c] / want - 1.0), Is.LessThan(2e-5), "ln " + ln[c]);
                }
            }
            // Outside [−32, 16]: clamped (a splat 1e-20 m across and one bigger than the planet are both left as drawable).
            Vector3 clamped = SplatLayout.LnScaleOf(With(Quaternion.identity, new Vector3(-100f, 100f, float.NaN)));
            Assert.That(clamped.x, Is.EqualTo(SplatLayout.LnScaleMin).Within(1e-5f));
            Assert.That(clamped.y, Is.EqualTo(SplatLayout.LnScaleMax).Within(1e-4f));
            Assert.That(clamped.z, Is.EqualTo(SplatLayout.LnScaleMin).Within(1e-5f));
        }

        [Test]
        public void TheRestOfASplatIsKeptExactly()
        {
            var q = Quaternion.Euler(10, 20, 30);
            SplatLayout.Splat s = SplatLayout.Encode(-1.25e-3f, 4000.5f, float.Epsilon, q.x, q.y, q.z, q.w, -2f, -3f, -4f, 1, 128, 254, 255, true);
            Assert.That(new Vector3(s.x, s.y, s.z), Is.EqualTo(new Vector3(-1.25e-3f, 4000.5f, float.Epsilon)));
            Assert.That(SplatLayout.ColorOf(s), Is.EqualTo(new Color32(1, 128, 254, 255)));
            Assert.That(SplatLayout.IsSky(s), Is.True);
            Assert.That(SplatLayout.IsSky(SplatLayout.Encode(0f, 0f, 0f, q.x, q.y, q.z, q.w, -2f, -3f, -4f, 1, 128, 254, 255, false)), Is.False);
            // The sky's bit leaves the rotation as it was.
            AreSame(SplatLayout.RotationOf(s), SplatLayout.RotationOf(SplatLayout.Encode(0f, 0f, 0f, q.x, q.y, q.z, q.w, -2f, -3f, -4f, 1, 128, 254, 255, false)));
        }

        static void AreSame(Quaternion a, Quaternion b)
        {
            for (int c = 0; c < 4; c++)
                Assert.That(a[c], Is.EqualTo(b[c]));
        }

        [Test]
        public void WhatTheKeyframesSawKeepsWhatTheRendererUses()
        {
            foreach (float weight in new[] { -1f, 0f, 0.001f, 0.0037f, 0.01f, 0.5f, 1f })
            foreach (float views in new[] { 0f, 1f, 3f, 5f, 2048f, 3001f, 1e6f })
            {
                SplatLayout.Seen t = SplatLayout.EncodeSeen(weight, views, 0x8421, 1234.5678f, 37.25f);
                Vector4 got = SplatLayout.SeenOf(t);
                Assert.That(got.x, Is.EqualTo(weight).Within(Math.Abs(weight) * 4.9e-4f + 1e-7f));
                if (views <= 2048f) Assert.That(got.y, Is.EqualTo(views), "keyframe counts exact to 2048");
                else Assert.That(got.y, Is.GreaterThan(5f), "more than the renderer distinguishes");
                Assert.That(got.z, Is.EqualTo(0x8421));
                Assert.That(got.w, Is.EqualTo(1234.5678f), "the rate as it was");
                Assert.That(SplatLayout.BeyondOf(t), Is.EqualTo(37.25f));
            }
            // The sector bits as D3D's float-to-uint conversion gave the old shader (uint)seen.z: negative 0, too large all.
            Assert.That(SplatLayout.SectorBits(-3f), Is.EqualTo(0u));
            Assert.That(SplatLayout.SectorBits(float.NaN), Is.EqualTo(0u));
            Assert.That(SplatLayout.SectorBits(65535f), Is.EqualTo(0xFFFFu));
            Assert.That(SplatLayout.SectorBits(65536f + 5f), Is.EqualTo(5u), "only the 16 sector bits are ever read");
            Assert.That(SplatLayout.SectorBits(1e20f), Is.EqualTo(0xFFFFu));
            Assert.That(SplatLayout.SectorBits(7.9f), Is.EqualTo(7u));
        }

        [Test]
        public void SelectFindsWhatASortPutsThere()
        {
            var rng = new System.Random(17);
            var cases = new List<float[]>
            {
                new[] { 3f },
                new[] { 2f, 1f },
                new float[100], // all equal
            };
            var sorted = new float[1000];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = i * 0.5f;
            cases.Add(sorted);
            var reversed = (float[])sorted.Clone();
            Array.Reverse(reversed);
            cases.Add(reversed);
            for (int n = 0; n < 30; n++)
            {
                var a = new float[rng.Next(1, 3000)];
                for (int i = 0; i < a.Length; i++) a[i] = rng.Next(0, 4) == 0 ? 7f : (float)(rng.NextDouble() * 100 - 50);
                cases.Add(a);
            }
            foreach (float[] values in cases)
            {
                var reference = (float[])values.Clone();
                Array.Sort(reference);
                foreach (int k in new[] { 0, values.Length / 2, values.Length - 1 })
                    Assert.That(PlyReader.Select((float[])values.Clone(), k), Is.EqualTo(reference[k]), "n " + values.Length + ", k " + k);
            }
        }

        // ---- The reader against the managed one it replaced ----

        /// <summary>A PLY of <paramref name="count"/> random splats, each property in the type given, values from
        /// <paramref name="value"/> (by splat and property name).</summary>
        static byte[] Ply(int count, (string name, string type)[] properties, Func<int, string, double> value)
        {
            var header = new StringBuilder("ply\nformat binary_little_endian 1.0\nelement vertex " + count + "\n");
            foreach (var p in properties)
                header.Append("property ").Append(p.type).Append(' ').Append(p.name).Append('\n');
            header.Append("end_header\n");
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(header.ToString()));
            for (int i = 0; i < count; i++)
                foreach (var p in properties)
                {
                    double v = value(i, p.name);
                    switch (p.type)
                    {
                        case "double": bytes.AddRange(BitConverter.GetBytes(v)); break;
                        case "uchar": bytes.Add((byte)v); break;
                        case "short": bytes.AddRange(BitConverter.GetBytes((short)v)); break;
                        default: bytes.AddRange(BitConverter.GetBytes((float)v)); break;
                    }
                }
            return bytes.ToArray();
        }

        /// <summary>One splat as the managed reader before this layout produced it (same arithmetic, Mathf).</summary>
        struct Old
        {
            public Vector3 position, scale;
            public Quaternion rotation;
            public Color32 color;
            public Vector4 seen;
            public bool sky;
            public float beyond;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        static List<Old> OldRead(int count, Func<int, string, float> P, bool hasSeen)
        {
            var kept = new List<Old>();
            for (int i = 0; i < count; i++)
            {
                float At(string name) => P(i, name);
                var position = new Vector3(At("x"), At("y"), -At("z"));
                var scale = new Vector3(Mathf.Exp(At("scale_0")), Mathf.Exp(At("scale_1")), Mathf.Exp(At("scale_2")));
                if (!Finite(position) || !Finite(Vector3.Scale(scale, scale)))
                    continue;
                var q = new Quaternion(-At("rot_1"), -At("rot_2"), At("rot_3"), At("rot_0"));
                float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (float.IsNaN(norm) || float.IsInfinity(norm))
                    continue;
                byte Channel(float dc) => (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + PlyReader.ShC0 * dc) * 255f), 0, 255);
                float opacity = 1f / (1f + Mathf.Exp(-At("opacity")));
                var old = new Old
                {
                    position = position,
                    scale = scale,
                    rotation = norm > 1e-12f ? new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm) : Quaternion.identity,
                    color = new Color32(Channel(At("f_dc_0")), Channel(At("f_dc_1")), Channel(At("f_dc_2")), (byte)Mathf.Clamp(Mathf.RoundToInt(opacity * 255f), 0, 255)),
                    seen = new Vector4(-1f, 0f, 0f, 0f),
                };
                if (hasSeen)
                {
                    float F(string name) { float x = At(name); return float.IsNaN(x) || float.IsInfinity(x) ? 0f : x; }
                    old.sky = At("sky") > 0.5f;
                    old.seen = new Vector4(F("seen_weight"), F("seen_views"), F("seen_sectors"), F("seen_rate"));
                    old.beyond = F("seen_beyond");
                }
                kept.Add(old);
            }
            return kept;
        }

        [Test]
        public void ReadsWhatTheManagedReaderRead()
        {
            var properties = new[]
            {
                ("x", "double"), ("y", "float"), ("z", "float"), ("nx", "uchar"), ("f_dc_0", "float"), ("f_dc_1", "short"), ("f_dc_2", "float"),
                ("sky", "uchar"), ("seen_weight", "float"), ("seen_views", "short"), ("seen_sectors", "float"), ("seen_rate", "double"), ("seen_beyond", "float"),
                ("opacity", "float"), ("scale_0", "float"), ("scale_1", "double"), ("scale_2", "float"),
                ("rot_0", "float"), ("rot_1", "float"), ("rot_2", "float"), ("rot_3", "float"),
            };
            const int count = 6000;
            var rng = new System.Random(19);
            var values = new Dictionary<(int, string), double>();
            foreach (var p in properties)
                for (int i = 0; i < count; i++)
                {
                    double v;
                    switch (p.Item1)
                    {
                        case "x": case "y": case "z": v = rng.NextDouble() * 200 - 100; break;
                        case "f_dc_0": case "f_dc_2": v = rng.NextDouble() * 4 - 2; break;
                        case "f_dc_1": v = rng.Next(-2, 3); break;
                        case "sky": v = rng.Next(0, 2); break;
                        case "seen_weight": v = rng.NextDouble(); break;
                        case "seen_views": v = rng.Next(0, 40); break;
                        case "seen_sectors": v = rng.Next(0, 65536); break;
                        case "seen_rate": v = rng.NextDouble() * 5000; break;
                        case "seen_beyond": v = rng.NextDouble() * 300; break;
                        case "opacity": v = rng.NextDouble() * 16 - 8; break;
                        case "scale_0": case "scale_1": case "scale_2": v = rng.NextDouble() * 12 - 9; break;
                        case "nx": v = 7; break;
                        default: v = rng.NextDouble() * 2 - 1; break;
                    }
                    values[(i, p.Item1)] = v;
                }
            // Diverged splats the reader leaves out, and a rotation of length zero.
            values[(10, "x")] = double.NaN;
            values[(20, "scale_1")] = 300.0;
            values[(30, "rot_2")] = double.PositiveInfinity;
            foreach (string r in new[] { "rot_0", "rot_1", "rot_2", "rot_3" }) values[(40, r)] = 0.0;
            values[(50, "seen_weight")] = double.NaN;
            // Opacities and colours that land exactly between two bytes (half to even).
            values[(60, "opacity")] = 0.0;
            values[(61, "f_dc_0")] = (0.5 / 255.0 * 255.0 - 0.5) / PlyReader.ShC0;

            byte[] ply = Ply(count, properties, (i, name) => values[(i, name)]);
            // What each property reads as: the value in its type, then a float.
            float Typed(int i, string name)
            {
                double v = values[(i, name)];
                foreach (var p in properties)
                    if (p.Item1 == name)
                        switch (p.Item2)
                        {
                            case "double": return (float)v;
                            case "uchar": return (byte)v;
                            case "short": return (short)v;
                            default: return (float)v;
                        }
                throw new ArgumentException(name);
            }
            List<Old> want = OldRead(count, Typed, true);
            SplatCloud cloud = PlyReader.Read(ply);
            try
            {
                Assert.That(cloud.Count, Is.EqualTo(want.Count));
                Assert.That(cloud.Count, Is.EqualTo(count - 3), "three diverged splats");
                for (int i = 0; i < want.Count; i++)
                {
                    Old o = want[i];
                    Assert.That(cloud.PositionOf(i), Is.EqualTo(o.position), "position " + i);
                    Assert.That(cloud.ColorOf(i), Is.EqualTo(o.color), "colour " + i);
                    Assert.That(cloud.IsSky(i), Is.EqualTo(o.sky), "sky " + i);
                    Quaternion r = cloud.RotationOf(i);
                    Assert.That(Mathf.Abs(Quaternion.Dot(r, o.rotation)), Is.EqualTo(1f).Within(1e-5f), "rotation " + i);
                    Vector3 s = cloud.ScaleOf(i);
                    for (int c = 0; c < 3; c++)
                        Assert.That(s[c] / o.scale[c], Is.EqualTo(1f).Within(2e-5f), "scale " + i);
                    Vector4 seen = cloud.SeenOf(i);
                    Assert.That(seen.x, Is.EqualTo(o.seen.x).Within(Mathf.Abs(o.seen.x) * 4.9e-4f + 1e-7f), "weight " + i);
                    Assert.That(seen.y, Is.EqualTo(o.seen.y), "views " + i);
                    Assert.That(seen.z, Is.EqualTo(o.seen.z), "sectors " + i);
                    Assert.That(seen.w, Is.EqualTo(o.seen.w), "rate " + i);
                    Assert.That(cloud.BeyondOf(i), Is.EqualTo(o.beyond).Within(o.beyond * 4.9e-4f), "beyond " + i);
                }
                // The bounds hold every centre and touch the extremes.
                Vector3 min = want[0].position, max = want[0].position;
                foreach (Old o in want)
                {
                    min = Vector3.Min(min, o.position);
                    max = Vector3.Max(max, o.position);
                }
                // (Bounds keeps a centre and extents: within their rounding.)
                Assert.That(Vector3.Distance(cloud.bounds.min, min), Is.LessThan(1e-4f));
                Assert.That(Vector3.Distance(cloud.bounds.max, max), Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }
    }
}
