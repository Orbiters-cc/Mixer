using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // A commit that is only re-parented by a rename or squash keeps its encoding header, its author line and its message
    // exactly as stored (UTF-8 named or not, Latin-1, leading blank lines, CRLF, no final line break), through fast-import
    // and through commit-tree; where commit-tree cannot write a commit as stored, the rewrite is refused instead.
    public sealed class UnitGitRewriteEncodingTests
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static string Make(UnitGitTestRepo repo, byte[] author, string encoding, byte[] message)
        {
            string tree = repo.Run("rev-parse", "HEAD^{tree}").Trim();
            string parent = repo.Run("rev-parse", "HEAD").Trim();
            var bytes = new MemoryStream();
            void Put(byte[] part) => bytes.Write(part, 0, part.Length);
            Put(Ascii("tree " + tree + "\nparent " + parent + "\nauthor "));
            Put(author);
            Put(Ascii(" 1600000000 +0200\ncommitter Someone <someone@example.test> 1600000100 +0200\n"));
            if (encoding != null) Put(Ascii("encoding " + encoding + "\n"));
            Put(Ascii("\n"));
            Put(message);
            GitCommandResult made = repo.Git.RunGitWithInput(60000, bytes.ToArray(), "hash-object", "-t", "commit", "-w", "--stdin");
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

        // Everything a re-parented copy must keep: the stored object without its parent and committer lines.
        private static byte[] Kept(byte[] commit)
        {
            int end = 0;
            while (end + 1 < commit.Length && !(commit[end] == '\n' && commit[end + 1] == '\n')) end++;
            var kept = new MemoryStream();
            int line = 0;
            while (line <= end)
            {
                int next = Array.IndexOf(commit, (byte)'\n', line);
                string start = Encoding.ASCII.GetString(commit, line, Math.Min(10, next - line));
                if (!start.StartsWith("parent ", StringComparison.Ordinal) && !start.StartsWith("committer ", StringComparison.Ordinal))
                    kept.Write(commit, line, next + 1 - line);
                line = next + 1;
            }
            kept.Write(commit, end + 1, commit.Length - end - 1);
            return kept.ToArray();
        }

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

        private static readonly byte[] Utf8Message = Encoding.UTF8.GetBytes("\n\nleading blank lines, Zoé\r\nCRLF line\r\nno final line break");
        private static readonly byte[] Latin1Message = Latin1.GetBytes("\nété en Latin-1\r\nno final line break");

        [Test]
        public void FastImportCopiesEveryEncodingAsStored()
        {
            using (var repo = History(out string first))
            {
                string named = Make(repo, Encoding.UTF8.GetBytes("Zoé <z@example.test>"), "UTF-8", Utf8Message);
                string latin = Make(repo, Latin1.GetBytes("René <r@example.test>"), "ISO-8859-1", Latin1Message);
                string plain = Make(repo, Ascii("Plain <p@example.test>"), null, Encoding.UTF8.GetBytes("\r\n\r\nplain, CRLF"));
                var before = new[] { named, latin, plain }.Select(hash => Kept(Stored(repo, hash))).ToList();

                GitCommandResult result = Rename(repo, first, false);
                Assert.That(result.Success, Is.True, result.Message);
                var after = new[] { "HEAD~2", "HEAD~1", "HEAD" }.Select(revision => Kept(Stored(repo, revision))).ToList();
                for (int i = 0; i < before.Count; i++)
                    Assert.That(after[i], Is.EqualTo(before[i]), "Commit " + i + " is copied byte for byte (but its parent and committer).");
                Assert.That(repo.Run("rev-parse", "HEAD~2^").Trim(), Is.Not.EqualTo(named), "It was re-parented.");
            }
        }

        [Test]
        public void CommitTreeCopiesWhatItCanAndRefusesTheRest()
        {
            // Another encoding with an ASCII author: commit-tree writes the header from i18n.commitEncoding, the bytes as they are.
            using (var repo = History(out string first))
            {
                string latin = Make(repo, Ascii("Rene <r@example.test>"), "ISO-8859-1", Latin1Message);
                string plain = Make(repo, Ascii("Plain <p@example.test>"), null, Encoding.UTF8.GetBytes("\r\n\r\nplain, CRLF"));
                var before = new[] { latin, plain }.Select(hash => Kept(Stored(repo, hash))).ToList();
                GitCommandResult result = Rename(repo, first, true);
                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(Kept(Stored(repo, "HEAD~1")), Is.EqualTo(before[0]));
                Assert.That(Kept(Stored(repo, "HEAD")), Is.EqualTo(before[1]));
            }

            // What commit-tree cannot write as stored: refused, nothing moves.
            var cannot = new List<(byte[] author, string encoding, byte[] message)>
            {
                (Ascii("Named <n@example.test>"), "UTF-8", Utf8Message),                       // no header for a UTF-8 name
                (Latin1.GetBytes("René <r@example.test>"), "ISO-8859-1", Latin1Message),  // an author in Latin-1
                (Ascii("Bare <b@example.test>"), null, Latin1Message),                         // not UTF-8, no encoding named
            };
            foreach (var commit in cannot)
            {
                using (var repo = History(out string first))
                {
                    string top = Make(repo, commit.author, commit.encoding, commit.message);
                    GitCommandResult result = Rename(repo, first, true);
                    Assert.That(result.Success, Is.False, commit.encoding ?? "none");
                    Assert.That(result.Message, Does.Contain("left the history as it is"));
                    Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(top));

                    // fast-import copies the same commit as stored.
                    byte[] before = Kept(Stored(repo, top));
                    GitCommandResult streamed = Rename(repo, first, false);
                    Assert.That(streamed.Success, Is.True, streamed.Message);
                    Assert.That(Kept(Stored(repo, "HEAD")), Is.EqualTo(before));
                }
            }
        }
    }
}
