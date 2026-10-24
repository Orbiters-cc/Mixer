using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The commit view, as in JetBrains IDEs: every change with a box saying whether it goes into the next commit (Git's
    // staging, set per file or folder at once), grouped as changes and unversioned files, in folders or flat; the diff of
    // the selected file beside it; and the message with Commit and Commit and Push under the list.
    internal sealed partial class UnitGitWindow
    {
        private const string GroupByFolderPref = "Orbiters.UnitGit.Changes.GroupByFolder";
        private const string DownloadsNoticeHiddenKey = "Orbiters.UnitGit.Changes.DownloadsNoticeHidden";
        private const float ChangeRowHeight = 26f;
        private readonly HashSet<string> selectedChangePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> collapsedChangeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Boxes ticked or cleared but not yet written by Git: shown at once, reconciled by the next refresh.
        private readonly Dictionary<string, bool> pendingInclude = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(List<string> paths, bool include)> includeQueue = new List<(List<string> paths, bool include)>();
        private bool includeRunning;
        private int includeGeneration;
        private int includeRunningCount;
        private bool includeRunningAdds;
        // Commit (true: and push) clicked while Git was still including files: it runs as soon as they are in.
        private bool? commitWhenIncluded;
        private double includeStarted;
        private string includePaths;
        private Label commitProgressLabel;
        private string changeFilter = string.Empty;
        private string changeAnchorPath = string.Empty;
        private List<ChangeRow> changeRows = new List<ChangeRow>();
        private ListView changesListView;
        private VisualElement changesNoticesRoot;
        private string changesNoticesKey;
        private Label commitSummaryLabel;
        private Button commitButton;
        private Button commitPushButton;
        private UnitGitCheck amendCheck;

        private sealed class ChangeRow
        {
            public enum RowKind { Group, Folder, File }
            public RowKind Kind;
            public string Key = string.Empty;
            public string Name = string.Empty;
            public int Depth;
            public UnitGitStatusEntry Change;
            public readonly List<UnitGitStatusEntry> Files = new List<UnitGitStatusEntry>();
        }

        private VisualElement BuildLocalChangesBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var split = BuildTrackedSplit(LocalChangesDiffSplitPref, 0, 460f);
            split.Add(BuildLocalChangesListPane());
            localDiffPaneRoot = new VisualElement();
            localDiffPaneRoot.AddToClassList("unitgit-diff-pane-container");
            localDiffPaneRoot.Add(BuildDiffViewerPane());
            split.Add(localDiffPaneRoot);
            workspace.Add(split);

            return workspace;
        }

        private VisualElement BuildLocalChangesListPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-changes-pane");
            // More room: a taller message box.
            pane.RegisterCallback<GeometryChangedEvent>(evt => pane.EnableInClassList("unitgit-changes-pane--tall", evt.newRect.height > 520f));

            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            addButton = UnitGitUi.Pill("Add", AddSelectedUnversioned, null, null, "Add the selected unversioned files to Git (Ctrl+Alt+A)");
            addButton.AddToClassList("unitgit-add-button");
            toolbar.Add(addButton);
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.Rollback, "Rollback: discard the changes of the selected files", RollbackSelectedChanges));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.Shelve, "Shelve every change (set it aside)", ShelveAll));
            toolbar.Add(UnitGitUi.Separator());
            bool grouped = EditorPrefs.GetBool(GroupByFolderPref, true);
            var group = UnitGitUi.Icon(UnitGitIconKind.Folder, "Group by folder", ToggleGroupByFolder);
            group.EnableInClassList("ug-icon-button--on", grouped);
            toolbar.Add(group);
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.ExpandAll, "Expand all", () => { collapsedChangeFolders.Clear(); RefreshChangesList(); }));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.CollapseAll, "Collapse all", CollapseAllChangeFolders));
            toolbar.Add(UnitGitUi.Spacer());
            toolbar.Add(UnitGitUi.Search(changeFilter, "Filter files", value => { changeFilter = value ?? string.Empty; RefreshChangesList(); }, "ug-toolbar__search"));
            pane.Add(toolbar);

            changesNoticesRoot = new VisualElement();
            changesNoticesKey = null;
            UpdateChangesNotices();
            pane.Add(changesNoticesRoot);

            var list = new VisualElement();
            list.AddToClassList("unitgit-changes-list");
            localChangesListRoot = list;
            AddChangedFileGroups(list);
            pane.Add(list);

            pane.Add(BuildCommitPanel());
            UpdateCommitSummary();
            return pane;
        }

        // The notices above the list follow every refresh (a conflict resolved, downloads untracked) without rebuilding the pane.
        private void UpdateChangesNotices()
        {
            if (changesNoticesRoot == null || snapshot == null) return;
            int conflicts = Conflicted().Count;
            bool downloads = snapshot.TrackedIgnoredDownloads.Count > 0 && !SessionState.GetBool(DownloadsNoticeHiddenKey, false);
            string key = conflicts + "|" + (downloads ? snapshot.TrackedIgnoredDownloads.Count + ":" + snapshot.TrackedIgnoredDownloadBytes : "-");
            if (key == changesNoticesKey) return;
            changesNoticesKey = key;
            changesNoticesRoot.Clear();
            if (conflicts > 0) changesNoticesRoot.Add(BuildConflictsNotice());
            if (downloads) changesNoticesRoot.Add(BuildTrackedDownloadsNotice());
        }

        private VisualElement BuildConflictsNotice()
        {
            int count = Conflicted().Count;
            return BuildNotice(UnitGitIconKind.Conflict, count + " file" + (count == 1 ? " has" : "s have") + " conflicts",
                "Both sides changed the same parts. Resolve them before committing.", "Resolve", () => SetActiveTab(UnitGitTab.Conflicts), "unitgit-notice--conflict", null);
        }

        // MCB version downloads committed before Mixer Git ignored them: Git keeps tracking them until told otherwise.
        private VisualElement BuildTrackedDownloadsNotice()
        {
            var paths = new List<string>(snapshot.TrackedIgnoredDownloads);
            VisualElement notice = null;
            notice = BuildNotice(UnitGitIconKind.Backup, "MCB downloads are in Git (" + UnitGitService.FormatBytes(snapshot.TrackedIgnoredDownloadBytes) + ")",
                snapshot.TrackedIgnoredDownloads.Count + " version files MCB downloads again when needed. Stop tracking them to keep your repository small: they stay on disk.",
                "Stop tracking", () =>
                {
                    if (busy || commitWhenIncluded != null) return;
                    // Gone at once; a failure brings it back with the next refresh.
                    notice?.AddToClassList("unitgit-notice--leave");
                    notice?.schedule.Execute(() => notice.RemoveFromHierarchy()).StartingIn(180);
                    RunAction("Stop tracking MCB downloads", () => gitService.StopTracking(paths), working: "Untracking MCB downloads…",
                        success: paths.Count + " MCB downloads leave Git with your next commit");
                }, null,
                "Runs git rm --cached on these files: they stay on disk, and their removal is staged for your next commit. Commits already made keep their copies.",
                () => SessionState.SetBool(DownloadsNoticeHiddenKey, true));
            return notice;
        }

        private VisualElement BuildNotice(UnitGitIconKind icon, string title, string body, string actionText, Action action, string variant, string actionTooltip,
            Action hide = null)
        {
            var notice = new VisualElement { tooltip = title + "\n" + body };
            notice.AddToClassList("unitgit-notice");
            if (!string.IsNullOrEmpty(variant)) notice.AddToClassList(variant);
            var glyph = new UnitGitIconElement(icon);
            glyph.AddToClassList("unitgit-notice__icon");
            notice.Add(glyph);
            var texts = new VisualElement();
            texts.AddToClassList("unitgit-notice__texts");
            notice.Add(texts);
            texts.Add(UnitGitUi.Text(title, "unitgit-notice__title"));
            texts.Add(UnitGitUi.Text(body, "unitgit-notice__body"));
            var button = UnitGitUi.Pill(actionText, action, "primary", null, actionTooltip);
            button.AddToClassList("unitgit-notice__button");
            notice.Add(button);
            if (hide != null)
            {
                notice.Add(UnitGitUi.Icon(UnitGitIconKind.Close, "Hide until Unity restarts", () =>
                {
                    hide();
                    notice.AddToClassList("unitgit-notice--leave");
                    notice.schedule.Execute(() => notice.RemoveFromHierarchy()).StartingIn(180);
                }, "unitgit-notice__close"));
            }
            return notice;
        }

        // ---- The list ----------------------------------------------------------------------------------------------

        private void AddChangedFileGroups(VisualElement parent)
        {
            changeRows = BuildChangeRows();
            if (snapshot.Changes.Count == 0)
            {
                parent.Add(BuildCleanState());
                changesListView = null;
                return;
            }
            if (changeRows.Count == 0)
            {
                parent.Add(BuildEmptyState("No file matches “" + changeFilter.Trim() + "”."));
                changesListView = null;
                return;
            }
            changesListView = BuildVirtualList(changeRows, ChangeRowHeight, BuildChangeRow, "unitgit-local-changes-list");
            changesListView.focusable = true;
            changesListView.RegisterCallback<KeyDownEvent>(OnChangesKeyDown);
            parent.Add(changesListView);
        }

        private VisualElement BuildCleanState()
        {
            var clean = new VisualElement();
            clean.AddToClassList("ug-empty");
            var mark = new VisualElement();
            mark.AddToClassList("ug-empty__mark");
            mark.Add(new UnitGitIconElement(UnitGitIconKind.Check));
            clean.Add(mark);
            clean.Add(UnitGitUi.Text("Everything is committed", "ug-empty__title"));
            clean.Add(UnitGitUi.Text("Changes to the project show up here as you save them.", "ug-empty__text"));
            return clean;
        }

        // Rebuilds the rows in place (filter, folders, a refresh), keeping the scroll position.
        private void RefreshChangesList()
        {
            if (localChangesListRoot == null || snapshot == null) return;
            UpdateAddButton();
            var scroll = changesListView?.Q<ScrollView>();
            var offset = scroll != null ? scroll.scrollOffset : Vector2.zero;
            localChangesListRoot.Clear();
            AddChangedFileGroups(localChangesListRoot);
            var restored = changesListView?.Q<ScrollView>();
            if (restored != null) restored.schedule.Execute(() => restored.scrollOffset = offset);
            UpdateCommitSummary();
        }

        private List<ChangeRow> BuildChangeRows()
        {
            var rows = new List<ChangeRow>();
            if (snapshot == null) return rows;
            string term = changeFilter.Trim();
            var changes = snapshot.Changes
                .Where(change => term.Length == 0 || change.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool grouped = EditorPrefs.GetBool(GroupByFolderPref, true);
            AddChangeGroup(rows, "Changes", changes.Where(change => !change.IsUntracked).ToList(), grouped);
            AddChangeGroup(rows, "Unversioned Files", changes.Where(change => change.IsUntracked).ToList(), grouped);
            return rows;
        }

        private void AddChangeGroup(List<ChangeRow> rows, string title, List<UnitGitStatusEntry> files, bool grouped)
        {
            if (files.Count == 0) return;
            var group = new ChangeRow { Kind = ChangeRow.RowKind.Group, Key = "group:" + title, Name = title };
            group.Files.AddRange(files);
            rows.Add(group);
            if (collapsedChangeFolders.Contains(group.Key)) return;
            if (!grouped)
            {
                foreach (var file in files)
                    rows.Add(new ChangeRow { Kind = ChangeRow.RowKind.File, Key = file.Path, Name = GetFileLeaf(file.Path), Depth = 1, Change = file });
                return;
            }
            var tree = new FolderNode(string.Empty, string.Empty);
            foreach (var file in files) tree.Add(file);
            AddFolderRows(rows, group.Key + "/", tree, 1);
        }

        private sealed class FolderNode
        {
            public readonly string Name, Path;
            public readonly SortedDictionary<string, FolderNode> Folders = new SortedDictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            public readonly List<UnitGitStatusEntry> Files = new List<UnitGitStatusEntry>();
            public readonly List<UnitGitStatusEntry> All = new List<UnitGitStatusEntry>();

            public FolderNode(string name, string path) { Name = name; Path = path; }

            public void Add(UnitGitStatusEntry change)
            {
                var node = this;
                string[] parts = change.Path.Replace('\\', '/').Split('/');
                All.Add(change);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!node.Folders.TryGetValue(parts[i], out var next))
                        node.Folders[parts[i]] = next = new FolderNode(parts[i], node.Path.Length == 0 ? parts[i] : node.Path + "/" + parts[i]);
                    node = next;
                    node.All.Add(change);
                }
                node.Files.Add(change);
            }
        }

        // Folders with a single subfolder and no file of their own are shown as one row ("Assets/Avatars/Rexouium").
        private void AddFolderRows(List<ChangeRow> rows, string keyPrefix, FolderNode node, int depth)
        {
            foreach (var folder in node.Folders.Values)
            {
                var shown = folder;
                string name = folder.Name;
                while (shown.Files.Count == 0 && shown.Folders.Count == 1)
                {
                    shown = shown.Folders.Values.First();
                    name += "/" + shown.Name;
                }
                var row = new ChangeRow { Kind = ChangeRow.RowKind.Folder, Key = keyPrefix + shown.Path, Name = name, Depth = depth };
                row.Files.AddRange(shown.All);
                rows.Add(row);
                if (!collapsedChangeFolders.Contains(row.Key))
                    AddFolderRows(rows, keyPrefix, shown, depth + 1);
            }
            foreach (var file in node.Files)
                rows.Add(new ChangeRow { Kind = ChangeRow.RowKind.File, Key = file.Path, Name = GetFileLeaf(file.Path), Depth = depth, Change = file });
        }

        private VisualElement BuildChangeRow(ChangeRow row)
        {
            if (row.Kind == ChangeRow.RowKind.File) return BuildChangeFileRow(row);

            var element = new VisualElement();
            element.AddToClassList("ug-row");
            element.AddToClassList(row.Kind == ChangeRow.RowKind.Group ? "ug-row--group" : "ug-row--folder");
            element.style.paddingLeft = 6 + row.Depth * 16;
            bool collapsed = collapsedChangeFolders.Contains(row.Key);
            var chevron = new UnitGitIconElement(collapsed ? UnitGitIconKind.ChevronCollapsed : UnitGitIconKind.ChevronExpanded);
            chevron.AddToClassList("ug-row__chevron");
            element.Add(chevron);
            var check = new UnitGitCheck(CheckStateOf(row.Files));
            check.Toggled += include => SetIncluded(row.Files, include);
            element.Add(check);
            if (row.Kind == ChangeRow.RowKind.Folder)
            {
                var folder = new UnitGitIconElement(UnitGitIconKind.Folder);
                folder.AddToClassList("ug-row__folder");
                element.Add(folder);
            }
            element.Add(UnitGitUi.Text(row.Name, "ug-row__name"));
            element.Add(UnitGitUi.Text(row.Files.Count + (row.Files.Count == 1 ? " file" : " files"), "ug-row__count"));
            element.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                if (!collapsedChangeFolders.Remove(row.Key)) collapsedChangeFolders.Add(row.Key);
                RefreshChangesList();
            });
            return element;
        }

        private VisualElement BuildChangeFileRow(ChangeRow row)
        {
            var change = row.Change;
            var element = new Button();
            element.AddToClassList("ug-row");
            element.AddToClassList("unitgit-change-file-row");
            element.userData = change.Path;
            element.tooltip = change.Path + "  •  " + change.DisplayStatus;
            element.style.paddingLeft = 6 + row.Depth * 16 + 18;
            element.EnableInClassList("ug-row--selected", selectedChangePaths.Contains(change.Path));
            var check = new UnitGitCheck(CheckStateOf(change));
            check.Toggled += include => SetIncluded(new[] { change }, include);
            element.Add(check);
            element.Add(BuildProjectFileIcon(change.Path, "ug-row__icon"));
            var name = UnitGitUi.Text(row.Name, "ug-row__name");
            name.AddToClassList("ug-status--" + StatusKey(change));
            element.Add(name);
            if (!EditorPrefs.GetBool(GroupByFolderPref, true))
                element.Add(UnitGitUi.Text(GetDirectoryLabel(change.Path), "ug-row__path"));
            else
                element.Add(UnitGitUi.Spacer());
            var letter = UnitGitUi.Text(StatusLetter(change), "ug-row__letter");
            letter.AddToClassList("ug-status--" + StatusKey(change));
            element.Add(letter);
            // Trickle-down: the button's own click handling stops the pointer-down before a bubbling callback sees it.
            // The checkbox handles its own presses. Keyboard activation ("clicked") still selects.
            bool selectedOnDown = false;
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target is VisualElement target && target.GetFirstOfType<UnitGitCheck>() != null) return;
                if (evt.button == 0 && evt.clickCount == 2)
                {
                    OpenProjectPath(change.Path);
                    evt.StopPropagation();
                    return;
                }
                if (evt.button == 0 || (evt.button == 1 && !selectedChangePaths.Contains(change.Path)))
                {
                    SelectChange(change, evt.shiftKey, evt.ctrlKey || evt.commandKey);
                    selectedOnDown = true;
                }
            }, TrickleDown.TrickleDown);
            // The menu opens on release: opened on press, Windows takes the right button's release as a click on the
            // item under the cursor, which "included" every selected file without the user choosing it.
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button != 1) return;
                evt.StopPropagation();
                ShowChangeContextMenu(change);
            }, TrickleDown.TrickleDown);
            element.clicked += () =>
            {
                if (!selectedOnDown) SelectChange(change, false, false);
                selectedOnDown = false;
            };
            return element;
        }

        internal static string StatusKey(UnitGitStatusEntry change)
        {
            switch (change.DisplayStatus)
            {
                case "conflict": return "conflict";
                case "untracked": return "untracked";
                case "added": return "added";
                case "deleted": return "deleted";
                case "renamed": case "copied": return "renamed";
                default: return "modified";
            }
        }

        private static string StatusLetter(UnitGitStatusEntry change)
        {
            switch (StatusKey(change))
            {
                case "conflict": return "C";
                case "untracked": return "U";
                case "added": return "A";
                case "deleted": return "D";
                case "renamed": return "R";
                default: return "M";
            }
        }

        // ---- Including files -----------------------------------------------------------------------------------------

        private bool IsIncluded(UnitGitStatusEntry change, out bool partly)
        {
            partly = false;
            if (pendingInclude.TryGetValue(change.Path, out bool pending)) return pending;
            if (change.IsUntracked || !change.IsStaged) return false;
            partly = change.IsUnstaged;
            return !partly;
        }

        private UnitGitCheckState CheckStateOf(UnitGitStatusEntry change)
        {
            bool included = IsIncluded(change, out bool partly);
            return partly ? UnitGitCheckState.Mixed : included ? UnitGitCheckState.On : UnitGitCheckState.Off;
        }

        private UnitGitCheckState CheckStateOf(IList<UnitGitStatusEntry> files)
        {
            int on = 0;
            bool mixed = false;
            foreach (var file in files)
            {
                if (IsIncluded(file, out bool partly)) on++;
                mixed |= partly;
            }
            if (on == files.Count && !mixed) return UnitGitCheckState.On;
            return on == 0 && !mixed ? UnitGitCheckState.Off : UnitGitCheckState.Mixed;
        }

        private int IncludedCount() => snapshot == null ? 0 : snapshot.Changes.Count(change => IsIncluded(change, out bool partly) || partly);

        // The boxes change at once; Git follows in the background, one batch after the other, then the list refreshes.
        // Only what needs it reaches Git: a staged removal is already in (and adding an ignored file back would fail).
        private void SetIncluded(IEnumerable<UnitGitStatusEntry> files, bool include)
        {
            var paths = new List<string>();
            foreach (var file in files)
            {
                pendingInclude[file.Path] = include;
                if (include ? file.IsUnstaged || file.IsUntracked : file.IsStaged) paths.Add(file.Path);
            }
            changesListView?.RefreshItems();
            UpdateCommitSummary();
            if (paths.Count == 0) return;
            includeQueue.Add((paths, include));
            RunIncludeQueue();
        }

        private void RunIncludeQueue()
        {
            if (includeRunning || includeQueue.Count == 0) return;
            includeRunning = true;
            int generation = includeGeneration;
            var batch = includeQueue.ToList();
            includeQueue.Clear();
            includeRunningCount = batch.Sum(item => item.paths.Count);
            includeRunningAdds = batch.All(item => item.include);
            includeStarted = EditorApplication.timeSinceStartup;
            includePaths = string.Join("\n", batch.SelectMany(item => item.paths).Take(8));
            if (includeRunningCount > 8) includePaths += "\n…and " + (includeRunningCount - 8) + " more files";
            UpdateCommitSummary();
            string root = gitService.ProjectRoot;
            Task.Run(() =>
                {
                    var service = new UnitGitService(root);
                    service.ProcessLogReceived = line => QueueConsoleLine("prepare commit", line);
                    GitCommandResult last = null;
                    foreach (var (paths, include) in batch)
                    {
                        last = include ? service.Stage(paths) : service.Unstage(paths);
                        if (last != null && !last.Success) return last;
                    }
                    return last;
                })
                .ContinueWith(task => QueueMainThreadAction(() =>
                {
                    if (this == null || generation != includeGeneration) return;
                    includeRunning = false;
                    includeRunningCount = 0;
                    var result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                    bool failed = result == null || !result.Success;
                    if (failed)
                    {
                        pendingInclude.Clear();
                        includeQueue.Clear();
                        string reason = UnitGitRedaction.Redact(result?.Message ?? task.Exception?.GetBaseException().Message ?? "unknown error");
                        commitIssue = "Could not prepare the commit. " + reason;
                        pendingCommitDescription = null;
                        AppendConsole("prepare commit", reason);
                        ShowToast("Could not update the files to commit" + (commitWhenIncluded != null ? ", so nothing was committed" : string.Empty) + ": " + reason,
                            error: true, "Console", () => SetActiveTab(UnitGitTab.Console));
                        commitWhenIncluded = null;
                    }
                    if (includeQueue.Count > 0)
                    {
                        RunIncludeQueue();
                        return;
                    }
                    if (commitWhenIncluded != null)
                    {
                        bool push = commitWhenIncluded.Value;
                        commitWhenIncluded = null;
                        CommitStaged(push);
                        return;
                    }
                    UpdateCommitSummary();
                    RefreshTopBar();
                    RefreshSnapshot();
                }));
            EnsureEditorUpdatePump();
        }

        // Called once a refresh landed: what Git now says replaces what was shown ahead of it.
        private void ReconcilePendingInclude()
        {
            if (!includeRunning && includeQueue.Count == 0) pendingInclude.Clear();
        }

        // ---- Selection ------------------------------------------------------------------------------------------------

        private void SelectChange(UnitGitStatusEntry change, bool range, bool toggle)
        {
            if (range && !string.IsNullOrEmpty(changeAnchorPath))
            {
                var files = changeRows.Where(row => row.Kind == ChangeRow.RowKind.File).Select(row => row.Change.Path).ToList();
                int a = files.FindIndex(path => string.Equals(path, changeAnchorPath, StringComparison.OrdinalIgnoreCase));
                int b = files.FindIndex(path => string.Equals(path, change.Path, StringComparison.OrdinalIgnoreCase));
                if (a >= 0 && b >= 0)
                {
                    if (!toggle) selectedChangePaths.Clear();
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) selectedChangePaths.Add(files[i]);
                }
            }
            else if (toggle)
            {
                if (!selectedChangePaths.Remove(change.Path)) selectedChangePaths.Add(change.Path);
                changeAnchorPath = change.Path;
            }
            else
            {
                selectedChangePaths.Clear();
                selectedChangePaths.Add(change.Path);
                changeAnchorPath = change.Path;
            }
            UpdateLocalChangeSelectionState();
            SelectLocalChange(change);
        }

        private void SelectLocalChange(UnitGitStatusEntry change)
        {
            if (change == null)
            {
                return;
            }

            if (selectedChangePaths.Count == 0) selectedChangePaths.Add(change.Path);
            if (string.Equals(selectedChangePath, change.Path, StringComparison.Ordinal))
                return;

            selectedChangePath = change.Path;
            diffSearchMatchIndex = 0;
            UpdateLocalChangeSelectionState();
            RebuildLocalDiffPane();
        }

        private Button addButton;

        // Ctrl+Alt+A, as in JetBrains IDEs: the selected unversioned files go to Git (and into the commit).
        internal void AddSelectedUnversioned()
        {
            var unversioned = SelectedChanges().Where(change => change.IsUntracked && !IsIncluded(change, out _)).ToList();
            if (unversioned.Count == 0)
            {
                ShowToast("Select unversioned files to add");
                return;
            }

            SetIncluded(unversioned, true);
            ShowToast("Added " + unversioned.Count + (unversioned.Count == 1 ? " file" : " files") + " to Git");
            UpdateAddButton();
        }

        private void UpdateAddButton()
        {
            if (addButton == null) return;
            int count = SelectedChanges().Count(change => change.IsUntracked && !IsIncluded(change, out _));
            addButton.SetEnabled(count > 0);
            UnitGitUi.SetText(addButton, count > 1 ? "Add " + count : "Add");
        }

        private void UpdateLocalChangeSelectionState()
        {
            UpdateAddButton();
            if (localChangesListRoot == null)
            {
                return;
            }

            localChangesListRoot.Query<Button>(className: "unitgit-change-file-row").ForEach(row =>
            {
                string rowPath = row.userData as string;
                row.EnableInClassList("ug-row--selected", rowPath != null && selectedChangePaths.Contains(rowPath));
            });
        }

        private void OnChangesKeyDown(KeyDownEvent evt)
        {
            var files = changeRows.Where(row => row.Kind == ChangeRow.RowKind.File).ToList();
            if (files.Count == 0) return;
            int index = files.FindIndex(row => string.Equals(row.Change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase));
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                    int next = Mathf.Clamp(index + (evt.keyCode == KeyCode.UpArrow ? -1 : 1), 0, files.Count - 1);
                    SelectChange(files[next].Change, evt.shiftKey, false);
                    changesListView?.ScrollToItem(changeRows.IndexOf(files[next]));
                    evt.StopPropagation();
                    break;
                case KeyCode.Space:
                    var chosen = SelectedChanges();
                    if (chosen.Count > 0) SetIncluded(chosen, !chosen.All(change => IsIncluded(change, out _)));
                    evt.StopPropagation();
                    break;
                case KeyCode.Delete:
                    RollbackSelectedChanges();
                    evt.StopPropagation();
                    break;
            }
        }

        // The next (or previous) file of the list, selected with its diff, as F7 does past a file's last change.
        private bool SelectAdjacentChange(int step)
        {
            var files = changeRows.Where(row => row.Kind == ChangeRow.RowKind.File).ToList();
            int index = files.FindIndex(row => string.Equals(row.Change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase));
            int next = index + step;
            if (index < 0 || next < 0 || next >= files.Count) return false;
            SelectChange(files[next].Change, false, false);
            changesListView?.ScrollToItem(changeRows.IndexOf(files[next]));
            return true;
        }

        private List<UnitGitStatusEntry> SelectedChanges() =>
            snapshot == null ? new List<UnitGitStatusEntry>() : snapshot.Changes.Where(change => selectedChangePaths.Contains(change.Path)).ToList();

        private void ShowChangeContextMenu(UnitGitStatusEntry clicked)
        {
            var chosen = SelectedChanges();
            if (chosen.Count == 0) chosen.Add(clicked);
            string what = chosen.Count == 1 ? "‘" + GetFileLeaf(chosen[0].Path) + "’" : chosen.Count + " files";
            var menu = new GenericMenu();
            bool allIncluded = chosen.All(change => IsIncluded(change, out _));
            var unversioned = chosen.Where(change => change.IsUntracked && !IsIncluded(change, out _)).ToList();
            if (unversioned.Count > 0)
                menu.AddItem(new GUIContent("Add to Git	Ctrl+Alt+A"), false, AddSelectedUnversioned);
            menu.AddItem(new GUIContent(allIncluded ? "Exclude from commit" : "Include in commit"), false, () => SetIncluded(chosen, !allIncluded));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Rollback " + what + "…"), false, RollbackSelectedChanges);
            menu.AddSeparator(string.Empty);
            if (chosen.Count == 1)
            {
                menu.AddItem(new GUIContent("Open"), false, () => OpenProjectPath(chosen[0].Path));
                menu.AddItem(new GUIContent("Show in Explorer"), false, () => EditorUtility.RevealInFinder(Path.Combine(gitService.ProjectRoot, chosen[0].Path)));
                if (!chosen[0].IsUntracked)
                    menu.AddItem(new GUIContent("Show History"), false, () => ShowHistory(chosen[0].Path));
            }
            menu.AddItem(new GUIContent("Copy path" + (chosen.Count == 1 ? string.Empty : "s")), false, () =>
                EditorGUIUtility.systemCopyBuffer = string.Join("\n", chosen.Select(change => change.Path)));
            menu.ShowAsContext();
        }

        private void ToggleGroupByFolder()
        {
            EditorPrefs.SetBool(GroupByFolderPref, !EditorPrefs.GetBool(GroupByFolderPref, true));
            var button = contentRoot?.Query<Button>(className: "ug-icon-button").ToList().FirstOrDefault(b => b.tooltip == "Group by folder");
            button?.EnableInClassList("ug-icon-button--on", EditorPrefs.GetBool(GroupByFolderPref, true));
            RefreshChangesList();
        }

        private void CollapseAllChangeFolders()
        {
            foreach (var row in BuildChangeRows().Where(row => row.Kind == ChangeRow.RowKind.Folder)) collapsedChangeFolders.Add(row.Key);
            RefreshChangesList();
        }

        // ---- Rollback -------------------------------------------------------------------------------------------------

        private void RollbackSelectedChanges()
        {
            var chosen = SelectedChanges();
            if (chosen.Count == 0)
            {
                ShowToast("Select the files to roll back first.", error: true);
                return;
            }
            // A rename is rolled back by both of its paths.
            var tracked = chosen.Where(change => !change.IsUntracked)
                .SelectMany(change => string.IsNullOrEmpty(change.OriginalPath) ? new[] { change.Path } : new[] { change.Path, change.OriginalPath })
                .Distinct().ToList();
            var untracked = chosen.Where(change => change.IsUntracked).Select(change => change.Path).ToList();
            string message = (tracked.Count > 0 ? tracked.Count + (tracked.Count == 1 ? " file goes" : " files go") + " back to its last commit." : string.Empty) +
                             (untracked.Count > 0 ? (tracked.Count > 0 ? "\n" : string.Empty) + untracked.Count + " new file" + (untracked.Count == 1 ? " is" : "s are") +
                              " deleted (assets go to the trash)." : string.Empty) +
                             "\n\n" + string.Join("\n", chosen.Take(8).Select(change => "  " + change.Path)) + (chosen.Count > 8 ? "\n  …" : string.Empty);
            if (!EditorUtility.DisplayDialog("Rollback " + (chosen.Count == 1 ? GetFileLeaf(chosen[0].Path) : chosen.Count + " files"), message, "Rollback", "Cancel"))
                return;
            string root = gitService.ProjectRoot;
            RunAction("rollback", () =>
            {
                var result = tracked.Count > 0 ? gitService.Rollback(tracked) : new GitCommandResult { ExitCode = 0 };
                if (!result.Success) return result;
                if (untracked.Count > 0) QueueMainThreadAction(() => DeleteUntracked(root, untracked));
                return result;
            }, _ => selectedChangePaths.Clear(), "Rolling back…", "Rolled back " + (chosen.Count == 1 ? "‘" + GetFileLeaf(chosen[0].Path) + "’" : chosen.Count + " files"));
        }

        // New files: project assets go to the trash (recoverable), anything else is deleted.
        private static void DeleteUntracked(string root, List<string> paths)
        {
            var assets = paths.Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)).ToArray();
            var failed = new List<string>();
            if (assets.Length > 0) AssetDatabase.MoveAssetsToTrash(assets, failed);
            foreach (string path in paths.Except(assets).Concat(failed))
            {
                string full = Path.Combine(root, path);
                if (File.Exists(full)) File.Delete(full);
            }
        }

        // ---- Commit -----------------------------------------------------------------------------------------------------

        private VisualElement BuildCommitPanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-local-commit-panel");

            var message = new TextField { multiline = true, value = commitMessage };
            message.AddToClassList("unitgit-local-commit-message");
            var placeholder = UnitGitUi.Text("Commit message", "unitgit-local-commit-placeholder");
            placeholder.pickingMode = PickingMode.Ignore;
            message.Add(placeholder);
            placeholder.style.display = string.IsNullOrEmpty(commitMessage) ? DisplayStyle.Flex : DisplayStyle.None;
            message.RegisterValueChangedCallback(evt =>
            {
                commitMessage = evt.newValue;
                placeholder.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                UpdateCommitSummary();
            });
            message.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) && (evt.ctrlKey || evt.commandKey))
                {
                    evt.StopPropagation();
                    CommitStaged();
                }
            }, TrickleDown.TrickleDown);
            panel.Add(message);
            commitProgressLabel = UnitGitUi.Text(string.Empty, "unitgit-commit-progress");
            panel.Add(commitProgressLabel);

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-local-commit-actions");
            var amend = new VisualElement();
            amend.AddToClassList("unitgit-local-amend");
            amendCheck = new UnitGitCheck(commitAmend ? UnitGitCheckState.On : UnitGitCheckState.Off);
            amendCheck.Toggled += SetAmendCommit;
            amend.Add(amendCheck);
            amend.Add(UnitGitUi.Text("Amend", "unitgit-local-amend__label"));
            amend.tooltip = "Add the included files to the last commit instead, and edit its message.";
            amend.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.target == amendCheck) return;
                SetAmendCommit(!commitAmend);
            });
            actions.Add(amend);
            commitSummaryLabel = UnitGitUi.Text(string.Empty, "unitgit-local-commit-summary");
            actions.Add(commitSummaryLabel);
            actions.Add(UnitGitUi.Spacer());
            commitPushButton = UnitGitUi.Pill("Commit and Push", () => CommitStaged(push: true), null, UnitGitIconKind.Push,
                "Commit the included files, then push the branch");
            actions.Add(commitPushButton);
            commitButton = UnitGitUi.Pill(commitAmend ? "Amend" : "Commit", () => CommitStaged(), "primary", UnitGitIconKind.Commit, "Ctrl+Enter");
            commitButton.name = "unitgit-commit-submit";
            actions.Add(commitButton);
            panel.Add(actions);
            return panel;
        }

        private void UpdateCommitSummary()
        {
            if (commitSummaryLabel == null || snapshot == null) return;
            int included = IncludedCount();
            int working = includeRunningCount + includeQueue.Sum(item => item.paths.Count);
            commitSummaryLabel.text = commitWhenIncluded != null ? "Preparing commit…"
                : includeRunning ? (includeRunningAdds ? "Including " : "Updating ") + working + (working == 1 ? " file…" : " files…")
                : included + " of " + snapshot.Changes.Count + " included";
            commitSummaryLabel.EnableInClassList("unitgit-local-commit-summary--working", includeRunning || commitWhenIncluded != null);
            if (commitProgressLabel != null)
            {
                string progress = includeRunning ? "Git is " + (includeRunningAdds ? "including" : "updating") + " " + working + " files · " +
                    (int)(EditorApplication.timeSinceStartup - includeStarted) + "s\n" + includePaths +
                    (commitWhenIncluded != null ? "\nYour commit will start when this finishes. Git errors appear here; command details are in Console." : "") : commitIssue;
                commitProgressLabel.text = progress ?? string.Empty;
                commitProgressLabel.style.display = string.IsNullOrEmpty(progress) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            // Clickable while Git is still including files: the commit then waits for them.
            bool ready = !busy && commitWhenIncluded == null && (included > 0 || commitAmend);
            commitButton?.SetEnabled(ready);
            commitPushButton?.SetEnabled(ready);
            if (commitButton != null) UnitGitUi.SetText(commitButton, commitAmend ? "Amend" : "Commit");
            if (commitPushButton != null) UnitGitUi.SetText(commitPushButton, commitAmend ? "Amend and Push" : "Commit and Push");
        }

        private void SetAmendCommit(bool value)
        {
            if (commitAmend == value)
            {
                return;
            }

            if (value)
            {
                commitMessageBeforeAmend = commitMessage;
                commitMessage = snapshot != null ? snapshot.HeadMessage?.Trim() ?? string.Empty : string.Empty;
                commitAmend = true;
            }
            else
            {
                commitAmend = false;
                commitMessage = commitMessageBeforeAmend;
                commitMessageBeforeAmend = string.Empty;
            }

            if (amendCheck != null) amendCheck.State = commitAmend ? UnitGitCheckState.On : UnitGitCheckState.Off;
            contentRoot?.Q<TextField>(className: "unitgit-local-commit-message")?.SetValueWithoutNotify(commitMessage);
            var placeholder = contentRoot?.Q<Label>(className: "unitgit-local-commit-placeholder");
            if (placeholder != null) placeholder.style.display = string.IsNullOrEmpty(commitMessage) ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateCommitSummary();
        }

        private void RebuildLocalDiffPane()
        {
            if (localDiffPaneRoot == null)
            {
                RebuildContent();
                return;
            }

            localDiffPaneRoot.Clear();
            localDiffPaneRoot.Add(BuildDiffViewerPane());
        }
    }
}
