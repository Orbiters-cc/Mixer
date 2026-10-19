using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orbiters.Toolkit.Meshes;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The binary mesh of the link, as Blender likes it: vertices welded where Unity only split them for a UV seam or a
    /// hard edge (same place, normals less than 120 degrees apart, same skin, same motion in every shape frame), so sheets
    /// that touch (double-sided cloth, closed lips, seams that open in a shape) stay apart; faces as triangles
    /// whose corners carry the UV and normal of the Unity vertex they came from, materials per face, skin and shapes per
    /// welded vertex. Blender edits come back per welded vertex and Unity spreads them over the members. Little-endian:
    /// <code>
    ///   u16 idLen, id (UTF-8)            u16 nameLen, name
    ///   u32 vertexCount, vertexCount × (f32 x, y, z)
    ///   u32 cornerCount (= 3 × faces), cornerCount × u32 vertex index, cornerCount × (f32 u, v), cornerCount × (f32 nx, ny, nz)
    ///   u32 faceCount, faceCount × u16 material index
    ///   u8 hasSkin; if 1: u16 boneCount, boneCount × (u16 len, bone name); vertexCount × (u8 n, n × (u16 bone, f32 weight))
    ///   u16 shapeCount, shapeCount × (u16 len, name, u32 n, n × (u32 vertex, f32 dx, dy, dz))   the last frame of each shape
    ///   u16 groupCount, groupCount × (u16 len, name, u32 n, n × (u32 vertex, f32 weight))
    ///   u16 sectionCount, sectionCount × (u8 tag, u32 byteLength, data): what a reader does not know it skips
    ///     1 UV channel:  u8 channel (1..7, or 0 when it has more than two components), u8 dimension (2..4), cornerCount × dimension × f32
    ///     2 Colours:     cornerCount × (u8 r, g, b, a)
    ///     3 Shape frames, for a shape whose last frame's weight is not 100 or that has in-betweens:
    ///                    u16 shape index (in the list above), f32 last frame's weight, u16 n, n × (f32 weight, u32 m, m × (u32 vertex, f32 dx, dy, dz))
    ///     4 Bind poses:  u16 boneCount, boneCount × 16 f32 (mesh space to bone space, Unity's element order m[0..15])
    /// </code>
    /// Positions frames: <c>u16 idLen, id, u16 shapeLen, shape (empty for the base), u32 count, count × (f32 x, y, z)</c>.
    /// Weights frames: <c>u16 idLen, id, u16 channelLen, channel, u8 isGroup, u32 count, count × (u32 vertex, f32 weight)</c>.
    /// </summary>
    internal static class MeshDataCodec
    {
        public const byte UvSection = 1, ColorSection = 2, FramesSection = 3, BindPoseSection = 4;

        /// <summary>
        /// Vertices at one place weld for the link only when their normals are less than 120° apart: UV seams and hard
        /// edges weld, the two sides of double-sided cloth and closed lips do not (welded, their faces would be duplicates
        /// Blender drops).
        /// </summary>
        public const float LinkWeldMinNormalDot = -0.5f;

        /// <summary>
        /// The link's weld: by place, normal and <paramref name="keys"/> (<see cref="LinkWeldKeys"/>). A face left on the
        /// same welded vertices as an earlier one (the two sides of a card that share one normal) gets vertices of its own
        /// in a second pass, as Blender cannot hold two faces on the same vertices.
        /// </summary>
        public static MeshNeighbors LinkWeld(Vector3[] positions, int[] triangles, Vector3[] normals, float tolerance, long[] keys)
        {
            var n = MeshNeighbors.Build(positions, triangles, tolerance, normals, LinkWeldMinNormalDot, keys);
            var sheets = keys != null && keys.Length == positions.Length ? (long[])keys.Clone() : new long[positions.Length];
            var seen = new Dictionary<(int, int, int), int>();
            bool split = false;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = n.Weld[triangles[t]], b = n.Weld[triangles[t + 1]], c = n.Weld[triangles[t + 2]];
                if (a == b || b == c || a == c) continue;
                if (a > b) { int x = a; a = b; b = x; }
                if (b > c) { int x = b; b = c; c = x; }
                if (a > b) { int x = a; a = b; b = x; }
                var face = (a, b, c);
                if (!seen.TryGetValue(face, out int count)) { seen[face] = 0; continue; }
                seen[face] = count + 1;
                for (int k = 0; k < 3; k++) sheets[triangles[t + k]] = MixKey(sheets[triangles[t + k]], 0x5bd1e995L * (count + 1));
                split = true;
            }
            return split ? MeshNeighbors.Build(positions, triangles, tolerance, normals, LinkWeldMinNormalDot, sheets) : n;
        }

        /// <summary>
        /// Per vertex of a mesh, what must agree for two of its vertices at one place to weld for the link: their skin and
        /// how every frame of every blendshape moves them. Read from the original mesh, so it holds through a session.
        /// </summary>
        public static long[] LinkWeldKeys(Mesh mesh)
        {
            var keys = new long[mesh.vertexCount];
            AddSkinKeys(keys, mesh);
            var deltas = new Vector3[keys.Length];
            for (int s = 0; s < mesh.blendShapeCount; s++)
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    mesh.GetBlendShapeFrameVertices(s, f, deltas, null, null);
                    AddFrameKeys(keys, s, f, deltas);
                }
            return keys;
        }

        private static void AddSkinKeys(long[] keys, Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.BlendWeight)) return;
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int cursor = 0;
            for (int v = 0; v < keys.Length && v < perVertex.Length; v++)
            {
                for (int k = 0; k < perVertex[v]; k++)
                {
                    var bw = weights[cursor + k];
                    keys[v] = MixKey(keys[v], ((long)bw.boneIndex << 32) | (uint)BitConverter.SingleToInt32Bits(bw.weight));
                }
                cursor += perVertex[v];
            }
        }

        private static void AddFrameKeys(long[] keys, int shape, int frame, Vector3[] deltas)
        {
            long tag = ((long)shape << 16) | (uint)frame;
            for (int v = 0; v < keys.Length; v++)
            {
                var d = deltas[v];
                if (d.sqrMagnitude <= 0f) continue;
                long key = MixKey(keys[v], tag);
                key = MixKey(key, ((long)BitConverter.SingleToInt32Bits(d.x) << 32) | (uint)BitConverter.SingleToInt32Bits(d.y));
                keys[v] = MixKey(key, BitConverter.SingleToInt32Bits(d.z));
            }
        }

        private static long MixKey(long key, long value)
        {
            unchecked
            {
                ulong h = (ulong)key ^ ((ulong)value + 0x9E3779B97F4A7C15UL + ((ulong)key << 6) + ((ulong)key >> 2));
                h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
                return (long)h;
            }
        }

        public sealed class Frame { public float Weight; public int[] Indices; public Vector3[] Deltas; }

        /// <summary>
        /// How Blender names an in-between frame of a shape as a shape key of its own: "<shape> @<weight>" (Blender has no
        /// in-betweens; its Export for MCB folds these keys back into frames of the shape).
        /// </summary>
        public const string InBetweenSeparator = " @";

        public static bool IsInBetweenKey(string name)
        {
            int at = name != null ? name.LastIndexOf(InBetweenSeparator, StringComparison.Ordinal) : -1;
            return at > 0 && float.TryParse(name.Substring(at + InBetweenSeparator.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        public sealed class Decoded
        {
            public string Id, Name;
            public Vector3[] Vertices;
            public int[] CornerVertex;
            public Vector2[] CornerUv;
            public Vector3[] CornerNormal;
            public ushort[] FaceMaterial;
            public string[] Bones;
            public List<MeshDocument.Influence>[] Influences;
            public List<(string name, int[] indices, Vector3[] deltas)> Shapes = new List<(string, int[], Vector3[])>();
            public List<(string name, int[] indices, float[] weights)> Groups = new List<(string, int[], float[])>();
            /// <summary>UV channels 1..7 per corner (channel to dimension and values, dimension per corner).</summary>
            public Dictionary<int, (int dimension, float[] values)> CornerUvs = new Dictionary<int, (int, float[])>();
            /// <summary>RGBA per corner, or null.</summary>
            public byte[] CornerColors;
            /// <summary>Shape index to its last frame's weight and in-between frames (shapes absent here: one frame at 100).</summary>
            public Dictionary<int, (float weight, List<Frame> inBetweens)> ShapeFrames = new Dictionary<int, (float, List<Frame>)>();
            public Matrix4x4[] BindPoses;
        }

        public sealed class PositionsFrame { public string Id; public string Shape; public Vector3[] Positions; }
        public sealed class WeightsFrame { public string Id; public string Channel; public bool IsGroup; public int[] Indices; public float[] Weights; }

        /// <summary>A shape as sent: its last frame and in-betweens, sparse over the mesh's own vertices.</summary>
        public sealed class ShapeSnapshot
        {
            public string Name;
            public float Weight = 100f;
            public int[] Indices;
            public Vector3[] Deltas;
            public List<Frame> InBetweens;
        }

        /// <summary>
        /// Everything the encoder needs, read on the main thread; <see cref="Encode(MeshSnapshot)"/> can then run on any
        /// thread. Indices are the mesh's own vertices; the encoder welds.
        /// </summary>
        public sealed class MeshSnapshot
        {
            public string Id, Name;
            public Vector3[] Positions, Normals;
            public Vector2[] Uvs;
            public int[] Triangles;
            public ushort[] FaceMaterial;
            public float WeldTolerance;
            /// <summary>The weld to use (a document's); null: built from <see cref="Positions"/>, <see cref="Normals"/> and <see cref="WeldKeys"/>.</summary>
            public MeshNeighbors Weld;
            public long[] WeldKeys;
            public string[] BoneNames;
            public byte[] BonesPerVertex;
            public BoneWeight1[] BoneWeights;
            /// <summary>A document's influences, used instead of <see cref="BonesPerVertex"/>/<see cref="BoneWeights"/> when set.</summary>
            public List<MeshDocument.Influence>[] Influences;
            public Matrix4x4[] BindPoses;
            public List<ShapeSnapshot> Shapes = new List<ShapeSnapshot>();
            public List<(string name, Dictionary<int, float> weights)> Groups = new List<(string, Dictionary<int, float>)>();
            public List<(int channel, int dimension, Vector4[] values)> ExtraUvs = new List<(int, int, Vector4[])>();
            public Color32[] Colors;
        }

        /// <summary>The mesh as its document knows it (current positions, edited shapes, weights, bind poses), welded as the link welds it.</summary>
        public static byte[] Encode(string id, EditTarget target) => Encode(Snapshot(id, target));

        public static MeshSnapshot Snapshot(string id, EditTarget target)
        {
            var doc = target.Document;
            var mesh = target.Working != null ? target.Working : target.Original;
            var s = new MeshSnapshot
            {
                Id = id, Name = mesh.name.Replace(" (Mixer working)", ""),
                Positions = doc.Positions, Normals = doc.Normals, Triangles = doc.Triangles,
                FaceMaterial = FaceMaterials(mesh, doc.Triangles.Length / 3), Weld = doc.LinkWeld,
            };
            s.Uvs = mesh.uv; if (s.Uvs == null || s.Uvs.Length != doc.VertexCount) s.Uvs = new Vector2[doc.VertexCount];
            ReadExtras(mesh, s);
            var skinned = target.Skinned;
            if (doc.HasSkin && skinned != null)
            {
                s.BoneNames = new string[skinned.bones.Length];
                for (int i = 0; i < s.BoneNames.Length; i++) s.BoneNames[i] = skinned.bones[i] != null ? skinned.bones[i].name : "bone " + i;
                s.Influences = doc.EnsureInfluences();
                s.BindPoses = (Matrix4x4[])doc.BindPoses.Clone();
            }
            var buffer = new Vector3[doc.VertexCount];
            foreach (string shapeName in doc.ShapeOrder)
            {
                if (!doc.HasShape(shapeName)) continue;
                doc.ShapeEdits.TryGetValue(shapeName, out var layer);
                int index = layer != null && layer.IsNew ? -1 : doc.Original.GetBlendShapeIndex(shapeName);
                int frames = index >= 0 ? doc.Original.GetBlendShapeFrameCount(index) : 1;
                var shape = new ShapeSnapshot { Name = shapeName, Weight = index >= 0 ? doc.Original.GetBlendShapeFrameWeight(index, frames - 1) : 100f };
                if (frames > 1)
                {
                    // In-betweens as a commit would make them (moved with an edit of the last frame).
                    var all = MeshBuilder.FramesOf(doc, doc.Original, shapeName);
                    shape.InBetweens = new List<Frame>();
                    for (int f = 0; f < all.Count - 1; f++) shape.InBetweens.Add(Sparse(all[f].weight, all[f].deltas));
                    var lastFrame = Sparse(all[all.Count - 1].weight, all[all.Count - 1].deltas);
                    shape.Indices = lastFrame.Indices; shape.Deltas = lastFrame.Deltas;
                }
                else
                {
                    Vector3[] source = layer?.Deltas;
                    if (source == null)
                    {
                        if (index < 0) continue;
                        doc.Original.GetBlendShapeFrameVertices(index, frames - 1, buffer, null, null);
                        source = buffer;
                    }
                    var sparse = Sparse(shape.Weight, source);
                    shape.Indices = sparse.Indices; shape.Deltas = sparse.Deltas;
                }
                s.Shapes.Add(shape);
            }
            foreach (var g in doc.Groups) s.Groups.Add((g.Name, new Dictionary<int, float>(g.Weights)));
            return s;
        }

        /// <summary>A mesh that is not being edited (no document): read from the asset on the main thread.</summary>
        public static byte[] Encode(string id, Mesh mesh, SkinnedMeshRenderer skinned) => Encode(Snapshot(id, mesh, skinned));

        public static MeshSnapshot Snapshot(string id, Mesh mesh, SkinnedMeshRenderer skinned)
        {
            var s = new MeshSnapshot { Id = id, Name = mesh.name, Positions = mesh.vertices, Triangles = mesh.triangles };
            s.Normals = mesh.normals; if (s.Normals == null || s.Normals.Length != s.Positions.Length) s.Normals = new Vector3[s.Positions.Length];
            s.Uvs = mesh.uv; if (s.Uvs == null || s.Uvs.Length != s.Positions.Length) s.Uvs = new Vector2[s.Positions.Length];
            s.FaceMaterial = FaceMaterials(mesh, s.Triangles.Length / 3);
            s.WeldTolerance = MeshDocument.WeldTolerance(mesh);
            s.WeldKeys = new long[s.Positions.Length];
            AddSkinKeys(s.WeldKeys, mesh);
            ReadExtras(mesh, s);
            bool skin = skinned != null && skinned.bones != null && skinned.bones.Length > 0 && mesh.HasVertexAttribute(VertexAttribute.BlendWeight);
            if (skin)
            {
                s.BoneNames = new string[skinned.bones.Length];
                for (int i = 0; i < s.BoneNames.Length; i++) s.BoneNames[i] = skinned.bones[i] != null ? skinned.bones[i].name : "";
                s.BonesPerVertex = mesh.GetBonesPerVertex().ToArray();
                s.BoneWeights = mesh.GetAllBoneWeights().ToArray();
                s.BindPoses = mesh.bindposes;
            }
            var deltas = new Vector3[s.Positions.Length];
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int frames = mesh.GetBlendShapeFrameCount(i);
                var shape = new ShapeSnapshot { Name = mesh.GetBlendShapeName(i), Weight = mesh.GetBlendShapeFrameWeight(i, frames - 1) };
                for (int f = 0; f < frames; f++)
                {
                    mesh.GetBlendShapeFrameVertices(i, f, deltas, null, null);
                    AddFrameKeys(s.WeldKeys, i, f, deltas);
                    var frame = Sparse(mesh.GetBlendShapeFrameWeight(i, f), deltas);
                    if (f < frames - 1) (shape.InBetweens ?? (shape.InBetweens = new List<Frame>())).Add(frame);
                    else { shape.Indices = frame.Indices; shape.Deltas = frame.Deltas; }
                }
                s.Shapes.Add(shape);
            }
            return s;
        }

        // UV channels 1..7 and vertex colours, when the mesh has them; channel 0 too when it has more than the two
        // components the main block carries (so Blender knows what an FBX export cannot keep).
        private static void ReadExtras(Mesh mesh, MeshSnapshot s)
        {
            var values = new List<Vector4>();
            for (int channel = 0; channel <= 7; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!mesh.HasVertexAttribute(attribute)) continue;
                int dimension = Mathf.Clamp(mesh.GetVertexAttributeDimension(attribute), 2, 4);
                if (channel == 0 && dimension <= 2) continue;
                values.Clear();
                mesh.GetUVs(channel, values);
                if (values.Count != s.Positions.Length) continue;
                s.ExtraUvs.Add((channel, dimension, values.ToArray()));
            }
            if (mesh.HasVertexAttribute(VertexAttribute.Color))
            {
                var colors = mesh.colors32;
                if (colors != null && colors.Length == s.Positions.Length) s.Colors = colors;
            }
        }

        private static Frame Sparse(float weight, Vector3[] dense)
        {
            int count = 0;
            for (int v = 0; v < dense.Length; v++) if (dense[v].sqrMagnitude > 0f) count++;
            var frame = new Frame { Weight = weight, Indices = new int[count], Deltas = new Vector3[count] };
            int k = 0;
            for (int v = 0; v < dense.Length; v++) if (dense[v].sqrMagnitude > 0f) { frame.Indices[k] = v; frame.Deltas[k] = dense[v]; k++; }
            return frame;
        }

        public static byte[] Encode(MeshSnapshot s)
        {
            var positions = s.Positions;
            var triangles = s.Triangles;
            var n = s.Weld ?? LinkWeld(positions, triangles, s.Normals, s.WeldTolerance, s.WeldKeys);
            var representative = n.Representative;
            int corners = triangles.Length;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, s.Id);
                WriteString(writer, s.Name);
                writer.Write(n.WeldedCount);
                for (int w = 0; w < n.WeldedCount; w++) WriteVector(writer, positions[representative[w]]);
                writer.Write(corners);
                for (int i = 0; i < corners; i++) writer.Write(n.Weld[triangles[i]]);
                for (int i = 0; i < corners; i++) { var uv = s.Uvs[triangles[i]]; writer.Write(uv.x); writer.Write(uv.y); }
                for (int i = 0; i < corners; i++) WriteVector(writer, s.Normals[triangles[i]]);
                int faces = corners / 3;
                writer.Write(faces);
                for (int f = 0; f < faces; f++) writer.Write(f < s.FaceMaterial.Length ? s.FaceMaterial[f] : (ushort)0);
                bool skin = s.BoneNames != null;
                writer.Write((byte)(skin ? 1 : 0));
                if (skin)
                {
                    writer.Write((ushort)s.BoneNames.Length);
                    foreach (var b in s.BoneNames) WriteString(writer, b);
                    int[] start = null;
                    if (s.Influences == null)
                    {
                        start = new int[positions.Length + 1];
                        for (int v = 0; v < positions.Length; v++) start[v + 1] = start[v] + (v < s.BonesPerVertex.Length ? s.BonesPerVertex[v] : 0);
                    }
                    for (int w = 0; w < n.WeldedCount; w++)
                    {
                        int v = representative[w];
                        if (s.Influences != null)
                        {
                            var list = s.Influences[v];
                            writer.Write((byte)Math.Min(255, list.Count));
                            for (int k = 0; k < list.Count && k < 255; k++) { writer.Write((ushort)list[k].Bone); writer.Write(list[k].Weight); }
                            continue;
                        }
                        int count = start[v + 1] - start[v];
                        writer.Write((byte)Math.Min(255, count));
                        for (int k = 0; k < count && k < 255; k++) { var bw = s.BoneWeights[start[v] + k]; writer.Write((ushort)bw.boneIndex); writer.Write(bw.weight); }
                    }
                }
                // Shapes: sparse over the mesh's vertices, sent per welded vertex (its representative's delta).
                var dense = new Vector3[positions.Length];
                writer.Write((ushort)s.Shapes.Count);
                foreach (var shape in s.Shapes)
                {
                    WriteString(writer, shape.Name);
                    WriteWelded(writer, n, dense, shape.Indices, shape.Deltas);
                }
                writer.Write((ushort)s.Groups.Count);
                foreach (var (name, weights) in s.Groups)
                {
                    WriteString(writer, name);
                    var idx = new List<int>(); var ws = new List<float>();
                    for (int w = 0; w < n.WeldedCount; w++)
                        if (weights.TryGetValue(representative[w], out float weight) && weight > 0f) { idx.Add(w); ws.Add(weight); }
                    writer.Write(idx.Count);
                    for (int i = 0; i < idx.Count; i++) { writer.Write(idx[i]); writer.Write(ws[i]); }
                }
                WriteSections(writer, s, n, dense);
                return stream.ToArray();
            }
        }

        // The deltas of the welded vertices whose representative moves, from a sparse list over the mesh's vertices.
        private static void WriteWelded(BinaryWriter writer, MeshNeighbors n, Vector3[] dense, int[] indices, Vector3[] deltas)
        {
            for (int i = 0; i < indices.Length; i++) dense[indices[i]] = deltas[i];
            var representative = n.Representative;
            int moving = 0;
            for (int w = 0; w < n.WeldedCount; w++) if (dense[representative[w]].sqrMagnitude > 0f) moving++;
            writer.Write(moving);
            for (int w = 0; w < n.WeldedCount; w++)
            {
                var delta = dense[representative[w]];
                if (delta.sqrMagnitude <= 0f) continue;
                writer.Write(w);
                WriteVector(writer, delta);
            }
            for (int i = 0; i < indices.Length; i++) dense[indices[i]] = Vector3.zero;
        }

        private static void WriteSections(BinaryWriter writer, MeshSnapshot s, MeshNeighbors n, Vector3[] dense)
        {
            var sections = new List<(byte tag, byte[] data)>();
            var triangles = s.Triangles;
            foreach (var (channel, dimension, values) in s.ExtraUvs)
            {
                sections.Add((UvSection, Section(w =>
                {
                    w.Write((byte)channel);
                    w.Write((byte)dimension);
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        var uv = values[triangles[i]];
                        w.Write(uv.x); w.Write(uv.y);
                        if (dimension > 2) w.Write(uv.z);
                        if (dimension > 3) w.Write(uv.w);
                    }
                })));
            }
            if (s.Colors != null)
            {
                sections.Add((ColorSection, Section(w =>
                {
                    for (int i = 0; i < triangles.Length; i++) { var c = s.Colors[triangles[i]]; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
                })));
            }
            for (int index = 0; index < s.Shapes.Count; index++)
            {
                var shape = s.Shapes[index];
                bool inBetweens = shape.InBetweens != null && shape.InBetweens.Count > 0;
                if (!inBetweens && Mathf.Approximately(shape.Weight, 100f)) continue;
                int shapeIndex = index;
                sections.Add((FramesSection, Section(w =>
                {
                    w.Write((ushort)shapeIndex);
                    w.Write(shape.Weight);
                    w.Write((ushort)(inBetweens ? shape.InBetweens.Count : 0));
                    if (!inBetweens) return;
                    foreach (var frame in shape.InBetweens)
                    {
                        w.Write(frame.Weight);
                        WriteWelded(w, n, dense, frame.Indices, frame.Deltas);
                    }
                })));
            }
            if (s.BoneNames != null && s.BindPoses != null && s.BindPoses.Length == s.BoneNames.Length)
            {
                sections.Add((BindPoseSection, Section(w =>
                {
                    w.Write((ushort)s.BindPoses.Length);
                    foreach (var pose in s.BindPoses) for (int i = 0; i < 16; i++) w.Write(pose[i]);
                })));
            }
            writer.Write((ushort)sections.Count);
            foreach (var (tag, data) in sections)
            {
                writer.Write(tag);
                writer.Write(data.Length);
                writer.Write(data);
            }
        }

        private static byte[] Section(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static ushort[] FaceMaterials(Mesh mesh, int faces)
        {
            var result = new ushort[faces];
            int cursor = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = mesh.GetSubMesh(s);
                int count = sub.topology == MeshTopology.Triangles ? sub.indexCount / 3 : 0;
                for (int f = 0; f < count && cursor < faces; f++) result[cursor++] = (ushort)s;
            }
            return result;
        }

        public static Decoded Decode(byte[] payload)
        {
            using (var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var d = new Decoded { Id = ReadString(reader), Name = ReadString(reader) };
                int vertices = reader.ReadInt32();
                d.Vertices = new Vector3[vertices];
                for (int i = 0; i < vertices; i++) d.Vertices[i] = ReadVector(reader);
                int corners = reader.ReadInt32();
                d.CornerVertex = new int[corners];
                for (int i = 0; i < corners; i++) d.CornerVertex[i] = reader.ReadInt32();
                d.CornerUv = new Vector2[corners];
                for (int i = 0; i < corners; i++) d.CornerUv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                d.CornerNormal = new Vector3[corners];
                for (int i = 0; i < corners; i++) d.CornerNormal[i] = ReadVector(reader);
                int faces = reader.ReadInt32();
                d.FaceMaterial = new ushort[faces];
                for (int i = 0; i < faces; i++) d.FaceMaterial[i] = reader.ReadUInt16();
                if (reader.ReadByte() == 1)
                {
                    int bones = reader.ReadUInt16();
                    d.Bones = new string[bones];
                    for (int i = 0; i < bones; i++) d.Bones[i] = ReadString(reader);
                    d.Influences = new List<MeshDocument.Influence>[vertices];
                    for (int v = 0; v < vertices; v++)
                    {
                        int count = reader.ReadByte();
                        var list = new List<MeshDocument.Influence>(count);
                        for (int k = 0; k < count; k++) list.Add(new MeshDocument.Influence(reader.ReadUInt16(), reader.ReadSingle()));
                        d.Influences[v] = list;
                    }
                }
                int shapes = reader.ReadUInt16();
                for (int s = 0; s < shapes; s++)
                {
                    string name = ReadString(reader);
                    var frame = ReadSparse(reader, 100f);
                    d.Shapes.Add((name, frame.Indices, frame.Deltas));
                }
                int groups = reader.ReadUInt16();
                for (int g = 0; g < groups; g++)
                {
                    string name = ReadString(reader);
                    int count = reader.ReadInt32();
                    var idx = new int[count]; var ws = new float[count];
                    for (int i = 0; i < count; i++) { idx[i] = reader.ReadInt32(); ws[i] = reader.ReadSingle(); }
                    d.Groups.Add((name, idx, ws));
                }
                int sections = reader.ReadUInt16();
                for (int i = 0; i < sections; i++)
                {
                    byte tag = reader.ReadByte();
                    int length = reader.ReadInt32();
                    long end = reader.BaseStream.Position + length;
                    switch (tag)
                    {
                        case UvSection:
                        {
                            int channel = reader.ReadByte(), dimension = reader.ReadByte();
                            var values = new float[corners * dimension];
                            for (int k = 0; k < values.Length; k++) values[k] = reader.ReadSingle();
                            d.CornerUvs[channel] = (dimension, values);
                            break;
                        }
                        case ColorSection:
                            d.CornerColors = reader.ReadBytes(corners * 4);
                            break;
                        case FramesSection:
                        {
                            int shape = reader.ReadUInt16();
                            float weight = reader.ReadSingle();
                            int count = reader.ReadUInt16();
                            var inBetweens = new List<Frame>(count);
                            for (int k = 0; k < count; k++) { float w = reader.ReadSingle(); inBetweens.Add(ReadSparse(reader, w)); }
                            d.ShapeFrames[shape] = (weight, inBetweens);
                            break;
                        }
                        case BindPoseSection:
                        {
                            int count = reader.ReadUInt16();
                            d.BindPoses = new Matrix4x4[count];
                            for (int k = 0; k < count; k++) for (int e = 0; e < 16; e++) d.BindPoses[k][e] = reader.ReadSingle();
                            break;
                        }
                    }
                    reader.BaseStream.Position = end;
                }
                return d;
            }
        }

        private static Frame ReadSparse(BinaryReader reader, float weight)
        {
            int count = reader.ReadInt32();
            var frame = new Frame { Weight = weight, Indices = new int[count], Deltas = new Vector3[count] };
            for (int i = 0; i < count; i++) { frame.Indices[i] = reader.ReadInt32(); frame.Deltas[i] = ReadVector(reader); }
            return frame;
        }

        public static byte[] EncodePositions(string id, string shape, Vector3[] positions)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, id);
                WriteString(writer, shape ?? "");
                writer.Write(positions.Length);
                for (int i = 0; i < positions.Length; i++) WriteVector(writer, positions[i]);
                return stream.ToArray();
            }
        }

        public static PositionsFrame DecodePositions(byte[] payload)
        {
            using (var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var frame = new PositionsFrame { Id = ReadString(reader), Shape = ReadString(reader) };
                int count = reader.ReadInt32();
                if (count < 0 || (long)count * 12 > payload.Length) throw new InvalidDataException("positions frame too short");
                frame.Positions = new Vector3[count];
                for (int i = 0; i < count; i++) frame.Positions[i] = ReadVector(reader);
                if (frame.Shape.Length == 0) frame.Shape = null;
                return frame;
            }
        }

        public static byte[] EncodeWeights(string id, string channel, bool isGroup, IReadOnlyList<int> indices, IReadOnlyList<float> weights)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, id);
                WriteString(writer, channel ?? "");
                writer.Write((byte)(isGroup ? 1 : 0));
                writer.Write(indices.Count);
                for (int i = 0; i < indices.Count; i++) { writer.Write(indices[i]); writer.Write(weights[i]); }
                return stream.ToArray();
            }
        }

        public static WeightsFrame DecodeWeights(byte[] payload)
        {
            using (var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var frame = new WeightsFrame { Id = ReadString(reader), Channel = ReadString(reader), IsGroup = reader.ReadByte() == 1 };
                int count = reader.ReadInt32();
                frame.Indices = new int[count]; frame.Weights = new float[count];
                for (int i = 0; i < count; i++) { frame.Indices[i] = reader.ReadInt32(); frame.Weights[i] = reader.ReadSingle(); }
                return frame;
            }
        }

        /// <summary>The mesh id at the start of a mesh, positions or weights frame (to collapse older frames of the same mesh).</summary>
        public static string PeekId(byte[] payload)
        {
            if (payload == null || payload.Length < 2) return null;
            int len = payload[0] | (payload[1] << 8);
            if (len < 0 || 2 + len > payload.Length) return null;
            return Encoding.UTF8.GetString(payload, 2, len);
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            if (bytes.Length > ushort.MaxValue) Array.Resize(ref bytes, ushort.MaxValue);
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        private static string ReadString(BinaryReader r)
        {
            int len = r.ReadUInt16();
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
