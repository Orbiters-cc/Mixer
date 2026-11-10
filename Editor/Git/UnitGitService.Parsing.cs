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
    // Git's outputs read into models: status, branches, the history, commit details and unified diffs. Pure functions.
    internal sealed partial class UnitGitService
    {
        /// <summary>
        /// "git log -z --name-status" of several commits: each commit's header (<see cref="CommitDetailsFormat"/>, which
        /// contains its full id as second field), then "status NUL path NUL" per file ("R100 NUL old NUL new NUL" for a
        /// rename). A header is told from a path by the id of the commit expected next.
        /// </summary>
        internal static List<UnitGitCommitDetails> ParseCommitDetailsList(string output, IList<string> hashes)
        {
            var list = new List<UnitGitCommitDetails>();
            string[] tokens = (output ?? string.Empty).Split('\0');
            UnitGitCommitDetails current = null;
            int next = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (next < hashes.Count && IsDetailsHeader(token, hashes, ref next))
                {
                    current = ParseCommitDetails(token);
                    if (current?.Commit != null) list.Add(current);
                    else current = null;
                    continue;
                }

                string status = token.TrimStart('\n');
                if (current == null || status.Length == 0) continue;
                bool twoPaths = status[0] == 'R' || status[0] == 'C';
                if (i + 1 >= tokens.Length) break;
                string path = tokens[++i];
                if (twoPaths && i + 1 < tokens.Length)
                {
                    string previous = path;
                    path = tokens[++i];
                    if (!current.FileStatus.ContainsKey(path)) current.OriginalPaths[path] = previous;
                }

                if (path.Length == 0 || current.FileStatus.ContainsKey(path)) continue;
                current.ChangedFiles.Add(path);
                current.FileStatus[path] = status[0];
            }

            return list;
        }

        // The header of one of the commits asked for (Git keeps their order; a commit it skipped is passed over).
        private static bool IsDetailsHeader(string token, IList<string> hashes, ref int next)
        {
            int first = token.IndexOf(FieldSeparator);
            if (first < 0) return false;
            int second = token.IndexOf(FieldSeparator, first + 1);
            if (second < 0) return false;
            string hash = token.Substring(first + 1, second - first - 1);
            for (int i = next; i < hashes.Count; i++)
            {
                if (!string.Equals(hashes[i], hash, StringComparison.Ordinal)) continue;
                next = i + 1;
                return true;
            }

            return false;
        }

        internal static UnitGitCommitDetails ParseCommitDetails(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            int headerEnd = output.IndexOf('\0');
            string header = headerEnd >= 0 ? output.Substring(0, headerEnd) : output;
            string files = headerEnd >= 0 ? output.Substring(headerEnd + 1) : string.Empty;
            var fields = header.Split(new[] { FieldSeparator }, 14);
            var details = new UnitGitCommitDetails();
            if (fields.Length >= 10)
            {
                details.Commit = new UnitGitCommit
                {
                    ShortHash = fields[0],
                    FullHash = fields[1],
                    Subject = fields[2],
                    AuthorName = fields[3],
                    AuthorEmail = fields[4],
                    Decorations = fields[6],
                    ReleaseId = fields.Length >= 11 ? fields[10].Trim() : string.Empty,
                    Parents = fields.Length >= 12 ? ParseParents(fields[11]) : Array.Empty<string>(),
                    Timestamp = fields.Length >= 13 ? ParseTimestamp(fields[12]) : 0
                };
                details.AuthorDate = fields[5];
                details.CommitterName = fields[7];
                details.CommitterEmail = fields[8];
                details.CommitterDate = fields[9];
                details.Body = fields.Length >= 14 ? fields[13].Replace("\r\n", "\n").Trim() : string.Empty;
            }

            // --name-status lines: "M<TAB>path", "R100<TAB>old<TAB>new". Merges list one letter per parent ("MM<TAB>path").
            foreach (string line in SplitLines(files))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0)
                {
                    continue;
                }

                char status = parts[0][0];
                string path = UnquotePath((status == 'R' || status == 'C') && parts.Length >= 3 ? parts[2] : parts[1]);
                if (path.Length == 0 || details.FileStatus.ContainsKey(path))
                {
                    continue;
                }

                details.ChangedFiles.Add(path);
                details.FileStatus[path] = status;
                if ((status == 'R' || status == 'C') && parts.Length >= 3) details.OriginalPaths[path] = UnquotePath(parts[1]);
            }

            return details;
        }

        internal static UnitGitSnapshot ParseStatusOutput(string output)
        {
            var snapshot = new UnitGitSnapshot();
            ParseStatus(output, snapshot);
            return snapshot;
        }

        internal static List<UnitGitBranch> ParseBranches(string output)
        {
            var branches = new List<UnitGitBranch>();
            foreach (string line in SplitLines(output))
            {
                var fields = line.Split(BranchFieldSeparator);
                if (fields.Length < 5)
                {
                    continue;
                }

                string fullRef = fields[0];
                string name = fields[1];
                // "(HEAD detached at abc1234)" is no branch: a detached HEAD shows as UnitGitSnapshot.IsDetached instead.
                if (name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase) ||
                    !(fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) || fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal)))
                {
                    continue;
                }

                branches.Add(new UnitGitBranch
                {
                    FullRef = fullRef,
                    Name = name,
                    IsCurrent = fields[2] == "*",
                    Upstream = fields[3],
                    ShortHash = fields[4],
                    IsRemote = fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal)
                });
            }

            return branches
                .OrderBy(branch => branch.IsRemote)
                .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<UnitGitCommit> ParseCommits(string output, string logSearch)
        {
            var commits = new List<UnitGitCommit>();
            foreach (string line in SplitLines(output))
            {
                UnitGitCommit commit = ParseCommit(line);
                if (commit != null && MatchesSearch(commit, logSearch))
                {
                    commits.Add(commit);
                }
            }

            return commits;
        }

        /// <summary>One record of <see cref="CommitLogFormat"/>; null when it is not one.</summary>
        internal static UnitGitCommit ParseCommit(string record)
        {
            var fields = (record ?? string.Empty).Split(FieldSeparator);
            if (fields.Length < 6)
            {
                return null;
            }

            return new UnitGitCommit
            {
                ShortHash = fields[0],
                FullHash = fields[1],
                Subject = fields[2],
                AuthorName = fields[3],
                AuthorEmail = fields[4],
                Decorations = fields[5],
                ReleaseId = fields.Length >= 7 ? fields[6].Trim() : string.Empty,
                Parents = fields.Length >= 8 ? ParseParents(fields[7]) : Array.Empty<string>(),
                Timestamp = fields.Length >= 9 ? ParseTimestamp(fields[8]) : 0
            };
        }

        private static string[] ParseParents(string parents)
        {
            return string.IsNullOrWhiteSpace(parents)
                ? Array.Empty<string>()
                : parents.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static long ParseTimestamp(string seconds)
        {
            return long.TryParse(seconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
        }

        internal static bool MatchesSearch(UnitGitCommit commit, string logSearch)
        {
            if (commit == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(logSearch))
            {
                return true;
            }

            string query = logSearch.Trim();
            return Contains(commit.Subject, query) ||
                   Contains(commit.AuthorName, query) ||
                   Contains(commit.ShortHash, query) ||
                   Contains(commit.FullHash, query) ||
                   Contains(commit.Decorations, query);
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static List<string> ParseStashList(string output)
        {
            return SplitLines(output)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        internal static void ParseStatus(string output, UnitGitSnapshot snapshot)
        {
            foreach (string line in SplitLines(output))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    ParseBranchStatus(line.Substring(3), snapshot);
                    continue;
                }

                if (line.Length < 3)
                {
                    continue;
                }

                string path = line.Substring(3);
                string originalPath = string.Empty;
                int renameIndex = line[0] == 'R' || line[0] == 'C' || line[1] == 'R' || line[1] == 'C'
                    ? FindRenameSeparator(path)
                    : -1;
                if (renameIndex >= 0)
                {
                    originalPath = UnquotePath(path.Substring(0, renameIndex));
                    path = path.Substring(renameIndex + 4);
                }

                snapshot.Changes.Add(new UnitGitStatusEntry
                {
                    IndexStatus = line[0],
                    WorkTreeStatus = line[1],
                    Path = UnquotePath(path),
                    OriginalPath = originalPath
                });
            }
        }

        internal static UnitGitSnapshot ParseStatusV2Output(string output)
        {
            var snapshot = new UnitGitSnapshot();
            ParseStatusV2(output, snapshot);
            return snapshot;
        }

        /// <summary>
        /// "git status --porcelain=v2 --branch -z": the header lines give HEAD ("(initial)" before the first commit), the
        /// branch ("(detached)"), its upstream and how far ahead and behind it is; then one NUL-terminated entry per change,
        /// paths exactly as Git has them (no quoting), a rename's old path in the field after it.
        /// </summary>
        internal static void ParseStatusV2(string output, UnitGitSnapshot snapshot)
        {
            string[] records = (output ?? string.Empty).Split('\0');
            for (int i = 0; i < records.Length; i++)
            {
                string record = records[i];
                if (record.Length < 2) continue;
                if (record[0] == '#')
                {
                    ParseBranchHeader(record, snapshot);
                    continue;
                }

                string[] fields;
                var entry = new UnitGitStatusEntry();
                switch (record[0])
                {
                    case '1': // 1 XY sub mH mI mW hH hI path
                        fields = record.Split(new[] { ' ' }, 9);
                        if (fields.Length < 9) continue;
                        SetStatus(entry, fields[1]);
                        entry.Path = fields[8];
                        break;
                    case '2': // 2 XY sub mH mI mW hH hI Xscore path, then the old path
                        fields = record.Split(new[] { ' ' }, 10);
                        if (fields.Length < 10) continue;
                        SetStatus(entry, fields[1]);
                        entry.Path = fields[9];
                        entry.OriginalPath = i + 1 < records.Length ? records[++i] : string.Empty;
                        break;
                    case 'u': // u XY sub m1 m2 m3 mW h1 h2 h3 path
                        fields = record.Split(new[] { ' ' }, 11);
                        if (fields.Length < 11) continue;
                        SetStatus(entry, fields[1]);
                        entry.Path = fields[10];
                        break;
                    case '?':
                    case '!':
                        if (record[1] != ' ') continue;
                        entry.IndexStatus = entry.WorkTreeStatus = record[0];
                        entry.Path = record.Substring(2);
                        break;
                    default:
                        continue;
                }

                if (entry.Path.Length > 0) snapshot.Changes.Add(entry);
            }
        }

        // v2 writes "." where v1 wrote a space (unchanged in the index / the working tree).
        private static void SetStatus(UnitGitStatusEntry entry, string xy)
        {
            entry.IndexStatus = xy.Length > 0 && xy[0] != '.' ? xy[0] : ' ';
            entry.WorkTreeStatus = xy.Length > 1 && xy[1] != '.' ? xy[1] : ' ';
        }

        // "# branch.oid <id>|(initial)", "# branch.head <name>|(detached)", "# branch.upstream <name>", "# branch.ab +A -B".
        private static void ParseBranchHeader(string record, UnitGitSnapshot snapshot)
        {
            const string oid = "# branch.oid ", head = "# branch.head ", upstream = "# branch.upstream ", ab = "# branch.ab ";
            if (record.StartsWith(oid, StringComparison.Ordinal))
            {
                string value = record.Substring(oid.Length).Trim();
                snapshot.HasCommits = value != "(initial)";
                snapshot.Head = snapshot.HasCommits ? value : string.Empty;
            }

            else if (record.StartsWith(head, StringComparison.Ordinal))
            {
                string value = record.Substring(head.Length);
                // A detached HEAD (a visited commit, a rebase): no branch at all, so every "detached HEAD" fallback applies.
                snapshot.IsDetached = value == "(detached)";
                snapshot.CurrentBranch = snapshot.IsDetached ? string.Empty : value;
            }
            else if (record.StartsWith(upstream, StringComparison.Ordinal))
            {
                snapshot.Upstream = record.Substring(upstream.Length);
            }
            else if (record.StartsWith(ab, StringComparison.Ordinal))
            {
                foreach (string part in record.Substring(ab.Length).Split(' '))
                {
                    if (part.Length < 2) continue;
                    if (part[0] == '+') int.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshot.Ahead);
                    else if (part[0] == '-') int.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshot.Behind);
                }
            }
        }

        internal static void ParseBranchStatus(string branchStatus, UnitGitSnapshot snapshot)
        {
            string text = branchStatus;
            int bracketIndex = text.IndexOf(" [", StringComparison.Ordinal);
            string trackingPart = bracketIndex >= 0 ? text.Substring(bracketIndex) : string.Empty;
            string branchPart = bracketIndex >= 0 ? text.Substring(0, bracketIndex) : text;

            // A detached HEAD (a visited commit, a rebase): no branch at all, so every "detached HEAD" fallback applies.
            if (branchPart == "HEAD (no branch)")
            {
                snapshot.IsDetached = true;
                snapshot.CurrentBranch = string.Empty;
                return;
            }

            int upstreamIndex = branchPart.IndexOf("...", StringComparison.Ordinal);
            const string noCommitsPrefix = "No commits yet on ";
            if (branchPart.StartsWith(noCommitsPrefix, StringComparison.Ordinal))
            {
                snapshot.CurrentBranch = branchPart.Substring(noCommitsPrefix.Length);
                return;
            }

            if (upstreamIndex >= 0)
            {
                snapshot.CurrentBranch = branchPart.Substring(0, upstreamIndex);
                snapshot.Upstream = branchPart.Substring(upstreamIndex + 3);
            }
            else
            {
                snapshot.CurrentBranch = branchPart;
            }

            Match ahead = AheadRegex.Match(trackingPart);
            if (ahead.Success)
            {
                int.TryParse(ahead.Groups[1].Value, out snapshot.Ahead);
            }

            Match behind = BehindRegex.Match(trackingPart);
            if (behind.Success)
            {
                int.TryParse(behind.Groups[1].Value, out snapshot.Behind);
            }
        }

        private static int FindRenameSeparator(string path)
        {
            bool quoted = false;
            for (int i = 0; i < path.Length; i++)
            {
                if (quoted && path[i] == '\\') { i++; continue; }
                if (path[i] == '"') quoted = !quoted;
                if (!quoted && i + 4 <= path.Length && path.Substring(i, 4) == " -> ")
                    return i;
            }
            return -1;
        }

        internal static string UnquotePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            {
                var decoded = new StringBuilder();
                for (int i = 1; i < path.Length - 1; i++)
                {
                    if (path[i] != '\\') { decoded.Append(path[i]); continue; }
                    i++;
                    if (path[i] >= '0' && path[i] <= '7')
                    {
                        // Git quotes UTF-8 bytes as octal escapes, not Unicode code points.
                        var bytes = new List<byte>();
                        while (true)
                        {
                            int value = 0;
                            int digits = 0;
                            while (i < path.Length - 1 && digits < 3 && path[i] >= '0' && path[i] <= '7')
                            { value = value * 8 + path[i++] - '0'; digits++; }
                            bytes.Add((byte)value);
                            if (i + 1 >= path.Length - 1 || path[i] != '\\' || path[i + 1] < '0' || path[i + 1] > '7')
                                break;
                            i++;
                        }
                        decoded.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                        i--;
                    }
                    else
                    {
                        switch (path[i])
                        {
                            case 'a': decoded.Append('\a'); break;
                            case 'b': decoded.Append('\b'); break;
                            case 't': decoded.Append('\t'); break;
                            case 'n': decoded.Append('\n'); break;
                            case 'v': decoded.Append('\v'); break;
                            case 'f': decoded.Append('\f'); break;
                            case 'r': decoded.Append('\r'); break;
                            default: decoded.Append(path[i]); break;
                        }
                    }
                }
                return decoded.ToString();
            }

            return path;
        }

        internal static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                yield return line;
            }
        }

        internal static UnitGitDiff ParseUnifiedDiff(string output)
        {
            var diff = new UnitGitDiff();
            ParseUnifiedDiff(output, diff);
            return diff;
        }

        internal static void ParseUnifiedDiff(string output, UnitGitDiff diff)
        {
            var pendingRemoved = new Queue<string>();
            bool inHunk = false;
            char previous = ' ';
            List<string> rawLines = SplitLines(output).ToList();
            if (rawLines.Count > 0 && rawLines[rawLines.Count - 1].Length == 0)
            {
                rawLines.RemoveAt(rawLines.Count - 1); // the output's final newline, not an empty context line
            }

            foreach (string rawLine in rawLines)
            {
                if (rawLine.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    FlushRemoved(diff, pendingRemoved);
                    inHunk = false;
                    continue;
                }
                if (!inHunk && IsDiffHeaderLine(rawLine))
                {
                    continue;
                }

                if (!inHunk && rawLine.StartsWith("Binary files ", StringComparison.Ordinal) && rawLine.EndsWith(" differ", StringComparison.Ordinal))
                {
                    // Git's "Binary files a/x and /dev/null differ": one explanatory line instead of paths with a/ b/ prefixes.
                    AddDiffMessage(diff, rawLine.StartsWith("Binary files /dev/null and ", StringComparison.Ordinal)
                        ? "Binary file added: no text to compare."
                        : rawLine.EndsWith(" and /dev/null differ", StringComparison.Ordinal)
                            ? "Binary file deleted: no text to compare."
                            : "Binary file changed: no text to compare.");
                    continue;
                }

                if (rawLine.StartsWith("@@", StringComparison.Ordinal))
                {
                    inHunk = true;
                    FlushRemoved(diff, pendingRemoved);
                    diff.Lines.Add(new UnitGitDiffLine
                    {
                        Left = rawLine,
                        Right = rawLine,
                        Kind = UnitGitDiffLineKind.Hunk
                    });
                    continue;
                }

                if (inHunk && rawLine.StartsWith("\\", StringComparison.Ordinal))
                {
                    // "\ No newline at end of file" is about the line before it; it is not a line of the file.
                    if (previous != '+') diff.LeftEndsWithoutNewline = true;
                    if (previous != '-') diff.RightEndsWithoutNewline = true;
                    continue;
                }

                previous = rawLine.Length > 0 ? rawLine[0] : ' ';
                if (rawLine.StartsWith("-", StringComparison.Ordinal))
                {
                    pendingRemoved.Enqueue(rawLine.Length > 1 ? rawLine.Substring(1) : string.Empty);
                    continue;
                }

                if (rawLine.StartsWith("+", StringComparison.Ordinal))
                {
                    string added = rawLine.Length > 1 ? rawLine.Substring(1) : string.Empty;
                    if (pendingRemoved.Count > 0)
                    {
                        diff.Lines.Add(new UnitGitDiffLine
                        {
                            Left = pendingRemoved.Dequeue(),
                            Right = added,
                            Kind = UnitGitDiffLineKind.Changed
                        });
                    }
                    else
                    {
                        diff.Lines.Add(new UnitGitDiffLine
                        {
                            Right = added,
                            Kind = UnitGitDiffLineKind.Added
                        });
                    }

                    diff.DifferenceCount++;
                    continue;
                }

                FlushRemoved(diff, pendingRemoved);

                string context = rawLine.StartsWith(" ", StringComparison.Ordinal)
                    ? rawLine.Substring(1)
                    : rawLine;
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Left = context,
                    Right = context,
                    Kind = UnitGitDiffLineKind.Context
                });
            }

            FlushRemoved(diff, pendingRemoved);
        }

        private static bool IsDiffHeaderLine(string line)
        {
            foreach (string prefix in DiffHeaderPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void FlushRemoved(UnitGitDiff diff, Queue<string> pendingRemoved)
        {
            while (pendingRemoved.Count > 0)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Left = pendingRemoved.Dequeue(),
                    Kind = UnitGitDiffLineKind.Removed
                });
                diff.DifferenceCount++;
            }
        }
    }
}
