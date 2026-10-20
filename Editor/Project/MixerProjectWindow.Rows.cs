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
    // The two lists: the folder tree on the left; on the right the open folder (folders unfolding in place), the recent
    // assets or a search; the path bar above it.
    internal sealed partial class MixerProjectWindow
    {
        internal const string RecentPath = "\u0001recent";
        internal const string PackagesPath = "Packages";
        private const int UnfoldLimit = 1500;
        private const int NameLimit = 400, InsideLimit = 300;

        private readonly List<ProjectItem> treeRows = new List<ProjectItem>();
        private readonly List<ProjectItem> rows = new List<ProjectItem>();
        private readonly Dictionary<string, string> packageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture> previewCache = new Dictionary<string, Texture>(StringComparer.Ordinal);
        private VisualElement crumbs;
        private Button unfoldButton, foldButton;
        private bool previewsWaiting;
        private double lastPreviewPoll;

        /// <summary>The lines on the right (tests).</summary>
        internal IReadOnlyList<ProjectItem> Rows => rows;

        private ProjectItem Row(int index) => index >= 0 && index < rows.Count ? rows[index] : null;
        private ProjectItem TreeRow(int index) => index >= 0 && index < treeRows.Count ? treeRows[index] : null;

        /// <summary>The line showing <paramref name="path"/> on the right, or -1.</summary>
        internal int IndexOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            for (int i = 0; i < rows.Count; i++) if (!rows[i].IsHeading && rows[i].Path == path) return i;
            return -1;
        }

        private static ProjectItem Heading(string text) => new ProjectItem { IsHeading = true, Name = text, Path = string.Empty };

        // ---- The folder tree ----------------------------------------------------------------------------------------

        private void RebuildTree()
        {
            treeDirty = false;
            treeRows.Clear();
            treeRows.Add(new ProjectItem { Path = RecentPath, Name = "Recent" });
            AddTreeFolder("Assets", "Assets", 0);
            var packages = ProjectFiles.Packages();
            packageNames.Clear();
            foreach (var (path, name) in packages) packageNames[path] = name;
            bool open = treeOpenSet.Contains(PackagesPath);
            treeRows.Add(new ProjectItem { Path = PackagesPath, Name = "Packages", IsFolder = true, HasChildren = packages.Count > 0, Expanded = open && packages.Count > 0 });
            if (open) foreach (var (path, name) in packages) AddTreeFolder(path, name, 1);
            tree.SetItemCount(treeRows.Count, keepAtEnd: false);
            tree.RefreshRows();
        }

        private void AddTreeFolder(string path, string name, int depth)
        {
            var children = treeOpenSet.Contains(path) ? ProjectFiles.Folders(path) : null;
            bool hasChildren = children != null ? children.Count > 0 : ProjectFiles.HasChildren(path, foldersOnly: true);
            treeRows.Add(new ProjectItem { Path = path, Name = name, IsFolder = true, Depth = depth, HasChildren = hasChildren, Expanded = children != null && hasChildren, Empty = !hasChildren && !ProjectFiles.HasChildren(path, false) });
            if (children == null) return;
            foreach (string child in children) AddTreeFolder(child, System.IO.Path.GetFileName(child), depth + 1);
        }

        private void ToggleTree(string path, bool open, bool recursive)
        {
            if (open) treeOpenSet.Add(path); else treeOpenSet.Remove(path);
            if (recursive)
            {
                var below = path == PackagesPath ? packageNames.Keys.ToList() : ProjectFiles.FoldersBelow(path, UnfoldLimit);
                foreach (string sub in below) { if (open) treeOpenSet.Add(sub); else treeOpenSet.Remove(sub); }
            }
            treeDirty = true;
            RebuildNow();
        }

        private VisualElement MakeTreeRow()
        {
            var row = MixerUi.Box("mxp-trow");
            row.Add(MixerUi.Box("mxp-trow__indent", PickingMode.Ignore));
            row.Add(MixerUi.Icon(IconGlyph.Chevron, "", null, evt =>
            {
                var item = TreeRow(tree.ItemOf(row));
                if (item != null && item.HasChildren) ToggleTree(item.Path, !item.Expanded, evt.altKey);
                evt.StopPropagation();
            }, "mxp-trow__chevron"));
            var icon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mxp-trow__icon");
            row.Add(icon);
            var glyph = new VectorIcon(IconGlyph.Clock);
            glyph.AddToClassList("mxp-trow__glyph");
            row.Add(glyph);
            row.Add(MixerUi.Text("", "mxp-trow__name"));
            row.RegisterCallback<PointerDownEvent>(evt => OnTreePointerDown(row, evt));
            return row;
        }

        private void BindTreeRow(VisualElement element, int index)
        {
            var item = TreeRow(index);
            if (item == null) return;
            bool recent = item.Path == RecentPath;
            element.Q(className: "mxp-trow__indent").style.width = item.Depth * 12f;
            var chevron = element.Q(className: "mxp-trow__chevron");
            chevron.style.visibility = item.HasChildren ? Visibility.Visible : Visibility.Hidden;
            chevron.EnableInClassList("mxp-trow__chevron--open", item.Expanded);
            var icon = element.Q<Image>(className: "mxp-trow__icon");
            MixerUi.Show(icon, !recent);
            MixerUi.Show(element.Q(className: "mxp-trow__glyph"), recent);
            if (!recent) icon.image = FolderIcon(item.Expanded, item.Empty);
            element.Q<Label>(className: "mxp-trow__name").text = item.Name;
            bool current = !Searching && (recent ? showRecent : !showRecent && item.Path == folder);
            element.EnableInClassList("mxp-trow--current", current);
            element.EnableInClassList("mxp-trow--root", item.Depth == 0);
            element.EnableInClassList("mxp-trow--drop", dropFolder != null && item.Path == dropFolder);
            element.tooltip = recent ? "The assets used lately: selected or opened, anywhere in the editor" : item.Depth == 1 && packageNames.ContainsKey(item.Path) ? item.Path : string.Empty;
        }

        private void OnTreePointerDown(VisualElement element, PointerDownEvent evt)
        {
            var item = TreeRow(tree.ItemOf(element));
            if (item == null) return;
            root.Focus();
            if (evt.button != 0) return;
            if (item.Path == RecentPath) { ShowRecent(); return; }
            // As Unity's tree: a click opens the folder on the right, a double click folds or unfolds it here.
            if (evt.clickCount == 2 && item.HasChildren) { ToggleTree(item.Path, !item.Expanded, evt.altKey); return; }
            Navigate(item.Path);
        }

        internal static Texture FolderIcon(bool open, bool empty = false) =>
            EditorGUIUtility.IconContent(empty ? "FolderEmpty Icon" : open ? "FolderOpened Icon" : "Folder Icon").image;

        // ---- The right pane -----------------------------------------------------------------------------------------

        private void RebuildContent()
        {
            rows.Clear();
            if (Searching) BuildSearchRows();
            else if (showRecent) BuildRecentRows();
            else if (folder == PackagesPath) AddItems(PackageItems(), 0);
            else AddItems(ProjectFiles.Children(folder, sort, kinds), 0);
            content.SetItemCount(rows.Count, keepAtEnd: false);
            content.RefreshRows();
            if (GridMode) LayOutGrid();
            bool empty = rows.Count == 0;
            MixerUi.Show(emptyLabel, empty);
            if (empty)
                emptyLabel.text = Searching ? (AssetContentIndex.Building ? "Nothing yet: the assets' contents are still being read." : "Nothing matches “" + search.Trim() + "”.")
                    : showRecent ? "The assets you select or open show here."
                    : kinds != ProjectKinds.None ? "Nothing of the kinds the filter keeps." : "This folder is empty.";
        }

        private List<ProjectItem> PackageItems()
        {
            var items = new List<ProjectItem>();
            foreach (var (path, name) in ProjectFiles.Packages())
            {
                var item = ProjectFiles.Item(path);
                item.Name = name;
                item.IsFolder = true;
                items.Add(item);
            }
            return items;
        }

        private void AddItems(List<ProjectItem> items, int depth)
        {
            foreach (var item in items)
            {
                item.Depth = depth;
                // The grid shows the open folder's own contents; the list unfolds folders in place.
                item.Expanded = !GridMode && item.IsFolder && item.HasChildren && contentOpenSet.Contains(item.Path);
                rows.Add(item);
                if (item.Expanded) AddItems(ProjectFiles.Children(item.Path, sort, kinds), depth + 1);
            }
        }

        private void BuildRecentRows()
        {
            string section = null;
            var today = DateTime.Now.Date;
            foreach (var (path, when) in RecentAssets.List())
            {
                var item = ProjectFiles.Item(path);
                if (!ProjectFiles.Keeps(kinds, item)) continue;
                item.Used = when;
                var day = when.ToLocalTime().Date;
                string heading = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day > today.AddDays(-7) ? "This week" : "Earlier";
                if (heading != section) { rows.Add(Heading(heading)); section = heading; }
                rows.Add(item);
            }
        }

        // Names first (Unity's search: t:Material and the other filters work), then what assets hold inside.
        private void BuildSearchRows()
        {
            string query = search.Trim();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = new List<ProjectItem>();
            string[] guids;
            try { guids = includePackages ? AssetDatabase.FindAssets(query) : AssetDatabase.FindAssets(query, new[] { "Assets" }); }
            catch (Exception) { guids = Array.Empty<string>(); }
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                var item = ProjectFiles.Item(path);
                if (!item.IsFolder && !ProjectFiles.Keeps(kinds, item)) continue;
                names.Add(item);
                if (names.Count >= NameLimit) break;
            }
            ProjectFiles.Sort(names, sort);
            // The words of the query (Unity's "t:" and "l:" filters left out) searched in the assets' contents.
            string words = string.Join(" ", query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.IndexOf(':') < 0));
            var inside = new List<ProjectItem>();
            if (words.Length >= 2)
            {
                foreach (var hit in AssetContentIndex.Search(words, includePackages, InsideLimit))
                {
                    if (seen.Contains(hit.Path)) continue;
                    var item = ProjectFiles.Item(hit.Path);
                    if (!ProjectFiles.Keeps(kinds, item)) continue;
                    item.MatchLabel = hit.Label;
                    item.MatchValue = hit.Value;
                    inside.Add(item);
                }
                ProjectFiles.Sort(inside, sort);
            }
            if (names.Count > 0)
            {
                rows.Add(Heading("Names · " + names.Count + (names.Count >= NameLimit ? "+" : "")));
                rows.AddRange(names);
            }
            if (inside.Count > 0 || (words.Length >= 2 && AssetContentIndex.Building))
            {
                rows.Add(Heading("Inside assets · " + inside.Count + (inside.Count >= InsideLimit ? "+" : "") + (AssetContentIndex.Building ? " · still reading " + AssetContentIndex.Progress : "")));
                rows.AddRange(inside);
            }
        }

        // Rows with a second line: a search's (where it is, what matched), the recent ones (where).
        private int ContentSpan(int index)
        {
            var item = Row(index);
            if (item == null || item.IsHeading) return 1;
            return Searching || showRecent ? 2 : 1;
        }

        private VisualElement MakeContentRow()
        {
            var row = MixerUi.Box("mxp-row");
            row.Add(MixerUi.Box("mxp-row__indent", PickingMode.Ignore));
            row.Add(MixerUi.Icon(IconGlyph.Chevron, "", null, evt =>
            {
                var item = Row(content.ItemOf(row));
                if (item != null && item.IsFolder && item.HasChildren) SetUnfolded(item.Path, !item.Expanded, evt.altKey);
                evt.StopPropagation();
            }, "mxp-row__chevron"));
            var picture = MixerUi.Box("mxp-row__picture", PickingMode.Ignore);
            var icon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mxp-row__icon");
            picture.Add(icon);
            row.Add(picture);
            var body = MixerUi.Box("mxp-row__body", PickingMode.Ignore);
            row.Add(body);
            var line = MixerUi.Box("mxp-row__line", PickingMode.Ignore);
            body.Add(line);
            line.Add(MixerUi.Text("", "mxp-row__name"));
            line.Add(MixerUi.Text("", "mxp-row__ext"));
            var chip = MixerUi.Text("new", "mxp-new");
            chip.tooltip = "Added in the last 24 hours";
            chip.pickingMode = PickingMode.Position;
            line.Add(chip);
            var rename = new TextField();
            rename.AddToClassList("mxp-row__rename");
            MixerUi.Show(rename, false);
            line.Add(rename);
            line.Add(MixerUi.Spacer());
            var date = MixerUi.Text("", "mxp-row__date");
            date.pickingMode = PickingMode.Position;
            line.Add(date);
            line.Add(MixerUi.Icon(IconGlyph.Target, "Where it is used: in the open scenes and by other assets", null, evt =>
            {
                var item = Row(content.ItemOf(row));
                if (item != null && !item.IsHeading && !item.IsFolder) ShowUsages(item.Path);
                evt.StopPropagation();
            }, "mxp-row__target"));
            var line2 = MixerUi.Box("mxp-row__line2", PickingMode.Ignore);
            line2.Add(MixerUi.Text("", "mxp-row__match"));
            line2.Add(MixerUi.Text("", "mxp-row__detail"));
            body.Add(line2);
            row.RegisterCallback<PointerDownEvent>(evt => OnRowPointerDown(row, evt));
            rename.RegisterCallback<FocusOutEvent>(_ => CommitRename(rename, true));
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CommitRename(rename, true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { CommitRename(rename, false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            return row;
        }

        private void BindContentRow(VisualElement element, int index)
        {
            var item = Row(index);
            if (item == null) return;
            var chevron = element.Q(className: "mxp-row__chevron");
            var picture = element.Q(className: "mxp-row__picture");
            var icon = element.Q<Image>(className: "mxp-row__icon");
            var name = element.Q<Label>(className: "mxp-row__name");
            var ext = element.Q<Label>(className: "mxp-row__ext");
            var chip = element.Q(className: "mxp-new");
            var rename = element.Q<TextField>(className: "mxp-row__rename");
            var date = element.Q<Label>(className: "mxp-row__date");
            var target = element.Q(className: "mxp-row__target");
            var line2 = element.Q(className: "mxp-row__line2");
            var match = element.Q<Label>(className: "mxp-row__match");
            var detail = element.Q<Label>(className: "mxp-row__detail");
            bool heading = item.IsHeading;
            element.EnableInClassList("mxp-row--heading", heading);
            name.text = item.Name;
            MixerUi.Show(chevron, !heading);
            MixerUi.Show(picture, !heading);
            if (heading)
            {
                element.Q(className: "mxp-row__indent").style.width = 0f;
                foreach (var e in new[] { ext, chip, rename, date, target, line2 }) MixerUi.Show(e, false);
                MixerUi.Show(name, true);
                element.RemoveFromClassList("mxp-row--picked");
                element.RemoveFromClassList("mxp-row--cursor");
                element.RemoveFromClassList("mxp-row--drop");
                return;
            }
            bool tall = ContentSpan(index) > 1;
            element.EnableInClassList("mxp-row--tall", tall);
            element.Q(className: "mxp-row__indent").style.width = item.Depth * 14f;
            chevron.style.visibility = item.IsFolder && item.HasChildren ? Visibility.Visible : Visibility.Hidden;
            chevron.EnableInClassList("mxp-row__chevron--open", item.Expanded);
            icon.image = IconOf(item);
            ext.text = item.IsFolder ? string.Empty : item.Extension;
            MixerUi.Show(chip, item.IsNew);
            MixerUi.Show(date, true);
            date.text = DateText(item);
            date.tooltip = item.Added == default ? string.Empty
                : "Added " + item.Added.ToLocalTime().ToString("g") + "\nModified " + item.Modified.ToLocalTime().ToString("g") + (item.Used != default ? "\nUsed " + item.Used.ToLocalTime().ToString("g") : "");
            MixerUi.Show(target, !item.IsFolder);
            target.EnableInClassList("mxp-row__target--on", usagePath == item.Path);
            target.EnableInClassList("mxp-row__target--busy", usageBusy && usagePath == item.Path);
            element.EnableInClassList("mxp-row--picked", picked.Contains(item.Path));
            element.EnableInClassList("mxp-row--cursor", item.Path == cursor);
            element.EnableInClassList("mxp-row--drop", item.IsFolder && dropFolder != null && item.Path == dropFolder);
            element.EnableInClassList("mxp-row--odd", index % 2 == 1);
            element.EnableInClassList("mxp-row--folder", item.IsFolder);
            element.EnableInClassList("mxp-row--ping", pingPath != null && item.Path == pingPath);
            MixerUi.Show(line2, tall);
            if (tall)
            {
                MixerUi.Show(match, item.MatchLabel != null);
                match.text = item.MatchLabel ?? string.Empty;
                detail.text = Detail(item);
            }
            bool renaming = renamingPath != null && item.Path == renamingPath;
            MixerUi.Show(name, !renaming);
            MixerUi.Show(ext, !renaming && !item.IsFolder);
            MixerUi.Show(rename, renaming);
            var previous = rename.userData as string;
            rename.userData = renaming ? item.Path : null;
            if (renaming && previous != item.Path)
            {
                rename.SetValueWithoutNotify(item.Name);
                rename.schedule.Execute(() => { rename.Q(TextField.textInputUssName)?.Focus(); rename.SelectAll(); }).StartingIn(10);
            }
        }

        private string DateText(ProjectItem item)
        {
            if (showRecent && !Searching) return ProjectFiles.Ago(item.Used);
            return ProjectFiles.Ago(sort == ProjectSort.Added ? item.Added : item.Modified);
        }

        private string Detail(ProjectItem item)
        {
            string where = ParentOf(item.Path) ?? string.Empty;
            if (where.StartsWith(PackagesPath + "/", StringComparison.Ordinal))
            {
                foreach (var pair in packageNames)
                    if (where == pair.Key || where.StartsWith(pair.Key + "/", StringComparison.Ordinal)) { where = pair.Value + where.Substring(pair.Key.Length); break; }
            }
            return item.MatchLabel != null ? item.MatchValue + "  ·  " + where : where;
        }

        // "Material · 12 KB", "Folder".
        private static string KindAndSize(ProjectItem item)
        {
            if (item.IsFolder) return "Folder";
            var type = AssetDatabase.GetMainAssetTypeAtPath(item.Path);
            return (type != null ? ObjectNames.NicifyVariableName(type.Name) : "File") + (item.Size > 0 ? "  ·  " + MixerUi.Bytes(item.Size) : "");
        }

        private static Texture IconOf(ProjectItem item)
        {
            if (item.IsFolder) return FolderIcon(item.Expanded, !item.HasChildren);
            return AssetDatabase.GetCachedIcon(item.Path) ?? EditorGUIUtility.IconContent("DefaultAsset Icon").image;
        }

        // A picture of the asset (a material's ball, a texture, a model) once Unity made one; its icon meanwhile.
        private Texture PreviewOf(ProjectItem item)
        {
            if (item.IsFolder) return IconOf(item);
            if (previewCache.TryGetValue(item.Path, out var cached) && cached != null) return cached;
            var asset = AssetDatabase.LoadMainAssetAtPath(item.Path);
            if (asset == null) return IconOf(item);
            var preview = AssetPreview.GetAssetPreview(asset);
            if (preview != null) { previewCache[item.Path] = preview; return preview; }
            if (AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID())) previewsWaiting = true;
            return IconOf(item);
        }

        private void PollPreviews()
        {
            if (!GridMode || !previewsWaiting) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastPreviewPoll < 0.25) return;
            lastPreviewPoll = now;
            previewsWaiting = false;
            grid.RefreshRows();
        }

        // ---- Folding in place -----------------------------------------------------------------------------------------

        private void SetUnfolded(string path, bool open, bool recursive)
        {
            if (open) contentOpenSet.Add(path); else contentOpenSet.Remove(path);
            if (recursive)
                foreach (string sub in ProjectFiles.FoldersBelow(path, UnfoldLimit)) { if (open) contentOpenSet.Add(sub); else contentOpenSet.Remove(sub); }
            RebuildNow();
        }

        /// <summary>Unfolds every folder below the open one (as IntelliJ's Expand All), the first <see cref="UnfoldLimit"/>.</summary>
        internal void UnfoldAll()
        {
            if (Searching || showRecent || GridMode) return;
            var below = new List<string>();
            if (folder == PackagesPath)
            {
                foreach (string package in packageNames.Keys)
                {
                    if (below.Count >= UnfoldLimit) break;
                    below.Add(package);
                    below.AddRange(ProjectFiles.FoldersBelow(package, UnfoldLimit - below.Count));
                }
            }
            else below = ProjectFiles.FoldersBelow(folder, UnfoldLimit);
            contentOpenSet.UnionWith(below);
            if (below.Count >= UnfoldLimit) toasts.Show("Unfolded the first " + UnfoldLimit.ToString("N0") + " folders.");
            RebuildNow();
        }

        /// <summary>Folds every folder below the open one.</summary>
        internal void FoldAll()
        {
            string prefix = folder == PackagesPath ? PackagesPath + "/" : folder + "/";
            contentOpenSet.RemoveWhere(p => p.StartsWith(prefix, StringComparison.Ordinal));
            RebuildNow();
        }

        // ---- The path bar -----------------------------------------------------------------------------------------------

        private VisualElement BuildPathBar()
        {
            var bar = MixerUi.Box("mxp-pathbar");
            crumbs = MixerUi.Box("mxp-crumbs");
            bar.Add(crumbs);
            bar.Add(MixerUi.Spacer());
            unfoldButton = MixerUi.Icon(IconGlyph.UnfoldAll, "Unfold all (Alt+click a folder's arrow: that folder and all below it)", UnfoldAll, "mxp-tool");
            bar.Add(unfoldButton);
            foldButton = MixerUi.Icon(IconGlyph.FoldAll, "Fold all", FoldAll, "mxp-tool");
            bar.Add(foldButton);
            return bar;
        }

        private void RebuildPathBar()
        {
            if (crumbs == null) return;
            crumbs.Clear();
            bool folders = !Searching && !showRecent && !GridMode;
            unfoldButton.SetEnabled(folders);
            foldButton.SetEnabled(folders);
            if (Searching)
            {
                AddCrumbGlyph(IconGlyph.Search);
                crumbs.Add(MixerUi.Text(includePackages ? "Search in Assets and packages" : "Search in Assets", "mxp-crumb mxp-crumb--current"));
                return;
            }
            if (showRecent)
            {
                AddCrumbGlyph(IconGlyph.Clock);
                crumbs.Add(MixerUi.Text("Recent", "mxp-crumb mxp-crumb--current"));
                return;
            }
            var parts = new List<string>();
            for (string p = folder; !string.IsNullOrEmpty(p); p = ParentOf(p)) parts.Add(p);
            parts.Reverse();
            for (int i = 0; i < parts.Count; i++)
            {
                string path = parts[i];
                if (i > 0)
                {
                    var separator = new VectorIcon(IconGlyph.Chevron);
                    separator.AddToClassList("mxp-crumb__separator");
                    crumbs.Add(separator);
                }
                string label = packageNames.TryGetValue(path, out var display) ? display : System.IO.Path.GetFileName(path);
                bool last = i == parts.Count - 1;
                var crumb = new Button { text = label, tooltip = path };
                crumb.AddToClassList("mxp-crumb");
                if (last) crumb.AddToClassList("mxp-crumb--current");
                MixerUi.Press(crumb, () => Navigate(path));
                crumbs.Add(crumb);
            }
        }

        private void AddCrumbGlyph(IconGlyph glyph)
        {
            var icon = new VectorIcon(glyph);
            icon.AddToClassList("mxp-crumb__glyph");
            crumbs.Add(icon);
        }
    }
}
