using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The shelf, as in JetBrains IDEs: changes set aside (Git stashes), each with its files; the selected file's diff on the
    // right. Unshelve brings them back (and removes the shelf), Apply keeps a copy, Delete forgets it.
    internal sealed partial class UnitGitWindow
    {
        private const float ShelfRowHeight = 26f;

        internal sealed class ShelfFile
        {
            public string Shelf, Path;
            public char Status;
            public bool Untracked;
        }

        private sealed class ShelfEntry
        {
            public string Ref, Branch, Message, Line;
        }

        private VisualElement BuildShelfBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");
            var split = BuildTrackedSplit(ShelfConsoleSplitPref, 0, 420f);

            var pane = new VisualElement();
            pane.AddToClassList("unitgit-shelf-pane");
            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            var shelve = UnitGitUi.Pill("Shelve changes", ShelveAll, "primary", UnitGitIconKind.Shelve, "Set every local change aside, as a new shelf");
            shelve.SetEnabled(snapshot.Changes.Count > 0);
            toolbar.Add(shelve);
            toolbar.Add(UnitGitUi.Spacer());
            var entries = GetShelfEntries();
            var selected = entries.FirstOrDefault(e => e.Ref == shelfTab.selectedShelf);
            var unshelve = UnitGitUi.Icon(UnitGitIconKind.Pull, "Unshelve: bring the selected shelf back and remove it", UnshelveSelectedShelf);
            var apply = UnitGitUi.Icon(UnitGitIconKind.Commit, "Apply: bring the selected shelf back and keep it", ApplySelectedShelf);
            var delete = UnitGitUi.Icon(UnitGitIconKind.Delete, "Delete the selected shelf", DropSelectedShelf);
            foreach (var button in new[] { unshelve, apply, delete })
            {
                button.SetEnabled(selected != null);
                toolbar.Add(button);
            }
            pane.Add(toolbar);

            var scroll = new ScrollView();
            scroll.name = "unitgit-shelf-list";
            scroll.AddToClassList("unitgit-shelf-list");
            shelfTab.shelfListRoot = scroll.contentContainer;
            pane.Add(scroll);
            FillShelves(entries);
            split.Add(pane);

            shelfTab.shelfDiffRoot = new VisualElement();
            shelfTab.shelfDiffRoot.AddToClassList("unitgit-diff-pane");
            split.Add(shelfTab.shelfDiffRoot);
            workspace.Add(split);
            ShowShelfDiff();
            return workspace;
        }

        private List<ShelfEntry> GetShelfEntries()
        {
            var entries = new List<ShelfEntry>();
            foreach (string line in GetShelves())
            {
                // "stash@{0}: On master: message" or "stash@{0}: WIP on master: 1a2b3c subject"
                string reference = ExtractShelfRef(line);
                string rest = line.Length > reference.Length + 1 ? line.Substring(reference.Length + 1).Trim() : string.Empty;
                string branch = string.Empty, message = rest;
                int colon = rest.IndexOf(':');
                if (colon > 0)
                {
                    string where = rest.Substring(0, colon);
                    message = rest.Substring(colon + 1).Trim();
                    int on = where.LastIndexOf("on ", StringComparison.OrdinalIgnoreCase);
                    branch = on >= 0 ? where.Substring(on + 3).Trim() : where;
                }
                entries.Add(new ShelfEntry { Ref = reference, Branch = branch, Message = message.Length == 0 ? reference : message, Line = line });
            }
            return entries;
        }

        private void FillShelves(List<ShelfEntry> entries)
        {
            if (shelfTab.shelfListRoot == null) return;
            shelfTab.shelfListRoot.Clear();
            if (entries.Count == 0)
            {
                var empty = new VisualElement();
                empty.AddToClassList("ug-empty");
                var mark = new VisualElement();
                mark.AddToClassList("ug-empty__mark");
                mark.Add(new UnitGitIconElement(UnitGitIconKind.Shelve));
                empty.Add(mark);
                empty.Add(UnitGitUi.Text("Nothing on the shelf", "ug-empty__title"));
                empty.Add(UnitGitUi.Text("Shelve changes to set them aside, switch branches or pull, and bring them back later.", "ug-empty__text"));
                shelfTab.shelfListRoot.Add(empty);
                return;
            }
            foreach (var entry in entries)
            {
                bool expanded = shelfTab.expandedShelves.Contains(entry.Ref);
                var row = new VisualElement();
                row.AddToClassList("ug-row");
                row.AddToClassList("ug-shelf-row");
                row.EnableInClassList("ug-row--selected", entry.Ref == shelfTab.selectedShelf && string.IsNullOrEmpty(shelfTab.selectedShelfFile));
                var chevron = new UnitGitIconElement(expanded ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed);
                chevron.AddToClassList("ug-row__chevron");
                row.Add(chevron);
                var icon = new UnitGitIconElement(UnitGitIconKind.Shelve);
                icon.AddToClassList("ug-shelf-row__icon");
                row.Add(icon);
                row.Add(UnitGitUi.Text(entry.Message, "ug-row__name"));
                row.Add(UnitGitUi.Spacer());
                if (!string.IsNullOrEmpty(entry.Branch)) row.Add(UnitGitUi.Chip(entry.Branch, "local"));
                row.tooltip = entry.Line + "\nDouble click: unshelve";
                var item = entry;
                row.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 0 && evt.button != 1) return;
                    shelfTab.selectedShelf = item.Ref;
                    shelfTab.selectedShelfFile = string.Empty;
                    if (evt.button == 0 && evt.clickCount == 2) { UnshelveSelectedShelf(); return; }
                    if (evt.button == 0)
                    {
                        if (!shelfTab.expandedShelves.Remove(item.Ref)) shelfTab.expandedShelves.Add(item.Ref);
                        LoadShelfFiles(item.Ref);
                    }
                    RefreshShelf();
                    if (evt.button == 1) ShowShelfMenu(item);
                });
                shelfTab.shelfListRoot.Add(row);
                if (!expanded) continue;
                if (!shelfTab.shelfFiles.TryGetValue(entry.Ref, out var files))
                {
                    var loading = UnitGitUi.Text("Reading its files…", "ug-shelf-loading");
                    shelfTab.shelfListRoot.Add(loading);
                    continue;
                }
                foreach (var file in files) shelfTab.shelfListRoot.Add(BuildShelfFileRow(file));
            }
        }

        private VisualElement BuildShelfFileRow(ShelfFile file)
        {
            var row = new VisualElement();
            row.AddToClassList("ug-row");
            row.style.paddingLeft = 6 + 16 + 18;
            row.EnableInClassList("ug-row--selected", file.Shelf == shelfTab.selectedShelf && file.Path == shelfTab.selectedShelfFile);
            row.Add(BuildProjectFileIcon(file.Path, "ug-row__icon"));
            string key = file.Untracked ? "untracked" : CommitStatusKey(file.Status);
            var name = UnitGitUi.Text(GetFileLeaf(file.Path), "ug-row__name");
            name.AddToClassList("ug-status--" + key);
            row.Add(name);
            row.Add(UnitGitUi.Text(GetDirectoryLabel(file.Path), "ug-row__path"));
            var letter = UnitGitUi.Text(file.Untracked ? "U" : file.Status.ToString(), "ug-row__letter");
            letter.AddToClassList("ug-status--" + key);
            row.Add(letter);
            row.tooltip = file.Path;
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                shelfTab.selectedShelf = file.Shelf;
                shelfTab.selectedShelfFile = file.Path;
                RefreshShelf();
                ShowShelfDiff();
            });
            return row;
        }

        private void RefreshShelf()
        {
            FillShelves(GetShelfEntries());
            var toolbar = shelfTab.shelfListRoot?.parent?.parent?.parent?.Q(className: "ug-toolbar");
            if (toolbar != null)
                foreach (var button in toolbar.Query<Button>(className: "ug-icon-button").ToList())
                    button.SetEnabled(!string.IsNullOrEmpty(shelfTab.selectedShelf));
        }

        // A shelf's files: its tracked changes, and the new files it kept apart (its third parent).
        private void LoadShelfFiles(string reference)
        {
            if (shelfTab.shelfFiles.ContainsKey(reference)) return;
            string root = gitService.ProjectRoot;
            Task.Run(() =>
                {
                    var service = new UnitGitService(root);
                    var files = new List<ShelfFile>();
                    // -z: paths exactly as Git has them (a non-ASCII name would otherwise come quoted and escaped).
                    var tracked = service.RunGit(GitAccess.Read, UnitGitService.DefaultTimeoutMilliseconds, "stash", "show", "--name-status", "-z", reference);
                    if (tracked.Success) files.AddRange(ParseNameStatus(tracked.StandardOutput, reference, false));
                    var untracked = service.RunGit(GitAccess.Read, UnitGitService.DefaultTimeoutMilliseconds, "show", "--name-status", "-z", "--format=", reference + "^3");
                    if (untracked.Success) files.AddRange(ParseNameStatus(untracked.StandardOutput, reference, true));
                    return files;
                })
                .ContinueWith(task => QueueMainThreadAction(() =>
                {
                    shelfTab.shelfFiles[reference] = task.Status == TaskStatus.RanToCompletion ? task.Result : new List<ShelfFile>();
                    if (activeTab == UnitGitTab.Shelf) RefreshShelf();
                }));
            EnsureEditorUpdatePump();
        }

        // "--name-status -z": "status NUL path NUL", a rename or copy "R100 NUL old NUL new NUL"; the file is its new path.
        internal static List<(char status, string path)> ParseNameStatus(string output)
        {
            var files = new List<(char, string)>();
            string[] tokens = (output ?? string.Empty).Split('\0');
            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                string status = tokens[i].TrimStart('\n');
                if (status.Length == 0) continue;
                string path = tokens[++i];
                if ((status[0] == 'R' || status[0] == 'C') && i + 1 < tokens.Length) path = tokens[++i];
                if (path.Length > 0) files.Add((status[0], path));
            }
            return files;
        }

        private static IEnumerable<ShelfFile> ParseNameStatus(string output, string reference, bool untracked) =>
            ParseNameStatus(output).Select(file => new ShelfFile { Shelf = reference, Status = file.status, Path = file.path, Untracked = untracked });

        private void ShowShelfDiff()
        {
            if (shelfTab.shelfDiffRoot == null) return;
            shelfTab.shelfDiffRoot.Clear();
            shelfTab.shelfDiffViewer = null;
            var file = shelfTab.shelfFiles.TryGetValue(shelfTab.selectedShelf ?? string.Empty, out var files) ? files.FirstOrDefault(f => f.Path == shelfTab.selectedShelfFile) : null;
            if (file == null)
            {
                shelfTab.shelfDiffRoot.Add(BuildDiffPlaceholder(GetShelves().Count == 0 ? "Shelved changes show up here" : "Select a shelved file to see its changes"));
                return;
            }
            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            toolbar.AddToClassList("unitgit-diff-toolbar");
            toolbar.Add(BuildProjectFileIcon(file.Path, "ug-diff-file__icon"));
            var name = UnitGitUi.Text(GetFileLeaf(file.Path), "ug-diff-file__name");
            name.AddToClassList("ug-status--" + (file.Untracked ? "untracked" : CommitStatusKey(file.Status)));
            toolbar.Add(name);
            toolbar.Add(UnitGitUi.Text(GetDirectoryLabel(file.Path), "ug-diff-file__path"));
            toolbar.Add(UnitGitUi.Spacer());
            toolbar.Add(UnitGitDiffViewer.BuildViewOptions(() => shelfTab.shelfDiffViewer, ShowShelfDiff));
            toolbar.Add(UnitGitUi.Separator());
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.PreviousDifference, "Previous change (Shift+F7)", () => shelfTab.shelfDiffViewer?.MoveChange(-1)));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.NextDifference, "Next change (F7)", () => shelfTab.shelfDiffViewer?.MoveChange(1)));
            shelfTab.shelfDiffRoot.Add(toolbar);
            var placeholder = BuildDiffPlaceholder("Loading the diff…", spinner: true);
            shelfTab.shelfDiffRoot.Add(placeholder);

            string root = gitService.ProjectRoot;
            string revision = file.Untracked ? file.Shelf + "^3" : file.Shelf;
            string path = file.Path;
            int request = ++shelfTab.shelfDiffRequest;
            var whitespace = UnitGitDiffViewer.Whitespace;
            Task.Run(() =>
                {
                    var diff = new UnitGitService(root).GetCommitFileDiff(revision, path, whitespace);
                    PrepareDiff(diff);
                    return diff;
                })
                .ContinueWith(task => QueueMainThreadAction(() =>
                {
                    if (request != shelfTab.shelfDiffRequest || shelfTab.shelfDiffRoot == null || activeTab != UnitGitTab.Shelf) return;
                    placeholder.RemoveFromHierarchy();
                    shelfTab.shelfDiffViewer = new UnitGitDiffViewer();
                    var diff = task.Status == TaskStatus.RanToCompletion ? task.Result : new UnitGitDiff();
                    diff.LeftTitle = "Before";
                    diff.RightTitle = "Shelved";
                    shelfTab.shelfDiffViewer.SetDiff(diff);
                    shelfTab.shelfDiffViewer.List.focusable = true;
                    shelfTab.shelfDiffViewer.List.RegisterCallback<KeyDownEvent>(evt =>
                    {
                        if (evt.keyCode != KeyCode.F7) return;
                        shelfTab.shelfDiffViewer?.MoveChange(evt.shiftKey ? -1 : 1);
                        evt.StopPropagation();
                    });
                    shelfTab.shelfDiffRoot.Add(shelfTab.shelfDiffViewer);
                    var shown = shelfTab.shelfDiffViewer;
                    shown.schedule.Execute(() => shown.MoveChange(1)).StartingIn(30);
                }));
            EnsureEditorUpdatePump();
        }

        private void ShowShelfMenu(ShelfEntry entry)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Unshelve (bring back and remove)"), false, UnshelveSelectedShelf);
            menu.AddItem(new GUIContent("Apply (bring back and keep)"), false, ApplySelectedShelf);
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Delete…"), false, DropSelectedShelf);
            menu.AddItem(new GUIContent("Copy name"), false, () => EditorGUIUtility.systemCopyBuffer = entry.Message);
            menu.ShowAsContext();
        }

        private List<string> GetShelves()
        {
            return snapshot != null ? snapshot.Shelves : new List<string>();
        }

        private static string ExtractShelfRef(string shelf)
        {
            if (string.IsNullOrWhiteSpace(shelf))
            {
                return string.Empty;
            }

            int colon = shelf.IndexOf(':');
            return colon > 0 ? shelf.Substring(0, colon) : shelf;
        }
    }
}
