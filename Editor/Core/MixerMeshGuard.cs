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
    /// session ends, but a state recorded while it showed (an Undo step of another tool) can put it back on its
    /// renderer later: a missing mesh, saved so if the scene is. Each working mesh's original is remembered for the
    /// editor session (across script reloads); after Undo and Redo, after script reloads and before scene saves, a
    /// renderer showing a working mesh no session shows gets the original back.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerMeshGuard
    {
        private const string StateKey = "Orbiters.Mixer.MeshGuard";
        // Working mesh instance id -> its original's GlobalObjectId.
        private static readonly Dictionary<int, string> originals = Load();
        private static readonly HashSet<int> shown = new HashSet<int>();

        static MixerMeshGuard()
        {
            Undo.undoRedoPerformed += () => Repair();
            EditorSceneManager.sceneSaving += (scene, path) => Repair();
            EditorApplication.CallbackFunction once = null;
            once = () => { EditorApplication.update -= once; Repair(); };
            EditorApplication.update += once;
        }

        /// <summary>A session shows <paramref name="working"/> in place of <paramref name="original"/>.</summary>
        public static void Shown(Mesh working, Mesh original)
        {
            int id = working.GetInstanceID();
            shown.Add(id);
            if (originals.ContainsKey(id) || !EditorUtility.IsPersistent(original)) return;
            originals[id] = GlobalObjectId.GetGlobalObjectIdSlow(original).ToString();
            Save();
        }

        /// <summary>The session of <paramref name="working"/> ended (the mesh is destroyed next).</summary>
        public static void Released(Mesh working)
        {
            if (working == null) return;
            int id = working.GetInstanceID();
            shown.Remove(id);
            if (originals.Remove(id)) Save();
        }

        /// <summary>The original a working mesh stands for, or null when <paramref name="mesh"/> is not one.</summary>
        public static Mesh OriginalOf(Mesh mesh) => mesh != null ? Original(mesh.GetInstanceID()) : null;

        public static bool IsWorking(Mesh mesh) => mesh != null && originals.ContainsKey(mesh.GetInstanceID());

        /// <summary>Puts the originals back on every renderer showing a working mesh no session shows; how many it changed.</summary>
        public static int Repair()
        {
            if (originals.Count == 0) return 0;
            int repaired = 0;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
                Object holder = filter != null ? (Object)filter : renderer;
                if (holder == null) continue;
                int id = new SerializedObject(holder).FindProperty("m_Mesh").objectReferenceInstanceIDValue;
                if (id == 0 || shown.Contains(id) || !originals.ContainsKey(id)) continue;
                var original = Original(id);
                if (original == null) continue;
                if (filter != null) filter.sharedMesh = original; else ((SkinnedMeshRenderer)renderer).sharedMesh = original;
                EditorUtility.SetDirty(holder);
                if (renderer.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
                Debug.LogWarning("[Mixer] '" + renderer.name + "' was showing a Mixer working mesh no session shows (an Undo or Redo of a step recorded while it was edited): its mesh '" + original.name + "' is back.");
                repaired++;
            }
            return repaired;
        }

        /// <summary>
        /// A prefab is about to be written while sessions show working meshes (Apply overrides on an instance being
        /// edited): the prefab's renderers that point at a working mesh get its original back first, so the file never
        /// holds a reference to a mesh that is never saved. How many it changed.
        /// </summary>
        internal static int RepairPrefab(string path)
        {
            if (originals.Count == 0) return 0;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) return 0;
            int repaired = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
                Object holder = filter != null ? (Object)filter : renderer is SkinnedMeshRenderer ? renderer : null;
                if (holder == null) continue;
                int id = new SerializedObject(holder).FindProperty("m_Mesh").objectReferenceInstanceIDValue;
                if (id == 0 || !originals.ContainsKey(id)) continue;
                var original = Original(id);
                if (original == null) continue;
                if (filter != null) filter.sharedMesh = original; else ((SkinnedMeshRenderer)renderer).sharedMesh = original;
                EditorUtility.SetDirty(holder);
                Debug.LogWarning("[Mixer] '" + path + "' was being saved with Mixer's working copy of '" + original.name + "' on '" + renderer.name + "': the prefab keeps '" + original.name + "' (commit in Mixer, then apply, to put the edits in the prefab).");
                repaired++;
            }
            return repaired;
        }

        private static Mesh Original(int workingId) =>
            originals.TryGetValue(workingId, out string id) && GlobalObjectId.TryParse(id, out var parsed)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as Mesh
                : null;

        private sealed class PrefabSaves : AssetModificationProcessor
        {
            private static string[] OnWillSaveAssets(string[] paths)
            {
                if (originals.Count == 0) return paths;
                foreach (string path in paths)
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        try { RepairPrefab(path); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                return paths;
            }
        }

        private static void Save() => SessionState.SetString(StateKey, string.Join("\n", originals.Select(pair => pair.Key + "\t" + pair.Value)));

        private static Dictionary<int, string> Load()
        {
            var result = new Dictionary<int, string>();
            foreach (string line in SessionState.GetString(StateKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && int.TryParse(line.Substring(0, tab), out int id)) result[id] = line.Substring(tab + 1);
            }
            return result;
        }
    }
}
