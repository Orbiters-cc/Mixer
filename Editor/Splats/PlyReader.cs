using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Orbiters.Mixer.Splats;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Reads Gaussian splat PLYs: binary little-endian vertices with the reference 3DGS properties (<c>x y z
    /// f_dc_0..2 opacity scale_0..2 rot_0..3</c>; others, such as normals or higher spherical harmonics, are skipped),
    /// and where Orbiters - N4N0 wrote them, its <see cref="Seen"/> properties.
    /// Orbiters - N4N0 writes them in the right-handed world of the contracts (Unity's with Z negated, rotations as
    /// (w, −x, −y, z)); reading mirrors that back. The vertices are read a block at a time, never the whole file, and
    /// decoded by a Burst job straight into the cloud's GPU layout (<see cref="SplatLayout"/>): nothing else holds them.
    /// The first reads of an editor session may run before Burst compiled the job: the same code then runs on Mono,
    /// slower, not different.
    /// </summary>
    public static unsafe class PlyReader
    {
        /// <summary>Degree-0 spherical harmonic: colour = 0.5 + C0 · f_dc.</summary>
        public const float ShC0 = 0.2820948f;

        /// <summary>The header is searched for its end this far into the file.</summary>
        const int MaxHeader = 65536;

        static readonly string[] Required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };

        /// <summary>Orbiters - N4N0's own properties: the sky shell, and what the training keyframes saw of each splat
        /// (<see cref="SplatCloud.packedSeen"/>).</summary>
        public static readonly string[] Seen = { "sky", "seen_weight", "seen_views", "seen_sectors", "seen_rate", "seen_beyond" };

        /// <summary>What the header says: where the vertices start, how many, how long each one is, where its properties are.</summary>
        internal sealed class Header
        {
            public long DataStart;
            public int Count;
            public int Stride;
            public readonly Dictionary<string, (int offset, string type)> Properties = new Dictionary<string, (int offset, string type)>();
        }

        public static SplatCloud Read(byte[] data)
        {
            using (var stream = new MemoryStream(data, false))
                return Read(stream);
        }

        public static SplatCloud ReadFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                return Read(stream);
        }

        /// <summary>Refuses a file that is not a splat PLY Mixer reads, from its header and length alone.</summary>
        public static void Check(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReadHeader(stream);
        }

        /// <summary>Reads a seekable stream from its start.</summary>
        public static SplatCloud Read(Stream stream)
        {
            Header header = ReadHeader(stream);
            int count = header.Count, stride = header.Stride;
            bool hasSeen = Array.TrueForAll(Seen, header.Properties.ContainsKey);
            // The properties' offsets and types, the required ones first, then (when the PLY has them) Orbiters - N4N0's.
            int fields = Required.Length + Seen.Length;
            var offsets = new int[fields];
            var types = new int[fields];
            for (int k = 0; k < fields; k++)
            {
                string name = k < Required.Length ? Required[k] : Seen[k - Required.Length];
                if (!header.Properties.TryGetValue(name, out (int offset, string type) p))
                    continue; // a Seen property when the PLY lacks some: hasSeen is false and they are never read
                offsets[k] = p.offset;
                types[k] = TypeCode(p.type);
            }
            var packed = new byte[(long)count * SplatLayout.Stride];
            var seen = new byte[hasSeen ? (long)count * SplatLayout.SeenStride : 0];
            var kept = new byte[count];
            stream.Position = header.DataStart;
            int block = Math.Max(1, (1 << 20) / Math.Max(1, stride));
            var buffer = new byte[block * stride];
            fixed (byte* source = buffer)
            fixed (byte* to = packed)
            fixed (byte* toSeen = seen)
            fixed (byte* keep = kept)
            fixed (int* at = offsets)
            fixed (int* type = types)
            {
                for (int first = 0; first < count; first += block)
                {
                    int n = Math.Min(block, count - first);
                    Fill(stream, buffer, n * stride);
                    new DecodeJob
                    {
                        Source = source, Stride = stride, Offsets = at, Types = type, HasSeen = hasSeen,
                        Splats = (SplatLayout.Splat*)to + first, Seen = hasSeen ? (SplatLayout.Seen*)toSeen + first : null, Kept = keep + first,
                    }.Schedule(n, 1024).Complete();
                }
                var result = stackalloc int[1];
                var box = stackalloc float3[2];
                new CompactJob
                {
                    Splats = (SplatLayout.Splat*)to, Seen = hasSeen ? (SplatLayout.Seen*)toSeen : null, Kept = keep, Count = count,
                    Result = result, Bounds = box,
                }.Run();
                int left = result[0];
                if (left < count)
                {
                    Array.Resize(ref packed, left * SplatLayout.Stride);
                    if (hasSeen)
                        Array.Resize(ref seen, left * SplatLayout.SeenStride);
                }
                var cloud = ScriptableObject.CreateInstance<SplatCloud>();
                cloud.packed = packed;
                cloud.packedSeen = seen;
                cloud.bounds = new Bounds();
                if (left > 0)
                    cloud.bounds.SetMinMax(box[0], box[1]);
                cloud.core = Core(cloud);
                cloud.hasHorizon = Horizon(cloud, out cloud.horizon);
                // Everything the renderer uploads, so the hash follows what the GPU reads (the sky flags are bits of it).
                var hash = new Hash128();
                hash.Append(packed);
                hash.Append(seen);
                cloud.contentHash = hash.ToString();
                return cloud;
            }
        }

        const int Float32 = 0, Float64 = 1, Int8 = 2, UInt8 = 3, Int16 = 4, UInt16 = 5, Int32 = 6, UInt32 = 7;

        static int TypeCode(string type)
        {
            switch (type)
            {
                case "float": case "float32": return Float32;
                case "double": case "float64": return Float64;
                case "char": case "int8": return Int8;
                case "uchar": case "uint8": return UInt8;
                case "short": case "int16": return Int16;
                case "ushort": case "uint16": return UInt16;
                case "int": case "int32": return Int32;
                default: return UInt32;
            }
        }

        /// <summary>
        /// One block of vertices into the GPU layout, each splat at its own index (<see cref="Kept"/> says which ones
        /// survive; <see cref="CompactJob"/> closes the gaps). Strict float mode, and the same arithmetic as Mono's
        /// <c>Mathf</c> gave before: exp in double, then float.
        /// </summary>
        [BurstCompile(FloatPrecision.High, FloatMode.Strict)]
        struct DecodeJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public byte* Source;
            [NativeDisableUnsafePtrRestriction] public int* Offsets;
            [NativeDisableUnsafePtrRestriction] public int* Types;
            [NativeDisableUnsafePtrRestriction] public SplatLayout.Splat* Splats;
            [NativeDisableUnsafePtrRestriction] public SplatLayout.Seen* Seen;
            [NativeDisableUnsafePtrRestriction] public byte* Kept;
            public int Stride;
            public bool HasSeen;

            float P(int v, int k)
            {
                byte* p = Source + (long)v * Stride + Offsets[k];
                // Fields need not be aligned (a PLY may mix byte and float properties): copied out byte by byte.
                switch (Types[k])
                {
                    case Float32: { float x; UnsafeUtility.MemCpy(&x, p, 4); return x; }
                    case Float64: { double x; UnsafeUtility.MemCpy(&x, p, 8); return (float)x; }
                    case Int8: return *(sbyte*)p;
                    case UInt8: return *p;
                    case Int16: { short x; UnsafeUtility.MemCpy(&x, p, 2); return x; }
                    case UInt16: { ushort x; UnsafeUtility.MemCpy(&x, p, 2); return x; }
                    case Int32: { int x; UnsafeUtility.MemCpy(&x, p, 4); return x; }
                    default: { uint x; UnsafeUtility.MemCpy(&x, p, 4); return x; }
                }
            }

            /// <summary>Where <see cref="Seen"/>'s properties start among the fields.</summary>
            const int SeenField = 14;

            /// <summary>Seen property <paramref name="k"/>: what the keyframes saw is a number or nothing known (0).</summary>
            float Number(int v, int k)
            {
                float x = P(v, SeenField + k);
                return Finite(x) ? x : 0f;
            }

            static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

            static float Exp(float x) => (float)math.exp((double)x);

            /// <summary>Mathf.RoundToInt (half to even, on the float's exact value), clamped to a byte; not a number is 0.</summary>
            static byte Channel(float value)
            {
                double x = value;
                if (!(x > 0.0)) return 0;
                if (x >= 255.0) return 255;
                double f = math.floor(x);
                double d = x - f;
                if (d > 0.5 || (d == 0.5 && ((long)f & 1L) != 0L)) f += 1.0;
                return (byte)f;
            }

            public void Execute(int v)
            {
                Kept[v] = 0;
                // Mirroring Z back: positions negate z, a quaternion (w, x, y, z) becomes (w, −x, −y, z).
                float px = P(v, 0), py = P(v, 1), pz = -P(v, 2);
                float l0 = P(v, 7), l1 = P(v, 8), l2 = P(v, 9);
                float s0 = Exp(l0), s1 = Exp(l1), s2 = Exp(l2);
                // A diverged splat (no position, a size whose covariance overflows, a rotation that is not a number)
                // would draw over the whole view or nowhere: it is left out. A rotation of length zero is none.
                if (!Finite(px) || !Finite(py) || !Finite(pz) || !Finite(s0 * s0) || !Finite(s1 * s1) || !Finite(s2 * s2))
                    return;
                float qx = -P(v, 11), qy = -P(v, 12), qz = P(v, 13), qw = P(v, 10);
                float norm = (float)math.sqrt((double)(qx * qx + qy * qy + qz * qz + qw * qw));
                if (!Finite(norm))
                    return;
                if (norm > 1e-12f)
                {
                    qx /= norm;
                    qy /= norm;
                    qz /= norm;
                    qw /= norm;
                }
                else
                {
                    qx = qy = qz = 0f;
                    qw = 1f;
                }
                float opacity = 1f / (1f + Exp(-P(v, 6)));
                bool sky = false;
                if (HasSeen)
                {
                    sky = P(v, SeenField) > 0.5f;
                    Seen[v] = SplatLayout.EncodeSeen(Number(v, 1), Number(v, 2), Number(v, 3), Number(v, 4), Number(v, 5));
                }
                Splats[v] = SplatLayout.Encode(px, py, pz, qx, qy, qz, qw, l0, l1, l2,
                    Channel((0.5f + ShC0 * P(v, 3)) * 255f), Channel((0.5f + ShC0 * P(v, 4)) * 255f), Channel((0.5f + ShC0 * P(v, 5)) * 255f),
                    Channel(opacity * 255f), sky);
                Kept[v] = 1;
            }
        }

        /// <summary>Moves the kept splats down over the left-out ones, in order, and measures their bounds.</summary>
        [BurstCompile(FloatPrecision.High, FloatMode.Strict)]
        struct CompactJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public SplatLayout.Splat* Splats;
            [NativeDisableUnsafePtrRestriction] public SplatLayout.Seen* Seen;
            [NativeDisableUnsafePtrRestriction] public byte* Kept;
            [NativeDisableUnsafePtrRestriction] public int* Result;
            [NativeDisableUnsafePtrRestriction] public float3* Bounds;
            public int Count;

            public void Execute()
            {
                int left = 0;
                var min = new float3(float.MaxValue);
                var max = new float3(-float.MaxValue);
                for (int i = 0; i < Count; i++)
                {
                    if (Kept[i] == 0)
                        continue;
                    if (left != i)
                    {
                        Splats[left] = Splats[i];
                        if (Seen != null)
                            Seen[left] = Seen[i];
                    }
                    var p = new float3(Splats[left].x, Splats[left].y, Splats[left].z);
                    min = math.min(min, p);
                    max = math.max(max, p);
                    left++;
                }
                Result[0] = left;
                Bounds[0] = min;
                Bounds[1] = max;
            }
        }

        static void Fill(Stream stream, byte[] buffer, int length)
        {
            for (int done = 0; done < length;)
            {
                int got = stream.Read(buffer, done, length - done);
                if (got <= 0)
                    throw new FormatException("The PLY is cut short.");
                done += got;
            }
        }

        /// <summary>Reads and checks the header; the stream's length must hold every vertex it declares.</summary>
        internal static Header ReadHeader(Stream stream)
        {
            stream.Position = 0;
            var start = new byte[(int)Math.Min(MaxHeader, stream.Length)];
            int length = 0;
            while (length < start.Length)
            {
                int got = stream.Read(start, length, start.Length - length);
                if (got <= 0)
                    break;
                length += got;
            }
            int end = HeaderEnd(start, length);
            if (end < 0)
                throw new FormatException("Not a PLY file (no end_header).");
            string[] lines = Encoding.ASCII.GetString(start, 0, end).Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "ply")
                throw new FormatException("Not a PLY file.");
            var header = new Header { DataStart = end, Count = -1 };
            bool binary = false, inVertex = false;
            foreach (string raw in lines)
            {
                string[] w = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0)
                    continue;
                if (w[0] == "format")
                {
                    if (w.Length < 2 || w[1] != "binary_little_endian")
                        throw new FormatException("Only binary little-endian PLYs are read (this one is " + (w.Length > 1 ? w[1] : "?") + ").");
                    binary = true;
                }
                if (w[0] == "element")
                {
                    if (header.Count >= 0 && inVertex)
                        break; // Elements after the vertices are not needed.
                    inVertex = w.Length > 2 && w[1] == "vertex";
                    if (!inVertex)
                        throw new FormatException("The vertices must be the PLY's first element.");
                    if (!int.TryParse(w[2], NumberStyles.None, CultureInfo.InvariantCulture, out header.Count))
                        throw new FormatException("The PLY's vertex count is not a number Mixer reads: " + w[2] + ".");
                    if (header.Count > SplatCloud.MaxCount)
                        throw new FormatException("The PLY has " + header.Count.ToString("N0") + " splats; Mixer reads at most " + SplatCloud.MaxCount.ToString("N0") + ".");
                }
                else if (w[0] == "property" && inVertex)
                {
                    if (w.Length != 3)
                        throw new FormatException("List properties are not supported: " + raw);
                    header.Properties[w[2]] = (header.Stride, w[1]);
                    header.Stride += Size(w[1]);
                }
            }
            if (!binary)
                throw new FormatException("The PLY does not say its format (binary little-endian is read).");
            if (header.Count < 0)
                throw new FormatException("The PLY has no vertices.");
            foreach (string p in Required)
                if (!header.Properties.ContainsKey(p))
                    throw new FormatException("Not a Gaussian splat PLY: no '" + p + "' property.");
            if (stream.Length < end + (long)header.Count * header.Stride)
                throw new FormatException("The PLY is cut short.");
            return header;
        }

        /// <summary>The sky's colour at the horizon, as Orbiters - N4N0's viewer finds it (its <c>sky::horizon</c>): the
        /// sky shell's splats from 2° below to 12° above the horizon, seen from the per-axis median of the other splats,
        /// by opacity. False without any, or when the cloud does not say which splats are the sky.</summary>
        internal static bool Horizon(SplatCloud cloud, out Color horizon)
        {
            horizon = default;
            int n = cloud.Count;
            if (!cloud.HasSeen)
                return false;
            fixed (byte* bytes = cloud.packed)
            {
                var splats = (SplatLayout.Splat*)bytes;
                int ground = 0;
                for (int i = 0; i < n; i++)
                    if (!SplatLayout.IsSky(splats[i]))
                        ground++;
                if (ground == 0)
                    return false;
                // The median of each axis: the element a sort would put at Count / 2, found without sorting.
                var axis = new float[ground];
                Vector3 centre = default;
                for (int a = 0; a < 3; a++)
                {
                    int g = 0;
                    for (int i = 0; i < n; i++)
                        if (!SplatLayout.IsSky(splats[i]))
                            axis[g++] = a == 0 ? splats[i].x : a == 1 ? splats[i].y : splats[i].z;
                    centre[a] = Select(axis, ground / 2);
                }
                double r = 0, gr = 0, b = 0, w = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!SplatLayout.IsSky(splats[i]))
                        continue;
                    Vector3 d = new Vector3(splats[i].x, splats[i].y, splats[i].z) - centre;
                    float length = d.magnitude;
                    if (length <= 0f)
                        continue;
                    float elevation = Mathf.Asin(Mathf.Clamp(d.y / length, -1f, 1f)) * Mathf.Rad2Deg;
                    if (elevation < -2f || elevation > 12f)
                        continue;
                    Color32 c = SplatLayout.ColorOf(splats[i]);
                    double weight = c.a / 255.0;
                    r += weight * c.r / 255.0;
                    gr += weight * c.g / 255.0;
                    b += weight * c.b / 255.0;
                    w += weight;
                }
                if (w <= 0)
                    return false;
                horizon = new Color((float)(r / w), (float)(gr / w), (float)(b / w), 1f);
                return true;
            }
        }

        /// <summary>
        /// The value at <paramref name="k"/> of <paramref name="values"/> once sorted (which it partly reorders): a
        /// quickselect with a median-of-three pivot, and a plain sort of what is left once it has taken more rounds than a
        /// balanced one would, so it never goes quadratic. Values must be numbers.
        /// </summary>
        internal static float Select(float[] values, int k)
        {
            int lo = 0, hi = values.Length - 1;
            int rounds = 2 * (32 - CountLeadingZeros((uint)Math.Max(1, values.Length))) + 8;
            while (hi > lo)
            {
                if (rounds-- == 0)
                {
                    Array.Sort(values, lo, hi - lo + 1);
                    return values[k];
                }
                int mid = lo + (hi - lo) / 2;
                float a = values[lo], b = values[mid], c = values[hi];
                float pivot = a < b ? (b < c ? b : a < c ? c : a) : (a < c ? a : b < c ? c : b);
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;
                    if (i <= j)
                    {
                        float t = values[i];
                        values[i] = values[j];
                        values[j] = t;
                        i++;
                        j--;
                    }
                }
                // [lo, j] ≤ pivot ≤ [i, hi]; between them, values equal to the pivot.
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return values[k];
            }
            return values[k];
        }

        static int CountLeadingZeros(uint x)
        {
            int n = 0;
            while (n < 32 && (x & 0x80000000u) == 0)
            {
                x <<= 1;
                n++;
            }
            return n;
        }

        /// <summary>Where the scene is, for framing: the 2nd to 98th percentile per axis of the splats within 100 m of
        /// the densest 2 m cell (Orbiters seeds the sky 400 m out, a thin shell that can hold most splats). Splats further
        /// than 1000 km out are never the scene.</summary>
        internal static Bounds Core(SplatCloud cloud)
        {
            const float Far = 1e6f;
            int count = cloud.Count;
            int step = Math.Max(1, count / 20000);
            var sample = new List<Vector3>();
            for (int i = 0; i < count; i += step)
            {
                Vector3 p = cloud.PositionOf(i);
                // NaN fails every comparison: left out too.
                if (Mathf.Abs(p.x) < Far && Mathf.Abs(p.y) < Far && Mathf.Abs(p.z) < Far)
                    sample.Add(p);
            }
            if (sample.Count == 0)
                return new Bounds();
            var cells = new Dictionary<Vector3Int, int>();
            Vector3Int densest = default;
            int most = 0;
            foreach (Vector3 p in sample)
            {
                var cell = Vector3Int.FloorToInt(p / 2f);
                cells.TryGetValue(cell, out int n);
                cells[cell] = ++n;
                if (n > most)
                {
                    most = n;
                    densest = cell;
                }
            }
            Vector3 centre = ((Vector3)densest + Vector3.one * 0.5f) * 2f;
            List<Vector3> near = sample.FindAll(p => (p - centre).sqrMagnitude < 100f * 100f);
            if (near.Count == 0)
                return new Bounds(centre, Vector3.zero);
            Vector3 lo = default, hi = default;
            for (int a = 0; a < 3; a++)
            {
                var axis = near.ConvertAll(p => p[a]);
                axis.Sort();
                lo[a] = axis[(int)(axis.Count * 0.02f)];
                hi[a] = axis[Math.Min(axis.Count - 1, (int)(axis.Count * 0.98f))];
            }
            var b = new Bounds();
            b.SetMinMax(lo, hi);
            return b;
        }

        /// <summary>Where the vertices start: after the line that is exactly <c>end_header</c> (a comment may mention it).</summary>
        static int HeaderEnd(byte[] data, int length)
        {
            byte[] marker = Encoding.ASCII.GetBytes("end_header");
            int limit = length - marker.Length;
            for (int i = 0; i <= limit; i++)
            {
                if (i > 0 && data[i - 1] != '\n')
                    continue;
                bool match = true;
                for (int k = 0; k < marker.Length && match; k++)
                    match = data[i + k] == marker[k];
                if (!match)
                    continue;
                int j = i + marker.Length;
                if (j < length && data[j] == '\r')
                    j++;
                if (j < length && data[j] == '\n')
                    return j + 1;
            }
            return -1;
        }

        static int Size(string type)
        {
            switch (type)
            {
                case "char": case "int8": case "uchar": case "uint8": return 1;
                case "short": case "int16": case "ushort": case "uint16": return 2;
                case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
                case "double": case "float64": return 8;
                default: throw new FormatException("Unknown PLY property type '" + type + "'.");
            }
        }
    }
}
