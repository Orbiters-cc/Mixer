using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What a target's document differs from its original by, written a few seconds after each change (and at once
    /// before a script reload, Play, closing its scene or quitting) to <c>Library/Orbiters/Mixer/autosave</c>, and read
    /// back after a script reload, a crash, Play or a reopened scene so edits in progress are not lost. Sparse: moved
    /// base vertices, then each made, edited or renamed shape's deltas where they differ from what loading the shape
    /// gives, then the shape order and deletions, vertex groups, bone influences, rest-pose bind poses (with the leaf
    /// bones added) and the mask. Writes in the background never overtake a newer one, and a deleted autosave stays
    /// deleted whatever was still being written.
    /// </summary>
    internal static class MixerAutosave
    {
        private const string Magic = "MIXA";
        private const int Version = 5;

        public static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Orbiters", "Mixer", "autosave"));

        private static readonly object gate = new object();
        // Per file, the newest write started (or the delete): an older write that ends later is thrown away.
        private static readonly Dictionary<string, long> newest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static long sequence;

        public static string PathFor(string targetId)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(targetId ?? ""));
                var name = new StringBuilder();
                for (int i = 0; i < 8; i++) name.Append(hash[i].ToString("x2"));
                return Path.Combine(Folder, name + ".bin");
            }
        }

        public static bool Exists(string targetId) => File.Exists(PathFor(targetId));

        /// <summary>Removes the autosave; a write still running for it is dropped when it ends.</summary>
        public static void Delete(string targetId)
        {
            string p = PathFor(targetId);
            lock (gate)
            {
                newest[p] = ++sequence;
                try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
            }
        }

        /// <summary>Snapshots the document now and writes it in the background.</summary>
        public static void Write(EditTarget target)
        {
            var snapshot = Snapshot.Of(target);
            string path = PathFor(target.Id);
            long seq = Begin(path);
            string name = target.Name;
            DedicatedTask.Run(() => WriteFile(path, snapshot, seq, name));
        }

        /// <summary>Snapshots and writes the document before returning (a script reload, Play or quitting is next).</summary>
        public static void WriteNow(EditTarget target)
        {
            var snapshot = Snapshot.Of(target);
            string path = PathFor(target.Id);
            WriteFile(path, snapshot, Begin(path), target.Name);
        }

        /// <summary>Gives <paramref name="to"/> everything <paramref name="from"/>'s document holds (a duplicated object's session).</summary>
        public static bool Copy(EditTarget from, EditTarget to)
        {
            var snapshot = Snapshot.Of(from);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) snapshot.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true)) return Read(to, reader, extendBones: false);
            }
        }

        private static long Begin(string path)
        {
            lock (gate) { long seq = ++sequence; newest[path] = seq; return seq; }
        }

        private static void WriteFile(string path, Snapshot snapshot, long seq, string name)
        {
            string temp = path + "." + seq + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new BinaryWriter(File.Create(temp))) snapshot.WriteTo(writer);
                lock (gate)
                {
                    if (!newest.TryGetValue(path, out long latest) || latest != seq) { File.Delete(temp); return; }
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    warned.Remove(path);
                }
            }
            catch (Exception e)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                bool first;
                lock (gate) first = warned.Add(path);
                if (first) Debug.LogWarning("[Mixer] The autosave of '" + name + "' could not be written (" + e.Message + "): its edits are only in memory until the next autosave works.");
            }
        }

        /// <summary>
        /// Applies the autosave of the target to its document; false when there is none or it does not fit. The whole file
        /// is read and checked before anything of it is applied: a cut or damaged one leaves the document as it was, and is
        /// kept beside as <c>.corrupt</c> (the next autosave writes a good one in its place).
        /// </summary>
        /// <param name="extendBones">The renderer lost the leaf bones the session added (a scene reopened from its file): give them back.</param>
        public static bool Restore(EditTarget target, bool extendBones = false)
        {
            string path = PathFor(target.Id);
            if (!File.Exists(path)) return false;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path))) return Read(target, reader, extendBones);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mixer] The autosave of '" + target.Name + "' could not be read (" + e.Message + "): nothing of it was applied. It is kept as " + Path.GetFileName(path) + ".corrupt.");
                try
                {
                    string corrupt = path + ".corrupt";
                    if (File.Exists(corrupt)) File.Delete(corrupt);
                    File.Move(path, corrupt);
                }
                catch (Exception) { }
                return false;
            }
        }

        /// <summary>
        /// Whether the autosave of <paramref name="targetId"/> is one this version of Mixer reads, made for the mesh with
        /// <paramref name="fingerprint"/> (<see cref="FingerprintOf(Mesh)"/>); false when there is none.
        /// </summary>
        public static bool Fits(string targetId, int vertexCount, long fingerprint)
        {
            string path = PathFor(targetId);
            if (!File.Exists(path)) return false;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                    return Encoding.ASCII.GetString(reader.ReadBytes(4)) == Magic && reader.ReadInt32() == Version && reader.ReadInt32() == vertexCount && reader.ReadInt64() == fingerprint;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Old autosaves nobody points at any more, and temporary files writes left behind.</summary>
        public static void CleanUp(ICollection<string> keepIds, TimeSpan olderThan)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string id in keepIds) keep.Add(PathFor(id));
                var now = DateTime.UtcNow;
                foreach (string file in Directory.GetFiles(Folder))
                {
                    var age = now - File.GetLastWriteTimeUtc(file);
                    bool stale = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ? age > TimeSpan.FromMinutes(5)
                        : file.EndsWith(".corrupt", StringComparison.OrdinalIgnoreCase) ? age > olderThan
                        : file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && !keep.Contains(file) && age > olderThan;
                    if (stale) { try { File.Delete(file); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
        }

        // ---- Format ------------------------------------------------------------------------------------------------

        private sealed class Snapshot
        {
            public int VertexCount;
            public long Fingerprint;
            public string Layer;
            public List<string> Order;
            public int[] Moved;
            public Vector3[] MovedPositions;
            public readonly List<(string name, bool isNew, bool edited, string source, int[] indices, Vector3[] deltas)> Shapes = new List<(string, bool, bool, string, int[], Vector3[])>();
            public List<string> Deleted;
            public readonly List<(string name, int[] indices, float[] weights)> Groups = new List<(string, int[], float[])>();
            public readonly List<(int vertex, MeshDocument.Influence[] list)> Influences = new List<(int, MeshDocument.Influence[])>();
            public Matrix4x4[] BindPoses;
            public string[] AddedBones;
            public readonly List<(int vertex, float value)> Masked = new List<(int, float)>();

            public static Snapshot Of(EditTarget target)
            {
                var doc = target.Document;
                var s = new Snapshot { VertexCount = doc.VertexCount, Fingerprint = doc.OriginalFingerprint, Layer = doc.Layer ?? "", Order = new List<string>(doc.ShapeOrder) };
                s.Moved = new int[doc.MovedBase.Count];
                doc.MovedBase.CopyTo(s.Moved);
                s.MovedPositions = new Vector3[s.Moved.Length];
                for (int i = 0; i < s.Moved.Length; i++) s.MovedPositions[i] = doc.Positions[s.Moved[i]];
                foreach (var layer in doc.ShapeEdits.Values)
                {
                    bool renamed = layer.Source != layer.Name;
                    if (!layer.IsNew && !layer.Edited && !renamed) continue;
                    // Only where the deltas differ from what loading the shape gives (zeros for a new one): an edit back to
                    // zero on an original shape is kept too.
                    var baseline = layer.Edited ? doc.BaselineDeltas(layer) : null;
                    // Counted first, so the arrays are made once at their size (no list growing through copies).
                    int differing = 0;
                    if (baseline != null) for (int v = 0; v < layer.Deltas.Length; v++) if (MeshDocument.Differs(layer.Deltas[v], baseline[v])) differing++;
                    var idx = new int[differing];
                    var d = new Vector3[differing];
                    if (baseline != null)
                        for (int v = 0, i = 0; v < layer.Deltas.Length; v++)
                            if (MeshDocument.Differs(layer.Deltas[v], baseline[v])) { idx[i] = v; d[i] = layer.Deltas[v]; i++; }
                    s.Shapes.Add((layer.Name, layer.IsNew, layer.Edited, layer.Source ?? "", idx, d));
                }
                s.Deleted = new List<string>(doc.DeletedShapes);
                foreach (var g in doc.Groups)
                {
                    var idx = new int[g.Weights.Count]; var w = new float[g.Weights.Count]; int i = 0;
                    foreach (var pair in g.Weights) { idx[i] = pair.Key; w[i] = pair.Value; i++; }
                    s.Groups.Add((g.Name, idx, w));
                }
                if (doc.Influences != null) foreach (int v in doc.WeightEdited) s.Influences.Add((v, doc.Influences[v].ToArray()));
                // Rest-pose edits: the bones stay moved in the scene through a reload, so their bind poses must come back too.
                s.BindPoses = doc.BindPosesEdited && doc.BindPoses != null ? (Matrix4x4[])doc.BindPoses.Clone() : new Matrix4x4[0];
                var added = new List<string>();
                var bones = target.Skinned != null ? target.Skinned.bones : null;
                // Each leaf bone by its id and its place under the avatar's root (an object new since the scene's last
                // save may come back from Play or a file with another id).
                if (s.BindPoses.Length > doc.OriginalBoneCount && bones != null)
                    for (int i = doc.OriginalBoneCount; i < bones.Length && i < s.BindPoses.Length; i++)
                        added.Add(bones[i] != null ? SceneGraphExporter.IdOf(bones[i].gameObject) + BoneSeparator + PathUnder(bones[i], target.Renderer.transform.root) : "");
                s.AddedBones = added.ToArray();
                if (doc.Mask != null) for (int v = 0; v < doc.Mask.Length; v++) if (doc.Mask[v] > 0f) s.Masked.Add((v, doc.Mask[v]));
                return s;
            }

            public void WriteTo(BinaryWriter writer)
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(Version);
                writer.Write(VertexCount);
                writer.Write(Fingerprint);
                writer.Write(Layer);
                writer.Write(Order.Count);
                foreach (string n in Order) writer.Write(n);
                writer.Write(Moved.Length);
                for (int i = 0; i < Moved.Length; i++) { writer.Write(Moved[i]); WriteVector(writer, MovedPositions[i]); }
                writer.Write(Shapes.Count);
                foreach (var sh in Shapes)
                {
                    writer.Write(sh.name); writer.Write(sh.isNew); writer.Write(sh.edited); writer.Write(sh.source); writer.Write(sh.indices.Length);
                    for (int i = 0; i < sh.indices.Length; i++) { writer.Write(sh.indices[i]); WriteVector(writer, sh.deltas[i]); }
                }
                writer.Write(Deleted.Count);
                foreach (string d in Deleted) writer.Write(d);
                writer.Write(Groups.Count);
                foreach (var g in Groups)
                {
                    writer.Write(g.name); writer.Write(g.indices.Length);
                    for (int i = 0; i < g.indices.Length; i++) { writer.Write(g.indices[i]); writer.Write(g.weights[i]); }
                }
                writer.Write(Influences.Count);
                foreach (var inf in Influences)
                {
                    writer.Write(inf.vertex); writer.Write(inf.list.Length);
                    for (int i = 0; i < inf.list.Length; i++) { writer.Write(inf.list[i].Bone); writer.Write(inf.list[i].Weight); }
                }
                writer.Write(BindPoses.Length);
                foreach (var pose in BindPoses) for (int i = 0; i < 16; i++) writer.Write(pose[i]);
                writer.Write(AddedBones.Length);
                foreach (string b in AddedBones) writer.Write(b);
                writer.Write(Masked.Count);
                foreach (var (vertex, value) in Masked) { writer.Write(vertex); writer.Write(value); }
            }

            /// <summary>
            /// What <see cref="WriteTo"/> wrote after the header (magic, version, vertex count, fingerprint), read whole:
            /// a file cut short or with counts it cannot hold throws before anything is applied.
            /// </summary>
            public static Snapshot ReadFrom(BinaryReader reader, int vertexCount, long fingerprint)
            {
                var s = new Snapshot { VertexCount = vertexCount, Fingerprint = fingerprint, Layer = reader.ReadString() };
                int orderCount = Count(reader, 1);
                s.Order = new List<string>(orderCount);
                for (int i = 0; i < orderCount; i++) s.Order.Add(reader.ReadString());
                int moved = Count(reader, 16);
                s.Moved = new int[moved];
                s.MovedPositions = new Vector3[moved];
                for (int i = 0; i < moved; i++) { s.Moved[i] = reader.ReadInt32(); s.MovedPositions[i] = ReadVector(reader); }
                int shapes = Count(reader, 8);
                for (int k = 0; k < shapes; k++)
                {
                    string name = reader.ReadString();
                    bool isNew = reader.ReadBoolean();
                    bool edited = reader.ReadBoolean();
                    string source = reader.ReadString();
                    int n = Count(reader, 16);
                    var idx = new int[n];
                    var d = new Vector3[n];
                    for (int i = 0; i < n; i++) { idx[i] = reader.ReadInt32(); d[i] = ReadVector(reader); }
                    s.Shapes.Add((name, isNew, edited, source, idx, d));
                }
                int deleted = Count(reader, 1);
                s.Deleted = new List<string>(deleted);
                for (int i = 0; i < deleted; i++) s.Deleted.Add(reader.ReadString());
                int groups = Count(reader, 5);
                for (int g = 0; g < groups; g++)
                {
                    string name = reader.ReadString();
                    int n = Count(reader, 8);
                    var idx = new int[n];
                    var w = new float[n];
                    for (int i = 0; i < n; i++) { idx[i] = reader.ReadInt32(); w[i] = reader.ReadSingle(); }
                    s.Groups.Add((name, idx, w));
                }
                int influenced = Count(reader, 8);
                for (int k = 0; k < influenced; k++)
                {
                    int v = reader.ReadInt32();
                    int n = Count(reader, 8);
                    var list = new MeshDocument.Influence[n];
                    for (int i = 0; i < n; i++) { int bone = reader.ReadInt32(); float w = reader.ReadSingle(); list[i] = new MeshDocument.Influence(bone, w); }
                    s.Influences.Add((v, list));
                }
                int poses = Count(reader, 64);
                s.BindPoses = new Matrix4x4[poses];
                for (int p = 0; p < poses; p++) for (int i = 0; i < 16; i++) s.BindPoses[p][i] = reader.ReadSingle();
                int addedCount = Count(reader, 1);
                s.AddedBones = new string[addedCount];
                for (int i = 0; i < addedCount; i++) s.AddedBones[i] = reader.ReadString();
                int masked = Count(reader, 8);
                for (int i = 0; i < masked; i++) { int v = reader.ReadInt32(); float m = reader.ReadSingle(); s.Masked.Add((v, m)); }
                return s;
            }

            // A count of items at least bytesEach long each: one the rest of the file cannot hold is damage, not a size to allocate.
            private static int Count(BinaryReader reader, int bytesEach)
            {
                int n = reader.ReadInt32();
                long left = reader.BaseStream.CanSeek ? reader.BaseStream.Length - reader.BaseStream.Position : long.MaxValue;
                if (n < 0 || (long)n * bytesEach > left) throw new InvalidDataException("the autosave is cut short or damaged (a count of " + n + ")");
                return n;
            }
        }

        /// <summary>A hash of vertex positions alone: whether a mesh asset holds the positions it was given.</summary>
        internal static long FingerprintOf(Vector3[] positions)
        {
            var hash = new Fnv();
            hash.Add(positions);
            return hash.Value;
        }

        /// <summary>
        /// Which version of a mesh this is, by everything a session reads from it and an autosave or a kept working mesh
        /// relies on: positions, triangles and their submeshes, UVs, bind poses, bone weights and the blendshapes' names and
        /// frames. A mesh reimported with any of them changed is another one: its autosave and working mesh are not used.
        /// </summary>
        internal static long FingerprintOf(Mesh mesh)
        {
            if (mesh == null) return 0;
            bool skinned = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            var bindposes = skinned ? mesh.bindposes : new Matrix4x4[0];
            byte[] perVertex = null;
            BoneWeight1[] weights = null;
            if (skinned && bindposes.Length > 0)
            {
                perVertex = mesh.GetBonesPerVertex().ToArray();
                weights = mesh.GetAllBoneWeights().ToArray();
            }
            return FingerprintOf(mesh, mesh.vertices, mesh.triangles, bindposes, perVertex, weights);
        }

        /// <summary><see cref="FingerprintOf(Mesh)"/> with the arrays a document already read from the mesh.</summary>
        internal static long FingerprintOf(Mesh mesh, Vector3[] positions, int[] triangles, Matrix4x4[] bindposes, byte[] perVertex, BoneWeight1[] weights)
        {
            var hash = new Fnv();
            hash.Add(positions);
            hash.Add(triangles?.Length ?? 0);
            if (triangles != null) foreach (int index in triangles) hash.Add(index);
            hash.Add(mesh.subMeshCount);
            for (int i = 0; i < mesh.subMeshCount; i++) hash.Add(mesh.GetSubMesh(i).indexCount);
            var uv = mesh.uv;
            hash.Add(uv.Length);
            foreach (var t in uv) { hash.Add(t.x); hash.Add(t.y); }
            hash.Add(bindposes?.Length ?? 0);
            if (bindposes != null) foreach (var pose in bindposes) for (int i = 0; i < 16; i++) hash.Add(pose[i]);
            hash.Add(perVertex?.Length ?? 0);
            if (perVertex != null) foreach (byte b in perVertex) hash.Add(b);
            hash.Add(weights?.Length ?? 0);
            if (weights != null) foreach (var w in weights) { hash.Add(w.boneIndex); hash.Add(w.weight); }
            hash.Add(mesh.blendShapeCount);
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                hash.Add(mesh.GetBlendShapeName(s));
                int frames = mesh.GetBlendShapeFrameCount(s);
                hash.Add(frames);
                for (int f = 0; f < frames; f++) hash.Add(mesh.GetBlendShapeFrameWeight(s, f));
            }
            return hash.Value;
        }

        // FNV-1a over 32-bit words.
        private struct Fnv
        {
            private ulong hash;
            private bool started;

            public long Value => (long)(started ? hash : 14695981039346656037UL);

            public void Add(int value)
            {
                unchecked
                {
                    if (!started) { hash = 14695981039346656037UL; started = true; }
                    hash = (hash ^ (uint)value) * 1099511628211UL;
                }
            }

            public void Add(float value)
            {
                var bits = new FloatBits { Float = value };
                Add((int)bits.Bits);
            }

            public void Add(string value)
            {
                Add(value?.Length ?? -1);
                if (value != null) foreach (char c in value) Add(c);
            }

            public void Add(Vector3[] values)
            {
                if (values == null) return;
                foreach (var p in values) { Add(p.x); Add(p.y); Add(p.z); }
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
        }

        private static bool Read(EditTarget target, BinaryReader reader, bool extendBones)
        {
            var doc = target.Document;
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != Magic) return false;
            if (reader.ReadInt32() != Version) return false;
            int vertexCount = reader.ReadInt32();
            if (vertexCount != doc.VertexCount) return false;
            long fingerprint = reader.ReadInt64();
            if (fingerprint != doc.OriginalFingerprint)
            {
                Debug.LogWarning("[Mixer] '" + target.Name + "' changed since its edits were autosaved (reimported?): they were made on the mesh before, and are not applied.");
                return false;
            }
            // All of it first: a file cut short throws here, with the document untouched.
            var snapshot = Snapshot.ReadFrom(reader, vertexCount, fingerprint);
            Apply(target, snapshot, extendBones);
            return true;
        }

        private static void Apply(EditTarget target, Snapshot s, bool extendBones)
        {
            var doc = target.Document;
            int vertexCount = doc.VertexCount;
            for (int i = 0; i < s.Moved.Length; i++)
            {
                int v = s.Moved[i];
                if (v >= 0 && v < vertexCount) { doc.Positions[v] = s.MovedPositions[i]; doc.MovedBase.Add(v); doc.Dirty.Add(v); }
            }
            foreach (var (name, isNew, edited, rawSource, indices, deltas) in s.Shapes)
            {
                string source = rawSource.Length == 0 ? null : rawSource;
                ShapeLayer layerData;
                if (isNew)
                {
                    layerData = new ShapeLayer { Name = name, IsNew = true, Edited = edited, Source = source != null && doc.InSource(source) ? source : null };
                    layerData.Deltas = (Vector3[])doc.BaselineDeltas(layerData).Clone();
                    doc.ShapeEdits[name] = layerData;
                    doc.DeletedShapes.Remove(name);
                }
                else
                {
                    layerData = doc.Shape(name);
                    if (layerData != null) layerData.Edited = edited;
                }
                if (layerData == null) continue;
                for (int i = 0; i < indices.Length; i++)
                {
                    int v = indices[i];
                    if (v >= 0 && v < vertexCount) layerData.Deltas[v] = deltas[i];
                }
            }
            foreach (string name in s.Deleted)
            {
                if (!doc.InSource(name)) continue;
                doc.ShapeEdits.Remove(name);
                doc.DeletedShapes.Add(name);
            }
            // The order as it was (renames keep their place); a name it lacks goes at the end.
            var known = new HashSet<string>();
            doc.ShapeOrder.Clear();
            foreach (string name in s.Order)
                if ((doc.InSource(name) || doc.ShapeEdits.ContainsKey(name)) && known.Add(name)) doc.ShapeOrder.Add(name);
            for (int i = 0; i < doc.ShapeSource.blendShapeCount; i++) { string name = doc.ShapeSource.GetBlendShapeName(i); if (known.Add(name)) doc.ShapeOrder.Add(name); }
            foreach (string name in doc.ShapeEdits.Keys) if (known.Add(name)) doc.ShapeOrder.Add(name);
            doc.Groups.Clear();
            foreach (var (name, indices, weights) in s.Groups)
            {
                var group = new VertexGroup { Name = name };
                for (int i = 0; i < indices.Length; i++) { int v = indices[i]; if (v >= 0 && v < vertexCount) group.Weights[v] = weights[i]; }
                doc.Groups.Add(group);
            }
            foreach (var (v, list) in s.Influences)
                if (v >= 0 && v < vertexCount && doc.HasSkin) doc.SetInfluences(v, list);
            if (s.BindPoses.Length > 0) ApplyBindPoses(target, s.BindPoses, s.AddedBones, extendBones);
            if (s.Masked.Count > 0)
            {
                var mask = doc.EnsureMask();
                foreach (var (v, m) in s.Masked) if (v >= 0 && v < vertexCount) mask[v] = m;
            }
            doc.Layer = s.Layer.Length > 0 && doc.HasShape(s.Layer) ? s.Layer : null;
            doc.Revision++;
        }

        // Only when the renderer has that many bones (an undone added bone leaves the original's), or, from a scene file
        // that lost them, once the added leaf bones are found again.
        private static void ApplyBindPoses(EditTarget target, Matrix4x4[] bindPoses, string[] added, bool extendBones)
        {
            var doc = target.Document;
            var skinned = target.Skinned;
            if (skinned == null) return;
            var bones = skinned.bones;
            if (bones.Length != bindPoses.Length)
            {
                if (!extendBones || bones.Length != doc.OriginalBoneCount || bones.Length + added.Length != bindPoses.Length) return;
                var extended = new List<Transform>(bones);
                foreach (string entry in added)
                {
                    var found = FindAdded(entry, skinned.transform.root);
                    if (found == null) return;
                    extended.Add(found);
                }
                skinned.bones = extended.ToArray();
            }
            doc.BindPoses = bindPoses;
            doc.BindPosesDirty = true;
            doc.BindPosesEdited = true;
        }

        private const char BoneSeparator = '\u001f';

        private static string PathUnder(Transform t, Transform root)
        {
            var names = new List<string>();
            for (var p = t; p != null && p != root; p = p.parent) names.Insert(0, p.name);
            return string.Join("/", names);
        }

        private static Transform FindAdded(string entry, Transform root)
        {
            int cut = entry.IndexOf(BoneSeparator);
            string id = cut >= 0 ? entry.Substring(0, cut) : entry;
            string path = cut >= 0 ? entry.Substring(cut + 1) : null;
            if (SceneGraphExporter.Resolve(id) is GameObject found && found != null) return found.transform;
            return !string.IsNullOrEmpty(path) && root != null ? root.Find(path) : null;
        }

        private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
