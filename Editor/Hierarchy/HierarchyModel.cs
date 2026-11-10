using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Orbiters.Toolkit.Editor;
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
        /// <summary>Holds a mesh or has one below: shown taller, with a picture of its meshes.</summary>
        public bool HasMeshes;
        /// <summary>A VRChat avatar's root (its avatar descriptor).</summary>
        public bool VrchatAvatar;
        /// <summary>The light on the object, turned from its row.</summary>
        public Light Light;
        /// <summary>An avatar's armature: its row selects the main bones.</summary>
        public QuickBones Bones;
        /// <summary>An avatar tool (My Avatar) takes drops on this avatar root.</summary>
        public IAvatarObjectTool DropTool;
        /// <summary>An avatar tool asks whether this item it added should be refitted.</summary>
        public IAvatarObjectTool RefitTool;
        /// <summary>A newer version waits for this item or for this avatar's custom base (My Avatar, MCB).</summary>
        public AvatarObjectUpdate Update;

        /// <summary>A bone of an avatar (a skinned mesh's or the humanoid's).</summary>
        public bool IsBone;
        /// <summary>An empty object that only groups others (shown as a folder).</summary>
        public bool IsFolder;

        private Component[] components;
        /// <summary>The object's components, read once until they change (rows are bound again on every click).</summary>
        public Component[] Components => components ??= Object != null ? Object.GetComponents<Component>() : Array.Empty<Component>();

        // ---- Kept by the model from one refresh to the next: a row is the same object for its GameObject while it lives,
        // and each group of facts is read again only once a change made it stale (HierarchyModel.Apply). ----

        /// <summary>The row above in the tree, as last listed (null for a scene's root).</summary>
        internal HierarchyRow Parent;
        /// <summary>The children shown (helpers hidden from the hierarchy left out), as last listed.</summary>
        internal List<HierarchyRow> Children;
        internal bool FactsStale = true, ChildrenStale = true, OwnMeshStale = true, MeshesStale = true, ToolsStale = true, ActiveStale = true, VisibilityStale = true;
        /// <summary>A mesh shown on the object itself (a skinned mesh, or a mesh filter with its renderer).</summary>
        internal bool OwnMesh;
        /// <summary>Roots an avatar whose armature is worth finding (<see cref="HierarchyAvatars.IsAvatarRoot"/>).</summary>
        internal bool AvatarRoot;
        /// <summary>
        /// Counts changes at or below the object that may change its picture: meshes, materials, renderers switched, parts
        /// made active or not, objects added, removed or moved. A picture key worked out at one count holds until the next.
        /// </summary>
        internal int PictureRevision;
        internal void ForgetComponents() => components = null;
    }

    /// <summary>What the rows need after changes (<see cref="HierarchyModel.Apply"/>).</summary>
    internal enum HierarchyUpdate
    {
        None,
        /// <summary>The rows stay; what they show changed (bind them again).</summary>
        Rebind,
        /// <summary>List the rows again (<see cref="HierarchyModel.Refresh"/>), then bind them.</summary>
        Rows,
    }

    /// <summary>What the search and the filter chips keep.</summary>
    internal sealed class HierarchyFilter
    {
        /// <summary>The search: words found in the name (with t: and c: for types), or a regular expression over it.</summary>
        public SearchPattern Pattern = SearchPattern.Empty;
        public bool Meshes, Skinned, Lights, Cameras, Prefabs, Missing, Inactive;
        public bool Any => !Pattern.IsEmpty || Meshes || Skinned || Lights || Cameras || Prefabs || Missing || Inactive;
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
        /// <summary>The avatars shown, with their armatures; each forgotten when something under it changes.</summary>
        public readonly HierarchyAvatars Avatars = new HierarchyAvatars();
        private bool loaded;
        private static Scene dontDestroyOnLoad;

        // Every object met so far (shown, searched, or looked through for meshes), by instance ID, with its facts.
        private readonly Dictionary<int, HierarchyRow> nodes = new Dictionary<int, HierarchyRow>();
        // Each scene's roots as last listed, by scene handle; a scene missing here is listed again.
        private readonly Dictionary<int, List<HierarchyRow>> sceneRoots = new Dictionary<int, List<HierarchyRow>>();
        private readonly Dictionary<int, int> indexOf = new Dictionary<int, int>();
        private bool linkStale = true;
        // Counts changes to the facts the search reads (names, components, flags): a narrowed search is valid at one count.
        private int revision;
        private SearchNarrowing lastSearch;

        public int Count => Rows.Count;
        public HierarchyRow this[int index] => index >= 0 && index < Rows.Count ? Rows[index] : null;
        /// <summary>The rows that are objects (scene headers left out).</summary>
        public int ObjectCount { get; private set; }
        /// <summary>Changes whenever the open objects change, for whoever keeps them (the window, across editor starts).</summary>
        public int Version { get; private set; }
        /// <summary>Whether this editor session already holds folds (a script reload); false on an editor start.</summary>
        public static bool HasSessionState => !string.IsNullOrEmpty(SessionState.GetString(ExpandedKey, ""));
        /// <summary>A refresh met objects destroyed without a change event: another refresh is needed to list their places again.</summary>
        public bool NeedsRefresh { get; private set; }
        /// <summary>An object's row was forgotten for good (its object is gone): whoever keeps something per row frees it.</summary>
        public event Action<int> Forgotten;

        static HierarchyModel()
        {
            EditorApplication.playModeStateChanged += _ => dontDestroyOnLoad = default;
        }

        public int IndexOf(int instanceId) => indexOf.TryGetValue(instanceId, out int index) ? index : -1;

        /// <summary>The kept row of an object, or null when the model has not met it.</summary>
        internal HierarchyRow Known(int instanceId) => nodes.TryGetValue(instanceId, out var node) ? node : null;

        /// <summary>Rebuilds every row from scratch: every fact is read again.</summary>
        public void Rebuild(HierarchyFilter filter)
        {
            if (!loaded) LoadExpanded();
            MarkAllStale();
            Refresh(filter);
        }

        /// <summary>
        /// Lists the rows again from the facts kept, reading again only the facts a change made stale (<see cref="Apply"/>).
        /// What is open, the search and the filters may have changed since.
        /// </summary>
        public void Refresh(HierarchyFilter filter)
        {
            if (!loaded) LoadExpanded();
            NeedsRefresh = false;
            if (linkStale) RefreshLinkState();
            Rows.Clear();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                Rows.Add(new HierarchyRow { IsScene = true, Scene = stage.scene, Name = System.IO.Path.GetFileNameWithoutExtension(stage.assetPath), InstanceId = stage.scene.handle, Expanded = true, HasChildren = true });
                var stageRoot = NodeOf(stage.prefabContentsRoot);
                stageRoot.Parent = null;
                Walk(stageRoot, 1, stage.scene, filter, false);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++) AddScene(SceneManager.GetSceneAt(i), filter, false);
                if (EditorApplication.isPlaying)
                {
                    var ddol = DontDestroyOnLoadScene();
                    if (ddol.IsValid() && ddol.isLoaded && ddol.rootCount > 0) AddScene(ddol, filter, true);
                }
            }
            if (filter.Any) ApplyFilter(filter);
            Finish();
        }

        /// <summary>Forgets every object (scenes opened or closed, Play mode, Prefab Mode): instance IDs mean other objects now.</summary>
        public void Reset()
        {
            var gone = new List<int>(nodes.Keys);
            nodes.Clear();
            sceneRoots.Clear();
            Avatars.Clear();
            linkStale = true;
            revision++;
            foreach (int id in gone) Forgotten?.Invoke(id);
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
            foreach (var root in RootsOf(scene)) Walk(root, 1, scene, filter, false);
        }

        private List<HierarchyRow> RootsOf(Scene scene)
        {
            if (sceneRoots.TryGetValue(scene.handle, out var roots)) return roots;
            roots = new List<HierarchyRow>();
            foreach (var go in scene.GetRootGameObjects())
            {
                var node = NodeOf(go);
                node.Parent = null;
                roots.Add(node);
            }
            return sceneRoots[scene.handle] = roots;
        }

        private HierarchyRow NodeOf(GameObject go)
        {
            int id = go.GetInstanceID();
            if (nodes.TryGetValue(id, out var node))
            {
                if (ReferenceEquals(node.Object, go)) return node;
                // Another object under the same ID (an Undo brought it back): nothing known about it holds.
                node.Object = go;
                MarkStale(node);
                return node;
            }
            return nodes[id] = new HierarchyRow { Object = go, InstanceId = id };
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

        // Only what the rows' order and the search need is read here; the rest (meshes below, avatar tools) for the rows
        // kept (Finish).
        private void Walk(HierarchyRow node, int depth, Scene scene, HierarchyFilter filter, bool excludedByParent)
        {
            var go = node.Object;
            if (go == null)
            {
                // Destroyed without a change event (a script, no Undo): its parent is listed again next time.
                if (node.Parent != null) node.Parent.ChildrenStale = true; else sceneRoots.Remove(scene.handle);
                NeedsRefresh = true;
                return;
            }
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) return;
            if (node.FactsStale) RefreshFacts(node);
            if (node.ActiveStale) { node.Active = go.activeInHierarchy; node.ActiveStale = false; }
            if (node.VisibilityStale)
            {
                node.Hidden = SceneVisibilityManager.instance.IsHidden(go);
                node.PickingDisabled = SceneVisibilityManager.instance.IsPickingDisabled(go);
                node.VisibilityStale = false;
            }
            int id = node.InstanceId;
            bool excluded = excludedByParent || excludedIds.Contains(id);
            node.Scene = scene;
            node.Depth = depth;
            node.Excluded = excluded;
            node.BlenderOwned = ownedIds.Contains(id);
            EnsureChildren(node);
            node.Expanded = Expanded.Contains(id) || filter.Any;
            node.Match = true;
            Rows.Add(node);
            if (!node.Expanded || !node.HasChildren) return;
            var children = node.Children;
            for (int i = 0; i < children.Count; i++) Walk(children[i], depth + 1, scene, filter, excluded);
        }

        // The facts of one object that only a change of the object itself changes.
        private void RefreshFacts(HierarchyRow node)
        {
            var go = node.Object;
            node.Name = go.name;
            node.PrefabInstance = PrefabUtility.IsPartOfPrefabInstance(go);
            node.PrefabRoot = PrefabUtility.IsAnyPrefabInstanceRoot(go);
            node.PrefabMissing = PrefabUtility.IsPrefabAssetMissing(go);
            node.MissingScript = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0;
            node.VrchatAvatar = HierarchyAvatars.IsVrchatAvatar(go);
            node.Light = go.GetComponent<Light>();
            node.AvatarRoot = HierarchyAvatars.IsAvatarRoot(go);
            node.ForgetComponents();
            node.FactsStale = false;
            node.ToolsStale = true;
            node.ActiveStale = true;
            node.VisibilityStale = true;
            revision++;
        }

        private void EnsureChildren(HierarchyRow node)
        {
            if (!node.ChildrenStale && node.Children != null) return;
            var t = node.Object.transform;
            var children = node.Children ??= new List<HierarchyRow>();
            children.Clear();
            node.ChildrenStale = false;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i).gameObject;
                // Children hidden from the hierarchy (the gizmos' helpers under bones) give no fold arrow.
                if ((child.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
                var row = NodeOf(child);
                row.Parent = node;
                children.Add(row);
            }
            node.HasChildren = children.Count > 0;
        }

        // A mesh shown on the object or anywhere below it (helpers hidden from the hierarchy aside), kept until a change
        // at or below the object.
        private bool MeshesOf(HierarchyRow node)
        {
            if (!node.MeshesStale) return node.HasMeshes;
            bool has = false;
            var go = node.Object;
            if (go != null)
            {
                if (node.OwnMeshStale)
                {
                    var t = go.transform;
                    node.OwnMesh = t.TryGetComponent<SkinnedMeshRenderer>(out var skinned) && skinned.sharedMesh != null
                                   || t.TryGetComponent<MeshFilter>(out var meshFilter) && meshFilter.sharedMesh != null && t.TryGetComponent<MeshRenderer>(out _);
                    node.OwnMeshStale = false;
                }
                has = node.OwnMesh;
                if (!has)
                {
                    EnsureChildren(node);
                    foreach (var child in node.Children)
                        if (MeshesOf(child)) { has = true; break; }
                }
            }
            if (has != node.HasMeshes) node.ToolsStale = true;
            node.HasMeshes = has;
            node.MeshesStale = false;
            return has;
        }

        // The rows kept: their meshes below, avatars and bones, and what the avatar tools say of them.
        private void Finish()
        {
            indexOf.Clear();
            int objects = 0;
            // Every avatar shown is scanned first, so a row knows it is a bone whatever comes first.
            foreach (var row in Rows)
                if (!row.IsScene && row.AvatarRoot) Avatars.Scan(row.Object);
            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if (row.IsScene) continue;
                objects++;
                indexOf[row.InstanceId] = i;
                var go = row.Object;
                bool meshes = MeshesOf(row);
                row.Bones = Avatars.BonesOf(go);
                row.IsBone = row.Bones == null && Avatars.IsBone(go);
                row.IsFolder = row.HasChildren && row.Bones == null && !row.IsBone && row.Components.Length == 1 && !row.PrefabRoot;
                if (!row.ToolsStale) continue;
                row.ToolsStale = false;
                row.DropTool = row.VrchatAvatar ? AvatarObjectTools.DropTarget(go) : null;
                row.RefitTool = meshes ? AvatarObjectTools.RefitQuestion(go) : null;
                row.Update = row.VrchatAvatar || meshes ? AvatarObjectTools.UpdateOf(go) : null;
            }
            ObjectCount = objects;
        }

        // ---- Search -------------------------------------------------------------------------------------------------

        // What the last plain-word search matched: a longer search of the same kind (each earlier word still inside one of
        // the new ones) can only match among those, while no fact the search reads changed since.
        private sealed class SearchNarrowing
        {
            public string Kind;
            public List<string> Words;
            public int Revision;
            public HashSet<int> Matched;
        }

        // Rows that match stay with their ancestors (dimmed); the rest go. Names and components come from the kept facts:
        // a keystroke reads nothing from the scene.
        private void ApplyFilter(HierarchyFilter filter)
        {
            var pattern = filter.Pattern ?? SearchPattern.Empty;
            string typeName = null, componentName = null;
            var words = new List<string>();
            // Find References In Scene's search ("ref:" and an asset's path or an object's ID): Unity finds what refers to it.
            var referring = !pattern.IsRegex && UnityWindowHooks.IsReferenceSearch(pattern.Text) ? Referring(pattern.Text.Trim()) : null;
            // A regular expression is matched against names as it is; plain words may name types with t: and c:.
            foreach (string token in pattern.IsRegex || pattern.IsEmpty || referring != null ? Array.Empty<string>() : pattern.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) typeName = token.Substring(2);
                else if (token.StartsWith("c:", StringComparison.OrdinalIgnoreCase)) componentName = token.Substring(2);
                else words.Add(token);
            }
            // Narrowing only for plain words (an expression or a reference search is matched in full each time).
            string kind = pattern.IsRegex || referring != null ? null
                : pattern.Comparison + "|" + typeName + "|" + componentName + "|" + filter.Meshes + filter.Skinned + filter.Lights + filter.Cameras + filter.Prefabs + filter.Missing + filter.Inactive;
            HashSet<int> candidates = null;
            if (kind != null && lastSearch != null && lastSearch.Kind == kind && lastSearch.Revision == revision && Refines(lastSearch.Words, words, pattern.Comparison))
                candidates = lastSearch.Matched;
            var matchedIds = kind != null ? new HashSet<int>() : null;
            var keep = new bool[Rows.Count];
            var matched = new bool[Rows.Count];
            var ancestors = new Stack<int>();
            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                while (ancestors.Count > 0 && Rows[ancestors.Peek()].Depth >= row.Depth) ancestors.Pop();
                bool match = !row.IsScene && (candidates == null || candidates.Contains(row.InstanceId))
                             && Matches(row, pattern, words, typeName, componentName, referring, filter);
                matched[i] = match;
                if (match)
                {
                    matchedIds?.Add(row.InstanceId);
                    keep[i] = true;
                    foreach (int a in ancestors) keep[a] = true;
                }
                if (row.IsScene) keep[i] = true;
                ancestors.Push(i);
            }
            lastSearch = kind != null ? new SearchNarrowing { Kind = kind, Words = words, Revision = revision, Matched = matchedIds } : null;
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

        // Whether every name matching all of `now` also matches all of `before`: each earlier word is inside a new one.
        private static bool Refines(List<string> before, List<string> now, StringComparison comparison)
        {
            foreach (string old in before)
            {
                bool inside = false;
                foreach (string word in now) if (word.IndexOf(old, comparison) >= 0) { inside = true; break; }
                if (!inside) return false;
            }
            return true;
        }

        private static bool Matches(HierarchyRow row, SearchPattern pattern, List<string> words, string typeName, string componentName, HashSet<int> referring, HierarchyFilter filter)
        {
            string name = row.Name ?? string.Empty;
            if (referring != null) { if (!referring.Contains(row.InstanceId)) return false; }
            else if (pattern.IsRegex) { if (!pattern.Matches(name)) return false; }
            else foreach (string w in words) if (name.IndexOf(w, pattern.Comparison) < 0) return false;
            if (typeName != null && !HasComponentNamed(row, typeName)) return false;
            if (componentName != null && !HasComponentNamed(row, componentName)) return false;
            bool anyKind = filter.Meshes || filter.Skinned || filter.Lights || filter.Cameras || filter.Prefabs || filter.Missing || filter.Inactive;
            if (!anyKind) return true;
            if (filter.Meshes && Has<MeshRenderer>(row)) return true;
            if (filter.Skinned && Has<SkinnedMeshRenderer>(row)) return true;
            if (filter.Lights && Has<Light>(row)) return true;
            if (filter.Cameras && Has<Camera>(row)) return true;
            if (filter.Prefabs && row.PrefabRoot) return true;
            if (filter.Missing && (row.MissingScript || row.PrefabMissing)) return true;
            if (filter.Inactive && !row.Active) return true;
            return false;
        }

        private static bool HasComponentNamed(HierarchyRow row, string name)
        {
            foreach (var c in row.Components)
                if (c != null && c.GetType().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool Has<T>(HierarchyRow row) where T : Component
        {
            foreach (var c in row.Components) if (c is T && c != null) return true;
            return false;
        }

        // The objects Unity's own search finds (its Hierarchy's): in the open prefab's scene in Prefab Mode, else in the
        // loaded scenes.
        private static HashSet<int> Referring(string query)
        {
            var found = new HashSet<int>();
            var property = new HierarchyProperty(UnityEditor.HierarchyType.GameObjects, false);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) property.SetCustomScenes(new[] { stage.scene.handle });
            property.SetSearchFilter(query, (int)SearchableEditorWindow.SearchMode.All);
            while (property.Next(null)) found.Add(property.instanceID);
            return found;
        }

        // Each scene keeps its own exclusions (where the Blender link and MixerLink.IsKeptOutOfBlender read them). Resolved
        // again only when the link or the scenes changed (LinkChanged, Reset, MarkAllStale).
        private void RefreshLinkState()
        {
            linkStale = false;
            excludedIds.Clear(); ownedIds.Clear();
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
        }

        // ---- Changes ------------------------------------------------------------------------------------------------

        [Flags]
        private enum Mark
        {
            None = 0,
            Facts = 1 << 0,
            Meshes = 1 << 1,
            Tools = 1 << 2,
            Picture = 1 << 3,
            Avatar = 1 << 4,
            Active = 1 << 5,
            Below = Meshes | Tools | Picture | Avatar,
        }

        /// <summary>
        /// Makes stale what the editor's changes may have changed, and says what the rows need: nothing, binding again
        /// (a light turned), or listing again (<see cref="Refresh"/>). Cheap: facts are read again by the next refresh, and
        /// only for the rows it lists.
        /// </summary>
        public HierarchyUpdate Apply(EditorChanges changes)
        {
            if (changes == null) return HierarchyUpdate.None;
            // An Undo or Redo may have changed anything it recorded; a scene changed as a whole says nothing more.
            if (changes.Everything || changes.UndoRedo) { MarkAllStale(); return HierarchyUpdate.Rows; }
            var update = HierarchyUpdate.None;
            foreach (int id in changes.Destroyed)
            {
                if (!nodes.TryGetValue(id, out var node)) continue;
                MarkCachedUp(node.Parent, Mark.Below | Mark.Facts);
                Forget(node);
                update = HierarchyUpdate.Rows;
            }
            foreach (int id in changes.Rewritten)
            {
                if (nodes.TryGetValue(id, out var node)) MarkSubtree(node);
                MarkUp(changes.TransformOf(id), Mark.Below | Mark.Facts);
                update = HierarchyUpdate.Rows;
            }
            if (changes.Subtrees.Count > 0)
            {
                // Created, moved, reordered or removed objects: the scenes' roots are listed again too.
                sceneRoots.Clear();
                update = HierarchyUpdate.Rows;
                revision++;
            }
            foreach (int id in changes.Subtrees)
            {
                if (nodes.TryGetValue(id, out var node))
                {
                    node.ChildrenStale = true;
                    node.FactsStale = true;
                    // A move may change what is active below, and the old place's meshes.
                    MarkCachedDown(node, Mark.Active | Mark.Picture);
                    MarkCachedUp(node.Parent, Mark.Below | Mark.Facts);
                }
                MarkUp(changes.TransformOf(id), Mark.Below | Mark.Facts);
            }
            foreach (int id in changes.Structure)
            {
                if (nodes.TryGetValue(id, out var node)) { node.FactsStale = true; node.OwnMeshStale = true; }
                // A model's root counts as an avatar only with skinned meshes below it: the ancestors' facts too.
                MarkUp(changes.TransformOf(id), Mark.Below | Mark.Facts);
                update = HierarchyUpdate.Rows;
                revision++;
            }
            foreach (int id in changes.Properties)
            {
                var changed = changes.Find(id);
                if (changed is GameObject go)
                {
                    if (go == null) continue;
                    // Its name, whether it is active (and so its children), its flags.
                    if (nodes.TryGetValue(id, out var node))
                    {
                        node.FactsStale = true;
                        MarkCachedDown(node, Mark.Active | Mark.Picture);
                    }
                    MarkUp(go.transform, Mark.Picture);
                    update = HierarchyUpdate.Rows;
                    revision++;
                }
                else if (changed is Component component && component != null)
                {
                    var t = component.transform;
                    nodes.TryGetValue(t.gameObject.GetInstanceID(), out var node);
                    if (component is Transform)
                    {
                        // Moving, turning, posing: only a light's ball (and its detail) shows it.
                        if (node != null && node.Light != null && update == HierarchyUpdate.None) update = HierarchyUpdate.Rebind;
                        continue;
                    }
                    if (component is Renderer || component is MeshFilter)
                    {
                        if (node != null) node.OwnMeshStale = true;
                        MarkUp(t, component is SkinnedMeshRenderer ? Mark.Below : Mark.Meshes | Mark.Tools | Mark.Picture);
                    }
                    else if (component is Animator)
                    {
                        if (node != null) node.FactsStale = true;
                        MarkUp(t, Mark.Avatar | Mark.Tools);
                    }
                    else if (node != null) node.FactsStale = true;
                    update = HierarchyUpdate.Rows;
                }
            }
            if (changes.Hierarchy)
            {
                // hierarchyChanged also follows changes made without Undo, which raise no object change: the rows shown are
                // listed again, with their names and whether they are active; with no object change at all, every fact of
                // the rows shown is read again.
                ResyncRows(explained: changes.StructureChanged || changes.Properties.Count > 0);
                update = HierarchyUpdate.Rows;
            }
            return update;
        }

        /// <summary>
        /// An edit made from the Mixer Hierarchy itself (its change events come a frame later): every fact of the rows shown
        /// is read again at the next refresh, as the rows were rebuilt before.
        /// </summary>
        public void ResyncShown() => ResyncRows(explained: false);

        /// <summary>The scene views' visibility or picking switches changed (they raise no object change).</summary>
        public void VisibilityChanged()
        {
            foreach (var node in nodes.Values) node.VisibilityStale = true;
        }

        /// <summary>An avatar tool's answers may have changed (My Avatar, MCB, a custom base applied).</summary>
        public void ToolsChanged()
        {
            foreach (var node in nodes.Values) node.ToolsStale = true;
        }

        /// <summary>The Blender link's exclusions or the objects Blender edits changed.</summary>
        public void LinkChanged() => linkStale = true;

        /// <summary>A renderer's mesh was swapped by a tool without telling the editor (Mixer's working copy).</summary>
        public void MeshSwapped(Transform renderer)
        {
            if (renderer == null) return;
            if (nodes.TryGetValue(renderer.gameObject.GetInstanceID(), out var node)) node.OwnMeshStale = true;
            MarkUp(renderer, Mark.Below);
        }

        /// <summary>Every kept fact is read again at the next refresh (the rows stay the same objects).</summary>
        public void MarkAllStale()
        {
            List<HierarchyRow> dead = null;
            foreach (var node in nodes.Values)
            {
                if (node.Object == null) { (dead ??= new List<HierarchyRow>()).Add(node); continue; }
                MarkStale(node);
            }
            if (dead != null) foreach (var node in dead) Forget(node);
            sceneRoots.Clear();
            Avatars.Clear();
            linkStale = true;
            revision++;
        }

        private static void MarkStale(HierarchyRow node)
        {
            node.FactsStale = node.ChildrenStale = node.OwnMeshStale = node.MeshesStale = node.ToolsStale = node.ActiveStale = node.VisibilityStale = true;
            node.PictureRevision++;
        }

        private void ResyncRows(bool explained)
        {
            sceneRoots.Clear();
            revision++;
            if (!explained) Avatars.Clear();
            List<HierarchyRow> dead = null;
            foreach (var row in Rows)
            {
                if (row.IsScene) continue;
                var go = row.Object;
                if (go == null) { (dead ??= new List<HierarchyRow>()).Add(row); continue; }
                row.ChildrenStale = true;
                row.MeshesStale = true;
                row.ActiveStale = true;
                if (!explained) { MarkStale(row); continue; }
                string name = go.name;
                if (name != row.Name) row.Name = name;
            }
            if (dead != null) foreach (var node in dead) { MarkCachedUp(node.Parent, Mark.Below | Mark.Facts); Forget(node); }
        }

        private void Stale(HierarchyRow node, Mark mark)
        {
            if ((mark & Mark.Facts) != 0) node.FactsStale = true;
            if ((mark & Mark.Meshes) != 0) node.MeshesStale = true;
            if ((mark & Mark.Tools) != 0) node.ToolsStale = true;
            if ((mark & Mark.Picture) != 0) node.PictureRevision++;
            if ((mark & Mark.Active) != 0) node.ActiveStale = true;
            if ((mark & Mark.Avatar) != 0) Avatars.Forget(node.InstanceId);
        }

        // The object and every ancestor it has now (avatars forgotten whether or not their rows were met).
        private void MarkUp(Transform t, Mark mark)
        {
            for (; t != null; t = t.parent)
            {
                int id = t.gameObject.GetInstanceID();
                if (nodes.TryGetValue(id, out var node)) Stale(node, mark);
                else if ((mark & Mark.Avatar) != 0) Avatars.Forget(id);
            }
        }

        // The ancestors as last listed (where a moved or destroyed object was).
        private void MarkCachedUp(HierarchyRow node, Mark mark)
        {
            for (int guard = 0; node != null && guard < 10000; node = node.Parent, guard++) Stale(node, mark);
        }

        // The object and every row met below it.
        private void MarkCachedDown(HierarchyRow node, Mark mark)
        {
            Stale(node, mark);
            if (node.Children == null) return;
            foreach (var child in node.Children) if (child.Parent == node) MarkCachedDown(child, mark);
        }

        private void MarkSubtree(HierarchyRow node)
        {
            MarkStale(node);
            Avatars.Forget(node.InstanceId);
            if (node.Children == null) return;
            foreach (var child in node.Children) if (child.Parent == node) MarkSubtree(child);
        }

        // A destroyed object and the rows below it that are gone with it (one moved away earlier stays).
        private void Forget(HierarchyRow node)
        {
            if (nodes.TryGetValue(node.InstanceId, out var known) && ReferenceEquals(known, node)) nodes.Remove(node.InstanceId);
            Avatars.Forget(node.InstanceId);
            revision++;
            Forgotten?.Invoke(node.InstanceId);
            if (node.Children == null) return;
            foreach (var child in node.Children)
                if (child.Parent == node && child.Object == null) Forget(child);
        }

        // ---- Checking the kept facts --------------------------------------------------------------------------------

        /// <summary>
        /// Builds the rows from scratch in a model of its own (the same folds) and lists where they differ from these: the
        /// net under the incremental updates, for tests and for the "Orbiters.Mixer.Hierarchy.Verify" preference.
        /// Empty when they agree.
        /// </summary>
        internal List<string> Verify(HierarchyFilter filter, int limit = 20)
        {
            var fresh = new HierarchyModel { loaded = true };
            fresh.Expanded.UnionWith(Expanded);
            fresh.FoldedScenes.UnionWith(FoldedScenes);
            fresh.Refresh(filter);
            var differences = new List<string>();
            if (fresh.Rows.Count != Rows.Count) differences.Add("rows: " + Rows.Count + " kept, " + fresh.Rows.Count + " from scratch");
            for (int i = 0; i < Math.Min(Rows.Count, fresh.Rows.Count) && differences.Count < limit; i++)
            {
                string what = Differs(Rows[i], fresh.Rows[i]);
                if (what != null) differences.Add("row " + i + " (" + fresh.Rows[i].Name + "): " + what);
            }
            return differences;
        }

        private static string Differs(HierarchyRow a, HierarchyRow b)
        {
            if (a.IsScene != b.IsScene || a.InstanceId != b.InstanceId) return "another object (" + a.Name + ")";
            var parts = new List<string>();
            void Check(string name, bool same) { if (!same) parts.Add(name); }
            Check("depth", a.Depth == b.Depth);
            Check("children", a.HasChildren == b.HasChildren);
            Check("expanded", a.Expanded == b.Expanded);
            Check("name", a.Name == b.Name);
            Check("active", a.Active == b.Active);
            Check("prefab", a.PrefabInstance == b.PrefabInstance && a.PrefabRoot == b.PrefabRoot && a.PrefabMissing == b.PrefabMissing);
            Check("missing script", a.MissingScript == b.MissingScript);
            Check("visibility", a.Hidden == b.Hidden && a.PickingDisabled == b.PickingDisabled);
            Check("match", a.Match == b.Match);
            Check("Blender", a.Excluded == b.Excluded && a.BlenderOwned == b.BlenderOwned);
            Check("meshes", a.HasMeshes == b.HasMeshes);
            Check("avatar", a.VrchatAvatar == b.VrchatAvatar);
            Check("light", a.Light == b.Light);
            Check("bones", (a.Bones == null) == (b.Bones == null) && a.IsBone == b.IsBone);
            Check("folder", a.IsFolder == b.IsFolder);
            Check("tools", (a.DropTool == null) == (b.DropTool == null) && (a.RefitTool == null) == (b.RefitTool == null) && (a.Update == null) == (b.Update == null));
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        // ---- Expanded state -----------------------------------------------------------------------------------------

        private void LoadExpanded()
        {
            loaded = true;
            Expanded.Clear();
            bool saved = HasSessionState;
            foreach (string part in SessionState.GetString(ExpandedKey, "").Split(new[] { ',', '#' }, StringSplitOptions.RemoveEmptyEntries))
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
            if (!saved && Expanded.Count == 0)
            {
                // A fresh editor: scenes' roots open, like Unity does (not after "Collapse all": that saved no folds).
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects()) Expanded.Add(root.GetInstanceID());
                }
            }
        }

        /// <summary>Opens objects kept from an earlier editor session (instance IDs of today's objects), before the first
        /// rebuild: they replace the scenes' roots a fresh editor opens (roots the user had folded stay folded).</summary>
        public void Restore(IEnumerable<int> ids)
        {
            if (!loaded) { LoadExpanded(); Expanded.Clear(); }
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
            // A search narrowed among what it walked must walk this scene's objects again.
            revision++;
        }

        // "#" first: folds saved with nothing open still say this session has folds (HasSessionState).
        private void SaveExpanded()
        {
            SessionState.SetString(ExpandedKey, "#" + string.Join(",", Expanded.Take(4000)));
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
