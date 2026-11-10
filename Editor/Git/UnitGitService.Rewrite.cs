using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor
{
    // Renaming and squashing commits of the checked-out branch without touching the index or the working tree.
    internal sealed partial class UnitGitService
    {
        private sealed class CommitRewriteInfo
        {
            public string Hash = string.Empty;
            public string TreeHash = string.Empty;
            public List<string> Parents = new List<string>();
            public string AuthorName = string.Empty;
            public string AuthorEmail = string.Empty;
            public string AuthorDate = string.Empty;
            public string Message = string.Empty;
            // The message's bytes as stored, for a commit that is only re-parented; null when the commit names another
            // encoding (its message is then re-encoded to UTF-8 as read).
            public byte[] RawMessage;
            // The first header a rebuild would lose (a signature, a merged tag, anything but tree, parent, author,
            // committer and encoding); null when there is none.
            public string LostHeader;
        }

        internal GitCommandResult RenameCommit(string commitHash, string message, UnitGitHeadState expected)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RewriteHeadHistory(new[] { commitHash.Trim() }, message, false, expected);
        }

        internal GitCommandResult SquashCommits(IList<string> commitHashes, string message, UnitGitHeadState expected)
        {
            if (commitHashes == null || commitHashes.Count < 2)
            {
                return Failure("Select at least two commits to squash.");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RewriteHeadHistory(commitHashes, message, true, expected);
        }

        private GitCommandResult RewriteHeadHistory(IList<string> commitHashes, string message, bool squash, UnitGitHeadState expected)
        {
            GitCommandResult branchResult = RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            if (!branchResult.Success)
                return Failure("Check out a branch before rewriting history.");
            string branchRef = FirstNonEmptyLine(branchResult.StandardOutput);
            // Only the confirmed branch may be rewritten. Commits added to it meanwhile are rewritten
            // with the rest, and update-ref below refuses if it moves while the rewrite runs.
            GitCommandResult changed = CheckHeadUnchanged(squash ? "Squash" : "Rename", expected,
                new UnitGitHeadState { BranchRef = branchRef }, false);
            if (changed != null)
                return changed;
            GitCommandResult headResult = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", branchRef);
            if (!headResult.Success)
            {
                return headResult;
            }

            string oldHead = FirstNonEmptyLine(headResult.StandardOutput);
            GitCommandResult historyResult = RunGit(DefaultTimeoutMilliseconds, "rev-list", "--topo-order", "--reverse", oldHead);
            if (!historyResult.Success)
            {
                return historyResult;
            }

            List<string> history = SplitLines(historyResult.StandardOutput)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (history.Count == 0)
            {
                return Failure("No commits were found on HEAD.");
            }

            var historySet = new HashSet<string>(history, StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string hash in commitHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)))
            {
                GitCommandResult resolved = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", hash.Trim() + "^{commit}");
                if (!resolved.Success)
                {
                    return resolved;
                }

                string resolvedHash = FirstNonEmptyLine(resolved.StandardOutput);
                if (!historySet.Contains(resolvedHash))
                {
                    return Failure("Commit " + ShortHash(resolvedHash) + " is not reachable from the current HEAD.");
                }

                selected.Add(resolvedHash);
            }

            if (!squash && selected.Count != 1)
            {
                return Failure("Select exactly one commit to rename.");
            }

            if (squash && selected.Count < 2)
            {
                return Failure("Select at least two commits to squash.");
            }

            int startIndex = history.FindIndex(hash => selected.Contains(hash));
            int endIndex = history.FindLastIndex(hash => selected.Contains(hash));
            if (startIndex < 0 || endIndex < startIndex)
            {
                return Failure("Selected commits were not found in HEAD history.");
            }

            if (squash)
            {
                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (!selected.Contains(history[i]))
                    {
                        return Failure("Selected commits must be contiguous in the current HEAD history to squash.");
                    }
                }
            }

            // Every commit from the oldest selected one to HEAD in one read, instead of two reads per commit.
            var infos = new Dictionary<string, CommitRewriteInfo>(StringComparer.Ordinal);
            GitCommandResult infosResult = ReadRewriteInfos(history.GetRange(startIndex, history.Count - startIndex), infos);
            if (!infosResult.Success)
            {
                return infosResult;
            }

            if (squash)
            {
                GitCommandResult topology = CheckSquashTopology(history, startIndex, endIndex, selected, infos);
                if (!topology.Success)
                {
                    return topology;
                }
            }

            // Before anything is written: every commit this rewrite replaces (the selected ones, and every one above a
            // replaced parent) must be one a rebuild reproduces whole. A signature or a merged tag cannot be: it would be
            // dropped from each, silently.
            var replaced = new List<string>();
            var replacedSet = new HashSet<string>(StringComparer.Ordinal);
            for (int i = startIndex; i < history.Count; i++)
            {
                string hash = history[i];
                if (selected.Contains(hash) || infos[hash].Parents.Any(replacedSet.Contains))
                {
                    replaced.Add(hash);
                    replacedSet.Add(hash);
                }
            }

            GitCommandResult raw = ReadRawCommits(replaced, infos);
            if (!raw.Success)
            {
                return raw;
            }

            string carrying = replaced.FirstOrDefault(hash => infos[hash].LostHeader != null);
            if (carrying != null)
            {
                string header = infos[carrying].LostHeader;
                string what = header.StartsWith("gpgsig", StringComparison.Ordinal) ? "a signature"
                    : header == "mergetag" ? "a merged tag" : "a \"" + header + "\" header";
                return Failure("Commit " + ShortHash(carrying) + " carries " + what + ", which its rewritten copy could not keep: " + (squash ? "squashing" : "renaming") +
                               " would drop it. Mixer Git left the history as it is.");
            }

            // All new commits through one git fast-import (a constant number of processes), else one commit-tree each.
            var stream = new CommitStream();
            GitCommandResult streamable = CanStreamRewrite(infos.Values, stream);
            if (!streamable.Success)
            {
                return streamable;
            }

            var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
            string squashedHash = string.Empty;
            for (int i = startIndex; i < history.Count; i++)
            {
                string originalHash = history[i];
                if (squash && i > startIndex && i <= endIndex)
                {
                    rewritten[originalHash] = squashedHash;
                    continue;
                }

                CommitRewriteInfo source = infos[originalHash];
                List<string> parents = RewriteParents(source.Parents, rewritten);
                if (!selected.Contains(originalHash) && parents.SequenceEqual(source.Parents, StringComparer.Ordinal))
                {
                    // Nothing below this commit changed (e.g. a side branch merged later): keep it
                    // instead of creating a copy that would diverge from the branch that owns it.
                    rewritten[originalHash] = originalHash;
                    continue;
                }

                string treeHash = squash && i == startIndex
                    ? infos[history[endIndex]].TreeHash
                    : source.TreeHash;
                bool edited = squash ? i == startIndex : selected.Contains(originalHash);
                string commitMessage = edited ? message : source.Message;
                // A commit that is only re-parented keeps its message byte for byte.
                byte[] rawMessage = edited ? null : source.RawMessage;

                string newHash;
                if (stream.Enabled)
                {
                    newHash = stream.Add(source, treeHash, parents, commitMessage, rawMessage);
                }
                else
                {
                    GitCommandResult createResult = CreateCommitFromTree(source, treeHash, parents, commitMessage, rawMessage);
                    if (!createResult.Success)
                    {
                        return createResult;
                    }

                    newHash = FirstNonEmptyLine(createResult.StandardOutput);
                }

                if (string.IsNullOrWhiteSpace(newHash))
                {
                    return Failure("Git did not return a rewritten commit hash.");
                }

                rewritten[originalHash] = newHash;
                if (squash && i == startIndex)
                {
                    squashedHash = newHash;
                }
            }

            if (!rewritten.TryGetValue(oldHead, out string newHead) || string.IsNullOrWhiteSpace(newHead))
            {
                return Failure("Could not resolve the rewritten HEAD.");
            }

            // HEAD is the last commit of the history, so when it was rewritten it is the stream's last commit.
            string streamRef = null;
            if (stream.Enabled && newHead.StartsWith(":", StringComparison.Ordinal))
            {
                GitCommandResult imported = WriteCommitStream(stream, out streamRef, out newHead);
                if (!imported.Success)
                {
                    RunGit(DefaultTimeoutMilliseconds, "update-ref", "--no-deref", "-d", streamRef);
                    return imported;
                }
            }

            // Commit trees are unchanged. Move only the captured branch, and only if nobody advanced it.
            // Do not reset the index or whichever branch another process has since checked out.
            GitCommandResult updateResult = RunGit(DefaultTimeoutMilliseconds, "update-ref", "--no-deref",
                "-m", "Mixer Git: rewrite history", branchRef, newHead, oldHead);
            // The scratch ref fast-import wrote (this call named it): gone whether the branch moved or not; the new commits
            // are reachable from the branch, or left to Git's garbage collection.
            if (streamRef != null)
            {
                RunGit(DefaultTimeoutMilliseconds, "update-ref", "--no-deref", "-d", streamRef);
            }

            if (!updateResult.Success)
            {
                return updateResult;
            }

            updateResult.StandardOutput = "Rewrote " + branchRef + " " + ShortHash(oldHead) + " -> " + ShortHash(newHead) + ".";
            return updateResult;
        }

        /// <summary>
        /// The squashed commit gets the oldest selected commit's parents and the newest one's tree.
        /// Refuse selections where that would drop or re-parent history outside the selection.
        /// </summary>
        private static GitCommandResult CheckSquashTopology(IList<string> history, int startIndex, int endIndex,
            ICollection<string> selected, IDictionary<string, CommitRewriteInfo> infos)
        {
            string tip = history[endIndex];
            var reached = new HashSet<string>(StringComparer.Ordinal) { tip };
            var pending = new Stack<string>(reached);
            while (pending.Count > 0)
            {
                foreach (string parent in infos[pending.Pop()].Parents)
                {
                    if (selected.Contains(parent) && reached.Add(parent))
                        pending.Push(parent);
                }
            }

            string sideCommit = selected.FirstOrDefault(hash => !reached.Contains(hash));
            if (sideCommit != null)
            {
                return Failure("Selected commits must form a single line of history to squash: " + ShortHash(sideCommit) +
                               " is not an ancestor of " + ShortHash(tip) + ".");
            }

            List<string> baseParents = infos[history[startIndex]].Parents;
            for (int i = startIndex + 1; i <= endIndex; i++)
            {
                string merged = infos[history[i]].Parents.FirstOrDefault(parent => !selected.Contains(parent) && !baseParents.Contains(parent));
                if (merged != null)
                {
                    return Failure("Squashing would drop merged history: " + ShortHash(history[i]) + " merges " + ShortHash(merged) +
                                   ", which is not selected. Select the merged commits too, or leave the merge out of the selection.");
                }
            }

            for (int i = endIndex + 1; i < history.Count; i++)
            {
                string forkPoint = infos[history[i]].Parents.FirstOrDefault(parent => parent != tip && selected.Contains(parent));
                if (forkPoint != null)
                {
                    return Failure("Squashing would re-parent " + ShortHash(history[i]) + ": it branches off from selected commit " + ShortHash(forkPoint) +
                                   ". Select its commits too, or pick commits nothing else branches off from.");
                }
            }

            return new GitCommandResult();
        }

        // Tree, parents, author and whole message of each commit, in one "git log --no-walk --stdin" fed the hashes.
        private GitCommandResult ReadRewriteInfos(IList<string> hashes, IDictionary<string, CommitRewriteInfo> infos)
        {
            // The author date in Git's own form (seconds and offset): what fast-import and GIT_AUTHOR_DATE both take, exactly.
            string[] options = { "log", "--no-walk=unsorted", "-z", "--date=raw", "--format=%H%x1f%T%x1f%P%x1f%an%x1f%ae%x1f%ad%x1f%B" };
            GitCommandResult read = RunWithList(null, LongTimeoutMilliseconds, hashes, false, options.Concat(new[] { "--stdin" }).ToArray(), options, new[] { "--" });
            if (!read.Success)
            {
                return read;
            }

            foreach (string record in read.StandardOutput.Split('\0'))
            {
                if (record.Length == 0) continue;
                string[] fields = record.Split(new[] { FieldSeparator }, 7);
                if (fields.Length < 7)
                {
                    return Failure("Could not parse commit metadata for " + ShortHash(fields[0]) + ".");
                }

                infos[fields[0]] = new CommitRewriteInfo
                {
                    Hash = fields[0],
                    TreeHash = fields[1],
                    Parents = fields[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                    AuthorName = fields[3],
                    AuthorEmail = fields[4],
                    AuthorDate = fields[5],
                    Message = TrimTrailingNewlines(fields[6])
                };
            }

            string missing = hashes.FirstOrDefault(hash => !infos.ContainsKey(hash));
            return missing == null ? new GitCommandResult() : Failure("Could not read commit " + ShortHash(missing) + ".");
        }

        // One "git commit-tree" per rewritten commit (each needs its parent's new id); the message goes through stdin.
        private GitCommandResult CreateCommitFromTree(CommitRewriteInfo source, string treeHash, IList<string> parents, string message,
            byte[] rawMessage)
        {
            var args = new List<string> { "commit-tree", treeHash };
            foreach (string parent in parents)
            {
                args.Add("-p");
                args.Add(parent);
            }

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "GIT_AUTHOR_NAME", source.AuthorName },
                { "GIT_AUTHOR_EMAIL", source.AuthorEmail },
                { "GIT_AUTHOR_DATE", source.AuthorDate }
            };
            // commit-tree reads the message from stdin when no -m or -F is given; with stdin unusable, from a file.
            if (rawMessage == null)
            {
                return RunCommitWithMessageFile(args, message, environment);
            }

            // The stored message as it is: commit-tree keeps what it is given.
            if (UnitGitRunner.CleanInput)
            {
                args.Add("-F");
                args.Add("-");
                return RunGitWithInput(environment, LongTimeoutMilliseconds, rawMessage, args.ToArray());
            }

            string file = Path.Combine(Path.GetTempPath(), "mixer-git-message-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllBytes(file, rawMessage);
                args.Add("-F");
                args.Add(file);
                return RunGitWithEnvironment(environment, LongTimeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(file);
            }
        }

        private static readonly HashSet<string> RebuiltHeaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "tree", "parent", "author", "committer", "encoding"
        };

        // Each commit's stored bytes, through the kept-alive cat-file (else a cat-file of its own): its headers, and its
        // message as stored.
        private GitCommandResult ReadRawCommits(IList<string> hashes, IDictionary<string, CommitRewriteInfo> infos)
        {
            UnitGitObjects objects = UnitGitObjects.For(ProjectRoot);
            var bytes = new MemoryStream();
            foreach (string hash in hashes)
            {
                bytes.SetLength(0);
                if (objects.Read(hash, bytes, out string type, out _) != UnitGitObjects.Answer.Found || type != "commit")
                {
                    bytes.SetLength(0);
                    GitCommandResult read = ReadGitStream(DefaultTimeoutMilliseconds, (chunk, offset, count) =>
                    {
                        bytes.Write(chunk, offset, count);
                        return true;
                    }, "cat-file", "commit", hash);
                    if (!read.Success)
                    {
                        return read.StandardError.Length > 0 ? read : Failure("Could not read commit " + ShortHash(hash) + ".");
                    }
                }

                ParseRawCommit(bytes.ToArray(), infos[hash]);
            }

            return new GitCommandResult();
        }

        // Headers end at the first empty line; a line starting with a space continues the header above (a signature).
        internal static void ParseRawCommitForTests(byte[] commit, out string lostHeader, out byte[] rawMessage)
        {
            var info = new CommitRewriteInfo();
            ParseRawCommit(commit, info);
            lostHeader = info.LostHeader;
            rawMessage = info.RawMessage;
        }

        private static void ParseRawCommit(byte[] commit, CommitRewriteInfo info)
        {
            int end = 0;
            while (end < commit.Length && !(commit[end] == (byte)'\n' && (end + 1 >= commit.Length || commit[end + 1] == (byte)'\n'))) end++;
            int headerLength = Math.Min(end, commit.Length);
            bool encoded = false;
            info.LostHeader = null;
            int line = 0;
            while (line < headerLength)
            {
                int next = Array.IndexOf(commit, (byte)'\n', line, headerLength - line);
                if (next < 0) next = headerLength;
                if (commit[line] != (byte)' ')
                {
                    int space = Array.IndexOf(commit, (byte)' ', line, next - line);
                    string key = Encoding.ASCII.GetString(commit, line, (space < 0 ? next : space) - line);
                    if (key == "encoding") encoded = true;
                    if (!RebuiltHeaders.Contains(key) && info.LostHeader == null) info.LostHeader = key;
                }
                line = next + 1;
            }

            int messageStart = Math.Min(commit.Length, end + 2);
            info.RawMessage = encoded ? null : commit.Skip(messageStart).ToArray();
        }

        /// <summary>
        /// The new commits of one rewrite as a git fast-import stream: each commit is "the tree with this id" (deleteall,
        /// then the tree at the root path, which fast-import takes in Git 2.26) with the author, message and parents it would
        /// get from commit-tree, and the committer commit-tree would write (Git's GIT_COMMITTER_IDENT, read once). Parents
        /// are object ids or the marks of commits earlier in the stream.
        /// </summary>
        private sealed class CommitStream
        {
            internal bool Enabled;
            internal string Committer = string.Empty;
            internal readonly MemoryStream Bytes = new MemoryStream();
            private int marks;
            private string reference = string.Empty;

            internal string Reference
            {
                get
                {
                    if (reference.Length == 0) reference = "refs/mixer-git/rewrite/" + Guid.NewGuid().ToString("N");
                    return reference;
                }
            }

            internal string Add(CommitRewriteInfo source, string treeHash, IList<string> parents, string message, byte[] rawMessage)
            {
                string mark = ":" + (++marks).ToString(CultureInfo.InvariantCulture);
                // A commit with no parent on a ref that already has one in this stream would continue from it: reset first.
                if (parents.Count == 0) Text("reset " + Reference + "\n");
                Text("commit " + Reference + "\nmark " + mark + "\n");
                Text("author " + (source.AuthorName.Length > 0 ? source.AuthorName + " " : string.Empty) + "<" + source.AuthorEmail + "> " +
                     source.AuthorDate + "\n");
                Text("committer " + Committer + "\n");
                // Exactly the bytes commit-tree is given on stdin.
                byte[] body = rawMessage ?? UnitGitRunner.Utf8.GetBytes(NormalizeCommitMessage(message) + "\n");
                Text("data " + body.Length.ToString(CultureInfo.InvariantCulture) + "\n");
                Bytes.Write(body, 0, body.Length);
                Text("\n");
                for (int i = 0; i < parents.Count; i++) Text((i == 0 ? "from " : "merge ") + parents[i] + "\n");
                Text("deleteall\n");
                if (treeHash != EmptyTreeSha1 && treeHash != EmptyTreeSha256) Text("M 040000 " + treeHash + " \"\"\n");
                Text("\n");
                return mark;
            }

            internal void Text(string text)
            {
                byte[] bytes = UnitGitRunner.Utf8.GetBytes(text);
                Bytes.Write(bytes, 0, bytes.Length);
            }
        }

        private const string EmptyTreeSha1 = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        private const string EmptyTreeSha256 = "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";

        /// <summary>For tests: rewrite with one commit-tree per commit, as a repository that signs its commits does.</summary>
        internal static bool ForceCommitTreeRewrite;

        /// <summary>
        /// Whether this rewrite can go through fast-import, which writes what commit-tree would except a signature: a
        /// repository that signs its commits (commit.gpgSign) keeps commit-tree, which signs; so does a system whose stdin
        /// cannot be clean, and an author whose name or e-mail fast-import cannot carry. Fails only when Git cannot name a
        /// committer (commit-tree would fail as well).
        /// </summary>
        private GitCommandResult CanStreamRewrite(IEnumerable<CommitRewriteInfo> sources, CommitStream stream)
        {
            stream.Enabled = false;
            if (ForceCommitTreeRewrite || !UnitGitRunner.CleanInput ||
                sources.Any(source => !FitsIdent(source.AuthorName) || !FitsIdent(source.AuthorEmail) || !FitsIdent(source.AuthorDate)))
            {
                return new GitCommandResult();
            }

            GitCommandResult signing = RunGit(DefaultTimeoutMilliseconds, "config", "--bool", "--get", "commit.gpgSign");
            if (signing.Success && FirstNonEmptyLine(signing.StandardOutput) == "true")
            {
                return new GitCommandResult();
            }

            GitCommandResult ident = RunGit(DefaultTimeoutMilliseconds, "var", "GIT_COMMITTER_IDENT");
            if (!ident.Success)
            {
                return ident;
            }

            stream.Committer = FirstNonEmptyLine(ident.StandardOutput);
            stream.Enabled = stream.Committer.Length > 0;
            return new GitCommandResult();
        }

        // What fast-import keeps as it is and commit-tree would write the same: nothing Git's ident sanitising changes (it
        // drops "crud", control characters, spaces and . , : ; < > " \ ', from both ends of a name and an e-mail, and an
        // empty name fails). Anything else goes through commit-tree, whose result is then Git's own.
        private static bool FitsIdent(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') >= 0 || value.IndexOf('>') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\0') >= 0)
                return false;
            return !IsCrud(value[0]) && !IsCrud(value[value.Length - 1]);
        }

        private static bool IsCrud(char c) =>
            c <= ' ' || c == '.' || c == ',' || c == ':' || c == ';' || c == '<' || c == '>' || c == '"' || c == '\\' || c == '\'';

        // Runs the stream into a scratch ref this call named, and reads the id of its last commit (the new HEAD).
        private GitCommandResult WriteCommitStream(CommitStream stream, out string streamRef, out string newHead)
        {
            streamRef = stream.Reference;
            newHead = string.Empty;
            stream.Text("done\n");
            GitCommandResult imported = RunGitWithInput(LongTimeoutMilliseconds, stream.Bytes.ToArray(), "fast-import", "--quiet", "--done");
            if (!imported.Success)
            {
                return imported;
            }

            GitCommandResult head = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", streamRef + "^{commit}");
            if (!head.Success)
            {
                return head;
            }

            newHead = FirstNonEmptyLine(head.StandardOutput);
            return string.IsNullOrWhiteSpace(newHead) ? Failure("Git did not return a rewritten commit hash.") : new GitCommandResult();
        }

        private static List<string> RewriteParents(IEnumerable<string> parents, IDictionary<string, string> rewritten)
        {
            var result = new List<string>();
            foreach (string parent in parents)
            {
                string next = rewritten.TryGetValue(parent, out string rewrittenParent)
                    ? rewrittenParent
                    : parent;
                if (!result.Contains(next))
                {
                    result.Add(next);
                }
            }

            return result;
        }
    }
}
