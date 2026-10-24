using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Orbiters.Mixer.Editor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private const string ShellStyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-shell.uss";
        private UnitGitTabs tabs;
        private UnitGitToasts toasts;
        private Label changesBadge;
        private Label shelfBadge;
        private Label conflictsBadge;
        private Button conflictsTab;
        private VisualElement statusArea;
        private Label statusText;
        private Label branchName;
        private Label branchAhead;
        private Label branchBehind;
        private Button branchWidget;
        private Button settingsButton;
        private Button pushButton;

        // Built once: tabs, badges, the branch and the busy state then change in place.
        private void BuildShell()
        {
            rootVisualElement.Clear();
            rootVisualElement.AddToClassList("unitgit-root");
            foreach (string path in new[] { ShellStyleSheetPath, StyleSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null && !rootVisualElement.styleSheets.Contains(sheet))
                    rootVisualElement.styleSheets.Add(sheet);
            }

            rootVisualElement.Add(BuildTopBar());
            rootVisualElement.Add(BuildVisitBanner());
            contentRoot = new VisualElement();
            contentRoot.AddToClassList("unitgit-content");
            rootVisualElement.Add(contentRoot);
            toasts = new UnitGitToasts();
            rootVisualElement.Add(toasts);
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnShortcut, TrickleDown.TrickleDown);
            UpdateTopBar();
        }

        // JetBrains' Git shortcuts: Ctrl+K commit, Ctrl+Shift+K push, Ctrl+T update, Ctrl+Alt+A add, F5 refresh.
        private void OnShortcut(KeyDownEvent evt)
        {
            bool command = evt.ctrlKey || evt.commandKey;
            if (command && evt.altKey && evt.keyCode == KeyCode.A)
            {
                if (activeTab != UnitGitTab.LocalChanges) return;
                AddSelectedUnversioned();
            }
            else if (command && evt.keyCode == KeyCode.K && evt.shiftKey) PushCurrentBranch();
            else if (command && evt.keyCode == KeyCode.K)
            {
                if (activeTab != UnitGitTab.LocalChanges) SetActiveTab(UnitGitTab.LocalChanges);
                rootVisualElement.schedule.Execute(() => contentRoot?.Q<TextField>(className: "unitgit-local-commit-message")?.Focus()).StartingIn(20);
            }
            else if (command && evt.keyCode == KeyCode.T) PullFastForward();
            else if (evt.keyCode == KeyCode.F5) RefreshAll();
            else return;
            evt.StopPropagation();
        }

        private VisualElement BuildTopBar()
        {
            var bar = new VisualElement();
            bar.AddToClassList("ug-topbar");

            tabs = new UnitGitTabs();
            tabs.Add(UnitGitTab.LocalChanges.ToString(), "Changes", UnitGitIconKind.Changes, () => SetActiveTab(UnitGitTab.LocalChanges),
                changesBadge = UnitGitUi.Badge(0)).tooltip = "What changed since the last commit, and the commit (Ctrl+K)";
            conflictsTab = tabs.Add(UnitGitTab.Conflicts.ToString(), "Conflicts", UnitGitIconKind.Conflict, () => SetActiveTab(UnitGitTab.Conflicts),
                conflictsBadge = UnitGitUi.Badge(0, "danger"));
            conflictsTab.AddToClassList("ug-tab--conflicts");
            tabs.Add(UnitGitTab.Log.ToString(), "Log", UnitGitIconKind.Log, () => SetActiveTab(UnitGitTab.Log));
            tabs.Add(UnitGitTab.Shelf.ToString(), "Shelf", UnitGitIconKind.Shelve, () => SetActiveTab(UnitGitTab.Shelf), shelfBadge = UnitGitUi.Badge(0));
            tabs.Add(UnitGitTab.Backups.ToString(), "Backups", UnitGitIconKind.Backup, () => SetActiveTab(UnitGitTab.Backups));
            tabs.Add(UnitGitTab.Console.ToString(), "Console", UnitGitIconKind.Console, () => SetActiveTab(UnitGitTab.Console));
            bar.Add(tabs);

            bar.Add(UnitGitUi.Spacer());

            statusArea = new VisualElement();
            statusArea.AddToClassList("ug-status");
            statusArea.Add(UnitGitUi.Spinner());
            statusText = UnitGitUi.Text(string.Empty, "ug-status__text");
            statusArea.Add(statusText);
            bar.Add(statusArea);

            bar.Add(BuildStorageWidget());

            branchWidget = new Button { tooltip = "Branches: checkout, create, merge…" };
            branchWidget.AddToClassList("ug-branch-widget");
            branchWidget.Add(new UnitGitIconElement(UnitGitIconKind.Branch));
            branchName = UnitGitUi.Text(string.Empty, "ug-branch-widget__name");
            branchWidget.Add(branchName);
            branchAhead = UnitGitUi.Text(string.Empty, "ug-branch-widget__sync");
            branchAhead.AddToClassList("ug-branch-widget__sync--ahead");
            branchAhead.tooltip = "Commits to push";
            branchWidget.Add(branchAhead);
            branchBehind = UnitGitUi.Text(string.Empty, "ug-branch-widget__sync");
            branchBehind.AddToClassList("ug-branch-widget__sync--behind");
            branchBehind.tooltip = "Commits to pull";
            branchWidget.Add(branchBehind);
            var chevron = new UnitGitIconElement(UnitGitIconKind.ChevronExpanded);
            chevron.AddToClassList("ug-branch-widget__chevron");
            branchWidget.Add(chevron);
            UnitGitUi.Press(branchWidget, ShowBranchPopup);
            bar.Add(branchWidget);

            var actions = new VisualElement();
            actions.AddToClassList("ug-topbar__actions");
            actions.Add(UnitGitUi.Icon(UnitGitIconKind.Pull, "Update: pull the current branch, fast-forward only (Ctrl+T)", PullFastForward));
            pushButton = UnitGitUi.Icon(UnitGitIconKind.Push, "Push the current branch (Ctrl+Shift+K)", PushCurrentBranch);
            actions.Add(pushButton);
            actions.Add(UnitGitUi.Icon(UnitGitIconKind.Fetch, "Fetch from every remote", Fetch));
            actions.Add(UnitGitUi.Icon(UnitGitIconKind.Refresh, "Refresh (F5)", RefreshAll));
            actions.Add(UnitGitUi.Separator());
            settingsButton = UnitGitUi.Icon(UnitGitIconKind.Settings, "Settings", () => SetActiveTab(activeTab == UnitGitTab.Settings ? UnitGitTab.Log : UnitGitTab.Settings));
            actions.Add(settingsButton);
            bar.Add(actions);
            return bar;
        }

        // Badges, the branch, the busy state and the chosen tab, without rebuilding the bar.
        private void UpdateTopBar()
        {
            if (tabs == null) return;
            bool ready = snapshot != null && snapshot.HasRepository && snapshot.HasCommits && string.IsNullOrWhiteSpace(snapshot.LastError);
            int changes = ready ? snapshot.Changes.Count : 0;
            changesBadge.text = changes > 999 ? "999+" : changes.ToString();
            changesBadge.style.display = changes > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            int shelves = ready && snapshot.Shelves != null ? snapshot.Shelves.Count : 0;
            shelfBadge.text = shelves.ToString();
            shelfBadge.style.display = shelves > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            bool conflicts = ready && HasConflictWork();
            conflictsTab.style.display = conflicts || activeTab == UnitGitTab.Conflicts ? DisplayStyle.Flex : DisplayStyle.None;
            int conflicted = conflicts ? Conflicted().Count : 0;
            conflictsBadge.text = conflicted.ToString();
            conflictsBadge.style.display = conflicted > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            tabs.Select(activeTab == UnitGitTab.Settings ? null : activeTab.ToString());
            settingsButton.EnableInClassList("ug-icon-button--on", activeTab == UnitGitTab.Settings);

            branchWidget.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            storageWidget.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            bool visiting = ready && IsVisiting;
            branchWidget.EnableInClassList("ug-branch-widget--visiting", visiting);
            if (ready)
            {
                string branch = visiting ? "Visiting " + snapshot.DetachedCommit.ShortHash
                    : string.IsNullOrWhiteSpace(snapshot.CurrentBranch) ? "detached HEAD" : snapshot.CurrentBranch;
                branchName.text = branch;
                branchWidget.tooltip = (visiting ? branch + ": no branch is checked out" : branch) + (snapshot.Ahead > 0 || snapshot.Behind > 0
                    ? "  (" + snapshot.Ahead + " to push, " + snapshot.Behind + " to pull)" : string.Empty) + "\nClick for branches: checkout, create, merge…";
                branchAhead.text = snapshot.Ahead > 0 ? "↑" + snapshot.Ahead : string.Empty;
                branchAhead.style.display = snapshot.Ahead > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                branchBehind.text = snapshot.Behind > 0 ? "↓" + snapshot.Behind : string.Empty;
                branchBehind.style.display = snapshot.Behind > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            string status = busy ? (string.IsNullOrEmpty(busyLabel) ? "Working…" : busyLabel)
                : commitWhenIncluded != null ? (commitWhenIncluded.Value ? "Committing and pushing…" : "Committing…")
                : refreshingSnapshot && snapshot == null ? "Reading the repository…" : string.Empty;
            statusText.text = status;
            statusArea.EnableInClassList("ug-status--idle", string.IsNullOrEmpty(status));
            foreach (var button in rootVisualElement.Query<Button>(className: "ug-icon-button").ToList())
                if (button.parent != null && button.parent.ClassListContains("ug-topbar__actions") && button != settingsButton)
                    button.SetEnabled(ready && !busy && commitWhenIncluded == null);
            UpdateVisitBanner();
        }

        private void RefreshTopBar() => UpdateTopBar();

        private void SetActiveTab(UnitGitTab tab)
        {
            bool changed = activeTab != tab;
            activeTab = tab;
            UpdateTopBar();
            RebuildContent();
            if (changed && contentRoot != null) UnitGitUi.Enter(contentRoot, "unitgit-content--enter");
        }

        private void ShowToast(string text, bool error = false, string actionText = null, Action action = null) =>
            toasts?.Show(text, error, actionText, action);

        private void RebuildContent()
        {
            if (contentRoot == null)
            {
                return;
            }

            localChangesListRoot = null;
            localDiffPaneRoot = null;
            changesNoticesRoot = null;
            commitDetailsRoot = null;
            contentRoot.Clear();

            if (snapshot == null)
            {
                contentRoot.Add(BuildMessagePanel("Loading Git state", "Mixer Git is reading the current Unity project."));
                return;
            }

            if (!snapshot.GitAvailable)
            {
                contentRoot.Add(BuildMessagePanel("Git is not available", snapshot.LastError));
                return;
            }

            if (!snapshot.IsUnityProject)
            {
                contentRoot.Add(BuildMessagePanel("No Unity project found", snapshot.LastError));
                return;
            }

            if (!snapshot.HasRepository)
            {
                contentRoot.Add(BuildInitializerPanel());
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.LastError))
            {
                VisualElement panel = BuildMessagePanel("Git state unavailable", snapshot.LastError);
                var actions = new VisualElement();
                actions.AddToClassList("unitgit-message-actions");
                actions.Add(UnitGitUi.Pill("Try again", RefreshSnapshot, "primary", UnitGitIconKind.Refresh));
                panel.Add(actions);
                contentRoot.Add(panel);
                return;
            }

            if (!snapshot.HasCommits)
            {
                contentRoot.Add(BuildInitializerPanel());
                return;
            }

            switch (activeTab)
            {
                case UnitGitTab.LocalChanges:
                    contentRoot.Add(BuildLocalChangesBody());
                    break;
                case UnitGitTab.Shelf:
                    contentRoot.Add(BuildShelfBody());
                    break;
                case UnitGitTab.Console:
                    contentRoot.Add(BuildConsoleBody());
                    break;
                case UnitGitTab.Backups:
                    contentRoot.Add(BuildBackupsBody());
                    break;
                case UnitGitTab.Conflicts:
                    contentRoot.Add(BuildConflictsBody());
                    break;
                case UnitGitTab.Settings:
                    contentRoot.Add(BuildSettingsBody());
                    break;
                default:
                    contentRoot.Add(BuildLogBody());
                    break;
            }
        }

        private VisualElement BuildInitializerPanel()
        {
            bool hasPartialRepository = snapshot != null && snapshot.HasRepository && !snapshot.HasCommits;
            var panel = BuildMessagePanel(
                hasPartialRepository ? "Finish the first Git commit" : "Initialize Git for this Unity project",
                hasPartialRepository
                    ? "A Git repository exists at the Unity project root, but it has no commits yet. Mixer Git will ensure the VRChat Unity .gitignore, stage the current project state, and create the first commit."
                    : "Mixer Git will create a repository at the project root, ensure a VRChat Unity .gitignore, stage the current project state, and create an initial commit. Large Unity projects can take several minutes.");

            var rootPath = new Label(snapshot.ProjectRoot);
            rootPath.AddToClassList("unitgit-path-label");
            panel.Add(rootPath);

            var excludePackageToggle = new Toggle("Do not include the project-root Package folder in the first commit");
            excludePackageToggle.value = excludePackageFolderFromInitialCommit;
            excludePackageToggle.AddToClassList("unitgit-init-toggle");
            excludePackageToggle.RegisterValueChangedCallback(evt => excludePackageFolderFromInitialCommit = evt.newValue);
            panel.Add(excludePackageToggle);

            var excludePackageDetail = new Label("Adds /[Pp]ackage/ to .gitignore before staging. This does not ignore Unity's Packages folder.");
            excludePackageDetail.AddToClassList("unitgit-init-toggle-detail");
            panel.Add(excludePackageDetail);

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-message-actions");
            actions.Add(UnitGitUi.Pill(hasPartialRepository ? "Create first commit" : "Initialize Git", InitializeProjectGit, "primary", UnitGitIconKind.Commit));
            actions.Add(UnitGitUi.Pill("Refresh", RefreshSnapshot, null, UnitGitIconKind.Refresh));
            panel.Add(actions);

            return panel;
        }
    }
}
