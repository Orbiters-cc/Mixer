using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor.Processes;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Path lists and messages handed to Git on stdin, streamed reads and the kept-alive object reader, each against a real
    // repository: names with spaces, glob characters, non-ASCII letters and leading dashes must reach Git exactly.
    public sealed class UnitGitStdinTests
    {
        // A "[ab]" name is a glob without --literal-pathspecs (it would match "a.txt"); "-x" an option without the list.
        private static readonly string[] OddNames =
        {
            "Assets/with space.txt", "Assets/-leading dash.txt", "Assets/[ab].txt", "Assets/a.txt", "Assets/é ü 日本.txt",
            "Assets/it's #1.txt", "Assets/-/-x.txt"
        };

        [Test]
        public void StageUnstageRollbackAndStopTrackingKeepOddNamesExact()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                foreach (string name in OddNames) repo.Write(name, "one\n");
                repo.Commit("base");
                foreach (string name in OddNames) repo.Write(name, "two\n");

                // Only the glob-looking name: a.txt must stay out.
                AssertOk(repo.Git.Stage(new[] { "Assets/[ab].txt" }));
                Assert.That(StatusOf(repo)["Assets/[ab].txt"], Is.EqualTo("M "));
                Assert.That(StatusOf(repo)["Assets/a.txt"], Is.EqualTo(" M"));

                AssertOk(repo.Git.Stage(OddNames));
                foreach (string name in OddNames) Assert.That(StatusOf(repo)[name], Is.EqualTo("M "), name);

                AssertOk(repo.Git.Unstage(new[] { "Assets/-leading dash.txt", "Assets/é ü 日本.txt" }));
                Assert.That(StatusOf(repo)["Assets/-leading dash.txt"], Is.EqualTo(" M"));
                Assert.That(StatusOf(repo)["Assets/é ü 日本.txt"], Is.EqualTo(" M"));
                Assert.That(StatusOf(repo)["Assets/with space.txt"], Is.EqualTo("M "));

                AssertOk(repo.Git.Rollback(new[] { "Assets/-/-x.txt", "Assets/é ü 日本.txt" }));
                Assert.That(repo.Read("Assets/-/-x.txt"), Is.EqualTo("one\n"));
                Assert.That(repo.Read("Assets/é ü 日本.txt"), Is.EqualTo("one\n"));
                Assert.That(StatusOf(repo).ContainsKey("Assets/-/-x.txt"), Is.False);

                AssertOk(repo.Git.StopTracking(new[] { "Assets/it's #1.txt" }));
                // Staged as removed, and untracked again on disk.
                Assert.That(repo.Run("diff", "--cached", "--name-status", "--", "Assets/it's #1.txt").Trim(), Is.EqualTo("D	Assets/it's #1.txt"));
                Assert.That(File.Exists(repo.Full("Assets/it's #1.txt")), Is.True, "Stop tracking keeps the file on disk.");
            }
        }

        [Test]
        public void FiveThousandPathsGoToGitInOneCommandAndComeBackExact()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/keep.txt", "base\n");
                repo.Commit("base");
                var paths = new List<string>();
                for (int i = 0; i < 5000; i++)
                {
                    string prefix = i % 3 == 0 ? "-dash " : i % 3 == 1 ? "é space " : "plain ";
                    string path = "Assets/Many/" + (i % 10) + "/" + prefix + i.ToString("0000") + ".txt";
                    repo.Write(path, i + "\n");
                    paths.Add(path);
                }

                var commands = new List<string>();
                repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
                AssertOk(repo.Git.Stage(paths));
                Assert.That(commands.Count(line => line.Contains(" add ")), Is.EqualTo(1), "One git add for all paths.");
                repo.Git.ProcessLogReceived = null;
                var staged = new HashSet<string>(repo.Run("diff", "--cached", "--name-only", "-z").Split('\0').Where(path => path.Length > 0));
                Assert.That(staged.SetEquals(paths), Is.True);

                AssertOk(repo.Git.Unstage(paths.Take(2500).ToList()));
                staged = new HashSet<string>(repo.Run("diff", "--cached", "--name-only", "-z").Split('\0').Where(path => path.Length > 0));
                Assert.That(staged.SetEquals(paths.Skip(2500)), Is.True);

                // More paths than a command line holds: Rollback asks Git for the whole listing and keeps only these.
                AssertOk(repo.Git.Rollback(paths.Skip(2500).ToList()));
                Assert.That(repo.Run("diff", "--cached", "--name-only", "-z").Trim('\0').Length, Is.EqualTo(0));
                Assert.That(File.Exists(repo.Full(paths[4999])), Is.True, "Rolled-back added files stay on disk, untracked.");
            }
        }

        [Test]
        public void CommitMessagesThroughStdinKeepEveryCharacter()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\n");
                AssertOk(repo.Git.Stage(new[] { "Assets/a.txt" }));
                const string message = "Tête « quoted \"x\" » 日本語\n\n-not an option\n\\backslash\\";
                AssertOk(repo.Git.Commit(message, "Trailer-Key: value"));
                Assert.That(repo.Run("log", "-1", "--format=%B").TrimEnd('\n'),
                    Is.EqualTo(message + "\n\nTrailer-Key: value"));
            }
        }

        [Test]
        public void StreamedReadsStopEarlyAndStoppedProcessesAreGone()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                for (int i = 0; i < 300; i++) repo.Write("Assets/f" + i + ".txt", i + "\n");
                repo.Commit("files");
                int before = EditorProcessRunner.ActiveCount;
                long seen = 0;
                GitCommandResult result = repo.Git.ReadGitStream(60000, (bytes, offset, count) =>
                {
                    seen += count;
                    return false;
                }, "ls-files", "-z");
                Assert.That(result.Success && result.Stopped, Is.True, result.Message);
                Assert.That(seen, Is.GreaterThan(0));
                Assert.That(EditorProcessRunner.ActiveCount, Is.LessThanOrEqualTo(before), "The stopped process is gone.");

                GitCommandResult whole = repo.Git.ReadGitStream(60000, (bytes, offset, count) => true, "ls-files", "-z");
                Assert.That(whole.Success && !whole.Stopped, Is.True, whole.Message);
            }
        }

        [Test]
        public void TheKeptAliveObjectReaderAnswersLikeCatFile()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                foreach (string name in OddNames) repo.Write(name, "content of " + name + "\n");
                File.WriteAllBytes(repo.Full("Assets/binary.bin"), new byte[] { 0, 1, 2, 255, 10, 13, 0 });
                string hash = repo.Commit("files");
                var objects = UnitGitObjects.For(repo.Root);
                foreach (string name in OddNames)
                {
                    var bytes = new MemoryStream();
                    Assert.That(objects.Read(hash + ":" + name, bytes, out string type, out long size), Is.EqualTo(UnitGitObjects.Answer.Found), name);
                    Assert.That(type, Is.EqualTo("blob"));
                    Assert.That(Encoding.UTF8.GetString(bytes.ToArray()), Is.EqualTo("content of " + name + "\n"));
                    Assert.That(size, Is.EqualTo(bytes.Length));
                    Assert.That(repo.Git.ReadFileAt(hash, name), Is.EqualTo("content of " + name + "\n"));
                    Assert.That(repo.Git.ReadFileAt("HEAD", name), Is.EqualTo("content of " + name + "\n"), "HEAD goes through a process of its own.");
                }

                Assert.That(objects.Read(hash + ":Assets/none.txt", null, out _, out _), Is.EqualTo(UnitGitObjects.Answer.Missing));
                Assert.That(repo.Git.ReadFileAt(hash, "Assets/none.txt"), Is.Null);
                Assert.That(objects.Describe(hash + ":Assets/binary.bin", out string kind, out long length), Is.EqualTo(UnitGitObjects.Answer.Found));
                Assert.That(kind + " " + length, Is.EqualTo("blob 7"));

                string folder = UnitGitTestRepo.TempFolder();
                try
                {
                    string written = Path.Combine(folder, "binary.bin");
                    Assert.That(repo.Git.WriteBlob(hash + ":Assets/binary.bin", written), Is.True);
                    Assert.That(File.ReadAllBytes(written), Is.EqualTo(new byte[] { 0, 1, 2, 255, 10, 13, 0 }));
                    Assert.That(repo.Git.WriteBlob(hash + ":Assets", Path.Combine(folder, "tree.bin")), Is.False, "A folder is no file version.");
                    Assert.That(File.Exists(Path.Combine(folder, "tree.bin")), Is.False);
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(folder);
                }

                Assert.That(UnitGitObjects.IsStable(hash + ":x"), Is.True);
                Assert.That(UnitGitObjects.IsStable(hash + "^:x"), Is.True);
                Assert.That(UnitGitObjects.IsStable("HEAD:x"), Is.False, "HEAD moves: never asked from a kept-alive process.");
                Assert.That(UnitGitObjects.IsStable(":2:x"), Is.False, "The index changes: never asked from a kept-alive process.");
                Assert.That(UnitGitObjects.IsStable(hash + ":a\nb"), Is.False);
            }
        }

        private static Dictionary<string, string> StatusOf(UnitGitTestRepo repo)
        {
            return UnitGitService.ParseStatusOutput(repo.Run("status", "--porcelain=v1", "-uall")).Changes
                .ToDictionary(change => change.Path, change => change.IndexStatus.ToString() + change.WorkTreeStatus);
        }

        private static void AssertOk(GitCommandResult result)
        {
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
