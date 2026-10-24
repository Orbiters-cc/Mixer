using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The Log's branches, as in JetBrains IDEs: HEAD, then local and remote branches in folders. A click shows that
    // branch's history (again: every branch), a double click checks it out, a right click has everything else.
    internal sealed partial class UnitGitWindow
    {
        private VisualElement branchesListRoot;

        private VisualElement BuildBranchesPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-branches-pane");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            toolbar.Add(UnitGitUi.Search(branchSearch, "Branches", value =>
            {
                branchSearch = value ?? string.Empty;
                RefreshBranchesPane();
            }, "ug-branches-search"));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.CreateBranch, "New branch from the selected commit", () => PromptCreateBranchFrom(null)));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.Remote, "Remote: connect this project to GitHub or GitLab", OpenRemoteSetup));
            pane.Add(toolbar);

            var scroll = new ScrollView();
            scroll.name = "unitgit-branch-scroll";
            scroll.AddToClassList("unitgit-branch-scroll");
            branchesListRoot = scroll.contentContainer;
            FillBranches();
            pane.Add(scroll);
            return pane;
        }

        private void RefreshBranchesPane()
        {
            if (branchesListRoot == null || branchesListRoot.panel == null) return;
            branchesListRoot.Clear();
            FillBranches();
        }

        private void FillBranches()
        {
            // While visiting (or rebasing, bisecting), HEAD is a commit of its own: its history is "HEAD" for Git.
            bool visiting = IsVisiting;
            bool detached = snapshot.IsDetached;
            string gitWork = snapshot.DetachedBy.ToLowerInvariant();
            string headRef = detached ? "HEAD" : snapshot.CurrentBranch;
            string headText = visiting ? "visiting " + snapshot.DetachedCommit.ShortHash : detached ? (gitWork.Length > 0 ? gitWork : "detached HEAD") : snapshot.CurrentBranch;
            var head = BranchTreeRow(UnitGitIconKind.Star, "HEAD", headText, 0,
                !string.IsNullOrEmpty(logFilter.Branch) && logFilter.Branch == headRef, "ug-branch-row--head");
            head.tooltip = visiting
                ? "No branch is checked out: you are visiting " + snapshot.DetachedCommit.ShortHash + ". Click to show only its history."
                : detached ? "No branch is checked out" + (gitWork.Length > 0 ? ": Git is " + gitWork + "." : ".") + " Click to show only its history."
                : "The checked-out branch: " + snapshot.CurrentBranch + ". Click to show only its history.";
            head.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0 || string.IsNullOrEmpty(headRef)) return;
                SetLogFilter(branch: logFilter.Branch == headRef ? string.Empty : headRef);
            });
            branchesListRoot.Add(head);
            AddBranchSection("Local", snapshot.Branches.Where(branch => !branch.IsRemote), BranchLocalFoldPref);
            AddBranchSection("Remote", snapshot.Branches.Where(branch => branch.IsRemote), BranchRemoteFoldPref);
        }

        private void AddBranchSection(string title, IEnumerable<UnitGitBranch> branches, string prefKey)
        {
            var filtered = branches.Where(MatchesBranchSearch).ToList();
            bool expanded = GetFoldoutExpanded(prefKey, true) || !string.IsNullOrWhiteSpace(branchSearch);
            var header = BranchTreeRow(expanded ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed, title,
                filtered.Count.ToString(), 0, false, "ug-branch-row--section");
            header.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                EditorPrefs.SetBool(prefKey, !GetFoldoutExpanded(prefKey, true));
                RefreshBranchesPane();
            });
            branchesListRoot.Add(header);
            if (!expanded) return;
            if (filtered.Count == 0)
            {
                branchesListRoot.Add(UnitGitUi.Text(string.IsNullOrWhiteSpace(branchSearch) ? "No " + title.ToLowerInvariant() + " branches" : "No match",
                    "unitgit-branch-empty"));
                return;
            }

            foreach (IGrouping<string, UnitGitBranch> group in filtered.GroupBy(GetBranchFolder).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                int depth = 1;
                if (!string.IsNullOrEmpty(group.Key))
                {
                    string folderPrefKey = GetFoldoutPrefKey("BranchFolder." + title, group.Key);
                    bool folderExpanded = GetFoldoutExpanded(folderPrefKey, true) || !string.IsNullOrWhiteSpace(branchSearch);
                    var folder = BranchTreeRow(UnitGitIconKind.Folder, group.Key, group.Count().ToString(), 1, false, "ug-branch-row--folder");
                    folder.RegisterCallback<MouseDownEvent>(evt =>
                    {
                        if (evt.button != 0) return;
                        EditorPrefs.SetBool(folderPrefKey, !GetFoldoutExpanded(folderPrefKey, true));
                        RefreshBranchesPane();
                    });
                    branchesListRoot.Add(folder);
                    if (!folderExpanded) continue;
                    depth = 2;
                }

                foreach (UnitGitBranch branch in group.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                    branchesListRoot.Add(BuildBranchRow(branch, depth));
            }
        }

        private VisualElement BranchTreeRow(UnitGitIconKind icon, string name, string detail, int depth, bool selected, string extraClass)
        {
            var row = new VisualElement();
            row.AddToClassList("ug-row");
            row.AddToClassList("unitgit-branch-row");
            if (!string.IsNullOrEmpty(extraClass)) row.AddToClassList(extraClass);
            row.EnableInClassList("ug-row--selected", selected);
            row.style.paddingLeft = 6 + depth * 14;
            var glyph = new UnitGitIconElement(icon);
            glyph.AddToClassList("unitgit-branch-icon");
            row.Add(glyph);
            row.Add(UnitGitUi.Text(name, "unitgit-branch-name"));
            if (!string.IsNullOrEmpty(detail)) row.Add(UnitGitUi.Text(detail, "unitgit-branch-hash"));
            return row;
        }

        private bool MatchesBranchSearch(UnitGitBranch branch)
        {
            return branch != null &&
                   (string.IsNullOrWhiteSpace(branchSearch) ||
                    branch.Name.IndexOf(branchSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetBranchFolder(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.Name;
            if (branch.IsRemote)
            {
                // Remote branches are grouped by their remote ("origin"), then by their own folders.
                int first = name.IndexOf("/", StringComparison.Ordinal);
                if (first < 0) return string.Empty;
                string remote = name.Substring(0, first);
                string rest = name.Substring(first + 1);
                int slash = rest.LastIndexOf("/", StringComparison.Ordinal);
                return slash > 0 ? remote + "/" + rest.Substring(0, slash) : remote;
            }

            int last = name.LastIndexOf("/", StringComparison.Ordinal);
            return last > 0 ? name.Substring(0, last) : string.Empty;
        }

        private VisualElement BuildBranchRow(UnitGitBranch branch, int depth)
        {
            bool filtered = logFilter.Branch == branch.Name;
            var row = BranchTreeRow(branch.IsCurrent ? UnitGitIconKind.Star : UnitGitIconKind.Branch, GetBranchLeafName(branch),
                branch.IsRemote ? branch.ShortHash : (string.IsNullOrWhiteSpace(branch.Upstream) ? branch.ShortHash : branch.Upstream), depth, filtered,
                branch.IsCurrent ? "unitgit-branch-row--current" : null);
            row.tooltip = branch.Name + (branch.IsCurrent ? " (checked out)" : string.Empty) +
                          "\nClick: show its history • Double click: checkout • Right click: more";
            var glyph = row.Q<UnitGitIconElement>();
            if (!branch.IsCurrent) glyph.style.opacity = 0.85f;
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 1)
                {
                    selectedBranch = branch;
                    ShowBranchContextMenu(branch);
                    evt.StopPropagation();
                    return;
                }
                if (evt.button != 0) return;
                selectedBranch = branch;
                if (evt.clickCount == 2 && !branch.IsCurrent)
                {
                    CheckoutSelectedBranch();
                    evt.StopPropagation();
                    return;
                }
                SetLogFilter(branch: filtered ? string.Empty : branch.Name);
            });
            return row;
        }

        private void ShowBranchContextMenu(UnitGitBranch branch)
        {
            var menu = new GenericMenu();
            string current = snapshot?.CurrentBranch ?? string.Empty;
            if (!branch.IsCurrent) menu.AddItem(new GUIContent("Checkout"), false, () => WithBranch(branch, CheckoutSelectedBranch));
            else menu.AddDisabledItem(new GUIContent("Checkout (current)"));
            menu.AddItem(new GUIContent("New branch from ‘" + branch.Name.Replace("/", "∕") + "’…"), false, () => PromptCreateBranchFrom(branch.Name));
            if (!branch.IsCurrent)
                menu.AddItem(new GUIContent("Merge into ‘" + current.Replace("/", "∕") + "’"), false, () => WithBranch(branch, MergeSelectedBranch));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Show its history"), false, () => SetLogFilter(branch: branch.Name));
            if (!branch.IsRemote) menu.AddItem(new GUIContent("Update (fast-forward from its upstream)"), false, () => WithBranch(branch, UpdateSelectedBranch));
            menu.AddSeparator(string.Empty);
            if (!branch.IsCurrent) menu.AddItem(new GUIContent("Delete…"), false, () => WithBranch(branch, DeleteSelectedBranch));
            menu.AddItem(new GUIContent("Copy name"), false, () => EditorGUIUtility.systemCopyBuffer = branch.Name);
            menu.ShowAsContext();
        }

        private static string GetBranchLeafName(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.Name;
            int slash = name.LastIndexOf("/", StringComparison.Ordinal);
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}
