using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The Git log, as in JetBrains IDEs: branches on the left, the commit graph with search and filters (branch, user,
    // date) in the middle, the selected commit's files and message on the right. Selecting only restyles the visible rows
    // and refills the details: nothing else is rebuilt.
    internal sealed partial class UnitGitWindow
    {
        private const string BranchesVisiblePref = "Orbiters.UnitGit.Log.BranchesVisible";
        private const float LogRowHeight = 28f;

        internal sealed class LogItem
        {
            public UnitGitCommit Commit;
            public UnitGitReleaseEntry Release;
            public UnitGitCommitGraph.Row Graph;
        }

        private VisualElement BuildLogBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var logDetailsSplit = BuildTrackedSplit(LogDetailsSplitPref, 1, 360f);
            logDetailsSplit.Add(BuildLogPane());
            logTab.commitDetailsRoot = new VisualElement();
            logTab.commitDetailsRoot.AddToClassList("ug-details-host");
            logTab.commitDetailsRoot.Add(BuildDetailsPane());
            logDetailsSplit.Add(logTab.commitDetailsRoot);

            if (EditorPrefs.GetBool(BranchesVisiblePref, true))
            {
                var branchSplit = BuildTrackedSplit(BranchLogSplitPref, 0, 210f);
                branchSplit.Add(BuildBranchesPane());
                branchSplit.Add(logDetailsSplit);
                workspace.Add(branchSplit);
            }
            else workspace.Add(logDetailsSplit);
            return workspace;
        }

        private VisualElement BuildLogPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-log-pane");
            // Narrow: the hash goes, then the author's name (its initial stays), then the date; the subject keeps its room.
            pane.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float width = evt.newRect.width;
                pane.EnableInClassList("ug-log--narrow", width < 640f);
                pane.EnableInClassList("ug-log--compact", width < 380f);
            });

            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            var branches = UnitGitUi.Icon(UnitGitIconKind.Branch, "Show or hide the branches", () =>
            {
                EditorPrefs.SetBool(BranchesVisiblePref, !EditorPrefs.GetBool(BranchesVisiblePref, true));
                RebuildContent();
            });
            branches.EnableInClassList("ug-icon-button--on", EditorPrefs.GetBool(BranchesVisiblePref, true));
            toolbar.Add(branches);
            var search = UnitGitUi.Search(logTab.logSearchDraft, "Text or hash", value =>
            {
                logTab.logSearchDraft = value ?? string.Empty;
                QueueLogSearchRefresh();
            }, "ug-log-search");
            toolbar.Add(search);
            toolbar.Add(BuildFilterButton(UnitGitIconKind.Branch, "Branch", string.IsNullOrEmpty(logTab.logFilter.Branch) ? null : logTab.logFilter.Branch, ShowBranchFilterMenu));
            toolbar.Add(BuildFilterButton(UnitGitIconKind.User, "User", string.IsNullOrEmpty(logTab.logFilter.Author) ? null : logTab.logFilter.Author, ShowUserFilterMenu));
            toolbar.Add(BuildFilterButton(UnitGitIconKind.Calendar, "Date", logTab.logFilter.Since.HasValue ? SinceLabel(logTab.logFilter.Since.Value) : null, ShowDateFilterMenu));
            if (!string.IsNullOrWhiteSpace(logTab.logFilter.Path))
            {
                var file = BuildFilterButton(UnitGitIconKind.Changes, "File", GetFileLeaf(logTab.logFilter.Path), ShowPathFilterMenu);
                file.tooltip = "History of " + logTab.logFilter.Path;
                toolbar.Add(file);
            }
            toolbar.Add(UnitGitUi.Spacer());
            logTab.logCountLabel = UnitGitUi.Text(LogCountText(), "ug-log-count");
            toolbar.Add(logTab.logCountLabel);
            pane.Add(toolbar);

            BuildLogItems();
            var header = new VisualElement();
            header.AddToClassList("unitgit-log-header");
            var graphSpace = new VisualElement();
            graphSpace.AddToClassList("ug-log-graph-space");
            graphSpace.style.width = logTab.graphWidth;
            header.Add(graphSpace);
            header.Add(BuildHeaderLabel("Commit", "unitgit-log-header__commit"));
            header.Add(BuildHeaderLabel("Author", "unitgit-log-header__author"));
            header.Add(BuildHeaderLabel("Date", "unitgit-log-header__date"));
            header.Add(BuildHeaderLabel("Hash", "unitgit-log-header__hash"));
            pane.Add(header);

            if (logTab.logItems.Count == 0)
            {
                pane.Add(BuildLogEmptyState());
                return pane;
            }

            logTab.logList = new ListView
            {
                itemsSource = logTab.logItems,
                fixedItemHeight = LogRowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = MakeLogRow,
                bindItem = BindLogRow,
            };
            logTab.logList.AddToClassList("unitgit-log-list");
            logTab.logList.focusable = true;
            logTab.logList.style.flexGrow = 1;
            logTab.logList.style.flexBasis = 0;
            logTab.logList.style.minHeight = 0;
            var scroll = logTab.logList.Q<ScrollView>();
            scroll.name = "unitgit-log-scroll";
            scroll.verticalScroller.valueChanged += value => LoadMoreHistoryIfNeeded(scroll, value);
            logTab.logList.RegisterCallback<KeyDownEvent>(OnLogKeyDown);
            pane.Add(logTab.logList);
            return pane;
        }

        private VisualElement BuildLogEmptyState()
        {
            var empty = new VisualElement();
            empty.AddToClassList("ug-empty");
            bool filtered = !string.IsNullOrWhiteSpace(logTab.logSearch) || !logTab.logFilter.IsEmpty;
            empty.Add(UnitGitUi.Text(filtered ? "No commit matches" : "No commits yet", "ug-empty__title"));
            empty.Add(UnitGitUi.Text(filtered ? "Change the search or the filters." : "Commit your changes to start the history.", "ug-empty__text"));
            if (filtered)
            {
                var clear = UnitGitUi.Pill("Clear filters", ClearLogFilters, "ghost", UnitGitIconKind.Close);
                clear.style.marginTop = 10;
                empty.Add(clear);
            }
            return empty;
        }

        private string LogCountText()
        {
            if (snapshot == null) return string.Empty;
            int count = snapshot.Commits.Count;
            return count + (snapshot.HasMoreCommits ? "+" : string.Empty) + (count == 1 ? " commit" : " commits");
        }

        // Commits and their release rows, with the graph lanes.
        private void BuildLogItems()
        {
            logTab.logItems.Clear();
            if (snapshot == null) return;
            var graph = UnitGitCommitGraph.Layout(snapshot.Commits, out int lanes);
            logTab.graphWidth = UnitGitGraphCell.Inset * 2 + (Math.Min(lanes, 10) - 1) * UnitGitGraphCell.LaneWidth + 6f;
            UnitGitCommitGraph.Row previous = null;
            for (int i = 0; i < snapshot.Commits.Count; i++)
            {
                var commit = snapshot.Commits[i];
                var release = GetReleaseForCommit(commit);
                if (release != null)
                    logTab.logItems.Add(new LogItem { Commit = commit, Release = release, Graph = previous != null ? UnitGitCommitGraph.PassThrough(previous) : new UnitGitCommitGraph.Row { Lane = -1 } });
                logTab.logItems.Add(new LogItem { Commit = commit, Graph = graph[i] });
                previous = graph[i];
            }
        }

        // ---- Rows ----------------------------------------------------------------------------------------------------

        private VisualElement MakeLogRow()
        {
            var row = new VisualElement { focusable = false };
            row.AddToClassList("unitgit-log-row");
            var graph = new UnitGitGraphCell();
            row.Add(graph);

            var subject = new VisualElement { name = "subject" };
            subject.AddToClassList("unitgit-log-subject-wrap");
            subject.Add(UnitGitUi.Text(string.Empty, "unitgit-log-subject"));
            var refs = new VisualElement { name = "refs" };
            refs.AddToClassList("ug-log-refs");
            subject.Add(refs);
            row.Add(subject);

            var release = new VisualElement { name = "release" };
            release.AddToClassList("ug-log-release");
            var pill = new VisualElement();
            pill.AddToClassList("ug-log-release__pill");
            pill.Add(UnitGitUi.Text(string.Empty, "ug-log-release__name"));
            pill.Add(UnitGitUi.Chip(string.Empty, "accent"));
            pill.Add(UnitGitUi.Text(string.Empty, "ug-log-release__title"));
            release.Add(pill);
            row.Add(release);

            var author = new VisualElement { name = "author" };
            author.AddToClassList("unitgit-log-author");
            author.Add(UnitGitUi.Text(string.Empty, "ug-avatar"));
            author.Add(UnitGitUi.Text(string.Empty, "ug-log-author__name"));
            row.Add(author);
            row.Add(UnitGitUi.Text(string.Empty, "unitgit-log-date"));
            row.Add(UnitGitUi.Text(string.Empty, "unitgit-log-hash"));

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (!(row.userData is LogItem item)) return;
                if (item.Release != null) HandleReleaseRowMouseDown(evt, item.Commit, item.Release);
                else HandleCommitRowMouseDown(evt, item.Commit);
            });
            return row;
        }

        private void BindLogRow(VisualElement row, int index)
        {
            var item = logTab.logItems[index];
            row.userData = item;
            var commit = item.Commit;
            bool isRelease = item.Release != null;
            bool selected = isRelease
                ? logTab.selectedCommit != null && logTab.selectedCommit.FullHash == commit.FullHash && string.Equals(logTab.selectedReleaseId, commit.ReleaseId, StringComparison.Ordinal)
                : logTab.selectedCommitHashes.Contains(commit.FullHash) && string.IsNullOrEmpty(logTab.selectedReleaseId);
            row.EnableInClassList("unitgit-log-row--selected", selected);
            row.EnableInClassList("ug-log-row--release", isRelease);
            row.EnableInClassList("ug-log-row--merge", !isRelease && commit.Parents != null && commit.Parents.Length > 1);

            var graph = (UnitGitGraphCell)row[0];
            graph.style.width = logTab.graphWidth;
            graph.Bind(item.Graph, selected && !isRelease);

            var subjectWrap = row.Q("subject");
            var release = row.Q("release");
            var author = row.Q("author");
            var date = row.Q<Label>(className: "unitgit-log-date");
            var hash = row.Q<Label>(className: "unitgit-log-hash");
            subjectWrap.style.display = isRelease ? DisplayStyle.None : DisplayStyle.Flex;
            release.style.display = isRelease ? DisplayStyle.Flex : DisplayStyle.None;
            author.style.visibility = isRelease ? Visibility.Hidden : Visibility.Visible;
            date.style.visibility = isRelease ? Visibility.Hidden : Visibility.Visible;
            hash.style.visibility = isRelease ? Visibility.Hidden : Visibility.Visible;

            if (isRelease)
            {
                var r = item.Release;
                string name = !string.IsNullOrWhiteSpace(r.name) ? r.name : !string.IsNullOrWhiteSpace(r.tool) ? r.tool : "Release";
                release.Q<Label>(className: "ug-log-release__name").text = Shorten(name, 32);
                var version = release.Q<Label>(className: "ug-chip");
                version.text = r.version ?? string.Empty;
                version.style.display = string.IsNullOrWhiteSpace(r.version) ? DisplayStyle.None : DisplayStyle.Flex;
                release.Q<Label>(className: "ug-log-release__title").text = Shorten(r.title ?? string.Empty, 60);
                row.tooltip = "Release checkpoint" + (string.IsNullOrWhiteSpace(r.tool) ? string.Empty : " published by " + r.tool) + ". Click to see its details.";
                return;
            }

            subjectWrap.Q<Label>(className: "unitgit-log-subject").text = commit.Subject;
            BindRefs(subjectWrap.Q("refs"), commit.Decorations);
            var avatar = author.Q<Label>(className: "ug-avatar");
            avatar.text = Initial(commit.AuthorName);
            avatar.style.backgroundColor = AvatarColor(commit.AuthorEmail + commit.AuthorName);
            author.Q<Label>(className: "ug-log-author__name").text = commit.AuthorName;
            date.text = commit.RelativeDate;
            date.tooltip = commit.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(commit.Timestamp).LocalDateTime.ToString("f", CultureInfo.CurrentCulture) : null;
            hash.text = commit.ShortHash;
            row.tooltip = null;
        }

        // Branch and tag labels, as chips: the checked-out branch first, then local, remote and tags. While a commit is
        // visited, it says so, and the branch "Back" returns to stands out at its latest commit.
        private void BindRefs(VisualElement refs, string decorations)
        {
            refs.Clear();
            if (string.IsNullOrWhiteSpace(decorations)) return;
            string returnBranch = snapshot != null && snapshot.IsDetached ? snapshot.ReturnBranch : string.Empty;
            int shown = 0, hidden = 0;
            foreach (string raw in decorations.Split(','))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                string variant = "local", text = part, tooltip = part;
                bool returnTip = false;
                if (part == "HEAD")
                {
                    // A detached HEAD: the commit being visited, or where Git is rebasing or bisecting.
                    variant = "head";
                    bool visiting = IsVisiting;
                    text = visiting ? "Visiting" : "HEAD";
                    tooltip = visiting ? "HEAD: the commit you are visiting. Every branch keeps its later commits."
                        : snapshot != null && snapshot.DetachedBy.Length > 0 ? "HEAD: Git is " + snapshot.DetachedBy.ToLowerInvariant() + " here."
                        : "HEAD: the checked-out commit, on no branch.";
                }
                else if (part.StartsWith("HEAD -> ", StringComparison.Ordinal)) { variant = "head"; text = part.Substring(8); }
                else if (part.StartsWith("tag: ", StringComparison.Ordinal)) { variant = "tag"; text = part.Substring(5); }
                else if (part.Contains("/") && !(returnBranch.Length > 0 && part == returnBranch)) variant = "remote";
                if (variant == "local" && returnBranch.Length > 0 && part == returnBranch)
                {
                    returnTip = true;
                    tooltip = part + ": its latest commit, where ‘Back to " + part + "’ returns.";
                }
                if (shown >= 3) { hidden++; continue; }
                var chip = UnitGitUi.Chip(text.Length > 26 ? text.Substring(0, 24) + "…" : text, variant, tooltip);
                if (returnTip) chip.AddToClassList("ug-chip--return");
                refs.Add(chip);
                shown++;
            }
            if (hidden > 0) refs.Add(UnitGitUi.Chip("+" + hidden));
        }

        internal static string Initial(string name)
        {
            name = (name ?? string.Empty).Trim();
            return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
        }

        internal static Color AvatarColor(string key)
        {
            int hash = 17;
            foreach (char c in key ?? string.Empty) hash = hash * 31 + c;
            return Color.HSVToRGB((hash & 0x7fffffff) % 360 / 360f, 0.45f, 0.62f);
        }

        // ---- Filters -------------------------------------------------------------------------------------------------

        private Button BuildFilterButton(UnitGitIconKind icon, string title, string value, Action open)
        {
            var button = new Button { tooltip = "Filter by " + title.ToLowerInvariant() };
            button.AddToClassList("ug-filter");
            button.EnableInClassList("ug-filter--on", value != null);
            button.Add(new UnitGitIconElement(icon));
            button.Add(UnitGitUi.Text(value == null ? title : Shorten(value, 22), "ug-filter__label"));
            var chevron = new UnitGitIconElement(UnitGitIconKind.ChevronExpanded);
            chevron.AddToClassList("ug-filter__chevron");
            button.Add(chevron);
            UnitGitUi.Press(button, open);
            return button;
        }

        private void ShowBranchFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All branches"), string.IsNullOrEmpty(logTab.logFilter.Branch), () => SetLogFilter(branch: string.Empty));
            if (!string.IsNullOrWhiteSpace(snapshot?.CurrentBranch))
                menu.AddItem(new GUIContent("Current (" + snapshot.CurrentBranch.Replace("/", "∕") + ")"), logTab.logFilter.Branch == snapshot.CurrentBranch,
                    () => SetLogFilter(branch: snapshot.CurrentBranch));
            menu.AddSeparator(string.Empty);
            foreach (var branch in snapshot.Branches.OrderBy(b => b.IsRemote).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = branch.Name;
                menu.AddItem(new GUIContent((branch.IsRemote ? "Remote/" : "Local/") + name.Replace("/", "∕")), logTab.logFilter.Branch == name, () => SetLogFilter(branch: name));
            }
            menu.ShowAsContext();
        }

        private void ShowUserFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Anyone"), string.IsNullOrEmpty(logTab.logFilter.Author), () => SetLogFilter(author: string.Empty));
            menu.AddSeparator(string.Empty);
            foreach (string author in snapshot.Commits.Select(commit => commit.AuthorName).Where(a => !string.IsNullOrWhiteSpace(a))
                         .GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(30))
            {
                string value = author;
                menu.AddItem(new GUIContent(value.Replace("/", "∕")), logTab.logFilter.Author == value, () => SetLogFilter(author: value));
            }
            menu.ShowAsContext();
        }

        private void ShowDateFilterMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Any time"), !logTab.logFilter.Since.HasValue, () => SetLogFilter(clearSince: true));
            menu.AddSeparator(string.Empty);
            foreach (var (label, days) in new[] { ("Last 24 hours", 1), ("Last 7 days", 7), ("Last 30 days", 30), ("Last year", 365) })
            {
                int span = days;
                menu.AddItem(new GUIContent(label), logTab.logFilter.Since.HasValue && SinceLabel(logTab.logFilter.Since.Value) == label,
                    () => SetLogFilter(since: DateTime.Now.Date.AddDays(1 - span)));
            }
            menu.ShowAsContext();
        }

        private void ShowPathFilterMenu()
        {
            var menu = new GenericMenu();
            string path = logTab.logFilter.Path;
            menu.AddItem(new GUIContent("Every file"), false, () => SetLogFilter(path: string.Empty));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Copy path"), false, () => EditorGUIUtility.systemCopyBuffer = path);
            menu.ShowAsContext();
        }

        /// <summary>JetBrains' "Show History": the Log, for the commits that changed this file or folder.</summary>
        private void ShowHistory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            logTab.logFilter.Path = path.Replace('\\', '/').TrimEnd('/');
            logTab.selectedCommitHashes.Clear();
            logTab.selectedCommit = null;
            if (activeTab != UnitGitTab.Log) SetActiveTab(UnitGitTab.Log);
            SetLogFilter();
        }

        private static string SinceLabel(DateTime since)
        {
            int days = (int)Math.Round((DateTime.Now.Date.AddDays(1) - since.Date).TotalDays);
            return days <= 1 ? "Last 24 hours" : days <= 7 ? "Last 7 days" : days <= 30 ? "Last 30 days" : days <= 365 ? "Last year" : "Since " + since.ToShortDateString();
        }

        private void SetLogFilter(string branch = null, string author = null, DateTime? since = null, bool clearSince = false, string path = null)
        {
            if (branch != null) logTab.logFilter.Branch = branch;
            if (author != null) logTab.logFilter.Author = author;
            if (path != null) logTab.logFilter.Path = path;
            if (since.HasValue) logTab.logFilter.Since = since;
            if (clearSince) logTab.logFilter.Since = null;
            logTab.historyLimit = CommitPageSize * 3;
            RebuildContent();
            RefreshSnapshot();
        }

        private void ClearLogFilters()
        {
            logTab.logFilter.Branch = string.Empty;
            logTab.logFilter.Author = string.Empty;
            logTab.logFilter.Since = null;
            logTab.logFilter.Path = string.Empty;
            logTab.logSearchDraft = logTab.logSearch = string.Empty;
            logTab.historyLimit = CommitPageSize * 3;
            RebuildContent();
            RefreshSnapshot();
        }

        // Scrolling near the end loads older commits: no pages to click through.
        private void LoadMoreHistoryIfNeeded(ScrollView scroll, float value)
        {
            if (value >= scroll.verticalScroller.highValue - LogRowHeight * 10) LoadMoreHistory();
        }

        private void LoadMoreHistory()
        {
            if (snapshot == null || !snapshot.HasMoreCommits || refreshingSnapshot || logTab.loadingMoreHistory) return;
            logTab.loadingMoreHistory = true;
            logTab.historyOnlyRequested = true;
            logTab.historyLimit += CommitPageSize * 3;
            if (logTab.logCountLabel != null) logTab.logCountLabel.text = "Loading older commits…";
            RefreshSnapshot();
        }

        // ---- Selection ------------------------------------------------------------------------------------------------

        private void SelectCommitFromRow(UnitGitCommit commit, bool selectRelease)
        {
            SelectCommitFromRow(commit, selectRelease, false, false);
        }

        private void SelectCommitFromRow(UnitGitCommit commit, bool selectRelease, bool rangeSelect, bool toggleSelect)
        {
            if (commit == null)
            {
                return;
            }

            if (selectRelease)
            {
                logTab.selectedCommitHashes.Clear();
                logTab.selectionAnchorHash = string.Empty;
                SetPrimarySelectedCommit(commit, commit.ReleaseId);
                RefreshLogSelection();
                return;
            }

            logTab.selectedReleaseId = string.Empty;
            if (rangeSelect)
            {
                ApplyRangeCommitSelection(commit, toggleSelect);
            }
            else if (toggleSelect)
            {
                ApplyToggleCommitSelection(commit);
            }
            else
            {
                logTab.selectedCommitHashes.Clear();
                logTab.selectedCommitHashes.Add(commit.FullHash);
                logTab.selectionAnchorHash = commit.FullHash;
                SetPrimarySelectedCommit(commit, string.Empty);
            }

            RefreshLogSelection();
        }

        // The rows restyle at once; the details follow (they load in the background).
        private void RefreshLogSelection()
        {
            if (logTab.logList == null || logTab.logList.panel == null)
            {
                RebuildContent();
                return;
            }
            logTab.logList.RefreshItems();
            RefreshDetailsPane();
        }

        private void RefreshDetailsPane()
        {
            if (logTab.commitDetailsRoot == null) return;
            RememberScrollOffsets();
            logTab.commitDetailsRoot.Clear();
            logTab.commitDetailsRoot.Add(BuildDetailsPane());
            RestoreScrollOffsets();
        }

        private void SetPrimarySelectedCommit(UnitGitCommit commit, string releaseId)
        {
            logTab.selectedCommit = commit;
            logTab.selectedDetails = null;
            logTab.selectedReleaseId = string.IsNullOrWhiteSpace(releaseId) ? string.Empty : releaseId;
        }

        private void ApplyToggleCommitSelection(UnitGitCommit commit)
        {
            if (logTab.selectedCommitHashes.Contains(commit.FullHash))
            {
                logTab.selectedCommitHashes.Remove(commit.FullHash);
                UnitGitCommit nextCommit = GetSelectedCommitsInLogOrder().FirstOrDefault();
                SetPrimarySelectedCommit(nextCommit, string.Empty);
            }
            else
            {
                logTab.selectedCommitHashes.Add(commit.FullHash);
                logTab.selectionAnchorHash = commit.FullHash;
                SetPrimarySelectedCommit(commit, string.Empty);
            }
        }

        private void ApplyRangeCommitSelection(UnitGitCommit commit, bool addToExistingSelection)
        {
            string anchorHash = !string.IsNullOrWhiteSpace(logTab.selectionAnchorHash)
                ? logTab.selectionAnchorHash
                : (logTab.selectedCommit != null ? logTab.selectedCommit.FullHash : commit.FullHash);
            List<UnitGitCommit> range = GetCommitRange(anchorHash, commit.FullHash);
            if (!addToExistingSelection)
            {
                logTab.selectedCommitHashes.Clear();
            }

            foreach (UnitGitCommit rangeCommit in range)
            {
                logTab.selectedCommitHashes.Add(rangeCommit.FullHash);
            }

            if (string.IsNullOrWhiteSpace(logTab.selectionAnchorHash))
            {
                logTab.selectionAnchorHash = anchorHash;
            }

            SetPrimarySelectedCommit(commit, string.Empty);
        }

        private List<UnitGitCommit> GetCommitRange(string firstHash, string secondHash)
        {
            if (snapshot == null || snapshot.Commits == null || snapshot.Commits.Count == 0)
            {
                return new List<UnitGitCommit>();
            }

            int firstIndex = snapshot.Commits.FindIndex(commit => string.Equals(commit.FullHash, firstHash, StringComparison.Ordinal));
            int secondIndex = snapshot.Commits.FindIndex(commit => string.Equals(commit.FullHash, secondHash, StringComparison.Ordinal));
            if (firstIndex < 0 || secondIndex < 0)
            {
                return snapshot.Commits
                    .Where(commit => string.Equals(commit.FullHash, secondHash, StringComparison.Ordinal))
                    .ToList();
            }

            int start = Math.Min(firstIndex, secondIndex);
            int count = Math.Abs(firstIndex - secondIndex) + 1;
            return snapshot.Commits.Skip(start).Take(count).ToList();
        }

        private List<UnitGitCommit> GetSelectedCommitsInLogOrder()
        {
            if (snapshot == null || snapshot.Commits == null || logTab.selectedCommitHashes.Count == 0)
            {
                return new List<UnitGitCommit>();
            }

            return snapshot.Commits
                .Where(commit => logTab.selectedCommitHashes.Contains(commit.FullHash))
                .ToList();
        }

        private void HandleCommitRowMouseDown(MouseDownEvent evt, UnitGitCommit commit)
        {
            if (evt.button != 0 && evt.button != 1)
            {
                return;
            }

            evt.StopPropagation();
            logTab.logList?.Focus();
            bool isContextClick = evt.button == 1;
            bool selectedByContext = isContextClick && logTab.selectedCommitHashes.Contains(commit.FullHash);
            if (selectedByContext)
            {
                SetPrimarySelectedCommit(commit, string.Empty);
                RefreshLogSelection();
            }
            else
            {
                SelectCommitFromRow(commit, false, evt.shiftKey, evt.ctrlKey || evt.commandKey);
            }

            if (isContextClick)
            {
                ShowCommitContextMenu(commit);
            }
        }

        private void HandleReleaseRowMouseDown(MouseDownEvent evt, UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            if (evt.button != 0 && evt.button != 1)
            {
                return;
            }

            evt.StopPropagation();
            SelectCommitFromRow(commit, true);
            if (evt.button == 1)
            {
                ShowReleaseContextMenu(commit, release);
            }
        }

        private void OnLogKeyDown(KeyDownEvent evt)
        {
            if (snapshot == null || snapshot.Commits.Count == 0) return;
            if (evt.keyCode == KeyCode.C && (evt.ctrlKey || evt.commandKey) && logTab.selectedCommit != null)
            {
                CopyText("copy hash", logTab.selectedCommit.FullHash);
                ShowToast("Copied " + logTab.selectedCommit.ShortHash);
                evt.StopPropagation();
                return;
            }
            if (evt.keyCode != KeyCode.UpArrow && evt.keyCode != KeyCode.DownArrow) return;
            int index = logTab.selectedCommit != null ? snapshot.Commits.FindIndex(c => c.FullHash == logTab.selectedCommit.FullHash) : -1;
            int next = Mathf.Clamp(index + (evt.keyCode == KeyCode.UpArrow ? -1 : 1), 0, snapshot.Commits.Count - 1);
            if (next == index) return;
            var commit = snapshot.Commits[next];
            SelectCommitFromRow(commit, false, evt.shiftKey, false);
            int row = logTab.logItems.FindIndex(item => item.Release == null && item.Commit == commit);
            if (row >= 0) logTab.logList?.ScrollToItem(row);
            evt.StopPropagation();
        }

        // After a refresh on the Log: rows, counts and branches change in place, the details only when the selection did.
        private void RefreshLogInPlace()
        {
            string before = logTab.selectedCommit?.FullHash;
            BuildLogItems();
            logTab.loadingMoreHistory = false;
            if (logTab.logCountLabel != null) logTab.logCountLabel.text = LogCountText();
            logTab.logList.itemsSource = logTab.logItems;
            logTab.logList.RefreshItems();
            RefreshBranchesPane();
            if (logTab.selectedCommit?.FullHash != before || logTab.selectedDetails == null) RefreshDetailsPane();
        }

    }
}
