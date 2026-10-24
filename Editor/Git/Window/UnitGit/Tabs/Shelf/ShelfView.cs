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
        private readonly Dictionary<string, List<ShelfFile>> shelfFiles = new Dictionary<string, List<ShelfFile>>(StringComparer.Ordinal);
        private readonly HashSet<string> expandedShelves = new HashSet<string>(StringComparer.Ordinal);
        private string selectedShelfFile = string.Empty;
        private VisualElement shelfListRoot;
        private VisualElement shelfDiffRoot;
        private UnitGitDiffViewer shelfDiffViewer;
        private int shelfDiffRequest;

        private sealed class ShelfFile
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
            var selected = entries.FirstOrDefault(e => e.Ref == selectedShelf);
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
            shelfListRoot = scroll.contentContainer;
            pane.Add(scroll);
            FillShelves(entries);
            split.Add(pane);

            shelfDiffRoot = new VisualElement();
            shelfDiffRoot.AddToClassList("unitgit-diff-pane");
            split.Add(shelfDiffRoot);
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
            if (shelfListRoot == null) return;
            shelfListRoot.Clear();
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
                shelfListRoot.Add(empty);
                return;
            }
            foreach (var entry in entries)
            {
                bool expanded = expandedShelves.Contains(entry.Ref);
                var row = new VisualElement();
                row.AddToClassList("ug-row");
                row.AddToClassList("ug-shelf-row");
                row.EnableInClassList("ug-row--selected", entry.Ref == selectedShelf && string.IsNullOrEmpty(selectedShelfFile));
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
                    selectedShelf = item.Ref;
                    selectedShelfFile = string.Empty;
                    if (evt.button == 0 && evt.clickCount == 2) { UnshelveSelectedShelf(); return; }
                    if (evt.button == 0)
                    {
                        if (!expandedShelves.Remove(item.Ref)) expandedShelves.Add(item.Ref);
                        LoadShelfFiles(item.Ref);
                    }
                    RefreshShelf();
                    if (evt.button == 1) ShowShelfMenu(item);
                });
                shelfListRoot.Add(row);
                if (!expanded) continue;
                if (!shelfFiles.TryGetValue(entry.Ref, out var files))
                {
                    var loading = UnitGitUi.Text("Reading its files…", "ug-shelf-loading");
                    shelfListRoot.Add(loading);
                    continue;
                }
                foreach (var file in files) shelfListRoot.Add(BuildShelfFileRow(file));
            }
        }

        private VisualElement BuildShelfFileRow(ShelfFile file)
        {
            var row = new VisualElement();
            row.AddToClassList("ug-row");
            row.style.paddingLeft = 6 + 16 + 18;
            row.EnableInClassList("ug-row--selected", file.Shelf == selectedShelf && file.Path == selectedShelfFile);
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
                selectedShelf = file.Shelf;
                selectedShelfFile = file.Path;
                RefreshShelf();
                ShowShelfDiff();
            });
            return row;
        }

        private void RefreshShelf()
        {
            FillShelves(GetShelfEntries());
            var toolbar = shelfListRoot?.parent?.parent?.parent?.Q(className: "ug-toolbar");
            if (toolbar != null)
                foreach (var button in toolbar.Query<Button>(className: "ug-icon-button").ToList())
                    button.SetEnabled(!string.IsNullOrEmpty(selectedShelf));
        }

        // A shelf's files: its tracked changes, and the new files it kept apart (its third parent).
        private void LoadShelfFiles(string reference)
        {
            if (shelfFiles.ContainsKey(reference)) return;
            string root = gitService.ProjectRoot;
            Task.Run(() =>
                {
                    var service = new UnitGitService(root);
                    var files = new List<ShelfFile>();
                    var tracked = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "stash", "show", "--name-status", reference);
                    if (tracked.Success) files.AddRange(ParseNameStatus(tracked.StandardOutput, reference, false));
                    var untracked = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "show", "--name-status", "--format=", reference + "^3");
                    if (untracked.Success) files.AddRange(ParseNameStatus(untracked.StandardOutput, reference, true));
                    return files;
                })
                .ContinueWith(task => QueueMainThreadAction(() =>
                {
                    shelfFiles[reference] = task.Status == TaskStatus.RanToCompletion ? task.Result : new List<ShelfFile>();
                    if (activeTab == UnitGitTab.Shelf) RefreshShelf();
                }));
            EnsureEditorUpdatePump();
        }

        private static IEnumerable<ShelfFile> ParseNameStatus(string output, string reference, bool untracked)
        {
            foreach (string raw in (output ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string[] parts = raw.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                yield return new ShelfFile { Shelf = reference, Status = parts[0][0], Path = parts[parts.Length - 1].Trim(), Untracked = untracked };
            }
        }

        private void ShowShelfDiff()
        {
            if (shelfDiffRoot == null) return;
            shelfDiffRoot.Clear();
            shelfDiffViewer = null;
            var file = shelfFiles.TryGetValue(selectedShelf ?? string.Empty, out var files) ? files.FirstOrDefault(f => f.Path == selectedShelfFile) : null;
            if (file == null)
            {
                shelfDiffRoot.Add(BuildDiffPlaceholder(GetShelves().Count == 0 ? "Shelved changes show up here" : "Select a shelved file to see its changes"));
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
            toolbar.Add(UnitGitDiffViewer.BuildViewOptions(() => shelfDiffViewer, ShowShelfDiff));
            toolbar.Add(UnitGitUi.Separator());
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.PreviousDifference, "Previous change (Shift+F7)", () => shelfDiffViewer?.MoveChange(-1)));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.NextDifference, "Next change (F7)", () => shelfDiffViewer?.MoveChange(1)));
            shelfDiffRoot.Add(toolbar);
            var placeholder = BuildDiffPlaceholder("Loading the diff…", spinner: true);
            shelfDiffRoot.Add(placeholder);

            string root = gitService.ProjectRoot;
            string revision = file.Untracked ? file.Shelf + "^3" : file.Shelf;
            string path = file.Path;
            int request = ++shelfDiffRequest;
            var whitespace = UnitGitDiffViewer.Whitespace;
            Task.Run(() =>
                {
                    var diff = new UnitGitService(root).GetCommitFileDiff(revision, path, whitespace);
                    PrepareDiff(diff);
                    return diff;
                })
                .ContinueWith(task => QueueMainThreadAction(() =>
                {
                    if (request != shelfDiffRequest || shelfDiffRoot == null || activeTab != UnitGitTab.Shelf) return;
                    placeholder.RemoveFromHierarchy();
                    shelfDiffViewer = new UnitGitDiffViewer();
                    var diff = task.Status == TaskStatus.RanToCompletion ? task.Result : new UnitGitDiff();
                    diff.LeftTitle = "Before";
                    diff.RightTitle = "Shelved";
                    shelfDiffViewer.SetDiff(diff);
                    shelfDiffViewer.List.focusable = true;
                    shelfDiffViewer.List.RegisterCallback<KeyDownEvent>(evt =>
                    {
                        if (evt.keyCode != KeyCode.F7) return;
                        shelfDiffViewer?.MoveChange(evt.shiftKey ? -1 : 1);
                        evt.StopPropagation();
                    });
                    shelfDiffRoot.Add(shelfDiffViewer);
                    var shown = shelfDiffViewer;
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
