using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    // What the rows do: the selection (files are Unity's selection, folders only light up here), opening, the keys,
    // rename, the create buttons and menus, delete and duplicate, drag and drop, the context menus.
    internal sealed partial class MixerProjectWindow
    {
        /// <summary>Asks before deleting (Delete or Cancel); false cancels. Tests replace it.</summary>
        internal static Func<string, string, bool> AskToDelete = (title, message) => EditorUtility.DisplayDialog(title, message, "Delete", "Cancel");

        // What lights up here (folders too), the keyboard's line, where a Shift+click extends from: paths.
        private readonly HashSet<string> picked = new HashSet<string>(StringComparer.Ordinal);
        private string cursor, anchor;
        // The objects this window gave Unity's selection: its change notice is ours, not something to follow.
        private HashSet<int> pushed;
        private string renamingPath;
        private bool creating;
        private string dragFrom, dropFolder, pingPath;
        private double pingUntil;
        private Vector2 dragStart;

        /// <summary>The paths lit up (tests).</summary>
        internal IReadOnlyCollection<string> Picked => picked;

        // ---- Selection ----------------------------------------------------------------------------------------------

        private bool IsOurSelection()
        {
            if (pushed == null) return false;
            var ids = Selection.instanceIDs;
            if (ids.Length != pushed.Count) return false;
            foreach (int id in ids) if (!pushed.Contains(id)) return false;
            return true;
        }

        /// <summary>
        /// Lights up a line (a range from the anchor, or toggled with Ctrl). The files lit up become Unity's selection;
        /// folders never do: a click on a folder leaves the selection, and the Inspector, as they were.
        /// </summary>
        internal void Pick(int index, bool range, bool toggle)
        {
            var item = Row(index);
            if (item == null || item.IsHeading) return;
            int from = range ? IndexOf(anchor) : -1;
            if (range && from >= 0)
            {
                picked.Clear();
                for (int i = Mathf.Min(from, index); i <= Mathf.Max(from, index); i++)
                    if (!rows[i].IsHeading) picked.Add(rows[i].Path);
            }
            else if (toggle)
            {
                if (!picked.Remove(item.Path)) picked.Add(item.Path);
                anchor = item.Path;
            }
            else
            {
                picked.Clear();
                picked.Add(item.Path);
                anchor = item.Path;
            }
            cursor = item.Path;
            PushSelection();
            RefreshContent();
            UpdateFooter();
        }

        private void PushSelection()
        {
            var objects = new List<Object>();
            foreach (string path in picked)
            {
                int index = IndexOf(path);
                bool isFolder = index >= 0 ? rows[index].IsFolder : AssetDatabase.IsValidFolder(path);
                if (isFolder) continue;
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset != null) objects.Add(asset);
            }
            if (objects.Count == 0) return;
            pushed = new HashSet<int>(objects.Select(o => o.GetInstanceID()));
            Selection.objects = objects.ToArray();
        }

        private void OnRowPointerDown(VisualElement element, PointerDownEvent evt) => OnItemPointerDown(content.ItemOf(element), evt);

        // A press on a line of the list or a tile of the grid.
        private void OnItemPointerDown(int index, PointerDownEvent evt)
        {
            var item = Row(index);
            if (item == null || item.IsHeading) return;
            if (renamingPath != null && item.Path == renamingPath) return;
            root.Focus();
            if (evt.button == 1)
            {
                if (!picked.Contains(item.Path)) Pick(index, false, false);
                return;
            }
            if (evt.button != 0) return;
            if (evt.clickCount == 2) { Activate(item); evt.StopPropagation(); return; }
            if (evt.shiftKey && IndexOf(anchor) >= 0) Pick(index, true, false);
            else if (evt.ctrlKey || evt.commandKey) Pick(index, false, true);
            else if (!picked.Contains(item.Path) || picked.Count == 1) Pick(index, false, false);
            else { cursor = anchor = item.Path; RefreshContent(); }
            dragFrom = item.Path;
            dragStart = evt.position;
        }

        private void OnViewportPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || ItemAtPoint(evt.position) >= 0) return;
            picked.Clear();
            cursor = null;
            root.Focus();
            RefreshContent();
            UpdateFooter();
        }

        /// <summary>A double click or Enter: a folder opens on the right, a file opens as Unity opens it.</summary>
        internal void Activate(ProjectItem item)
        {
            if (item == null || item.IsHeading) return;
            if (item.IsFolder) { Navigate(item.Path); return; }
            var asset = AssetDatabase.LoadMainAssetAtPath(item.Path);
            if (asset != null) AssetDatabase.OpenAsset(asset);
        }

        /// <summary>
        /// Shows an asset without selecting it (a ping): its folder opened when it is not on screen, its line flashing.
        /// </summary>
        internal void Ping(string path)
        {
            if (string.IsNullOrEmpty(path) || !built) return;
            if (IndexOf(path) < 0)
            {
                string parent = ParentOf(path);
                if (parent != null && (Searching || showRecent || !path.StartsWith(folder + "/", StringComparison.Ordinal))) Navigate(parent);
            }
            Reveal(path);
            pingPath = path;
            pingUntil = EditorApplication.timeSinceStartup + 1.6;
            RefreshContent();
        }

        /// <summary>Scrolls a line on screen, unfolding the folders between the open folder and it.</summary>
        internal void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path) || content == null) return;
            int index = IndexOf(path);
            if (index < 0 && !Searching && !showRecent && path.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                bool changed = false;
                for (string p = ParentOf(path); p != null && p != folder; p = ParentOf(p)) changed |= contentOpenSet.Add(p);
                if (changed) { RebuildContent(); index = IndexOf(path); }
            }
            if (index >= 0) RevealIndex(index);
        }

        // ---- Keys ---------------------------------------------------------------------------------------------------------

        private void OnKey(KeyDownEvent evt)
        {
            if (renamingPath != null) return;
            if (evt.target is VisualElement target && (target == searchField || searchField.Contains(target))) return;
            int current = IndexOf(cursor);
            bool command = evt.ctrlKey || evt.commandKey;
            switch (evt.keyCode)
            {
                case KeyCode.DownArrow: if (GridMode) StepLines(current, 1, evt.shiftKey); else Step(current, 1, evt.shiftKey); break;
                case KeyCode.UpArrow: if (GridMode) StepLines(current, -1, evt.shiftKey); else Step(current < 0 ? rows.Count : current, -1, evt.shiftKey); break;
                case KeyCode.Home: Step(-1, 1, evt.shiftKey); break;
                case KeyCode.End: Step(rows.Count, -1, evt.shiftKey); break;
                case KeyCode.RightArrow:
                    if (GridMode) Step(current, 1, evt.shiftKey);
                    else if (current >= 0 && rows[current].IsFolder && rows[current].HasChildren)
                    {
                        if (!rows[current].Expanded) SetUnfolded(rows[current].Path, true, evt.altKey); else Step(current, 1, false);
                    }
                    break;
                case KeyCode.LeftArrow:
                    if (GridMode) Step(current < 0 ? rows.Count : current, -1, evt.shiftKey);
                    else if (current >= 0)
                    {
                        if (rows[current].IsFolder && rows[current].Expanded) SetUnfolded(rows[current].Path, false, evt.altKey);
                        else { int parent = IndexOf(ParentOf(rows[current].Path)); if (parent >= 0) { Pick(parent, false, false); RevealIndex(parent); } }
                    }
                    break;
                case KeyCode.Return: case KeyCode.KeypadEnter: if (current >= 0) Activate(rows[current]); break;
                case KeyCode.F2: BeginRename(); break;
                case KeyCode.Delete: DeletePicked(); break;
                case KeyCode.Backspace:
                    if (evt.commandKey) DeletePicked();
                    else if (!Searching && !showRecent && folder != "Assets" && folder != PackagesPath) Navigate(packageNames.ContainsKey(folder) ? PackagesPath : ParentOf(folder));
                    break;
                case KeyCode.D: if (!command) return; DuplicatePicked(); break;
                case KeyCode.A:
                    if (!command) return;
                    picked.Clear();
                    foreach (var row in rows) if (!row.IsHeading) picked.Add(row.Path);
                    PushSelection();
                    RefreshContent();
                    UpdateFooter();
                    break;
                case KeyCode.F: if (!command) return; searchField.Q(TextField.textInputUssName)?.Focus(); break;
                case KeyCode.Escape:
                    if (usagePath != null) CloseUsages();
                    else { picked.Clear(); cursor = null; RefreshContent(); UpdateFooter(); }
                    break;
                default: return;
            }
            evt.StopPropagation();
        }

        private void Step(int from, int direction, bool extend)
        {
            int index = from + direction;
            while (index >= 0 && index < rows.Count && rows[index].IsHeading) index += direction;
            if (index < 0 || index >= rows.Count) return;
            Pick(index, extend && IndexOf(anchor) >= 0, false);
            RevealIndex(index);
        }

        // ---- Rename ---------------------------------------------------------------------------------------------------------

        /// <summary>Starts renaming a line in place (the line under the keyboard by default).</summary>
        internal void BeginRename(string path = null)
        {
            path = path ?? cursor;
            int index = IndexOf(path);
            if (index < 0 || (packageNames.ContainsKey(path)) || !ProjectFiles.IsWritable(path)) return;
            renamingPath = path;
            RevealIndex(index);
            RefreshContent();
        }

        private void CommitRename(TextField field, bool apply)
        {
            if (renamingPath == null) return;
            bool ours = field.userData is string bound && bound == renamingPath;
            string path = renamingPath;
            renamingPath = null;
            field.userData = null;
            if (apply && ours)
            {
                int index = IndexOf(path);
                bool isFolder = index >= 0 ? rows[index].IsFolder : AssetDatabase.IsValidFolder(path);
                string name = field.value.Trim();
                string current = isFolder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
                if (name.Length > 0 && name != current)
                {
                    string error = AssetDatabase.RenameAsset(path, name);
                    if (!string.IsNullOrEmpty(error)) toasts.Show(error, true);
                    else Renamed(path, ParentOf(path) + "/" + name + (isFolder ? string.Empty : System.IO.Path.GetExtension(path)));
                }
            }
            MarkAllDirty();
            RebuildNow();
            root.Focus();
        }

        // A path that changed (a rename, a move): what lit up, the keyboard's line and the unfolded folders follow it.
        private void Renamed(string from, string to)
        {
            string Map(string p) => p == from ? to : p != null && p.StartsWith(from + "/", StringComparison.Ordinal) ? to + p.Substring(from.Length) : p;
            var lit = picked.Select(Map).ToList();
            picked.Clear();
            picked.UnionWith(lit);
            cursor = Map(cursor);
            anchor = Map(anchor);
            var open = contentOpenSet.Select(Map).ToList();
            contentOpenSet.Clear();
            contentOpenSet.UnionWith(open);
            var openTree = treeOpenSet.Select(Map).ToList();
            treeOpenSet.Clear();
            treeOpenSet.UnionWith(openTree);
            folder = Map(folder);
            if (usagePath != null) usagePath = Map(usagePath);
        }

        // ---- Create ---------------------------------------------------------------------------------------------------------

        /// <summary>Where the create buttons create: the folder of the line under the keyboard, else the open folder.</summary>
        internal string CreateTarget()
        {
            int index = !showRecent && !Searching ? IndexOf(cursor) : -1;
            if (index >= 0) return rows[index].IsFolder ? rows[index].Path : ParentOf(rows[index].Path);
            return showRecent || Searching || folder == PackagesPath ? "Assets" : folder;
        }

        private bool CanCreateIn(string target)
        {
            if (ProjectFiles.IsWritable(target)) return true;
            toasts.Show("This package cannot be changed: create in a folder of Assets.", true);
            return false;
        }

        internal string CreateFolder(string target = null)
        {
            target = target ?? CreateTarget();
            if (!CanCreateIn(target)) return null;
            string path = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(target, "New Folder"));
            if (string.IsNullOrEmpty(path)) { toasts.Show("No folder could be made in " + target + ".", true); return null; }
            Created(path, selectInUnity: false);
            return path;
        }

        internal string CreateMaterial(string target = null)
        {
            target = target ?? CreateTarget();
            if (!CanCreateIn(target)) return null;
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var shader = pipeline != null && pipeline.defaultShader != null ? pipeline.defaultShader : Shader.Find("Standard");
            string path = ProjectFiles.Unique(target, "New Material.mat");
            AssetDatabase.CreateAsset(new Material(shader), path);
            Created(path, selectInUnity: true);
            return path;
        }

        internal string CreateAnimation(string target = null)
        {
            target = target ?? CreateTarget();
            if (!CanCreateIn(target)) return null;
            string path = ProjectFiles.Unique(target, "New Animation.anim");
            AssetDatabase.CreateAsset(new AnimationClip(), path);
            Created(path, selectInUnity: true);
            return path;
        }

        internal string CreateAnimator(string target = null)
        {
            target = target ?? CreateTarget();
            if (!CanCreateIn(target)) return null;
            string path = ProjectFiles.Unique(target, "New Animator.controller");
            AnimatorController.CreateAnimatorControllerAtPath(path);
            Created(path, selectInUnity: true);
            return path;
        }

        // Something new: on screen (its folder opened or unfolded), lit up (a file selected, as Unity does), being renamed.
        private void Created(string path, bool selectInUnity)
        {
            string parent = ParentOf(path);
            if (Searching || showRecent || (parent != folder && !parent.StartsWith(folder + "/", StringComparison.Ordinal))) Navigate(parent);
            for (string p = parent; p != null && p != folder; p = ParentOf(p)) contentOpenSet.Add(p);
            MarkAllDirty();
            RebuildNow();
            picked.Clear();
            picked.Add(path);
            cursor = anchor = path;
            if (selectInUnity) PushSelection();
            Reveal(path);
            BeginRename(path);
            UpdateFooter();
        }

        // Unity's Assets/Create items, as its Project window lists them (ours replace its folder, material, animation and controller).
        private static readonly HashSet<string> OurCreateItems = new HashSet<string>(StringComparer.Ordinal)
        {
            "Assets/Create/Folder", "Assets/Create/Material", "Assets/Create/Animation", "Assets/Create/Animator Controller",
        };

        private static List<string> UnityCreateItems()
        {
            try { return Unsupported.GetSubmenus("Assets").Where(p => p.StartsWith("Assets/Create/", StringComparison.Ordinal) && !OurCreateItems.Contains(p)).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        private void AppendCreateMenu(DropdownMenu menu, string target)
        {
            var status = ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            menu.AppendAction("Create/Folder", _ => CreateFolder(target), status);
            menu.AppendSeparator("Create/");
            menu.AppendAction("Create/Material", _ => CreateMaterial(target), status);
            menu.AppendAction("Create/Animation", _ => CreateAnimation(target), status);
            menu.AppendAction("Create/Animator Controller", _ => CreateAnimator(target), status);
            menu.AppendSeparator("Create/");
            foreach (string item in UnityCreateItems())
            {
                string path = item;
                menu.AppendAction("Create/" + path.Substring("Assets/Create/".Length), _ => RunUnityCreate(path, target), status);
            }
        }

        // Unity's create items make their asset in its Project window's folder, or in Assets without one: what they made
        // (within a few seconds) moves to the folder asked for, then is renamed in place.
        private void RunUnityCreate(string menuPath, string target)
        {
            var before = new HashSet<string>(AssetDatabase.GetAllAssetPaths(), StringComparer.Ordinal);
            creating = true;
            if (!EditorApplication.ExecuteMenuItem(menuPath)) { creating = false; toasts.Show("Unity could not run " + menuPath.Substring("Assets/".Length) + ".", true); return; }
            double until = EditorApplication.timeSinceStartup + 3.0;
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                var made = AssetDatabase.GetAllAssetPaths().Where(p => !before.Contains(p) && (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal))).ToList();
                if (made.Count == 0 && EditorApplication.timeSinceStartup < until) return;
                EditorApplication.update -= poll;
                creating = false;
                // Only the topmost of what appeared (a folder's contents go with it).
                made = made.Where(p => !made.Any(o => o != p && p.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
                if (made.Count == 0 || this == null) return;
                string first = null;
                foreach (string path in made)
                {
                    string place = path;
                    if (ParentOf(path) != target)
                    {
                        string moved = AssetDatabase.GenerateUniqueAssetPath(target + "/" + System.IO.Path.GetFileName(path));
                        if (string.IsNullOrEmpty(AssetDatabase.MoveAsset(path, moved))) place = moved;
                    }
                    first = first ?? place;
                }
                Created(first, selectInUnity: !AssetDatabase.IsValidFolder(first));
            };
            EditorApplication.update += poll;
        }

        // ---- Delete, duplicate, copy ----------------------------------------------------------------------------------

        // The lines lit up, without those inside a folder also lit up (they go with it), packages' roots left out.
        private List<string> PickedRoots()
        {
            var paths = picked.Where(p => !packageNames.ContainsKey(p) && p != "Assets" && p != PackagesPath).ToList();
            return paths.Where(p => !paths.Any(o => o != p && p.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
        }

        internal void DeletePicked()
        {
            var paths = PickedRoots().Where(ProjectFiles.IsWritable).ToList();
            if (paths.Count == 0) return;
            string what = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : paths.Count + " assets";
            if (!AskToDelete("Delete " + what + "?", "It goes to the Recycle Bin; Undo cannot bring it back.")) return;
            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(paths.ToArray(), failed);
            foreach (string path in paths) picked.Remove(path);
            if (failed.Count > 0) toasts.Show(failed.Count + " could not be deleted: " + string.Join(", ", failed.Select(System.IO.Path.GetFileName)), true);
            else toasts.Show("Deleted " + what);
            MarkAllDirty();
            RebuildNow();
        }

        internal void DuplicatePicked()
        {
            var copies = new List<string>();
            foreach (string path in PickedRoots().Where(ProjectFiles.IsWritable))
            {
                string copy = AssetDatabase.GenerateUniqueAssetPath(path);
                if (AssetDatabase.CopyAsset(path, copy)) copies.Add(copy);
            }
            if (copies.Count == 0) return;
            MarkAllDirty();
            RebuildNow();
            picked.Clear();
            picked.UnionWith(copies);
            cursor = anchor = copies[copies.Count - 1];
            PushSelection();
            Reveal(cursor);
            RefreshContent();
        }

        private void CopyPaths() => EditorGUIUtility.systemCopyBuffer = string.Join("\n", picked.OrderBy(p => p, StringComparer.Ordinal));

        // ---- Context menus ----------------------------------------------------------------------------------------------

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            int index = ItemAtPoint(evt.mousePosition);
            var item = Row(index);
            string target = CreateTarget();
            if (item != null && !item.IsHeading)
            {
                if (!picked.Contains(item.Path)) Pick(index, false, false);
                target = item.IsFolder ? item.Path : ParentOf(item.Path);
                bool writable = ProjectFiles.IsWritable(item.Path) && !packageNames.ContainsKey(item.Path);
                var whenWritable = writable ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                evt.menu.AppendAction("Open", _ => Activate(item));
                if (item.IsFolder && item.HasChildren) evt.menu.AppendAction(item.Expanded ? "Fold" : "Unfold", _ => SetUnfolded(item.Path, !item.Expanded, false));
                if (!item.IsFolder) evt.menu.AppendAction("Where it is used", _ => ShowUsages(item.Path));
                if (Searching || showRecent) evt.menu.AppendAction("Show in its folder", _ => { Navigate(ParentOf(item.Path)); Reveal(item.Path); });
                evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(item.Path));
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Rename", _ => BeginRename(item.Path), whenWritable);
                evt.menu.AppendAction("Duplicate", _ => DuplicatePicked(), whenWritable);
                evt.menu.AppendAction("Delete", _ => DeletePicked(), whenWritable);
                evt.menu.AppendAction("Copy Path", _ => CopyPaths());
                evt.menu.AppendAction("Reimport", _ => { foreach (string path in PickedRoots()) AssetDatabase.ImportAsset(path, ImportAssetOptions.ImportRecursive); });
                evt.menu.AppendSeparator();
            }
            AppendCreateMenu(evt.menu, target);
            if (!Searching && !showRecent)
            {
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Unfold all", _ => UnfoldAll());
                evt.menu.AppendAction("Fold all", _ => FoldAll());
            }
            evt.menu.AppendAction("Refresh", _ => AssetDatabase.Refresh());
        }

        private void BuildTreeMenu(ContextualMenuPopulateEvent evt)
        {
            var item = TreeRow(tree.ItemAt(tree.WorldToLocal(evt.mousePosition)));
            if (item == null || item.Path == RecentPath)
            {
                if (item != null) evt.menu.AppendAction("Forget the recent assets", _ => RecentAssets.Clear());
                return;
            }
            if (item.Path == PackagesPath) { evt.menu.AppendAction("Open", _ => Navigate(PackagesPath)); return; }
            evt.menu.AppendAction("Open", _ => Navigate(item.Path));
            evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(item.Path));
            evt.menu.AppendAction("Copy Path", _ => EditorGUIUtility.systemCopyBuffer = item.Path);
            evt.menu.AppendSeparator();
            AppendCreateMenu(evt.menu, item.Path);
        }

        // ---- Drag and drop -------------------------------------------------------------------------------------------------

        private void OnViewportPointerMove(PointerMoveEvent evt)
        {
            if (dragFrom == null) return;
            // A release outside the window never came back as a pointer up: a move without the button held is no drag.
            if ((evt.pressedButtons & 1) == 0) { EndDrag(); return; }
            if (((Vector2)evt.position - dragStart).magnitude < 5f) return;
            StartDrag();
        }

        // Unity's own drag and drop: the assets land in Inspector fields, the Scene and Hierarchy, the Animator, and here
        // on a folder (a move).
        private void StartDrag()
        {
            string from = dragFrom;
            dragFrom = null;
            var paths = picked.Contains(from) ? picked.Where(p => !packageNames.ContainsKey(p)).ToList() : new List<string> { from };
            var objects = paths.Select(AssetDatabase.LoadMainAssetAtPath).Where(o => o != null).ToArray();
            if (objects.Length == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objects;
            DragAndDrop.paths = paths.ToArray();
            DragAndDrop.StartDrag(objects.Length == 1 ? objects[0].name : "<Multiple>");
        }

        private void EndDrag()
        {
            dragFrom = null;
            SetDropFolder(null);
        }

        private void SetDropFolder(string path)
        {
            if (dropFolder == path) return;
            dropFolder = path;
            bool whole = path != null && path == folder;
            content.Viewport.EnableInClassList("mxp-content--drop", whole);
            grid.Viewport.EnableInClassList("mxp-content--drop", whole);
            RefreshContent();
            tree.RefreshRows();
        }

        // The folder a drop at this point goes into: a folder line, a file's folder, else the open folder.
        private string DropTargetAt(Vector2 world, bool onTree)
        {
            if (onTree)
            {
                var item = TreeRow(tree.ItemAt(tree.WorldToLocal(world)));
                return item == null || item.Path == RecentPath || item.Path == PackagesPath ? null : item.Path;
            }
            var row = Row(ItemAtPoint(world));
            if (row != null && !row.IsHeading) return row.IsFolder ? row.Path : ParentOf(row.Path);
            return Searching || showRecent || folder == PackagesPath ? null : folder;
        }

        private enum DropKind { None, Move, Import, Prefab }

        private static DropKind KindOfDrop(string target, out List<string> sources)
        {
            sources = new List<string>();
            if (target == null || !ProjectFiles.IsWritable(target)) return DropKind.None;
            var paths = DragAndDrop.paths ?? Array.Empty<string>();
            var assets = paths.Where(p => !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p))).ToList();
            if (assets.Count > 0)
            {
                sources = assets.Where(p => ProjectFiles.IsWritable(p) && p != target && ParentOf(p) != target && !target.StartsWith(p + "/", StringComparison.Ordinal)).ToList();
                return sources.Count > 0 ? DropKind.Move : DropKind.None;
            }
            var files = paths.Where(p => Path.IsPathRooted(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
            if (files.Count > 0) { sources = files; return DropKind.Import; }
            if (DragAndDrop.objectReferences.OfType<GameObject>().Any(g => g != null && g.scene.IsValid())) return DropKind.Prefab;
            return DropKind.None;
        }

        private void OnDragUpdated(DragUpdatedEvent evt, bool onTree)
        {
            string target = DropTargetAt(evt.mousePosition, onTree);
            var kind = KindOfDrop(target, out _);
            SetDropFolder(kind == DropKind.None ? null : target);
            DragAndDrop.visualMode = kind == DropKind.None ? DragAndDropVisualMode.Rejected : kind == DropKind.Move ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Copy;
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt, bool onTree)
        {
            string target = DropTargetAt(evt.mousePosition, onTree);
            var kind = KindOfDrop(target, out var sources);
            SetDropFolder(null);
            if (kind == DropKind.None) return;
            DragAndDrop.AcceptDrag();
            evt.StopPropagation();
            if (kind == DropKind.Move) MoveInto(target, sources);
            else if (kind == DropKind.Import) ImportInto(target, sources);
            else SavePrefabsInto(target, DragAndDrop.objectReferences.OfType<GameObject>().Where(g => g != null && g.scene.IsValid()).ToList());
            EndDrag();
        }

        internal void MoveInto(string target, List<string> sources)
        {
            var moved = new List<string>();
            string error = null;
            foreach (string source in sources)
            {
                string destination = AssetDatabase.GenerateUniqueAssetPath(target + "/" + System.IO.Path.GetFileName(source));
                string result = AssetDatabase.MoveAsset(source, destination);
                if (string.IsNullOrEmpty(result)) { moved.Add(destination); Renamed(source, destination); }
                else error = result;
            }
            if (error != null) toasts.Show(error, true);
            else if (moved.Count > 0) toasts.Show("Moved " + (moved.Count == 1 ? System.IO.Path.GetFileName(moved[0]) : moved.Count + " assets") + " to " + System.IO.Path.GetFileName(target));
            AfterDrop(target, moved);
        }

        private void ImportInto(string target, List<string> files)
        {
            var made = new List<string>();
            foreach (string file in files)
            {
                string destination = AssetDatabase.GenerateUniqueAssetPath(target + "/" + System.IO.Path.GetFileName(file.TrimEnd('/', '\\')));
                try { FileUtil.CopyFileOrDirectory(file, FileUtil.GetPhysicalPath(destination)); made.Add(destination); }
                catch (Exception e) { toasts.Show("Could not copy " + System.IO.Path.GetFileName(file) + ": " + e.Message, true); }
            }
            AssetDatabase.Refresh();
            AfterDrop(target, made);
        }

        private void SavePrefabsInto(string target, List<GameObject> objects)
        {
            var made = new List<string>();
            foreach (var go in objects.Where(g => !objects.Any(o => o != g && g.transform.IsChildOf(o.transform))))
            {
                string path = AssetDatabase.GenerateUniqueAssetPath(target + "/" + ProjectFiles.FileName(go.name) + ".prefab");
                if (PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction) != null) made.Add(path);
            }
            AfterDrop(target, made);
        }

        private void AfterDrop(string target, List<string> made)
        {
            if (target != folder && target.StartsWith(folder + "/", StringComparison.Ordinal)) contentOpenSet.Add(target);
            MarkAllDirty();
            RebuildNow();
            if (made.Count == 0) return;
            picked.Clear();
            picked.UnionWith(made);
            cursor = anchor = made[0];
            PushSelection();
            Reveal(made[0]);
            RefreshContent();
        }
    }
}
