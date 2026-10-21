using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        // Not kept across a script reload: what they wait for (a rename's field, a create's poll, a drag) is gone then.
        [NonSerialized] private string renamingPath;
        [NonSerialized] private bool creating;
        [NonSerialized] private string dragFrom, dropFolder, pingPath;
        private double pingUntil;
        // Scripts and assembly definitions Unity's create items just made, with the name written in them: renamed in
        // place, their class (or assembly) takes the new name, as Unity's own naming gives it.
        private readonly Dictionary<string, string> freshScripts = new Dictionary<string, string>(StringComparer.Ordinal);
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

        // By instance id, as Unity's window selects: nothing is loaded for it (Ctrl+A over thousands of models).
        private void PushSelection()
        {
            var ids = new List<int>();
            foreach (string path in picked)
            {
                int index = IndexOf(path);
                bool isFolder = index >= 0 ? rows[index].IsFolder : AssetDatabase.IsValidFolder(path);
                if (isFolder) continue;
                int id = InstanceIdOf(path);
                if (id != 0) ids.Add(id);
            }
            if (ids.Count == 0) return;
            pushed = new HashSet<int>(ids);
            Selection.instanceIDs = ids.ToArray();
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
            var asset = ObjectOf(item.Path);
            if (asset != null) AssetDatabase.OpenAsset(asset);
        }

        /// <summary>
        /// Shows an asset without selecting it (a ping): its folder opened when it is not on screen, its line flashing.
        /// </summary>
        internal void Ping(string path)
        {
            if (string.IsNullOrEmpty(path) || !built) return;
            // A locked window, or one showing a search, stays where it is: only a line on screen flashes.
            if (IndexOf(path) < 0 && !locked && !Searching)
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
                    else if (current >= 0 && ((rows[current].IsFolder && rows[current].HasChildren) || rows[current].HasSubAssets))
                    {
                        if (!rows[current].Expanded) SetUnfolded(rows[current].Path, true, evt.altKey); else Step(current, 1, false);
                    }
                    break;
                case KeyCode.LeftArrow:
                    if (GridMode) Step(current < 0 ? rows.Count : current, -1, evt.shiftKey);
                    else if (current >= 0)
                    {
                        if (rows[current].Expanded) SetUnfolded(rows[current].Path, false, evt.altKey && rows[current].IsFolder);
                        else { int parent = IndexOf(rows[current].IsSubAsset ? rows[current].AssetPath : ParentOf(rows[current].Path)); if (parent >= 0) { Pick(parent, false, false); RevealIndex(parent); } }
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
                default:
                    // Typing the start of a name jumps to it (the key's character comes in its own event).
                    if (command || evt.altKey || evt.character == '\0' || !TypeAhead(evt.character)) return;
                    break;
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
            if (index < 0 || rows[index].IsSubAsset || packageNames.ContainsKey(path) || !ProjectFiles.IsWritable(path)) return;
            renamingPath = path;
            RevealIndex(index);
            RefreshContent();
        }

        private void CommitRename(TextField field, bool apply)
        {
            if (renamingPath == null) return;
            if (renamingPath == PendingPath) { CommitPending(field, apply); return; }
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
                    else
                    {
                        string renamed = ParentOf(path) + "/" + name + (isFolder ? string.Empty : System.IO.Path.GetExtension(path));
                        Renamed(path, renamed);
                        if (freshScripts.TryGetValue(path, out string written)) { freshScripts.Remove(path); NameScript(renamed, written); }
                    }
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
            if (index >= 0) return rows[index].IsFolder ? rows[index].Path : ParentOf(rows[index].AssetPath);
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

        // Something new: on screen (its folder opened or unfolded), lit up (a file selected, as Unity does), being renamed
        // (not when the user already named it in a save dialog). The grid shows only the open folder: one made in a
        // folder below it opens that folder.
        private void Created(string path, bool selectInUnity, bool rename = true)
        {
            string parent = ParentOf(path);
            if (Searching || showRecent || (parent != folder && (GridMode || !parent.StartsWith(folder + "/", StringComparison.Ordinal)))) Navigate(parent);
            for (string p = parent; p != null && p != folder; p = ParentOf(p)) contentOpenSet.Add(p);
            MarkAllDirty();
            RebuildNow();
            picked.Clear();
            picked.Add(path);
            cursor = anchor = path;
            if (selectInUnity) PushSelection();
            Reveal(path);
            if (rename) BeginRename(path);
            UpdateFooter();
        }

        // Unity's Assets/Create items, as its Project window lists them (ours replace its folder, material, animation and
        // controller); the menus are built in MixerProjectWindow.Unity.
        private static readonly HashSet<string> OurCreateItems = new HashSet<string>(StringComparer.Ordinal)
        {
            "Assets/Create/Folder", "Assets/Create/Material", "Assets/Create/Animation", "Assets/Create/Animator Controller",
        };

        private static List<string> UnityCreateItems()
        {
            try { return Unsupported.GetSubmenus("Assets").Where(p => p.StartsWith("Assets/Create/", StringComparison.Ordinal) && !OurCreateItems.Contains(p)).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        // Unity's own folder for its create items: its Project window's, else Assets (internal; Assets without it).
        private static readonly System.Reflection.MethodInfo ActiveFolderPath = typeof(ProjectWindowUtil).GetMethod("GetActiveFolderPath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, Type.EmptyTypes, null);

        // Unity's create items ask its Project window for the folder and hand it the new asset to name: while one runs from
        // here, UnityCreateFlow's stand-in shows the folder asked for, and a naming it is handed is ended here, in place.
        // What an item makes itself in Unity's folder, or in Assets (its own choice), within a few seconds, moves to the
        // folder asked for, then is renamed in place. Only what appears in those folders counts (a tool, an import or an
        // autosave elsewhere is left alone). An item that asked where to save (a dialog: it took the user a moment) made its
        // asset where the user chose: it is shown there, not moved.
        private void RunUnityCreate(string menuPath, string target)
        {
            var before = new HashSet<string>(AssetDatabase.GetAllAssetPaths(), StringComparer.Ordinal);
            string unityFolder = UnityCreateFlow.Available ? target : "Assets";
            if (!UnityCreateFlow.Available)
            {
                try { if (ActiveFolderPath?.Invoke(null, null) is string active && AssetDatabase.IsValidFolder(active)) unityFolder = active; }
                catch (Exception) { }
            }
            creating = true;
            double started = EditorApplication.timeSinceStartup;
            bool ran = false;
            var naming = UnityCreateFlow.Run(this, target, () => ran = EditorApplication.ExecuteMenuItem(menuPath));
            if (naming != null)
            {
                creating = false;
                BeginPending(naming, target);
                return;
            }
            if (!ran) { creating = false; toasts.Show("Unity could not run " + menuPath.Substring("Assets/".Length) + ".", true); return; }
            bool asked = EditorApplication.timeSinceStartup - started > 1.0;
            double until = EditorApplication.timeSinceStartup + 3.0, next = 0;
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                double now = EditorApplication.timeSinceStartup;
                if (now < next) return;
                next = now + 0.2;
                var made = AssetDatabase.GetAllAssetPaths().Where(p => !before.Contains(p) && (asked
                    ? p.StartsWith("Assets/", StringComparison.Ordinal)
                    : (ParentOf(p) == unityFolder || ParentOf(p) == "Assets"))).ToList();
                if (made.Count == 0 && now < until) return;
                EditorApplication.update -= poll;
                creating = false;
                // Only the topmost of what appeared (a folder's contents go with it).
                made = made.Where(p => !made.Any(o => o != p && p.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
                if (made.Count == 0 || this == null) return;
                if (asked) { Created(made[0], selectInUnity: !AssetDatabase.IsValidFolder(made[0]), rename: false); return; }
                string first = null;
                foreach (string path in made)
                {
                    string place = path;
                    if (ParentOf(path) != target)
                    {
                        string moved = AssetDatabase.GenerateUniqueAssetPath(target + "/" + System.IO.Path.GetFileName(path));
                        if (string.IsNullOrEmpty(AssetDatabase.MoveAsset(path, moved))) place = moved;
                    }
                    // What Unity wrote in a script: the file's first name, spaces left out ("NewBehaviourScript").
                    if (IsNamedScript(place))
                    {
                        string written = ScriptName(System.IO.Path.GetFileNameWithoutExtension(path));
                        freshScripts[place] = NameScript(place, written);
                    }
                    first = first ?? place;
                }
                Created(first, selectInUnity: !AssetDatabase.IsValidFolder(first));
            };
            EditorApplication.update += poll;
        }

        private static bool IsNamedScript(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);

        // A C# name from a file name, as Unity's script templates make it: no spaces, letters, digits and _.
        private static string ScriptName(string fileName)
        {
            var name = new string(fileName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            return name.Length == 0 ? "NewScript" : char.IsDigit(name[0]) ? "_" + name : name;
        }

        // Gives a script's class (or an assembly definition's name) the name of its file, where it still holds the name
        // Unity wrote: a second "NewBehaviourScript" would no longer compile. Returns the name the file holds now.
        private string NameScript(string path, string written)
        {
            string wanted = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? ScriptName(System.IO.Path.GetFileNameWithoutExtension(path)) : System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(written) || wanted == written) return written;
            try
            {
                string full = FileUtil.GetPhysicalPath(path);
                byte[] raw = File.ReadAllBytes(full);
                bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                string text = new System.Text.UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
                string changed = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? Regex.Replace(text, @"\b" + Regex.Escape(written) + @"\b", wanted)
                    : Regex.Replace(text, @"(""name""\s*:\s*"")" + Regex.Escape(written) + @"""", m => m.Groups[1].Value + wanted + "\"");
                if (changed == text) return written;
                File.WriteAllText(full, changed, new System.Text.UTF8Encoding(bom));
                AssetDatabase.ImportAsset(path);
                return wanted;
            }
            catch (Exception e) { toasts.Show("Could not name " + System.IO.Path.GetFileName(path) + "'s class: " + e.Message, true); return written; }
        }

        // ---- Delete, duplicate, copy ----------------------------------------------------------------------------------

        // The lines lit up, without those inside a folder also lit up (they go with it), packages' roots left out.
        private List<string> PickedRoots()
        {
            var paths = picked.Where(p => !packageNames.ContainsKey(p) && p != "Assets" && p != PackagesPath && p.IndexOf(ProjectFiles.SubAssetMark) < 0).ToList();
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

        private void CopyPaths() => EditorGUIUtility.systemCopyBuffer = string.Join("\n", picked.Select(p => p.IndexOf(ProjectFiles.SubAssetMark) < 0 ? p : p.Substring(0, p.IndexOf(ProjectFiles.SubAssetMark))).Distinct().OrderBy(p => p, StringComparer.Ordinal));

        // ---- Context menus ----------------------------------------------------------------------------------------------

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            int index = ItemAtPoint(evt.mousePosition);
            var item = Row(index);
            string target = CreateTarget();
            if (item != null && item.IsSubAsset)
            {
                // A model's mesh, material or clip: opened, shown, its file's path; Unity's Assets menu does the rest.
                if (!picked.Contains(item.Path)) Pick(index, false, false);
                evt.menu.AppendAction("Open", _ => Activate(item));
                evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(item.AssetPath));
                evt.menu.AppendAction("Copy Path", _ => CopyPaths());
                evt.menu.AppendSeparator();
                AppendUnityAssetsMenu(evt.menu);
                return;
            }
            if (item != null && !item.IsHeading)
            {
                if (!picked.Contains(item.Path)) Pick(index, false, false);
                target = item.IsFolder ? item.Path : ParentOf(item.Path);
                bool writable = ProjectFiles.IsWritable(item.Path) && !packageNames.ContainsKey(item.Path);
                var whenWritable = writable ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                evt.menu.AppendAction("Open", _ => Activate(item));
                if ((item.IsFolder && item.HasChildren) || item.HasSubAssets) evt.menu.AppendAction(item.Expanded ? "Fold" : "Unfold", _ => SetUnfolded(item.Path, !item.Expanded, false));
                if (!item.IsFolder) evt.menu.AppendAction("Where it is used", _ => ShowUsages(item.Path));
                if (Searching || showRecent) evt.menu.AppendAction("Show in its folder", _ => { Navigate(ParentOf(item.Path)); Reveal(item.Path); });
                evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(item.Path));
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Rename", _ => BeginRename(item.Path), whenWritable);
                evt.menu.AppendAction("Duplicate", _ => DuplicatePicked(), whenWritable);
                evt.menu.AppendAction("Delete", _ => DeletePicked(), whenWritable);
                evt.menu.AppendAction("Copy Path", _ => CopyPaths());
                evt.menu.AppendAction("Copy", _ => CopyPicked());
                evt.menu.AppendAction("Paste", _ => Paste(), ClipboardPaths().Count > 0 && ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Reimport", _ => { foreach (string path in PickedRoots()) AssetDatabase.ImportAsset(path, ImportAssetOptions.ImportRecursive); });
                evt.menu.AppendAction("Import New Asset…", _ => ImportNewAsset(target), ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                AppendFavouriteActions(evt.menu, item);
                evt.menu.AppendSeparator();
                // Unity's own Assets menu (Import, Export Package, Find References, Properties, other tools' items).
                AppendUnityAssetsMenu(evt.menu);
                evt.menu.AppendSeparator();
            }
            else if (ClipboardPaths().Count > 0) evt.menu.AppendAction("Paste", _ => Paste(), ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            if (item == null || item.IsHeading) evt.menu.AppendAction("Import New Asset…", _ => ImportNewAsset(target), ProjectFiles.IsWritable(target) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
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
            if (item != null && (item.Path == FavouritesPath || FavouriteOf(item) != null)) { BuildFavouriteMenu(evt, item); return; }
            if (item == null || item.Path == RecentPath)
            {
                if (item != null) evt.menu.AppendAction("Forget the recent assets", _ => RecentAssets.Clear());
                return;
            }
            if (item.Path == PackagesPath) { evt.menu.AppendAction("Open", _ => Navigate(PackagesPath)); return; }
            evt.menu.AppendAction("Open", _ => Navigate(item.Path));
            evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(item.Path));
            evt.menu.AppendAction("Copy Path", _ => EditorGUIUtility.systemCopyBuffer = item.Path);
            if (!packageNames.ContainsKey(item.Path))
            {
                if (ProjectFavourites.Contains(item.Path)) evt.menu.AppendAction("Remove from Favourites", _ => ProjectFavourites.RemoveAsset(item.Path));
                else evt.menu.AppendAction("Add to Favourites", _ => AddFavourites(new[] { item.Path }));
            }
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
        // on a folder (a move). What lies in a folder also dragged goes with it, not on its own.
        private void StartDrag()
        {
            string from = dragFrom;
            dragFrom = null;
            var paths = picked.Contains(from) ? Topmost(picked.Where(p => !packageNames.ContainsKey(p))) : new List<string> { from };
            var objects = paths.Select(ObjectOf).Where(o => o != null).ToArray();
            if (objects.Length == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objects;
            // A sub-asset travels as its object only (a mesh into a field), not as its file.
            DragAndDrop.paths = paths.Where(p => p.IndexOf(ProjectFiles.SubAssetMark) < 0).ToArray();
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
                if (item != null && (item.Path == FavouritesPath || FavouriteOf(item) != null)) return FavouritesPath;
                return item == null || item.Path == RecentPath || item.Path == PackagesPath ? null : item.Path;
            }
            var row = Row(ItemAtPoint(world));
            if (row != null && !row.IsHeading) return row.IsFolder ? row.Path : ParentOf(row.AssetPath);
            return Searching || showRecent || folder == PackagesPath ? null : folder;
        }

        private enum DropKind { None, Move, Import, Prefab, Favourite }

        // The paths that are not inside another of them (a sub-asset's key stays with its file).
        private static List<string> Topmost(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            return list.Where(p => !list.Any(o => o != p && p.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
        }

        // An Explorer path inside this project: its asset path (Assets/..., Packages/...), else null.
        private static string ProjectPathOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
            try
            {
                string root = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd('\\', '/');
                string full = Path.GetFullPath(path).TrimEnd('\\', '/');
                if (!SafePaths.IsSameOrChild(full, root) || full.Length <= root.Length) return null;
                string relative = full.Substring(root.Length + 1).Replace('\\', '/');
                return string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(relative)) ? null : relative;
            }
            catch (Exception) { return null; }
        }

        private static DropKind KindOfDrop(string target, out List<string> sources)
        {
            sources = new List<string>();
            // Files dragged from Explorer that are in this project already are its assets (a copy would duplicate their GUIDs).
            var paths = (DragAndDrop.paths ?? Array.Empty<string>()).Select(p => ProjectPathOf(p) ?? p).Distinct().ToArray();
            // Onto the favourites: the project's folders and assets dragged there become favourites.
            if (target == FavouritesPath)
            {
                sources = paths.Where(p => !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p))).ToList();
                return sources.Count > 0 ? DropKind.Favourite : DropKind.None;
            }
            if (target == null || !ProjectFiles.IsWritable(target)) return DropKind.None;
            var assets = Topmost(paths.Where(p => !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p))));
            if (assets.Count > 0)
            {
                sources = assets.Where(p => ProjectFiles.IsWritable(p) && p != target && ParentOf(p) != target && !target.StartsWith(p + "/", StringComparison.Ordinal)).ToList();
                return sources.Count > 0 ? DropKind.Move : DropKind.None;
            }
            // Outside files and folders, never a drive's root (it has no name to copy under).
            var files = paths.Where(p => Path.IsPathRooted(p) && (File.Exists(p) || Directory.Exists(p)) && System.IO.Path.GetFileName(p.TrimEnd('/', '\\')).Length > 0).ToList();
            if (files.Count > 0) { sources = files; return DropKind.Import; }
            if (DragAndDrop.objectReferences.OfType<GameObject>().Any(g => g != null && g.scene.IsValid())) return DropKind.Prefab;
            return DropKind.None;
        }

        private void OnDragUpdated(DragUpdatedEvent evt, bool onTree)
        {
            string target = DropTargetAt(evt.mousePosition, onTree);
            var kind = KindOfDrop(target, out _);
            SetDropFolder(kind == DropKind.None ? null : target);
            DragAndDrop.visualMode = kind == DropKind.None ? DragAndDropVisualMode.Rejected : kind == DropKind.Move ? DragAndDropVisualMode.Move
                : kind == DropKind.Favourite ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Copy;
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
            if (kind == DropKind.Favourite) AddFavourites(sources);
            else if (kind == DropKind.Move) MoveInto(target, sources);
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

        // Files from Explorer are copied in; a .unitypackage is imported, as Unity's window does (its own dialog, its own
        // folders). A folder is never copied into itself.
        private void ImportInto(string target, List<string> files)
        {
            var made = new List<string>();
            string into = Path.GetFullPath(FileUtil.GetPhysicalPath(target));
            foreach (string file in files)
            {
                string name = System.IO.Path.GetFileName(file.TrimEnd('/', '\\'));
                if (File.Exists(file) && name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.ImportPackage(file, true);
                    continue;
                }
                if (Directory.Exists(file) && SafePaths.IsSameOrChild(into, Path.GetFullPath(file).TrimEnd('/', '\\')))
                {
                    toasts.Show(name + " cannot be copied into itself.", true);
                    continue;
                }
                string destination = AssetDatabase.GenerateUniqueAssetPath(target + "/" + name);
                try { FileUtil.CopyFileOrDirectory(file, FileUtil.GetPhysicalPath(destination)); made.Add(destination); }
                catch (Exception e) { toasts.Show("Could not copy " + name + ": " + e.Message, true); }
            }
            if (made.Count == 0) return;
            AssetDatabase.Refresh();
            AfterDrop(target, made);
        }

        // Scene objects dropped here become prefabs. As Unity asks: a prefab instance's root becomes a new original prefab
        // or a variant of its prefab. A part inside a prefab instance, or an object of Prefab Mode, cannot be saved alone.
        private void SavePrefabsInto(string target, List<GameObject> objects)
        {
            var made = new List<string>();
            foreach (var go in objects.Where(g => !objects.Any(o => o != g && g.transform.IsChildOf(o.transform))))
            {
                if (EditorSceneManager.IsPreviewScene(go.scene)) { toasts.Show(go.name + " is in Prefab Mode: save it from its prefab.", true); continue; }
                bool instance = PrefabUtility.IsPartOfPrefabInstance(go);
                if (instance && PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go && !PrefabUtility.IsAddedGameObjectOverride(go))
                {
                    toasts.Show(go.name + " is part of a prefab instance: drop its root, or unpack it first.", true);
                    continue;
                }
                string path = AssetDatabase.GenerateUniqueAssetPath(target + "/" + ProjectFiles.FileName(go.name) + ".prefab");
                try
                {
                    if (instance && PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                    {
                        int choice = EditorUtility.DisplayDialogComplex("Create Prefab", "Would you like to create a new original Prefab or a variant of " + go.name + "'s Prefab?", "Original Prefab", "Cancel", "Prefab Variant");
                        if (choice == 1) continue;
                        if (choice == 0) PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
                    }
                    if (PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction) != null) made.Add(path);
                }
                catch (Exception e) { toasts.Show("Could not save " + go.name + " as a prefab: " + e.Message, true); }
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
