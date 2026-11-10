using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Rename and squash rebuild every commit above the one they change. What a rebuild cannot carry over (a signature, a
    // merged tag, any other extra header) makes them refuse instead of dropping it; what it can carry over (a re-parented
    // commit's message) is kept byte for byte, on the fast-import and the commit-tree path alike.
    public sealed class UnitGitRewriteHeaderTests
    {
        private const string Signature =
            "gpgsig -----BEGIN PGP SIGNATURE-----\n \n iQEzBAABCAAdFiEE0000000000000000000000000000000\n =abcd\n -----END PGP SIGNATURE-----\n";

        // A commit written by hand on top of HEAD (git commit would not write these headers or names), and the branch on it.
        private static string Raw(UnitGitTestRepo repo, string author, string extraHeaders, string message)
        {
            string tree = repo.Run("rev-parse", "HEAD^{tree}").Trim();
            string parent = repo.Run("rev-parse", "HEAD").Trim();
            string text = "tree " + tree + "\nparent " + parent + "\nauthor " + author + " 1600000000 +0200\n" +
                          "committer Someone <someone@example.test> 1600000100 +0200\n" + extraHeaders + "\n" + message;
            GitCommandResult made = repo.Git.RunGitWithInput(60000, Encoding.UTF8.GetBytes(text), "hash-object", "-t", "commit", "-w", "--stdin");
            Assert.That(made.Success, Is.True, made.Message);
            string hash = made.StandardOutput.Trim();
            repo.Run("update-ref", "refs/heads/main", hash, parent);
            return hash;
        }

        private static UnitGitTestRepo History(out string first, out string second)
        {
            if (!UnitGitRunner.CleanInput) Assert.Ignore("Stdin cannot be clean on this system.");
            var repo = UnitGitTestRepo.Create();
            repo.Write("Assets/a.txt", "a\n");
            first = repo.Commit("first");
            repo.Write("Assets/a.txt", "b\n");
            second = repo.Commit("second");
            return repo;
        }

        [Test]
        public void ExtraHeadersAreFoundAndMessagesKeptAsStored()
        {
            var commit = Encoding.UTF8.GetBytes("tree t\nparent p\nauthor A <a@b> 1 +0000\ncommitter C <c@d> 1 +0000\n" + Signature +
                                                "\n  spaced  \n\n tail   ");
            UnitGitService.ParseRawCommitForTests(commit, out string lost, out byte[] message, out string encoding);
            Assert.That(lost, Is.EqualTo("gpgsig"));
            Assert.That(Encoding.UTF8.GetString(message), Is.EqualTo("  spaced  \n\n tail   "));
            Assert.That(encoding, Is.Null);

            UnitGitService.ParseRawCommitForTests(Encoding.UTF8.GetBytes("tree t\nauthor A <a@b> 1 +0000\nencoding ISO-8859-1\n\nx"), out lost, out message, out encoding);
            Assert.That(lost, Is.Null, "An encoding header is copied with the commit.");
            Assert.That(Encoding.UTF8.GetString(message), Is.EqualTo("x"), "A message in another encoding is copied as its bytes.");
            Assert.That(encoding, Is.EqualTo("ISO-8859-1"));

            UnitGitService.ParseRawCommitForTests(Encoding.UTF8.GetBytes("tree t\nmergetag object x\n type commit\n\n"), out lost, out message, out encoding);
            Assert.That(lost, Is.EqualTo("mergetag"));
            Assert.That(message, Is.Empty);
        }

        [Test]
        public void RenameRefusesWhenACommitAboveCarriesASignatureOrAnUnknownHeader()
        {
            foreach (string header in new[] { Signature, "mergetag object 0000000000000000000000000000000000000000\n type commit\n", "x-reviewed-by Someone\n" })
            {
                using (var repo = History(out string first, out _))
                {
                    string top = Raw(repo, "Top <top@example.test>", header, "top\n");
                    repo.Git.ReadHeadState(out UnitGitHeadState head);
                    GitCommandResult result = repo.Git.RenameCommit(first, "renamed", head);
                    Assert.That(result.Success, Is.False, header);
                    Assert.That(result.Message, Does.Contain(top.Substring(0, 7)).And.Contain("left the history as it is"));
                    Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top), "Nothing moved.");

                    // The carrying commit itself is refused too.
                    repo.Git.ReadHeadState(out head);
                    Assert.That(repo.Git.RenameCommit(top, "renamed top", head).Success, Is.False);
                    Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top));
                }
            }
        }

        [Test]
        public void ReparentedCommitsKeepTheirMessageBytesAndOddAuthorsGoThroughCommitTree()
        {
            foreach (bool streamed in new[] { true, false })
            {
                using (var repo = History(out string first, out _))
                {
                    const string stored = "  spaced subject  \n\n body with trailing spaces   \n\n\n";
                    // A name with a space in front: Git's ident rules drop it, which only commit-tree reproduces.
                    Raw(repo, streamed ? "Top <top@example.test>" : " Spaced <top@example.test>", string.Empty, stored);
                    var commands = new List<string>();
                    repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
                    repo.Git.ReadHeadState(out UnitGitHeadState head);
                    GitCommandResult result = repo.Git.RenameCommit(first, "renamed", head);
                    repo.Git.ProcessLogReceived = null;
                    Assert.That(result.Success, Is.True, result.Message);
                    Assert.That(commands.Count(line => line.Contains(" fast-import ")), Is.EqualTo(streamed ? 1 : 0));
                    Assert.That(commands.Count(line => line.Contains(" commit-tree ")), Is.EqualTo(streamed ? 0 : 3));

                    string raw = repo.Run("cat-file", "commit", "HEAD");
                    Assert.That(raw.Substring(raw.IndexOf("\n\n", StringComparison.Ordinal) + 2), Is.EqualTo(stored), "The re-parented message, byte for byte.");
                    Assert.That(repo.Run("log", "-1", "--format=%B", "HEAD~2"), Does.StartWith("renamed"));
                    if (!streamed) Assert.That(repo.Run("log", "-1", "--format=%an", "HEAD").Trim(), Is.EqualTo("Spaced"), "Git's own ident rules.");
                }
            }
        }
    }
}
