using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The Mixer Hierarchy: Unity's Hierarchy rebuilt on the Toolkit's virtual list, with search and kind filters,
    /// the selection in both directions, rename, drag to reparent and reorder (and into other windows' fields), prefab
    /// drops, visibility and picking switches, prefab and missing-script badges, Mixer's edits and the Blender link
    /// state per object (sent, excluded, edited in Blender), and a context menu that also sends objects to Blender or
    /// keeps them out, and opens Unity's GameObject menu.
    /// </summary>
    internal sealed partial class MixerHierarchyWindow : EditorWindow, IHasCustomMenu
    {
        private const string StyleSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        private const string HierarchySheetPath = MixerInfo.BasePath + "/Editor/UI/hierarchy.uss";
        private const string CompactPref = "Orbiters.Mixer.Hierarchy.Compact";
        private const string ComponentsPref = "Orbiters.Mixer.Hierarchy.ShowComponents";
        private const string DotsPref = "Orbiters.Mixer.Hierarchy.ShowBlenderDots";
        private const float RowHeight = 24f, CompactRowHeight = 20f;
        private const int PersistedLimit = 2000;

        [SerializeField] private string search = string.Empty;
        [SerializeField] private bool filterMeshes, filterSkinned, filterLights, filterCameras, filterPrefabs, filterMissing, filterInactive;
        // The open objects as GlobalObjectIds: instance IDs change from one editor start to the next, these do not.
        [SerializeField] private List<string> persistedExpanded = new List<string>();

        private readonly HierarchyModel model = new HierarchyModel();
        private readonly HierarchyFilter filter = new HierarchyFilter();
        private VisualElement root, dragLine;
        private Button filterButton;
        private Label filterCount;
        private VirtualList list;
        private TextField searchField;
        private MixerSearch searchOptions;
        private Label footer;
        private MixerToasts toasts;
        // Not kept across a script reload (Unity keeps a window's private fields): the rows must be built again first.
        [NonSerialized] private bool dirty = true, visible = true, built;
        private int anchorId;
        // The row the keys step from: the one last clicked or stepped to (a range keeps the anchor as Unity's active object).
        private int cursorId;
        private int renamingId;
        // A selection this window made: its change needs no reveal (the row is on screen) nor a rebuild.
        [NonSerialized] private bool selectingHere;
        // What Cut took (instance ids, kept across script reloads): the next Paste moves those objects, so every reference
        // to them (bones, PhysBones, constraints, other tools' fields) still holds. Shown dimmed until then.
        private const string CutKey = "Orbiters.Mixer.Hierarchy.Cut";
        private double lastRebuild, lastPersist;
        private int persistedVersion = -1;

        private static bool Compact => EditorPrefs.GetBool(CompactPref, false);
        private static bool ShowComponents => EditorPrefs.GetBool(ComponentsPref, true);
        private static bool ShowDots => EditorPrefs.GetBool(DotsPref, false);

        [MenuItem(MixerInfo.HierarchyMenuPath, priority = 2001)]
        public static void Open()
        {
            var window = GetWindow<MixerHierarchyWindow>();
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Hierarchy", MixerLogo.TabIcon(), "Mixer Hierarchy");
            minSize = new Vector2(240f, 160f);
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Selection.selectionChanged += OnSelectionChanged;
            AvatarObjectTools.Changed += MarkDirty;
            EditorSceneManager.sceneDirtied += OnSceneChanged;
            EditorSceneManager.sceneSaved += OnSceneChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneChanged;
            PrefabStage.prefabStageOpened += OnPrefabStage;
            PrefabStage.prefabStageClosing += OnPrefabStage;
            MixerLinkService.Changed += MarkDirty;
            MixerSession.Changed += MarkDirty;
            HierarchyVrchat.Changed += MarkDirty;
            BoneSubSelection.Changed += OnSubSelectionChanged;
            Undo.undoRedoPerformed += MarkDirty;
            HookUnity();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            AvatarObjectTools.Changed -= MarkDirty;
            EditorSceneManager.sceneDirtied -= OnSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneChanged;
            PrefabStage.prefabStageOpened -= OnPrefabStage;
            PrefabStage.prefabStageClosing -= OnPrefabStage;
            MixerLinkService.Changed -= MarkDirty;
            MixerSession.Changed -= MarkDirty;
            HierarchyVrchat.Changed -= MarkDirty;
            BoneSubSelection.Changed -= OnSubSelectionChanged;
            Undo.undoRedoPerformed -= MarkDirty;
            UnhookUnity();
            PersistExpanded(force: true);
        }

        private void OnBecameVisible() { visible = true; MarkDirty(); }
        private void OnBecameInvisible() => visible = false;
        private void MarkDirty() => dirty = true;
        private void OnHierarchyChanged() { model.Avatars.Clear(); MarkDirty(); }
        private void OnSceneChanged(Scene _) => MarkDirty();
        private void OnSceneOpened(Scene _, OpenSceneMode __) => MarkDirty();
        private void OnPrefabStage(PrefabStage _) => MarkDirty();
        private void OnPlayModeChanged(PlayModeStateChange _) => MarkDirty();

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open Unity's Hierarchy"), false, HierarchyReplacement.OpenUnityHierarchy);
            menu.AddItem(new GUIContent("Replace Unity's Hierarchy"), HierarchyReplacement.Enabled, () => HierarchyReplacement.SetEnabled(!HierarchyReplacement.Enabled));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Compact rows"), Compact, () => { EditorPrefs.SetBool(CompactPref, !Compact); ApplyDensity(); });
            menu.AddItem(new GUIContent("Component icons"), ShowComponents, () => { EditorPrefs.SetBool(ComponentsPref, !ShowComponents); list.RefreshRows(); });
            menu.AddItem(new GUIContent("Blender dots"), ShowDots, () => { EditorPrefs.SetBool(DotsPref, !ShowDots); list.RefreshRows(); });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Expand all"), false, () => { foreach (var row in model.Rows.ToList()) if (row.Object != null && row.HasChildren) model.SetExpanded(row.InstanceId, true, true); RebuildNow(); });
            menu.AddItem(new GUIContent("Collapse all"), false, () => { model.CollapseAll(); RebuildNow(); });
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            root.AddToClassList("mxh-root");
            root.EnableInClassList("mxh-root--compact", Compact);
            foreach (string path in new[] { StyleSheetPath, HierarchySheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }
            root.focusable = true;
            RestoreExpanded();
            // Kept again only once the folds change: the model has not read this session's folds yet.
            persistedVersion = model.Version;
            BuildTopBar();
            list = new VirtualList(Compact ? CompactRowHeight : RowHeight, MakeRow, BindRow);
            // Rows with a picture, a light or the armature's bones take two lines, a VRChat avatar three (one each in compact rows).
            list.SpanOf = index => Lines(model[index]);
            list.RegisterCallback<GeometryChangedEvent>(_ => OnWidthChanged());
            list.AddToClassList("mxh-list");
            root.Add(list);
            dragLine = MixerUi.Box("mxh-dragline", PickingMode.Ignore);
            MixerUi.Show(dragLine, false);
            list.Viewport.Add(dragLine);
            list.Viewport.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            list.Viewport.RegisterCallback<PointerDownEvent>(OnViewportPointerDown);
            list.Viewport.RegisterCallback<PointerMoveEvent>(OnViewportPointerMove);
            list.Viewport.RegisterCallback<PointerUpEvent>(OnViewportPointerUp);
            list.Viewport.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            list.Viewport.RegisterCallback<DragPerformEvent>(OnDragPerform);
            list.Viewport.RegisterCallback<DragLeaveEvent>(_ => MixerUi.Show(dragLine, false));
            list.Viewport.RegisterCallback<DragExitedEvent>(_ => { MixerUi.Show(dragLine, false); EndDrag(); });
            root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            root.RegisterCallback<ValidateCommandEvent>(OnValidateCommand);
            root.RegisterCallback<ExecuteCommandEvent>(OnCommand);
            footer = MixerUi.Text("", "mxh-footer");
            root.Add(footer);
            toasts = new MixerToasts();
            root.Add(toasts);
            root.schedule.Execute(Tick).Every(50);
            built = true;
            dirty = true;
        }

        private void BuildTopBar()
        {
            var bar = MixerUi.Box("mxh-topbar");
            root.Add(bar);
            searchField = new TextField { value = search };
            searchField.AddToClassList("mx-field");
            searchField.AddToClassList("mxh-search");
            searchField.tooltip = "Search by name; t:Light or c:Rigidbody by type";
            searchField.RegisterValueChangedCallback(evt => { search = evt.newValue; LeaveUnitySearch(); filter.Pattern = searchOptions.For(search); MarkDirty(); });
            searchField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) { searchField.value = string.Empty; root.Focus(); evt.StopPropagation(); } else if (evt.keyCode == KeyCode.DownArrow) { root.Focus(); evt.StopPropagation(); } });
            var searchIcon = new VectorIcon(IconGlyph.Search); searchIcon.AddToClassList("mxh-search__icon");
            searchField.Add(searchIcon);
            // The kind filters, in a menu at the end of the search field (they took a line of their own).
            filterButton = new Button { text = string.Empty, tooltip = "Show only some kinds of objects" };
            filterButton.AddToClassList("mxh-filter");
            var filterIcon = new VectorIcon(IconGlyph.Sliders) { pickingMode = PickingMode.Ignore }; filterIcon.AddToClassList("mxh-filter__icon"); filterButton.Add(filterIcon);
            filterButton.Add(MixerUi.Text("Filter", "mxh-filter__text"));
            filterCount = MixerUi.Text("", "mxh-filter__count");
            filterButton.Add(filterCount);
            MixerUi.Press(filterButton, ShowFilterMenu);
            searchOptions = new MixerSearch(searchField, "Hierarchy", () => { SyncFilter(); RebuildNow(); }, filterButton);
            bar.Add(searchField);
            bar.Add(MixerUi.Icon(IconGlyph.Plus, "Create an empty object (under the selection)", () => CreateEmpty(true), "mxh-add"));
            SyncFilter();
        }

        private void ShowFilterMenu()
        {
            var menu = new GenericMenu();
            void Kind(string name, Func<bool> get, Action<bool> set) =>
                menu.AddItem(new GUIContent(name), get(), () => { set(!get()); SyncFilter(); RebuildNow(); });
            Kind("Meshes", () => filterMeshes, v => filterMeshes = v);
            Kind("Skinned meshes", () => filterSkinned, v => filterSkinned = v);
            Kind("Lights", () => filterLights, v => filterLights = v);
            Kind("Cameras", () => filterCameras, v => filterCameras = v);
            Kind("Prefabs", () => filterPrefabs, v => filterPrefabs = v);
            Kind("Missing", () => filterMissing, v => filterMissing = v);
            Kind("Inactive", () => filterInactive, v => filterInactive = v);
            menu.AddSeparator("");
            if (FilterCount() > 0)
                menu.AddItem(new GUIContent("Show everything"), false, () =>
                {
                    filterMeshes = filterSkinned = filterLights = filterCameras = filterPrefabs = filterMissing = filterInactive = false;
                    SyncFilter(); RebuildNow();
                });
            else menu.AddDisabledItem(new GUIContent("Show everything"));
            menu.DropDown(filterButton.worldBound);
        }

        private int FilterCount() => (filterMeshes ? 1 : 0) + (filterSkinned ? 1 : 0) + (filterLights ? 1 : 0) + (filterCameras ? 1 : 0)
                                     + (filterPrefabs ? 1 : 0) + (filterMissing ? 1 : 0) + (filterInactive ? 1 : 0);

        private void SyncFilter()
        {
            filter.Pattern = searchOptions != null ? searchOptions.For(search) : new SearchPattern(search, false, false);
            filter.Meshes = filterMeshes; filter.Skinned = filterSkinned; filter.Lights = filterLights; filter.Cameras = filterCameras;
            filter.Prefabs = filterPrefabs; filter.Missing = filterMissing; filter.Inactive = filterInactive;
            if (filterButton == null) return;
            int count = FilterCount();
            filterButton.EnableInClassList("mxh-filter--on", count > 0);
            filterCount.text = count > 0 ? count.ToString() : string.Empty;
            MixerUi.Show(filterCount, count > 0);
        }

        private void ApplyDensity()
        {
            list.SetRowHeight(Compact ? CompactRowHeight : RowHeight);
            root.EnableInClassList("mxh-root--compact", Compact);
            list.RefreshRows();
        }

        private void Tick()
        {
            if (!built) return;
            PersistExpanded(force: false);
            PollThumbnails();
            TickHooks();
            if (!visible || !dirty) return;
            // Hierarchy changes come in bursts (an import, a prefab apply): one rebuild per 60 ms is plenty.
            if (EditorApplication.timeSinceStartup - lastRebuild < 0.06) return;
            RebuildNow();
        }

        // What the user did (a fold, a filter, a drop) shows at once, without waiting for the burst throttle.
        private void RebuildNow()
        {
            if (!built) return;
            dirty = false;
            lastRebuild = EditorApplication.timeSinceStartup;
            Rebuild();
        }

        private void Rebuild()
        {
            thumbnailKeys.Clear();
            model.Rebuild(filter);
            list.SetItemCount(model.Count, keepAtEnd: false);
            list.RefreshRows();
            // An expression found too slow while matching shows on the field.
            searchOptions?.ShowState();
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            int shown = model.Rows.Count(r => r.Object != null);
            int selected = Selection.gameObjects.Length;
            footer.text = (filter.Any ? shown + " matching" : shown + (shown == 1 ? " object shown" : " objects shown")) + (selected > 0 ? " · " + selected + " selected" : "")
                          + (MixerLinkService.Connected ? " · Blender linked" : "");
        }

        /// <summary>Opens the way to an object and scrolls it on screen (tests, pings). A window whose rows are not
        /// built yet (a background tab after a reload) only opens the way.</summary>
        internal void RevealObject(GameObject go)
        {
            if (go == null) return;
            if (!built || list == null) { model.Reveal(go); return; }
            model.Reveal(go);
            RebuildNow();
            int index = model.IndexOf(go.GetInstanceID());
            if (index >= 0) list.Reveal(index);
            list.RefreshRows();
        }

        // Only the rows' selected look changes: the rows themselves stay (a rectangle drag in the Scene view changes the
        // selection many times a second).
        private void OnSelectionChanged()
        {
            if (!built) return;
            if (selectingHere) selectingHere = false;
            else RevealRow(Selection.activeGameObject);
            RefreshSelection();
            UpdateFooter();
        }

        private void RefreshSelection()
        {
            foreach (var element in list.Rows)
            {
                if (element.resolvedStyle.display == DisplayStyle.None) continue;
                var row = RowOf(element);
                element.EnableInClassList("mxh-row--selected", row != null && !row.IsScene && Selection.Contains(row.InstanceId));
            }
        }

        // A bone sub-selected in Weight Paint (here or in the Scene view's armature overlay) shows at once.
        private void OnSubSelectionChanged()
        {
            if (!built) return;
            var bone = BoneSubSelection.Bone;
            RevealRow(bone != null ? bone.gameObject : null);
        }

        private void RevealRow(GameObject go)
        {
            // A prefab asset picked in the Project is in no scene: nothing here to open.
            if (go != null && !go.scene.IsValid()) go = null;
            if (go != null && model.IndexOf(go.GetInstanceID()) < 0 && !filter.Any) { model.Reveal(go); RebuildNow(); }
            int index = go != null ? model.IndexOf(go.GetInstanceID()) : -1;
            if (index >= 0) list.Reveal(index);
            list.RefreshRows();
        }

        // ---- Expanded state across editor starts ---------------------------------------------------------------------

        // An editor start (no folds in this session yet): the objects open when the editor closed open again.
        private void RestoreExpanded()
        {
            if (HierarchyModel.HasSessionState || persistedExpanded == null || persistedExpanded.Count == 0) return;
            var ids = new List<GlobalObjectId>();
            foreach (string text in persistedExpanded) if (GlobalObjectId.TryParse(text, out var id)) ids.Add(id);
            if (ids.Count == 0) return;
            var instances = new int[ids.Count];
            try { GlobalObjectId.GlobalObjectIdentifiersToInstanceIDsSlow(ids.ToArray(), instances); }
            catch (Exception) { return; }
            model.Restore(instances);
        }

        // At most once a second, and when the window goes: the open objects that have a stable id (saved scenes' objects).
        private void PersistExpanded(bool force)
        {
            if (model.Version == persistedVersion) return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - lastPersist < 1.0) return;
            lastPersist = now;
            persistedVersion = model.Version;
            var objects = new List<Object>();
            foreach (int id in model.Expanded)
            {
                if (objects.Count >= PersistedLimit) break;
                if (EditorUtility.InstanceIDToObject(id) is GameObject go && go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path)) objects.Add(go);
            }
            var ids = new GlobalObjectId[objects.Count];
            try { if (objects.Count > 0) GlobalObjectId.GetGlobalObjectIdsSlow(objects.ToArray(), ids); }
            catch (Exception) { return; }
            persistedExpanded = ids.Where(i => i.identifierType != 0).Select(i => i.ToString()).ToList();
        }

        // ---- Rows ---------------------------------------------------------------------------------------------------

        private VisualElement MakeRow()
        {
            var row = MixerUi.Box("mxh-row");
            row.Add(new MixerIndent("mxh-row__indent"));
            var chevron = MixerUi.Icon(IconGlyph.Chevron, "", null, evt =>
            {
                var r = RowOf(row);
                if (r != null && r.HasChildren)
                {
                    if (r.IsScene) model.SetSceneFolded(r.Scene, r.Expanded); else model.SetExpanded(r.InstanceId, !r.Expanded, evt.altKey);
                    RebuildNow();
                }
                evt.StopPropagation();
            }, "mxh-row__chevron");
            row.Add(chevron);
            var dot = MixerUi.Box("mxh-row__dot", PickingMode.Ignore);
            row.Add(dot);
            row.Add(MakeActiveBox(row));
            var picture = MixerUi.Box("mxh-row__picture", PickingMode.Ignore);
            row.Add(picture);
            var icon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; icon.AddToClassList("mxh-row__icon"); picture.Add(icon);
            var glyph = new VectorIcon(IconGlyph.Bones); glyph.AddToClassList("mxh-row__glyph"); picture.Add(glyph);
            var thumbnail = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; thumbnail.AddToClassList("mxh-row__thumbnail"); picture.Add(thumbnail);
            // A VRChat avatar's picture carries VRChat's logo.
            var vrchat = VRChatLogo.Create();
            if (vrchat != null) { vrchat.AddToClassList("mxh-row__vrchat-mark"); vrchat.tooltip = "A VRChat avatar (it has an avatar descriptor)"; vrchat.pickingMode = PickingMode.Position; picture.Add(vrchat); }
            var body = MixerUi.Box("mxh-row__body", PickingMode.Ignore);
            row.Add(body);
            var line = MixerUi.Box("mxh-row__line", PickingMode.Ignore);
            body.Add(line);
            line.Add(MixerUi.Text("", "mxh-row__name"));
            var rename = new TextField(); rename.AddToClassList("mxh-row__rename"); MixerUi.Show(rename, false); line.Add(rename);
            line.Add(MixerUi.Box("mxh-row__badges", PickingMode.Ignore));
            line.Add(MixerUi.Spacer());
            var components = MixerUi.Box("mxh-row__components", PickingMode.Ignore);
            line.Add(components);
            // As Unity's Hierarchy: a click switches the object and its children, Alt+click the object alone.
            var pick = MixerUi.Icon(IconGlyph.Lock, "Pickable in the Scene view (Alt+click: this object only)", null, evt =>
            {
                var r = RowOf(row);
                if (r?.Object != null) { SceneVisibilityManager.instance.TogglePicking(r.Object, !evt.altKey); RebuildNow(); }
                evt.StopPropagation();
            }, "mxh-row__pick");
            var eye = MixerUi.Icon(IconGlyph.Eye, "Visible in the Scene view (Alt+click: this object only)", null, evt =>
            {
                var r = RowOf(row);
                if (r?.Object != null) { SceneVisibilityManager.instance.ToggleVisibility(r.Object, !evt.altKey); RebuildNow(); }
                evt.StopPropagation();
            }, "mxh-row__eye");
            // At the end of the name's line, or at the row's right end on rows with a light's ball or a drop field.
            var switches = MixerUi.Box("mxh-row__switches");
            switches.Add(pick);
            switches.Add(eye);
            line.Add(switches);
            // Other tools' decorations, first so everything on the line draws over them.
            line.Insert(0, MakeDecorations(row, components, switches));
            body.Add(MakeSecondLine(row));
            body.Add(MakeAvatarLines(row));
            // Right of the lines: a light's turning ball, My Avatar's drop field on an avatar root.
            row.Add(MakeLightSphere(row));
            row.Add(MakeDropField(row));
            row.RegisterCallback<PointerDownEvent>(evt => OnRowPointerDown(row, evt));
            // A double-click on the row's controls (the active box, the light's ball, the switches) is theirs.
            row.RegisterCallback<ClickEvent>(evt => { if (evt.clickCount == 2 && !OnControl(evt.target as VisualElement, row)) OnRowDoubleClick(row); });
            rename.RegisterCallback<FocusOutEvent>(_ => CommitRename(row, rename, true));
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CommitRename(row, rename, true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { CommitRename(row, rename, false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            return row;
        }

        private HierarchyRow RowOf(VisualElement element) => model[list.ItemOf(element)];

        private static readonly string[] ControlClasses = { "mxh-row__active", "mxh-row__sphere", "mxh-row__chevron", "mxh-row__switches", "mxh-drop", "mxh-bones", "mxh-refit", "mxh-row__rename" };

        private static bool OnControl(VisualElement target, VisualElement row)
        {
            for (var e = target; e != null && e != row; e = e.parent)
                if (e is Button || ControlClasses.Any(e.ClassListContains)) return true;
            return false;
        }

        private void BindRow(VisualElement element, int index)
        {
            var row = model[index];
            if (row == null) return;
            var indent = element.Q<MixerIndent>(className: "mxh-row__indent");
            var chevron = element.Q(className: "mxh-row__chevron");
            var dot = element.Q(className: "mxh-row__dot");
            var icon = element.Q<Image>(className: "mxh-row__icon");
            var name = element.Q<Label>(className: "mxh-row__name");
            var rename = element.Q<TextField>(className: "mxh-row__rename");
            var badges = element.Q(className: "mxh-row__badges");
            var components = element.Q(className: "mxh-row__components");
            var pick = element.Q(className: "mxh-row__pick");
            var eye = element.Q(className: "mxh-row__eye");
            // A guide under the fold arrow of each object above (the scene's own arrow excepted).
            indent.Set(Mathf.Max(0, row.Depth - 1) * 14f + (row.IsScene ? 0f : 4f), row.IsScene ? 0 : Mathf.Max(0, row.Depth - 1), 14f, 13f);
            chevron.style.visibility = row.HasChildren ? Visibility.Visible : Visibility.Hidden;
            chevron.EnableInClassList("mxh-row__chevron--open", row.Expanded);
            element.EnableInClassList("mxh-row--scene", row.IsScene);
            element.EnableInClassList("mxh-row--selected", !row.IsScene && Selection.Contains(row.InstanceId));
            var picked = BoneSubSelection.Bone;
            bool subSelected = !row.IsScene && picked != null && row.Object == picked.gameObject;
            element.EnableInClassList("mxh-row--subselected", subSelected);
            element.EnableInClassList("mxh-row--dim", !row.IsScene && (!row.Active || !row.Match));
            element.EnableInClassList("mxh-row--prefab", row.PrefabInstance);
            element.EnableInClassList("mxh-row--missing", row.PrefabMissing || row.MissingScript);
            element.EnableInClassList("mxh-row--hidden", row.Hidden);
            element.EnableInClassList("mxh-row--odd", index % 2 == 1);
            element.EnableInClassList("mxh-row--excluded", row.Excluded);
            element.EnableInClassList("mxh-row--cut", !row.IsScene && IsCut(row.InstanceId));
            element.EnableInClassList("mxh-row--ping", pingId != 0 && !row.IsScene && row.InstanceId == pingId);
            BindDecorations(element);
            name.text = row.IsScene ? row.Name + (row.Scene.isLoaded && row.Scene.isDirty ? " *" : "") + (row.Scene.isLoaded || row.IsDontDestroyOnLoad ? "" : " (not loaded)") : row.Name;
            name.EnableInClassList("mxh-row__name--active-scene", row.IsScene && row.Active);
            bool renaming = row.Object != null && row.InstanceId == renamingId;
            MixerUi.Show(name, !renaming);
            MixerUi.Show(rename, renaming);
            rename.userData = renaming ? (object)row.InstanceId : null;
            badges.Clear();
            components.Clear();
            bool tall = Tall(row);
            element.EnableInClassList("mxh-row--tall", tall);
            BindRich(element, row, tall);
            if (row.IsScene)
            {
                icon.image = EditorGUIUtility.IconContent("SceneAsset Icon").image;
                MixerUi.Show(dot, false); MixerUi.Show(pick, false); MixerUi.Show(eye, false);
                return;
            }
            // Unity's own icon of the object: a prefab, a model, a variant or a missing prefab as its Hierarchy shows them.
            icon.image = IconOf(row);
            if (row.VrchatAvatar && !element.ClassListContains("mxh-row--picture"))
            {
                var logo = VRChatLogo.Create();
                if (logo != null) { logo.tooltip = "A VRChat avatar (it has an avatar descriptor)"; logo.pickingMode = PickingMode.Position; logo.AddToClassList("mxh-row__vrchat"); badges.Add(logo); }
            }
            if (row.PrefabRoot) badges.Add(Badge("prefab", row.PrefabMissing ? "The prefab asset is missing" : "A prefab instance", "mxh-badge--prefab"));
            if (row.MissingScript) badges.Add(Badge("missing script", "A component's script is missing", "mxh-badge--missing"));
            if (subSelected) badges.Add(PaintsPicked()
                ? Badge("painting", "The bone Weight Paint paints and poses; the mesh stays selected", "mxh-badge--mixer")
                : Badge("posing", "Posed in Weight Paint; the painted mesh is not skinned to it", "mxh-badge--mixer"));
            if (ShowComponents && row.Object != null && !row.VrchatAvatar)
            {
                int shown = 0;
                foreach (var component in row.Components)
                {
                    if (component == null || component is Transform) continue;
                    if (shown >= 4) { components.Add(MixerUi.Text("+", "mxh-row__more")); break; }
                    var image = new Image { image = AssetPreview.GetMiniThumbnail(component), scaleMode = ScaleMode.ScaleToFit, tooltip = component.GetType().Name, pickingMode = PickingMode.Position };
                    image.AddToClassList("mxh-row__component");
                    components.Add(image);
                    shown++;
                }
            }
            MixerUi.Show(dot, ShowDots);
            dot.EnableInClassList("mxh-row__dot--excluded", row.Excluded);
            dot.EnableInClassList("mxh-row__dot--linked", !row.Excluded && MixerLinkService.Connected);
            dot.EnableInClassList("mxh-row__dot--owned", row.BlenderOwned);
            dot.tooltip = row.BlenderOwned ? "Being edited in Blender" : row.Excluded ? "Kept out of Blender" : MixerLinkService.Connected ? "In the Blender project" : "Goes to Blender when it is linked";
            MixerUi.Show(pick, true); MixerUi.Show(eye, true);
            pick.EnableInClassList("mxh-row__switch--off", row.PickingDisabled);
            eye.EnableInClassList("mxh-row__switch--off", row.Hidden);
            var pickIcon = pick.Q<VectorIcon>(); if (pickIcon != null) pickIcon.Glyph = row.PickingDisabled ? IconGlyph.Lock : IconGlyph.Unlock;
            var eyeIcon = eye.Q<VectorIcon>(); if (eyeIcon != null) eyeIcon.Glyph = row.Hidden ? IconGlyph.EyeOff : IconGlyph.Eye;
            if (renaming && rename.value != row.Name && !rename.focusController?.focusedElement?.Equals(rename.Q(TextField.textInputUssName)) == true)
            {
                rename.SetValueWithoutNotify(row.Name);
                rename.schedule.Execute(() => { rename.Q(TextField.textInputUssName)?.Focus(); rename.SelectAll(); }).StartingIn(10);
            }
        }

        // Whether the sub-selected bone is the painted channel (else it is only posed).
        private static bool PaintsPicked()
        {
            var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
            var skinned = target?.Skinned;
            var channel = MixerSceneTool.ChannelOn(target);
            return skinned != null && !channel.IsGroup && channel.Bone >= 0 && channel.Bone < skinned.bones.Length && skinned.bones[channel.Bone] == BoneSubSelection.Bone;
        }

        private static VisualElement Badge(string text, string tooltip, string cls)
        {
            var badge = MixerUi.Text(text, "mxh-badge");
            badge.AddToClassList(cls);
            badge.tooltip = tooltip;
            return badge;
        }

        // ---- Selection, keys, rename ---------------------------------------------------------------------------------

        private void OnRowPointerDown(VisualElement element, PointerDownEvent evt)
        {
            var row = RowOf(element);
            if (row == null) return;
            root.Focus();
            int index = list.ItemOf(element);
            if (row.IsScene)
            {
                if (evt.button == 0 && evt.clickCount == 2 && row.Scene.isLoaded && !row.IsDontDestroyOnLoad && !IsPrefabStageScene(row.Scene)) SceneManager.SetActiveScene(row.Scene);
                return;
            }
            if (evt.button == 1)
            {
                if (!Selection.Contains(row.InstanceId)) Select(index, false, false);
                else cursorId = row.InstanceId;
                return;
            }
            if (evt.button != 0) return;
            // Weight Paint: a plain click on a bone of the painted rig sub-selects it and the mesh stays selected (Shift
            // and Ctrl clicks still select in Unity). Nothing to drag then: a drag would move the selected mesh.
            bool plain = !evt.shiftKey && !evt.ctrlKey && !evt.commandKey;
            if (plain && row.Object != null && BoneSubSelection.Pick(row.Object.transform)) { list.RefreshRows(); return; }
            BoneSubSelection.Clear();
            if (evt.shiftKey && AnchorIndex() >= 0) Select(index, true, false);
            else if (evt.ctrlKey || evt.commandKey) Select(index, false, true);
            else if (!Selection.Contains(row.InstanceId)) Select(index, false, false);
            else anchorId = cursorId = row.InstanceId;
            BeginDrag(index, evt);
        }

        private void OnRowDoubleClick(VisualElement element)
        {
            var row = RowOf(element);
            if (row?.Object == null) return;
            var picked = BoneSubSelection.Bone;
            if (picked != null && row.Object == picked.gameObject) { BoneSubSelection.Frame(); return; }
            Selection.activeGameObject = row.Object;
            SceneView.FrameLastActiveSceneView();
        }

        // The row a Shift+click extends from, found again after every rebuild (rows move when folds change).
        private int AnchorIndex() => anchorId != 0 ? model.IndexOf(anchorId) : -1;

        private void Select(int index, bool range, bool toggle)
        {
            var row = model[index];
            if (row?.Object == null) return;
            var before = Selection.instanceIDs;
            if (range)
            {
                int anchor = AnchorIndex();
                int from = Mathf.Min(anchor, index), to = Mathf.Max(anchor, index);
                var ids = new List<int>();
                for (int i = from; i <= to; i++) if (model[i]?.Object != null) ids.Add(model[i].InstanceId);
                Selection.instanceIDs = ids.ToArray();
            }
            else if (toggle)
            {
                var ids = Selection.instanceIDs.ToList();
                if (ids.Contains(row.InstanceId)) ids.Remove(row.InstanceId); else ids.Add(row.InstanceId);
                Selection.instanceIDs = ids.ToArray();
                anchorId = row.InstanceId;
            }
            else
            {
                Selection.activeInstanceID = row.InstanceId;
                anchorId = row.InstanceId;
            }
            cursorId = row.InstanceId;
            if (!Selection.instanceIDs.SequenceEqual(before)) selectingHere = true;
            RefreshSelection();
        }

        // The row the keys step from: the cursor while it is selected, else Unity's active object.
        private int CursorIndex()
        {
            int index = cursorId != 0 && Selection.Contains(cursorId) ? model.IndexOf(cursorId) : -1;
            if (index >= 0) return index;
            return Selection.activeGameObject != null ? model.IndexOf(Selection.activeGameObject.GetInstanceID()) : -1;
        }

        // A rename whose row is not shown (a search hides the new object) is given up: it would hold every key.
        private bool Renaming
        {
            get
            {
                if (renamingId != 0 && model.IndexOf(renamingId) < 0) renamingId = 0;
                return renamingId != 0;
            }
        }

        private void OnKey(KeyDownEvent evt)
        {
            if (Renaming) return;
            if (evt.target is VisualElement target && (target == searchField || searchField.Contains(target))) return;
            // The light's ball turns with the arrows while it has the focus.
            if (evt.target is OrbitSphere) return;
            int current = CursorIndex();
            bool command = evt.ctrlKey || evt.commandKey;
            switch (evt.keyCode)
            {
                case KeyCode.DownArrow: Step(current, 1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.UpArrow: Step(current, -1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.Home: Step(-1, 1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.End: Step(model.Count, -1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.RightArrow: if (current >= 0 && model[current].HasChildren) { if (!model[current].Expanded) model.SetExpanded(model[current].InstanceId, true, evt.altKey); else Step(current, 1, false); RebuildNow(); } evt.StopPropagation(); break;
                case KeyCode.LeftArrow:
                    if (current >= 0) { if (model[current].Expanded && model[current].HasChildren) model.SetExpanded(model[current].InstanceId, false, evt.altKey); else { var parent = model[current].Object.transform.parent; if (parent != null) Selection.activeGameObject = parent.gameObject; } RebuildNow(); }
                    evt.StopPropagation(); break;
                case KeyCode.F2: BeginRename(); evt.StopPropagation(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: if (evt.target is Button) break; BeginRename(); evt.StopPropagation(); break;
                // As Unity: Delete, or Cmd+Backspace on macOS; a plain Backspace deletes nothing.
                case KeyCode.Delete: DeleteSelection(); evt.StopPropagation(); break;
                case KeyCode.Backspace: if (evt.commandKey) { DeleteSelection(); evt.StopPropagation(); } break;
                case KeyCode.F: if (!evt.ctrlKey) { if (BoneSubSelection.Bone != null) BoneSubSelection.Frame(); else SceneView.FrameLastActiveSceneView(); evt.StopPropagation(); } else { searchField.Q(TextField.textInputUssName)?.Focus(); evt.StopPropagation(); } break;
                case KeyCode.D: if (command) { Duplicate(); evt.StopPropagation(); } break;
                case KeyCode.A: if (command) { Selection.instanceIDs = model.Rows.Where(r => r.Object != null).Select(r => r.InstanceId).ToArray(); list.RefreshRows(); evt.StopPropagation(); } break;
                case KeyCode.Escape: ForgetCut(); Selection.activeObject = null; list.RefreshRows(); evt.StopPropagation(); break;
            }
        }

        // Edit menu commands (and their shortcuts: Ctrl+C, Ctrl+X, Ctrl+V...) the hierarchy answers while it has the focus.
        private static readonly HashSet<string> Commands = new HashSet<string> { "Find", "Duplicate", "Delete", "SoftDelete", "SelectAll", "Copy", "Cut", "Paste" };

        // The search field and a rename keep their own text commands (Ctrl+C copies text there, not objects).
        private bool InTextField(EventBase evt) =>
            Renaming || (evt.target is VisualElement target && (target == searchField || searchField.Contains(target)));

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            if (Commands.Contains(evt.commandName) && !InTextField(evt)) evt.StopPropagation();
        }

        private void OnCommand(ExecuteCommandEvent evt)
        {
            if (InTextField(evt)) return;
            switch (evt.commandName)
            {
                case "Find": searchField.Q(TextField.textInputUssName)?.Focus(); break;
                case "Duplicate": Duplicate(); break;
                case "Delete": case "SoftDelete": DeleteSelection(); break;
                case "Copy": Copy(); break;
                case "Cut": Cut(); break;
                case "Paste": Paste(); break;
                case "SelectAll": Selection.instanceIDs = model.Rows.Where(r => r.Object != null).Select(r => r.InstanceId).ToArray(); list.RefreshRows(); break;
                default: return;
            }
            evt.StopPropagation();
        }

        private void Step(int from, int direction, bool extend)
        {
            int index = from + direction;
            while (index >= 0 && index < model.Count && model[index].Object == null) index += direction;
            if (index < 0 || index >= model.Count) return;
            if (extend && AnchorIndex() >= 0) Select(index, true, false); else { Select(index, false, false); }
            list.Reveal(index);
        }

        // The clicked row's object from a context menu, else the cursor's.
        private void BeginRename(GameObject go = null)
        {
            if (go == null) { int index = CursorIndex(); go = index >= 0 ? model[index].Object : null; }
            if (go == null || model.IndexOf(go.GetInstanceID()) < 0) return;
            renamingId = go.GetInstanceID();
            list.RefreshRows();
        }

        private void CommitRename(VisualElement rowElement, TextField field, bool apply)
        {
            if (renamingId == 0) return;
            // A pooled row that scrolled away from the object being renamed holds no name for it: nothing is applied.
            bool ours = field.userData is int id && id == renamingId;
            var go = EditorUtility.InstanceIDToObject(renamingId) as GameObject;
            renamingId = 0;
            field.userData = null;
            if (apply && ours && go != null && !string.IsNullOrWhiteSpace(field.value) && field.value.Trim() != go.name)
            {
                Undo.RecordObject(go, "Rename " + go.name);
                go.name = field.value.Trim();
                EditorUtility.SetDirty(go);
            }
            MarkDirty();
            root.Focus();
        }

        internal void DeleteSelection()
        {
            var objects = Selection.gameObjects.Where(g => g != null && g.scene.IsValid() && !IsStageRoot(g)).ToList();
            if (objects.Count == 0) return;
            // Children go with their parent: only the topmost are destroyed (destroying a child of one already gone throws).
            objects = objects.Where(g => !objects.Any(o => o != g && g.transform.IsChildOf(o.transform))).ToList();
            string what = objects.Count == 1 ? objects[0].name : objects.Count + " objects";
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Delete " + what);
            foreach (var go in objects) if (go != null) Undo.DestroyObjectImmediate(go);
            toasts.Show("Deleted " + what);
            RebuildNow();
        }

        private void Duplicate()
        {
            if (Selection.gameObjects.Length == 0) return;
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            MarkDirty();
        }

        internal void Copy()
        {
            if (Selection.gameObjects.Length == 0) return;
            Unsupported.CopyGameObjectsToPasteboard();
            ForgetCut();
        }

        /// <summary>
        /// Cut keeps the objects where they are, dimmed, until a Paste moves them (beside the selected object, or under the
        /// clicked one with Paste as Child): a move keeps every reference to them, where a copy would break those. They go
        /// to Unity's pasteboard too, for a paste in another window (a copy there).
        /// </summary>
        internal void Cut()
        {
            var objects = Selection.gameObjects.Where(g => g != null && g.scene.IsValid() && !IsStageRoot(g)).ToList();
            if (objects.Count == 0) return;
            Unsupported.CopyGameObjectsToPasteboard();
            SessionState.SetString(CutKey, string.Join(",", objects.Select(g => g.GetInstanceID())));
            toasts.Show("Cut " + (objects.Count == 1 ? objects[0].name : objects.Count + " objects") + ": Paste moves " + (objects.Count == 1 ? "it" : "them") + ".");
            list.RefreshRows();
        }

        /// <summary>
        /// Moves what Cut took beside the selected object (under <paramref name="asChildOf"/> when given; at the active
        /// scene's root with nothing selected); without a cut, pastes Unity's pasteboard (copies), under
        /// <paramref name="asChildOf"/> when given.
        /// </summary>
        internal void Paste(GameObject asChildOf = null)
        {
            var cut = CutObjects();
            if (cut.Count == 0)
            {
                Unsupported.PasteGameObjectsFromPasteboard();
                if (asChildOf != null && asChildOf.scene.IsValid())
                    foreach (var pasted in Selection.gameObjects.Where(g => g != null && g.scene.IsValid() && g != asChildOf))
                        Undo.SetTransformParent(pasted.transform, asChildOf.transform, "Paste " + pasted.name);
                MarkDirty();
                return;
            }
            var anchor = asChildOf != null ? asChildOf : SceneParent();
            // Pasted onto what was cut, or inside it: nowhere to go yet.
            if (anchor != null && cut.Any(c => anchor.transform.IsChildOf(c.transform)))
            {
                toasts.Show("Select where " + (cut.Count == 1 ? cut[0].name + " goes" : "they go") + " first: Paste moves what was cut there.", true);
                return;
            }
            ForgetCut();
            HierarchyRow target;
            DropZone zone;
            if (anchor != null) { target = new HierarchyRow { Object = anchor, Scene = anchor.scene, InstanceId = anchor.GetInstanceID() }; zone = asChildOf != null ? DropZone.Onto : DropZone.Below; }
            else
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                var scene = stage != null ? stage.scene : SceneManager.GetActiveScene();
                target = new HierarchyRow { IsScene = true, Scene = scene, InstanceId = scene.handle };
                zone = DropZone.Onto;
            }
            Move(cut, target, zone);
            Selection.objects = cut.Where(g => g != null).ToArray();
        }

        // What Cut took that is still in a scene.
        private static List<GameObject> CutObjects()
        {
            var objects = new List<GameObject>();
            foreach (string part in SessionState.GetString(CutKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int id) && EditorUtility.InstanceIDToObject(id) is GameObject go && go != null && go.scene.IsValid()) objects.Add(go);
            return objects;
        }

        private static bool IsCut(int instanceId)
        {
            string cut = SessionState.GetString(CutKey, "");
            return cut.Length > 0 && ("," + cut + ",").Contains("," + instanceId + ",");
        }

        internal static bool HasCut => CutObjects().Count > 0;

        private void ForgetCut()
        {
            if (SessionState.GetString(CutKey, "").Length == 0) return;
            SessionState.EraseString(CutKey);
            list?.RefreshRows();
        }

        private void CreateEmpty(bool asChild, GameObject clicked = null)
        {
            var parent = clicked != null ? clicked : SceneParent();
            var go = new GameObject("GameObject");
            Undo.RegisterCreatedObjectUndo(go, "Create Empty");
            Place(go, parent != null ? (asChild ? parent.transform : parent.transform.parent) : null, parent != null ? parent.scene : default);
            if (parent != null && asChild) model.SetExpanded(parent.GetInstanceID(), true);
            Selection.activeGameObject = go;
            renamingId = go.GetInstanceID();
            RebuildNow();
        }

        /// <summary>
        /// Puts an object just created (and registered for Undo) under <paramref name="parent"/>, at its origin; without
        /// one, at the root of <paramref name="scene"/> (else the active scene), or in Prefab Mode under the prefab's root
        /// (a second root would be dropped when the prefab saves).
        /// </summary>
        internal static void Place(GameObject go, Transform parent, Scene scene = default)
        {
            // An asset's transform (a prefab picked in the Project) is no parent for a scene object.
            if (parent != null && !parent.gameObject.scene.IsValid()) parent = null;
            if (parent != null)
            {
                if (go.transform.parent == null && go.scene != parent.gameObject.scene) SceneManager.MoveGameObjectToScene(go, parent.gameObject.scene);
                // Through Undo, so a redo of the creation puts it back under its parent.
                Undo.SetTransformParent(go.transform, parent, false, "Create " + go.name);
                return;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                StageUtility.PlaceGameObjectInCurrentStage(go);
                Undo.SetTransformParent(go.transform, stage.prefabContentsRoot.transform, false, "Create " + go.name);
                return;
            }
            var into = scene.IsValid() && scene.isLoaded ? scene : SceneManager.GetActiveScene();
            if (into.IsValid() && into.isLoaded && go.scene != into) SceneManager.MoveGameObjectToScene(go, into);
        }

        // Unity's active object, when it is in a scene (a prefab asset picked in the Project is not a parent here).
        private static GameObject SceneParent()
        {
            var go = Selection.activeGameObject;
            return go != null && go.scene.IsValid() ? go : null;
        }

        private static bool IsPrefabStageScene(Scene scene)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && stage.scene == scene;
        }

        private static bool IsStageRoot(GameObject go)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && stage.prefabContentsRoot == go;
        }
    }
}
