using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Staging, rollback, push, cherry-pick and revert, commit diffs and log filters, each in a throwaway repository.
    public sealed class UnitGitOperationsTests
    {
        [Test]
        public void StageAndUnstageIncludeDeletionsAndKeepFilesOnDisk()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/kept.txt", "one\n");
                repo.Write("Assets/gone.txt", "gone\n");
                repo.Commit("base");
                repo.Write("Assets/kept.txt", "two\n");
                File.Delete(repo.Full("Assets/gone.txt"));
                repo.Write("Assets/new file.txt", "new\n");
                var paths = new[] { "Assets/kept.txt", "Assets/gone.txt", "Assets/new file.txt" };

                AssertOk(repo.Git.Stage(paths));
                var status = StatusOf(repo);
                Assert.That(status["Assets/kept.txt"], Is.EqualTo("M "));
                Assert.That(status["Assets/gone.txt"], Is.EqualTo("D "));
                Assert.That(status["Assets/new file.txt"], Is.EqualTo("A "));

                AssertOk(repo.Git.Unstage(paths));
                status = StatusOf(repo);
                Assert.That(status["Assets/kept.txt"], Is.EqualTo(" M"));
                Assert.That(status["Assets/gone.txt"], Is.EqualTo(" D"));
                Assert.That(status["Assets/new file.txt"], Is.EqualTo("??"));
                Assert.That(repo.Read("Assets/kept.txt"), Is.EqualTo("two\n"));
                Assert.That(repo.Read("Assets/new file.txt"), Is.EqualTo("new\n"));

                Assert.That(repo.Git.Stage(new string[0]).Success, Is.False, "No paths must never mean the whole tree.");
                Assert.That(StatusOf(repo)["Assets/kept.txt"], Is.EqualTo(" M"));
            }
        }

        [Test]
        public void UnstageBeforeTheFirstCommitKeepsTheFileOnDisk()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "staged\n");
                AssertOk(repo.Git.Stage(new[] { "Assets/a.txt" }));
                repo.Write("Assets/a.txt", "edited after staging\n");

                AssertOk(repo.Git.Unstage(new[] { "Assets/a.txt" }));
                Assert.That(StatusOf(repo)["Assets/a.txt"], Is.EqualTo("??"));
                Assert.That(repo.Read("Assets/a.txt"), Is.EqualTo("edited after staging\n"));
            }
        }

        [Test]
        public void StageSplitsLongPathListsIntoSeveralCommands()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                var paths = new List<string>();
                for (int i = 0; i < 450; i++)
                {
                    string path = "Assets/Many files/file " + i.ToString("000") + ".txt";
                    repo.Write(path, i + "\n");
                    paths.Add(path);
                }

                AssertOk(repo.Git.Stage(paths));
                Assert.That(Lines(repo.Run("ls-files", "--", "Assets/Many files")).Count, Is.EqualTo(450));
            }
        }

        [Test]
        public void RollbackRestoresTrackedFilesAndNeverTouchesUntrackedOnes()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/changed.txt", "base\n");
                repo.Write("Assets/deleted.txt", "keep me\n");
                repo.Commit("base");
                repo.Write("Assets/changed.txt", "staged\n");
                repo.Run("add", "Assets/changed.txt");
                repo.Write("Assets/changed.txt", "worktree\n");
                File.Delete(repo.Full("Assets/deleted.txt"));
                repo.Write("Assets/added.txt", "staged new\n");
                repo.Run("add", "Assets/added.txt");
                repo.Write("Assets/added.txt", "edited new\n");
                repo.Write("Assets/untracked.txt", "untracked\n");

                GitCommandResult result = repo.Git.Rollback(new[] { "Assets/changed.txt", "Assets/deleted.txt", "Assets/added.txt", "Assets/untracked.txt" });
                AssertOk(result);
                Assert.That(result.Message, Does.Contain("Rolled back 2 files"));
                Assert.That(result.Message, Does.Contain("1 added file"));
                Assert.That(repo.Read("Assets/changed.txt"), Is.EqualTo("base\n"));
                Assert.That(repo.Read("Assets/deleted.txt"), Is.EqualTo("keep me\n"));
                Assert.That(repo.Read("Assets/added.txt"), Is.EqualTo("edited new\n"), "An added file only leaves the index.");
                Assert.That(repo.Read("Assets/untracked.txt"), Is.EqualTo("untracked\n"));
                var status = StatusOf(repo);
                Assert.That(status.ContainsKey("Assets/changed.txt"), Is.False);
                Assert.That(status.ContainsKey("Assets/deleted.txt"), Is.False);
                Assert.That(status["Assets/added.txt"], Is.EqualTo("??"));
                Assert.That(status["Assets/untracked.txt"], Is.EqualTo("??"));

                GitCommandResult untrackedOnly = repo.Git.Rollback(new[] { "Assets/untracked.txt" });
                AssertOk(untrackedOnly);
                Assert.That(untrackedOnly.Message, Does.Contain("Nothing to roll back"));
                Assert.That(repo.Read("Assets/untracked.txt"), Is.EqualTo("untracked\n"));
            }
        }

        [Test]
        public void PushPublishesTheBranchOnceThenPushesToItsUpstream()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\n");
                repo.Commit("one");
                GitCommandResult noRemote = repo.Git.Push();
                Assert.That(noRemote.Success, Is.False);
                Assert.That(noRemote.Message, Is.EqualTo("This repository has no remote. Set one up first."));

                // Inside .git, so the repository's own cleanup removes it.
                string remote = Path.Combine(repo.Root, ".git", "unitgit-test-remote.git");
                repo.Run("init", "--bare", "-q", remote);
                repo.Run("remote", "add", "origin", remote);
                Assert.That(repo.Git.BuildSnapshot(string.Empty).Remotes, Is.EqualTo(new[] { "origin" }));

                AssertOk(repo.Git.Push());
                Assert.That(repo.Run("rev-parse", "--abbrev-ref", "main@{upstream}").Trim(), Is.EqualTo("origin/main"));
                repo.Write("Assets/a.txt", "two\n");
                string second = repo.Commit("two");
                AssertOk(repo.Git.Push());
                Assert.That(repo.Run("ls-remote", "origin", "refs/heads/main").Split('\t')[0], Is.EqualTo(second));
            }
        }

        [Test]
        public void CherryPickAndRevertApplyCommitsAndStopOnConflicts()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/notes.txt", "one\ntwo\nthree\n");
                repo.Commit("base");
                repo.Run("checkout", "-q", "-b", "feature");
                repo.Write("Assets/feature.txt", "feature\n");
                string feature = repo.Commit("add feature file");
                repo.Write("Assets/notes.txt", "one\nfeature\nthree\n");
                string featureNotes = repo.Commit("feature notes");
                repo.Run("checkout", "-q", "main");
                repo.Write("Assets/main.txt", "main\n");
                string mainWork = repo.Commit("main work");

                AssertOk(repo.Git.CherryPick(feature));
                Assert.That(repo.Read("Assets/feature.txt"), Is.EqualTo("feature\n"));
                Assert.That(repo.Run("log", "-1", "--format=%s%x1f%P").Trim(), Is.EqualTo("add feature file\x1f" + mainWork));
                string picked = repo.Run("rev-parse", "HEAD").Trim();

                GitCommandResult empty = repo.Git.CherryPick(feature);
                Assert.That(empty.Success, Is.False);
                Assert.That(empty.Message, Does.Contain("already on this branch"));
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.None), "An empty copy is not left in progress.");
                Assert.That(repo.Git.CherryPick("-n").Success, Is.False);

                AssertOk(repo.Git.RevertCommit(picked));
                Assert.That(File.Exists(repo.Full("Assets/feature.txt")), Is.False);
                Assert.That(repo.Run("log", "-1", "--format=%s").Trim(), Does.StartWith("Revert"));

                repo.Write("Assets/notes.txt", "one\nmain\nthree\n");
                string mainNotes = repo.Commit("main notes");
                GitCommandResult conflict = repo.Git.CherryPick(featureNotes);
                Assert.That(conflict.Success, Is.False);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.CherryPick));
                Assert.That(repo.Git.RevertCommit(mainNotes).Message, Does.Contain("Finish or cancel"));
                AssertOk(UnitGitConflicts.Abort(repo.Git, UnitGitOperation.CherryPick));

                repo.Write("Assets/notes.txt", "one\nmain again\nthree\n");
                repo.Commit("main notes again");
                Assert.That(repo.Git.RevertCommit(mainNotes).Success, Is.False);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.Revert));
                AssertOk(UnitGitConflicts.Abort(repo.Git, UnitGitOperation.Revert));
            }
        }

        [Test]
        public void CommitFileDiffComparesWithTheFirstParent()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\ntwo\n");
                repo.Write("Assets/gone.txt", "bye\n");
                repo.Write("Assets/old name.txt", "same line 1\nsame line 2\nsame line 3\n");
                string root = repo.Commit("root");
                repo.Write("Assets/a.txt", "one\nTWO\n");
                repo.Write("Assets/added.txt", "fresh\n");
                File.Delete(repo.Full("Assets/gone.txt"));
                repo.Run("mv", "Assets/old name.txt", "Assets/new name.txt");
                File.WriteAllBytes(repo.Full("Assets/image.bin"), new byte[] { 0, 1, 2, 0, 3 });
                string second = repo.Commit("second");

                UnitGitDiff rootDiff = repo.Git.GetCommitFileDiff(root, "Assets/a.txt");
                Assert.That(rootDiff.LeftTitle, Is.EqualTo("Before"));
                Assert.That(rootDiff.RightTitle, Is.EqualTo(Short(repo, root)));
                Assert.That(rootDiff.Lines.Where(line => line.Kind != UnitGitDiffLineKind.Hunk).Select(line => line.Kind), Is.All.EqualTo(UnitGitDiffLineKind.Added));
                Assert.That(rootDiff.Lines.Where(line => line.Kind == UnitGitDiffLineKind.Added).Select(line => line.Right), Is.EqualTo(new[] { "one", "two" }));

                UnitGitDiff modified = repo.Git.GetCommitFileDiff(second, "Assets/a.txt");
                Assert.That(modified.LeftTitle, Is.EqualTo(Short(repo, root)));
                Assert.That(modified.RightTitle, Is.EqualTo(Short(repo, second)));
                Assert.That(modified.DifferenceCount, Is.EqualTo(1));
                UnitGitDiffLine changed = modified.Lines.Single(line => line.Kind == UnitGitDiffLineKind.Changed);
                Assert.That(changed.Left, Is.EqualTo("two"));
                Assert.That(changed.Right, Is.EqualTo("TWO"));

                UnitGitDiff added = repo.Git.GetCommitFileDiff(second, "Assets/added.txt");
                Assert.That(added.Lines.Where(line => line.Kind != UnitGitDiffLineKind.Hunk).Select(line => line.Right), Is.EqualTo(new[] { "fresh" }));
                Assert.That(added.Lines.Where(line => line.Kind != UnitGitDiffLineKind.Hunk).Select(line => line.Kind), Is.All.EqualTo(UnitGitDiffLineKind.Added));
                Assert.That(added.Lines.Any(line => line.Right.Contains("file mode") || line.Right.Contains("/dev/null")), Is.False, "Git's header lines are not content.");

                UnitGitDiff deleted = repo.Git.GetCommitFileDiff(second, "Assets/gone.txt");
                Assert.That(deleted.Lines.Where(line => line.Kind != UnitGitDiffLineKind.Hunk).Select(line => line.Left), Is.EqualTo(new[] { "bye" }));
                Assert.That(deleted.Lines.Where(line => line.Kind != UnitGitDiffLineKind.Hunk).Select(line => line.Kind), Is.All.EqualTo(UnitGitDiffLineKind.Removed));

                UnitGitDiff renamed = repo.Git.GetCommitFileDiff(second, "Assets/new name.txt");
                Assert.That(renamed.Lines.Single().Right, Is.EqualTo("Renamed from Assets/old name.txt without changes."));

                UnitGitDiff binary = repo.Git.GetCommitFileDiff(second, "Assets/image.bin");
                Assert.That(binary.Lines.Single().Right, Does.Contain("Binary file added"));

                Assert.That(repo.Git.GetCommitFileDiff(second, string.Empty).Lines.Single().Right, Does.Contain("Select a changed file"));
            }
        }

        [Test]
        public void CommitsCarryParentsAndTimestampAndDetailsCarryBodyAndFileStatus()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Write("Assets/move me.txt", "rename source line 1\nline 2\nline 3\n");
                repo.Write("Assets/gone.txt", "bye\n");
                repo.Write("Assets/café.txt", "unicode\n");
                string root = CommitAt(repo, "root", "Mixer Git Tests", "2021-02-03T04:05:06+0000");
                repo.Run("checkout", "-q", "-b", "side");
                repo.Write("Assets/side.txt", "side\n");
                string side = repo.Commit("side");
                repo.Run("checkout", "-q", "main");
                repo.Write("Assets/a.txt", "a changed\n");
                repo.Write("Assets/new.txt", "new\n");
                File.Delete(repo.Full("Assets/gone.txt"));
                repo.Run("mv", "Assets/move me.txt", "Assets/moved.txt");
                repo.Run("add", "-A");
                AssertOk(repo.Git.Commit("change files\n\nFirst body line.\nSecond body line.\n"));
                string change = repo.Run("rev-parse", "HEAD").Trim();
                repo.Run("merge", "-q", "--no-ff", "--no-edit", "side");
                string merge = repo.Run("rev-parse", "HEAD").Trim();

                List<UnitGitCommit> commits = repo.Git.GetCommits(string.Empty, 50);
                Assert.That(commits.Single(commit => commit.FullHash == merge).Parents, Is.EqualTo(new[] { change, side }));
                Assert.That(commits.Single(commit => commit.FullHash == change).Parents, Is.EqualTo(new[] { root }));
                Assert.That(commits.Single(commit => commit.FullHash == root).Parents, Is.Empty);
                Assert.That(commits.Single(commit => commit.FullHash == root).Timestamp,
                    Is.EqualTo(new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero).ToUnixTimeSeconds()));

                UnitGitCommitDetails details = repo.Git.GetCommitDetails(change);
                Assert.That(details.Commit.Subject, Is.EqualTo("change files"));
                Assert.That(details.Body, Is.EqualTo("First body line.\nSecond body line."));
                Assert.That(details.Commit.Parents, Is.EqualTo(new[] { root }));
                Assert.That(details.Commit.Timestamp, Is.GreaterThan(0));
                Assert.That(details.FileStatus["Assets/a.txt"], Is.EqualTo('M'));
                Assert.That(details.FileStatus["Assets/new.txt"], Is.EqualTo('A'));
                Assert.That(details.FileStatus["Assets/gone.txt"], Is.EqualTo('D'));
                Assert.That(details.FileStatus["Assets/moved.txt"], Is.EqualTo('R'));
                Assert.That(details.ChangedFiles, Does.Contain("Assets/moved.txt").And.Not.Contain("Assets/move me.txt"));
                Assert.That(details.ChangedFiles, Is.EquivalentTo(details.FileStatus.Keys));

                UnitGitCommitDetails rootDetails = repo.Git.GetCommitDetails(root);
                Assert.That(rootDetails.Body, Is.Empty);
                Assert.That(rootDetails.Commit.Parents, Is.Empty);
                Assert.That(rootDetails.FileStatus.Values, Is.All.EqualTo('A'));
                Assert.That(rootDetails.ChangedFiles, Does.Contain("Assets/café.txt").And.Contain("Assets/move me.txt"));

                Assert.That(repo.Git.GetCommitDetails(merge).Commit.Parents, Is.EqualTo(new[] { change, side }));
            }
        }

        [Test]
        public void ParsingKeepsFieldPositionsAndReadsMergeAndRenameLines()
        {
            const string separator = "\x1f";
            string log = string.Join(separator, "a1b2c3d", "full-1", "Merge", "Ada", "ada@example.test", "2 days ago", "HEAD -> main", "", "p1 p2", "1612325106");
            UnitGitCommit commit = UnitGitService.ParseCommits(log, string.Empty).Single();
            Assert.That(commit.ReleaseId, Is.Empty);
            Assert.That(commit.Parents, Is.EqualTo(new[] { "p1", "p2" }));
            Assert.That(commit.Timestamp, Is.EqualTo(1612325106L));

            string show = string.Join(separator, "a1b2c3d", "full-1", "Subject", "Ada", "ada@example.test", "date", "", "Ada", "ada@example.test", "date", "", "p1 p2", "1612325106", "Body line\r\n") +
                          "\0\r\nMM\tAssets/a.txt\r\nR087\t\"Assets/old\\tname.txt\"\tAssets/new name.txt\r\n";
            UnitGitCommitDetails details = UnitGitService.ParseCommitDetails(show);
            Assert.That(details.Body, Is.EqualTo("Body line"));
            Assert.That(details.Commit.Parents, Is.EqualTo(new[] { "p1", "p2" }));
            Assert.That(details.ChangedFiles, Is.EqualTo(new[] { "Assets/a.txt", "Assets/new name.txt" }));
            Assert.That(details.FileStatus["Assets/a.txt"], Is.EqualTo('M'));
            Assert.That(details.FileStatus["Assets/new name.txt"], Is.EqualTo('R'));
        }

        [Test]
        public void LogFiltersNarrowToBranchAuthorAndDate()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "1\n");
                string old = CommitAt(repo, "old work", "Ada [VR] Lovelace", "2020-01-01T10:00:00+0000");
                repo.Run("checkout", "-q", "-b", "feature");
                repo.Write("Assets/b.txt", "2\n");
                string feature = CommitAt(repo, "feature work", "Grace Hopper", "2024-06-01T10:00:00+0000");
                repo.Run("checkout", "-q", "main");
                repo.Write("Assets/c.txt", "3\n");
                string recent = CommitAt(repo, "recent needle", "Ada [VR] Lovelace", "2024-07-01T10:00:00+0000");
                var since = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                Assert.That(Hashes(repo.Git.GetCommits(string.Empty, 50)), Is.EquivalentTo(new[] { old, feature, recent }));
                Assert.That(Hashes(repo.Git.GetCommits(string.Empty, 50, new UnitGitLogFilter { Branch = "feature" })), Is.EquivalentTo(new[] { old, feature }));
                Assert.That(Hashes(repo.Git.GetCommits(string.Empty, 50, new UnitGitLogFilter { Author = "ada [vr]" })), Is.EquivalentTo(new[] { old, recent }),
                    "The author is matched as typed, in any letter case, not as a regular expression.");
                Assert.That(Hashes(repo.Git.GetCommits(string.Empty, 50, new UnitGitLogFilter { Since = since })), Is.EquivalentTo(new[] { feature, recent }));

                var all = new UnitGitLogFilter { Branch = "main", Author = "Ada", Since = since };
                Assert.That(all.IsEmpty, Is.False);
                Assert.That(new UnitGitLogFilter().IsEmpty, Is.True);
                Assert.That(Hashes(repo.Git.BuildSnapshot("needle", 300, all).Commits), Is.EqualTo(new[] { recent }));
                Assert.That(repo.Git.BuildSnapshot("needle", 300, new UnitGitLogFilter { Branch = "feature" }).Commits, Is.Empty);
                Assert.That(repo.Git.BuildSnapshot(string.Empty, 300, null).Commits.Count, Is.EqualTo(3));
            }
        }

        private static string CommitAt(UnitGitTestRepo repo, string message, string author, string isoDate)
        {
            repo.Run("add", "-A");
            var environment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = author,
                ["GIT_AUTHOR_EMAIL"] = "author@example.test",
                ["GIT_AUTHOR_DATE"] = isoDate,
                ["GIT_COMMITTER_DATE"] = isoDate
            };
            AssertOk(repo.Git.RunGitWithEnvironment(environment, 60000, "commit", "-q", "-m", message));
            return repo.Run("rev-parse", "HEAD").Trim();
        }

        private static Dictionary<string, string> StatusOf(UnitGitTestRepo repo)
        {
            return UnitGitService.ParseStatusOutput(repo.Run("status", "--porcelain=v1", "-uall")).Changes
                .ToDictionary(change => change.Path, change => change.IndexStatus.ToString() + change.WorkTreeStatus);
        }

        private static string Short(UnitGitTestRepo repo, string hash) => repo.Run("rev-parse", "--short", hash).Trim();

        private static List<string> Hashes(IEnumerable<UnitGitCommit> commits) => commits.Select(commit => commit.FullHash).ToList();

        private static List<string> Lines(string output) =>
            UnitGitService.SplitLines(output).Where(line => line.Trim().Length > 0).ToList();

        private static void AssertOk(GitCommandResult result)
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
