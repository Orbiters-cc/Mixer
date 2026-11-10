using System;
using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Gaussian splats of a reconstructed world (Orbiters - N4N0's Reconstruct), in Unity's world: imported from a
    /// <c>.splats</c> file (the session's <c>splats.ply</c>) by Mixer's importer and drawn by <see cref="SplatRenderer"/>.
    /// The splats are kept once, in the GPU's layout (<see cref="SplatLayout"/>), which the renderer uploads as it is.
    /// </summary>
    public sealed class SplatCloud : ScriptableObject
    {
        /// <summary>
        /// The most splats Mixer reads and draws: the GPU sort runs 256 splats per thread group over a power-of-two count,
        /// and a dispatch takes at most 65,535 groups in a row.
        /// </summary>
        public const int MaxCount = 1 << 23;

        /// <summary>The splats, <see cref="SplatLayout.Splat"/> after <see cref="SplatLayout.Splat"/>
        /// (<see cref="SplatLayout.Stride"/> bytes each).</summary>
        [HideInInspector] public byte[] packed = new byte[0];

        /// <summary>What the training keyframes saw of each splat (Orbiters - N4N0's <c>seen_*</c> PLY properties),
        /// <see cref="SplatLayout.Seen"/> after <see cref="SplatLayout.Seen"/>: the largest blending weight it reached, the
        /// keyframes that saw it, the compass sectors they looked from (bits, by the direction from the splat towards the
        /// camera, 0 = +Z, clockwise towards +X), their finest sampling rate (pixels per metre) and how far it lies beyond
        /// what the capture's depth frames reached, metres (the PLY's <c>seen_beyond</c>: the far field
        /// <see cref="SplatRenderer.farField"/> fogs or crops). Empty where the PLY does not say, and then no splat is on
        /// the sky shell either.</summary>
        [HideInInspector] public byte[] packedSeen = new byte[0];

        /// <summary>The sky's colour at the horizon (encoded sRGB), from <see cref="skyMap"/>'s band around it, else from
        /// the sky shell's splats; what fog and haze turn to. Meaningful only with <see cref="hasHorizon"/>.</summary>
        public Color horizon;
        public bool hasHorizon;

        /// <summary>The sky trained with the splats (Orbiters - N4N0's <c>sky.png</c>), drawn behind them: an
        /// equirectangular map, longitude <c>atan2(x, z)</c> across from −180°, elevation from +90° at the top, its
        /// encoded sRGB values kept as linear data. Null where the reconstruction keeps its sky as splats on a shell.</summary>
        public Texture2D skyMap;

        /// <summary>Where most of the splats are (the 2nd to 98th percentile per axis, so not the far sky shell): what
        /// framing looks at.</summary>
        public Bounds core;

        /// <summary>Around every splat's centre (sky shell included), in the cloud's own space.</summary>
        public Bounds bounds;

        /// <summary>Changes with the splats (the importer sets it from <see cref="packed"/> and <see cref="packedSeen"/>): a
        /// reimport in place, even with the same count, is uploaded again.</summary>
        public string contentHash = "";

        public int Count => packed != null ? packed.Length / SplatLayout.Stride : 0;

        /// <summary>Whether <see cref="packedSeen"/> holds a record for every splat.</summary>
        public bool HasSeen => packedSeen != null && Count > 0 && packedSeen.Length == Count * SplatLayout.SeenStride;

        public SplatLayout.Splat SplatAt(int i)
        {
            int o = i * SplatLayout.Stride;
            return new SplatLayout.Splat
            {
                x = BitConverter.ToSingle(packed, o),
                y = BitConverter.ToSingle(packed, o + 4),
                z = BitConverter.ToSingle(packed, o + 8),
                color = BitConverter.ToUInt32(packed, o + 12),
                rotation0 = BitConverter.ToUInt32(packed, o + 16),
                rotation1 = BitConverter.ToUInt32(packed, o + 20),
                scale0 = BitConverter.ToUInt32(packed, o + 24),
                scale1 = BitConverter.ToUInt32(packed, o + 28),
            };
        }

        SplatLayout.Seen SeenRecordAt(int i)
        {
            int o = i * SplatLayout.SeenStride;
            return new SplatLayout.Seen
            {
                weightViews = BitConverter.ToUInt32(packedSeen, o),
                sectorsBeyond = BitConverter.ToUInt32(packedSeen, o + 4),
                rate = BitConverter.ToSingle(packedSeen, o + 8),
            };
        }

        /// <summary>Centre of splat <paramref name="i"/>, metres.</summary>
        public Vector3 PositionOf(int i)
        {
            int o = i * SplatLayout.Stride;
            return new Vector3(BitConverter.ToSingle(packed, o), BitConverter.ToSingle(packed, o + 4), BitConverter.ToSingle(packed, o + 8));
        }

        /// <summary>Orientation of the Gaussian's axes.</summary>
        public Quaternion RotationOf(int i) => SplatLayout.RotationOf(SplatAt(i));

        /// <summary>Standard deviations along the rotated axes, metres.</summary>
        public Vector3 ScaleOf(int i) => SplatLayout.ScaleOf(SplatAt(i));

        /// <summary>Colour as captured (encoded sRGB) and opacity.</summary>
        public Color32 ColorOf(int i) => SplatLayout.ColorOf(SplatAt(i));

        /// <summary>Whether the splat is on the trainer's sky shell (the PLY's <c>sky</c>; false where it does not say).</summary>
        public bool IsSky(int i) => SplatLayout.IsSky(SplatAt(i));

        /// <summary>The largest weight, keyframes, sector bits and finest rate (<see cref="packedSeen"/>); negative weight
        /// where the cloud does not say.</summary>
        public Vector4 SeenOf(int i) => HasSeen ? SplatLayout.SeenOf(SeenRecordAt(i)) : new Vector4(-1f, 0f, 0f, 0f);

        /// <summary>Metres beyond the depth frames' reach (0 where the cloud does not say).</summary>
        public float BeyondOf(int i) => HasSeen ? SplatLayout.BeyondOf(SeenRecordAt(i)) : 0f;

        /// <summary>
        /// Sets the splats from one value per splat (each array as long as <paramref name="positions"/>; the last three
        /// null or empty where nothing is known of what the keyframes saw), with <see cref="bounds"/>; a slow path for
        /// small clouds built in code (tests, tools). Rotations need not be unit length.
        /// </summary>
        public void SetSplats(Vector3[] positions, Quaternion[] rotations, Vector3[] scales, Color32[] colors, Vector4[] seen = null, bool[] sky = null, float[] beyond = null)
        {
            int n = positions.Length;
            if (rotations.Length != n || scales.Length != n || colors.Length != n)
                throw new ArgumentException("One rotation, scale and colour per position.");
            bool known = seen != null && seen.Length == n;
            if (known && ((sky != null && sky.Length != n) || (beyond != null && beyond.Length != n)))
                throw new ArgumentException("One sky flag and distance beyond per splat.");
            packed = new byte[n * SplatLayout.Stride];
            packedSeen = new byte[known ? n * SplatLayout.SeenStride : 0];
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            for (int i = 0; i < n; i++)
            {
                Quaternion q = rotations[i];
                float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                q = norm > 1e-12f ? new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm) : Quaternion.identity;
                Vector3 p = positions[i], s = scales[i];
                Color32 c = colors[i];
                SplatLayout.Splat splat = SplatLayout.Encode(p.x, p.y, p.z, q.x, q.y, q.z, q.w, Mathf.Log(s.x), Mathf.Log(s.y), Mathf.Log(s.z),
                    c.r, c.g, c.b, c.a, known && sky != null && sky[i]);
                Write(packed, i * SplatLayout.Stride, splat.x, splat.y, splat.z);
                Write(packed, i * SplatLayout.Stride + 12, splat.color, splat.rotation0, splat.rotation1, splat.scale0, splat.scale1);
                if (known)
                {
                    Vector4 v = seen[i];
                    SplatLayout.Seen t = SplatLayout.EncodeSeen(v.x, v.y, v.z, v.w, beyond != null ? beyond[i] : 0f);
                    Write(packedSeen, i * SplatLayout.SeenStride, t.weightViews, t.sectorsBeyond);
                    Write(packedSeen, i * SplatLayout.SeenStride + 8, t.rate);
                }
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            bounds = new Bounds();
            if (n > 0)
                bounds.SetMinMax(min, max);
        }

        static void Write(byte[] to, int at, params float[] values)
        {
            foreach (float v in values)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(v), 0, to, at, 4);
                at += 4;
            }
        }

        static void Write(byte[] to, int at, params uint[] values)
        {
            foreach (uint v in values)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(v), 0, to, at, 4);
                at += 4;
            }
        }
    }
}
