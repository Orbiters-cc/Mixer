using System.Runtime.InteropServices;
using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// How a <see cref="SplatCloud"/> holds its splats: in the layout of the GPU buffers the renderer uploads them to
    /// (SplatData.hlsl reads the same), so the importer writes them once and the renderer copies them up as they are.
    /// What each attribute keeps:
    /// <list type="bullet">
    /// <item>position: three 32-bit floats, as read;</item>
    /// <item>colour and opacity: 8 bits each, as the importer always rounded them;</item>
    /// <item>rotation: the "smallest three" of the unit quaternion at 20 bits each (the largest one's index in 2 bits, its
    /// sign folded into the others), each stored component within 6.8e-7 of the float's (about ten float roundings) and
    /// zero exactly, the rebuilt one within 2.4e-6;</item>
    /// <item>scale: the natural logarithm of each axis's standard deviation at 21 bits over [−32, 16] (1.3e-14 m to
    /// 8.9e6 m; outside, clamped), relative error at most 1.2e-5 as stored and 2e-5 once decoded in float;</item>
    /// <item>what the keyframes saw (<see cref="Seen"/>): largest weight, keyframe count and metres beyond the depth
    /// frames' reach as half floats (relative error 4.9e-4, integers exact to 2048, saturated at 65504, where none of the
    /// renderer's uses changes), the compass sectors as their 16 bits, the finest sampling rate as a 32-bit float; the sky
    /// shell's flag as one bit of the rotation's word.</item>
    /// </list>
    /// Everything here is plain arithmetic on blittable values, so Burst jobs can call it.
    /// </summary>
    public static class SplatLayout
    {
        /// <summary>Bytes of one <see cref="Splat"/>.</summary>
        public const int Stride = 32;

        /// <summary>Bytes of one <see cref="Seen"/>.</summary>
        public const int SeenStride = 12;

        public const float LnScaleMin = -32f, LnScaleMax = 16f;
        const uint ScaleSteps = (1u << 21) - 1;
        /// <summary>A rotation component c is stored as round(c · √2 · RotationHalf) + RotationHalf, so 0 (as in the
        /// identity) is exact.</summary>
        const uint RotationHalf = (1u << 19) - 1;
        const double Sqrt2 = 1.4142135623730951;
        const uint SkyBit = 1u << 30;

        /// <summary>One splat (<see cref="Stride"/> bytes): centre, colour (r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt;
        /// 24, encoded sRGB as captured), rotation and scale as the class summary says.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Splat
        {
            public float x, y, z;
            public uint color;
            public uint rotation0, rotation1;
            public uint scale0, scale1;
        }

        /// <summary>What the keyframes saw of one splat (<see cref="SeenStride"/> bytes): weight | views &lt;&lt; 16 (halves),
        /// sectors | beyond &lt;&lt; 16 (bits, half), rate.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Seen
        {
            public uint weightViews;
            public uint sectorsBeyond;
            public float rate;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct Bits
        {
            [FieldOffset(0)] public float f;
            [FieldOffset(0)] public uint u;
        }

        public static uint AsUint(float f) => new Bits { f = f }.u;

        public static float AsFloat(uint u) => new Bits { u = u }.f;

        /// <summary>A splat from its centre, unit rotation (x, y, z, w), the logarithms of its scales, its colour and opacity
        /// and whether it is on the sky shell.</summary>
        public static Splat Encode(float x, float y, float z, float qx, float qy, float qz, float qw, float lnScaleX, float lnScaleY, float lnScaleZ,
            byte r, byte g, byte b, byte a, bool sky)
        {
            EncodeRotation(qx, qy, qz, qw, out uint rotation0, out uint rotation1);
            uint s0 = QuantizeLnScale(lnScaleX), s1 = QuantizeLnScale(lnScaleY), s2 = QuantizeLnScale(lnScaleZ);
            return new Splat
            {
                x = x,
                y = y,
                z = z,
                color = r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24),
                rotation0 = rotation0,
                rotation1 = rotation1 | (sky ? SkyBit : 0u),
                scale0 = s0 | (s1 << 21),
                scale1 = (s1 >> 11) | (s2 << 10),
            };
        }

        /// <summary>What the keyframes saw, from the PLY's values (numbers: the reader turns what is not one into 0).</summary>
        public static Seen EncodeSeen(float weight, float views, float sectors, float rate, float beyond) => new Seen
        {
            weightViews = FloatToHalf(weight) | (FloatToHalf(views) << 16),
            sectorsBeyond = SectorBits(sectors) | (FloatToHalf(beyond) << 16),
            rate = rate,
        };

        /// <summary>The 16 sector bits the shader reads from the PLY's float as D3D converts a float to an unsigned
        /// integer (negative or not a number: 0; too large: all bits).</summary>
        public static uint SectorBits(float sectors)
        {
            if (!(sectors > 0f)) return 0u;
            if (sectors >= 4294967040f) return 0xFFFFu;
            return (uint)sectors & 0xFFFFu;
        }

        static void EncodeRotation(float qx, float qy, float qz, float qw, out uint lo, out uint hi)
        {
            // The largest component (the lowest index on a tie) is left out and rebuilt from the others; the quaternion is
            // negated where it is negative, which is the same rotation.
            float ax = Abs(qx), ay = Abs(qy), az = Abs(qz), aw = Abs(qw);
            uint largest = 0;
            float most = ax;
            if (ay > most) { largest = 1; most = ay; }
            if (az > most) { largest = 2; most = az; }
            if (aw > most) { largest = 3; }
            float kept = largest == 0 ? qx : largest == 1 ? qy : largest == 2 ? qz : qw;
            float sign = kept < 0f ? -1f : 1f;
            float a, b, c;
            if (largest == 0) { a = qy; b = qz; c = qw; }
            else if (largest == 1) { a = qx; b = qz; c = qw; }
            else if (largest == 2) { a = qx; b = qy; c = qw; }
            else { a = qx; b = qy; c = qz; }
            uint ua = QuantizeComponent(a * sign), ub = QuantizeComponent(b * sign), uc = QuantizeComponent(c * sign);
            lo = ua | (ub << 20);
            hi = (ub >> 12) | (uc << 8) | (largest << 28);
        }

        static float Abs(float v) => v < 0f ? -v : v;

        static uint QuantizeComponent(float c)
        {
            // Each component left is within ±1/√2.
            double u = c * Sqrt2 * RotationHalf;
            if (!(u > -(double)RotationHalf)) u = -(double)RotationHalf;
            if (u > RotationHalf) u = RotationHalf;
            return (uint)((long)System.Math.Floor(u + 0.5) + RotationHalf);
        }

        static uint QuantizeLnScale(float ln)
        {
            double t = (ln - (double)LnScaleMin) / ((double)LnScaleMax - LnScaleMin);
            if (!(t > 0.0)) t = 0.0;
            if (t > 1.0) t = 1.0;
            return (uint)(t * ScaleSteps + 0.5);
        }

        public static bool IsSky(in Splat s) => (s.rotation1 & SkyBit) != 0u;

        public static Color32 ColorOf(in Splat s) => new Color32((byte)s.color, (byte)(s.color >> 8), (byte)(s.color >> 16), (byte)(s.color >> 24));

        /// <summary>The rotation as SplatData.hlsl rebuilds it (unit length).</summary>
        public static Quaternion RotationOf(in Splat s)
        {
            uint qa = s.rotation0 & 0xFFFFFu;
            uint qb = (s.rotation0 >> 20) | ((s.rotation1 & 0xFFu) << 12);
            uint qc = (s.rotation1 >> 8) & 0xFFFFFu;
            uint largest = (s.rotation1 >> 28) & 3u;
            const float step = (float)(1.0 / (RotationHalf * Sqrt2));
            float a = ((float)qa - RotationHalf) * step;
            float b = ((float)qb - RotationHalf) * step;
            float c = ((float)qc - RotationHalf) * step;
            float rest = 1f - a * a - b * b - c * c;
            float d = rest > 0f ? (float)System.Math.Sqrt(rest) : 0f;
            switch (largest)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }

        /// <summary>The logarithms of the scales as SplatData.hlsl reads them.</summary>
        public static Vector3 LnScaleOf(in Splat s)
        {
            uint q0 = s.scale0 & 0x1FFFFFu;
            uint q1 = (s.scale0 >> 21) | ((s.scale1 & 0x3FFu) << 11);
            uint q2 = (s.scale1 >> 10) & 0x1FFFFFu;
            const float step = (LnScaleMax - LnScaleMin) / ScaleSteps;
            return new Vector3(LnScaleMin + q0 * step, LnScaleMin + q1 * step, LnScaleMin + q2 * step);
        }

        /// <summary>The standard deviations along the rotated axes, metres.</summary>
        public static Vector3 ScaleOf(in Splat s)
        {
            Vector3 ln = LnScaleOf(s);
            return new Vector3((float)System.Math.Exp(ln.x), (float)System.Math.Exp(ln.y), (float)System.Math.Exp(ln.z));
        }

        /// <summary>Weight, keyframes, sector bits, rate: <see cref="SplatCloud.SeenOf"/>.</summary>
        public static Vector4 SeenOf(in Seen t) =>
            new Vector4(HalfToFloat(t.weightViews & 0xFFFFu), HalfToFloat(t.weightViews >> 16), t.sectorsBeyond & 0xFFFFu, t.rate);

        public static float BeyondOf(in Seen t) => HalfToFloat(t.sectorsBeyond >> 16);

        /// <summary>
        /// Lower triangle of R·S²·Rᵀ, the splat's covariance, (xx, xy, xz) and (yy, yz, zz), with the rotation matrix of a
        /// unit quaternion written out as SplatData.hlsl writes it.
        /// </summary>
        public static void Covariance(Quaternion q, Vector3 scale, out Vector3 cov0, out Vector3 cov1)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            var r0 = new Vector3(1f - 2f * (y * y + z * z), 2f * (x * y - w * z), 2f * (x * z + w * y));
            var r1 = new Vector3(2f * (x * y + w * z), 1f - 2f * (x * x + z * z), 2f * (y * z - w * x));
            var r2 = new Vector3(2f * (x * z - w * y), 2f * (y * z + w * x), 1f - 2f * (x * x + y * y));
            Vector3 s2 = Vector3.Scale(scale, scale);
            Vector3 m0 = Vector3.Scale(r0, s2), m1 = Vector3.Scale(r1, s2), m2 = Vector3.Scale(r2, s2);
            cov0 = new Vector3(Vector3.Dot(m0, r0), Vector3.Dot(m0, r1), Vector3.Dot(m0, r2));
            cov1 = new Vector3(Vector3.Dot(m1, r1), Vector3.Dot(m1, r2), Vector3.Dot(m2, r2));
        }

        /// <summary>IEEE half of <paramref name="f"/>, rounded to nearest even as f32tof16 does, finite values beyond the
        /// half's range saturated at ±65504 (not infinity).</summary>
        public static uint FloatToHalf(float f)
        {
            uint x = AsUint(f);
            uint sign = (x >> 16) & 0x8000u;
            uint abs = x & 0x7FFFFFFFu;
            if (abs > 0x7F800000u) return sign | 0x7E00u; // not a number
            if (abs >= 0x477FF000u) return sign | 0x7BFFu; // 65520 and up would round to infinity
            if (abs < 0x38800000u)
            {
                // Below the smallest normal half (2^-14): a subnormal, m · 2^-24.
                if (abs < 0x33000000u) return sign; // under 2^-25: zero (2^-25 itself rounds to the even zero)
                uint e = abs >> 23;
                uint m = (abs & 0x7FFFFFu) | 0x800000u;
                int shift = 126 - (int)e;
                uint h = m >> shift;
                uint rest = m & ((1u << shift) - 1u);
                uint half = 1u << (shift - 1);
                if (rest > half || (rest == half && (h & 1u) != 0u)) h++;
                return sign | h;
            }
            uint n = (abs >> 13) - (112u << 10);
            uint r = abs & 0x1FFFu;
            if (r > 0x1000u || (r == 0x1000u && (n & 1u) != 0u)) n++;
            return sign | n;
        }

        /// <summary>The float of the IEEE half in the low 16 bits of <paramref name="h"/>, as f16tof32 gives it.</summary>
        public static float HalfToFloat(uint h)
        {
            uint sign = (h & 0x8000u) << 16;
            uint e = (h >> 10) & 0x1Fu;
            uint m = h & 0x3FFu;
            if (e == 0u)
            {
                float v = m * 5.9604645e-8f; // m · 2^-24, exact
                return sign != 0u ? -v : v;
            }
            if (e == 31u) return AsFloat(sign | 0x7F800000u | (m << 13));
            return AsFloat(sign | ((e + 112u) << 23) | (m << 13));
        }
    }
}
