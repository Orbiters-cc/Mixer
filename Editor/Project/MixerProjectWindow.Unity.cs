using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    // What Unity's Project window also does: favourites (folders, assets and saved searches), the "+" menu of everything
    // Unity creates, search scopes and labels, a lock that stops following the selection, Unity's whole Assets menu
    // (Import, Export Package, Find References, Properties, other tools' items) on the context menu, Copy and Paste, and
    // typing the start of a name to jump to it.
    internal sealed partial class MixerProjectWindow
    {
        internal const string FavouritesPath = "\u0001favourites";
        private const string SavedSearchPrefix = "\u0001search\u0001";

        // What Copy took, by GUID: shared by every Mixer Project window, kept across script reloads, and still found after
        // a rename or a move.
        private const string ClipboardKey = "Orbiters.Mixer.Project.Clipboard";

        private Button starButton, addButton;
        private GUIStyle lockStyle;
        private string typed = string.Empty;
        private double lastTyped;

        private static readonly MethodInfo AllLabels = typeof(AssetDatabase).GetMethod("GetAllLabels", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);

        // ---- Favourites ------------------------------------------------------------------------------------------------

        private void OnFavouritesChanged()
        {
            treeDirty = true;
            SyncStar();
        }

        private void AddFavouriteRows()
        {
            var entries = ProjectFavourites.Entries;
            treeRows.Add(new ProjectItem { Path = FavouritesPath, Name = "Favourites", HasChildren = entries.Count > 0, Expanded = !favouritesFolded && entries.Count > 0 });
            if (favouritesFolded) return;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.IsSearch)
                {
                    treeRows.Add(new ProjectItem { Path = SavedSearchPrefix + i, Name = entry.name, Depth = 1, Tag = entry });
                    continue;
                }
                string path = entry.AssetPath;
                bool isFolder = AssetDatabase.IsValidFolder(path);
                treeRows.Add(new ProjectItem
                {
                    Path = path, Depth = 1, Tag = entry, IsFolder = isFolder,
                    Name = isFolder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path),
                    Empty = isFolder && !ProjectFiles.HasChildren(path, false),
                });
            }
        }

        private static ProjectFavourites.Entry FavouriteOf(ProjectItem item) => item?.Tag as ProjectFavourites.Entry;

        /// <summary>A favourite clicked: a saved search runs, a folder opens, an asset is shown and selected.</summary>
        internal void OpenFavourite(ProjectFavourites.Entry entry)
        {
            if (entry == null) return;
            if (entry.IsSearch)
            {
                // This window's switches for this search, not the ones the field keeps for later searches.
                searchOptions.Set(entry.regex, entry.matchCase, remember: false);
                searchField.value = entry.query;
                search = entry.query;
                content.SetFirst(0);
                grid.SetFirst(0);
                RebuildNow();
                return;
            }
            string path = entry.AssetPath;
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.IsValidFolder(path)) { Navigate(path); return; }
            Navigate(ParentOf(path));
            picked.Clear();
            picked.Add(path);
            cursor = anchor = path;
            PushSelection();
            Reveal(path);
            RefreshContent();
            UpdateFooter();
        }

        // The star at the end of the search field saves the search as a favourite, or forgets it.
        private void ToggleSavedSearch()
        {
            if (!Searching) return;
            var pattern = searchOptions.For(search);
            var saved = ProjectFavourites.FindSearch(pattern.Text, pattern.IsRegex, pattern.MatchCase);
            if (saved != null) { ProjectFavourites.Remove(saved); toasts.Show("Removed “" + saved.name + "” from Favourites"); }
            else { ProjectFavourites.AddSearch(pattern.Text, pattern.IsRegex, pattern.MatchCase); toasts.Show("Saved the search to Favourites"); favouritesFolded = false; }
        }

        private void SyncStar()
        {
            if (starButton == null) return;
            MixerUi.Show(starButton, Searching);
            if (!Searching) return;
            var pattern = searchOptions.For(search);
            bool saved = ProjectFavourites.FindSearch(pattern.Text, pattern.IsRegex, pattern.MatchCase) != null;
            starButton.Q<VectorIcon>().Glyph = saved ? IconGlyph.StarFilled : IconGlyph.Star;
            starButton.EnableInClassList("mxp-star--on", saved);
            starButton.tooltip = saved ? "Saved in Favourites: click to forget it" : "Save this search to Favourites";
        }

        private void AddFavourites(IEnumerable<string> paths)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p) && p.IndexOf(ProjectFiles.SubAssetMark) < 0).ToList();
            if (ProjectFavourites.AddAssets(list)) { favouritesFolded = false; toasts.Show(list.Count == 1 ? "Added " + System.IO.Path.GetFileName(list[0]) + " to Favourites" : "Added to Favourites"); }
        }

        private void AppendFavouriteActions(DropdownMenu menu, ProjectItem item)
        {
            if (item.IsSubAsset) return;
            if (ProjectFavourites.Contains(item.Path)) menu.AppendAction("Remove from Favourites", _ => ProjectFavourites.RemoveAsset(item.Path));
            else menu.AppendAction("Add to Favourites", _ => AddFavourites(picked.Contains(item.Path) ? picked : new HashSet<string> { item.Path }));
        }

        private void BuildFavouriteMenu(ContextualMenuPopulateEvent evt, ProjectItem item)
        {
            var entry = FavouriteOf(item);
            if (entry == null)
            {
                if (!Searching && !showRecent && folder != PackagesPath && !ProjectFavourites.Contains(folder))
                    evt.menu.AppendAction("Add " + System.IO.Path.GetFileName(folder) + " to Favourites", _ => AddFavourites(new[] { folder }));
                if (Searching) evt.menu.AppendAction("Save the search to Favourites", _ => ToggleSavedSearch());
                return;
            }
            evt.menu.AppendAction("Open", _ => OpenFavourite(entry));
            if (!entry.IsSearch)
            {
                evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(entry.AssetPath));
                evt.menu.AppendAction("Copy Path", _ => EditorGUIUtility.systemCopyBuffer = entry.AssetPath);
            }
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Move up", _ => ProjectFavourites.Move(entry, -1));
            evt.menu.AppendAction("Move down", _ => ProjectFavourites.Move(entry, 1));
            evt.menu.AppendAction("Remove from Favourites", _ => ProjectFavourites.Remove(entry));
        }

        // ---- Creating ------------------------------------------------------------------------------------------------------

        // Everything Unity's Assets/Create menu holds, in its order; ours make the folder, material, animation and
        // controller (renamed in place where the user is).
        internal List<(string label, Action action)> CreateEntries(string target)
        {
            var entries = new List<(string, Action)>
            {
                ("Folder", () => CreateFolder(target)), (null, null),
                ("Material", () => CreateMaterial(target)), ("Animation", () => CreateAnimation(target)), ("Animator Controller", () => CreateAnimator(target)),
                (null, null),
            };
            foreach (string item in UnityCreateItems())
            {
                string path = item;
                entries.Add((path.Substring("Assets/Create/".Length), () => RunUnityCreate(path, target)));
            }
            return entries;
        }

        private void AppendCreateMenu(DropdownMenu menu, string target)
        {
            var status = ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            foreach (var (label, action) in CreateEntries(target))
            {
                if (label == null) { menu.AppendSeparator("Create/"); continue; }
                var run = action;
                menu.AppendAction("Create/" + label, _ => run(), status);
            }
        }

        /// <summary>The "+" button: everything that can be created, in the folder under the keyboard (else the open one).</summary>
        private void ShowCreateMenu()
        {
            string target = CreateTarget();
            var menu = new GenericMenu();
            if (!ProjectFiles.IsWritable(target))
            {
                menu.AddDisabledItem(new GUIContent("This package cannot be changed: open a folder of Assets"));
                menu.DropDown(addButton.worldBound);
                return;
            }
            foreach (var (label, action) in CreateEntries(target))
            {
                if (label == null) { menu.AddSeparator(""); continue; }
                var run = action;
                menu.AddItem(new GUIContent(label), false, () => run());
            }
            menu.DropDown(addButton.worldBound);
        }

        // ---- Search scope and labels -------------------------------------------------------------------------------------

        private string ScopeFolder => showRecent ? "Assets" : folder;

        private bool InScope(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            switch (scope)
            {
                case SearchScope.All: return path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal);
                case SearchScope.Packages: return path.StartsWith("Packages/", StringComparison.Ordinal);
                case SearchScope.Folder:
                    return ScopeFolder == PackagesPath ? path.StartsWith("Packages/", StringComparison.Ordinal) : path.StartsWith(ScopeFolder + "/", StringComparison.Ordinal);
                default: return path.StartsWith("Assets/", StringComparison.Ordinal);
            }
        }

        // The folders Unity's name search looks in, or null for all of them.
        private string[] ScopeRoots()
        {
            switch (scope)
            {
                case SearchScope.All: return null;
                case SearchScope.Packages: return ProjectFiles.Packages().Select(p => p.path).ToArray();
                case SearchScope.Folder: return ScopeFolder == PackagesPath ? ProjectFiles.Packages().Select(p => p.path).ToArray() : new[] { ScopeFolder };
                default: return new[] { "Assets" };
            }
        }

        private string ScopeName()
        {
            switch (scope)
            {
                case SearchScope.All: return "Search everywhere";
                case SearchScope.Packages: return "Search in packages";
                case SearchScope.Folder: return "Search in " + (ScopeFolder == PackagesPath ? "packages" : packageNames.TryGetValue(ScopeFolder, out var name) ? name : System.IO.Path.GetFileName(ScopeFolder));
                default: return "Search in Assets";
            }
        }

        private void ShowScopeMenu(VisualElement under)
        {
            var menu = new GenericMenu();
            void Scope(string label, SearchScope value) => menu.AddItem(new GUIContent(label), scope == value, () => { scope = value; RebuildNow(); });
            Scope("Everywhere (Assets and packages)", SearchScope.All);
            Scope("Assets", SearchScope.Assets);
            Scope("Packages", SearchScope.Packages);
            Scope("The open folder: " + (ScopeFolder == PackagesPath ? "Packages" : System.IO.Path.GetFileName(ScopeFolder)), SearchScope.Folder);
            menu.DropDown(under.worldBound);
        }

        // The project's asset labels (Unity's l: filter), for the Filter menu.
        private static List<string> Labels()
        {
            try { return AllLabels?.Invoke(null, null) is Dictionary<string, float> labels ? labels.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        private bool HasLabelToken(string label) => (" " + search + " ").IndexOf(" l:" + label + " ", StringComparison.OrdinalIgnoreCase) >= 0;

        private void ToggleLabel(string label)
        {
            string token = "l:" + label;
            var words = (search ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.RemoveAll(w => string.Equals(w, token, StringComparison.OrdinalIgnoreCase)) == 0) words.Add(token);
            if (searchOptions.Regex) searchOptions.Set(false, searchOptions.MatchCase, remember: false);
            searchField.value = string.Join(" ", words);
        }

        // ---- The lock --------------------------------------------------------------------------------------------------

        // Unity draws a window's ShowButton in its tab (the Inspector's lock): locked, a selection made elsewhere no longer
        // opens its folder here.
        private void ShowButton(Rect position)
        {
            if (lockStyle == null) lockStyle = "IN LockButton";
            locked = GUI.Toggle(position, locked, GUIContent.none, lockStyle);
        }

        // ---- Unity's Assets menu -------------------------------------------------------------------------------------

        // The items this window offers its own way (they act on what lights up here, folders too).
        private static readonly HashSet<string> OurAssetItems = new HashSet<string>(StringComparer.Ordinal)
        {
            "Assets/Show in Explorer", "Assets/Open", "Assets/Delete", "Assets/Rename", "Assets/Copy Path", "Assets/Refresh", "Assets/Reimport",
        };

        private static List<string> UnityAssetItems()
        {
            try { return Unsupported.GetSubmenus("Assets").Where(p => !p.StartsWith("Assets/Create/", StringComparison.Ordinal) && !OurAssetItems.Contains(p)).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        private void AppendUnityAssetsMenu(DropdownMenu menu)
        {
            foreach (string item in UnityAssetItems())
            {
                string path = item;
                menu.AppendAction(path.Substring("Assets/".Length), _ => RunUnityAssetCommand(path));
            }
        }

        // Unity's commands read the selection: what lights up here becomes it first (folders included), as in its window.
        // A folder only lit up for the command does not stay Unity's selection (folder clicks never select here).
        private void RunUnityAssetCommand(string menuPath)
        {
            var before = Selection.objects;
            bool folders = picked.Any(AssetDatabase.IsValidFolder);
            SelectPickedForCommand();
            var set = Selection.instanceIDs;
            if (!EditorApplication.ExecuteMenuItem(menuPath)) toasts.Show("Unity cannot " + menuPath.Substring("Assets/".Length).Replace("/", " › ") + " here.", true);
            if (folders && Selection.instanceIDs.SequenceEqual(set))
            {
                pushed = new HashSet<int>(before.Where(o => o != null).Select(o => o.GetInstanceID()));
                Selection.objects = before;
            }
        }

        private void SelectPickedForCommand()
        {
            var objects = picked.Select(ObjectOf).Where(o => o != null).ToArray();
            if (objects.Length == 0) return;
            pushed = new HashSet<int>(objects.Select(o => o.GetInstanceID()));
            Selection.objects = objects;
        }

        // A line's instance id without loading its asset, as Unity's window selects (internal; loaded without it). A
        // sub-asset is already loaded.
        private static readonly MethodInfo MainAssetInstanceId = typeof(AssetDatabase).GetMethod("GetMainAssetInstanceID", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);

        private int InstanceIdOf(string path)
        {
            if (path.IndexOf(ProjectFiles.SubAssetMark) >= 0) return ObjectOf(path) is Object sub && sub != null ? sub.GetInstanceID() : 0;
            if (MainAssetInstanceId != null)
            {
                try { if (MainAssetInstanceId.Invoke(null, new object[] { path }) is int id) return id; }
                catch (TargetInvocationException) { }
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            return asset != null ? asset.GetInstanceID() : 0;
        }

        /// <summary>The object a line stands for: a sub-asset, a file's main asset, a folder's asset.</summary>
        private Object ObjectOf(string path)
        {
            int mark = path.IndexOf(ProjectFiles.SubAssetMark);
            if (mark < 0) return AssetDatabase.LoadMainAssetAtPath(path);
            int index = IndexOf(path);
            if (index >= 0 && rows[index].SubAsset != null) return rows[index].SubAsset;
            return int.TryParse(path.Substring(mark + 1), out int id) ? EditorUtility.InstanceIDToObject(id) : null;
        }

        // ---- Copy and Paste -----------------------------------------------------------------------------------------

        private bool InTextField(EventBase evt) =>
            renamingPath != null || (evt.target is VisualElement target && (target == searchField || searchField.Contains(target)));

        // Edit menu commands (and their shortcuts) answered while the window has the focus, as by Unity's window.
        private static readonly HashSet<string> Commands = new HashSet<string>(StringComparer.Ordinal) { "Copy", "Paste", "Delete", "SoftDelete", "Duplicate", "SelectAll", "Find" };

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            if (Commands.Contains(evt.commandName) && !InTextField(evt)) evt.StopPropagation();
        }

        private void OnCommand(ExecuteCommandEvent evt)
        {
            if (InTextField(evt)) return;
            switch (evt.commandName)
            {
                case "Copy": CopyPicked(); break;
                case "Paste": Paste(); break;
                case "Delete": case "SoftDelete": DeletePicked(); break;
                case "Duplicate": DuplicatePicked(); break;
                case "SelectAll":
                    picked.Clear();
                    foreach (var row in rows) if (!row.IsHeading) picked.Add(row.Path);
                    PushSelection();
                    RefreshContent();
                    UpdateFooter();
                    break;
                case "Find": searchField.Q(TextField.textInputUssName)?.Focus(); break;
                default: return;
            }
            evt.StopPropagation();
        }

        internal void CopyPicked()
        {
            var copied = PickedRoots();
            var guids = copied.Select(AssetDatabase.AssetPathToGUID).Where(g => !string.IsNullOrEmpty(g)).ToList();
            SessionState.SetString(ClipboardKey, string.Join(",", guids));
            if (guids.Count > 0) toasts.Show(guids.Count == 1 ? "Copied " + System.IO.Path.GetFileName(copied[0]) : "Copied " + guids.Count + " assets");
        }

        // What Copy took, where it is now.
        private static List<string> ClipboardPaths() =>
            SessionState.GetString(ClipboardKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p)).ToList();

        /// <summary>Copies what Copy took into the folder under the keyboard (else the open one), as Unity's Paste does; a
        /// folder pasted while it is the one highlighted goes beside itself.</summary>
        internal void Paste()
        {
            string target = CreateTarget();
            var sources = ClipboardPaths();
            if (sources.Count == 0 || !CanCreateIn(target)) return;
            var copies = new List<string>();
            foreach (string source in sources)
            {
                string into = target == source || target.StartsWith(source + "/", StringComparison.Ordinal) ? ParentOf(source) : target;
                if (into == null || !ProjectFiles.IsWritable(into)) continue;
                string copy = AssetDatabase.GenerateUniqueAssetPath(into + "/" + System.IO.Path.GetFileName(source));
                if (AssetDatabase.CopyAsset(source, copy)) copies.Add(copy);
            }
            if (copies.Count == 0) return;
            AfterDrop(ParentOf(copies[0]) ?? target, copies);
        }

        // ---- Typing a name -------------------------------------------------------------------------------------------------

        // Letters typed in a row (within a second of each other) find the next line whose name starts with them.
        internal bool TypeAhead(char character)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != ' ') return false;
            double now = EditorApplication.timeSinceStartup;
            typed = now - lastTyped < 1.0 ? typed + character : character.ToString();
            lastTyped = now;
            int start = Mathf.Max(0, IndexOf(cursor));
            for (int n = 0; n < rows.Count; n++)
            {
                // A first letter looks from the next line, so pressing it again steps through its names.
                int i = (start + (typed.Length == 1 ? n + 1 : n)) % rows.Count;
                var row = rows[i];
                if (row.IsHeading || !row.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;
                Pick(i, false, false);
                RevealIndex(i);
                return true;
            }
            return true;
        }
    }
}
