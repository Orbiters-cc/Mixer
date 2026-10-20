using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Keeps renderers off Mixer's working meshes once no session shows them. A working mesh is destroyed when its
    /// session ends, but a state recorded while it showed (an Undo step of another tool, the Commit's own step, a
    /// deleted object brought back) can put it back on its renderer later: a missing mesh, saved so if the scene is.
    /// For the editor session (across script reloads) the guard remembers each working mesh's original, the renderer
    /// that showed it, and, once the session ended, the mesh that took its place (the committed asset, or the original
    /// after a revert); after Undo and Redo, after script reloads and before scene saves, a renderer showing a working
    /// mesh no session shows gets that mesh. A copy of an edited object (Ctrl+D, paste) that shows another renderer's
    /// working mesh is handed to <see cref="AdoptCopy"/>, else it gets the original.
    /// </summary>
    internal static class MixerMeshGuard
    {
        private const string StateKey = "Orbiters.Mixer.MeshGuard";

        private struct Entry
        {
            /// <summary>The original (<see cref="SceneGraphExporter.IdOf"/>), or the mesh that replaced the working mesh once released.</summary>
            public string Mesh;
            /// <summary>Instance id of the renderer that showed the working mesh.</summary>
            public int Renderer;
            public bool Released;
        }

        // Working mesh instance id -> its entry.
        private static readonly Dictionary<int, Entry> entries = Load();

        /// <summary>A copy of an edited object (renderer, the working mesh it shows): true when a session took it over.</summary>
        internal static Func<Renderer, Mesh, bool> AdoptCopy;

        /// <summary>A session shows <paramref name="working"/> in place of <paramref name="original"/> on <paramref name="renderer"/>.</summary>
        public static void Shown(Mesh working, Mesh original, Renderer renderer)
        {
            if (working == null || original == null) return;
            entries[working.GetInstanceID()] = new Entry { Mesh = SceneGraphExporter.IdOf(original), Renderer = renderer != null ? renderer.GetInstanceID() : 0 };
            Save();
        }

        /// <summary>The session of <paramref name="working"/> ended (the mesh is destroyed next); <paramref name="replacement"/> takes its place wherever it comes back.</summary>
        public static void Released(Mesh working, Mesh replacement)
        {
            // A destroyed mesh still has its instance id: what showed it gets the replacement.
            if ((object)working == null) return;
            int id = working.GetInstanceID();
            entries.TryGetValue(id, out var entry);
            if (replacement == null) replacement = Resolve(entry.Mesh);
            entries[id] = new Entry { Mesh = replacement != null ? SceneGraphExporter.IdOf(replacement) : entry.Mesh, Renderer = entry.Renderer, Released = true };
            Save();
        }

        /// <summary>The original a live working mesh stands for, or null when <paramref name="mesh"/> is not one.</summary>
        public static Mesh OriginalOf(Mesh mesh) => mesh != null && entries.TryGetValue(mesh.GetInstanceID(), out var e) && !e.Released ? Resolve(e.Mesh) : null;

        public static bool IsWorking(Mesh mesh) => mesh != null && entries.TryGetValue(mesh.GetInstanceID(), out var e) && !e.Released;

        /// <summary>The renderer a live working mesh belongs to (instance id), 0 when unknown.</summary>
        public static int RendererOf(Mesh mesh) => mesh != null && entries.TryGetValue(mesh.GetInstanceID(), out var e) ? e.Renderer : 0;

        /// <summary>
        /// Checks the renderers that ever showed a working mesh (cheap: after every Undo and Redo); with
        /// <paramref name="everywhere"/>, every renderer of the open scenes too (before a scene save, after a reload).
        /// Returns how many it changed.
        /// </summary>
        public static int Repair(bool everywhere = false)
        {
            if (entries.Count == 0) return 0;
            int repaired = 0;
            var seen = new HashSet<int>();
            foreach (int rendererId in entries.Values.Select(e => e.Renderer).Distinct().ToList())
            {
                if (rendererId == 0 || !seen.Add(rendererId)) continue;
                if (EditorUtility.InstanceIDToObject(rendererId) is Renderer r && r != null && RepairOne(r)) repaired++;
            }
            if (everywhere)
                foreach (var r in Object.FindObjectsOfType<Renderer>(true))
                    if (r != null && seen.Add(r.GetInstanceID()) && RepairOne(r)) repaired++;
            return repaired;
        }

        /// <summary>Checks the renderers under objects just made (a duplicate, a paste).</summary>
        public static int RepairUnder(GameObject root)
        {
            if (entries.Count == 0 || root == null) return 0;
            int repaired = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (RepairOne(r)) repaired++;
            return repaired;
        }

        // The mesh id a renderer refers to, a destroyed one too (sharedMesh is null for a missing reference).
        private static int MeshIdOf(Object holder, Mesh shown)
        {
            if (shown != null) return shown.GetInstanceID();
            var property = new SerializedObject(holder).FindProperty("m_Mesh");
            return property != null ? property.objectReferenceInstanceIDValue : 0;
        }

        private static bool RepairOne(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
            var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
            Object holder = filter != null ? (Object)filter : renderer is SkinnedMeshRenderer ? renderer : null;
            if (holder == null) return false;
            var shown = filter != null ? filter.sharedMesh : ((SkinnedMeshRenderer)renderer).sharedMesh;
            int id = MeshIdOf(holder, shown);
            if (id == 0 || !entries.TryGetValue(id, out var entry)) return false;
            if (!entry.Released && shown != null)
            {
                // A live working mesh on its own renderer is a session's; on another one it is a copy of an edited object.
                if (entry.Renderer == renderer.GetInstanceID()) return false;
                if (AdoptCopy != null && AdoptCopy(renderer, shown)) return true;
            }
            var mesh = Resolve(entry.Mesh);
            if (mesh == null) return false;
            if (filter != null) filter.sharedMesh = mesh; else ((SkinnedMeshRenderer)renderer).sharedMesh = mesh;
            EditorUtility.SetDirty(holder);
            if (renderer.gameObject.scene.IsValid() && !EditorApplication.isPlaying) EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            Debug.LogWarning("[Mixer] '" + renderer.name + "' was showing a Mixer working mesh no session shows (an Undo or Redo of a step recorded while it was edited, or a copy of an edited object): it shows '" + mesh.name + "' now.");
            return true;
        }

        /// <summary>
        /// A prefab is about to be written while sessions show working meshes (Apply overrides on an instance being
        /// edited): the prefab's renderers that point at a working mesh get its original back first, so the file never
        /// holds a reference to a mesh that is never saved. How many it changed.
        /// </summary>
        internal static int RepairPrefab(string path)
        {
            if (entries.Count == 0) return 0;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) return 0;
            int repaired = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
                Object holder = filter != null ? (Object)filter : renderer is SkinnedMeshRenderer ? renderer : null;
                if (holder == null) continue;
                int id = new SerializedObject(holder).FindProperty("m_Mesh").objectReferenceInstanceIDValue;
                if (id == 0 || !entries.TryGetValue(id, out var entry)) continue;
                var original = Resolve(entry.Mesh);
                if (original == null) continue;
                if (filter != null) filter.sharedMesh = original; else ((SkinnedMeshRenderer)renderer).sharedMesh = original;
                EditorUtility.SetDirty(holder);
                Debug.LogWarning("[Mixer] '" + path + "' was being saved with Mixer's working copy of '" + original.name + "' on '" + renderer.name + "': the prefab keeps '" + original.name + "' (commit in Mixer, then apply, to put the edits in the prefab).");
                repaired++;
            }
            return repaired;
        }

        private static Mesh Resolve(string id) => string.IsNullOrEmpty(id) ? null : SceneGraphExporter.Resolve(id) as Mesh;

        private sealed class PrefabSaves : AssetModificationProcessor
        {
            private static string[] OnWillSaveAssets(string[] paths)
            {
                if (entries.Count == 0) return paths;
                foreach (string path in paths)
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        try { RepairPrefab(path); MixerSession.WatchPrefab(path); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                return paths;
            }
        }

        private static void Save() => SessionState.SetString(StateKey, string.Join("\n", entries.Select(pair => pair.Key + "\t" + pair.Value.Mesh + "\t" + pair.Value.Renderer + "\t" + (pair.Value.Released ? "1" : "0"))));

        private static Dictionary<int, Entry> Load()
        {
            var result = new Dictionary<int, Entry>();
            foreach (string line in SessionState.GetString(StateKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int id)) continue;
                int renderer = parts.Length > 2 && int.TryParse(parts[2], out int r) ? r : 0;
                result[id] = new Entry { Mesh = parts[1], Renderer = renderer, Released = parts.Length > 3 && parts[3] == "1" };
            }
            return result;
        }
    }
}
