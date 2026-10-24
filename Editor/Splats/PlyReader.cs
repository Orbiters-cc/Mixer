using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Orbiters.Mixer.Splats;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Reads Gaussian splat PLYs: binary little-endian vertices with the reference 3DGS properties (<c>x y z
    /// f_dc_0..2 opacity scale_0..2 rot_0..3</c>; others, such as normals or higher spherical harmonics, are skipped),
    /// and where Orbiters - N4N0 wrote them, its <see cref="Seen"/> properties.
    /// Orbiters - N4N0 writes them in the right-handed world of the contracts (Unity's with Z negated, rotations as
    /// (w, −x, −y, z)); reading mirrors that back. The vertices are read a block at a time, never the whole file.
    /// </summary>
    public static class PlyReader
    {
        /// <summary>Degree-0 spherical harmonic: colour = 0.5 + C0 · f_dc.</summary>
        public const float ShC0 = 0.2820948f;

        /// <summary>The header is searched for its end this far into the file.</summary>
        const int MaxHeader = 65536;

        static readonly string[] Required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };

        /// <summary>Orbiters - N4N0's own properties: the sky shell, and what the training keyframes saw of each splat
        /// (<see cref="SplatCloud.seen"/>).</summary>
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
            var read = new Func<byte[], int, float>[Required.Length];
            var at = new int[Required.Length];
            for (int k = 0; k < Required.Length; k++)
            {
                (int offset, string type) = header.Properties[Required[k]];
                read[k] = Reader(type);
                at[k] = offset;
            }
            bool hasSeen = Array.TrueForAll(Seen, header.Properties.ContainsKey);
            var readSeen = new Func<byte[], int, float>[Seen.Length];
            var atSeen = new int[Seen.Length];
            for (int k = 0; hasSeen && k < Seen.Length; k++)
            {
                (int offset, string type) = header.Properties[Seen[k]];
                readSeen[k] = Reader(type);
                atSeen[k] = offset;
            }
            var positions = new Vector3[count];
            var rotations = new Quaternion[count];
            var scales = new Vector3[count];
            var colors = new Color32[count];
            var seen = new Vector4[hasSeen ? count : 0];
            var sky = new bool[hasSeen ? count : 0];
            var beyond = new float[hasSeen ? count : 0];
            int kept = 0;
            stream.Position = header.DataStart;
            int block = Math.Max(1, (1 << 20) / stride);
            var buffer = new byte[block * stride];
            for (int first = 0; first < count; first += block)
            {
                int n = Math.Min(block, count - first);
                Fill(stream, buffer, n * stride);
                for (int v = 0; v < n; v++)
                {
                    int o = v * stride;
                    float P(int k) => read[k](buffer, o + at[k]);
                    // Mirroring Z back: positions negate z, a quaternion (w, x, y, z) becomes (w, −x, −y, z).
                    var position = new Vector3(P(0), P(1), -P(2));
                    var scale = new Vector3(Mathf.Exp(P(7)), Mathf.Exp(P(8)), Mathf.Exp(P(9)));
                    // A diverged splat (no position, or a size whose covariance overflows) would draw over the whole view.
                    if (!Finite(position) || !Finite(Vector3.Scale(scale, scale)))
                        continue;
                    Quaternion q = new Quaternion(-P(11), -P(12), P(13), P(10));
                    float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    positions[kept] = position;
                    rotations[kept] = norm > 1e-12f ? new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm) : Quaternion.identity;
                    scales[kept] = scale;
                    byte Channel(float dc) => (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + ShC0 * dc) * 255f), 0, 255);
                    float opacity = 1f / (1f + Mathf.Exp(-P(6)));
                    colors[kept] = new Color32(Channel(P(3)), Channel(P(4)), Channel(P(5)), (byte)Mathf.Clamp(Mathf.RoundToInt(opacity * 255f), 0, 255));
                    if (hasSeen)
                    {
                        float S(int k) => readSeen[k](buffer, o + atSeen[k]);
                        sky[kept] = S(0) > 0.5f;
                        seen[kept] = new Vector4(S(1), S(2), S(3), S(4));
                        beyond[kept] = S(5);
                    }
                    kept++;
                }
            }
            if (kept < count)
            {
                Array.Resize(ref positions, kept);
                Array.Resize(ref rotations, kept);
                Array.Resize(ref scales, kept);
                Array.Resize(ref colors, kept);
                if (hasSeen)
                {
                    Array.Resize(ref seen, kept);
                    Array.Resize(ref sky, kept);
                    Array.Resize(ref beyond, kept);
                }
            }
            var cloud = ScriptableObject.CreateInstance<SplatCloud>();
            cloud.positions = positions;
            cloud.rotations = rotations;
            cloud.scales = scales;
            cloud.colors = colors;
            cloud.seen = seen;
            cloud.sky = sky;
            cloud.beyond = beyond;
            cloud.core = Core(positions);
            cloud.hasHorizon = Horizon(positions, colors, sky, out cloud.horizon);
            var hash = new Hash128();
            hash.Append(positions);
            hash.Append(rotations);
            hash.Append(scales);
            hash.Append(colors);
            hash.Append(seen);
            hash.Append(beyond);
            cloud.contentHash = hash.ToString();
            return cloud;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

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
        /// by opacity. False without any.</summary>
        internal static bool Horizon(Vector3[] positions, Color32[] colors, bool[] sky, out Color horizon)
        {
            horizon = default;
            if (sky.Length != positions.Length)
                return false;
            var ground = new List<Vector3>();
            for (int i = 0; i < positions.Length; i++)
                if (!sky[i])
                    ground.Add(positions[i]);
            if (ground.Count == 0)
                return false;
            Vector3 centre = default;
            for (int a = 0; a < 3; a++)
            {
                var axis = ground.ConvertAll(p => p[a]);
                axis.Sort();
                centre[a] = axis[axis.Count / 2];
            }
            double r = 0, g = 0, b = 0, w = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!sky[i])
                    continue;
                Vector3 d = positions[i] - centre;
                float length = d.magnitude;
                if (length <= 0f)
                    continue;
                float elevation = Mathf.Asin(Mathf.Clamp(d.y / length, -1f, 1f)) * Mathf.Rad2Deg;
                if (elevation < -2f || elevation > 12f)
                    continue;
                double weight = colors[i].a / 255.0;
                r += weight * colors[i].r / 255.0;
                g += weight * colors[i].g / 255.0;
                b += weight * colors[i].b / 255.0;
                w += weight;
            }
            if (w <= 0)
                return false;
            horizon = new Color((float)(r / w), (float)(g / w), (float)(b / w), 1f);
            return true;
        }

        /// <summary>Where the scene is, for framing: the 2nd to 98th percentile per axis of the splats within 100 m of
        /// the densest 2 m cell (Orbiters seeds the sky 400 m out, a thin shell that can hold most splats). Splats further
        /// than 1000 km out are never the scene.</summary>
        internal static Bounds Core(Vector3[] positions)
        {
            const float Far = 1e6f;
            int step = Math.Max(1, positions.Length / 20000);
            var sample = new List<Vector3>();
            for (int i = 0; i < positions.Length; i += step)
            {
                Vector3 p = positions[i];
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

        static Func<byte[], int, float> Reader(string type)
        {
            switch (type)
            {
                case "float": case "float32": return (d, at) => BitConverter.ToSingle(d, at);
                case "double": case "float64": return (d, at) => (float)BitConverter.ToDouble(d, at);
                case "char": case "int8": return (d, at) => (sbyte)d[at];
                case "uchar": case "uint8": return (d, at) => d[at];
                case "short": case "int16": return (d, at) => BitConverter.ToInt16(d, at);
                case "ushort": case "uint16": return (d, at) => BitConverter.ToUInt16(d, at);
                case "int": case "int32": return (d, at) => BitConverter.ToInt32(d, at);
                default: return (d, at) => BitConverter.ToUInt32(d, at);
            }
        }
    }
}
