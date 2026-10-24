using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Visit This Commit: an older commit checked out on its own, the later commits kept, and the way back with the shelf.
    public sealed class UnitGitVisitTests
    {
        [Test]
        public void VisitKeepsTheLaterCommitsOnTheBranchAndInTheLog()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                string third = Commit(repo, "three\n", "third");

                AssertOk(repo.Git.VisitCommit(first, null, false, out bool shelved));
                Assert.That(shelved, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
                Assert.That(repo.Run("rev-parse", "main").Trim(), Is.EqualTo(third), "Visiting never moves the branch.");
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\n"));
                Assert.That(repo.Git.GetCommits(string.Empty).Select(commit => commit.FullHash), Is.SupersetOf(new[] { first, second, third }));

                UnitGitSnapshot snapshot = repo.Git.BuildSnapshot(string.Empty);
                Assert.That(snapshot.IsDetached, Is.True);
                Assert.That(snapshot.CurrentBranch, Is.Empty);
                Assert.That(snapshot.DetachedCommit, Is.Not.Null);
                Assert.That(snapshot.DetachedCommit.FullHash, Is.EqualTo(first));
                Assert.That(snapshot.DetachedCommit.Subject, Is.EqualTo("first"));
                Assert.That(snapshot.Branches.Select(branch => branch.Name), Is.EqualTo(new[] { "main" }), "No '(HEAD detached at …)' pseudo-branch.");
                Assert.That(snapshot.Branches.Any(branch => branch.IsCurrent), Is.False);
                Assert.That(snapshot.PreviousBranch, Is.EqualTo("main"));
                Assert.That(snapshot.ReturnBranch, Is.EqualTo("main"));
                Assert.That(snapshot.NewerCommits, Is.EqualTo(2));
                Assert.That(snapshot.Commits.Select(commit => commit.FullHash), Is.SupersetOf(new[] { first, second, third }));
                Assert.That(UnitGitCommitGraph.IsHead(snapshot.Commits.Single(commit => commit.FullHash == first).Decorations), Is.True);
                Assert.That(UnitGitCommitGraph.IsHead(snapshot.Commits.Single(commit => commit.FullHash == third).Decorations), Is.False);
            }
        }

        [Test]
        public void TheVisitsOwnBranchIsTheWayBackAfterAnotherDetachedCheckout()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                Commit(repo, "three\n", "third");

                AssertOk(repo.Git.VisitCommit(second, null, false, out _));
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                // Git's previous checkout is now a commit, not a branch: only the visit remembers main.
                UnitGitSnapshot fromGit = repo.Git.BuildSnapshot(string.Empty);
                Assert.That(fromGit.PreviousBranch, Is.Empty);
                Assert.That(fromGit.ReturnBranch, Is.Empty);
                Assert.That(fromGit.NewerCommits, Is.EqualTo(-1));

                UnitGitSnapshot visit = repo.Git.BuildSnapshot(string.Empty, 300, null, "main");
                Assert.That(visit.ReturnBranch, Is.EqualTo("main"));
                Assert.That(visit.NewerCommits, Is.EqualTo(2));
                Assert.That(repo.Git.BuildSnapshot(string.Empty, 300, null, "gone").ReturnBranch, Is.Empty, "A deleted branch is no way back.");
            }
        }

        [Test]
        public void ReturnFromVisitChecksOutTheBranchAtItsLatestCommit()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string latest = Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));

                AssertOk(repo.Git.ReturnFromVisit("main", false, null));
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(latest));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("two\n"));
                Assert.That(repo.Git.BuildSnapshot(string.Empty).IsDetached, Is.False);
            }
        }

        [Test]
        public void ChangesShelvedBeforeVisitingComeBackWithTheBranch()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                repo.Write("Assets/file.txt", "work in progress\n");
                repo.Write("Assets/new.txt", "new file\n");
                const string shelf = "Before visiting test (2026-10-20 12:00:00)";

                AssertOk(repo.Git.VisitCommit(first, shelf, false, out bool shelved));
                Assert.That(shelved, Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\n"), "The visited commit as it was committed.");
                Assert.That(File.Exists(repo.Full("Assets/new.txt")), Is.False, "New files are shelved too.");
                Assert.That(repo.Git.FindShelf(shelf), Is.EqualTo("stash@{0}"));

                AssertOk(repo.Git.ReturnFromVisit("main", false, shelf));
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("work in progress\n"));
                Assert.That(repo.Read("Assets/new.txt"), Is.EqualTo("new file\n"));
                Assert.That(repo.Git.FindShelf(shelf), Is.Empty);
                Assert.That(repo.Run("stash", "list").Trim(), Is.Empty);
            }
        }

        [Test]
        public void VisitRefusesEditedFilesWithoutAShelf()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                repo.Write("Assets/file.txt", "unsaved work\n");

                GitCommandResult result = repo.Git.VisitCommit(first, null, false, out bool shelved);
                Assert.That(result.Success, Is.False);
                Assert.That(shelved, Is.False);
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(second));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("unsaved work\n"));
            }
        }

        [Test]
        public void ReturnRefusesFilesChangedWhileVisitingUnlessTheyAreDiscarded()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/file.txt", "re-saved by Unity\n");
                repo.Write("Assets/created.txt", "made while visiting\n");

                GitCommandResult refused = repo.Git.ReturnFromVisit("main", false, null);
                Assert.That(refused.Success, Is.False);
                Assert.That(repo.Git.IsHeadDetached(), Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("re-saved by Unity\n"));

                AssertOk(repo.Git.ReturnFromVisit("main", true, null));
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("two\n"));
                Assert.That(repo.Read("Assets/created.txt"), Is.EqualTo("made while visiting\n"), "Nothing is cleaned: new files stay.");
            }
        }

        [Test]
        public void ReturnRefusesABranchThatDoesNotExist()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));

                Assert.That(repo.Git.ReturnFromVisit("gone", false, null).Success, Is.False);
                Assert.That(repo.Git.ReturnFromVisit("--force", false, null).Success, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
            }
        }

        [Test]
        public void CheckpointsAreRefusedWhileVisiting()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/other.txt", "would be lost\n");

                UnitGitReleaseResult result = UnitGitReleases.CommitAll("checkpoint", null, repo.Git, false);
                Assert.That(result.Success, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
            }
        }

        [Test]
        public void NewFilesWhereTheBranchHasItsOwnAreShelvedOnTheWayBackAndOthersStay()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                repo.Write("Assets/Folder.meta", "guid: branch\n");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                // Unity makes a .meta of its own for a folder the visited commit has none for.
                repo.Write("Assets/Folder.meta", "guid: made while visiting\n");
                repo.Write("Assets/other.txt", "unrelated\n");

                AssertOk(repo.Git.ReadFilesInTheWay("refs/heads/main", out var inTheWay, out var ignored));
                Assert.That(inTheWay, Is.EqualTo(new[] { "Assets/Folder.meta" }));
                Assert.That(ignored, Is.Empty);
                GitCommandResult refused = repo.Git.ReturnFromVisit("main", false, null);
                Assert.That(refused.Success, Is.False);
                Assert.That(refused.Message, Does.Contain("Assets/Folder.meta"));
                Assert.That(repo.Git.IsHeadDetached(), Is.True);

                const string shelf = "Changed while visiting test (2026-10-21 12:00:00)";
                AssertOk(repo.Git.ReturnFromVisit("main", false, null, shelf));
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
                Assert.That(repo.Read("Assets/Folder.meta"), Is.EqualTo("guid: branch\n"));
                Assert.That(repo.Read("Assets/other.txt"), Is.EqualTo("unrelated\n"), "Only what stands in the way is shelved.");
                Assert.That(repo.Git.FindShelf(shelf), Is.Not.Empty);
            }
        }

        [Test]
        public void DiscardGivesTheBranchItsOwnFilesBack()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                repo.Write("Assets/Folder.meta", "guid: branch\n");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/Folder.meta", "guid: made while visiting\n");

                AssertOk(repo.Git.ReturnFromVisit("main", true, null));
                Assert.That(repo.Read("Assets/Folder.meta"), Is.EqualTo("guid: branch\n"));
                Assert.That(repo.Run("stash", "list").Trim(), Is.Empty);
            }
        }

        [Test]
        public void VisitRefusesToOverwriteIgnoredFilesUnlessAsked()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                // Committed once, then ignored and untracked: the old commit still has its own copies.
                repo.Write("Assets/local.cfg", "committed\n");
                repo.Write("Library/old.asset", "committed\n");
                string first = Commit(repo, "one\n", "first");
                repo.Write(".gitignore", "Assets/local.cfg\nLibrary/\n");
                repo.Run("rm", "-q", "--cached", "--", "Assets/local.cfg", "Library/old.asset");
                Commit(repo, "two\n", "second");
                repo.Write("Assets/local.cfg", "local edit\n");
                repo.Write("Library/other.asset", "made by Unity\n");

                AssertOk(repo.Git.ReadFilesInTheWay(first, out var inTheWay, out var ignored));
                Assert.That(ignored, Is.EqualTo(new[] { "Assets/local.cfg", "Library/old.asset" }), "Files inside a folder ignored as a whole count too.");
                Assert.That(inTheWay, Is.Empty);

                GitCommandResult refused = repo.Git.VisitCommit(first, null, false, out _);
                Assert.That(refused.Success, Is.False);
                Assert.That(refused.Message, Does.Contain("Assets/local.cfg"));
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
                Assert.That(repo.Read("Assets/local.cfg"), Is.EqualTo("local edit\n"));

                AssertOk(repo.Git.VisitCommit(first, null, false, out _, true));
                Assert.That(repo.Read("Assets/local.cfg"), Is.EqualTo("committed\n"), "Overwritten once the user said so.");
            }
        }

        [Test]
        public void CommitsMadeOnTheVisitedCommitAreCountedUntilABranchHasThem()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                AssertOk(repo.Git.ReadCommitsOnNoBranch(first, out int none));
                Assert.That(none, Is.Zero);

                Commit(repo, "made while visiting\n", "on no branch");
                repo.Write("Assets/file.txt", "shelved here\n");
                repo.Run("stash", "push", "-q", "-m", "a shelf here");
                AssertOk(repo.Git.ReadCommitsOnNoBranch(first, out int left));
                Assert.That(left, Is.EqualTo(1), "A shelf made on it does not keep the commit.");

                repo.Run("branch", "kept");
                AssertOk(repo.Git.ReadCommitsOnNoBranch(first, out int kept));
                Assert.That(kept, Is.Zero);
            }
        }

        [Test]
        public void ReturnShelvesNothingWhenTheWayBackIsClosed()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/file.txt", "edited while visiting\n");

                Assert.That(repo.Git.ReturnFromVisit("gone", false, null, "Changed while visiting test").Success, Is.False);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("edited while visiting\n"));
                Assert.That(repo.Run("stash", "list").Trim(), Is.Empty);
            }
        }

        [Test]
        public void AFailedCheckoutNeverPutsTheShelfOntoHalfWrittenFiles()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/z.bin", "bin one\n");
                string first = Commit(repo, "one\n", "first");
                repo.Write("Assets/z.bin", "bin two\n");
                Commit(repo, "two\n", "second");
                // A smudge filter that fails, as Git LFS does without its objects: Git stops with part of the commit written.
                repo.Write(".git/info/attributes", "*.bin filter=broken\n");
                repo.Run("config", "filter.broken.smudge", "false");
                repo.Run("config", "filter.broken.clean", "cat");
                repo.Run("config", "filter.broken.required", "true");
                repo.Write("Assets/file.txt", "work in progress\n");
                const string shelf = "Before visiting test (2026-10-21 12:00:00)";

                GitCommandResult result = repo.Git.VisitCommit(first, shelf, false, out bool shelved);
                Assert.That(result.Success, Is.False);
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
                Assert.That(shelved, Is.True, "The changes stay on the shelf.");
                Assert.That(repo.Git.FindShelf(shelf), Is.Not.Empty);
                Assert.That(result.Message, Does.Contain(shelf));
            }
        }

        [Test]
        public void SubmoduleCommitsAreNoChangeToShelveOrDiscard()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                const string sub = "Packages/sub";
                Directory.CreateDirectory(repo.Full(sub));
                repo.Run("-C", sub, "init", "-q", "-b", "main");
                repo.Run("-C", sub, "config", "user.email", "unitgit@example.test");
                repo.Run("-C", sub, "config", "user.name", "Mixer Git Tests");
                repo.Run("-C", sub, "config", "commit.gpgsign", "false");
                repo.Run("-C", sub, "config", "core.hooksPath", repo.Hooks);
                repo.Write(sub + "/a.txt", "one\n");
                repo.Run("-C", sub, "add", "-A");
                repo.Run("-C", sub, "commit", "-q", "-m", "sub one");
                Commit(repo, "one\n", "first");
                repo.Write(sub + "/a.txt", "two\n");
                repo.Run("-C", sub, "commit", "-q", "-a", "-m", "sub two");

                Assert.That(repo.Run("status", "--porcelain=v1"), Does.Contain(sub), "Git itself reports the submodule as changed.");
                AssertOk(repo.Git.ReadChangeCount(out int tracked, out int untracked));
                Assert.That(tracked + untracked, Is.Zero);
            }
        }

        [Test]
        public void DefaultRulesWaitUntilTheVisitEnds()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                Directory.CreateDirectory(repo.Full("Assets/MCB"));

                UnitGitProjectInitializer.EnsureDefaultRules(repo.Root);
                Assert.That(File.Exists(repo.Full(".gitignore")), Is.False, "The visited commit's files are left as they were.");

                AssertOk(repo.Git.ReturnFromVisit("main", false, null));
                UnitGitProjectInitializer.EnsureDefaultRules(repo.Root);
                Assert.That(repo.Read(".gitignore"), Does.Contain(UnitGitProjectInitializer.McbDownloadPatterns[2]));
            }
        }

        [Test]
        public void ABisectIsNoVisit()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                string third = Commit(repo, "three\n", "third");
                repo.Run("bisect", "start", third, first);

                UnitGitSnapshot snapshot = repo.Git.BuildSnapshot(string.Empty);
                Assert.That(snapshot.IsDetached, Is.True);
                Assert.That(snapshot.DetachedBy, Is.EqualTo("Bisecting"));
                Assert.That(repo.Git.ReturnFromVisit("main", false, null).Success, Is.False, "Back would leave the bisect half done.");
                Assert.That(repo.Git.VisitCommit(first, null, false, out _).Success, Is.False);
                repo.Run("bisect", "reset");
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
            }
        }

        [Test]
        public void ReadFileAtReadsAPackageAsCommitted()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                const string manifest = "Packages/orbiters.mixer/package.json";
                repo.Write(manifest, "{ \"name\": \"orbiters.mixer\", \"version\": \"0.2.4\" }\n");
                string first = Commit(repo, "one\n", "first");
                repo.Write(manifest, "{ \"name\": \"orbiters.mixer\", \"version\": \"0.2.10\" }\n");
                Commit(repo, "two\n", "second");

                Assert.That(UnitGitVisitChecks.PackageVersion(repo.Git.ReadFileAt(first, manifest)), Is.EqualTo("0.2.4"));
                Assert.That(UnitGitVisitChecks.PackageVersion(repo.Git.ReadFileAt("HEAD", manifest)), Is.EqualTo("0.2.10"));
                Assert.That(repo.Git.ReadFileAt(first, "Packages/orbiters.toolkit/package.json"), Is.Null);
            }
        }

        private static string Commit(UnitGitTestRepo repo, string content, string message)
        {
            repo.Write("Assets/file.txt", content);
            return repo.Commit(message);
        }

        private static void AssertOk(GitCommandResult result)
        {
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
