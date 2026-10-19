using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The binary mesh of the link, as Blender likes it: vertices welded by position (Unity's splits at UV seams and
    /// hard edges become face corners), faces as triangles whose corners carry the UV and normal of the Unity vertex
    /// they came from, materials per face, skin and shapes per welded vertex. Blender edits come back per welded
    /// vertex and Unity spreads them over the members. Little-endian:
    /// <code>
    ///   u16 idLen, id (UTF-8)            u16 nameLen, name
    ///   u32 vertexCount, vertexCount × (f32 x, y, z)
    ///   u32 cornerCount (= 3 × faces), cornerCount × u32 vertex index, cornerCount × (f32 u, v), cornerCount × (f32 nx, ny, nz)
    ///   u32 faceCount, faceCount × u16 material index
    ///   u8 hasSkin; if 1: u16 boneCount, boneCount × (u16 len, bone name); vertexCount × (u8 n, n × (u16 bone, f32 weight))
    ///   u16 shapeCount, shapeCount × (u16 len, name, u32 n, n × (u32 vertex, f32 dx, dy, dz))
    ///   u16 groupCount, groupCount × (u16 len, name, u32 n, n × (u32 vertex, f32 weight))
    /// </code>
    /// Positions frames: <c>u16 idLen, id, u16 shapeLen, shape (empty for the base), u32 count, count × (f32 x, y, z)</c>.
    /// Weights frames: <c>u16 idLen, id, u16 channelLen, channel, u8 isGroup, u32 count, count × (u32 vertex, f32 weight)</c>.
    /// </summary>
    internal static class MeshDataCodec
    {
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
        }

        public sealed class PositionsFrame { public string Id; public string Shape; public Vector3[] Positions; }
        public sealed class WeightsFrame { public string Id; public string Channel; public bool IsGroup; public int[] Indices; public float[] Weights; }

        /// <summary>The mesh as its document knows it (welded), from an edit target.</summary>
        public static byte[] Encode(string id, EditTarget target)
        {
            var doc = target.Document;
            var n = doc.Neighbors;
            var mesh = target.Working != null ? target.Working : target.Original;
            var uvs = mesh.uv;
            if (uvs == null || uvs.Length != doc.VertexCount) uvs = new Vector2[doc.VertexCount];
            string[] boneNames = null;
            var skinned = target.Skinned;
            if (doc.HasSkin && skinned != null)
            {
                boneNames = new string[skinned.bones.Length];
                for (int i = 0; i < boneNames.Length; i++) boneNames[i] = skinned.bones[i] != null ? skinned.bones[i].name : "bone " + i;
            }
            var shapes = new List<(string, int[], Vector3[])>();
            var deltas = new Vector3[doc.VertexCount];
            foreach (string shapeName in doc.ShapeOrder)
            {
                if (!doc.HasShape(shapeName)) continue;
                Vector3[] source;
                if (doc.ShapeEdits.TryGetValue(shapeName, out var layer)) source = layer.Deltas;
                else
                {
                    int index = doc.Original.GetBlendShapeIndex(shapeName);
                    if (index < 0) continue;
                    doc.Original.GetBlendShapeFrameVertices(index, doc.Original.GetBlendShapeFrameCount(index) - 1, deltas, null, null);
                    source = deltas;
                }
                var idx = new List<int>(); var d = new List<Vector3>();
                for (int w = 0; w < n.WeldedCount; w++)
                {
                    var delta = source[n.Representative[w]];
                    if (delta.sqrMagnitude <= 0f) continue;
                    idx.Add(w); d.Add(delta);
                }
                shapes.Add((shapeName, idx.ToArray(), d.ToArray()));
            }
            var groups = new List<(string, int[], float[])>();
            foreach (var g in doc.Groups)
            {
                var idx = new List<int>(); var ws = new List<float>();
                for (int w = 0; w < n.WeldedCount; w++)
                    if (g.Weights.TryGetValue(n.Representative[w], out float weight) && weight > 0f) { idx.Add(w); ws.Add(weight); }
                groups.Add((g.Name, idx.ToArray(), ws.ToArray()));
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, id);
                WriteString(writer, mesh.name.Replace(" (Mixer working)", ""));
                writer.Write(n.WeldedCount);
                for (int w = 0; w < n.WeldedCount; w++) WriteVector(writer, doc.Positions[n.Representative[w]]);
                var tris = doc.Triangles;
                writer.Write(tris.Length);
                for (int i = 0; i < tris.Length; i++) writer.Write(n.Weld[tris[i]]);
                for (int i = 0; i < tris.Length; i++) { var uv = uvs[tris[i]]; writer.Write(uv.x); writer.Write(uv.y); }
                for (int i = 0; i < tris.Length; i++) WriteVector(writer, doc.Normals[tris[i]]);
                int faces = tris.Length / 3;
                writer.Write(faces);
                var faceMaterial = FaceMaterials(mesh, faces);
                for (int f = 0; f < faces; f++) writer.Write(faceMaterial[f]);
                writer.Write((byte)(boneNames != null ? 1 : 0));
                if (boneNames != null)
                {
                    writer.Write((ushort)boneNames.Length);
                    foreach (string b in boneNames) WriteString(writer, b);
                    var lists = doc.EnsureInfluences();
                    for (int w = 0; w < n.WeldedCount; w++)
                    {
                        var list = lists[n.Representative[w]];
                        writer.Write((byte)Math.Min(255, list.Count));
                        for (int i = 0; i < list.Count && i < 255; i++) { writer.Write((ushort)list[i].Bone); writer.Write(list[i].Weight); }
                    }
                }
                writer.Write((ushort)shapes.Count);
                foreach (var (name, indices, dd) in shapes)
                {
                    WriteString(writer, name);
                    writer.Write(indices.Length);
                    for (int i = 0; i < indices.Length; i++) { writer.Write(indices[i]); WriteVector(writer, dd[i]); }
                }
                writer.Write((ushort)groups.Count);
                foreach (var (name, indices, ws) in groups)
                {
                    WriteString(writer, name);
                    writer.Write(indices.Length);
                    for (int i = 0; i < indices.Length; i++) { writer.Write(indices[i]); writer.Write(ws[i]); }
                }
                return stream.ToArray();
            }
        }

        /// <summary>A mesh that is not being edited (no document): welded on the fly from the asset (main thread, in one go).</summary>
        public static byte[] Encode(string id, Mesh mesh, SkinnedMeshRenderer skinned) => Encode(Snapshot(id, mesh, skinned));

        /// <summary>The arrays of a mesh asset, read on the main thread; <see cref="Encode(MeshSnapshot)"/> can then run on any thread.</summary>
        public sealed class MeshSnapshot
        {
            public string Id, Name;
            public Vector3[] Positions, Normals;
            public Vector2[] Uvs;
            public int[] Triangles;
            public ushort[] FaceMaterial;
            public float WeldTolerance;
            public string[] BoneNames;
            public byte[] BonesPerVertex;
            public BoneWeight1[] BoneWeights;
            public string[] ShapeNames;
            public Vector3[][] ShapeDeltas;
        }

        public static MeshSnapshot Snapshot(string id, Mesh mesh, SkinnedMeshRenderer skinned)
        {
            var s = new MeshSnapshot { Id = id, Name = mesh.name, Positions = mesh.vertices, Triangles = mesh.triangles };
            s.Normals = mesh.normals; if (s.Normals == null || s.Normals.Length != s.Positions.Length) s.Normals = new Vector3[s.Positions.Length];
            s.Uvs = mesh.uv; if (s.Uvs == null || s.Uvs.Length != s.Positions.Length) s.Uvs = new Vector2[s.Positions.Length];
            s.FaceMaterial = FaceMaterials(mesh, s.Triangles.Length / 3);
            s.WeldTolerance = Mathf.Max(1e-6f, mesh.bounds.size.magnitude * 1e-6f);
            bool skin = skinned != null && skinned.bones != null && skinned.bones.Length > 0 && mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            if (skin)
            {
                s.BoneNames = new string[skinned.bones.Length];
                for (int i = 0; i < s.BoneNames.Length; i++) s.BoneNames[i] = skinned.bones[i] != null ? skinned.bones[i].name : "";
                s.BonesPerVertex = mesh.GetBonesPerVertex().ToArray();
                s.BoneWeights = mesh.GetAllBoneWeights().ToArray();
            }
            int shapeCount = mesh.blendShapeCount;
            s.ShapeNames = new string[shapeCount];
            s.ShapeDeltas = new Vector3[shapeCount][];
            for (int i = 0; i < shapeCount; i++)
            {
                s.ShapeNames[i] = mesh.GetBlendShapeName(i);
                var deltas = new Vector3[s.Positions.Length];
                mesh.GetBlendShapeFrameVertices(i, mesh.GetBlendShapeFrameCount(i) - 1, deltas, null, null);
                s.ShapeDeltas[i] = deltas;
            }
            return s;
        }

        public static byte[] Encode(MeshSnapshot s)
        {
            var positions = s.Positions;
            var triangles = s.Triangles;
            var n = MeshNeighbors.Build(positions, triangles, s.WeldTolerance);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, s.Id);
                WriteString(writer, s.Name);
                writer.Write(n.WeldedCount);
                for (int w = 0; w < n.WeldedCount; w++) WriteVector(writer, positions[n.Representative[w]]);
                writer.Write(triangles.Length);
                for (int i = 0; i < triangles.Length; i++) writer.Write(n.Weld[triangles[i]]);
                for (int i = 0; i < triangles.Length; i++) { var uv = s.Uvs[triangles[i]]; writer.Write(uv.x); writer.Write(uv.y); }
                for (int i = 0; i < triangles.Length; i++) WriteVector(writer, s.Normals[triangles[i]]);
                int faces = triangles.Length / 3;
                writer.Write(faces);
                for (int f = 0; f < faces; f++) writer.Write(f < s.FaceMaterial.Length ? s.FaceMaterial[f] : (ushort)0);
                bool skin = s.BoneNames != null;
                writer.Write((byte)(skin ? 1 : 0));
                if (skin)
                {
                    writer.Write((ushort)s.BoneNames.Length);
                    foreach (var b in s.BoneNames) WriteString(writer, b);
                    var start = new int[positions.Length + 1];
                    for (int v = 0; v < positions.Length; v++) start[v + 1] = start[v] + (v < s.BonesPerVertex.Length ? s.BonesPerVertex[v] : 0);
                    for (int w = 0; w < n.WeldedCount; w++)
                    {
                        int v = n.Representative[w];
                        int count = start[v + 1] - start[v];
                        writer.Write((byte)Math.Min(255, count));
                        for (int k = 0; k < count && k < 255; k++) { var bw = s.BoneWeights[start[v] + k]; writer.Write((ushort)bw.boneIndex); writer.Write(bw.weight); }
                    }
                }
                int shapeCount = s.ShapeNames.Length;
                writer.Write((ushort)shapeCount);
                for (int sh = 0; sh < shapeCount; sh++)
                {
                    WriteString(writer, s.ShapeNames[sh]);
                    var deltas = s.ShapeDeltas[sh];
                    var idx = new List<int>(); var d = new List<Vector3>();
                    for (int w = 0; w < n.WeldedCount; w++) { var delta = deltas[n.Representative[w]]; if (delta.sqrMagnitude > 0f) { idx.Add(w); d.Add(delta); } }
                    writer.Write(idx.Count);
                    for (int i = 0; i < idx.Count; i++) { writer.Write(idx[i]); WriteVector(writer, d[i]); }
                }
                writer.Write((ushort)0);
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
                    int count = reader.ReadInt32();
                    var idx = new int[count]; var deltas = new Vector3[count];
                    for (int i = 0; i < count; i++) { idx[i] = reader.ReadInt32(); deltas[i] = ReadVector(reader); }
                    d.Shapes.Add((name, idx, deltas));
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
                return d;
            }
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
