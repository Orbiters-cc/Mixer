using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The selected commit, as in JetBrains IDEs: the files it changed (coloured by status; a double click shows the diff)
    // above its message, author, date, hash and parents.
    internal sealed partial class UnitGitWindow
    {
        private const string DetailsMessageSplitPref = SplitPrefPrefix + "DetailsMessage";
        private string selectedCommitFile = string.Empty;

        private VisualElement BuildDetailsPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-details-pane");
            EnsureCommitDetailsLoaded();

            UnitGitReleaseEntry selectedRelease = GetSelectedRelease();
            if (selectedRelease != null)
            {
                pane.Add(BuildReleaseDetailsPanel(selectedRelease));
                return pane;
            }

            var split = BuildTrackedSplit(DetailsMessageSplitPref, 1, 150f, TwoPaneSplitViewOrientation.Vertical);
            split.Add(BuildChangedFilesTree());
            split.Add(BuildCommitDetailsCard());
            pane.Add(split);
            return pane;
        }

        private UnitGitReleaseEntry GetSelectedRelease()
        {
            if (selectedCommit == null ||
                string.IsNullOrEmpty(selectedReleaseId) ||
                !string.Equals(selectedCommit.ReleaseId, selectedReleaseId, StringComparison.Ordinal))
            {
                return null;
            }

            return GetReleaseForCommit(selectedCommit);
        }

        private VisualElement BuildChangedFilesTree()
        {
            List<string> changedFiles = loadedSelection != null ? loadedSelection.Files : new List<string>();
            int selectedCount = GetSelectedCommitsInLogOrder().Count;
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");

            var header = new VisualElement();
            header.AddToClassList("ug-toolbar");
            header.AddToClassList("ug-details-header");
            header.Add(UnitGitUi.Text(changedFiles.Count == 0 ? "Changed files"
                : changedFiles.Count + (changedFiles.Count == 1 ? " file changed" : " files changed"), "ug-details-header__title"));
            if (selectedCount > 1) header.Add(UnitGitUi.Chip("in " + selectedCount + " commits"));
            header.Add(UnitGitUi.Spacer());
            if (changedFiles.Count > 0)
            {
                header.Add(UnitGitUi.Icon(UnitGitIconKind.ExpandAll, "Expand all", () => SetCommitFoldersExpanded(true)));
                header.Add(UnitGitUi.Icon(UnitGitIconKind.CollapseAll, "Collapse all", () => SetCommitFoldersExpanded(false)));
            }
            panel.Add(header);

            var body = new VisualElement();
            body.AddToClassList("unitgit-files-scroll");
            if (changedFiles.Count == 0)
            {
                if (detailsRead.IsBusy)
                {
                    var loading = new VisualElement();
                    loading.AddToClassList("ug-diff-placeholder");
                    loading.Add(UnitGitUi.Spinner());
                    body.Add(loading);
                }
                else body.Add(BuildEmptyState(selectedCommit == null ? "Select a commit." : "No changed files."));
            }
            else
            {
                var rows = loadedSelection.FileList.GetVisibleRows(row =>
                    GetFoldoutExpanded(GetFoldoutPrefKey("ChangedFilesFolder", row.Path), true));
                body.Add(BuildVirtualList(rows, 26f, BuildCommitFileRow, "unitgit-changed-files-scroll"));
            }

            panel.Add(body);
            return panel;
        }

        private void SetCommitFoldersExpanded(bool expanded)
        {
            if (loadedSelection == null) return;
            foreach (var folder in loadedSelection.FileList.Folders)
                EditorPrefs.SetBool(GetFoldoutPrefKey("ChangedFilesFolder", folder.Path), expanded);
            RefreshDetailsPane();
        }

        private static void AddChangedFileTreeNode(UnitGitFileList rows, ChangedFileTreeNode node, int depth)
        {
            foreach (ChangedFileTreeNode folder in node.Folders.Values)
            {
                rows.Add(new UnitGitFileRow
                {
                    IsFolder = true, Name = folder.Name, Path = folder.FullPath,
                    FileCount = folder.FileCount, Depth = depth
                });
                AddChangedFileTreeNode(rows, folder, depth + 1);
            }

            foreach (string file in node.Files)
            {
                rows.Add(new UnitGitFileRow { Path = file, Depth = depth });
            }
        }

        private VisualElement BuildCommitFileRow(UnitGitFileRow row)
        {
            if (!row.IsFolder)
                return BuildChangedFileButton(row.Path, row.Depth);
            string prefKey = GetFoldoutPrefKey("ChangedFilesFolder", row.Path);
            bool expanded = GetFoldoutExpanded(prefKey, true);
            var folder = new VisualElement();
            folder.AddToClassList("ug-row");
            folder.AddToClassList("ug-row--folder");
            folder.style.paddingLeft = 6 + row.Depth * 16;
            var chevron = new UnitGitIconElement(expanded ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed);
            chevron.AddToClassList("ug-row__chevron");
            folder.Add(chevron);
            var icon = new UnitGitIconElement(UnitGitIconKind.Folder);
            icon.AddToClassList("ug-row__folder");
            folder.Add(icon);
            folder.Add(UnitGitUi.Text(row.Name, "ug-row__name"));
            folder.Add(UnitGitUi.Text(row.FileCount + (row.FileCount == 1 ? " file" : " files"), "ug-row__count"));
            folder.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                EditorPrefs.SetBool(prefKey, !expanded);
                RefreshDetailsPane();
            });
            return folder;
        }

        private char CommitFileStatus(string file)
        {
            if (loadedSelection == null) return 'M';
            foreach (var details in loadedSelection.Details)
                if (details.FileStatus != null && details.FileStatus.TryGetValue(file, out char status))
                    return status;
            return 'M';
        }

        private static string CommitStatusKey(char status)
        {
            switch (status)
            {
                case 'A': return "added";
                case 'D': return "deleted";
                case 'R': case 'C': return "renamed";
                default: return "modified";
            }
        }

        private VisualElement BuildChangedFileButton(string file, int depth)
        {
            var row = new Button();
            row.AddToClassList("ug-row");
            row.AddToClassList("unitgit-file-row");
            row.style.paddingLeft = 6 + depth * 16 + 18;
            row.EnableInClassList("ug-row--selected", string.Equals(selectedCommitFile, file, StringComparison.Ordinal));
            char status = CommitFileStatus(file);
            row.tooltip = file + "\nDouble click: see what changed";

            row.Add(BuildProjectFileIcon(file, "ug-row__icon"));
            var name = UnitGitUi.Text(GetFileLeaf(file), "ug-row__name");
            name.AddToClassList("ug-status--" + CommitStatusKey(status));
            row.Add(name);
            row.Add(UnitGitUi.Spacer());

            // Scenes and prefabs: what this commit changed in them, object by object.
            var details = selectedDetails;
            if (details != null && details.Commit != null && (IsUnityYamlPath(file) || Semantic.ModelVersions.IsPreviewable(file)))
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mixer/Editor/Git/Styles/unitgit-semantic.uss");
                if (sheet != null && !row.styleSheets.Contains(sheet)) row.styleSheets.Add(sheet);
                var compare = new Button(() => SceneChangesWindow.Show(gitService.ProjectRoot, file, details.Commit.FullHash, details.Commit.Subject))
                {
                    text = "Compare",
                    tooltip = "See what this commit changed in " + GetFileLeaf(file) + ", object by object (beta)."
                };
                compare.AddToClassList("ugs-compare");
                compare.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
                compare.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
                row.Add(compare);
            }
            var letter = UnitGitUi.Text(status.ToString(), "ug-row__letter");
            letter.AddToClassList("ug-status--" + CommitStatusKey(status));
            row.Add(letter);

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0 && evt.clickCount == 2)
                {
                    OpenCommitFileDiff(file);
                    evt.StopPropagation();
                    return;
                }
                if (evt.button != 0 && evt.button != 1) return;
                selectedCommitFile = file;
                row.parent?.parent?.Query<Button>(className: "unitgit-file-row").ForEach(other => other.EnableInClassList("ug-row--selected", other == row));
                commitDetailsRoot?.Query<Button>(className: "unitgit-file-row").ForEach(other => other.EnableInClassList("ug-row--selected", other == row));
                if (evt.button == 1) ShowCommitFileMenu(file, status);
            });
            return row;
        }

        private void ShowCommitFileMenu(string file, char status)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Show diff"), false, () => OpenCommitFileDiff(file));
            if (status != 'D')
            {
                menu.AddItem(new GUIContent("Open"), false, () => OpenProjectPath(file));
                menu.AddItem(new GUIContent("Show in Explorer"), false, () => EditorUtility.RevealInFinder(Path.Combine(gitService.ProjectRoot, file)));
            }
            menu.AddItem(new GUIContent("Show History"), false, () => ShowHistory(file));
            menu.AddItem(new GUIContent("Copy path"), false, () => EditorGUIUtility.systemCopyBuffer = file);
            menu.ShowAsContext();
        }

        // The commit that last changed the file among the selected ones (the first in log order).
        private void OpenCommitFileDiff(string file)
        {
            var commit = loadedSelection?.Details.FirstOrDefault(details => details.ChangedFiles.Contains(file))?.Commit ?? selectedCommit;
            if (commit == null) return;
            UnitGitCommitDiffWindow.Show(gitService.ProjectRoot, commit.FullHash, commit.ShortHash, commit.Subject, file);
        }

        private VisualElement BuildCommitDetailsCard()
        {
            var scroll = new ScrollView();
            scroll.name = "unitgit-commit-card-scroll";
            scroll.AddToClassList("unitgit-details-card");

            List<UnitGitCommit> selectedCommits = GetSelectedCommitsInLogOrder();
            if (selectedCommits.Count > 1)
            {
                return BuildMultiCommitDetailsCard(scroll, selectedCommits);
            }

            if (selectedDetails == null || selectedDetails.Commit == null)
            {
                scroll.Add(BuildEmptyState(detailsRead.IsBusy ? string.Empty : "Select a commit to see its details."));
                return scroll;
            }

            var commit = selectedDetails.Commit;
            scroll.Add(UnitGitUi.Text(commit.Subject, "unitgit-details-title"));
            if (!string.IsNullOrWhiteSpace(selectedDetails.Body))
                scroll.Add(UnitGitUi.Text(selectedDetails.Body.Trim(), "ug-details-body"));

            var author = new VisualElement();
            author.AddToClassList("ug-details-meta");
            var avatar = UnitGitUi.Text(Initial(commit.AuthorName), "ug-avatar");
            avatar.AddToClassList("ug-avatar--large");
            avatar.style.backgroundColor = AvatarColor(commit.AuthorEmail + commit.AuthorName);
            author.Add(avatar);
            var who = new VisualElement();
            who.AddToClassList("ug-details-meta__texts");
            who.Add(UnitGitUi.Text(commit.AuthorName, "ug-details-author"));
            who.Add(UnitGitUi.Text(selectedDetails.AuthorDate + (string.IsNullOrWhiteSpace(commit.RelativeDate) ? string.Empty : "  •  " + commit.RelativeDate),
                "ug-details-date"));
            author.Add(who);
            author.tooltip = commit.AuthorName + " <" + commit.AuthorEmail + ">" +
                             (!string.IsNullOrWhiteSpace(selectedDetails.CommitterName) && selectedDetails.CommitterName != commit.AuthorName
                                 ? "\ncommitted by " + selectedDetails.CommitterName + " on " + selectedDetails.CommitterDate : string.Empty);
            scroll.Add(author);

            var ids = new VisualElement();
            ids.AddToClassList("ug-details-ids");
            var hash = UnitGitUi.Chip(commit.ShortHash, null, commit.FullHash);
            hash.AddToClassList("ug-hash-chip");
            ids.Add(hash);
            ids.Add(UnitGitUi.Icon(UnitGitIconKind.Copy, "Copy the full hash", () =>
            {
                CopyText("copy hash", commit.FullHash);
                ShowToast("Copied " + commit.ShortHash);
            }, "ug-icon-button--small"));
            if (commit.Parents != null && commit.Parents.Length > 0)
            {
                ids.Add(UnitGitUi.Text(commit.Parents.Length > 1 ? "merge of" : "parent", "ug-details-ids__label"));
                foreach (string parent in commit.Parents)
                {
                    string target = parent;
                    var link = new Button { text = parent.Length > 7 ? parent.Substring(0, 7) : parent, tooltip = "Select this parent" };
                    link.AddToClassList("ug-link");
                    UnitGitUi.Press(link, () => SelectCommitByHash(target));
                    ids.Add(link);
                }
            }
            scroll.Add(ids);

            if (!string.IsNullOrWhiteSpace(commit.Decorations))
            {
                var refs = new VisualElement();
                refs.AddToClassList("ug-log-refs");
                refs.AddToClassList("ug-details-refs");
                BindRefs(refs, commit.Decorations);
                scroll.Add(refs);
            }
            return scroll;
        }

        private void SelectCommitByHash(string hash)
        {
            var commit = snapshot?.Commits.FirstOrDefault(c => c.FullHash == hash);
            if (commit == null)
            {
                ShowToast("That commit is further back in the history: scroll down to load it.", error: true);
                return;
            }
            SelectCommitFromRow(commit, false);
            int row = logItems.FindIndex(item => item.Release == null && item.Commit == commit);
            if (row >= 0) logList?.ScrollToItem(row);
        }

        private VisualElement BuildMultiCommitDetailsCard(VisualElement card, IList<UnitGitCommit> commits)
        {
            card.Add(UnitGitUi.Text(commits.Count + " commits selected", "unitgit-details-title"));
            foreach (var commit in commits.Take(200))
            {
                var row = new VisualElement();
                row.AddToClassList("ug-details-commit");
                row.Add(UnitGitUi.Text(commit.ShortHash, "ug-details-commit__hash"));
                row.Add(UnitGitUi.Text(commit.Subject, "ug-details-commit__subject"));
                row.tooltip = commit.FullHash + "\n" + commit.AuthorName + " <" + commit.AuthorEmail + ">\n" + commit.Subject;
                card.Add(row);
            }
            return card;
        }

        private readonly UnitGitLatestRequest<SelectionDetails> detailsRead = new UnitGitLatestRequest<SelectionDetails>();
        private readonly Dictionary<string, UnitGitCommitDetails> detailsCache = new Dictionary<string, UnitGitCommitDetails>();
        private SelectionDetails loadedSelection;
        private string requestedSelection;

        private sealed class SelectionDetails
        {
            public readonly List<UnitGitCommitDetails> Details = new List<UnitGitCommitDetails>();
            public List<string> Files;
            public readonly UnitGitFileList FileList = new UnitGitFileList();
        }

        private void EnsureCommitDetailsLoaded()
        {
            var commits = GetSelectedCommitsInLogOrder();
            if (commits.Count == 0 && selectedCommit != null)
                commits.Add(selectedCommit);
            string key = string.Join(";", commits.Select(commit => commit.FullHash));
            if (key == requestedSelection)
            {
                selectedDetails = loadedSelection?.Details.FirstOrDefault(item => item.Commit.FullHash == selectedCommit?.FullHash);
                return;
            }
            requestedSelection = key;
            loadedSelection = null;
            selectedDetails = null;
            selectedCommitFile = string.Empty;
            string root = gitService.ProjectRoot;
            detailsRead.Request(stale =>
            {
                var result = new SelectionDetails();
                var files = new HashSet<string>(StringComparer.Ordinal);
                var service = new UnitGitService(root) { ReadSuperseded = stale };
                foreach (var commit in commits)
                {
                    if (stale())
                        return null;
                    if (!detailsCache.TryGetValue(commit.FullHash, out var details))
                    {
                        details = service.GetCommitDetails(commit.FullHash);
                        if (stale())
                            return null;
                        if (details != null && details.ChangedFiles.Count <= 10000)
                        {
                            if (detailsCache.Count >= 64 || detailsCache.Values.Sum(item => item.ChangedFiles.Count) > 50000)
                                detailsCache.Clear();
                            detailsCache[commit.FullHash] = details;
                        }
                    }
                    if (details == null)
                        continue;
                    // The log's commit knows its parents and date; the details read keeps them.
                    if (details.Commit != null && (details.Commit.Parents == null || details.Commit.Parents.Length == 0))
                    {
                        details.Commit.Parents = commit.Parents;
                        details.Commit.Timestamp = commit.Timestamp;
                        details.Commit.RelativeDate = commit.RelativeDate;
                    }
                    result.Details.Add(details);
                    foreach (string file in details.ChangedFiles)
                        files.Add(file);
                }
                result.Files = files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
                var tree = ChangedFileTreeNode.CreateRoot();
                foreach (string file in result.Files)
                    tree.AddFile(file);
                AddChangedFileTreeNode(result.FileList, tree, 0);
                return result;
            });
            EnsureEditorUpdatePump();
        }

        private void PollCommitDetails()
        {
            if (!detailsRead.Poll(out SelectionDetails result, out Exception error))
                return;
            loadedSelection = result;
            if (error != null)
                AppendConsole("commit details", error.Message);
            selectedDetails = result?.Details.FirstOrDefault(item => item.Commit.FullHash == selectedCommit?.FullHash);
            if (activeTab == UnitGitTab.Log && commitDetailsRoot != null)
            {
                RefreshDetailsPane();
                UnitGitUi.Enter(commitDetailsRoot.Q(className: "unitgit-details-pane"), "ug-fade-enter");
            }
        }
    }
}
