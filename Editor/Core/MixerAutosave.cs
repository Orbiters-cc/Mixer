using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What a target's document differs from its original by, written a few seconds after each change to
    /// <c>Library/Orbiters/Mixer/autosave</c> on a thread of its own, and read back after a script reload or a crash so
    /// edits in progress are not lost. Sparse: moved base vertices, then each edited or new shape's non-zero deltas,
    /// then vertex groups.
    /// </summary>
    internal static class MixerAutosave
    {
        private const string Magic = "MIXA";
        private const int Version = 2;

        public static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Orbiters", "Mixer", "autosave"));

        public static string PathFor(string targetId)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(targetId));
                var name = new StringBuilder();
                for (int i = 0; i < 8; i++) name.Append(hash[i].ToString("x2"));
                return Path.Combine(Folder, name + ".bin");
            }
        }

        public static bool Exists(string targetId) => File.Exists(PathFor(targetId));

        public static void Delete(string targetId)
        {
            try { string p = PathFor(targetId); if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
        }

        /// <summary>Snapshots the document now and writes it in the background.</summary>
        public static void Write(EditTarget target)
        {
            var doc = target.Document;
            var original = doc.Original.vertices;
            var moved = new List<int>(doc.MovedBase);
            var positions = new Vector3[moved.Count];
            for (int i = 0; i < moved.Count; i++) positions[i] = doc.Positions[moved[i]];
            var shapes = new List<(string name, bool isNew, int[] indices, Vector3[] deltas)>();
            foreach (var layer in doc.ShapeEdits.Values)
            {
                if (!layer.IsNew && !layer.Edited) continue;
                var idx = new List<int>();
                for (int v = 0; v < layer.Deltas.Length; v++) if (layer.Deltas[v].sqrMagnitude > 0f) idx.Add(v);
                var d = new Vector3[idx.Count];
                for (int i = 0; i < idx.Count; i++) d[i] = layer.Deltas[idx[i]];
                shapes.Add((layer.Name, layer.IsNew, idx.ToArray(), d));
            }
            var deleted = new List<string>(doc.DeletedShapes);
            var groups = new List<(string name, int[] indices, float[] weights)>();
            foreach (var g in doc.Groups)
            {
                var idx = new int[g.Weights.Count]; var w = new float[g.Weights.Count]; int i = 0;
                foreach (var pair in g.Weights) { idx[i] = pair.Key; w[i] = pair.Value; i++; }
                groups.Add((g.Name, idx, w));
            }
            var influences = new List<(int vertex, MeshDocument.Influence[] list)>();
            if (doc.Influences != null) foreach (int v in doc.WeightEdited) influences.Add((v, doc.Influences[v].ToArray()));
            string path = PathFor(target.Id);
            string layerName = doc.Layer ?? "";
            int vertexCount = doc.VertexCount;
            DedicatedTask.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                using (var writer = new BinaryWriter(File.Create(temp)))
                {
                    writer.Write(Encoding.ASCII.GetBytes(Magic));
                    writer.Write(Version);
                    writer.Write(vertexCount);
                    writer.Write(layerName);
                    writer.Write(moved.Count);
                    for (int i = 0; i < moved.Count; i++) { writer.Write(moved[i]); WriteVector(writer, positions[i]); }
                    writer.Write(shapes.Count);
                    foreach (var s in shapes)
                    {
                        writer.Write(s.name); writer.Write(s.isNew); writer.Write(s.indices.Length);
                        for (int i = 0; i < s.indices.Length; i++) { writer.Write(s.indices[i]); WriteVector(writer, s.deltas[i]); }
                    }
                    writer.Write(deleted.Count);
                    foreach (string d in deleted) writer.Write(d);
                    writer.Write(groups.Count);
                    foreach (var g in groups)
                    {
                        writer.Write(g.name); writer.Write(g.indices.Length);
                        for (int i = 0; i < g.indices.Length; i++) { writer.Write(g.indices[i]); writer.Write(g.weights[i]); }
                    }
                    writer.Write(influences.Count);
                    foreach (var inf in influences)
                    {
                        writer.Write(inf.vertex); writer.Write(inf.list.Length);
                        for (int i = 0; i < inf.list.Length; i++) { writer.Write(inf.list[i].Bone); writer.Write(inf.list[i].Weight); }
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            });
        }

        /// <summary>Applies the autosave of the target to its document; false when there is none or it does not fit.</summary>
        public static bool Restore(EditTarget target)
        {
            string path = PathFor(target.Id);
            if (!File.Exists(path)) return false;
            var doc = target.Document;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != Magic) return false;
                    int version = reader.ReadInt32();
                    if (version != Version) return false;
                    int vertexCount = reader.ReadInt32();
                    if (vertexCount != doc.VertexCount) return false;
                    string layer = reader.ReadString();
                    int moved = reader.ReadInt32();
                    for (int i = 0; i < moved; i++)
                    {
                        int v = reader.ReadInt32();
                        var p = ReadVector(reader);
                        if (v >= 0 && v < vertexCount) { doc.Positions[v] = p; doc.MovedBase.Add(v); doc.Dirty.Add(v); }
                    }
                    int shapes = reader.ReadInt32();
                    for (int s = 0; s < shapes; s++)
                    {
                        string name = reader.ReadString();
                        bool isNew = reader.ReadBoolean();
                        int n = reader.ReadInt32();
                        var layerData = isNew ? doc.Shape(name, create: true) : doc.Shape(name);
                        for (int i = 0; i < n; i++)
                        {
                            int v = reader.ReadInt32();
                            var d = ReadVector(reader);
                            if (layerData != null && v >= 0 && v < vertexCount) layerData.Deltas[v] = d;
                        }
                        if (layerData != null) layerData.Edited = true;
                    }
                    int deleted = reader.ReadInt32();
                    for (int i = 0; i < deleted; i++) doc.DeleteShape(reader.ReadString());
                    int groups = reader.ReadInt32();
                    for (int g = 0; g < groups; g++)
                    {
                        var group = new VertexGroup { Name = reader.ReadString() };
                        int n = reader.ReadInt32();
                        for (int i = 0; i < n; i++) { int v = reader.ReadInt32(); float w = reader.ReadSingle(); group.Weights[v] = w; }
                        doc.Groups.Add(group);
                    }
                    int influenced = reader.ReadInt32();
                    for (int k = 0; k < influenced; k++)
                    {
                        int v = reader.ReadInt32();
                        int n = reader.ReadInt32();
                        var list = new MeshDocument.Influence[n];
                        for (int i = 0; i < n; i++) { int bone = reader.ReadInt32(); float w = reader.ReadSingle(); list[i] = new MeshDocument.Influence(bone, w); }
                        if (v >= 0 && v < vertexCount && doc.HasSkin) doc.SetInfluences(v, list);
                    }
                    doc.Layer = layer.Length > 0 && doc.HasShape(layer) ? layer : null;
                }
                doc.Revision++;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mixer] The autosave of '" + target.Name + "' could not be read: " + e.Message);
                return false;
            }
        }

        private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
