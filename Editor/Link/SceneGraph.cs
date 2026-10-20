using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor
{
    /// <summary>The scene as Blender receives it: every object as a node with its transform, kind and mesh, plus the materials.</summary>
    [Serializable]
    internal sealed class SceneGraph
    {
        public string scene;
        public string sceneGuid;
        public List<SceneNode> nodes = new List<SceneNode>();
        public List<SceneMaterial> materials = new List<SceneMaterial>();
        /// <summary>
        /// Renderers left out of the graph (kept out of Blender, inactive or EditorOnly when those are excluded) that a
        /// tool marked with a target FBX: the Mixer extension's Export for MCB lists them as missing instead of writing an
        /// FBX that silently lacks them.
        /// </summary>
        public List<SceneKeptOut> keptOut = new List<SceneKeptOut>();
    }

    [Serializable]
    internal sealed class SceneKeptOut { public string id; public string name; public string targetFbx; }

    [Serializable]
    internal sealed class SceneNode
    {
        public string id;
        public string parent;
        public string name;
        public bool active = true;
        /// <summary>Empty, Mesh, Skinned, Light, Camera or Bone.</summary>
        public string type = "Empty";
        public float[] p = new float[3];
        public float[] r = new float[4];
        public float[] s = new float[3];
        /// <summary>For Mesh and Skinned: the id of the mesh data (sent as MeshData), shared by renderers showing the same mesh.</summary>
        public string meshId;
        public int vertexCount;
        public string[] materials;
        /// <summary>For Skinned: node ids of the bones in the renderer's order, and the root bone.</summary>
        public string[] bones;
        public string rootBone;
        /// <summary>For Mesh and Skinned: the project file (a Unity path) the renderer's mesh stands for to the tool that told Mixer, e.g. a custom base's target FBX for MCB; null otherwise.</summary>
        public string targetFbx;
        public SceneLight light;
        public SceneCamera camera;
    }

    [Serializable]
    internal sealed class SceneLight { public string type; public float[] color; public float intensity; public float range; public float spot; }

    [Serializable]
    internal sealed class SceneCamera { public float fov; public float near; public float far; public bool ortho; public float size; }

    [Serializable]
    internal sealed class SceneMaterial
    {
        public string id;
        public string name;
        public string shader;
        public float[] color;
        public float metallic;
        public float smoothness;
        public float[] emission;
        public Dictionary<string, string> textures = new Dictionary<string, string>();
    }

    /// <summary>A small change to the scene, either way.</summary>
    [Serializable]
    internal sealed class SceneDelta
    {
        public List<SceneNodeTransform> transforms = new List<SceneNodeTransform>();
        public List<SceneNodeFlag> active = new List<SceneNodeFlag>();
        public List<SceneNodeName> renamed = new List<SceneNodeName>();
        public List<SceneNodeParent> reparented = new List<SceneNodeParent>();
        public List<string> removed = new List<string>();
        /// <summary>Nodes added or changed in kind or mesh (resent whole, their meshes too).</summary>
        public List<SceneNode> added = new List<SceneNode>();
        /// <summary>Nodes whose other properties changed (materials, target FBX, bones, root bone, light, camera): resent whole, their meshes not.</summary>
        public List<SceneNode> changed = new List<SceneNode>();
        /// <summary>The materials the added and changed nodes use.</summary>
        public List<SceneMaterial> materials = new List<SceneMaterial>();
        /// <summary>The graph's kept-out renderers when they changed, else null.</summary>
        public List<SceneKeptOut> keptOut;
        public bool Empty => transforms.Count == 0 && active.Count == 0 && renamed.Count == 0 && reparented.Count == 0 && removed.Count == 0 && added.Count == 0 && changed.Count == 0 && keptOut == null;
    }

    [Serializable] internal sealed class SceneNodeTransform { public string id; public float[] p; public float[] r; public float[] s; }
    [Serializable] internal sealed class SceneNodeFlag { public string id; public bool active; }
    [Serializable] internal sealed class SceneNodeName { public string id; public string name; }
    [Serializable] internal sealed class SceneNodeParent { public string id; public string parent; }

    /// <summary>
    /// Builds the scene graph from the loaded scenes, leaving out what the user excluded and Mixer's and XRay's own
    /// helpers, and diffs two graphs into a delta. Ids are GlobalObjectIds, stable across sessions and reloads.
    /// </summary>
    internal static class SceneGraphExporter
    {
        public static readonly string[] HelperPrefixes = { MixerInfo.HelperPrefix, "__XRayGizmos_", "__ReFit_" };

        private const string InstancePrefix = "InstanceId-";

        /// <summary>A stable id for an object: its GlobalObjectId, or its instance id for objects that have none (preview scenes, some temporaries).</summary>
        public static string IdOf(UnityEngine.Object o)
        {
            if (o == null) return null;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(o);
            return id.identifierType == 0 ? InstancePrefix + o.GetInstanceID() : id.ToString();
        }

        public static UnityEngine.Object Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith(InstancePrefix, StringComparison.Ordinal))
                return int.TryParse(id.Substring(InstancePrefix.Length), out int instance) ? EditorUtility.InstanceIDToObject(instance) : null;
            return GlobalObjectId.TryParse(id, out var parsed) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) : null;
        }

        public static SceneGraph Build(Scene scene, MixerLinkSettings settings)
        {
            var graph = new SceneGraph { scene = scene.name, sceneGuid = AssetDatabase.AssetPathToGUID(scene.path) };
            if (string.IsNullOrEmpty(graph.sceneGuid)) graph.sceneGuid = scene.name;
            var excluded = settings != null ? new HashSet<string>(settings.excludedIds) : new HashSet<string>();
            var materials = new Dictionary<int, SceneMaterial>();
            var boneIds = new HashSet<Transform>();
            var transforms = new List<Transform>();
            var left = new List<Transform>();
            foreach (var root in scene.GetRootGameObjects()) Collect(root.transform, excluded, transforms, left, settings);
            var resolver = MixerLink.TargetFbxResolver;
            if (resolver != null)
            {
                foreach (var t in left)
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                        string target = resolver(r);
                        if (!string.IsNullOrEmpty(target)) graph.keptOut.Add(new SceneKeptOut { id = IdOf(r.gameObject), name = r.name, targetFbx = target });
                    }
            }
            // Bones first: every transform a skinned renderer uses, so Blender builds armatures for them.
            foreach (var t in transforms)
            {
                var skinned = t.GetComponent<SkinnedMeshRenderer>();
                if (skinned == null || skinned.bones == null) continue;
                foreach (var b in skinned.bones) if (b != null) boneIds.Add(b);
            }
            var included = new HashSet<Transform>(transforms);
            foreach (var t in transforms)
            {
                var node = new SceneNode { id = IdOf(t.gameObject), name = t.name, active = t.gameObject.activeSelf };
                node.parent = t.parent != null && included.Contains(t.parent) ? IdOf(t.parent.gameObject) : null;
                Fill(node.p, t.localPosition); FillQuaternion(node.r, t.localRotation); Fill(node.s, t.localScale);
                var skinned = t.GetComponent<SkinnedMeshRenderer>();
                var filter = t.GetComponent<MeshFilter>();
                var light = t.GetComponent<Light>();
                var camera = t.GetComponent<Camera>();
                if (skinned != null && skinned.sharedMesh != null)
                {
                    node.type = "Skinned";
                    var mesh = MixerMeshGuard.OriginalOf(skinned.sharedMesh) ?? skinned.sharedMesh;
                    node.meshId = IdOf(mesh);
                    node.vertexCount = mesh.vertexCount;
                    node.materials = MaterialIds(skinned.sharedMaterials, materials);
                    node.bones = skinned.bones.Select(b => b != null ? IdOf(b.gameObject) : "").ToArray();
                    node.rootBone = skinned.rootBone != null ? IdOf(skinned.rootBone.gameObject) : null;
                    node.targetFbx = MixerLink.TargetFbxResolver?.Invoke(skinned);
                }
                else if (filter != null && filter.sharedMesh != null && t.GetComponent<MeshRenderer>() != null)
                {
                    node.type = "Mesh";
                    var mesh = MixerMeshGuard.OriginalOf(filter.sharedMesh) ?? filter.sharedMesh;
                    node.meshId = IdOf(mesh);
                    node.vertexCount = mesh.vertexCount;
                    node.materials = MaterialIds(t.GetComponent<MeshRenderer>().sharedMaterials, materials);
                    node.targetFbx = MixerLink.TargetFbxResolver?.Invoke(t.GetComponent<MeshRenderer>());
                }
                else if (light != null)
                {
                    node.type = "Light";
                    node.light = new SceneLight { type = light.type.ToString(), color = new[] { light.color.r, light.color.g, light.color.b }, intensity = light.intensity, range = light.range, spot = light.spotAngle };
                }
                else if (camera != null)
                {
                    node.type = "Camera";
                    node.camera = new SceneCamera { fov = camera.fieldOfView, near = camera.nearClipPlane, far = camera.farClipPlane, ortho = camera.orthographic, size = camera.orthographicSize };
                }
                else if (boneIds.Contains(t)) node.type = "Bone";
                graph.nodes.Add(node);
            }
            graph.materials.AddRange(materials.Values);
            return graph;
        }

        // Into: what goes to Blender; left: the roots of what the user's exclusions keep out (Mixer's own helpers are neither).
        private static void Collect(Transform t, HashSet<string> excluded, List<Transform> into, List<Transform> left, MixerLinkSettings settings)
        {
            if (t == null) return;
            string name = t.name;
            foreach (string prefix in HelperPrefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (t.gameObject.hideFlags != HideFlags.None && (t.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) return;
            if (excluded.Contains(IdOf(t.gameObject)) ||
                (settings != null && settings.excludeInactive && !t.gameObject.activeInHierarchy) ||
                (settings != null && settings.excludeEditorOnly && t.CompareTag("EditorOnly")))
            {
                left.Add(t);
                return;
            }
            into.Add(t);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), excluded, into, left, settings);
        }

        private static string[] MaterialIds(Material[] sharedMaterials, Dictionary<int, SceneMaterial> materials)
        {
            if (sharedMaterials == null) return new string[0];
            var ids = new string[sharedMaterials.Length];
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                var m = sharedMaterials[i];
                if (m == null) { ids[i] = ""; continue; }
                int key = m.GetInstanceID();
                if (!materials.TryGetValue(key, out var info)) materials[key] = info = Describe(m);
                ids[i] = info.id;
            }
            return ids;
        }

        /// <summary>What Blender can show of a Unity material: colour, metallic and smoothness, and the usual texture slots as absolute paths.</summary>
        public static SceneMaterial Describe(Material m)
        {
            var info = new SceneMaterial { id = IdOf(m), name = m.name, shader = m.shader != null ? m.shader.name : "" };
            var color = m.HasProperty("_Color") ? m.GetColor("_Color") : m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white;
            info.color = new[] { color.r, color.g, color.b, color.a };
            info.metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            info.smoothness = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0.5f;
            if (m.HasProperty("_EmissionColor")) { var e = m.GetColor("_EmissionColor"); info.emission = new[] { e.r, e.g, e.b }; }
            foreach (var (channel, names) in new[]
            {
                ("basecolor", new[] { "_MainTex", "_BaseMap", "_BaseColorMap" }),
                ("normal", new[] { "_BumpMap", "_NormalMap" }),
                ("metallic", new[] { "_MetallicGlossMap", "_MetallicMap" }),
                ("roughness", new[] { "_RoughnessMap" }),
                ("emission", new[] { "_EmissionMap" }),
                ("occlusion", new[] { "_OcclusionMap" }),
            })
            {
                foreach (string property in names)
                {
                    if (!m.HasProperty(property)) continue;
                    var texture = m.GetTexture(property);
                    if (texture == null) continue;
                    string path = AssetDatabase.GetAssetPath(texture);
                    if (string.IsNullOrEmpty(path)) continue;
                    info.textures[channel] = System.IO.Path.GetFullPath(path);
                    break;
                }
            }
            return info;
        }

        /// <summary>What changed from <paramref name="before"/> to <paramref name="after"/>.</summary>
        public static SceneDelta Diff(SceneGraph before, SceneGraph after)
        {
            var delta = new SceneDelta();
            var old = new Dictionary<string, SceneNode>();
            foreach (var n in before.nodes) old[n.id] = n;
            var seen = new HashSet<string>();
            var usedMaterials = new HashSet<string>();
            foreach (var node in after.nodes)
            {
                seen.Add(node.id);
                if (!old.TryGetValue(node.id, out var was) || was.type != node.type || was.meshId != node.meshId || was.vertexCount != node.vertexCount)
                {
                    delta.added.Add(node);
                    if (node.materials != null) usedMaterials.UnionWith(node.materials);
                    continue;
                }
                if (!SameStrings(was.materials, node.materials) || was.targetFbx != node.targetFbx || !SameStrings(was.bones, node.bones) || was.rootBone != node.rootBone
                    || !SameLight(was.light, node.light) || !SameCamera(was.camera, node.camera))
                {
                    delta.changed.Add(node);
                    if (node.materials != null) usedMaterials.UnionWith(node.materials);
                }
                if (was.name != node.name) delta.renamed.Add(new SceneNodeName { id = node.id, name = node.name });
                if (was.parent != node.parent) delta.reparented.Add(new SceneNodeParent { id = node.id, parent = node.parent });
                if (was.active != node.active) delta.active.Add(new SceneNodeFlag { id = node.id, active = node.active });
                if (!Same(was.p, node.p) || !Same(was.r, node.r) || !Same(was.s, node.s)) delta.transforms.Add(new SceneNodeTransform { id = node.id, p = node.p, r = node.r, s = node.s });
            }
            foreach (var id in old.Keys) if (!seen.Contains(id)) delta.removed.Add(id);
            foreach (var material in after.materials) if (material != null && usedMaterials.Contains(material.id)) delta.materials.Add(material);
            if (!SameKeptOut(before.keptOut, after.keptOut)) delta.keptOut = after.keptOut ?? new List<SceneKeptOut>();
            return delta;
        }

        private static bool SameLight(SceneLight a, SceneLight b)
        {
            if (a == null || b == null) return a == b;
            return a.type == b.type && Same(a.color, b.color) && Mathf.Abs(a.intensity - b.intensity) <= 1e-6f && Mathf.Abs(a.range - b.range) <= 1e-6f && Mathf.Abs(a.spot - b.spot) <= 1e-6f;
        }

        private static bool SameCamera(SceneCamera a, SceneCamera b)
        {
            if (a == null || b == null) return a == b;
            return Mathf.Abs(a.fov - b.fov) <= 1e-6f && Mathf.Abs(a.near - b.near) <= 1e-6f && Mathf.Abs(a.far - b.far) <= 1e-6f && a.ortho == b.ortho && Mathf.Abs(a.size - b.size) <= 1e-6f;
        }

        private static bool SameKeptOut(List<SceneKeptOut> a, List<SceneKeptOut> b)
        {
            int na = a?.Count ?? 0, nb = b?.Count ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (a[i].id != b[i].id || a[i].name != b[i].name || a[i].targetFbx != b[i].targetFbx) return false;
            return true;
        }

        private static bool Same(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false;
            return true;
        }

        private static bool SameStrings(string[] a, string[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Fill(float[] into, Vector3 v) { into[0] = v.x; into[1] = v.y; into[2] = v.z; }
        private static void FillQuaternion(float[] into, Quaternion q) { into[0] = q.x; into[1] = q.y; into[2] = q.z; into[3] = q.w; }
        public static Vector3 ToVector(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
        public static Quaternion ToQuaternion(float[] a) => a != null && a.Length >= 4 ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;
    }
}
