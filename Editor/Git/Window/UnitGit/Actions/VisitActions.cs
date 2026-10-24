using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // Visit This Commit: the project as it was at an older commit, every later commit kept on its branch and in the Log,
    // and one click back to the latest. Git calls it a detached HEAD; the way back (branch, shelf made before visiting)
    // is kept per project (UnitGitSettings.Visit) so it outlives domain reloads and restarts.
    internal sealed partial class UnitGitWindow
    {
        private VisualElement visitBanner;
        private Label visitTitle;
        private Label visitDetail;
        private Button visitNewBranchButton;
        private Button visitBackButton;
        // Shown before Git is done: the commit a visit checks out, the branch "Back" checks out.
        private UnitGitCommit pendingVisit;
        private string pendingReturnBranch;
        // When the last visit or return finished (UTC ticks): Git state read after it replaces the pending state.
        private long visitActionDone;
        // The visit ends through Back, which says itself what became of the shelf.
        private bool visitReturning;

        // What a visit or Back is checked against before anything is asked, read off the main thread.
        private sealed class VisitFacts
        {
            public UnitGitHeadState Head;
            public int Tracked;
            public int Untracked;
            public List<string> InTheWay = new List<string>(); // new files where the target has its own: Git will not check out over them
            public List<string> Ignored = new List<string>(); // ignored files the target has its own copy of: Git overwrites them
            public int LeftBehind; // commits on the visited HEAD that no branch has
        }

        // A rebase or a bisect also detaches HEAD: those belong to Git (the Conflicts tab, "git bisect"), not to a visit.
        private bool IsVisiting => snapshot != null && snapshot.IsDetached && snapshot.DetachedCommit != null && snapshot.DetachedBy.Length == 0;

        private string VisitedShortHash => snapshot?.DetachedCommit?.ShortHash ?? "a commit";

        // Between the top bar and the content: what is being visited, how far behind the branch it is, and the way back.
        private VisualElement BuildVisitBanner()
        {
            visitBanner = new VisualElement();
            visitBanner.AddToClassList("ug-visit");
            visitBanner.style.display = DisplayStyle.None;
            var icon = new UnitGitIconElement(UnitGitIconKind.Log);
            icon.AddToClassList("ug-visit__icon");
            visitBanner.Add(icon);
            var texts = new VisualElement();
            texts.AddToClassList("ug-visit__texts");
            visitTitle = UnitGitUi.Text(string.Empty, "ug-visit__title");
            texts.Add(visitTitle);
            visitDetail = UnitGitUi.Text(string.Empty, "ug-visit__detail");
            texts.Add(visitDetail);
            visitBanner.Add(texts);
            visitNewBranchButton = UnitGitUi.Pill("New Branch Here…", () => PromptCreateBranchFrom(snapshot?.DetachedCommit?.FullHash), "ghost",
                UnitGitIconKind.CreateBranch, "Make a branch at this commit and check it out: commits you make here then belong to it.");
            visitNewBranchButton.AddToClassList("ug-visit__button");
            visitBanner.Add(visitNewBranchButton);
            visitBackButton = UnitGitUi.Pill("Back", () => ReturnFromVisit(visitBackButton), "primary", UnitGitIconKind.Undo);
            visitBackButton.AddToClassList("ug-visit__button");
            visitBanner.Add(visitBackButton);
            return visitBanner;
        }

        private void UpdateVisitBanner()
        {
            if (visitBanner == null) return;
            bool ready = snapshot != null && snapshot.HasRepository && snapshot.HasCommits && string.IsNullOrWhiteSpace(snapshot.LastError);
            bool visiting = ready && IsVisiting;
            UnitGitCommit commit = pendingVisit ?? (visiting ? snapshot.DetachedCommit : null);
            bool show = pendingReturnBranch != null || commit != null;
            visitBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            bool idle = !busy && commitWhenIncluded == null && pendingVisit == null && pendingReturnBranch == null;
            visitBackButton.SetEnabled(idle);
            visitNewBranchButton.SetEnabled(idle);
            if (pendingReturnBranch != null)
            {
                visitTitle.text = "Going back to ‘" + pendingReturnBranch + "’…";
                visitDetail.text = string.Empty;
                visitBanner.tooltip = null;
                return;
            }

            visitTitle.text = "Visiting " + commit.ShortHash;
            string branch = pendingVisit != null ? string.Empty : snapshot.ReturnBranch;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(commit.Subject)) parts.Add(Shorten(commit.Subject, 80));
            if (!string.IsNullOrWhiteSpace(commit.RelativeDate)) parts.Add(commit.RelativeDate);
            if (pendingVisit != null) parts.Add("checking out…");
            else if (branch.Length > 0 && snapshot.NewerCommits > 0)
                parts.Add(snapshot.NewerCommits + " newer commit" + (snapshot.NewerCommits == 1 ? string.Empty : "s") + " on ‘" + branch + "’");
            visitDetail.text = string.Join("  ·  ", parts);

            UnitGitVisit visit = UnitGitSettings.Visit;
            bool shelved = visit != null && visit.branch == branch && HasShelf(visit.shelf);
            UnitGitUi.SetText(visitBackButton, branch.Length > 0 ? "Back to ‘" + Shorten(branch, 28) + "’" : "Back to a Branch…");
            visitBackButton.tooltip = branch.Length > 0
                ? "Check out ‘" + branch + "’ again, at its latest commit" + (shelved ? ", and bring back the changes shelved before visiting." : ".")
                : "Choose the branch to check out again.";
            visitBanner.tooltip = "You are looking at the project as it was at " + commit.ShortHash + " (" + commit.RelativeDate + "). No branch is checked out " +
                                  "(Git calls it a detached HEAD): every branch keeps its later commits, and the Log still shows them." +
                                  (visit != null && !string.IsNullOrWhiteSpace(visit.branchTip) && branch.Length > 0 && visit.branch == branch
                                      ? "\n‘" + branch + "’ was at " + visit.branchTip.Substring(0, Math.Min(7, visit.branchTip.Length)) + " when the visit started."
                                      : string.Empty);
        }

        private bool HasShelf(string message)
        {
            return !string.IsNullOrWhiteSpace(message) && snapshot?.Shelves != null &&
                   snapshot.Shelves.Any(line => line.EndsWith(": " + message.Trim(), StringComparison.Ordinal));
        }

        private bool CanStartVisitAction()
        {
            if (gitService == null || snapshot == null) return false;
            if (busy || commitWhenIncluded != null)
            {
                ShowToast("Wait for " + (busyLabel ?? "the current Git operation") + " to finish.", error: true);
                return false;
            }

            if (HasConflictWork())
            {
                ShowToast("Finish or cancel the current operation first.", error: true, "Conflicts", () => SetActiveTab(UnitGitTab.Conflicts));
                return false;
            }

            if (UnitGitConflicts.IsBisecting(gitService.ProjectRoot))
            {
                ShowToast("A bisect is under way: finish it first (git bisect reset).", error: true);
                return false;
            }

            // Leaving Play mode reloads scripts, which would stop Git midway through the files.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ShowToast("Leave Play mode first: Git rewrites the project's files.", error: true);
                return false;
            }

            return true;
        }

        // Unsaved scenes and assets would be lost, or written over the other version later: saved first (Cancel stops).
        private static bool SaveOpenWork()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            AssetDatabase.SaveAssets();
            return true;
        }

        private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Files(int count) => count + " file" + (count == 1 ? string.Empty : "s");

        // Shelving is the default (Enter) and Escape cancels: discarding is never one key press away.
        private static UnitGitVisitChangesChoice AskChangedWhileVisiting(string title, int tracked, int inTheWay, string visited, string target)
        {
            return UnitGitVisitChecks.ChangesChoice(EditorUtility.DisplayDialogComplex(title, UnitGitVisitChecks.ChangedWhileVisitingText(tracked, inTheWay, visited, target),
                UnitGitVisitChecks.ShelveButton, UnitGitVisitChecks.CancelButton, UnitGitVisitChecks.DiscardButton));
        }

        // Commits made on a visited commit belong to no branch: keeping them comes first (Enter), Escape cancels.
        private bool LeaveCommitsBehind(string title, int count, string leaving)
        {
            if (count <= 0) return true;
            int choice = EditorUtility.DisplayDialogComplex(title, UnitGitVisitChecks.CommitsLeftBehindText(count, leaving), "New Branch Here…", "Cancel", "Leave Them Behind");
            if (choice == 0) PromptCreateBranchFrom(snapshot?.DetachedCommit?.FullHash);
            return choice == 2;
        }

        private void VisitCommit(UnitGitCommit commit)
        {
            if (commit == null || string.IsNullOrWhiteSpace(commit.FullHash) || !CanStartVisitAction()) return;
            if (!SaveOpenWork()) return;
            string root = gitService.ProjectRoot;
            string hash = commit.FullHash;
            string visited = UnitGitSettings.Visit?.commit;
            // The running versions come from the Package Manager (main thread only), what the commit holds from Git.
            List<UnitGitToolPackage> tools = UnitGitVisitChecks.ToolPackages(root);
            var facts = new VisitFacts();
            ReadForPrompt(() =>
            {
                var service = new UnitGitService(root);
                GitCommandResult state = service.ReadHeadState(out facts.Head);
                if (!state.Success) return state;
                GitCommandResult changes = service.ReadChangeCount(out facts.Tracked, out facts.Untracked);
                if (!changes.Success) return changes;
                foreach (UnitGitToolPackage tool in tools)
                {
                    tool.AtHead = service.ReadFileAt("HEAD", tool.PackageJson);
                    tool.AtTarget = service.ReadFileAt(hash, tool.PackageJson);
                }

                GitCommandResult way = service.ReadFilesInTheWay(hash, out facts.InTheWay, out facts.Ignored);
                if (!way.Success) return way;
                if (!string.IsNullOrEmpty(facts.Head.BranchRef)) return changes;
                GitCommandResult behind = service.ReadCommitsOnNoBranch(visited, out facts.LeftBehind);
                return behind.Success ? changes : behind;
            }, _ => ConfirmVisit(commit, facts, tools));
        }

        private void ConfirmVisit(UnitGitCommit commit, VisitFacts facts, List<UnitGitToolPackage> tools)
        {
            UnitGitHeadState head = facts.Head;
            if (head == null || !CanStartVisitAction()) return;
            if (head.Head == commit.FullHash)
            {
                ShowToast(commit.ShortHash + " is already checked out.");
                return;
            }

            string title = "Visit " + commit.ShortHash;
            UnitGitVisit visit = UnitGitSettings.Visit;
            bool fromBranch = !string.IsNullOrEmpty(head.BranchRef);
            // The commit may hold another Mixer Git, or none: what that means, and the way back without it.
            string returnBranch = fromBranch ? head.BranchName : !string.IsNullOrWhiteSpace(visit?.branch) ? visit.branch : snapshot.ReturnBranch;
            bool shelving = fromBranch ? facts.Tracked + facts.Untracked > 0 : visit != null && HasShelf(visit.shelf);
            string tooling = UnitGitVisitChecks.ToolChangeText(tools, commit.ShortHash, returnBranch, shelving);
            if (tooling != null && !EditorUtility.DisplayDialog(title, tooling, "Visit Anyway", "Cancel")) return;
            // Git overwrites ignored files without asking, and never shelves them: only once the user knows which.
            bool overwriteIgnored = facts.Ignored.Count > 0;
            if (overwriteIgnored && !EditorUtility.DisplayDialog(title, UnitGitVisitChecks.IgnoredInTheWayText(facts.Ignored, commit.ShortHash), "Visit Anyway", "Cancel"))
                return;
            if (!fromBranch && !LeaveCommitsBehind(title, facts.LeftBehind, "visiting " + commit.ShortHash)) return;

            string shelf = null;
            bool discard = false;
            bool keepShelf = false;
            if (fromBranch)
            {
                // From a branch: its changes are set aside, and come back with it.
                int changes = facts.Tracked + facts.Untracked;
                if (changes > 0)
                {
                    if (!EditorUtility.DisplayDialog(title,
                            Files(changes) + " changed since your last commit on ‘" + head.BranchName + "’. Mixer Git shelves them before visiting " +
                            commit.ShortHash + " and brings them back when you go back to ‘" + head.BranchName + "’.",
                            "Shelve " + changes + (changes == 1 ? " Change" : " Changes") + " and Visit", "Cancel"))
                        return;
                    shelf = "Before visiting " + commit.ShortHash + " (" + Timestamp() + ")";
                    keepShelf = true;
                }

                visit = new UnitGitVisit
                {
                    branch = head.BranchName,
                    branchTip = head.Head,
                    shelf = shelf ?? string.Empty,
                    started = DateTime.UtcNow.Ticks
                };
            }
            else
            {
                // Already visiting: the way back stays the first visit's. What changed while visiting goes, or is shelved apart.
                if (facts.Tracked + facts.InTheWay.Count > 0)
                {
                    switch (AskChangedWhileVisiting(title, facts.Tracked, facts.InTheWay.Count, VisitedShortHash, commit.ShortHash))
                    {
                        case UnitGitVisitChangesChoice.Cancel: return;
                        case UnitGitVisitChangesChoice.Discard: discard = true; break;
                        default: shelf = "Changed while visiting " + VisitedShortHash + " (" + Timestamp() + ")"; break;
                    }
                }

                visit = visit ?? new UnitGitVisit { branch = snapshot.ReturnBranch, started = DateTime.UtcNow.Ticks };
            }

            visit.commit = commit.FullHash;
            // Kept before Git starts: a domain reload during the checkout still knows the way back.
            UnitGitSettings.Visit = visit;
            pendingVisit = commit;
            string hash = commit.FullHash;
            string shelfMessage = shelf;
            bool discardChanges = discard;
            bool shelved = false;
            RunAction("visit " + commit.ShortHash, () => gitService.VisitCommit(hash, shelfMessage, discardChanges, out shelved, overwriteIgnored), result =>
            {
                visitActionDone = DateTime.UtcNow.Ticks;
                if (shelved) ForgetShelfFiles();
                if (keepShelf && !shelved && result != null && result.Success)
                {
                    // Nothing needed shelving after all.
                    var current = UnitGitSettings.Visit;
                    if (current != null) { current.shelf = string.Empty; UnitGitSettings.Visit = current; }
                }
            }, "Visiting " + commit.ShortHash + "…", "Visiting " + commit.ShortHash + ": every later commit stays on its branch", holdEditor: true);
            UpdateTopBar();
        }

        private void ReturnFromVisit(VisualElement anchor)
        {
            if (!IsVisiting || !CanStartVisitAction()) return;
            string branch = snapshot.ReturnBranch;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                ReturnFromVisitTo(branch);
                return;
            }

            var menu = new GenericMenu();
            var locals = snapshot.Branches.Where(candidate => !candidate.IsRemote).ToList();
            if (locals.Count == 0) menu.AddDisabledItem(new GUIContent("No local branch"));
            foreach (UnitGitBranch local in locals)
            {
                string name = local.Name;
                menu.AddItem(new GUIContent(name.Replace("/", "∕")), false, () => ReturnFromVisitTo(name));
            }
            if (anchor != null) menu.DropDown(anchor.worldBound);
            else menu.ShowAsContext();
        }

        private void ReturnFromVisitTo(string branch)
        {
            if (!IsVisiting || !CanStartVisitAction() || !SaveOpenWork()) return;
            string root = gitService.ProjectRoot;
            string visited = UnitGitSettings.Visit?.commit;
            var facts = new VisitFacts();
            ReadForPrompt(() =>
            {
                var service = new UnitGitService(root);
                // A branch gone or a merge under way is said before anything is asked or shelved.
                GitCommandResult open = service.CanReturnTo(branch);
                if (!open.Success) return open;
                GitCommandResult changes = service.ReadChangeCount(out facts.Tracked, out facts.Untracked);
                if (!changes.Success) return changes;
                GitCommandResult way = service.ReadFilesInTheWay("refs/heads/" + branch, out facts.InTheWay, out facts.Ignored);
                if (!way.Success) return way;
                GitCommandResult behind = service.ReadCommitsOnNoBranch(visited, out facts.LeftBehind);
                return behind.Success ? changes : behind;
            }, _ => ConfirmReturn(branch, facts));
        }

        private void ConfirmReturn(string branch, VisitFacts facts)
        {
            if (!IsVisiting || !CanStartVisitAction()) return;
            string visited = VisitedShortHash;
            string title = "Back to ‘" + branch + "’";
            if (!LeaveCommitsBehind(title, facts.LeftBehind, "going back to ‘" + branch + "’")) return;
            bool discard = false;
            string changedShelf = null;
            if (facts.Tracked + facts.InTheWay.Count > 0)
            {
                switch (AskChangedWhileVisiting(title, facts.Tracked, facts.InTheWay.Count, visited, "‘" + branch + "’"))
                {
                    case UnitGitVisitChangesChoice.Cancel: return;
                    case UnitGitVisitChangesChoice.Discard: discard = true; break;
                    default: changedShelf = "Changed while visiting " + visited + " (" + Timestamp() + ")"; break;
                }
            }

            // The shelf made before visiting goes back onto its own branch only.
            UnitGitVisit visit = UnitGitSettings.Visit;
            string restore = visit != null && visit.branch == branch && HasShelf(visit.shelf) ? visit.shelf : string.Empty;
            visitReturning = restore.Length > 0;
            pendingReturnBranch = branch;
            bool discardChanges = discard;
            // The service shelves what changed while visiting only once the way back is known to be open.
            RunAction("back to " + branch, () => gitService.ReturnFromVisit(branch, discardChanges, restore, changedShelf), result =>
            {
                visitActionDone = DateTime.UtcNow.Ticks;
                ForgetShelfFiles();
                if (result != null && !result.Success && HasConflictWork()) SetActiveTab(UnitGitTab.Conflicts);
            }, "Going back to ‘" + branch + "’…", "Back on ‘" + branch + "’" + (restore.Length > 0 ? ", with your changes from before the visit" : string.Empty),
                holdEditor: true);
            UpdateTopBar();
        }

        /// <summary>After each refresh: the pending state gives way to Git's, and a visit that ended (Back, a checkout, a new branch) is forgotten.</summary>
        private void ReconcileVisit(long readStarted)
        {
            if (pendingVisit != null || pendingReturnBranch != null)
            {
                if (visitActionDone == 0 || readStarted < visitActionDone) return;
                pendingVisit = null;
                pendingReturnBranch = null;
                visitActionDone = 0;
            }

            if (snapshot == null || !snapshot.HasRepository || !string.IsNullOrWhiteSpace(snapshot.LastError)) return;
            if (snapshot.IsDetached)
            {
                visitReturning = false;
                return;
            }

            UnitGitVisit visit = UnitGitSettings.Visit;
            if (visit == null || readStarted < visit.started) return;
            UnitGitSettings.Visit = null;
            bool returning = visitReturning;
            visitReturning = false;
            if (!returning && HasShelf(visit.shelf))
                ShowToast("Your changes from before the visit are on the shelf ‘" + Shorten(visit.shelf, 48) + "’", false, "Shelf", () => SetActiveTab(UnitGitTab.Shelf));
        }

        /// <summary>
        /// Commits made on a visited commit belong to no branch and leave the history on the way back: refused, with the way
        /// to keep them (a branch here). A rebase's own detached HEAD is left to Git.
        /// </summary>
        private bool RefuseWhileVisiting(string what)
        {
            if (snapshot == null || !snapshot.IsDetached || UnitGitConflicts.Operation(gitService.ProjectRoot, out _) == UnitGitOperation.Rebase) return false;
            string hash = snapshot.DetachedCommit?.FullHash;
            ShowToast("You are visiting " + VisitedShortHash + ": " + what + " would belong to no branch. Make a branch here first.", error: true,
                "New Branch Here…", () => PromptCreateBranchFrom(hash));
            return true;
        }
    }
}
