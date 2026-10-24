using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The diff view's folding, "»" (putting a change back), whitespace options, lock waits and log redaction. Everything that
    // touches files does it in a throwaway repository.
    public sealed class UnitGitDiffViewTests
    {
        // The user's own choice to fold or not is put back after each test.
        private sealed class CollapseChoice : IDisposable
        {
            private readonly bool had = UnityEditor.EditorPrefs.HasKey(UnitGitDiffViewer.CollapsePref);
            private readonly bool value = UnityEditor.EditorPrefs.GetBool(UnitGitDiffViewer.CollapsePref, true);

            public void Dispose()
            {
                if (had) UnityEditor.EditorPrefs.SetBool(UnitGitDiffViewer.CollapsePref, value);
                else UnityEditor.EditorPrefs.DeleteKey(UnitGitDiffViewer.CollapsePref);
            }
        }

        private static UnitGitDiff WholeFileDiff(int before, int after)
        {
            var output = new StringBuilder("@@ -1," + (before + after + 1) + " +1," + (before + after + 1) + " @@\n");
            for (int i = 0; i < before; i++) output.Append(" same ").Append(i).Append('\n');
            output.Append("-old\n+new\n");
            for (int i = 0; i < after; i++) output.Append(" tail ").Append(i).Append('\n');
            return UnitGitService.ParseUnifiedDiff(output.ToString());
        }

        [Test]
        public void UnchangedStretchesFoldAroundTheChangeAndUnfold()
        {
            using (new CollapseChoice())
            {
                var viewer = new UnitGitDiffViewer { Collapse = true };
                viewer.SetDiff(WholeFileDiff(100, 100));
                // A fold, 4 lines, the change, 4 lines, a fold.
                Assert.That(viewer.RowCount, Is.EqualTo(11));
                Assert.That(viewer.DifferenceCount, Is.EqualTo(1));
                Assert.That(viewer.MoveChange(1), Is.True);
                Assert.That(viewer.Blocks[0].LeftStart, Is.EqualTo(101));
                Assert.That(viewer.Blocks[0].RightStart, Is.EqualTo(101));

                viewer.Collapse = false;
                Assert.That(viewer.RowCount, Is.EqualTo(201), "Every line, without the hunk header.");
                viewer.Collapse = true;
                Assert.That(viewer.RowCount, Is.EqualTo(11));

                // A search match in a folded stretch unfolds just around it.
                viewer.Search("same 10", true);
                Assert.That(viewer.MatchCount, Is.EqualTo(1));
                Assert.That(viewer.RowCount, Is.EqualTo(11 + 9 + 1), "The match with 4 lines on each side, and the fold split in two.");
            }
        }

        [Test]
        public void NothingFoldsWithoutAChange()
        {
            using (new CollapseChoice())
            {
                var viewer = new UnitGitDiffViewer { Collapse = true };
                var diff = new UnitGitDiff();
                for (int i = 0; i < 50; i++) diff.Lines.Add(new UnitGitDiffLine { Right = "line " + i });
                viewer.SetDiff(diff);
                Assert.That(viewer.RowCount, Is.EqualTo(50));
                Assert.That(viewer.DifferenceCount, Is.Zero);
            }
        }

        [Test]
        public void LinesMissingBetweenHunksShowAsAStaticFold()
        {
            var viewer = new UnitGitDiffViewer();
            viewer.SetDiff(UnitGitService.ParseUnifiedDiff("@@ -10,2 +10,2 @@\n a\n-b\n+B\n@@ -500,2 +500,2 @@\n c\n-d\n+D\n"));
            // "9 lines" before the first hunk, its 2 rows, "488 lines" between the hunks, the second hunk's 2 rows.
            Assert.That(viewer.RowCount, Is.EqualTo(6));
            Assert.That(viewer.Blocks.Select(block => block.RightStart), Is.EqualTo(new[] { 11, 501 }));
        }

        [Test]
        public void NoNewlineMarkerIsNotALine()
        {
            var diff = UnitGitService.ParseUnifiedDiff("@@ -1,2 +1,2 @@\n a\n-b\n+b\n\\ No newline at end of file\n");
            Assert.That(diff.Lines.Count(line => line.Kind != UnitGitDiffLineKind.Hunk), Is.EqualTo(2));
            Assert.That(diff.Lines.Any(line => (line.Right ?? string.Empty).Contains("No newline")), Is.False);
            Assert.That(diff.RightEndsWithoutNewline, Is.True);
            Assert.That(diff.LeftEndsWithoutNewline, Is.False);
        }

        [Test]
        public void RevertPutsTheChangeBackKeepingLineEndingsAndCanBeUndone()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string path = repo.Full("Assets/file.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("one\r\nTWO\r\nthree\r\n")).ToArray();
                File.WriteAllBytes(path, bytes);

                var result = UnitGitBlockRevert.Apply(path, 2, new[] { "TWO" }, new[] { "two", "two and a half" }, false, out var before, out var after);
                Assert.That(result.Success, Is.True, result.Message);
                var written = File.ReadAllBytes(path);
                Assert.That(written.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "The byte order mark stays.");
                Assert.That(Encoding.UTF8.GetString(written, 3, written.Length - 3), Is.EqualTo("one\r\ntwo\r\ntwo and a half\r\nthree\r\n"));

                Assert.That(UnitGitBlockRevert.Undo(path, before, after).Success, Is.True);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
        }

        [Test]
        public void RevertRefusesWhenTheFileChangedMeanwhile()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/file.txt", "one\nedited elsewhere\nthree\n");
                string path = repo.Full("Assets/file.txt");
                var result = UnitGitBlockRevert.Apply(path, 2, new[] { "TWO" }, new[] { "two" }, false, out _, out _);
                Assert.That(result.Success, Is.False);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\nedited elsewhere\nthree\n"));

                // Undo only writes back over the exact bytes it wrote.
                var undo = UnitGitBlockRevert.Undo(path, Encoding.UTF8.GetBytes("x"), Encoding.UTF8.GetBytes("not what is there"));
                Assert.That(undo.Success, Is.False);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\nedited elsewhere\nthree\n"));
            }
        }

        [Test]
        public void RevertAtTheEndFollowsTheOldFinalNewline()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/file.txt", "a\nb\nadded\n");
                string path = repo.Full("Assets/file.txt");
                // The added last line goes; the old version had no newline after "b".
                Assert.That(UnitGitBlockRevert.Apply(path, 3, new[] { "added" }, new string[0], true, out _, out _).Success, Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("a\nb"));
                // A removed line comes back at its place.
                Assert.That(UnitGitBlockRevert.Apply(path, 2, new string[0], new[] { "between" }, true, out _, out _).Success, Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("a\nbetween\nb"));
            }
        }

        [Test]
        public void TheLocalDiffPutsBackOnlyTheWorkingTreeSectionAndHonoursWhitespace()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\ntwo\nthree\n");
                repo.Commit("base");
                repo.Write("Assets/a.txt", "one\n  two\nthree\nfour\n");
                var change = new UnitGitStatusEntry { Path = "Assets/a.txt", WorkTreeStatus = 'M' };

                var diff = repo.Git.GetFileDiff(change, UnitGitWhitespace.None);
                Assert.That(diff.EditableFrom, Is.EqualTo(0));
                Assert.That(diff.Lines.Count(line => line.Kind == UnitGitDiffLineKind.Context), Is.EqualTo(2), "The whole file, not only the changes.");

                var ignoring = repo.Git.GetFileDiff(change, UnitGitWhitespace.Ignore);
                Assert.That(ignoring.Lines.Count(line => line.Kind == UnitGitDiffLineKind.Changed), Is.Zero, "The re-indented line is no change.");
                Assert.That(ignoring.Lines.Count(line => line.Kind == UnitGitDiffLineKind.Added), Is.EqualTo(1));

                repo.Run("add", "Assets/a.txt");
                repo.Write("Assets/a.txt", "one\n  two\nthree\nfour\nfive\n");
                var staged = repo.Git.GetFileDiff(new UnitGitStatusEntry { Path = "Assets/a.txt", IndexStatus = 'M', WorkTreeStatus = 'M' });
                Assert.That(staged.EditableFrom, Is.GreaterThan(0), "Only the unstaged section is the file on disk.");
                Assert.That(staged.Lines[staged.EditableFrom - 1].Kind, Is.EqualTo(UnitGitDiffLineKind.Hunk));
            }
        }

        [Test]
        public void WritesWaitForAnotherProgramsIndexLock()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Commit("base");
                repo.Write("Assets/a.txt", "b\n");
                string lockPath = repo.Full(".git/index.lock");
                File.WriteAllText(lockPath, string.Empty);
                var release = new Thread(() =>
                {
                    Thread.Sleep(400);
                    File.Delete(lockPath);
                });
                release.Start();
                var result = repo.Git.Stage(new[] { "Assets/a.txt" });
                release.Join();
                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(repo.Run("status", "--porcelain").Trim(), Is.EqualTo("M  Assets/a.txt"));
            }
        }

        [Test]
        public void ShowHistoryListsTheCommitsOfOneFileThroughItsRename()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\n");
                repo.Commit("add a");
                repo.Write("Assets/b.txt", "b\n");
                repo.Commit("add b");
                repo.Write("Assets/a.txt", "two\n");
                repo.Commit("edit a");
                repo.Run("mv", "Assets/a.txt", "Assets/renamed.txt");
                repo.Commit("rename a");

                var history = repo.Git.GetCommits(string.Empty, 50, new UnitGitLogFilter { Path = "Assets/renamed.txt" });
                Assert.That(history.Select(commit => commit.Subject), Is.EqualTo(new[] { "rename a", "edit a", "add a" }));
                var folder = repo.Git.GetCommits(string.Empty, 50, new UnitGitLogFilter { Path = "Assets" });
                Assert.That(folder.Count, Is.EqualTo(4));
            }
        }

        [Test]
        public void ReadsAndWritesAreToldApart()
        {
            Assert.That(UnitGitWriteGate.IsRead(new[] { "--literal-pathspecs", "status", "--porcelain" }), Is.True);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "-c", "core.quotePath=true", "ls-files", "--cached" }), Is.True);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "stash", "list" }), Is.True);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "branch", "-vv" }), Is.True);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "--literal-pathspecs", "add", "-A", "--", "a" }), Is.False);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "branch", "-d", "topic" }), Is.False);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "stash", "push" }), Is.False);
            Assert.That(UnitGitWriteGate.IsRead(new[] { "commit", "-F", "message.txt" }), Is.False);
        }

        [Test]
        public void CredentialsNeverReachTheLog()
        {
            Assert.That(UnitGitRedaction.Redact("fatal: unable to access 'https://enzo:s3cret@github.com/a/b.git/'"),
                Is.EqualTo("fatal: unable to access 'https://enzo:***@github.com/a/b.git/'"));
            Assert.That(UnitGitRedaction.Redact("https://ghp_abcdefghijklmnopqrstuvwxyz0123456789@github.com/a/b"),
                Does.Not.Contain("ghp_"));
            Assert.That(UnitGitRedaction.Redact("> git -c http.extraHeader=\"Authorization: Bearer abc.def\" fetch"),
                Does.Not.Contain("abc.def"));
            Assert.That(UnitGitRedaction.Redact("token glpat-abcdefghijklmnopqrstu used"), Does.Not.Contain("glpat-"));
            Assert.That(UnitGitRedaction.Redact("ssh://git@github.com/a/b.git"), Is.EqualTo("ssh://git@github.com/a/b.git"));
        }
    }
}
