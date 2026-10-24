using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private const string HistoryRewriteWarning = "This rewrites local Git history. Mixer Git will not push; force-push manually if you want the remote branch to match.";

        private void ShowCommitContextMenu(UnitGitCommit commit)
        {
            var menu = new GenericMenu();
            if (commit == null)
            {
                menu.AddDisabledItem(new GUIContent("No Commit"));
                menu.ShowAsContext();
                return;
            }

            List<UnitGitCommit> selectedCommits = GetSelectedCommitsInLogOrder();
            menu.AddItem(new GUIContent("New Branch from Here…"), false, () => PromptCreateBranchFrom(commit.FullHash));
            AddVisitItem(menu, commit);
            menu.AddItem(new GUIContent("Cherry-Pick"), false, () => CherryPickCommit(commit));
            menu.AddItem(new GUIContent("Revert Commit"), false, () => RevertCommit(commit));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Rename Commit…"), false, () => PromptRenameCommit(commit));
            menu.AddItem(new GUIContent("Reset Current Branch to Here…"), false, () => PromptResetCurrentBranch(commit));
            if (selectedCommits.Count >= 2)
            {
                menu.AddItem(new GUIContent("Squash Selected Commits…"), false, PromptSquashSelectedCommits);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Squash Selected Commits…"));
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Copy Hash"), false, () => { CopyText("copy hash", commit.FullHash); ShowToast("Copied " + commit.ShortHash); });
            menu.AddItem(new GUIContent("Copy Short Hash"), false, () => CopyText("copy short hash", commit.ShortHash));
            menu.AddItem(new GUIContent("Copy Subject"), false, () => CopyText("copy subject", commit.Subject));
            menu.ShowAsContext();
        }

        // The project as it was at this commit, the later commits kept; the checked-out commit has nothing to visit.
        private void AddVisitItem(GenericMenu menu, UnitGitCommit commit)
        {
            var item = new GUIContent("Visit This Commit", "Check out this commit to look around; every later commit stays on its branch.");
            if (UnitGitCommitGraph.IsHead(commit.Decorations)) menu.AddDisabledItem(item);
            else menu.AddItem(item, false, () => VisitCommit(commit));
        }

        // Cherry-pick and revert make a new commit on the current branch; a conflict opens the Conflicts tab.
        private void CherryPickCommit(UnitGitCommit commit)
        {
            if (RefuseWhileVisiting("a cherry-picked commit")) return;
            RunAction("cherry-pick " + commit.ShortHash, () => gitService.CherryPick(commit.FullHash),
                result => { if (result != null && !result.Success && HasConflictWork()) SetActiveTab(UnitGitTab.Conflicts); },
                "Cherry-picking " + commit.ShortHash + "…", "Cherry-picked ‘" + Shorten(commit.Subject, 50) + "’");
        }

        private void RevertCommit(UnitGitCommit commit)
        {
            if (RefuseWhileVisiting("a revert commit")) return;
            RunAction("revert " + commit.ShortHash, () => gitService.RevertCommit(commit.FullHash),
                result => { if (result != null && !result.Success && HasConflictWork()) SetActiveTab(UnitGitTab.Conflicts); },
                "Reverting " + commit.ShortHash + "…", "Reverted ‘" + Shorten(commit.Subject, 50) + "’");
        }

        private void ShowReleaseContextMenu(UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            var menu = new GenericMenu();
            string releaseId = GetReleaseId(commit, release);
            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                menu.AddItem(new GUIContent("Hide Release Row"), false, () => HideReleaseRow(commit, release));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Hide Release Row"));
            }

            if (commit != null)
            {
                menu.AddSeparator(string.Empty);
                AddVisitItem(menu, commit);
                menu.AddItem(new GUIContent("Rename Commit"), false, () => PromptRenameCommit(commit));
                menu.AddItem(new GUIContent("Reset Current Branch to Here..."), false, () => PromptResetCurrentBranch(commit));
                menu.AddItem(new GUIContent("Copy Hash"), false, () => CopyText("copy hash", commit.FullHash));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Visit This Commit"));
                menu.AddDisabledItem(new GUIContent("Rename Commit"));
                menu.AddDisabledItem(new GUIContent("Reset Current Branch to Here..."));
                menu.AddDisabledItem(new GUIContent("Copy Hash"));
            }

            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                menu.AddItem(new GUIContent("Copy Release ID"), false, () => CopyText("copy release id", releaseId));
            }

            menu.ShowAsContext();
        }

        private void PromptRenameCommit(UnitGitCommit commit)
        {
            if (commit == null || gitService == null)
            {
                return;
            }

            string root = gitService.ProjectRoot;
            UnitGitHeadState head = null;
            ReadForPrompt(() =>
                {
                    var service = new UnitGitService(root);
                    GitCommandResult state = service.ReadHeadState(out head);
                    return state.Success ? service.GetCommitMessage(commit.FullHash) : state;
                },
                result => OpenRenameCommitPrompt(commit, result, head));
        }

        private void OpenRenameCommitPrompt(UnitGitCommit commit, GitCommandResult messageResult, UnitGitHeadState head)
        {
            UnitGitCommitMessagePromptWindow.Open(
                "Rename Commit",
                commit.ShortHash + "  " + commit.AuthorName,
                messageResult.StandardOutput,
                "Rename",
                newMessage =>
                {
                    if (string.IsNullOrWhiteSpace(newMessage))
                    {
                        EditorUtility.DisplayDialog("Rename Commit", "Commit message is required.", "OK");
                        return;
                    }

                    ReadChangedFileCount(new[] { commit }, fileCount =>
                    {
                        if (!ConfirmGitOperation(
                                "Rename Commit",
                                "Rewrite",
                                "rewrite commit " + commit.ShortHash + "\nupdate HEAD",
                                "Rename commit " + commit.ShortHash + ".",
                                HistoryRewriteWarning,
                                fileCount))
                        {
                            return;
                        }

                        string hash = commit.FullHash;
                        string message = newMessage;
                        RunAction("rename commit " + commit.ShortHash, () => gitService.RenameCommit(hash, message, head));
                    });
                });
        }

        private void PromptResetCurrentBranch(UnitGitCommit commit)
        {
            if (commit == null || gitService == null)
            {
                return;
            }

            // The dialog is modeless: remember the branch and commit it names, and reset only while both still match.
            string root = gitService.ProjectRoot;
            UnitGitHeadState head = null;
            ReadForPrompt(() => new UnitGitService(root).ReadHeadState(out head), _ =>
                UnitGitResetBranchPromptWindow.Open(
                    head.BranchName,
                    root,
                    commit,
                    mode =>
                    {
                        string hash = commit.FullHash;
                        UnitGitResetMode resetMode = mode;
                        RunAction("reset branch to " + commit.ShortHash, () => gitService.ResetCurrentBranch(hash, resetMode, head));
                    }));
        }

        private void PromptSquashSelectedCommits()
        {
            if (gitService == null)
            {
                return;
            }

            List<UnitGitCommit> commits = GetSelectedCommitsInLogOrder();
            if (commits.Count < 2)
            {
                EditorUtility.DisplayDialog("Squash Commits", "Select at least two commits to squash.", "OK");
                return;
            }

            string root = gitService.ProjectRoot;
            UnitGitHeadState head = null;
            ReadForPrompt(() => new UnitGitService(root).ReadHeadState(out head), _ => OpenSquashPrompt(commits, head));
        }

        private void OpenSquashPrompt(List<UnitGitCommit> commits, UnitGitHeadState head)
        {
            string defaultMessage = BuildDefaultSquashMessage(commits);
            UnitGitCommitMessagePromptWindow.Open(
                "Squash Commits",
                commits.Count + " selected commits",
                defaultMessage,
                "Squash",
                newMessage =>
                {
                    if (string.IsNullOrWhiteSpace(newMessage))
                    {
                        EditorUtility.DisplayDialog("Squash Commits", "Commit message is required.", "OK");
                        return;
                    }

                    ReadChangedFileCount(commits, fileCount =>
                    {
                        if (!ConfirmGitOperation(
                                "Squash Commits",
                                "Rewrite",
                                "rewrite selected commits\nupdate HEAD",
                                "Squash " + commits.Count + " selected commits into one commit.",
                                HistoryRewriteWarning,
                                fileCount))
                        {
                            return;
                        }

                        string[] hashes = commits.Select(commit => commit.FullHash).ToArray();
                        string message = newMessage;
                        RunAction("squash " + commits.Count + " commits", () => gitService.SquashCommits(hashes, message, head));
                    });
                });
        }

        private void HideReleaseRow(UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            if (gitService == null)
            {
                return;
            }

            string releaseId = GetReleaseId(commit, release);
            if (string.IsNullOrWhiteSpace(releaseId))
            {
                return;
            }

            if (!ConfirmGitOperation(
                    "Hide Release Row",
                    "Hide",
                    "update " + UnitGitReleases.ReleasesFileName,
                    "Hide release row " + releaseId + ".",
                    "This updates Mixer Git release metadata only. It does not rewrite Git history.",
                    1))
            {
                return;
            }

            string projectRoot = gitService.ProjectRoot;
            RunAction("hide release " + releaseId, () => ConvertReleaseResult(UnitGitReleases.HideRelease(projectRoot, releaseId)));
        }

        private static GitCommandResult ConvertReleaseResult(UnitGitReleaseResult result)
        {
            if (result == null)
            {
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = "Release operation failed."
                };
            }

            return new GitCommandResult
            {
                ExitCode = result.Success ? 0 : 1,
                StandardOutput = result.Success ? result.Message : string.Empty,
                StandardError = result.Success ? string.Empty : result.Message
            };
        }

        private string BuildDefaultSquashMessage(IList<UnitGitCommit> commits)
        {
            var builder = new StringBuilder();
            builder.AppendLine("squash " + commits.Count + " commits");
            builder.AppendLine();
            builder.AppendLine("Squashed commits:");
            foreach (UnitGitCommit commit in commits.AsEnumerable().Reverse())
            {
                builder.AppendLine("- " + commit.ShortHash + " " + commit.Subject);
            }

            return builder.ToString().TrimEnd();
        }

        private void ReadChangedFileCount(IEnumerable<UnitGitCommit> commits, Action<int> complete)
        {
            string root = gitService.ProjectRoot;
            var selected = commits.ToArray();
            ReadForPrompt(() => new GitCommandResult
            {
                StandardOutput = GetChangedFileCountForCommits(new UnitGitService(root), selected).ToString(CultureInfo.InvariantCulture)
            }, result => complete(int.Parse(result.StandardOutput, CultureInfo.InvariantCulture)));
        }

        private static int GetChangedFileCountForCommits(UnitGitService service, IEnumerable<UnitGitCommit> commits)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (UnitGitCommit commit in commits)
            {
                if (commit == null || string.IsNullOrWhiteSpace(commit.FullHash))
                {
                    continue;
                }

                UnitGitCommitDetails details = service.GetCommitDetails(commit.FullHash);
                if (details == null || details.ChangedFiles == null)
                {
                    continue;
                }

                foreach (string file in details.ChangedFiles)
                {
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        files.Add(file.Trim());
                    }
                }
            }

            return files.Count;
        }

        private void CopyText(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EditorGUIUtility.systemCopyBuffer = text;
            AppendConsole(label, "copied");
        }

        private static string GetReleaseId(UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            if (release != null && !string.IsNullOrWhiteSpace(release.id))
            {
                return release.id.Trim();
            }

            return commit != null && !string.IsNullOrWhiteSpace(commit.ReleaseId)
                ? commit.ReleaseId.Trim()
                : string.Empty;
        }
    }
}
