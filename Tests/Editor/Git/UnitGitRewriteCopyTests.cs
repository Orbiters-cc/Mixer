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
            GitCommandResult made = repo.Git.RunGitWithInput(60000, bytes.ToArray(), "hash-object", "--literally", "-t", "commit", "-w", "--stdin");
            Assert.That(made.Success, Is.True, made.Message);
            string hash = made.StandardOutput.Trim();
            repo.Run("update-ref", "refs/heads/main", hash, parent);
            return hash;
        }

        private static byte[] Stored(UnitGitTestRepo repo, string revision)
        {
            var bytes = new MemoryStream();
            GitCommandResult read = repo.Git.ReadGitStream(60000, (chunk, offset, count) => { bytes.Write(chunk, offset, count); return true; },
                "cat-file", "commit", revision);
            Assert.That(read.Success, Is.True, read.Message);
            return bytes.ToArray();
        }

        private static string Line(byte[] commit, string key) =>
            Latin1.GetString(commit).Split('\n').First(line => line.StartsWith(key + " ", StringComparison.Ordinal));

        private static GitCommandResult Rename(UnitGitTestRepo repo, string commit, bool commitTree)
        {
            UnitGitService.ForceCommitTreeRewrite = commitTree;
            try
            {
                repo.Git.ReadHeadState(out UnitGitHeadState head);
                return repo.Git.RenameCommit(commit, "renamed", head);
            }
            finally
            {
                UnitGitService.ForceCommitTreeRewrite = false;
            }
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
                foreach (bool commitTree in new[] { false, true })
                {
                    using (var repo = History(out string first))
                    {
                        string top = Make(repo, author.author, Encoding.UTF8.GetBytes("top\n"));
                        byte[] before = Stored(repo, top);
                        GitCommandResult result = Rename(repo, first, commitTree);
                        string at = author.what + (commitTree ? " through commit-tree" : " through fast-import");
                        if (result.Success)
                        {
                            byte[] after = Stored(repo, "HEAD");
                            Assert.That(Latin1.GetBytes(Line(after, "author")), Is.EqualTo(Latin1.GetBytes(Line(before, "author"))), at);
                            Assert.That(UnitGitService.CopyDifference(before, after, new[] { repo.Run("rev-parse", "HEAD~1").Trim() },
                                repo.Run("rev-parse", "HEAD^{tree}").Trim(), null), Is.Null, at);
                        }
                        else
                        {
                            Assert.That(result.Message, Does.Contain("left the history as it is"), at);
                            Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top), at + ": nothing moved");
                        }
                        // fast-import writes every one of them as stored but the empty name: it writes "author  <e-mail>", with
                        // the space a name would be followed by, and the check of the written copies refuses the rewrite.
                        if (!commitTree && author.what != "an empty name") Assert.That(result.Success, Is.True, at + ": " + result.Message);
                        if (!commitTree && author.what == "an empty name") Assert.That(result.Message, Does.Contain("different author line"), at);
                        Assert.That(repo.Run("for-each-ref", "refs/mixer-git/").Trim(), Is.Empty, at);
                    }
                }
            }
        }

        [Test]
        public void ACopyWrittenDifferentlyStopsTheRewriteBeforeAnyRefMoves()
        {
            foreach (bool commitTree in new[] { false, true })
            {
                using (var repo = History(out string first))
                {
                    string top = Make(repo, Ascii("Plain <p@example.test>"), Encoding.UTF8.GetBytes("top\n"));
                    UnitGitService.WrittenMessageSeamForTests = (hash, message) => hash == top ? Encoding.UTF8.GetBytes("tampered\n") : message;
                    GitCommandResult result;
                    try { result = Rename(repo, first, commitTree); }
                    finally { UnitGitService.WrittenMessageSeamForTests = null; }
                    Assert.That(result.Success, Is.False);
                    Assert.That(result.Message, Does.Contain(top.Substring(0, 7)).And.Contain("different message").And.Contain("moved nothing"));
                    Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top), "No ref moved.");
                    Assert.That(repo.Run("for-each-ref", "refs/mixer-git/").Trim(), Is.Empty);
                }
            }
        }

        [Test]
        public void TheCommitterIsRegeneratedOnPurpose()
        {
            var saved = new[] { "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" }.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Rewriter");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "rewriter@example.test");
            try
            {
                foreach (bool commitTree in new[] { false, true })
                {
                    using (var repo = History(out string first))
                    {
                        string top = Make(repo, Ascii("Plain <p@example.test>"), Encoding.UTF8.GetBytes("top\n"));
                        Assert.That(Rename(repo, first, commitTree).Success, Is.True);
                        // A rewritten commit is a new commit: whoever rewrote it, now (as on master). Its author stays.
                        Assert.That(Line(Stored(repo, "HEAD"), "committer"), Does.StartWith("committer Rewriter <rewriter@example.test> "));
                        Assert.That(Line(Stored(repo, "HEAD"), "author"), Is.EqualTo(Line(Stored(repo, top), "author")));
                    }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", saved[0]);
                Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", saved[1]);
            }
        }
    }
}
