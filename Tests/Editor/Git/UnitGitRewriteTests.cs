using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Renaming and squashing read every commit up to HEAD in one go and write each new commit with one process.
    public sealed class UnitGitRewriteTests
    {
        [Test]
        public void RenameReadsTheHistoryOnceAndKeepsEveryOtherCommitExact()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                var messages = new[]
                {
                    "first", "Tête « é » 日本語\n\nBody with -dash and \\backslash.", "third\n\nUnitGit-Release: r1",
                    "fourth line one\n\nparagraph two\n\nparagraph three", "fifth"
                };
                var hashes = new List<string>();
                for (int i = 0; i < messages.Length; i++)
                {
                    repo.Write("Assets/f" + i + ".txt", i + "\n");
                    repo.Run("add", "-A");
                    var environment = new Dictionary<string, string>
                    {
                        { "GIT_AUTHOR_NAME", "Author " + i }, { "GIT_AUTHOR_EMAIL", "a" + i + "@example.test" },
                        { "GIT_AUTHOR_DATE", "2024-0" + (i + 1) + "-02T03:04:05+02:00" }
                    };
                    Assert.That(repo.Git.RunGitWithEnvironment(environment, 60000, "commit", "-q", "-m", messages[i]).Success, Is.True);
                    hashes.Add(repo.Run("rev-parse", "HEAD").Trim());
                }

                string Field(string format, string revision) => repo.Run("log", "-1", "--format=" + format, revision).TrimEnd('\n');
                var before = Enumerable.Range(2, 3).Select(i => Field("%an|%ae|%aI|%B", hashes[i])).ToList();

                var commands = new List<string>();
                repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
                repo.Git.ReadHeadState(out UnitGitHeadState head);
                GitCommandResult result = repo.Git.RenameCommit(hashes[1], "renamed second", head);
                repo.Git.ProcessLogReceived = null;
                Assert.That(result.Success, Is.True, result.Message);

                Assert.That(commands.Count(line => line.Contains(" commit-tree ")), Is.EqualTo(4), "The renamed commit and the three after it.");
                Assert.That(commands.Count(line => line.Contains(" show -s") || line.Contains(" log -1 ")), Is.Zero, "No read per commit.");
                Assert.That(Field("%B", "HEAD~3"), Is.EqualTo("renamed second"));
                Assert.That(Field("%H", "HEAD~4"), Is.EqualTo(hashes[0]), "Commits below the renamed one are kept.");
                var after = new[] { "HEAD~2", "HEAD~1", "HEAD" }.Select(revision => Field("%an|%ae|%aI|%B", revision)).ToList();
                Assert.That(after, Is.EqualTo(before), "Author, date and message of later commits are unchanged.");
                Assert.That(Field("%T", "HEAD"), Is.EqualTo(Field("%T", hashes[4])));
            }
        }

        [Test]
        public void SquashKeepsTheNewestTreeAndTheCommitsAfterIt()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                var hashes = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    repo.Write("Assets/f.txt", i + "\n");
                    hashes.Add(repo.Commit("c" + i));
                }

                repo.Git.ReadHeadState(out UnitGitHeadState head);
                GitCommandResult result = repo.Git.SquashCommits(new[] { hashes[1], hashes[2] }, "squashed é", head);
                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(repo.Run("log", "--format=%s").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "c3", "squashed é", "c0" }));
                Assert.That(repo.Run("rev-parse", "HEAD~1^{tree}").Trim(), Is.EqualTo(repo.Run("rev-parse", hashes[2] + "^{tree}").Trim()));
            }
        }
    }
}
