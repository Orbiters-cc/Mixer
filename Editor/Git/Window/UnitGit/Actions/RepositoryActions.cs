using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private string GetInitializeCommandPreview()
        {
            return GitCommand("init") + "\n" +
                   GitCommand("add", "-A", "--", ".") + "\n" +
                   GitCommand("commit", "-m", "Initial commit");
        }

        private void InitializeProjectGit()
        {
            if (!ConfirmGitOperation(
                    "Initialize Project Git",
                    "Initialize",
                    GetInitializeCommandPreview(),
                    "Create a Git repository at the Unity project root, update .gitignore if needed, stage the current project state, and create the first commit.",
                    "This can take several minutes on large Unity projects.",
                    null))
            {
                return;
            }

            RunAction("init", () => initializer.Initialize(gitService, excludePackageFolderFromInitialCommit));
        }

        private void PromptCreateBranch()
        {
            string defaultName = "feature/new-branch";
            // The prompt is modeless: branch from the commit selected when it opened, not whatever is selected on submit.
            string startPoint = selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.FullHash)
                ? selectedCommit.FullHash
                : null;
            UnitGitBranchPromptWindow.Open(defaultName, branchName =>
                RunAction("create branch " + branchName, () => gitService.CreateBranch(branchName, startPoint)));
        }

        private void UpdateSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                AppendConsole("update branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            RunAction("update " + branch.Name, () => gitService.UpdateBranch(branch));
        }

        private void DeleteSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                AppendConsole("delete branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            string command = branch.IsRemote
                ? GitCommand("branch", "-dr", branch.Name)
                : GitCommand("branch", "-d", branch.Name);
            string warning = branch.IsRemote
                ? "This deletes only the local remote-tracking branch. It does not push a remote deletion."
                : "This removes the local branch ref. Git may refuse if the branch is not merged.";
            if (!ConfirmGitOperation(
                    "Delete Branch",
                    "Delete",
                    command,
                    "Delete branch " + branch.Name + ".",
                    warning,
                    0))
            {
                return;
            }

            RunAction("delete " + branch.Name, () => gitService.DeleteBranch(branch));
        }

        private void Fetch()
        {
            RunAction("fetch", () => gitService.Fetch(), working: "Fetching…", success: "Fetched every remote");
        }

        private void OpenRemoteSetup()
        {
            UnitGitGitHubSetupWindow.Open(
                GetDefaultRemoteRepositoryName(),
                RunRemoteProviderLogin,
                CreateRemoteRepository);
        }

        // Fast-forward only: Git refuses anything that would merge, so nothing is at risk and nothing is asked.
        private void PullFastForward()
        {
            string branch = snapshot?.CurrentBranch;
            RunAction("pull", () => gitService.PullFastForward(), working: "Pulling…",
                success: string.IsNullOrWhiteSpace(branch) ? "Up to date" : "‘" + branch + "’ is up to date");
        }

        private void PushCurrentBranch()
        {
            if (snapshot == null) return;
            if (snapshot.Remotes.Count == 0)
            {
                OpenRemoteSetup();
                return;
            }
            string branch = snapshot.CurrentBranch;
            RunAction("push", () => gitService.Push(), working: "Pushing…",
                success: snapshot.Ahead > 0 ? "Pushed " + snapshot.Ahead + " commit" + (snapshot.Ahead == 1 ? string.Empty : "s") + " to " + (string.IsNullOrWhiteSpace(branch) ? "the remote" : "‘" + branch + "’")
                    : "Pushed ‘" + branch + "’");
        }

        private void StageAll()
        {
            RunAction("stage all", () => gitService.StageAll(), working: "Including every change…");
        }

        private void UnstageAll()
        {
            RunAction("unstage all", () => gitService.UnstageAll(), working: "Excluding every change…");
        }

        // No confirmation: nothing is lost, and Undo-like fixes (amend, reset) stay one click away. Ctrl+Enter commits.
        private void CommitStaged(bool push = false)
        {
            string message = string.IsNullOrWhiteSpace(commitMessage) ? string.Empty : commitMessage.Trim();
            bool amend = commitAmend;
            int included = IncludedCount();
            if (RefuseWhileVisiting(amend ? "the amended commit" : "this commit")) return;
            if (message.Length == 0)
            {
                ShowToast("Write a commit message first.", error: true);
                contentRoot?.Q<TextField>(className: "unitgit-local-commit-message")?.Focus();
                return;
            }
            if (!amend && included == 0)
            {
                ShowToast("Tick the files to commit first.", error: true);
                return;
            }
            if (busy) { ShowToast("Wait for " + (busyLabel ?? "the current Git operation") + " to finish. Details are in Console.", error: true); return; }
            commitIssue = null;
            if (includeRunning || includeQueue.Count > 0)
            {
                // The boxes were just ticked: the commit follows as soon as Git has the files.
                commitWhenIncluded = push;
                pendingCommitDescription = "preparing files for your commit";
                EnsureEditorUpdatePump();
                UpdateCommitSummary();
                RefreshTopBar();
                return;
            }

            pendingCommitDescription = push ? "committing and pushing" : "committing";
            string subject = message.Split('\n')[0].Trim();
            string done = (amend ? "Amended \u2018" : "Committed \u2018") + (subject.Length > 60 ? subject.Substring(0, 57) + "\u2026" : subject) + "\u2019" +
                          (push ? " and pushed" : string.Empty);
            RunAction(
                amend ? "amend commit" : "commit",
                () =>
                {
                    var result = amend ? gitService.CommitAmend(message) : gitService.Commit(message);
                    return result.Success && push ? gitService.Push() : result;
                },
                result =>
                {
                    pendingCommitDescription = null;
                    commitIssue = result != null && result.Success ? null : "Commit did not finish successfully: " + UnitGitRedaction.Redact(result?.Message ?? "No result from Git.");
                    if (result != null && result.Success)
                    {
                        if (commitMessage.Trim() == message)
                            commitMessage = string.Empty;
                        commitMessageBeforeAmend = string.Empty;
                        commitAmend = false;
                        contentRoot?.Q<TextField>(className: "unitgit-local-commit-message")?.SetValueWithoutNotify(commitMessage);
                        var placeholder = contentRoot?.Q<Label>(className: "unitgit-local-commit-placeholder");
                        if (placeholder != null) placeholder.style.display = DisplayStyle.Flex;
                        if (amendCheck != null) amendCheck.State = UnitGitCheckState.Off;
                        UpdateCommitSummary();
                    }
                },
                push ? "Committing and pushing\u2026" : amend ? "Amending\u2026" : "Committing\u2026",
                done);
        }

    }
}
