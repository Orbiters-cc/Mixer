using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor
{
    /// <summary>One line of the hierarchy: a scene header or an object, as far down as it is open.</summary>
    internal sealed class HierarchyRow
    {
        public Scene Scene;
        public GameObject Object;
        public int InstanceId;
        public int Depth;
        public bool IsScene;
        public bool HasChildren;
        public bool Expanded;
        public string Name;
        public bool Active;
        public bool PrefabInstance, PrefabRoot, PrefabMissing;
        public bool MissingScript;
        public bool Hidden, PickingDisabled;
        /// <summary>Matches the search (rows that only lead to a match are dimmed).</summary>
        public bool Match = true;
        /// <summary>Kept out of Blender (itself or through an ancestor).</summary>
        public bool Excluded;
        public bool BlenderOwned;
        public bool MixerEdited;
    }

    /// <summary>What the search and the filter chips keep.</summary>
    internal sealed class HierarchyFilter
    {
        public string Text = string.Empty;
        public bool Meshes, Skinned, Lights, Cameras, Prefabs, Missing, Inactive;
        public bool Any => !string.IsNullOrEmpty(Text) || Meshes || Skinned || Lights || Cameras || Prefabs || Missing || Inactive;
    }

    /// <summary>
    /// The rows of the hierarchy from the loaded scenes (or the open prefab stage), with Unity's own expanded state
    /// when a Unity Hierarchy exists (read and written through its internals) and Mixer's own otherwise.
    /// </summary>
    internal sealed class HierarchyModel
    {
        private static readonly Type HierarchyType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        private static readonly MethodInfo GetExpandedIDs = HierarchyType?.GetMethod("GetExpandedIDs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo SetExpandedMethod = HierarchyType?.GetMethod("SetExpanded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
        private const string ExpandedKey = "Orbiters.Mixer.Hierarchy.Expanded";

        public readonly List<HierarchyRow> Rows = new List<HierarchyRow>();
        public readonly HashSet<int> Expanded = new HashSet<int>();
        private readonly HashSet<int> excludedIds = new HashSet<int>();
        private readonly HashSet<int> ownedIds = new HashSet<int>();
        private readonly HashSet<int> editedIds = new HashSet<int>();
        private bool loaded;
        private int settingsVersion = -1;

        public int Count => Rows.Count;
        public HierarchyRow this[int index] => index >= 0 && index < Rows.Count ? Rows[index] : null;

        public int IndexOf(int instanceId)
        {
            for (int i = 0; i < Rows.Count; i++) if (Rows[i].InstanceId == instanceId) return i;
            return -1;
        }

        /// <summary>Rebuilds every row.</summary>
        public void Rebuild(HierarchyFilter filter)
        {
            if (!loaded) LoadExpanded();
            RefreshLinkState();
            Rows.Clear();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                Rows.Add(new HierarchyRow { IsScene = true, Scene = stage.scene, Name = System.IO.Path.GetFileNameWithoutExtension(stage.assetPath), InstanceId = stage.scene.handle, Expanded = true, HasChildren = true });
                Walk(stage.prefabContentsRoot.transform, 1, filter, false);
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                int handle = scene.handle;
                bool open = !Expanded.Contains(-handle); // scenes are open unless folded
                Rows.Add(new HierarchyRow { IsScene = true, Scene = scene, Name = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name, InstanceId = handle, Expanded = open, HasChildren = scene.isLoaded && scene.rootCount > 0, Active = scene == SceneManager.GetActiveScene() });
                if (!open || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) Walk(root.transform, 1, filter, false);
            }
            if (filter.Any) ApplyFilter(filter);
        }

        private void Walk(Transform t, int depth, HierarchyFilter filter, bool excludedByParent)
        {
            var go = t.gameObject;
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) return;
            int id = go.GetInstanceID();
            bool excluded = excludedByParent || excludedIds.Contains(id);
            var row = new HierarchyRow
            {
                Object = go, Scene = go.scene, InstanceId = id, Depth = depth, Name = go.name, Active = go.activeInHierarchy,
                HasChildren = t.childCount > 0, Expanded = Expanded.Contains(id) || filter.Any,
                PrefabInstance = PrefabUtility.IsPartOfPrefabInstance(go), PrefabRoot = PrefabUtility.IsAnyPrefabInstanceRoot(go),
                PrefabMissing = PrefabUtility.IsPrefabAssetMissing(go),
                MissingScript = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0,
                Hidden = SceneVisibilityManager.instance.IsHidden(go), PickingDisabled = SceneVisibilityManager.instance.IsPickingDisabled(go),
                Excluded = excluded, BlenderOwned = ownedIds.Contains(id), MixerEdited = editedIds.Contains(id),
            };
            Rows.Add(row);
            if (!row.Expanded || !row.HasChildren) return;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, filter, excluded);
        }

        // Rows that match stay with their ancestors (dimmed); the rest go.
        private void ApplyFilter(HierarchyFilter filter)
        {
            string text = filter.Text.Trim();
            string typeName = null, componentName = null;
            var words = new List<string>();
            foreach (string token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) typeName = token.Substring(2);
                else if (token.StartsWith("c:", StringComparison.OrdinalIgnoreCase)) componentName = token.Substring(2);
                else words.Add(token);
            }
            var keep = new bool[Rows.Count];
            var matched = new bool[Rows.Count];
            var ancestors = new Stack<int>();
            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                while (ancestors.Count > 0 && Rows[ancestors.Peek()].Depth >= row.Depth) ancestors.Pop();
                bool match = !row.IsScene && Matches(row, words, typeName, componentName, filter);
                matched[i] = match;
                if (match)
                {
                    keep[i] = true;
                    foreach (int a in ancestors) keep[a] = true;
                }
                if (row.IsScene) keep[i] = true;
                ancestors.Push(i);
            }
            var kept = new List<HierarchyRow>();
            for (int i = 0; i < Rows.Count; i++)
            {
                if (!keep[i]) continue;
                Rows[i].Match = matched[i];
                kept.Add(Rows[i]);
            }
            Rows.Clear();
            Rows.AddRange(kept);
        }

        private static bool Matches(HierarchyRow row, List<string> words, string typeName, string componentName, HierarchyFilter filter)
        {
            var go = row.Object;
            foreach (string w in words) if (go.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (typeName != null && !go.GetComponents<Component>().Any(c => c != null && c.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if (componentName != null && !go.GetComponents<Component>().Any(c => c != null && c.GetType().Name.IndexOf(componentName, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            bool anyKind = filter.Meshes || filter.Skinned || filter.Lights || filter.Cameras || filter.Prefabs || filter.Missing || filter.Inactive;
            if (!anyKind) return true;
            if (filter.Meshes && go.GetComponent<MeshRenderer>() != null) return true;
            if (filter.Skinned && go.GetComponent<SkinnedMeshRenderer>() != null) return true;
            if (filter.Lights && go.GetComponent<Light>() != null) return true;
            if (filter.Cameras && go.GetComponent<Camera>() != null) return true;
            if (filter.Prefabs && row.PrefabRoot) return true;
            if (filter.Missing && (row.MissingScript || row.PrefabMissing)) return true;
            if (filter.Inactive && !go.activeInHierarchy) return true;
            return false;
        }

        private void RefreshLinkState()
        {
            excludedIds.Clear(); ownedIds.Clear(); editedIds.Clear();
            var settings = MixerLinkService.SettingsFor(SceneManager.GetActiveScene(), false);
            if (settings != null)
            {
                foreach (string id in settings.excludedIds)
                    if (SceneGraphExporter.Resolve(id) is GameObject go) excludedIds.Add(go.GetInstanceID());
            }
            foreach (string id in MixerLinkService.BlenderOwned)
                if (SceneGraphExporter.Resolve(id) is GameObject go) ownedIds.Add(go.GetInstanceID());
            foreach (var target in MixerSession.Targets) if (target.Alive) editedIds.Add(target.Renderer.gameObject.GetInstanceID());
        }

        // ---- Expanded state -----------------------------------------------------------------------------------------

        private void LoadExpanded()
        {
            loaded = true;
            Expanded.Clear();
            foreach (string part in SessionState.GetString(ExpandedKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int id)) Expanded.Add(id);
            // Unity's own Hierarchy, when one exists, knows what the user had open.
            var unity = UnityHierarchy();
            if (unity != null && GetExpandedIDs != null)
            {
                try
                {
                    if (GetExpandedIDs.Invoke(unity, null) is int[] ids) foreach (int id in ids) Expanded.Add(id);
                }
                catch (Exception) { }
            }
            if (Expanded.Count == 0)
            {
                // A fresh editor: scenes' roots open, like Unity does.
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects()) Expanded.Add(root.GetInstanceID());
                }
            }
        }

        public void SetExpanded(int instanceId, bool expanded, bool recursive = false)
        {
            if (expanded) Expanded.Add(instanceId); else Expanded.Remove(instanceId);
            if (recursive && EditorUtility.InstanceIDToObject(instanceId) is GameObject go)
                foreach (var child in go.GetComponentsInChildren<Transform>(true)) { if (expanded) Expanded.Add(child.gameObject.GetInstanceID()); else Expanded.Remove(child.gameObject.GetInstanceID()); }
            SaveExpanded();
            var unity = UnityHierarchy();
            if (unity != null && SetExpandedMethod != null)
            {
                try { SetExpandedMethod.Invoke(unity, new object[] { instanceId, expanded }); } catch (Exception) { }
            }
        }

        public void SetSceneFolded(Scene scene, bool folded)
        {
            if (folded) Expanded.Add(-scene.handle); else Expanded.Remove(-scene.handle);
            SaveExpanded();
        }

        private void SaveExpanded() => SessionState.SetString(ExpandedKey, string.Join(",", Expanded.Take(4000)));

        private static EditorWindow UnityHierarchy()
        {
            if (HierarchyType == null) return null;
            foreach (var window in Resources.FindObjectsOfTypeAll(HierarchyType))
                if (window is EditorWindow w && w != null && (w.hideFlags & HideFlags.HideAndDontSave) == 0) return w;
            return null;
        }

        /// <summary>Opens every ancestor of an object so it is on screen.</summary>
        public void Reveal(GameObject go)
        {
            if (go == null) return;
            for (var t = go.transform.parent; t != null; t = t.parent) Expanded.Add(t.gameObject.GetInstanceID());
            Expanded.Remove(-go.scene.handle);
            SaveExpanded();
        }
    }
}
