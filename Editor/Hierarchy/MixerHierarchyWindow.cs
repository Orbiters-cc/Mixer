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
    /// the selection in both directions, rename, drag to reparent and reorder, prefab drops, visibility and picking
    /// switches, prefab and missing-script badges, Mixer's edits and the Blender link state per object (sent,
    /// excluded, edited in Blender), and a context menu that also sends objects to Blender or keeps them out.
    /// </summary>
    internal sealed partial class MixerHierarchyWindow : EditorWindow, IHasCustomMenu
    {
        private const string StyleSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        private const string HierarchySheetPath = MixerInfo.BasePath + "/Editor/UI/hierarchy.uss";
        private const string CompactPref = "Orbiters.Mixer.Hierarchy.Compact";
        private const string ComponentsPref = "Orbiters.Mixer.Hierarchy.ShowComponents";
        private const string DotsPref = "Orbiters.Mixer.Hierarchy.ShowBlenderDots";
        private const float RowHeight = 24f, CompactRowHeight = 20f;

        [SerializeField] private string search = string.Empty;
        [SerializeField] private bool filterMeshes, filterSkinned, filterLights, filterCameras, filterPrefabs, filterMissing, filterInactive;

        private readonly HierarchyModel model = new HierarchyModel();
        private readonly HierarchyFilter filter = new HierarchyFilter();
        private VisualElement root, chips, dragLine;
        private VirtualList list;
        private TextField searchField;
        private Label footer;
        private MixerToasts toasts;
        private bool dirty = true, visible = true, built;
        private int anchorRow = -1;
        private int renamingId;
        private double lastRebuild;
        private readonly Dictionary<string, Button> chipButtons = new Dictionary<string, Button>();

        private static bool Compact => EditorPrefs.GetBool(CompactPref, false);
        private static bool ShowComponents => EditorPrefs.GetBool(ComponentsPref, true);
        private static bool ShowDots => EditorPrefs.GetBool(DotsPref, true);

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
            EditorApplication.hierarchyChanged += MarkDirty;
            Selection.selectionChanged += OnSelectionChanged;
            EditorSceneManager.sceneDirtied += _ => MarkDirty();
            EditorSceneManager.sceneSaved += _ => MarkDirty();
            PrefabStage.prefabStageOpened += _ => MarkDirty();
            PrefabStage.prefabStageClosing += _ => MarkDirty();
            MixerLinkService.Changed += MarkDirty;
            MixerSession.Changed += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            Selection.selectionChanged -= OnSelectionChanged;
            MixerLinkService.Changed -= MarkDirty;
            MixerSession.Changed -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
        }

        private void OnBecameVisible() { visible = true; MarkDirty(); }
        private void OnBecameInvisible() => visible = false;
        private void MarkDirty() => dirty = true;

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open Unity's Hierarchy"), false, () => EditorApplication.ExecuteMenuItem("Window/General/Hierarchy"));
            menu.AddItem(new GUIContent("Replace Unity's Hierarchy"), HierarchyReplacement.Enabled, () => { HierarchyReplacement.SetEnabled(!HierarchyReplacement.Enabled); if (HierarchyReplacement.Enabled) HierarchyReplacement.ReplaceOpenHierarchies(); });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Compact rows"), Compact, () => { EditorPrefs.SetBool(CompactPref, !Compact); ApplyDensity(); });
            menu.AddItem(new GUIContent("Component icons"), ShowComponents, () => { EditorPrefs.SetBool(ComponentsPref, !ShowComponents); list.RefreshRows(); });
            menu.AddItem(new GUIContent("Blender dots"), ShowDots, () => { EditorPrefs.SetBool(DotsPref, !ShowDots); list.RefreshRows(); });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Expand all"), false, () => { foreach (var row in model.Rows.ToList()) if (row.Object != null && row.HasChildren) model.SetExpanded(row.InstanceId, true, true); MarkDirty(); });
            menu.AddItem(new GUIContent("Collapse all"), false, () => { model.Expanded.Clear(); foreach (var row in model.Rows) if (row.Object != null && row.Depth == 1) model.SetExpanded(row.InstanceId, false, true); MarkDirty(); });
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            root.AddToClassList("mxh-root");
            foreach (string path in new[] { StyleSheetPath, HierarchySheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }
            root.focusable = true;
            BuildTopBar();
            list = new VirtualList(Compact ? CompactRowHeight : RowHeight, MakeRow, BindRow);
            list.AddToClassList("mxh-list");
            root.Add(list);
            dragLine = MixerUi.Box("mxh-dragline", PickingMode.Ignore);
            MixerUi.Show(dragLine, false);
            list.Viewport.Add(dragLine);
            list.Viewport.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            list.Viewport.RegisterCallback<PointerDownEvent>(OnViewportPointerDown);
            list.Viewport.RegisterCallback<PointerMoveEvent>(OnViewportPointerMove);
            list.Viewport.RegisterCallback<PointerUpEvent>(OnViewportPointerUp);
            list.Viewport.RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag(false));
            list.Viewport.RegisterCallback<DragUpdatedEvent>(OnExternalDragUpdated);
            list.Viewport.RegisterCallback<DragPerformEvent>(OnExternalDragPerform);
            list.Viewport.RegisterCallback<DragLeaveEvent>(_ => MixerUi.Show(dragLine, false));
            root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            root.RegisterCallback<ValidateCommandEvent>(evt => { if (evt.commandName == "Find" || evt.commandName == "Duplicate" || evt.commandName == "Delete" || evt.commandName == "SoftDelete" || evt.commandName == "SelectAll") evt.StopPropagation(); });
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
            var mark = MixerUi.Box("mx-brand__mark mxh-mark");
            var logo = new MixerLogo.Element(); logo.AddToClassList("mx-brand__logo"); mark.Add(logo);
            bar.Add(mark);
            searchField = new TextField { value = search };
            searchField.AddToClassList("mx-field");
            searchField.AddToClassList("mxh-search");
            searchField.tooltip = "Search by name; t:Light or c:Rigidbody by type";
            searchField.RegisterValueChangedCallback(evt => { search = evt.newValue; filter.Text = search; MarkDirty(); });
            searchField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) { searchField.value = string.Empty; root.Focus(); evt.StopPropagation(); } else if (evt.keyCode == KeyCode.DownArrow) { root.Focus(); evt.StopPropagation(); } });
            var searchIcon = new VectorIcon(IconGlyph.Search); searchIcon.AddToClassList("mxh-search__icon");
            searchField.Add(searchIcon);
            bar.Add(searchField);
            bar.Add(MixerUi.Icon(IconGlyph.Plus, "Create an empty object (under the selection)", () => CreateEmpty(false), "mxh-add"));
            chips = MixerUi.Box("mxh-chips");
            root.Add(chips);
            Chip("Meshes", IconGlyph.Grid, () => filterMeshes, v => filterMeshes = v);
            Chip("Skinned", IconGlyph.Person, () => filterSkinned, v => filterSkinned = v);
            Chip("Lights", IconGlyph.Sparkle, () => filterLights, v => filterLights = v);
            Chip("Cameras", IconGlyph.Camera, () => filterCameras, v => filterCameras = v);
            Chip("Prefabs", IconGlyph.Layers, () => filterPrefabs, v => filterPrefabs = v);
            Chip("Missing", IconGlyph.Close, () => filterMissing, v => filterMissing = v);
            Chip("Inactive", IconGlyph.EyeOff, () => filterInactive, v => filterInactive = v);
            SyncFilter();
        }

        private void Chip(string name, IconGlyph glyph, Func<bool> get, Action<bool> set)
        {
            var chip = new Button { text = string.Empty, tooltip = "Show only " + name.ToLowerInvariant() };
            chip.AddToClassList("mxh-chip");
            var icon = new VectorIcon(glyph) { pickingMode = PickingMode.Ignore }; icon.AddToClassList("mxh-chip__icon"); chip.Add(icon);
            chip.Add(MixerUi.Text(name, "mxh-chip__text"));
            MixerUi.Press(chip, () => { set(!get()); SyncFilter(); MarkDirty(); });
            chips.Add(chip);
            chipButtons[name] = chip;
        }

        private void SyncFilter()
        {
            filter.Text = search;
            filter.Meshes = filterMeshes; filter.Skinned = filterSkinned; filter.Lights = filterLights; filter.Cameras = filterCameras;
            filter.Prefabs = filterPrefabs; filter.Missing = filterMissing; filter.Inactive = filterInactive;
            foreach (var pair in chipButtons)
            {
                bool on = pair.Key == "Meshes" ? filterMeshes : pair.Key == "Skinned" ? filterSkinned : pair.Key == "Lights" ? filterLights : pair.Key == "Cameras" ? filterCameras : pair.Key == "Prefabs" ? filterPrefabs : pair.Key == "Missing" ? filterMissing : filterInactive;
                pair.Value.EnableInClassList("mxh-chip--on", on);
            }
        }

        private void ApplyDensity()
        {
            list.SetRowHeight(Compact ? CompactRowHeight : RowHeight);
            root.EnableInClassList("mxh-root--compact", Compact);
            list.RefreshRows();
        }

        private void Tick()
        {
            if (!built || !visible) return;
            if (!dirty) return;
            // Hierarchy changes come in bursts (an import, a prefab apply): one rebuild per 60 ms is plenty.
            if (EditorApplication.timeSinceStartup - lastRebuild < 0.06) return;
            dirty = false;
            lastRebuild = EditorApplication.timeSinceStartup;
            Rebuild();
        }

        private void Rebuild()
        {
            model.Rebuild(filter);
            list.SetItemCount(model.Count, keepAtEnd: false);
            list.RefreshRows();
            int objects = model.Rows.Count(r => r.Object != null);
            footer.text = (filter.Any ? objects + " matching" : objects + (objects == 1 ? " object" : " objects")) + (Selection.gameObjects.Length > 0 ? " · " + Selection.gameObjects.Length + " selected" : "")
                          + (MixerLinkService.Connected ? " · Blender linked" : "");
        }

        /// <summary>Opens the way to an object and scrolls it on screen (tests, pings).</summary>
        internal void RevealObject(GameObject go)
        {
            if (go == null) return;
            model.Reveal(go);
            Rebuild();
            int index = model.IndexOf(go.GetInstanceID());
            if (index >= 0) list.Reveal(index);
            list.RefreshRows();
        }

        private void OnSelectionChanged()
        {
            if (!built) return;
            var go = Selection.activeGameObject;
            if (go != null && model.IndexOf(go.GetInstanceID()) < 0 && !filter.Any) { model.Reveal(go); dirty = true; Tick(); }
            int index = go != null ? model.IndexOf(go.GetInstanceID()) : -1;
            if (index >= 0) list.Reveal(index);
            list.RefreshRows();
            MarkDirty();
        }

        // ---- Rows ---------------------------------------------------------------------------------------------------

        private VisualElement MakeRow()
        {
            var row = MixerUi.Box("mxh-row");
            row.Add(MixerUi.Box("mxh-row__indent", PickingMode.Ignore));
            var chevron = MixerUi.Icon(IconGlyph.Chevron, "", null, "mxh-row__chevron");
            row.Add(chevron);
            var dot = MixerUi.Box("mxh-row__dot", PickingMode.Ignore);
            row.Add(dot);
            var icon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; icon.AddToClassList("mxh-row__icon"); row.Add(icon);
            row.Add(MixerUi.Text("", "mxh-row__name"));
            var rename = new TextField(); rename.AddToClassList("mxh-row__rename"); MixerUi.Show(rename, false); row.Add(rename);
            row.Add(MixerUi.Box("mxh-row__badges", PickingMode.Ignore));
            row.Add(MixerUi.Spacer());
            row.Add(MixerUi.Box("mxh-row__components", PickingMode.Ignore));
            var pick = MixerUi.Icon(IconGlyph.Lock, "Pickable in the Scene view", null, "mxh-row__pick");
            var eye = MixerUi.Icon(IconGlyph.Eye, "Visible in the Scene view", null, "mxh-row__eye");
            row.Add(pick);
            row.Add(eye);
            row.RegisterCallback<PointerDownEvent>(evt => OnRowPointerDown(row, evt));
            row.RegisterCallback<ClickEvent>(evt => { if (evt.clickCount == 2) OnRowDoubleClick(row); });
            chevron.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0) return; var r = RowOf(row); if (r != null && r.HasChildren) { if (r.IsScene) model.SetSceneFolded(r.Scene, r.Expanded); else model.SetExpanded(r.InstanceId, !r.Expanded, evt.altKey); MarkDirty(); } evt.StopPropagation(); }, TrickleDown.TrickleDown);
            eye.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0) return; var r = RowOf(row); if (r?.Object != null) { SceneVisibilityManager.instance.ToggleVisibility(r.Object, evt.altKey); MarkDirty(); } evt.StopPropagation(); }, TrickleDown.TrickleDown);
            pick.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0) return; var r = RowOf(row); if (r?.Object != null) { SceneVisibilityManager.instance.TogglePicking(r.Object, evt.altKey); MarkDirty(); } evt.StopPropagation(); }, TrickleDown.TrickleDown);
            rename.RegisterCallback<FocusOutEvent>(_ => CommitRename(row, rename, true));
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CommitRename(row, rename, true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { CommitRename(row, rename, false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            return row;
        }

        private HierarchyRow RowOf(VisualElement element) => model[list.ItemOf(element)];

        private void BindRow(VisualElement element, int index)
        {
            var row = model[index];
            if (row == null) return;
            var indent = element.Q(className: "mxh-row__indent");
            var chevron = element.Q(className: "mxh-row__chevron");
            var dot = element.Q(className: "mxh-row__dot");
            var icon = element.Q<Image>(className: "mxh-row__icon");
            var name = element.Q<Label>(className: "mxh-row__name");
            var rename = element.Q<TextField>(className: "mxh-row__rename");
            var badges = element.Q(className: "mxh-row__badges");
            var components = element.Q(className: "mxh-row__components");
            var pick = element.Q(className: "mxh-row__pick");
            var eye = element.Q(className: "mxh-row__eye");
            indent.style.width = Mathf.Max(0, row.Depth - 1) * 14f + (row.IsScene ? 0f : 4f);
            chevron.style.visibility = row.HasChildren ? Visibility.Visible : Visibility.Hidden;
            chevron.EnableInClassList("mxh-row__chevron--open", row.Expanded);
            element.EnableInClassList("mxh-row--scene", row.IsScene);
            element.EnableInClassList("mxh-row--selected", !row.IsScene && Selection.Contains(row.InstanceId));
            element.EnableInClassList("mxh-row--dim", !row.IsScene && (!row.Active || !row.Match));
            element.EnableInClassList("mxh-row--prefab", row.PrefabInstance);
            element.EnableInClassList("mxh-row--missing", row.PrefabMissing || row.MissingScript);
            element.EnableInClassList("mxh-row--hidden", row.Hidden);
            element.EnableInClassList("mxh-row--odd", index % 2 == 1);
            element.EnableInClassList("mxh-row--excluded", row.Excluded);
            name.text = row.IsScene ? row.Name + (row.Scene.isDirty ? " *" : "") : row.Name;
            name.EnableInClassList("mxh-row__name--active-scene", row.IsScene && row.Active);
            bool renaming = row.Object != null && row.InstanceId == renamingId;
            MixerUi.Show(name, !renaming);
            MixerUi.Show(rename, renaming);
            badges.Clear();
            components.Clear();
            if (row.IsScene)
            {
                icon.image = EditorGUIUtility.IconContent("SceneAsset Icon").image;
                MixerUi.Show(dot, false); MixerUi.Show(pick, false); MixerUi.Show(eye, false);
                return;
            }
            icon.image = row.PrefabInstance ? EditorGUIUtility.IconContent(row.PrefabMissing ? "PrefabModel Icon" : "Prefab Icon").image : EditorGUIUtility.IconContent("GameObject Icon").image;
            if (row.PrefabRoot) badges.Add(Badge("prefab", row.PrefabMissing ? "The prefab asset is missing" : "A prefab instance", "mxh-badge--prefab"));
            if (row.MissingScript) badges.Add(Badge("missing script", "A component's script is missing", "mxh-badge--missing"));
            if (row.MixerEdited) badges.Add(Badge("mixer", "Being edited in Mixer (working copy shown)", "mxh-badge--mixer"));
            if (ShowComponents && row.Object != null)
            {
                int shown = 0;
                foreach (var component in row.Object.GetComponents<Component>())
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
                if (evt.button == 0 && evt.clickCount == 2 && row.Scene.isLoaded) SceneManager.SetActiveScene(row.Scene);
                return;
            }
            if (evt.button == 1)
            {
                if (!Selection.Contains(row.InstanceId)) Select(index, false, false);
                return;
            }
            if (evt.button != 0) return;
            if (evt.shiftKey && anchorRow >= 0) Select(index, true, false);
            else if (evt.ctrlKey || evt.commandKey) Select(index, false, true);
            else if (!Selection.Contains(row.InstanceId)) Select(index, false, false);
            BeginDrag(index, evt);
        }

        private void OnRowDoubleClick(VisualElement element)
        {
            var row = RowOf(element);
            if (row?.Object == null) return;
            Selection.activeGameObject = row.Object;
            SceneView.FrameLastActiveSceneView();
        }

        private void Select(int index, bool range, bool toggle)
        {
            var row = model[index];
            if (row?.Object == null) return;
            if (range)
            {
                int from = Mathf.Min(anchorRow, index), to = Mathf.Max(anchorRow, index);
                var ids = new List<int>();
                for (int i = from; i <= to; i++) if (model[i]?.Object != null) ids.Add(model[i].InstanceId);
                Selection.instanceIDs = ids.ToArray();
            }
            else if (toggle)
            {
                var ids = Selection.instanceIDs.ToList();
                if (ids.Contains(row.InstanceId)) ids.Remove(row.InstanceId); else ids.Add(row.InstanceId);
                Selection.instanceIDs = ids.ToArray();
                anchorRow = index;
            }
            else
            {
                Selection.activeInstanceID = row.InstanceId;
                anchorRow = index;
            }
            list.RefreshRows();
        }

        private void OnKey(KeyDownEvent evt)
        {
            if (renamingId != 0) return;
            if (evt.target is VisualElement target && (target == searchField || searchField.Contains(target))) return;
            int current = Selection.activeGameObject != null ? model.IndexOf(Selection.activeGameObject.GetInstanceID()) : -1;
            switch (evt.keyCode)
            {
                case KeyCode.DownArrow: Step(current, 1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.UpArrow: Step(current, -1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.Home: Step(-1, 1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.End: Step(model.Count, -1, evt.shiftKey); evt.StopPropagation(); break;
                case KeyCode.RightArrow: if (current >= 0 && model[current].HasChildren) { if (!model[current].Expanded) model.SetExpanded(model[current].InstanceId, true, evt.altKey); else Step(current, 1, false); MarkDirty(); } evt.StopPropagation(); break;
                case KeyCode.LeftArrow:
                    if (current >= 0) { if (model[current].Expanded && model[current].HasChildren) model.SetExpanded(model[current].InstanceId, false, evt.altKey); else { var parent = model[current].Object.transform.parent; if (parent != null) Selection.activeGameObject = parent.gameObject; } MarkDirty(); }
                    evt.StopPropagation(); break;
                case KeyCode.F2: BeginRename(); evt.StopPropagation(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: BeginRename(); evt.StopPropagation(); break;
                case KeyCode.Delete: case KeyCode.Backspace: DeleteSelection(); evt.StopPropagation(); break;
                case KeyCode.F: if (!evt.ctrlKey) { SceneView.FrameLastActiveSceneView(); evt.StopPropagation(); } else { searchField.Q(TextField.textInputUssName)?.Focus(); evt.StopPropagation(); } break;
                case KeyCode.D: if (evt.ctrlKey || evt.commandKey) { Duplicate(); evt.StopPropagation(); } break;
                case KeyCode.A: if (evt.ctrlKey || evt.commandKey) { Selection.instanceIDs = model.Rows.Where(r => r.Object != null).Select(r => r.InstanceId).ToArray(); list.RefreshRows(); evt.StopPropagation(); } break;
                case KeyCode.Escape: Selection.activeObject = null; list.RefreshRows(); evt.StopPropagation(); break;
            }
        }

        private void OnCommand(ExecuteCommandEvent evt)
        {
            switch (evt.commandName)
            {
                case "Find": searchField.Q(TextField.textInputUssName)?.Focus(); evt.StopPropagation(); break;
                case "Duplicate": Duplicate(); evt.StopPropagation(); break;
                case "Delete": case "SoftDelete": DeleteSelection(); evt.StopPropagation(); break;
                case "SelectAll": Selection.instanceIDs = model.Rows.Where(r => r.Object != null).Select(r => r.InstanceId).ToArray(); list.RefreshRows(); evt.StopPropagation(); break;
            }
        }

        private void Step(int from, int direction, bool extend)
        {
            int index = from + direction;
            while (index >= 0 && index < model.Count && model[index].Object == null) index += direction;
            if (index < 0 || index >= model.Count) return;
            if (extend && anchorRow >= 0) Select(index, true, false); else { Select(index, false, false); }
            list.Reveal(index);
        }

        private void BeginRename()
        {
            var go = Selection.activeGameObject;
            if (go == null || model.IndexOf(go.GetInstanceID()) < 0) return;
            renamingId = go.GetInstanceID();
            list.RefreshRows();
        }

        private void CommitRename(VisualElement rowElement, TextField field, bool apply)
        {
            if (renamingId == 0) return;
            var go = EditorUtility.InstanceIDToObject(renamingId) as GameObject;
            renamingId = 0;
            if (apply && go != null && !string.IsNullOrWhiteSpace(field.value) && field.value != go.name)
            {
                Undo.RecordObject(go, "Rename " + go.name);
                go.name = field.value.Trim();
                EditorUtility.SetDirty(go);
            }
            MarkDirty();
            root.Focus();
        }

        private void DeleteSelection()
        {
            var objects = Selection.gameObjects.Where(g => g != null && g.scene.IsValid()).ToList();
            if (objects.Count == 0) return;
            foreach (var go in objects) if (go != null) Undo.DestroyObjectImmediate(go);
            toasts.Show(objects.Count == 1 ? "Deleted " + objects[0].name : "Deleted " + objects.Count + " objects");
            MarkDirty();
        }

        private void Duplicate()
        {
            if (Selection.gameObjects.Length == 0) return;
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            MarkDirty();
        }

        private void CreateEmpty(bool asChild)
        {
            var parent = Selection.activeGameObject;
            var go = new GameObject("GameObject");
            Undo.RegisterCreatedObjectUndo(go, "Create Empty");
            if (parent != null) { go.transform.SetParent(asChild ? parent.transform : parent.transform.parent, false); model.SetExpanded(parent.GetInstanceID(), true); }
            else if (SceneManager.GetActiveScene().IsValid()) SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
            Selection.activeGameObject = go;
            renamingId = go.GetInstanceID();
            MarkDirty();
        }
    }
}
