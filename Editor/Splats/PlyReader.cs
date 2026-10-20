using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Orbiters.Mixer.Splats;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Reads Gaussian splat PLYs: binary little-endian vertices with the reference 3DGS properties (<c>x y z
    /// f_dc_0..2 opacity scale_0..2 rot_0..3</c>; others, such as normals or higher spherical harmonics, are skipped).
    /// Orbiters desktop writes them in the right-handed world of the contracts (Unity's with Z negated, rotations as
    /// (w, −x, −y, z)); reading mirrors that back.
    /// </summary>
    public static class PlyReader
    {
        /// <summary>Degree-0 spherical harmonic: colour = 0.5 + C0 · f_dc.</summary>
        public const float ShC0 = 0.2820948f;

        static readonly string[] Required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };

        public static SplatCloud Read(byte[] data)
        {
            int end = HeaderEnd(data);
            if (end < 0)
                throw new FormatException("Not a PLY file (no end_header).");
            string[] lines = Encoding.ASCII.GetString(data, 0, end).Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "ply")
                throw new FormatException("Not a PLY file.");
            int count = -1;
            int stride = 0;
            bool inVertex = false;
            var offsets = new Dictionary<string, (int offset, string type)>();
            foreach (string raw in lines)
            {
                string[] w = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0)
                    continue;
                if (w[0] == "format" && (w.Length < 2 || w[1] != "binary_little_endian"))
                    throw new FormatException("Only binary little-endian PLYs are read (this one is " + (w.Length > 1 ? w[1] : "?") + ").");
                if (w[0] == "element")
                {
                    if (count >= 0 && inVertex)
                        break; // Elements after the vertices are not needed.
                    inVertex = w.Length > 2 && w[1] == "vertex";
                    if (!inVertex)
                        throw new FormatException("The vertices must be the PLY's first element.");
                    count = int.Parse(w[2], CultureInfo.InvariantCulture);
                }
                else if (w[0] == "property" && inVertex)
                {
                    if (w.Length != 3)
                        throw new FormatException("List properties are not supported: " + raw);
                    offsets[w[2]] = (stride, w[1]);
                    stride += Size(w[1]);
                }
            }
            if (count < 0)
                throw new FormatException("The PLY has no vertices.");
            foreach (string p in Required)
                if (!offsets.ContainsKey(p))
                    throw new FormatException("Not a Gaussian splat PLY: no '" + p + "' property.");
            if (data.Length < end + (long)count * stride)
                throw new FormatException("The PLY is cut short.");

            Func<int, float>[] read = new Func<int, float>[Required.Length];
            for (int k = 0; k < Required.Length; k++)
            {
                (int offset, string type) = offsets[Required[k]];
                read[k] = Reader(data, end + offset, type, stride);
            }
            var cloud = ScriptableObject.CreateInstance<SplatCloud>();
            cloud.positions = new Vector3[count];
            cloud.rotations = new Quaternion[count];
            cloud.scales = new Vector3[count];
            cloud.colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                // Mirroring Z back: positions negate z, a quaternion (w, x, y, z) becomes (w, −x, −y, z).
                cloud.positions[i] = new Vector3(read[0](i), read[1](i), -read[2](i));
                Quaternion q = new Quaternion(-read[11](i), -read[12](i), read[13](i), read[10](i));
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                cloud.rotations[i] = n > 1e-12f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
                cloud.scales[i] = new Vector3(Mathf.Exp(read[7](i)), Mathf.Exp(read[8](i)), Mathf.Exp(read[9](i)));
                byte Channel(float dc) => (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + ShC0 * dc) * 255f), 0, 255);
                float opacity = 1f / (1f + Mathf.Exp(-read[6](i)));
                cloud.colors[i] = new Color32(Channel(read[3](i)), Channel(read[4](i)), Channel(read[5](i)), (byte)Mathf.Clamp(Mathf.RoundToInt(opacity * 255f), 0, 255));
            }
            cloud.core = Core(cloud.positions);
            return cloud;
        }

        /// <summary>Where the scene is, for framing: the 2nd to 98th percentile per axis of the splats within 100 m of
        /// the densest 2 m cell (Orbiters seeds the sky 400 m out, a thin shell that can hold most splats).</summary>
        internal static Bounds Core(Vector3[] positions)
        {
            if (positions.Length == 0)
                return new Bounds();
            int step = Math.Max(1, positions.Length / 20000);
            var sample = new List<Vector3>();
            for (int i = 0; i < positions.Length; i += step)
                sample.Add(positions[i]);
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

        static int HeaderEnd(byte[] data)
        {
            byte[] marker = Encoding.ASCII.GetBytes("end_header");
            int limit = Math.Min(data.Length, 65536) - marker.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int k = 0; k < marker.Length && match; k++)
                    match = data[i + k] == marker[k];
                if (!match)
                    continue;
                int j = i + marker.Length;
                if (j < data.Length && data[j] == '\r')
                    j++;
                return j < data.Length && data[j] == '\n' ? j + 1 : -1;
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

        static Func<int, float> Reader(byte[] data, int start, string type, int stride)
        {
            switch (type)
            {
                case "float": case "float32": return i => BitConverter.ToSingle(data, start + i * stride);
                case "double": case "float64": return i => (float)BitConverter.ToDouble(data, start + i * stride);
                case "char": case "int8": return i => (sbyte)data[start + i * stride];
                case "uchar": case "uint8": return i => data[start + i * stride];
                case "short": case "int16": return i => BitConverter.ToInt16(data, start + i * stride);
                case "ushort": case "uint16": return i => BitConverter.ToUInt16(data, start + i * stride);
                case "int": case "int32": return i => BitConverter.ToInt32(data, start + i * stride);
                default: return i => BitConverter.ToUInt32(data, start + i * stride);
            }
        }
    }
}
