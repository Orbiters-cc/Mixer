using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The scene graph a link host last built, kept so a change does not rebuild it: ids are worked out once per object
    /// (one batch of <see cref="GlobalObjectId.GetGlobalObjectIdsSlow"/> for the scene, then a dictionary by instance id),
    /// and the nodes remember their transforms and components, so a change of known objects re-describes those nodes
    /// only (<see cref="Redescribe"/>), a Mixer session event re-reads transforms only (<see cref="Retransform"/>), and the
    /// late answers of tools re-ask the target FBX only (<see cref="Recheck"/>). Anything that can change which objects
    /// are nodes (objects made, destroyed, reparented, renamed, shown or hidden, another scene) is a full
    /// <see cref="Build"/>, which reuses the ids.
    /// </summary>
    internal sealed class SceneGraphCache
    {
        // Ids by instance id. An unsaved scene's objects get other ids once it is saved, and unpacked prefab instances
        // others again: the host forgets them then (Forget). Ids in the instance form are never kept for assets (a mesh
        // made in memory and saved as an asset afterwards keeps its instance id).
        private Dictionary<int, string> ids = new Dictionary<int, string>();
        private string idsScene;

        // What the last build saw: per node its transform (same order as the graph's nodes), the node index per
        // GameObject, the transforms that are nodes, the bones skinned renderers use, and the roots the user's exclusions
        // keep out (for the kept-out list).
        private readonly List<Transform> nodeTransforms = new List<Transform>();
        private readonly Dictionary<int, int> nodeOf = new Dictionary<int, int>();
        private readonly HashSet<Transform> included = new HashSet<Transform>();
        private readonly HashSet<Transform> boneIds = new HashSet<Transform>();
        private readonly List<Transform> left = new List<Transform>();
        private SceneGraph built;

        /// <summary>The ids may have changed (a scene saved, prefab instances unpacked or updated): worked out again.</summary>
        public void Forget() => ids.Clear();

        /// <summary>The id of an object (<see cref="SceneGraphExporter.IdOf"/>), from the cache when it was worked out before.</summary>
        public string IdOf(UnityEngine.Object o)
        {
            if (o == null) return null;
            int instance = o.GetInstanceID();
            if (ids.TryGetValue(instance, out string id)) return id;
            var global = GlobalObjectId.GetGlobalObjectIdSlow(o);
            id = global.identifierType == 0 ? SceneGraphExporter.InstancePrefix + instance : global.ToString();
            if (global.identifierType != 0 || o is GameObject) ids[instance] = id;
            return id;
        }

        // Every GameObject of the scene gets its id in one batch; ids of objects gone are let go.
        private void Prime(Scene scene, List<GameObject> all)
        {
            if (idsScene != scene.path) { ids.Clear(); idsScene = scene.path; }
            var kept = new Dictionary<int, string>(all.Count + 64);
            var missing = new List<GameObject>();
            foreach (var go in all)
            {
                int instance = go.GetInstanceID();
                if (ids.TryGetValue(instance, out string id)) kept[instance] = id; else missing.Add(go);
            }
            // Assets (meshes, materials) stay: they are few, and their ids do not depend on the scene.
            foreach (var pair in ids)
            {
                if (kept.ContainsKey(pair.Key)) continue;
                var o = EditorUtility.InstanceIDToObject(pair.Key);
                if (o != null && !(o is GameObject)) kept[pair.Key] = pair.Value;
            }
            if (missing.Count > 0)
            {
                var objects = missing.ToArray();
                var found = new GlobalObjectId[objects.Length];
                GlobalObjectId.GetGlobalObjectIdsSlow(objects, found);
                for (int i = 0; i < objects.Length; i++)
                {
                    int instance = objects[i].GetInstanceID();
                    kept[instance] = found[i].identifierType == 0 ? SceneGraphExporter.InstancePrefix + instance : found[i].ToString();
                }
            }
            ids = kept;
        }

        /// <summary>The whole graph of the scene, remembered for the incremental updates.</summary>
        public SceneGraph Build(Scene scene, MixerLinkSettings settings)
        {
            var graph = new SceneGraph { scene = scene.name, sceneGuid = AssetDatabase.AssetPathToGUID(scene.path) };
            if (string.IsNullOrEmpty(graph.sceneGuid)) graph.sceneGuid = scene.name;
            var roots = scene.GetRootGameObjects();
            var all = new List<GameObject>();
            var children = new List<Transform>();
            foreach (var root in roots)
            {
                root.GetComponentsInChildren(true, children);
                foreach (var t in children) all.Add(t.gameObject);
            }
            Prime(scene, all);
            var excluded = settings != null ? new HashSet<string>(settings.excludedIds) : new HashSet<string>();
            var materials = new Dictionary<int, SceneMaterial>();
            var transforms = new List<Transform>();
            left.Clear();
            foreach (var root in roots) Collect(root.transform, excluded, transforms, settings);
            graph.keptOut = KeptOut();
            // Bones first: every transform a skinned renderer uses, so Blender builds armatures for them.
            boneIds.Clear();
            foreach (var t in transforms)
            {
                var skinned = t.GetComponent<SkinnedMeshRenderer>();
                if (skinned == null || skinned.bones == null) continue;
                foreach (var b in skinned.bones) if (b != null) boneIds.Add(b);
            }
            included.Clear();
            included.UnionWith(transforms);
            nodeTransforms.Clear();
            nodeOf.Clear();
            foreach (var t in transforms)
            {
                nodeOf[t.gameObject.GetInstanceID()] = graph.nodes.Count;
                nodeTransforms.Add(t);
                graph.nodes.Add(Describe(t, materials));
            }
            graph.materials.AddRange(materials.Values);
            built = graph;
            return graph;
        }

        // Into: what goes to Blender; left: the roots of what the user's exclusions keep out (Mixer's own helpers are neither).
        private void Collect(Transform t, HashSet<string> excluded, List<Transform> into, MixerLinkSettings settings)
        {
            if (t == null) return;
            string name = t.name;
            foreach (string prefix in SceneGraphExporter.HelperPrefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (t.gameObject.hideFlags != HideFlags.None && (t.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) return;
            if (excluded.Contains(IdOf(t.gameObject)) ||
                (settings != null && settings.excludeInactive && !t.gameObject.activeInHierarchy) ||
                (settings != null && settings.excludeEditorOnly && t.CompareTag("EditorOnly")))
            {
                left.Add(t);
                return;
            }
            into.Add(t);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), excluded, into, settings);
        }

        // Renderers kept out of the graph that a tool marked with a target FBX.
        private List<SceneKeptOut> KeptOut()
        {
            var result = new List<SceneKeptOut>();
            var resolver = MixerLink.TargetFbxResolver;
            if (resolver == null) return result;
            foreach (var t in left)
            {
                if (t == null) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                    string target = resolver(r);
                    if (!string.IsNullOrEmpty(target)) result.Add(new SceneKeptOut { id = IdOf(r.gameObject), name = r.name, targetFbx = target });
                }
            }
            return result;
        }

        private SceneNode Describe(Transform t, Dictionary<int, SceneMaterial> materials)
        {
            var node = new SceneNode { id = IdOf(t.gameObject), name = t.name, active = t.gameObject.activeSelf };
            node.parent = t.parent != null && included.Contains(t.parent) ? IdOf(t.parent.gameObject) : null;
            Fill(node.p, t.localPosition); FillQuaternion(node.r, t.localRotation); Fill(node.s, t.localScale);
            var skinned = t.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null)
            {
                node.type = "Skinned";
                var mesh = MixerMeshGuard.OriginalOf(skinned.sharedMesh) ?? skinned.sharedMesh;
                node.meshId = IdOf(mesh);
                node.vertexCount = mesh.vertexCount;
                node.materials = MaterialIds(skinned.sharedMaterials, materials);
                var bones = skinned.bones;
                node.bones = new string[bones.Length];
                for (int i = 0; i < bones.Length; i++) node.bones[i] = bones[i] != null ? IdOf(bones[i].gameObject) : "";
                node.rootBone = skinned.rootBone != null ? IdOf(skinned.rootBone.gameObject) : null;
                node.targetFbx = MixerLink.TargetFbxResolver?.Invoke(skinned);
                return node;
            }
            var filter = t.GetComponent<MeshFilter>();
            var meshRenderer = filter != null && filter.sharedMesh != null ? t.GetComponent<MeshRenderer>() : null;
            if (meshRenderer != null)
            {
                node.type = "Mesh";
                var mesh = MixerMeshGuard.OriginalOf(filter.sharedMesh) ?? filter.sharedMesh;
                node.meshId = IdOf(mesh);
                node.vertexCount = mesh.vertexCount;
                node.materials = MaterialIds(meshRenderer.sharedMaterials, materials);
                node.targetFbx = MixerLink.TargetFbxResolver?.Invoke(meshRenderer);
                return node;
            }
            var light = t.GetComponent<Light>();
            if (light != null)
            {
                node.type = "Light";
                node.light = new SceneLight { type = light.type.ToString(), color = new[] { light.color.r, light.color.g, light.color.b }, intensity = light.intensity, range = light.range, spot = light.spotAngle };
                return node;
            }
            var camera = t.GetComponent<Camera>();
            if (camera != null)
            {
                node.type = "Camera";
                node.camera = new SceneCamera { fov = camera.fieldOfView, near = camera.nearClipPlane, far = camera.farClipPlane, ortho = camera.orthographic, size = camera.orthographicSize };
                return node;
            }
            if (boneIds.Contains(t)) node.type = "Bone";
            return node;
        }

        private string[] MaterialIds(Material[] sharedMaterials, Dictionary<int, SceneMaterial> materials)
        {
            if (sharedMaterials == null) return new string[0];
            var result = new string[sharedMaterials.Length];
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                var m = sharedMaterials[i];
                if (m == null) { result[i] = ""; continue; }
                int key = m.GetInstanceID();
                if (!materials.TryGetValue(key, out var info)) materials[key] = info = SceneGraphExporter.Describe(m, IdOf(m));
                result[i] = info.id;
            }
            return result;
        }

        /// <summary>
        /// Re-describes the nodes of these GameObjects in <paramref name="graph"/> (the graph <see cref="Build"/> made,
        /// as Blender has it) and puts what changed into <paramref name="delta"/>. An object that is no node is left
        /// alone: it is kept out (excluded, a helper, another scene), and what makes one a node again (its activity, tag,
        /// parents, or new objects) rebuilds the graph anyway. False, with nothing changed, when what the nodes are
        /// changed (a skinned renderer's bones, a node turning into another kind, a node gone): a full build then.
        /// </summary>
        public bool Redescribe(ICollection<int> gameObjects, SceneGraph graph, SceneDelta delta)
        {
            if (graph == null || graph != built) return false;
            var described = new List<(int index, SceneNode node)>(gameObjects.Count);
            var materials = new Dictionary<int, SceneMaterial>();
            foreach (int instance in gameObjects)
            {
                if (!nodeOf.TryGetValue(instance, out int index)) continue;
                var t = nodeTransforms[index];
                if (t == null) return false;
                var was = graph.nodes[index];
                var node = Describe(t, materials);
                // Which transforms are bones depends on every skinned renderer's bone list, and a node's kind on its
                // components: a change of either is a full build.
                if (node.type != was.type || (node.type == "Skinned" && !SceneGraphExporter.SameStrings(node.bones, was.bones))) return false;
                described.Add((index, node));
            }
            var used = new HashSet<string>();
            foreach (var (index, node) in described)
            {
                SceneGraphExporter.DiffNode(graph.nodes[index], node, delta, used);
                graph.nodes[index] = node;
            }
            foreach (var material in materials.Values) if (used.Contains(material.id)) delta.materials.Add(material);
            return true;
        }

        /// <summary>The nodes whose transform moved since <paramref name="graph"/> (the graph <see cref="Build"/> made) has them; false when a node is gone (a full build then).</summary>
        public bool Retransform(SceneGraph graph, SceneDelta delta)
        {
            if (graph == null || graph != built) return false;
            for (int i = 0; i < nodeTransforms.Count; i++)
            {
                var t = nodeTransforms[i];
                if (t == null) return false;
                var node = graph.nodes[i];
                Vector3 p = t.localPosition, s = t.localScale;
                Quaternion r = t.localRotation;
                if (Close(node.p, p) && Close(node.r, r) && Close(node.s, s)) continue;
                Fill(node.p, p); FillQuaternion(node.r, r); Fill(node.s, s);
                delta.transforms.Add(new SceneNodeTransform { id = node.id, p = node.p, r = node.r, s = node.s });
            }
            return true;
        }

        /// <summary>The target FBX of every mesh node and the kept-out list asked again (tools answer from caches that fill late).</summary>
        public bool Recheck(SceneGraph graph, SceneDelta delta)
        {
            if (graph == null || graph != built) return false;
            var resolver = MixerLink.TargetFbxResolver;
            for (int i = 0; i < nodeTransforms.Count; i++)
            {
                var node = graph.nodes[i];
                if (node.type != "Skinned" && node.type != "Mesh") continue;
                var t = nodeTransforms[i];
                if (t == null) return false;
                Renderer renderer = node.type == "Skinned" ? (Renderer)t.GetComponent<SkinnedMeshRenderer>() : t.GetComponent<MeshRenderer>();
                if (renderer == null) return false;
                string target = resolver?.Invoke(renderer);
                if (target == node.targetFbx) continue;
                node.targetFbx = target;
                delta.changed.Add(node);
            }
            var keptOut = KeptOut();
            if (!SceneGraphExporter.SameKeptOut(graph.keptOut, keptOut)) { graph.keptOut = keptOut; delta.keptOut = keptOut; }
            return true;
        }

        private static bool Close(float[] a, Vector3 v) => Mathf.Abs(a[0] - v.x) <= 1e-6f && Mathf.Abs(a[1] - v.y) <= 1e-6f && Mathf.Abs(a[2] - v.z) <= 1e-6f;
        private static bool Close(float[] a, Quaternion q) => Mathf.Abs(a[0] - q.x) <= 1e-6f && Mathf.Abs(a[1] - q.y) <= 1e-6f && Mathf.Abs(a[2] - q.z) <= 1e-6f && Mathf.Abs(a[3] - q.w) <= 1e-6f;
        private static void Fill(float[] into, Vector3 v) { into[0] = v.x; into[1] = v.y; into[2] = v.z; }
        private static void FillQuaternion(float[] into, Quaternion q) { into[0] = q.x; into[1] = q.y; into[2] = q.z; into[3] = q.w; }
    }
}
