using System.Collections.Generic;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The tabs' selection after a snapshot, on the tab state objects alone (no window): what
    // UnitGitWindow.ReconcileSnapshotSelection did when these were the window's fields.
    public sealed class UnitGitTabStateTests
    {
        private static UnitGitCommit Commit(string hash) => new UnitGitCommit { FullHash = hash };

        private static UnitGitSnapshot Snapshot(params string[] hashes)
        {
            var snapshot = new UnitGitSnapshot { HasRepository = true, HasCommits = hashes.Length > 0 };
            foreach (string hash in hashes) snapshot.Commits.Add(Commit(hash));
            return snapshot;
        }

        [Test]
        public void ChangesSelectionFollowsTheFilesThatAreStillChanged()
        {
            var state = new UnitGitChangesState();
            var snapshot = Snapshot("c1");
            snapshot.Changes.Add(new UnitGitStatusEntry { Path = "Assets/a.txt" });
            snapshot.Changes.Add(new UnitGitStatusEntry { Path = "Assets/b.txt" });
            state.selectedChangePath = "Assets/gone.txt";
            state.selectedChangePaths.Add("Assets/gone.txt");
            state.selectedChangePaths.Add("ASSETS/B.TXT");
            state.Reconcile(snapshot);
            Assert.That(state.selectedChangePath, Is.EqualTo("Assets/a.txt"), "A file no longer changed gives way to the first change.");
            Assert.That(state.selectedChangePaths, Is.EquivalentTo(new[] { "ASSETS/B.TXT" }), "Paths compare without case; gone ones drop.");

            state.selectedChangePaths.Clear();
            state.Reconcile(snapshot);
            Assert.That(state.selectedChangePaths, Is.EquivalentTo(new[] { "Assets/a.txt" }), "An empty selection takes the selected file.");

            state.Reconcile(Snapshot("c1"));
            Assert.That(state.selectedChangePath, Is.Empty, "Nothing changed: nothing selected.");
        }

        [Test]
        public void LogSelectionFollowsTheCommitsAndBranchesThatRemain()
        {
            var state = new UnitGitLogState();
            var snapshot = Snapshot("c3", "c2", "c1");
            var current = new UnitGitBranch { Name = "main", FullRef = "refs/heads/main", IsCurrent = true };
            snapshot.Branches.Add(new UnitGitBranch { Name = "other", FullRef = "refs/heads/other" });
            snapshot.Branches.Add(current);

            state.selectedBranch = new UnitGitBranch { FullRef = "refs/heads/deleted" };
            state.selectedCommit = Commit("gone");
            state.selectedCommitHashes.UnionWith(new[] { "gone", "c2" });
            state.selectionAnchorHash = "gone";
            state.selectedReleaseId = "r1";
            state.selectedDetails = new UnitGitCommitDetails { Commit = Commit("gone") };
            state.Reconcile(snapshot);
            Assert.That(state.selectedBranch, Is.SameAs(current), "A deleted branch gives way to the current one.");
            Assert.That(state.selectedCommit.FullHash, Is.EqualTo("c2"), "The remaining selected commit becomes the shown one.");
            Assert.That(state.selectedCommitHashes, Is.EquivalentTo(new[] { "c2" }));
            Assert.That(state.selectionAnchorHash, Is.Empty);
            Assert.That(state.selectedReleaseId, Is.Empty);
            Assert.That(state.selectedDetails, Is.Null, "Details of another commit are dropped.");

            state.selectedCommitHashes.Clear();
            state.selectedCommit = Commit("gone");
            state.Reconcile(snapshot);
            Assert.That(state.selectedCommit.FullHash, Is.EqualTo("c3"), "With nothing selected left, the newest commit.");
            Assert.That(state.selectedCommitHashes, Is.EquivalentTo(new[] { "c3" }));
            Assert.That(state.selectionAnchorHash, Is.EqualTo("c3"));

            var details = new UnitGitCommitDetails { Commit = Commit("c3") };
            state.selectedDetails = details;
            state.Reconcile(snapshot);
            Assert.That(state.selectedDetails, Is.SameAs(details), "Details of the shown commit stay.");

            state.Reconcile(new UnitGitSnapshot { HasRepository = false });
            Assert.That(state.selectedCommit, Is.Null);
            Assert.That(state.selectedCommitHashes, Is.Empty);
            Assert.That(state.selectedDetails, Is.Null);
        }
    }
}
