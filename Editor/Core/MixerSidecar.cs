using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What Unity's Mesh cannot hold, kept beside a committed mesh asset as <c>&lt;mesh&gt;.mixer.json</c>: the vertex
    /// groups. Read back when the mesh is edited again, and sent to Blender as its vertex groups.
    /// </summary>
    internal static class MixerSidecar
    {
        public sealed class Data
        {
            public int version = 1;
            public int vertexCount;
            public List<GroupData> groups = new List<GroupData>();
        }

        public sealed class GroupData
        {
            public string name;
            public List<int> indices = new List<int>();
            public List<float> weights = new List<float>();
        }

        public static string PathFor(string assetPath) => Path.ChangeExtension(assetPath, null) + ".mixer.json";

        /// <summary>Writes the groups beside the asset, or removes the file when there are none.</summary>
        public static void Save(string assetPath, MeshDocument doc)
        {
            string path = PathFor(assetPath);
            if (doc.Groups.Count == 0)
            {
                if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
                return;
            }
            var data = new Data { vertexCount = doc.VertexCount };
            foreach (var g in doc.Groups)
            {
                var gd = new GroupData { name = g.Name };
                foreach (var pair in g.Weights) { gd.indices.Add(pair.Key); gd.weights.Add(pair.Value); }
                data.groups.Add(gd);
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
            AssetDatabase.ImportAsset(path);
        }

        /// <summary>Loads the groups beside a mesh asset into the document; false when there is no file for it.</summary>
        public static bool Load(Mesh mesh, MeshDocument doc)
        {
            if (mesh == null || !AssetDatabase.Contains(mesh)) return false;
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
            string path = PathFor(assetPath);
            if (!File.Exists(path)) return false;
            try
            {
                var data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(path));
                if (data == null || data.vertexCount != doc.VertexCount) return false;
                doc.Groups.Clear();
                foreach (var gd in data.groups)
                {
                    var g = new VertexGroup { Name = gd.name };
                    for (int i = 0; i < gd.indices.Count && i < gd.weights.Count; i++) if (gd.indices[i] >= 0 && gd.indices[i] < doc.VertexCount) g.Weights[gd.indices[i]] = gd.weights[i];
                    doc.Groups.Add(g);
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mixer] The vertex groups beside '" + assetPath + "' could not be read: " + e.Message);
                return false;
            }
        }
    }
}
