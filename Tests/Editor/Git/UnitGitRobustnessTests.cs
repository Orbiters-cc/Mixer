using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor.Processes;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // A change in flight at a reload is let finish, a lock is reported and never deleted, stdin never gains a byte order mark (and every list still reaches Git without
    // stdin), commit data survives the user's i18n settings, and a resumed history never mixes two histories.
    public sealed class UnitGitRobustnessTests
    {
        [Test]
        public void AChangeInFlightAtAReloadIsLetFinish()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/keep.txt", "base\n");
                repo.Commit("base");
                var paths = new List<string>();
                for (int i = 0; i < 400; i++)
                {
                    paths.Add("Assets/Many/" + i + ".txt");
                    repo.Write(paths[i], i + "\n");
                }

                GitCommandResult staged = null;
                var worker = new System.Threading.Thread(() => staged = repo.Git.Stage(paths));
                worker.Start();
                // Once git add runs, "reload": reads stop at once, the change gets its grace time.
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (EditorProcessRunner.ActiveCount == 0 && wait.ElapsedMilliseconds < 10000 && worker.IsAlive) System.Threading.Thread.Sleep(1);
                EditorProcessRunner.StopAllThenResume(EditorProcessRunner.WriteGraceMilliseconds);
                Assert.That(worker.Join(30000), Is.True);
                // Finished within the grace time: a correct result. (Past it, the change is stopped and says so instead.)
                if (!staged.Success)
                {
                    Assert.That(staged.Message, Does.Contain("index.lock"), "A stopped change names the lock.");
                    Assert.Ignore("git add needed more than the grace time here.");
                }
                var index = repo.Run("diff", "--cached", "--name-only", "-z").Split('\0').Where(path => path.Length > 0).ToList();
                Assert.That(index, Is.EquivalentTo(paths));
                Assert.That(File.Exists(repo.Full(".git/index.lock")), Is.False);
            }
        }

        [Test]
        public void AKilledChangeNamesTheLockAndDeletesNothing()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/keep.txt", "base\n");
                repo.Commit("base");
                for (int i = 0; i < 3000; i++) repo.Write("Assets/Many/" + i + ".txt", i + "\n");
                string lockPath = repo.Full(".git/index.lock");
                GitCommandResult killed = repo.Git.RunGit(150, "add", "-A");
                if (killed.Success) Assert.Ignore("git add finished within 150 ms here: nothing was killed.");
                Assert.That(killed.Message, Does.Contain("index.lock"));
                Assert.That(killed.Message, Does.Contain("another program may be using the repository; if none is, deleting"));
                if (File.Exists(lockPath))
                {
                    // Left as Git left it: the next change says so, and the user decides.
                    GitCommandResult next = repo.Git.Stage(new[] { "Assets/keep.txt" });
                    Assert.That(next.Success, Is.False);
                    Assert.That(next.Message, Does.Contain("deleting"));
                    Assert.That(File.Exists(lockPath), Is.True);
                    File.Delete(lockPath);
                }
                Assert.That(repo.Git.Stage(new[] { "Assets/keep.txt" }).Success, Is.True);
            }
        }

        [Test]
        public void ALockSomeoneElseHoldsIsReportedAndKept()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                string lockPath = repo.Full(".git/index.lock");
                File.WriteAllText(lockPath, string.Empty);
                GitCommandResult add = repo.Git.Stage(new[] { "Assets/a.txt" });
                Assert.That(add.Success, Is.False);
                Assert.That(add.Message, Does.Contain("index.lock"));
                Assert.That(add.Message, Does.Contain("another program may be using the repository; if none is, deleting"));
                Assert.That(add.Message.Split(new[] { "deleting" }, StringSplitOptions.None).Length, Is.EqualTo(2), "Said once.");
                Assert.That(File.Exists(lockPath), Is.True, "Mixer Git never deletes a lock.");
                File.Delete(lockPath);
                Assert.That(repo.Git.Stage(new[] { "Assets/a.txt" }).Success, Is.True);
            }
        }

        [Test]
        public void AChangeRunsToItsEndEvenWhenTheReadThatAskedIsSuperseded()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Git.ReadSuperseded = () => true;
                GitCommandResult add = repo.Git.Stage(new[] { "Assets/a.txt" });
                repo.Git.ReadSuperseded = null;
                Assert.That(add.Success, Is.True, add.Message);
                Assert.That(repo.Run("diff", "--cached", "--name-only").Trim(), Is.EqualTo("Assets/a.txt"));
            }
        }

        [Test]
        public void StdinStartsWithExactlyTheBytesWritten()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                // The id of empty content: a byte order mark in front would make it another blob.
                GitCommandResult empty = repo.Git.RunGitWithInput(60000, new byte[0], "hash-object", "--stdin");
                Assert.That(empty.StandardOutput.Trim(), Is.EqualTo("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"));
            }
        }

        [Test]
        public void EveryListReachesGitWithoutStdinToo()
        {
            foreach (bool clean in new[] { true, false })
            {
                UnitGitRunner.ForceNoCleanInput = !clean;
                try
                {
                    using (var repo = UnitGitTestRepo.Create())
                    {
                        repo.Write(".gitignore", "*.ignored\n");
                        repo.Write("Assets/-é a.txt", "a\n");
                        repo.Write("Assets/b.ignored", "b\n");
                        string first = repo.Commit("first");
                        repo.Write("Assets/-é a.txt", "a2\n");
                        string second = repo.Commit("second");

                        repo.Write("Assets/-é a.txt", "a3\n");
                        Assert.That(repo.Git.Stage(new[] { "Assets/-é a.txt" }).Success, Is.True, "stage, clean " + clean);
                        Assert.That(repo.Git.Commit("third é").Success, Is.True, "commit, clean " + clean);
                        Assert.That(repo.Run("log", "-1", "--format=%s").Trim(), Is.EqualTo("third é"));

                        Dictionary<string, UnitGitCommitDetails> details = repo.Git.GetCommitDetails(new[] { second, first });
                        Assert.That(details.Keys, Is.EquivalentTo(new[] { first, second }), "details, clean " + clean);

                        using (var index = new UnitGitScopedIndex(repo.Git))
                            Assert.That(index.FilterIgnored(new[] { "Assets/-é a.txt", "Assets/b.ignored" }), Is.EqualTo(new[] { "Assets/-é a.txt" }),
                                "check-ignore, clean " + clean);

                        repo.Git.ReadHeadState(out UnitGitHeadState head);
                        GitCommandResult renamed = repo.Git.RenameCommit(first, "first renamed", head);
                        Assert.That(renamed.Success, Is.True, renamed.Message);
                        Assert.That(repo.Run("log", "--format=%s").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries),
                            Is.EqualTo(new[] { "third é", "second", "first renamed" }));
                    }
                }
                finally
                {
                    UnitGitRunner.ForceNoCleanInput = false;
                }
            }
        }

        [Test]
        public void RenamingKeepsOtherCommitsWhateverTheLogEncodingAndTheirOwn()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "1\n");
                string first = repo.Commit("first");
                repo.Write("Assets/a.txt", "2\n");
                repo.Run("add", "-A");
                var author = new Dictionary<string, string> { { "GIT_AUTHOR_NAME", "Zoë Café" }, { "GIT_AUTHOR_EMAIL", "zoe@example.test" } };
                Assert.That(repo.Git.RunGitWithEnvironment(author, 60000, "commit", "-q", "-m", "café ☕").Success, Is.True);

                // A commit stored in Latin-1, with its "encoding" header.
                repo.Write("Assets/a.txt", "3\n");
                repo.Run("add", "-A");
                string message = repo.Full("latin1.txt");
                File.WriteAllBytes(message, Encoding.GetEncoding("ISO-8859-1").GetBytes("déjà vu\n"));
                repo.Run("-c", "i18n.commitEncoding=ISO-8859-1", "commit", "-q", "-F", message);
                File.Delete(message);

                // The user prints logs in Latin-1.
                repo.Run("config", "i18n.logOutputEncoding", "ISO-8859-1");
                repo.Git.ReadHeadState(out UnitGitHeadState head);
                GitCommandResult renamed = repo.Git.RenameCommit(first, "premier", head);
                Assert.That(renamed.Success, Is.True, renamed.Message);

                string Utf8Field(string format, string revision) =>
                    repo.Run("-c", "i18n.logOutputEncoding=UTF-8", "log", "-1", "--format=" + format, revision).TrimEnd('\n');
                Assert.That(Utf8Field("%B", "HEAD"), Is.EqualTo("déjà vu"));
                Assert.That(Utf8Field("%an|%B", "HEAD~1"), Is.EqualTo("Zoë Café|café ☕"));
                Assert.That(Utf8Field("%B", "HEAD~2"), Is.EqualTo("premier"));
                Assert.That(repo.Git.GetCommits(string.Empty, 5).Select(commit => commit.Subject), Is.EqualTo(new[] { "déjà vu", "café ☕", "premier" }));
                Assert.That(repo.Git.GetCommitDetails(repo.Run("rev-parse", "HEAD~1").Trim()).Commit.AuthorName, Is.EqualTo("Zoë Café"));
            }
        }

        [Test]
        public void AHistoryResumedAfterTheRefsMovedIsReadAgainNotMixed()
        {
            using (var repo = UnitGitTestRepo.Create())
            using (var history = new UnitGitHistory())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Commit("A");
                foreach (string name in new[] { "B", "C", "D" }) repo.Run("commit", "-q", "--allow-empty", "-m", name);

                // The same key on purpose: the reader itself must notice.
                var arguments = new[] { "--all", "--" };
                Assert.That(history.Read(repo.Root, "k", arguments, string.Empty, 2, null, out var page, out _).Success, Is.True);
                Assert.That(page.Select(commit => commit.Subject), Is.EqualTo(new[] { "D", "C" }));
                history.StopReading();
                repo.Run("commit", "-q", "--allow-empty", "-m", "E");
                Assert.That(history.Read(repo.Root, "k", arguments, string.Empty, 5, null, out page, out _).Success, Is.True);
                Assert.That(page.Select(commit => commit.Subject), Is.EqualTo(new[] { "E", "D", "C", "B", "A" }));

                // Load more after a new commit: the refs signature moved, so the whole refresh runs.
                using (var window = new UnitGitHistory())
                {
                    UnitGitSnapshot first = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = window, HistoryLimit = 2 });
                    repo.Run("commit", "-q", "--allow-empty", "-m", "F");
                    UnitGitSnapshot more = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = window, HistoryLimit = 4, Previous = first, HistoryOnly = true });
                    Assert.That(more.Commits.Select(commit => commit.Subject), Is.EqualTo(new[] { "F", "E", "D", "C" }));
                    Assert.That(more.RefsSignature, Is.Not.EqualTo(first.RefsSignature));
                }
            }
        }
    }
}
