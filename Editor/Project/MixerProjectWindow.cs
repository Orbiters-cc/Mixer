using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The Mixer Project window: Unity's Project window rebuilt in Mixer's look. Folders on the left (with the assets used
    /// lately at the top); on the right the open folder as a tree that unfolds in place (fold all, unfold all), sorted by
    /// name or by the date things were added or changed, with a "new" chip on what came in the last day, as a list or an
    /// icon grid (the status bar's slider, as in Unity's window). The search finds
    /// names and what assets hold inside (parameters, states, animated properties, VRChat components' fields). The top bar
    /// makes a folder, a material, an animation or an animator controller in the open folder. A click on a folder never
    /// changes Unity's selection (the Inspector keeps what it shows); a click on a file selects it as Unity's does. Each
    /// asset's target button shows where it is used: the place itself when there is one, a side panel listing them all
    /// otherwise.
    /// </summary>
    internal sealed partial class MixerProjectWindow : EditorWindow, IHasCustomMenu
    {
        private const string StyleSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        private const string ProjectSheetPath = MixerInfo.BasePath + "/Editor/UI/project.uss";
        internal const string MenuPath = "Tools/Orbiters/Mixer Project";
        private const float RowHeight = 22f;

        [SerializeField] private string folder = "Assets";
        [SerializeField] private string search = string.Empty;
        [SerializeField] private bool showRecent;
        [SerializeField] private List<string> treeOpen = new List<string> { "Assets" };
        [SerializeField] private List<string> contentOpen = new List<string>();
        [SerializeField] private ProjectSort sort = ProjectSort.Name;
        [SerializeField] private ProjectKinds kinds = ProjectKinds.None;
        [SerializeField] private SearchScope scope = SearchScope.Assets;
        [SerializeField] private bool locked, favouritesFolded;
        // The icons' size in the grid; 0 is the list.
        [SerializeField] private float iconSize;
        [SerializeField] private float treeWidth = 230f;

        // Not kept across a script reload (Unity keeps a window's private fields): the rows must be built again first.
        [NonSerialized] private bool built, dirty = true, treeDirty = true, visible = true;
        private VisualElement root, panes, usagePanel;
        private TextField searchField;
        private MixerSearch searchOptions;
        private Button filterButton, sortButton;
        private Label filterCount, footer, emptyLabel;
        private VirtualList tree, content;
        private MixerToasts toasts;
        private double lastRebuild, lastFooter;
        private readonly List<Button> createButtons = new List<Button>();
        private readonly HashSet<string> treeOpenSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> contentOpenSet = new HashSet<string>(StringComparer.Ordinal);

        [MenuItem(MenuPath, priority = 2002)]
        public static void Open() => GetWindow<MixerProjectWindow>().Show();

        /// <summary>The folder the right pane shows (what the create buttons create in).</summary>
        internal string Folder => folder;

        private void OnEnable()
        {
            titleContent = new GUIContent("Project", MixerLogo.TabIcon(), "Mixer Project");
            minSize = new Vector2(320f, 180f);
            EditorApplication.projectChanged += MarkAllDirty;
            Selection.selectionChanged += OnSelectionChanged;
            RecentAssets.Changed += OnRecentChanged;
            AssetContentIndex.Updated += OnIndexUpdated;
            ProjectFavourites.Changed += OnFavouritesChanged;
            ProjectPreviews.Changed += OnAssetsChanged;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= MarkAllDirty;
            Selection.selectionChanged -= OnSelectionChanged;
            RecentAssets.Changed -= OnRecentChanged;
            AssetContentIndex.Updated -= OnIndexUpdated;
            ProjectFavourites.Changed -= OnFavouritesChanged;
            ProjectPreviews.Changed -= OnAssetsChanged;
            // A background tab never built its rows: its folds are still the ones kept.
            if (!built) return;
            treeOpen = treeOpenSet.ToList();
            contentOpen = contentOpenSet.ToList();
        }

        private void OnBecameVisible() { visible = true; MarkAllDirty(); }
        private void OnBecameInvisible() => visible = false;
        private void MarkDirty() => dirty = true;
        private void MarkAllDirty() => dirty = treeDirty = true;

        // Only the pictures of what changed are made again.
        private void OnAssetsChanged(IEnumerable<string> paths)
        {
            foreach (string path in paths) previewCache.Remove(path);
        }
        private void OnRecentChanged() { if (showRecent) MarkDirty(); }
        private void OnIndexUpdated() { if (Searching) MarkDirty(); }
        private bool Searching => !string.IsNullOrWhiteSpace(search);

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open Unity's Project window"), false, ProjectReplacement.OpenUnityProject);
            menu.AddItem(new GUIContent("Replace Unity's Project window"), ProjectReplacement.Enabled, () => ProjectReplacement.SetEnabled(!ProjectReplacement.Enabled));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Lock"), locked, () => locked = !locked);
            menu.AddItem(new GUIContent("Read the project's contents again"), false, () => { AssetContentIndex.MarkDirty(); AssetContentIndex.Refresh(); });
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            root.AddToClassList("mxp-root");
            foreach (string path in new[] { StyleSheetPath, ProjectSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }
            root.focusable = true;
            treeOpenSet.Clear(); treeOpenSet.UnionWith(treeOpen ?? new List<string>());
            treeOpenSet.Add("Assets");
            contentOpenSet.Clear(); contentOpenSet.UnionWith(contentOpen ?? new List<string>());
            if (!showRecent && folder != PackagesPath && !AssetDatabase.IsValidFolder(folder)) folder = "Assets";
            BuildTopBar();
            var split = new TwoPaneSplitView(0, Mathf.Clamp(treeWidth, 140f, 600f), TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("mxp-split");
            root.Add(split);
            var left = MixerUi.Box("mxp-left");
            left.RegisterCallback<GeometryChangedEvent>(evt => { if (evt.newRect.width > 0) treeWidth = evt.newRect.width; });
            tree = new VirtualList(RowHeight, MakeTreeRow, BindTreeRow);
            tree.AddToClassList("mxp-tree");
            left.Add(tree);
            split.Add(left);
            var right = MixerUi.Box("mxp-right");
            split.Add(right);
            right.Add(BuildPathBar());
            panes = MixerUi.Box("mxp-panes");
            right.Add(panes);
            var column = MixerUi.Box("mxp-column");
            panes.Add(column);
            content = new VirtualList(RowHeight, MakeContentRow, BindContentRow);
            content.SpanOf = index => ContentSpan(index);
            content.AddToClassList("mxp-content");
            column.Add(content);
            column.Add(BuildGrid());
            emptyLabel = MixerUi.Text("", "mxp-empty");
            emptyLabel.pickingMode = PickingMode.Ignore;
            column.Add(emptyLabel);
            usagePanel = BuildUsagePanel();
            panes.Add(usagePanel);
            HookContent();
            root.Add(BuildStatusBar());
            toasts = new MixerToasts();
            root.Add(toasts);
            root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            root.RegisterCallback<ValidateCommandEvent>(OnValidateCommand);
            root.RegisterCallback<ExecuteCommandEvent>(OnCommand);
            root.schedule.Execute(Tick).Every(100);
            ApplyViewMode();
            built = true;
            dirty = treeDirty = true;
            AssetContentIndex.Refresh();
        }

        private void BuildTopBar()
        {
            var bar = MixerUi.Box("mxp-topbar");
            root.Add(bar);
            searchField = new TextField { value = search };
            searchField.AddToClassList("mx-field");
            searchField.AddToClassList("mxp-search");
            searchField.tooltip = "Search names and what assets hold inside: parameters, states, layers, animated properties and blendshapes, VRChat components' fields. t:Material and other Unity filters work too (not in a regular expression, which is matched against file names and contents).";
            searchField.RegisterValueChangedCallback(evt => { search = evt.newValue; MarkDirty(); });
            searchField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) { searchField.value = string.Empty; root.Focus(); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.DownArrow) { root.Focus(); evt.StopPropagation(); }
            });
            var searchIcon = new VectorIcon(IconGlyph.Search); searchIcon.AddToClassList("mxp-search__icon");
            searchField.Add(searchIcon);
            filterButton = new Button { text = string.Empty, tooltip = "Show only some kinds of assets" };
            filterButton.AddToClassList("mxp-filter");
            var filterIcon = new VectorIcon(IconGlyph.Sliders) { pickingMode = PickingMode.Ignore }; filterIcon.AddToClassList("mxp-filter__icon"); filterButton.Add(filterIcon);
            filterButton.Add(MixerUi.Text("Filter", "mxp-filter__text"));
            filterCount = MixerUi.Text("", "mxp-filter__count");
            filterButton.Add(filterCount);
            MixerUi.Press(filterButton, ShowFilterMenu);
            starButton = MixerUi.Icon(IconGlyph.Star, "Save this search to Favourites", ToggleSavedSearch, "mxp-star");
            searchOptions = new MixerSearch(searchField, "Project", () => { if (Searching) RebuildNow(); }, starButton, filterButton);
            bar.Add(searchField);
            sortButton = new Button { text = string.Empty };
            sortButton.AddToClassList("mxp-sort");
            var sortIcon = new VectorIcon(IconGlyph.Sort) { pickingMode = PickingMode.Ignore }; sortIcon.AddToClassList("mxp-sort__icon"); sortButton.Add(sortIcon);
            sortButton.Add(MixerUi.Text("", "mxp-sort__text"));
            MixerUi.Press(sortButton, ShowSortMenu);
            bar.Add(sortButton);
            var create = MixerUi.Box("mxp-create");
            // As Unity's "+": everything that can be created; the four buttons after it are the usual ones.
            addButton = new Button { text = string.Empty, tooltip = "Create: everything Unity and your tools can make, in the highlighted folder (else the open one)" };
            addButton.AddToClassList("mxp-add");
            var addIcon = new VectorIcon(IconGlyph.Plus) { pickingMode = PickingMode.Ignore }; addIcon.AddToClassList("mxp-add__icon"); addButton.Add(addIcon);
            var addChevron = new VectorIcon(IconGlyph.Chevron) { pickingMode = PickingMode.Ignore }; addChevron.AddToClassList("mxp-add__chevron"); addButton.Add(addChevron);
            MixerUi.Press(addButton, ShowCreateMenu);
            create.Add(addButton);
            create.Add(CreateButton("Folder Icon", "folder", () => CreateFolder()));
            create.Add(CreateButton("Material Icon", "material", () => CreateMaterial()));
            create.Add(CreateButton("AnimationClip Icon", "animation", () => CreateAnimation()));
            create.Add(CreateButton("AnimatorController Icon", "animator controller", () => CreateAnimator()));
            bar.Add(create);
            SyncTopBar();
        }

        private Button CreateButton(string icon, string what, Action action)
        {
            var button = new Button { text = string.Empty, userData = what };
            createButtons.Add(button);
            button.AddToClassList("mxp-create__button");
            var image = new Image { image = EditorGUIUtility.IconContent(icon).image, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.AddToClassList("mxp-create__icon");
            button.Add(image);
            var plus = new VectorIcon(IconGlyph.Plus) { pickingMode = PickingMode.Ignore };
            plus.AddToClassList("mxp-create__plus");
            button.Add(plus);
            MixerUi.Press(button, action);
            return button;
        }

        private void ShowFilterMenu()
        {
            var menu = new GenericMenu();
            void Kind(string name, ProjectKinds kind) => menu.AddItem(new GUIContent(name), (kinds & kind) != 0, () => { kinds ^= kind; SyncTopBar(); RebuildNow(); });
            Kind("Scenes", ProjectKinds.Scenes);
            Kind("Prefabs", ProjectKinds.Prefabs);
            Kind("Models", ProjectKinds.Models);
            Kind("Materials", ProjectKinds.Materials);
            Kind("Textures", ProjectKinds.Textures);
            Kind("Animations", ProjectKinds.Animations);
            Kind("Animators and masks", ProjectKinds.Animators);
            Kind("Audio", ProjectKinds.Audio);
            Kind("Scripts", ProjectKinds.Scripts);
            Kind("Shaders", ProjectKinds.Shaders);
            Kind("Other assets", ProjectKinds.Other);
            // Unity's asset labels: each adds or removes its l: filter in the search.
            var labels = Labels();
            if (labels.Count > 0)
            {
                menu.AddSeparator("");
                foreach (string label in labels)
                {
                    string l = label;
                    menu.AddItem(new GUIContent("Labels/" + l), HasLabelToken(l), () => ToggleLabel(l));
                }
            }
            menu.AddSeparator("");
            if (kinds != ProjectKinds.None) menu.AddItem(new GUIContent("Show everything"), false, () => { kinds = ProjectKinds.None; SyncTopBar(); RebuildNow(); });
            else menu.AddDisabledItem(new GUIContent("Show everything"));
            menu.DropDown(filterButton.worldBound);
        }

        private void ShowSortMenu()
        {
            var menu = new GenericMenu();
            void Sort(string name, ProjectSort value) => menu.AddItem(new GUIContent(name), sort == value, () => { sort = value; SyncTopBar(); RebuildNow(); });
            Sort("Name", ProjectSort.Name);
            Sort("Date added (newest first)", ProjectSort.Added);
            Sort("Date modified (newest first)", ProjectSort.Modified);
            menu.DropDown(sortButton.worldBound);
        }

        private void SyncTopBar()
        {
            if (filterButton == null) return;
            int count = 0;
            foreach (ProjectKinds k in Enum.GetValues(typeof(ProjectKinds))) if (k != ProjectKinds.None && (kinds & k) != 0) count++;
            filterButton.EnableInClassList("mxp-filter--on", count > 0);
            filterCount.text = count > 0 ? count.ToString() : string.Empty;
            MixerUi.Show(filterCount, count > 0);
            sortButton.Q<Label>().text = sort == ProjectSort.Name ? "Name" : sort == ProjectSort.Added ? "Added" : "Modified";
            sortButton.tooltip = "Sorted by " + (sort == ProjectSort.Name ? "name" : sort == ProjectSort.Added ? "the date things were added, newest first" : "the date things changed, newest first");
        }

        private void Tick()
        {
            if (!built) return;
            // The open folder was deleted or moved by another tool: back to Assets rather than creating into nothing.
            if (!showRecent && folder != PackagesPath && !AssetDatabase.IsValidFolder(folder)) Navigate("Assets");
            PollPreviews();
            double now = EditorApplication.timeSinceStartup;
            if (pingPath != null && now > pingUntil) { pingPath = null; RefreshContent(); }
            if (AssetContentIndex.Building && now - lastFooter > 0.5) UpdateFooter();
            if (!visible || !(dirty || treeDirty)) return;
            // Imports come in bursts and every key typed in the search asks again: one rebuild per 80 ms (120 searching).
            if (now - lastRebuild < (Searching ? 0.12 : 0.08)) return;
            RebuildNow();
        }

        // What the user did (a fold, a sort, a create) shows at once, without waiting for the burst throttle.
        private void RebuildNow()
        {
            if (!built) return;
            dirty = false;
            lastRebuild = EditorApplication.timeSinceStartup;
            if (treeDirty) RebuildTree(); else tree.RefreshRows();
            RebuildContent();
            RebuildPathBar();
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (footer == null) return;
            lastFooter = EditorApplication.timeSinceStartup;
            int files = 0, folders = 0;
            foreach (var row in rows) { if (row.IsHeading || row.IsSubAsset) continue; if (row.IsFolder) folders++; else files++; }
            SyncStar();
            string what = (folders > 0 ? folders + (folders == 1 ? " folder · " : " folders · ") : "") + files + (files == 1 ? " file" : " files");
            // One asset lit up: where it is, as Unity's status bar shows it.
            string only = picked.Count == 1 ? picked.First() : null;
            string where = only == null ? null : only.IndexOf(ProjectFiles.SubAssetMark) >= 0 ? only.Substring(0, only.IndexOf(ProjectFiles.SubAssetMark)) + " › " + (ObjectOf(only)?.name ?? "") : only;
            footer.text = (where != null ? where + "  ·  " : "") + what + (picked.Count > 1 ? " · " + picked.Count + " selected" : "")
                          + (AssetContentIndex.Building ? " · reading the assets' contents, " + AssetContentIndex.Progress : "");
            string target = CreateTarget();
            bool writable = ProjectFiles.IsWritable(target);
            foreach (var button in createButtons)
            {
                button.SetEnabled(writable);
                button.tooltip = writable ? "New " + button.userData + " in " + target : "This package cannot be changed: open a folder of Assets to create a " + button.userData;
            }
        }

        /// <summary>Shows a folder on the right (and opens the way to it in the tree, the folder itself staying as it was).</summary>
        internal void Navigate(string path)
        {
            if (string.IsNullOrEmpty(path) || (path != PackagesPath && !AssetDatabase.IsValidFolder(path))) return;
            if (path != folder || showRecent) { picked.Clear(); cursor = anchor = null; }
            folder = path;
            showRecent = false;
            if (Searching && searchField != null) searchField.SetValueWithoutNotify(search = string.Empty);
            for (string p = ParentOf(path); !string.IsNullOrEmpty(p); p = ParentOf(p)) treeOpenSet.Add(p);
            if (path.StartsWith(PackagesPath + "/", StringComparison.Ordinal)) treeOpenSet.Add(PackagesPath);
            treeDirty = true;
            content?.SetFirst(0);
            grid?.SetFirst(0);
            RebuildNow();
        }

        /// <summary>Searches as typing in the field does, at once (tests).</summary>
        internal void SetSearch(string text)
        {
            search = text ?? string.Empty;
            searchField?.SetValueWithoutNotify(search);
            RebuildNow();
        }

        /// <summary>Searches with the field's switches set (tests).</summary>
        internal void SetSearch(string text, bool regex, bool matchCase)
        {
            searchOptions?.Set(regex, matchCase);
            SetSearch(text);
        }

        /// <summary>Where the search looks (tests; the path bar's menu does the same).</summary>
        internal void SetScope(SearchScope value)
        {
            scope = value;
            RebuildNow();
        }

        /// <summary>Shows the recent assets on the right (the tree's Recent line).</summary>
        internal void ShowRecent()
        {
            showRecent = true;
            if (Searching && searchField != null) searchField.SetValueWithoutNotify(search = string.Empty);
            content?.SetFirst(0);
            grid?.SetFirst(0);
            RebuildNow();
        }

        internal static string ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : null;
        }

        // Assets selected elsewhere (another window, a tool, Unity's create menus) are the selection here too; a single one
        // off screen is shown (its folder opened), unless a search is on screen.
        private void OnSelectionChanged()
        {
            if (!built) return;
            if (!IsOurSelection())
            {
                pushed = null;
                picked.Clear();
                foreach (string guid in Selection.assetGUIDs)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path)) picked.Add(path);
                }
                string active = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
                // Locked, the window stays where it is (as Unity's).
                if (!string.IsNullOrEmpty(active) && !locked)
                {
                    cursor = anchor = active;
                    if (!creating && !Searching && IndexOf(active) < 0 && Selection.assetGUIDs.Length == 1 && !AssetDatabase.IsValidFolder(active))
                    {
                        string parent = ParentOf(active);
                        if (parent != null && (showRecent || !active.StartsWith(folder + "/", StringComparison.Ordinal))) Navigate(parent);
                    }
                    Reveal(active);
                }
            }
            RefreshRows();
            SyncUsageRows();
            UpdateFooter();
        }

        private void RefreshRows()
        {
            tree?.RefreshRows();
            RefreshContent();
        }
    }
}
