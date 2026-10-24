using System.Linq;
using Orbiters.UnitGit;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        // The branch widget's popup: every branch with its actions, the same ones as the Log's branch list.
        private void ShowBranchPopup()
        {
            if (snapshot == null || branchWidget == null) return;
            var actions = new UnitGitBranchPopup.Actions
            {
                NewBranch = () => PromptCreateBranchFrom(null),
                NewBranchFrom = branch => PromptCreateBranchFrom(branch.Name),
                Checkout = branch => WithBranch(branch, CheckoutSelectedBranch),
                Merge = branch => WithBranch(branch, MergeSelectedBranch),
                Update = branch => WithBranch(branch, UpdateSelectedBranch),
                Delete = branch => WithBranch(branch, DeleteSelectedBranch),
            };
            UnityEditor.PopupWindow.Show(branchWidget.worldBound,
                new UnitGitBranchPopup(snapshot.Branches, snapshot.CurrentBranch, actions, GetGraphColorForKey));
        }

        private void WithBranch(UnitGitBranch branch, System.Action action)
        {
            selectedBranch = snapshot?.Branches.FirstOrDefault(b => b.FullRef == branch.FullRef) ?? branch;
            action();
        }

        // A new branch from a branch or, without one, from the commit selected in the Log (else the current commit).
        private void PromptCreateBranchFrom(string startPoint)
        {
            string from = startPoint ?? (selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.FullHash) ? selectedCommit.FullHash : null);
            UnitGitBranchPromptWindow.Open("feature/new-branch", branchName =>
                RunAction("create branch " + branchName, () => gitService.CreateBranch(branchName, from),
                    working: "Creating ‘" + branchName + "’…", success: "Created ‘" + branchName + "’"));
        }
    }
}
