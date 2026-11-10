using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Meshes;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The binary mesh of the link, as Blender likes it: vertices welded where Unity only split them for a UV seam or a
    /// hard edge (same place, normals less than 120 degrees apart, same skin, same motion in every shape frame), so sheets
    /// that touch (double-sided cloth, closed lips, seams that open in a shape) stay apart; faces as triangles whose
    /// corners carry the UV and normal of the Unity vertex they came from, materials per face, skin and shapes per welded
    /// vertex. Blender edits come back per welded vertex and Unity spreads them over the members. Little-endian; every
    /// array is one block of its elements (read and written at once, numpy reads them with one call):
    /// <code>
    ///   u16 idLen, id (UTF-8)            u16 nameLen, name
    ///   u32 vertexCount, vertexCount × (f32 x, y, z)
    ///   u32 cornerCount (= 3 × faces), cornerCount × u32 vertex, cornerCount × u32 attribute
    ///   u32 attributeCount, attributeCount × (f32 u, v), attributeCount × (f32 nx, ny, nz)
    ///       Corner attributes: what a corner carries besides its vertex, shared by the corners that name it. From Unity
    ///       an attribute is a Unity vertex (they are the distinct place, normal and UV tuples already); from Blender one
    ///       per corner.
    ///   u32 faceCount, faceCount × u16 material index
    ///   u8 hasSkin; if 1: u16 boneCount, boneCount × (u16 len, bone name),
    ///       vertexCount × u8 influences, u32 n (their sum), n × u16 bone, n × f32 weight (the vertices' influences in order)
    ///   u16 shapeCount, shapeCount × (u16 len, name, sparse vectors)   the last frame of each shape
    ///   u16 groupCount, groupCount × (u16 len, name, u32 n, n × u32 vertex, n × f32 weight)
    ///   u16 sectionCount, sectionCount × (u8 tag, u32 byteLength, data); a tag the reader does not know is an error
    ///     1 UV channel:  u8 channel (1..7, or 0 when it has more than two components), u8 dimension (2..4), attributeCount × dimension × f32
    ///     2 Colours:     attributeCount × (u8 r, g, b, a)
    ///     3 Shape frames, for a shape whose last frame's weight is not 100 or that has in-betweens:
    ///                    u16 shape index (in the list above), f32 last frame's weight, u16 n, n × (f32 weight, sparse vectors)
    ///     4 Bind poses:  u16 boneCount, boneCount × 16 f32 (mesh space to bone space, Unity's element order m[0..15])
    ///     5 Link:        u64 topology (<see cref="Topology"/> of Unity's welded mesh; from Blender: the one its copy was
    ///                    built from), u16 keyLen, key (an asset mesh's content key, empty otherwise)
    ///   sparse vectors: u32 n, n × u32 vertex, n × (f32 dx, dy, dz)
    /// </code>
    /// A bone Unity has no transform for travels with an empty name. Positions frames (either way):
    /// <c>u16 idLen, id, u16 shapeLen, shape (empty for the base), u64 topology, u32 vertexCount, u32 n, [n × u32 vertex
    /// when n is not vertexCount], n × (f32 x, y, z)</c>: the positions of the welded vertices that moved since the last
    /// frame (every vertex in order when n is vertexCount). Weights frames: <c>u16 idLen, id, u16 channelLen, channel,
    /// u8 isGroup, i32 bone (its index in the bone list sent, -1 for a group), u64 topology, u32 n, n × u32 vertex,
    /// n × f32 weight</c>.
    /// </summary>
    internal static class MeshDataCodec
    {
        public const byte UvSection = 1, ColorSection = 2, FramesSection = 3, BindPoseSection = 4, LinkSection = 5;

        /// <summary>
        /// Vertices at one place weld for the link only when their normals are less than 120° apart: UV seams and hard
        /// edges weld, the two sides of double-sided cloth and closed lips do not (welded, their faces would be duplicates
        /// Blender drops).
        /// </summary>
        public const float LinkWeldMinNormalDot = -0.5f;

        /// <summary>
        /// The link's weld: by place, normal and <paramref name="keys"/> (<see cref="LinkWeldKeys"/>). A face left on the
        /// same welded vertices as an earlier one (the two sides of a card that share one normal) gets vertices of its own
        /// in a second pass, as Blender cannot hold two faces on the same vertices. Every vertex of such a face goes to the
        /// sheet of the face's copy number (the highest when it is on several), whatever its number of faces, so the
        /// copied sheet still welds across its own UV seams. A face repeated on the very same Unity vertices cannot be
        /// told apart this way (Blender leaves it out, and the export says so).
        /// </summary>
        public static MeshNeighbors LinkWeld(Vector3[] positions, int[] triangles, Vector3[] normals, float tolerance, long[] keys)
        {
            var n = MeshNeighbors.Build(positions, triangles, tolerance, normals, LinkWeldMinNormalDot, keys);
            var level = new int[positions.Length];
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
                for (int k = 0; k < 3; k++) level[triangles[t + k]] = Math.Max(level[triangles[t + k]], count + 1);
                split = true;
            }
            if (!split) return n;
            var sheets = keys != null && keys.Length == positions.Length ? (long[])keys.Clone() : new long[positions.Length];
            for (int v = 0; v < sheets.Length; v++) if (level[v] > 0) sheets[v] = MixKey(sheets[v], 0x5bd1e995L * level[v]);
            return MeshNeighbors.Build(positions, triangles, tolerance, normals, LinkWeldMinNormalDot, sheets);
        }

        private static readonly ConditionalWeakTable<MeshNeighbors, StrongBox<ulong>> topologies = new ConditionalWeakTable<MeshNeighbors, StrongBox<ulong>>();

        /// <summary>
        /// The welded mesh's topology as a number: the welded vertex count and the welded vertex of every face corner,
        /// hashed. Blender gets it with the mesh and sends it back with its edits, so edits made for another topology (a
        /// mesh reimported or replaced meanwhile, or a copy in Blender from before) are never put on the wrong vertices.
        /// Never 0. Cached per weld (a document's weld lives as long as its session).
        /// </summary>
        public static ulong Topology(MeshNeighbors n, int[] triangles)
        {
            if (n == null || triangles == null) return 0;
            return topologies.GetValue(n, weld =>
            {
                ulong h = Mix(0xcbf29ce484222325UL, (ulong)weld.WeldedCount);
                h = Mix(h, (ulong)triangles.Length);
                for (int i = 0; i < triangles.Length; i++)
                {
                    int v = triangles[i];
                    h = Mix(h, (ulong)(v >= 0 && v < weld.Weld.Length ? weld.Weld[v] : -1));
                }
                return new StrongBox<ulong>(h == 0 ? 1UL : h);
            }).Value;
        }

        /// <summary>The topology of a document's mesh as the link welds it.</summary>
        public static ulong Topology(MeshDocument doc) => doc != null ? Topology(doc.LinkWeld, doc.Triangles) : 0;

        private static ulong Mix(ulong h, ulong value)
        {
            unchecked
            {
                h ^= value + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
                h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
                return h;
            }
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
            for (int v = 0; v < keys.Length; v++)
            {
                var d = deltas[v];
                if (d.sqrMagnitude <= 0f) continue;
                keys[v] = FrameKey(keys[v], shape, frame, d);
            }
        }

        // The same keys from a frame's sparse deltas (its moving vertices only, as the dense pass above keeps them).
        private static void AddFrameKeys(long[] keys, int shape, int frame, int[] indices, Vector3[] deltas)
        {
            for (int i = 0; i < indices.Length; i++) keys[indices[i]] = FrameKey(keys[indices[i]], shape, frame, deltas[i]);
        }

        private static long FrameKey(long key, int shape, int frame, Vector3 d)
        {
            long tag = ((long)shape << 16) | (uint)frame;
            key = MixKey(key, tag);
            key = MixKey(key, ((long)BitConverter.SingleToInt32Bits(d.x) << 32) | (uint)BitConverter.SingleToInt32Bits(d.y));
            return MixKey(key, BitConverter.SingleToInt32Bits(d.z));
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

        public static bool IsInBetweenKey(string name) => InBetweenBase(name) != null;

        /// <summary>The shape an in-between key's name is of ("Smile" for "Smile @50"), or null for any other name.</summary>
        public static string InBetweenBase(string name)
        {
            int at = name != null ? name.LastIndexOf(InBetweenSeparator, StringComparison.Ordinal) : -1;
            if (at <= 0) return null;
            return float.TryParse(name.Substring(at + InBetweenSeparator.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? name.Substring(0, at) : null;
        }

        /// <summary>
        /// Whether Blender's key <paramref name="name"/> stands for an in-between frame of a shape of <paramref name="doc"/>,
        /// not for a shape: it reads as "&lt;shape&gt; @&lt;weight&gt;", the shape exists, and no shape of that very name does
        /// (a shape Unity named "X @2" stays a shape).
        /// </summary>
        public static bool IsInBetweenOf(MeshDocument doc, string name)
        {
            string shape = InBetweenBase(name);
            return shape != null && doc != null && !doc.HasShape(name) && doc.HasShape(shape);
        }

        public sealed class Decoded
        {
            public string Id, Name;
            /// <summary>The Link section's topology (from Blender: the Unity topology its copy was built from), 0 when absent.</summary>
            public ulong Topology;
            public string Key;
            public Vector3[] Vertices;
            public int[] CornerVertex;
            /// <summary>Per corner: its attribute (an index into <see cref="AttributeUv"/>, <see cref="AttributeNormal"/>, <see cref="AttributeUvs"/>, <see cref="AttributeColors"/>).</summary>
            public int[] CornerAttribute;
            public Vector2[] AttributeUv;
            public Vector3[] AttributeNormal;
            public ushort[] FaceMaterial;
            public string[] Bones;
            /// <summary>The influences, vertex after vertex: how many each vertex has, then their bones and weights (null without skin).</summary>
            public byte[] InfluenceCount;
            public ushort[] InfluenceBone;
            public float[] InfluenceWeight;
            public List<(string name, int[] indices, Vector3[] deltas)> Shapes = new List<(string, int[], Vector3[])>();
            public List<(string name, int[] indices, float[] weights)> Groups = new List<(string, int[], float[])>();
            /// <summary>UV channels 1..7 per attribute (channel to dimension and values, dimension per attribute).</summary>
            public Dictionary<int, (int dimension, float[] values)> AttributeUvs = new Dictionary<int, (int, float[])>();
            /// <summary>RGBA per attribute, or null.</summary>
            public byte[] AttributeColors;
            /// <summary>Shape index to its last frame's weight and in-between frames (shapes absent here: one frame at 100).</summary>
            public Dictionary<int, (float weight, List<Frame> inBetweens)> ShapeFrames = new Dictionary<int, (float, List<Frame>)>();
            public Matrix4x4[] BindPoses;
        }

        /// <summary>A positions frame: <see cref="Indices"/> null when it holds every welded vertex in order.</summary>
        public sealed class PositionsFrame { public string Id; public string Shape; public ulong Topology; public int VertexCount; public int[] Indices; public Vector3[] Positions; }
        public sealed class WeightsFrame { public string Id; public string Channel; public bool IsGroup; public int Bone = -1; public ulong Topology; public int[] Indices; public float[] Weights; }

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
            /// <summary>The influences as Unity lays them out: how many per vertex, then the vertices' influences in order.</summary>
            public byte[] BonesPerVertex;
            public BoneWeight1[] BoneWeights;
            public Matrix4x4[] BindPoses;
            public List<ShapeSnapshot> Shapes = new List<ShapeSnapshot>();
            public List<(string name, Dictionary<int, float> weights)> Groups = new List<(string, Dictionary<int, float>)>();
            public List<(int channel, int dimension, Vector4[] values)> ExtraUvs = new List<(int, int, Vector4[])>();
            public Color32[] Colors;
            /// <summary>An asset mesh's content key (Blender sends it back in its Hello: a mesh it holds with the same key is not sent again); null for a document.</summary>
            public string Key;
            /// <summary>Set by <see cref="Encode(MeshSnapshot)"/>: the topology it wrote.</summary>
            public ulong Topology;
        }

        // A dense array's moving entries, gathered in one pass into buffers kept for one snapshot.
        private sealed class SparseScratch
        {
            private int[] indices = Array.Empty<int>();
            private Vector3[] deltas = Array.Empty<Vector3>();

            public Frame Sparse(float weight, Vector3[] dense)
            {
                if (indices.Length < dense.Length) { indices = new int[dense.Length]; deltas = new Vector3[dense.Length]; }
                int count = 0;
                for (int v = 0; v < dense.Length; v++)
                {
                    var d = dense[v];
                    if (d.sqrMagnitude > 0f) { indices[count] = v; deltas[count] = d; count++; }
                }
                var frame = new Frame { Weight = weight, Indices = new int[count], Deltas = new Vector3[count] };
                Array.Copy(indices, frame.Indices, count);
                Array.Copy(deltas, frame.Deltas, count);
                return frame;
            }
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
                for (int i = 0; i < s.BoneNames.Length; i++) s.BoneNames[i] = BoneName(skinned.bones[i]);
                doc.CopyBoneWeights(out s.BonesPerVertex, out s.BoneWeights);
                s.BindPoses = (Matrix4x4[])doc.BindPoses.Clone();
            }
            var buffer = new Vector3[doc.VertexCount];
            var scratch = new SparseScratch();
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
                    for (int f = 0; f < all.Count - 1; f++) shape.InBetweens.Add(scratch.Sparse(all[f].weight, all[f].deltas));
                    var lastFrame = scratch.Sparse(all[all.Count - 1].weight, all[all.Count - 1].deltas);
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
                    var sparse = scratch.Sparse(shape.Weight, source);
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
                for (int i = 0; i < s.BoneNames.Length; i++) s.BoneNames[i] = BoneName(skinned.bones[i]);
                s.BonesPerVertex = mesh.GetBonesPerVertex().ToArray();
                s.BoneWeights = mesh.GetAllBoneWeights().ToArray();
                s.BindPoses = mesh.bindposes;
            }
            // Each frame is read whole (Unity hands out no sparse frames) into one buffer, and kept sparse; the weld keys
            // come from its moving vertices only.
            var deltas = new Vector3[s.Positions.Length];
            var scratch = new SparseScratch();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int frames = mesh.GetBlendShapeFrameCount(i);
                var shape = new ShapeSnapshot { Name = mesh.GetBlendShapeName(i), Weight = mesh.GetBlendShapeFrameWeight(i, frames - 1) };
                for (int f = 0; f < frames; f++)
                {
                    mesh.GetBlendShapeFrameVertices(i, f, deltas, null, null);
                    var frame = scratch.Sparse(mesh.GetBlendShapeFrameWeight(i, f), deltas);
                    AddFrameKeys(s.WeldKeys, i, f, frame.Indices, frame.Deltas);
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

        /// <summary>A bone's name as the link sends it: empty for a bone Unity has no transform for (Blender names its group by the index, and edits come back by index).</summary>
        public static string BoneName(Transform bone) => bone != null ? bone.name : "";

        /// <summary>
        /// The renderer's bone a bone of Blender's list stands for: the one at the same index when the names agree there
        /// (Blender keeps Unity's list, so bones of the same name stay apart), else the first of that name; -1 for none.
        /// </summary>
        public static int ResolveBone(Transform[] bones, int index, string name)
        {
            if (bones == null) return -1;
            // A bone without a transform keeps its index: the mesh's weights on it are the mesh's, whatever Unity shows.
            if (index >= 0 && index < bones.Length && BoneName(bones[index]) == (name ?? "")) return index;
            if (string.IsNullOrEmpty(name)) return -1;
            return Array.FindIndex(bones, t => t != null && t.name == name);
        }

        public static byte[] Encode(MeshSnapshot s)
        {
            var positions = s.Positions;
            var triangles = s.Triangles;
            var n = s.Weld ?? LinkWeld(positions, triangles, s.Normals, s.WeldTolerance, s.WeldKeys);
            var weld = n.Weld;
            var representative = n.Representative;
            int welded = n.WeldedCount, corners = triangles.Length, attributes = positions.Length;
            s.Topology = Topology(n, triangles);
            var w = new LinkWriter(welded * 13 + corners * 8 + attributes * 20 + 4096);
            w.String(s.Id);
            w.String(s.Name);
            w.I32(welded);
            var vertices = w.Claim<Vector3>(welded);
            for (int i = 0; i < welded; i++) vertices[i] = positions[representative[i]];
            // Corners: the welded vertex, and the Unity vertex whose UV and normal (and extra channels) they carry.
            w.I32(corners);
            var cornerVertex = w.Claim<int>(corners);
            for (int i = 0; i < corners; i++) cornerVertex[i] = weld[triangles[i]];
            w.Block(triangles, corners);
            w.I32(attributes);
            w.Block(s.Uvs, attributes);
            w.Block(s.Normals, attributes);
            int faces = corners / 3;
            w.I32(faces);
            int known = Math.Min(faces, s.FaceMaterial.Length);
            w.Block(s.FaceMaterial, known);
            if (known < faces) w.Claim<ushort>(faces - known).Clear();
            bool skin = s.BoneNames != null;
            w.U8((byte)(skin ? 1 : 0));
            if (skin) WriteSkin(w, s, n);
            // Shapes: sparse over the mesh's vertices, sent per welded vertex (its representative's delta).
            w.U16((ushort)s.Shapes.Count);
            foreach (var shape in s.Shapes)
            {
                w.String(shape.Name);
                WriteWelded(w, n, shape.Indices, shape.Deltas);
            }
            w.U16((ushort)s.Groups.Count);
            foreach (var (name, weights) in s.Groups)
            {
                w.String(name);
                int count = 0;
                foreach (var pair in weights) if (IsRepresentative(n, pair.Key) && pair.Value > 0f) count++;
                w.I32(count);
                var indices = w.Claim<int>(count);
                int k = 0;
                foreach (var pair in weights) if (IsRepresentative(n, pair.Key) && pair.Value > 0f) indices[k++] = weld[pair.Key];
                var values = w.Claim<float>(count);
                k = 0;
                foreach (var pair in weights) if (IsRepresentative(n, pair.Key) && pair.Value > 0f) values[k++] = pair.Value;
            }
            WriteSections(w, s, n);
            return w.ToArray();
        }

        private static bool IsRepresentative(MeshNeighbors n, int v) => v >= 0 && v < n.Weld.Length && n.Representative[n.Weld[v]] == v;

        // Per welded vertex its representative's influences, in Unity's layout.
        private static void WriteSkin(LinkWriter w, MeshSnapshot s, MeshNeighbors n)
        {
            w.U16((ushort)s.BoneNames.Length);
            foreach (var b in s.BoneNames) w.String(b);
            var perVertex = s.BonesPerVertex ?? Array.Empty<byte>();
            var weights = s.BoneWeights ?? Array.Empty<BoneWeight1>();
            var start = new int[s.Positions.Length + 1];
            for (int v = 0; v < s.Positions.Length; v++) start[v + 1] = start[v] + (v < perVertex.Length ? perVertex[v] : 0);
            int welded = n.WeldedCount;
            var representative = n.Representative;
            var counts = w.Claim<byte>(welded);
            int total = 0;
            for (int i = 0; i < welded; i++)
            {
                int v = representative[i];
                int count = Math.Min(start[v + 1], weights.Length) - start[v];
                counts[i] = (byte)Math.Max(0, Math.Min(255, count));
                total += counts[i];
            }
            w.I32(total);
            var bones = w.Claim<ushort>(total);
            int k = 0;
            for (int i = 0; i < welded; i++)
            {
                int v = representative[i], count = Math.Max(0, Math.Min(255, Math.Min(start[v + 1], weights.Length) - start[v]));
                for (int j = 0; j < count; j++) bones[k++] = (ushort)weights[start[v] + j].boneIndex;
            }
            var values = w.Claim<float>(total);
            k = 0;
            for (int i = 0; i < welded; i++)
            {
                int v = representative[i], count = Math.Max(0, Math.Min(255, Math.Min(start[v + 1], weights.Length) - start[v]));
                for (int j = 0; j < count; j++) values[k++] = weights[start[v] + j].weight;
            }
        }

        // The deltas of the welded vertices whose representative moves, from a sparse list over the mesh's vertices.
        private static void WriteWelded(LinkWriter w, MeshNeighbors n, int[] indices, Vector3[] deltas)
        {
            int count = 0;
            for (int i = 0; i < indices.Length; i++) if (IsRepresentative(n, indices[i]) && deltas[i].sqrMagnitude > 0f) count++;
            w.I32(count);
            var to = w.Claim<int>(count);
            int k = 0;
            for (int i = 0; i < indices.Length; i++) if (IsRepresentative(n, indices[i]) && deltas[i].sqrMagnitude > 0f) to[k++] = n.Weld[indices[i]];
            var values = w.Claim<Vector3>(count);
            k = 0;
            for (int i = 0; i < indices.Length; i++) if (IsRepresentative(n, indices[i]) && deltas[i].sqrMagnitude > 0f) values[k++] = deltas[i];
        }

        private static void WriteSections(LinkWriter w, MeshSnapshot s, MeshNeighbors n)
        {
            int countAt = w.ReserveU16();
            int sections = 0;
            int attributes = s.Positions.Length;
            foreach (var (channel, dimension, values) in s.ExtraUvs)
            {
                int at = w.BeginSection(UvSection);
                w.U8((byte)channel);
                w.U8((byte)dimension);
                var into = w.Claim<float>(attributes * dimension);
                for (int v = 0, k = 0; v < attributes; v++)
                {
                    var uv = values[v];
                    into[k++] = uv.x; into[k++] = uv.y;
                    if (dimension > 2) into[k++] = uv.z;
                    if (dimension > 3) into[k++] = uv.w;
                }
                w.EndSection(at);
                sections++;
            }
            if (s.Colors != null)
            {
                int at = w.BeginSection(ColorSection);
                w.Block(s.Colors, attributes);
                w.EndSection(at);
                sections++;
            }
            for (int index = 0; index < s.Shapes.Count; index++)
            {
                var shape = s.Shapes[index];
                bool inBetweens = shape.InBetweens != null && shape.InBetweens.Count > 0;
                if (!inBetweens && Mathf.Approximately(shape.Weight, 100f)) continue;
                int at = w.BeginSection(FramesSection);
                w.U16((ushort)index);
                w.F32(shape.Weight);
                w.U16((ushort)(inBetweens ? shape.InBetweens.Count : 0));
                if (inBetweens)
                    foreach (var frame in shape.InBetweens)
                    {
                        w.F32(frame.Weight);
                        WriteWelded(w, n, frame.Indices, frame.Deltas);
                    }
                w.EndSection(at);
                sections++;
            }
            if (s.BoneNames != null && s.BindPoses != null && s.BindPoses.Length == s.BoneNames.Length)
            {
                int at = w.BeginSection(BindPoseSection);
                w.U16((ushort)s.BindPoses.Length);
                // Matrix4x4 holds its elements in Unity's order m[0..15] (column after column).
                w.Block(s.BindPoses, s.BindPoses.Length);
                w.EndSection(at);
                sections++;
            }
            {
                int at = w.BeginSection(LinkSection);
                w.U64(s.Topology);
                w.String(s.Key);
                w.EndSection(at);
                sections++;
            }
            w.PatchU16(countAt, (ushort)sections);
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

        /// <summary>
        /// Reads a mesh payload. Every count is checked against the bytes left before anything is allocated, so a payload
        /// that does not hold together throws <see cref="InvalidDataException"/> instead of allocating what it announces.
        /// </summary>
        public static Decoded Decode(byte[] payload)
        {
            var r = new LinkReader(payload, "mesh");
            var d = new Decoded { Id = r.String(), Name = r.String() };
            int vertices = r.Count(r.I32(), 12, "vertices");
            d.Vertices = r.Array<Vector3>(vertices);
            int corners = r.Count(r.I32(), 8, "face corners");
            d.CornerVertex = r.Array<int>(corners);
            d.CornerAttribute = r.Array<int>(corners);
            int attributes = r.Count(r.I32(), 8 + 12, "corner attributes");
            d.AttributeUv = r.Array<Vector2>(attributes);
            d.AttributeNormal = r.Array<Vector3>(attributes);
            for (int i = 0; i < corners; i++)
                if ((uint)d.CornerAttribute[i] >= (uint)attributes || (uint)d.CornerVertex[i] >= (uint)vertices) throw new InvalidDataException("A face corner names a vertex or attribute the mesh does not have.");
            int faces = r.Count(r.I32(), 2, "faces");
            d.FaceMaterial = r.Array<ushort>(faces);
            if (r.U8() == 1)
            {
                int bones = r.Count(r.U16(), 2, "bones");
                d.Bones = new string[bones];
                for (int i = 0; i < bones; i++) d.Bones[i] = r.String();
                d.InfluenceCount = r.Array<byte>(r.Count(vertices, 1, "influence counts"));
                int total = r.Count(r.I32(), 6, "influences");
                long sum = 0;
                for (int v = 0; v < vertices; v++) sum += d.InfluenceCount[v];
                if (sum != total) throw new InvalidDataException("The influences per vertex add up to " + sum + ", the payload holds " + total + ".");
                d.InfluenceBone = r.Array<ushort>(total);
                d.InfluenceWeight = r.Array<float>(total);
            }
            int shapes = r.Count(r.U16(), 6, "shapes");
            for (int s = 0; s < shapes; s++)
            {
                string name = r.String();
                var frame = ReadSparse(r, 100f);
                d.Shapes.Add((name, frame.Indices, frame.Deltas));
            }
            int groups = r.Count(r.U16(), 6, "vertex groups");
            for (int g = 0; g < groups; g++)
            {
                string name = r.String();
                int count = r.Count(r.I32(), 8, "group weights");
                d.Groups.Add((name, r.Array<int>(count), r.Array<float>(count)));
            }
            int sections = r.Count(r.U16(), 5, "sections");
            for (int i = 0; i < sections; i++)
            {
                byte tag = r.U8();
                int length = r.Count(r.I32(), 1, "section bytes");
                int end = r.Position + length;
                switch (tag)
                {
                    case UvSection:
                    {
                        int channel = r.U8(), dimension = r.U8();
                        if (dimension < 1 || dimension > 4) throw new InvalidDataException("A UV channel of " + dimension + " components.");
                        int values = r.Count((int)Math.Min(int.MaxValue, (long)attributes * dimension), 4, "UV values");
                        d.AttributeUvs[channel] = (dimension, r.Array<float>(values));
                        break;
                    }
                    case ColorSection:
                        d.AttributeColors = r.Array<byte>(r.Count((int)Math.Min(int.MaxValue, (long)attributes * 4), 1, "colours"));
                        break;
                    case FramesSection:
                    {
                        int shape = r.U16();
                        float weight = r.F32();
                        int count = r.Count(r.U16(), 8, "in-between frames");
                        var inBetweens = new List<Frame>(count);
                        for (int k = 0; k < count; k++) { float w = r.F32(); inBetweens.Add(ReadSparse(r, w)); }
                        d.ShapeFrames[shape] = (weight, inBetweens);
                        break;
                    }
                    case BindPoseSection:
                        d.BindPoses = r.Array<Matrix4x4>(r.Count(r.U16(), 64, "bind poses"));
                        break;
                    case LinkSection:
                        d.Topology = r.U64();
                        d.Key = r.String();
                        break;
                    default:
                        // Both sides speak one protocol: a section it does not name is a payload that is not this protocol's.
                        throw new InvalidDataException("A mesh section of an unknown kind (" + tag + ").");
                }
                // A section read past its own length would be read again from inside it, as often as it says.
                if (r.Position > end) throw new InvalidDataException("A section holds more than its length.");
                r.Position = end;
            }
            return d;
        }

        private static Frame ReadSparse(LinkReader r, float weight)
        {
            int count = r.Count(r.I32(), 16, "shape deltas");
            return new Frame { Weight = weight, Indices = r.Array<int>(count), Deltas = r.Array<Vector3>(count) };
        }

        /// <summary>The positions of every welded vertex, in order.</summary>
        public static byte[] EncodePositions(string id, string shape, ulong topology, Vector3[] positions) =>
            EncodePositions(id, shape, topology, positions.Length, null, positions);

        /// <summary>
        /// The positions of the welded vertices <paramref name="indices"/> (null: every one of the
        /// <paramref name="vertexCount"/>, in order) of a mesh of <paramref name="vertexCount"/> welded vertices.
        /// </summary>
        public static byte[] EncodePositions(string id, string shape, ulong topology, int vertexCount, IReadOnlyList<int> indices, IReadOnlyList<Vector3> positions)
        {
            int count = indices != null ? indices.Count : vertexCount;
            if (positions.Count < count) throw new ArgumentException("Fewer positions than vertices.", nameof(positions));
            var w = new LinkWriter(64 + count * 16);
            w.String(id);
            w.String(shape);
            w.U64(topology);
            w.I32(vertexCount);
            w.I32(count);
            if (indices != null && count == vertexCount)
            {
                // Every vertex moved: written in order, without indices.
                var all = w.Claim<Vector3>(count);
                for (int i = 0; i < count; i++) all[indices[i]] = positions[i];
                return w.ToArray();
            }
            if (indices != null)
            {
                var to = w.Claim<int>(count);
                for (int i = 0; i < count; i++) to[i] = indices[i];
            }
            var values = w.Claim<Vector3>(count);
            for (int i = 0; i < count; i++) values[i] = positions[i];
            return w.ToArray();
        }

        public static PositionsFrame DecodePositions(byte[] payload)
        {
            var r = new LinkReader(payload, "positions");
            var frame = new PositionsFrame { Id = r.String(), Shape = r.String(), Topology = r.U64() };
            frame.VertexCount = r.I32();
            int count = r.Count(r.I32(), 12, "positions");
            if (frame.VertexCount < 0 || count > frame.VertexCount) throw new InvalidDataException("A positions frame of " + count + " vertices for a mesh of " + frame.VertexCount + ".");
            if (count != frame.VertexCount)
            {
                r.Count(count, 16, "moved vertices");
                frame.Indices = r.Array<int>(count);
                for (int i = 0; i < count; i++)
                    if ((uint)frame.Indices[i] >= (uint)frame.VertexCount) throw new InvalidDataException("A positions frame names vertex " + frame.Indices[i] + " of " + frame.VertexCount + ".");
            }
            frame.Positions = r.Array<Vector3>(count);
            if (frame.Shape.Length == 0) frame.Shape = null;
            return frame;
        }

        public static byte[] EncodeWeights(string id, string channel, bool isGroup, int bone, ulong topology, IReadOnlyList<int> indices, IReadOnlyList<float> weights)
        {
            var w = new LinkWriter(64 + indices.Count * 8);
            w.String(id);
            w.String(channel);
            w.U8((byte)(isGroup ? 1 : 0));
            w.I32(isGroup ? -1 : bone);
            w.U64(topology);
            w.I32(indices.Count);
            var to = w.Claim<int>(indices.Count);
            for (int i = 0; i < indices.Count; i++) to[i] = indices[i];
            var values = w.Claim<float>(indices.Count);
            for (int i = 0; i < indices.Count; i++) values[i] = weights[i];
            return w.ToArray();
        }

        public static WeightsFrame DecodeWeights(byte[] payload)
        {
            var r = new LinkReader(payload, "weights");
            var frame = new WeightsFrame { Id = r.String(), Channel = r.String(), IsGroup = r.U8() == 1 };
            frame.Bone = r.I32();
            frame.Topology = r.U64();
            int count = r.Count(r.I32(), 8, "weights");
            frame.Indices = r.Array<int>(count);
            frame.Weights = r.Array<float>(count);
            return frame;
        }

        /// <summary>The mesh id at the start of a mesh, positions or weights frame.</summary>
        public static string PeekId(byte[] payload)
        {
            if (payload == null || payload.Length < 2) return null;
            int len = payload[0] | (payload[1] << 8);
            if (2 + len > payload.Length) return null;
            return System.Text.Encoding.UTF8.GetString(payload, 2, len);
        }
    }
}
