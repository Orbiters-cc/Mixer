using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

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
        /// <summary>The scene header of Play mode's DontDestroyOnLoad objects (no scene menu applies to it).</summary>
        public bool IsDontDestroyOnLoad;
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

        private Component[] components;
        /// <summary>The object's components, read once per rebuild (rows are bound again on every click).</summary>
        public Component[] Components => components ??= Object != null ? Object.GetComponents<Component>() : Array.Empty<Component>();
    }

    /// <summary>What the search and the filter chips keep.</summary>
    internal sealed class HierarchyFilter
    {
        public string Text = string.Empty;
        public bool Meshes, Skinned, Lights, Cameras, Prefabs, Missing, Inactive;
        public bool Any => !string.IsNullOrEmpty(Text) || Meshes || Skinned || Lights || Cameras || Prefabs || Missing || Inactive;
    }

    /// <summary>
    /// The rows of the hierarchy from the loaded scenes (or the open prefab stage), and in Play mode the
    /// DontDestroyOnLoad objects, with Unity's own expanded state when a Unity Hierarchy exists (read and written
    /// through its internals) and Mixer's own otherwise. Objects' and scenes' folds are kept apart.
    /// </summary>
    internal sealed class HierarchyModel
    {
        private static readonly Type HierarchyType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        private static readonly MethodInfo GetExpandedIDs = HierarchyType?.GetMethod("GetExpandedIDs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo SetExpandedMethod = HierarchyType?.GetMethod("SetExpanded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
        private const string ExpandedKey = "Orbiters.Mixer.Hierarchy.Expanded";
        private const string FoldedScenesKey = "Orbiters.Mixer.Hierarchy.FoldedScenes";

        public readonly List<HierarchyRow> Rows = new List<HierarchyRow>();
        /// <summary>Open objects, by instance ID.</summary>
        public readonly HashSet<int> Expanded = new HashSet<int>();
        /// <summary>Folded scenes, by scene handle (scenes are open unless folded).</summary>
        public readonly HashSet<int> FoldedScenes = new HashSet<int>();
        private readonly HashSet<int> excludedIds = new HashSet<int>();
        private readonly HashSet<int> ownedIds = new HashSet<int>();
        private readonly HashSet<int> editedIds = new HashSet<int>();
        private bool loaded;
        private static Scene dontDestroyOnLoad;

        public int Count => Rows.Count;
        public HierarchyRow this[int index] => index >= 0 && index < Rows.Count ? Rows[index] : null;
        /// <summary>Changes whenever the open objects change, for whoever keeps them (the window, across editor starts).</summary>
        public int Version { get; private set; }
        /// <summary>Whether this editor session already holds folds (a script reload); false on an editor start.</summary>
        public static bool HasSessionState => !string.IsNullOrEmpty(SessionState.GetString(ExpandedKey, ""));

        static HierarchyModel()
        {
            EditorApplication.playModeStateChanged += _ => dontDestroyOnLoad = default;
        }

        public int IndexOf(int instanceId)
        {
            for (int i = 0; i < Rows.Count; i++) if (Rows[i].InstanceId == instanceId && !Rows[i].IsScene) return i;
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
                if (filter.Any) ApplyFilter(filter);
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++) AddScene(SceneManager.GetSceneAt(i), filter, false);
            if (EditorApplication.isPlaying)
            {
                var ddol = DontDestroyOnLoadScene();
                if (ddol.IsValid() && ddol.isLoaded && ddol.rootCount > 0) AddScene(ddol, filter, true);
            }
            if (filter.Any) ApplyFilter(filter);
        }

        private void AddScene(Scene scene, HierarchyFilter filter, bool dontDestroy)
        {
            int handle = scene.handle;
            bool open = !FoldedScenes.Contains(handle);
            Rows.Add(new HierarchyRow
            {
                IsScene = true, IsDontDestroyOnLoad = dontDestroy, Scene = scene, InstanceId = handle, Expanded = open,
                Name = dontDestroy ? "DontDestroyOnLoad" : string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name,
                HasChildren = scene.isLoaded && scene.rootCount > 0, Active = !dontDestroy && scene == SceneManager.GetActiveScene(),
            });
            if (!open || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects()) Walk(root.transform, 1, filter, false);
        }

        // Play mode keeps DontDestroyOnLoad objects (Av3Emulator, Gesture Manager, ClientSim) in a scene of their own that
        // no scene list names: a throwaway object marked DontDestroyOnLoad tells which one it is, once per Play session.
        private static Scene DontDestroyOnLoadScene()
        {
            if (dontDestroyOnLoad.IsValid() || !EditorApplication.isPlaying) return dontDestroyOnLoad;
            GameObject probe = null;
            try
            {
                probe = new GameObject("Mixer Hierarchy probe") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(probe);
                dontDestroyOnLoad = probe.scene;
            }
            catch (Exception) { dontDestroyOnLoad = default; }
            finally
            {
                if (probe != null) Object.DestroyImmediate(probe);
            }
            return dontDestroyOnLoad;
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
                HasChildren = HasShownChildren(t), Expanded = Expanded.Contains(id) || filter.Any,
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

        // Children hidden from the hierarchy (the gizmos' helpers under bones) give no fold arrow.
        private static bool HasShownChildren(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
                if ((t.GetChild(i).gameObject.hideFlags & HideFlags.HideInHierarchy) == 0) return true;
            return false;
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
            if (typeName != null && !row.Components.Any(c => c != null && c.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if (componentName != null && !row.Components.Any(c => c != null && c.GetType().Name.IndexOf(componentName, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
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

        // Each scene keeps its own exclusions (where the Blender link and MixerLink.IsKeptOutOfBlender read them).
        private void RefreshLinkState()
        {
            excludedIds.Clear(); ownedIds.Clear(); editedIds.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var settings = MixerLinkService.SettingsFor(scene, false);
                if (settings == null) continue;
                foreach (string id in settings.excludedIds)
                    if (SceneGraphExporter.Resolve(id) is GameObject go && go.scene == scene) excludedIds.Add(go.GetInstanceID());
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
            foreach (string part in SessionState.GetString(FoldedScenesKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int handle)) FoldedScenes.Add(handle);
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

        /// <summary>Opens objects kept from an earlier editor session (instance IDs of today's objects), before the first rebuild.</summary>
        public void Restore(IEnumerable<int> ids)
        {
            if (!loaded) LoadExpanded();
            bool any = false;
            foreach (int id in ids) if (id != 0 && Expanded.Add(id)) any = true;
            if (any) SaveExpanded();
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

        /// <summary>Folds everything: the objects, while the scenes stay open with their roots shown.</summary>
        public void CollapseAll()
        {
            Expanded.Clear();
            SaveExpanded();
        }

        public void SetSceneFolded(Scene scene, bool folded)
        {
            if (folded) FoldedScenes.Add(scene.handle); else FoldedScenes.Remove(scene.handle);
            SessionState.SetString(FoldedScenesKey, string.Join(",", FoldedScenes));
            Version++;
        }

        private void SaveExpanded()
        {
            SessionState.SetString(ExpandedKey, string.Join(",", Expanded.Take(4000)));
            Version++;
        }

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
            FoldedScenes.Remove(go.scene.handle);
            SessionState.SetString(FoldedScenesKey, string.Join(",", FoldedScenes));
            SaveExpanded();
        }
    }
}
