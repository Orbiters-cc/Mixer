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
            // What a commit that is only re-parented keeps exactly as stored (nothing decoded): its message, its author
            // line (in a commit of another encoding it is in that encoding too) and its encoding header's value (null: none).
            public byte[] RawMessage;
            public byte[] RawAuthor;
            public byte[] RawEncoding;
            // The whole stored commit, to compare its rewritten copy with.
            public byte[] RawCommit;
            // The first header a rebuild would lose (a signature, a merged tag, anything but tree, parent, author,
            // committer and encoding); null when there is none.
            public string LostHeader;
        }

        internal GitCommandResult RenameCommit(string commitHash, string message, UnitGitHeadState expected)
        {
            // One operation: it holds the repository's lease from here to its end (see UnitGitLease).
            return UnitGitLease.Hold(() =>
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
        
            });
        }

        internal GitCommandResult SquashCommits(IList<string> commitHashes, string message, UnitGitHeadState expected)
        {
            // One operation: it holds the repository's lease from here to its end (see UnitGitLease).
            return UnitGitLease.Hold(() =>
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
        
            });
        }

        // One object reader for the whole rewrite (raw commits, the check of the written copies), stopped and waited for
        // when it returns, whatever the path.
        private GitCommandResult RewriteHeadHistory(IList<string> commitHashes, string message, bool squash, UnitGitHeadState expected)
        {
            using (UnitGitObjects objects = UnitGitObjects.Open(ProjectRoot))
                return RewriteHeadHistory(commitHashes, message, squash, expected, objects);
        }

        private GitCommandResult RewriteHeadHistory(IList<string> commitHashes, string message, bool squash, UnitGitHeadState expected,
            UnitGitObjects objects)
        {
            SweepScratchRefs();
            GitCommandResult branchResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            if (!branchResult.Success)
                return Failure("Check out a branch before rewriting history.");
            string branchRef = FirstNonEmptyLine(branchResult.StandardOutput);
            // Only the confirmed branch may be rewritten. Commits added to it meanwhile are rewritten
            // with the rest, and update-ref below refuses if it moves while the rewrite runs.
            GitCommandResult changed = CheckHeadUnchanged(squash ? "Squash" : "Rename", expected,
                new UnitGitHeadState { BranchRef = branchRef }, false);
            if (changed != null)
                return changed;
            GitCommandResult headResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", branchRef);
            if (!headResult.Success)
            {
                return headResult;
            }

            string oldHead = FirstNonEmptyLine(headResult.StandardOutput);
            GitCommandResult historyResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-list", "--topo-order", "--reverse", oldHead);
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
                GitCommandResult resolved = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", hash.Trim() + "^{commit}");
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

            GitCommandResult raw = ReadRawCommits(replaced, infos, objects);
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

            // Every new commit through one git fast-import (a constant number of processes), which writes each part as it
            // is given; the check of the written copies below proves it did. Signing: a commit carrying a signature is
            // refused above (its copy could not keep it), and the commits a rewrite writes are not signed, as the original
            // Mixer Git never signed them, in a repository configured to sign as well (commit-tree, which it used, signs
            // only when told to with -S).
            var stream = new CommitStream();
            GitCommandResult committer = ReadCommitter(stream);
            if (!committer.Success)
            {
                return committer;
            }

            // What every new commit will be, decided before anything is written. A parent is an object id, or "@n": the
            // n-th new commit of this plan (a mark for fast-import, its new id for commit-tree).
            var plan = new List<PlannedCommit>();
            var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
            string squashedRef = string.Empty;
            for (int i = startIndex; i < history.Count; i++)
            {
                string originalHash = history[i];
                if (squash && i > startIndex && i <= endIndex)
                {
                    rewritten[originalHash] = squashedRef;
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

                // A commit that is only re-parented is copied as stored: author line, encoding header and message bytes,
                // nothing decoded. The edited one (renamed, or the squash) keeps its author line and encoding header too, and
                // gets the user's message encoded in that encoding (UTF-8 when it names none). Every new commit gets a new
                // committer, the identity and time of whoever rewrites, now, on purpose and as before: it is a new commit.
                var commit = new PlannedCommit
                {
                    Source = source,
                    Tree = squash && i == startIndex ? infos[history[endIndex]].TreeHash : source.TreeHash,
                    Parents = parents,
                    Edited = squash ? i == startIndex : selected.Contains(originalHash)
                };
                if (source.RawAuthor == null)
                {
                    return Failure("Commit " + ShortHash(originalHash) + " has no author line. Mixer Git left the history as it is.");
                }

                if (commit.Edited)
                {
                    string why = EncodeEditedMessage(source, message, out commit.Message);
                    if (why != null)
                    {
                        return Failure("Commit " + ShortHash(originalHash) + " " + why + ". Mixer Git left the history as it is.");
                    }
                }
                else
                {
                    commit.Message = source.RawMessage;
                }

                rewritten[originalHash] = "@" + plan.Count.ToString(CultureInfo.InvariantCulture);
                if (squash && i == startIndex)
                {
                    squashedRef = rewritten[originalHash];
                }
                plan.Add(commit);
            }

            if (!rewritten.TryGetValue(oldHead, out string headRef) || string.IsNullOrWhiteSpace(headRef))
            {
                return Failure("Could not resolve the rewritten HEAD.");
            }

            string streamRef = stream.Reference;
            GitCommandResult written;
            lock (RunningRewrites) RunningRewrites.Add(streamRef);
            try
            {
                written = WriteThroughFastImport(stream, plan);
                if (written.Success)
                {
                    written = CheckWrittenCopies(plan, objects);
                }

                if (!written.Success)
                {
                    DeleteScratchRef(streamRef);
                    return written;
                }

                string newHead = headRef.StartsWith("@", StringComparison.Ordinal) ? plan[int.Parse(headRef.Substring(1), CultureInfo.InvariantCulture)].NewId : headRef;

                // Commit trees are unchanged. Move only the captured branch, and only if nobody advanced it.
                // Do not reset the index or whichever branch another process has since checked out.
                GitCommandResult updateResult = RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "update-ref", "--no-deref",
                    "-m", "Mixer Git: rewrite history", branchRef, newHead, oldHead);
                // The scratch ref fast-import wrote (this call named it): gone whether the branch moved or not; the new
                // commits are reachable from the branch, or left to Git's garbage collection. One a reload left behind is
                // swept by the next rewrite or backup (SweepScratchRefs).
                DeleteScratchRef(streamRef);
                if (!updateResult.Success)
                {
                    return updateResult;
                }

                updateResult.StandardOutput = "Rewrote " + branchRef + " " + ShortHash(oldHead) + " -> " + ShortHash(newHead) + ".";
                return updateResult;
            }
            finally
            {
                lock (RunningRewrites) RunningRewrites.Remove(streamRef);
            }
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
            GitCommandResult read = RunWithList(GitAccess.Read, null, LongTimeoutMilliseconds, hashes, false, options.Concat(new[] { "--stdin" }).ToArray(), options, new[] { "--" });
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

        /// <summary>One new commit of a rewrite, decided before anything is written.</summary>
        private sealed class PlannedCommit
        {
            public CommitRewriteInfo Source;
            public string Tree;
            public List<string> Parents;
            public bool Edited;
            public byte[] Message;
            public string NewId;
        }

        /// <summary>For tests: changes the message bytes a rewrite writes for a commit (by its old id), not the ones it
        /// expects, so the check of the written copies has a difference to find.</summary>
        internal static Func<string, byte[], byte[]> WrittenMessageSeamForTests;

        private static byte[] WrittenMessage(PlannedCommit commit) =>
            WrittenMessageSeamForTests?.Invoke(commit.Source.Hash, commit.Message) ?? commit.Message;

        private static bool NamesUtf8(byte[] encoding)
        {
            if (encoding == null) return true;
            string name = Encoding.ASCII.GetString(encoding).ToLowerInvariant();
            return name == "utf-8" || name == "utf8";
        }

        // The user's new message in the encoding the commit names (UTF-8 when none); null, or why it cannot be.
        private static string EncodeEditedMessage(CommitRewriteInfo source, string message, out byte[] bytes)
        {
            string text = NormalizeCommitMessage(message) + "\n";
            bytes = null;
            if (NamesUtf8(source.RawEncoding))
            {
                bytes = UnitGitRunner.Utf8.GetBytes(text);
                return null;
            }

            string name = Encoding.ASCII.GetString(source.RawEncoding);
            try
            {
                bytes = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text);
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                return "is stored in the encoding \"" + name + "\", in which the new message cannot be written";
            }
        }

        private string Resolve(string parent, IList<PlannedCommit> plan, bool marks) =>
            !parent.StartsWith("@", StringComparison.Ordinal) ? parent
            : marks ? ":" + (int.Parse(parent.Substring(1), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)
            : plan[int.Parse(parent.Substring(1), CultureInfo.InvariantCulture)].NewId;

        /// <summary>
        /// The invariant a rewrite keeps: before any ref moves, every new commit is read back and
        /// compared byte for byte with the one it replaces. Only its parent lines (the new parents, checked), its committer
        /// line, the edited commit's message (checked against the one planned) and the squash's tree (checked against the
        /// newest selected one) may differ. Any other difference stops the rewrite: no ref moves, the unreferenced objects
        /// are left to Git's garbage collection, and the commit and the part are named.
        /// </summary>
        private GitCommandResult CheckWrittenCopies(IList<PlannedCommit> plan, UnitGitObjects objects)
        {
            var bytes = new MemoryStream();
            foreach (PlannedCommit commit in plan)
            {
                bytes.SetLength(0);
                if (objects.Read(commit.NewId, bytes, out string type, out _) != UnitGitObjects.Answer.Found || type != "commit")
                {
                    bytes.SetLength(0);
                    GitCommandResult read = ReadGitStream(GitAccess.Read, DefaultTimeoutMilliseconds, (chunk, offset, count) =>
                    {
                        bytes.Write(chunk, offset, count);
                        return true;
                    }, "cat-file", "commit", commit.NewId);
                    if (!read.Success)
                    {
                        return Failure("Could not read back the rewritten copy of " + ShortHash(commit.Source.Hash) + ". Mixer Git moved nothing.");
                    }
                }

                var parents = commit.Parents.Select(parent => Resolve(parent, plan, false)).ToList();
                string differs = CopyDifference(commit.Source.RawCommit, bytes.ToArray(), parents, commit.Tree, commit.Edited ? commit.Message : null);
                if (differs != null)
                {
                    return Failure("The rewritten copy of commit " + ShortHash(commit.Source.Hash) + " came out with a different " + differs +
                                   ". Mixer Git moved nothing and left the history as it is.");
                }
            }

            return new GitCommandResult();
        }

        // Header lines (key, whole line) and the message of a stored commit.
        private static List<KeyValuePair<string, byte[]>> SplitCommit(byte[] commit, out byte[] message)
        {
            int end = 0;
            while (end < commit.Length && !(commit[end] == (byte)'\n' && (end + 1 >= commit.Length || commit[end + 1] == (byte)'\n'))) end++;
            var lines = new List<KeyValuePair<string, byte[]>>();
            int line = 0;
            while (line < end)
            {
                int next = Array.IndexOf(commit, (byte)'\n', line, end - line);
                if (next < 0) next = end;
                byte[] whole = commit.Skip(line).Take(next - line).ToArray();
                if (whole.Length > 0 && whole[0] == (byte)' ' && lines.Count > 0)
                {
                    // A continued header belongs to the line above.
                    var previous = lines[lines.Count - 1];
                    lines[lines.Count - 1] = new KeyValuePair<string, byte[]>(previous.Key, previous.Value.Concat(new[] { (byte)'\n' }).Concat(whole).ToArray());
                }
                else
                {
                    int space = Array.IndexOf(whole, (byte)' ');
                    lines.Add(new KeyValuePair<string, byte[]>(Encoding.ASCII.GetString(whole, 0, space < 0 ? whole.Length : space), whole));
                }
                line = next + 1;
            }

            message = commit.Skip(Math.Min(commit.Length, end + 2)).ToArray();
            return lines;
        }

        /// <summary>The first part in which <paramref name="written"/> is not the copy of <paramref name="original"/> it
        /// should be, or null (see CheckWrittenCopies).</summary>
        internal static string CopyDifference(byte[] original, byte[] written, IList<string> parents, string tree, byte[] editedMessage)
        {
            List<KeyValuePair<string, byte[]>> was = SplitCommit(original, out byte[] wasMessage);
            List<KeyValuePair<string, byte[]>> now = SplitCommit(written, out byte[] nowMessage);
            string Value(KeyValuePair<string, byte[]> header) => Encoding.ASCII.GetString(header.Value, header.Key.Length + 1, Math.Max(0, header.Value.Length - header.Key.Length - 1));
            if (!now.Where(header => header.Key == "tree").Select(Value).SequenceEqual(new[] { tree })) return "tree";
            if (!now.Where(header => header.Key == "parent").Select(Value).SequenceEqual(parents)) return "parents";
            var wasRest = was.Where(header => header.Key != "tree" && header.Key != "parent" && header.Key != "committer").ToList();
            var nowRest = now.Where(header => header.Key != "tree" && header.Key != "parent" && header.Key != "committer").ToList();
            if (now.Count(header => header.Key == "committer") != 1) return "committer line";
            for (int i = 0; i < Math.Max(wasRest.Count, nowRest.Count); i++)
            {
                if (i >= wasRest.Count || i >= nowRest.Count || wasRest[i].Key != nowRest[i].Key || !wasRest[i].Value.SequenceEqual(nowRest[i].Value))
                    return (i < wasRest.Count ? wasRest[i].Key : nowRest[i].Key) + " line";
            }

            return nowMessage.SequenceEqual(editedMessage ?? wasMessage) ? null : "message";
        }

        private static readonly HashSet<string> RebuiltHeaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "tree", "parent", "author", "committer", "encoding"
        };

        // Each commit's stored bytes, through the kept-alive cat-file (else a cat-file of its own): its headers, and its
        // message as stored.
        private GitCommandResult ReadRawCommits(IList<string> hashes, IDictionary<string, CommitRewriteInfo> infos, UnitGitObjects objects)
        {
            var bytes = new MemoryStream();
            foreach (string hash in hashes)
            {
                bytes.SetLength(0);
                if (objects.Read(hash, bytes, out string type, out _) != UnitGitObjects.Answer.Found || type != "commit")
                {
                    bytes.SetLength(0);
                    GitCommandResult read = ReadGitStream(GitAccess.Read, DefaultTimeoutMilliseconds, (chunk, offset, count) =>
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
        internal static void ParseRawCommitForTests(byte[] commit, out string lostHeader, out byte[] rawMessage, out string encoding)
        {
            var info = new CommitRewriteInfo();
            ParseRawCommit(commit, info);
            lostHeader = info.LostHeader;
            rawMessage = info.RawMessage;
            encoding = info.RawEncoding == null ? null : Encoding.ASCII.GetString(info.RawEncoding);
        }

        private static void ParseRawCommit(byte[] commit, CommitRewriteInfo info)
        {
            int end = 0;
            while (end < commit.Length && !(commit[end] == (byte)'\n' && (end + 1 >= commit.Length || commit[end + 1] == (byte)'\n'))) end++;
            int headerLength = Math.Min(end, commit.Length);
            info.RawCommit = commit;
            info.LostHeader = null;
            info.RawAuthor = null;
            info.RawEncoding = null;
            int line = 0;
            while (line < headerLength)
            {
                int next = Array.IndexOf(commit, (byte)'\n', line, headerLength - line);
                if (next < 0) next = headerLength;
                if (commit[line] != (byte)' ')
                {
                    int space = Array.IndexOf(commit, (byte)' ', line, next - line);
                    string key = Encoding.ASCII.GetString(commit, line, (space < 0 ? next : space) - line);
                    int value = space < 0 ? next : space + 1;
                    if (key == "encoding") info.RawEncoding = commit.Skip(value).Take(next - value).ToArray();
                    if (key == "author") info.RawAuthor = commit.Skip(value).Take(next - value).ToArray();
                    if (!RebuiltHeaders.Contains(key) && info.LostHeader == null) info.LostHeader = key;
                }
                line = next + 1;
            }

            int messageStart = Math.Min(commit.Length, end + 2);
            info.RawMessage = commit.Skip(messageStart).ToArray();
        }

        /// <summary>
        /// The new commits of one rewrite as a git fast-import stream: each commit is "the tree with this id" (deleteall,
        /// then the tree at the root path, which fast-import takes in Git 2.26) with the author, message and parents it would
        /// get from commit-tree, and the committer commit-tree would write (Git's GIT_COMMITTER_IDENT, read once). Parents
        /// are object ids or the marks of commits earlier in the stream.
        /// </summary>
        private sealed class CommitStream
        {
            internal string Committer = string.Empty;
            internal readonly MemoryStream Bytes = new MemoryStream();
            private int marks;
            private string reference = string.Empty;

            internal string Reference
            {
                get
                {
                    if (reference.Length == 0) reference = ScratchRefs + Guid.NewGuid().ToString("N");
                    return reference;
                }
            }

            internal void Add(PlannedCommit commit, IList<string> parents)
            {
                string mark = ":" + (++marks).ToString(CultureInfo.InvariantCulture);
                // A commit with no parent on a ref that already has one in this stream would continue from it: reset first.
                if (parents.Count == 0) Text("reset " + Reference + "\n");
                Text("commit " + Reference + "\nmark " + mark + "\n");
                // The author line and the encoding header as stored: fast-import writes them as they are.
                Text("author ");
                Bytes.Write(commit.Source.RawAuthor, 0, commit.Source.RawAuthor.Length);
                Text("\ncommitter " + Committer + "\n");
                if (commit.Source.RawEncoding != null)
                {
                    Text("encoding ");
                    Bytes.Write(commit.Source.RawEncoding, 0, commit.Source.RawEncoding.Length);
                    Text("\n");
                }
                byte[] body = WrittenMessage(commit);
                Text("data " + body.Length.ToString(CultureInfo.InvariantCulture) + "\n");
                Bytes.Write(body, 0, body.Length);
                Text("\n");
                for (int i = 0; i < parents.Count; i++) Text((i == 0 ? "from " : "merge ") + parents[i] + "\n");
                Text("deleteall\n");
                if (commit.Tree != EmptyTreeSha1 && commit.Tree != EmptyTreeSha256) Text("M 040000 " + commit.Tree + " \"\"\n");
                Text("\n");
            }

            internal void Text(string text)
            {
                byte[] bytes = UnitGitRunner.Utf8.GetBytes(text);
                Bytes.Write(bytes, 0, bytes.Length);
            }
        }

        private const string EmptyTreeSha1 = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        private const string EmptyTreeSha256 = "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";

        // The committer every new commit gets: Git's own GIT_COMMITTER_IDENT (what any Git command would write), read once.
        // fast-import reads the stream on stdin: without a clean stdin (a byte order mark first) no rewrite is possible.
        private GitCommandResult ReadCommitter(CommitStream stream)
        {
            if (!UnitGitRunner.CleanInput)
            {
                return Failure("On this system Git's input would start with a byte order mark, so Mixer Git cannot rename or squash commits here.");
            }

            GitCommandResult ident = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "var", "GIT_COMMITTER_IDENT");
            if (!ident.Success)
            {
                return ident;
            }

            stream.Committer = FirstNonEmptyLine(ident.StandardOutput);
            return stream.Committer.Length > 0 ? new GitCommandResult() : Failure("Git could not name the committer.");
        }

        private const string ScratchRefs = "refs/mixer-git/rewrite/";
        // The scratch refs of the rewrites running in this editor: never swept.
        private static readonly HashSet<string> RunningRewrites = new HashSet<string>(StringComparer.Ordinal);

        private void DeleteScratchRef(string reference)
        {
            GitCommandResult at = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", reference);
            if (at.Success && FirstNonEmptyLine(at.StandardOutput).Length > 0)
                RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "update-ref", "--no-deref", "-d", reference, FirstNonEmptyLine(at.StandardOutput));
        }

        /// <summary>
        /// Deletes the scratch refs a rewrite left behind (a reload stopped it before it could), each with the value it was
        /// read at, so a ref that moved meanwhile stays. A running rewrite's own is never touched. Called at the start of a
        /// rewrite and before a backup (whose --all would carry them).
        /// </summary>
        internal void SweepScratchRefs()
        {
            GitCommandResult refs = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "for-each-ref", "--format=%(objectname) %(refname)", ScratchRefs);
            if (!refs.Success) return;
            foreach (string line in SplitLines(refs.StandardOutput))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string id = line.Substring(0, space), reference = line.Substring(space + 1).Trim();
                if (!reference.StartsWith(ScratchRefs, StringComparison.Ordinal)) continue;
                lock (RunningRewrites)
                    if (RunningRewrites.Contains(reference)) continue;
                RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "update-ref", "--no-deref", "-d", reference, id);
            }
        }

        // Runs the stream into a scratch ref this call named, and reads each new commit's id from fast-import's marks
        // (exported to a file this call creates and deletes).
        private GitCommandResult WriteThroughFastImport(CommitStream stream, IList<PlannedCommit> plan)
        {
            foreach (PlannedCommit commit in plan) stream.Add(commit, commit.Parents.Select(parent => Resolve(parent, plan, true)).ToList());
            stream.Text("done\n");
            string marks = Path.Combine(Path.GetTempPath(), "mixer-git-marks-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                GitCommandResult imported = RunGitWithInput(GitAccess.Exclusive, LongTimeoutMilliseconds, stream.Bytes.ToArray(), "fast-import", "--quiet", "--done",
                    "--export-marks=" + marks);
                if (!imported.Success)
                {
                    return imported;
                }

                var ids = new Dictionary<int, string>();
                foreach (string line in File.ReadAllLines(marks))
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length == 2 && parts[0].StartsWith(":", StringComparison.Ordinal) &&
                        int.TryParse(parts[0].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int mark))
                        ids[mark] = parts[1];
                }

                for (int i = 0; i < plan.Count; i++)
                {
                    if (!ids.TryGetValue(i + 1, out plan[i].NewId))
                    {
                        return Failure("fast-import did not report the rewritten copy of " + ShortHash(plan[i].Source.Hash) + ". Mixer Git moved nothing.");
                    }
                }

                return new GitCommandResult();
            }
            catch (IOException ex)
            {
                return Failure("Could not read fast-import's marks: " + ex.Message + ". Mixer Git moved nothing.");
            }
            finally
            {
                DeleteTempFile(marks);
            }
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
