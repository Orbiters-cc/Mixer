using System;
using Orbiters.UnitGit;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        // Like JetBrains' Shelve: named, every change (new files too) set aside, the working tree back to the last commit.
        private void ShelveAll()
        {
            if (snapshot == null || snapshot.Changes.Count == 0)
            {
                ShowToast("There is nothing to shelve.", error: true);
                return;
            }
            string suggestion = string.IsNullOrWhiteSpace(commitMessage) ? "Shelved " + DateTime.Now.ToString("MMM d, HH:mm") : commitMessage.Split('\n')[0].Trim();
            UnitGitCommitMessagePromptWindow.Open("Shelve Changes", snapshot.Changes.Count + " changed file" + (snapshot.Changes.Count == 1 ? string.Empty : "s") +
                " are set aside; the project goes back to the last commit.", suggestion, "Shelve", name =>
            {
                string shelfName = string.IsNullOrWhiteSpace(name) ? suggestion : name.Trim();
                RunAction("shelve", () => gitService.ShelveAll(shelfName), _ =>
                {
                    ForgetShelfFiles();
                    AssetDatabase.Refresh();
                }, "Shelving…", "Shelved ‘" + Shorten(shelfName, 50) + "’");
            });
        }

        // Actions run on a worker thread: pass the confirmed selection, never read the live field there.
        private void MergeSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null || branch.IsCurrent)
            {
                ShowToast(branch == null ? "Select a branch first." : "Choose a branch other than the current one.", error: true);
                return;
            }
            if (RefuseWhileVisiting("a merge commit")) return;

            string current = snapshot?.CurrentBranch;
            RunAction("merge " + branch.Name, () => UnitGitConflicts.MergeBranch(gitService, branch.Name), result =>
            {
                AssetDatabase.Refresh();
                if (UnitGitConflicts.Operation(gitService.ProjectRoot, out _) != UnitGitOperation.None) activeTab = UnitGitTab.Conflicts;
            }, "Merging ‘" + branch.Name + "’…", "Merged ‘" + branch.Name + "’ into ‘" + current + "’");
        }

        // Git refuses to check out over local changes it would lose: nothing to confirm.
        private void CheckoutSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                ShowToast("Select a branch first.", error: true);
                return;
            }

            RunAction("checkout " + branch.Name, () => gitService.Checkout(branch), _ => AssetDatabase.Refresh(),
                "Checking out ‘" + branch.Name + "’…", "Checked out ‘" + branch.Name + "’");
        }

        private void UnshelveSelectedShelf() => BringShelfBack(pop: true);

        private void ApplySelectedShelf() => BringShelfBack(pop: false);

        private void BringShelfBack(bool pop)
        {
            string shelf = selectedShelf;
            if (string.IsNullOrWhiteSpace(shelf))
            {
                ShowToast("Select a shelf first.", error: true);
                return;
            }

            RunAction((pop ? "unshelve " : "apply shelf ") + shelf, () => gitService.RunGit(30000, "stash", pop ? "pop" : "apply", shelf), result =>
            {
                ForgetShelfFiles();
                if (pop && result != null && result.Success) selectedShelf = string.Empty;
                AssetDatabase.Refresh();
                if (result != null && !result.Success && HasConflictWork()) SetActiveTab(UnitGitTab.Conflicts);
            }, pop ? "Unshelving…" : "Applying the shelf…", pop ? "Unshelved: the changes are back" : "Applied: the changes are back, the shelf stays");
        }

        private void DropSelectedShelf()
        {
            string shelf = selectedShelf;
            if (string.IsNullOrWhiteSpace(shelf))
            {
                ShowToast("Select a shelf first.", error: true);
                return;
            }

            if (!EditorUtility.DisplayDialog("Delete Shelf", "Delete this shelf and the changes it holds? This cannot be undone.", "Delete", "Cancel"))
            {
                return;
            }

            RunAction("stash drop " + shelf, () => gitService.RunGit(30000, "stash", "drop", shelf), _ => ForgetShelfFiles(), "Deleting the shelf…", "Shelf deleted");
            selectedShelf = string.Empty;
            selectedShelfFile = string.Empty;
        }

        // Stash names shift when one is added or removed: what was read about them is read again.
        private void ForgetShelfFiles()
        {
            shelfFiles.Clear();
            expandedShelves.Clear();
            selectedShelfFile = string.Empty;
        }
    }
}
