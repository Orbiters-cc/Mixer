using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The invariant of rename and squash: a commit they only re-parent comes out byte for byte the same but for its parent
    // lines and its committer, through fast-import and through commit-tree; when commit-tree would write it otherwise the
    // rewrite is refused before anything is written, and whatever got written differently is caught before any ref moves.
    public sealed class UnitGitRewriteCopyTests
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static UnitGitTestRepo History(out string first)
        {
            if (!UnitGitRunner.CleanInput) Assert.Ignore("Stdin cannot be clean on this system.");
            var repo = UnitGitTestRepo.Create();
            repo.Write("Assets/a.txt", "a\n");
            first = repo.Commit("first");
            repo.Write("Assets/a.txt", "b\n");
            repo.Commit("second");
            return repo;
        }

        // A commit object written by hand on top of HEAD (--literally: Git's checks would refuse some of these authors).
        private static string Make(UnitGitTestRepo repo, byte[] author, byte[] message)
        {
            string tree = repo.Run("rev-parse", "HEAD^{tree}").Trim();
            string parent = repo.Run("rev-parse", "HEAD").Trim();
            var bytes = new MemoryStream();
            void Put(byte[] part) => bytes.Write(part, 0, part.Length);
            Put(Ascii("tree " + tree + "\nparent " + parent + "\nauthor "));
            Put(author);
            Put(Ascii(" 1600000000 +0200\ncommitter Someone <someone@example.test> 1600000100 +0200\n\n"));
            Put(message);
            GitCommandResult made = repo.Git.RunGitWithInput(GitAccess.Exclusive, 60000, bytes.ToArray(), "hash-object", "--literally", "-t", "commit", "-w", "--stdin");
            Assert.That(made.Success, Is.True, made.Message);
            string hash = made.StandardOutput.Trim();
            repo.Run("update-ref", "refs/heads/main", hash, parent);
            return hash;
        }

        private static byte[] Stored(UnitGitTestRepo repo, string revision)
        {
            var bytes = new MemoryStream();
            GitCommandResult read = repo.Git.ReadGitStream(GitAccess.Read, 60000, (chunk, offset, count) => { bytes.Write(chunk, offset, count); return true; },
                "cat-file", "commit", revision);
            Assert.That(read.Success, Is.True, read.Message);
            return bytes.ToArray();
        }

        private static string Line(byte[] commit, string key) =>
            Latin1.GetString(commit).Split('\n').First(line => line.StartsWith(key + " ", StringComparison.Ordinal));

        private static GitCommandResult Rename(UnitGitTestRepo repo, string commit)
        {
            repo.Git.ReadHeadState(out UnitGitHeadState head);
            return repo.Git.RenameCommit(commit, "renamed", head);
        }

        private static readonly (string what, byte[] author)[] Authors =
        {
            ("spaces around the name", Ascii("   A  <a@example.test>")),
            ("a trailing period", Ascii("A. <a@example.test>")),
            ("crud around the e-mail", Ascii("A <.a@example.test,>")),
            ("a tab inside", Ascii("A\tB <a@example.test>")),
            ("an empty name", Ascii("<a@example.test>")),
            ("unusual characters", Encoding.UTF8.GetBytes("Zoë O'Brien-Ünal (ops) [x] {y} <z@example.test>")),
            ("high bytes, no encoding", Latin1.GetBytes("René <r@example.test>")),
            ("an ordinary name", Ascii("Plain Name <p@example.test>")),
        };

        [Test]
        public void EveryAuthorIsCopiedAsStoredOrTheRewriteIsRefused()
        {
            foreach (var author in Authors)
            {
                using (var repo = History(out string first))
                {
                    string top = Make(repo, author.author, Encoding.UTF8.GetBytes("top\n"));
                    byte[] before = Stored(repo, top);
                    GitCommandResult result = Rename(repo, first);
                    string at = author.what;
                    if (author.what == "an empty name")
                    {
                        // fast-import writes it "author  <e-mail>", with the space a name would be followed by: the check of
                        // the written copies refuses the rewrite, and nothing moves.
                        Assert.That(result.Success, Is.False, at);
                        Assert.That(result.Message, Does.Contain("different author line").And.Contain("left the history as it is"), at);
                        Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top), at + ": nothing moved");
                    }
                    else
                    {
                        Assert.That(result.Success, Is.True, at + ": " + result.Message);
                        byte[] after = Stored(repo, "HEAD");
                        Assert.That(Latin1.GetBytes(Line(after, "author")), Is.EqualTo(Latin1.GetBytes(Line(before, "author"))), at);
                        Assert.That(UnitGitService.CopyDifference(before, after, new[] { repo.Run("rev-parse", "HEAD~1").Trim() },
                            repo.Run("rev-parse", "HEAD^{tree}").Trim(), null), Is.Null, at);
                    }
                    Assert.That(repo.Run("for-each-ref", "refs/mixer-git/").Trim(), Is.Empty, at);
                }
            }
        }

        [Test]
        public void ACopyWrittenDifferentlyStopsTheRewriteBeforeAnyRefMoves()
        {
            using (var repo = History(out string first))
            {
                string top = Make(repo, Ascii("Plain <p@example.test>"), Encoding.UTF8.GetBytes("top\n"));
                UnitGitService.WrittenMessageSeamForTests = (hash, message) => hash == top ? Encoding.UTF8.GetBytes("tampered\n") : message;
                GitCommandResult result;
                try { result = Rename(repo, first); }
                finally { UnitGitService.WrittenMessageSeamForTests = null; }
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Does.Contain(top.Substring(0, 7)).And.Contain("different message").And.Contain("moved nothing"));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top), "No ref moved.");
                Assert.That(repo.Run("for-each-ref", "refs/mixer-git/").Trim(), Is.Empty);
            }
        }

        [Test]
        public void TheCommitterIsRegeneratedOnPurposeAndNothingIsSigned()
        {
            var saved = new[] { "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" }.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Rewriter");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "rewriter@example.test");
            try
            {
                using (var repo = History(out string first))
                {
                    // A repository configured to sign: the commits a rewrite writes are not signed, as on the original code.
                    repo.Run("config", "commit.gpgsign", "true");
                    string top = Make(repo, Ascii("Plain <p@example.test>"), Encoding.UTF8.GetBytes("top\n"));
                    Assert.That(Rename(repo, first).Success, Is.True);
                    // A rewritten commit is a new commit: whoever rewrote it, now (as on master). Its author stays.
                    Assert.That(Line(Stored(repo, "HEAD"), "committer"), Does.StartWith("committer Rewriter <rewriter@example.test> "));
                    Assert.That(Line(Stored(repo, "HEAD"), "author"), Is.EqualTo(Line(Stored(repo, top), "author")));
                    Assert.That(Latin1.GetString(Stored(repo, "HEAD")), Does.Not.Contain("gpgsig"));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", saved[0]);
                Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", saved[1]);
            }
        }

        [Test]
        public void ScratchRefsAreSweptOnlyWhenTheirOwnerIsGone()
        {
            string self = System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                          System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            using (var repo = History(out string first))
            {
                string head = repo.Run("rev-parse", "HEAD").Trim();
                // Two service instances on one repository stand for two editors; the other editor is 4242, started at 777.
                var otherEditor = new UnitGitService(repo.Root);
                string mine = "refs/mixer-git/rewrite/" + self + "/left", theirs = "refs/mixer-git/rewrite/4242-777/running";
                repo.Run("update-ref", mine, head);
                repo.Run("update-ref", theirs, head);
                repo.Run("update-ref", "refs/mixer-git/rewrite/unreadable", head);
                bool otherAlive = true;
                UnitGitService.OwnerAliveForTests = (pid, started) => pid == 4242 && started == 777 ? otherAlive : false;
                try
                {
                    otherEditor.SweepScratchRefs();
                    string left = repo.Run("for-each-ref", "--format=%(refname)", "refs/mixer-git/");
                    Assert.That(left, Does.Not.Contain(mine), "This editor's leftover, with no rewrite of its own running: swept.");
                    Assert.That(left, Does.Contain(theirs), "The other editor still runs: its rewrite's ref is kept.");
                    Assert.That(left, Does.Contain("refs/mixer-git/rewrite/unreadable"), "A name that cannot be read is kept.");

                    otherAlive = false;
                    Assert.That(Rename(repo, first).Success, Is.True, "A rewrite sweeps first, and deletes its own.");
                    left = repo.Run("for-each-ref", "--format=%(refname)", "refs/mixer-git/");
                    Assert.That(left, Does.Not.Contain(theirs), "Its editor gone: swept.");
                    Assert.That(left.Trim(), Is.EqualTo("refs/mixer-git/rewrite/unreadable"));
                }
                finally
                {
                    UnitGitService.OwnerAliveForTests = null;
                }
            }
        }
    }
}
