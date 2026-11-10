using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Commit details for a whole selection in one read, and a commit file's diff from what the details already know: each
    // compared with the one-by-one reads they replace.
    public sealed class UnitGitCommitReadTests
    {
        [Test]
        public void ASelectionReadsInOneGoWhatOneByOneReadsGave()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Write("Assets/b é.txt", "b\n");
                string root = repo.Commit("root commit");
                repo.Run("mv", "Assets/a.txt", "Assets/-renamed a.txt");
                repo.Write("Assets/b é.txt", "b2\n");
                repo.Run("add", "-A");
                repo.Run("commit", "-q", "-m", "rename\n\nwith a body\nof two lines");
                string renamed = repo.Run("rev-parse", "HEAD").Trim();
                repo.Run("checkout", "-q", "-b", "side", root);
                repo.Write("Assets/side.txt", "s\n");
                string side = repo.Commit("side");
                repo.Run("checkout", "-q", "main");
                repo.Run("merge", "-q", "--no-edit", "side");
                string merge = repo.Run("rev-parse", "HEAD").Trim();
                repo.Run("commit", "-q", "--allow-empty", "-m", "empty");
                string empty = repo.Run("rev-parse", "HEAD").Trim();

                var hashes = new List<string> { empty, merge, side, renamed, root };
                Dictionary<string, UnitGitCommitDetails> together = repo.Git.GetCommitDetails(hashes);
                Assert.That(together.Keys, Is.EquivalentTo(hashes));
                foreach (string hash in hashes)
                {
                    UnitGitCommitDetails alone = repo.Git.GetCommitDetails(hash);
                    UnitGitCommitDetails fromList = together[hash];
                    Assert.That(Describe(fromList), Is.EqualTo(Describe(alone)), hash);
                }

                Assert.That(together[renamed].OriginalPaths["Assets/-renamed a.txt"], Is.EqualTo("Assets/a.txt"));
                Assert.That(together[renamed].Body, Is.EqualTo("with a body\nof two lines"));
            }
        }

        [Test]
        public void ACommitFileDiffWithTheDetailsHintIsTheSameDiff()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "one\ntwo\nthree\nfour\nfive\n");
                string root = repo.Commit("root");
                repo.Run("mv", "Assets/a.txt", "Assets/moved é.txt");
                repo.Write("Assets/moved é.txt", "one\ntwo\nTHREE\nfour\nfive\n");
                string moved = repo.Commit("move and edit");

                foreach (string hash in new[] { root, moved })
                {
                    UnitGitCommitDetails details = repo.Git.GetCommitDetails(hash);
                    foreach (string file in details.ChangedFiles)
                    {
                        var hint = new UnitGitCommitFileHint
                        {
                            ShortHash = details.Commit.ShortHash,
                            Parents = details.Commit.Parents,
                            PreviousPath = details.OriginalPaths.TryGetValue(file, out string previous) ? previous : string.Empty
                        };
                        UnitGitDiff withHint = repo.Git.GetCommitFileDiff(hash, file, UnitGitWhitespace.None, hint);
                        UnitGitDiff without = repo.Git.GetCommitFileDiff(hash, file, UnitGitWhitespace.None);
                        Assert.That(withHint.LeftTitle + " | " + withHint.RightTitle, Is.EqualTo(without.LeftTitle + " | " + without.RightTitle));
                        Assert.That(withHint.Lines.Select(line => line.Kind + ":" + line.Left + "|" + line.Right),
                            Is.EqualTo(without.Lines.Select(line => line.Kind + ":" + line.Left + "|" + line.Right)), hash + " " + file);
                    }
                }
            }
        }

        private static string Describe(UnitGitCommitDetails details)
        {
            return string.Join("\n", new[]
            {
                details.Commit.ShortHash, details.Commit.FullHash, details.Commit.Subject, details.Commit.AuthorName,
                details.Commit.AuthorEmail, details.Commit.Decorations, details.Commit.ReleaseId,
                string.Join(" ", details.Commit.Parents), details.Commit.Timestamp.ToString(), details.AuthorDate, details.CommitterName,
                details.CommitterEmail, details.CommitterDate, details.Body,
                string.Join(";", details.ChangedFiles.Select(file => details.FileStatus[file] + " " + file))
            });
        }
    }
}
