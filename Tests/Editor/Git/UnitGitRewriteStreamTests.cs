using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Rename and squash write their new commits through one git fast-import, on histories with merges, a second root, an
    // empty tree and odd messages: each succeeds, uses one fast-import, leaves no scratch ref, and with the committer fixed
    // the same rewrite gives the same commit ids again (nothing depends on time or order but what Git records).
    public sealed class UnitGitRewriteStreamTests
    {
        private static readonly string[] Committer = { "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE" };

        [Test]
        public void RewritesOfAnyHistorySucceedAndAreDeterministic()
        {
            var saved = Committer.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Rewriter Ü");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "rewriter@example.test");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "1700000000 +0530");
            try
            {
                using (var repo = UnitGitTestRepo.Create())
                {
                    // main: c0 - c1 - c2 - merge(side) - c4 - c5; side: an empty-tree root s0 - s1 (a second root of HEAD).
                    var commit = new Dictionary<string, string>();
                    void Make(string name, string message, string author, string date)
                    {
                        repo.Write("Assets/" + name + ".txt", name + "\n");
                        repo.Run("add", "-A");
                        var environment = new Dictionary<string, string>
                        {
                            { "GIT_AUTHOR_NAME", author }, { "GIT_AUTHOR_EMAIL", name + "@example.test" }, { "GIT_AUTHOR_DATE", date }
                        };
                        Assert.That(repo.Git.RunGitWithEnvironment(GitAccess.Exclusive, environment, 60000, "commit", "-q", "--cleanup=verbatim", "-m", message).Success, Is.True);
                        commit[name] = repo.Run("rev-parse", "HEAD").Trim();
                    }

                    Make("c0", "root", "Zoé 日本", "1600000000 -0730");
                    Make("c1", "Tête « é »\n\nBody with -dash, \\backslash, \"quotes\"\nand a line", "A", "1600000100 +0000");
                    Make("c2", "two\n\nUnitGit-Release: r1", "B", "1600000200 +1345");
                    repo.Run("checkout", "-q", "--orphan", "side");
                    repo.Run("rm", "-rq", "--cached", ".");
                    Assert.That(repo.Git.RunGit(GitAccess.Exclusive, 60000, "commit", "-q", "--allow-empty", "-m", "empty root").Success, Is.True);
                    commit["s0"] = repo.Run("rev-parse", "HEAD").Trim();
                    repo.Run("clean", "-fdxq");
                    Make("s1", "side work", "S", "1600000300 +0100");
                    repo.Run("checkout", "-q", "main");
                    repo.Run("merge", "-q", "--no-ff", "--allow-unrelated-histories", "-m", "merge side", "side");
                    commit["m"] = repo.Run("rev-parse", "HEAD").Trim();
                    Make("c4", "four", "C", "1600000400 +0200");
                    Make("c5", "five", "D", "1600000500 +0200");

                    Same(repo, head => repo.Git.RenameCommit(commit["c1"], "renamed ✓\n\nnew body", head));
                    Same(repo, head => repo.Git.RenameCommit(commit["c0"], "renamed root", head));
                    Same(repo, head => repo.Git.RenameCommit(commit["s0"], "renamed second root", head));
                    Same(repo, head => repo.Git.SquashCommits(new[] { commit["c4"], commit["c5"] }, "squashed", head));
                    Same(repo, head => repo.Git.SquashCommits(new[] { commit["c1"], commit["c2"] }, "squashed below a merge", head));
                }
            }
            finally
            {
                for (int i = 0; i < Committer.Length; i++) Environment.SetEnvironmentVariable(Committer[i], saved[i]);
            }
        }

        // Runs the rewrite through fast-import, puts the branch back, runs it through commit-tree, and compares.
        private static void Same(UnitGitTestRepo repo, Func<UnitGitHeadState, GitCommandResult> rewrite)
        {
            string before = repo.Run("rev-parse", "HEAD").Trim();
            var commands = new List<string>();
            repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
            repo.Git.ReadHeadState(out UnitGitHeadState head);
            GitCommandResult streamed = rewrite(head);
            repo.Git.ProcessLogReceived = null;
            Assert.That(streamed.Success, Is.True, streamed.Message);
            Assert.That(commands.Count(line => line.Contains(" fast-import ")), Is.EqualTo(1));
            Assert.That(commands.Count(line => line.Contains(" commit-tree ")), Is.Zero);
            string fast = repo.Run("rev-parse", "HEAD").Trim();
            Assert.That(fast, Is.Not.EqualTo(before));
            Assert.That(repo.Run("for-each-ref", "refs/mixer-git/").Trim(), Is.Empty, "The scratch ref is gone.");

            repo.Run("update-ref", "refs/heads/main", before, fast);
            repo.Git.ReadHeadState(out head);
            GitCommandResult again = rewrite(head);
            Assert.That(again.Success, Is.True, again.Message);
            string slow = repo.Run("rev-parse", "HEAD").Trim();
            Assert.That(fast, Is.EqualTo(slow), "The same rewrite wrote different commits:\n" +
                                               repo.Run("cat-file", "-p", fast) + "\n---\n" + repo.Run("cat-file", "-p", slow));
            repo.Run("update-ref", "refs/heads/main", before, slow);
        }
    }
}
