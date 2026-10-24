using System;
using System.Collections.Generic;

namespace Orbiters.UnitGit.Editor
{
    internal delegate void UnitGitProcessLogHandler(string line);

    /// <summary>The checked-out branch and commit a prompt was opened for.</summary>
    internal sealed class UnitGitHeadState
    {
        public string BranchRef = string.Empty; // empty for a detached HEAD
        public string Head = string.Empty;

        public string BranchName
        {
            get { return BranchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? BranchRef.Substring(11) : BranchRef; }
        }
    }

    internal sealed class GitCommandResult
    {
        public int ExitCode;
        public string StandardOutput = string.Empty;
        public string StandardError = string.Empty;
        public bool TimedOut;

        public bool Success
        {
            get { return ExitCode == 0 && !TimedOut; }
        }

        public string Message
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(StandardError))
                {
                    return StandardError.Trim();
                }

                return StandardOutput.Trim();
            }
        }
    }

    internal sealed class UnitGitSnapshot
    {
        public bool GitAvailable;
        public bool IsUnityProject;
        public bool HasRepository;
        public bool HasCommits;
        public string ProjectRoot = string.Empty;
        public string CurrentBranch = string.Empty;
        public string Upstream = string.Empty;
        public int Ahead;
        public int Behind;
        public string LastError = string.Empty;
        public List<UnitGitBranch> Branches = new List<UnitGitBranch>();
        public List<UnitGitStatusEntry> Changes = new List<UnitGitStatusEntry>();
        public UnitGitFileList ChangeList = new UnitGitFileList();
        public List<UnitGitCommit> Commits = new List<UnitGitCommit>();
        public bool HasMoreCommits;
        public UnitGitReleaseFile Releases = new UnitGitReleaseFile();
        public List<string> Shelves = new List<string>();
        public List<string> Remotes = new List<string>();
        public string HeadMessage = string.Empty;
        // Files Git still tracks although the ignore rules now leave them out (MCB version downloads).
        public List<string> TrackedIgnoredDownloads = new List<string>();
        public long TrackedIgnoredDownloadBytes;

        // A commit checked out on its own ("git checkout --detach", Visit This Commit) instead of a branch. CurrentBranch is
        // empty then, and nothing lists a branch as current.
        public bool IsDetached;
        public UnitGitCommit DetachedCommit; // the checked-out commit, when detached
        public string PreviousBranch = string.Empty; // the branch checked out before it (Git's @{-1}), if it still exists
        public string ReturnBranch = string.Empty; // the local branch "Back" goes to: the visit's own, else PreviousBranch
        public int NewerCommits = -1; // commits on ReturnBranch after the checked-out commit; -1 when not counted
        public string DetachedBy = string.Empty; // "Rebasing", "Bisecting": Git's own detached HEAD, which is no visit
    }

    /// <summary>
    /// A commit being visited and the way back: the branch to return to, where it pointed, and the shelf made before
    /// visiting. Kept per project (<see cref="UnitGitSettings.Visit"/>), so it outlives domain reloads and restarts.
    /// </summary>
    [Serializable]
    internal sealed class UnitGitVisit
    {
        public string branch = string.Empty;
        public string branchTip = string.Empty; // full hash of the branch when the visit started
        public string shelf = string.Empty; // the message of the shelf made before visiting; empty when nothing was shelved
        public string commit = string.Empty; // the full hash visited last
        public long started; // UTC ticks: Git state read before this still shows the branch
    }

    internal sealed class UnitGitBranch
    {
        public string FullRef = string.Empty;
        public string Name = string.Empty;
        public string Upstream = string.Empty;
        public string ShortHash = string.Empty;
        public bool IsCurrent;
        public bool IsRemote;
    }

    internal sealed class UnitGitStatusEntry
    {
        public string Path = string.Empty;
        public string OriginalPath = string.Empty;
        public char IndexStatus = ' ';
        public char WorkTreeStatus = ' ';

        public bool IsUntracked
        {
            get { return IndexStatus == '?' && WorkTreeStatus == '?'; }
        }

        public bool IsStaged
        {
            get { return IndexStatus != ' ' && IndexStatus != '?'; }
        }

        public bool IsUnstaged
        {
            get { return WorkTreeStatus != ' ' || IsUntracked; }
        }

        public string DisplayStatus
        {
            get
            {
                if (IndexStatus == 'U' || WorkTreeStatus == 'U' ||
                    (IndexStatus == 'A' && WorkTreeStatus == 'A') ||
                    (IndexStatus == 'D' && WorkTreeStatus == 'D'))
                    return "conflict";

                if (IsUntracked)
                {
                    return "untracked";
                }

                if (IndexStatus == 'A' || WorkTreeStatus == 'A')
                {
                    return "added";
                }

                if (IndexStatus == 'M' || WorkTreeStatus == 'M')
                {
                    return "modified";
                }

                if (IndexStatus == 'D' || WorkTreeStatus == 'D')
                {
                    return "deleted";
                }

                if (IndexStatus == 'R' || WorkTreeStatus == 'R')
                {
                    return "renamed";
                }

                if (IndexStatus == 'C' || WorkTreeStatus == 'C')
                {
                    return "copied";
                }

                return (IndexStatus.ToString() + WorkTreeStatus).Trim();
            }
        }
    }

    internal sealed class UnitGitCommit
    {
        public string ShortHash = string.Empty;
        public string FullHash = string.Empty;
        public string Subject = string.Empty;
        public string AuthorName = string.Empty;
        public string AuthorEmail = string.Empty;
        public string RelativeDate = string.Empty;
        public string Decorations = string.Empty;
        public string ReleaseId = string.Empty;
        public string[] Parents = Array.Empty<string>(); // full hashes, first parent first
        public long Timestamp; // author date, unix seconds

        public bool HasRelease
        {
            get { return !string.IsNullOrWhiteSpace(ReleaseId); }
        }
    }

    internal sealed class UnitGitCommitDetails
    {
        public UnitGitCommit Commit;
        public string AuthorDate = string.Empty;
        public string CommitterName = string.Empty;
        public string CommitterEmail = string.Empty;
        public string CommitterDate = string.Empty;
        public string Body = string.Empty; // the message without its subject line
        public List<string> ChangedFiles = new List<string>(); // renamed and copied files by their new path
        // Status letter of each changed path: A, M, D, R, C or T (for merges, the change against the first parent).
        public Dictionary<string, char> FileStatus = new Dictionary<string, char>(StringComparer.Ordinal);
    }

    /// <summary>Narrows the history: one branch instead of all refs, commits by one author, commits since a date.</summary>
    internal sealed class UnitGitLogFilter
    {
        public string Branch = string.Empty; // a ref Git can read ("main", "origin/main", "refs/heads/main")
        public string Author = string.Empty; // part of the author's name or email, any letter case
        public DateTime? Since; // committed on or after; unspecified kinds are local times
        public string Path = string.Empty; // a file or folder ("Show History"): only commits that changed it

        public bool IsEmpty
        {
            get { return string.IsNullOrWhiteSpace(Branch) && string.IsNullOrWhiteSpace(Author) && !Since.HasValue && string.IsNullOrWhiteSpace(Path); }
        }
    }

    internal sealed class UnitGitDiff
    {
        public string Path = string.Empty;
        public string LeftTitle = "Repository";
        public string RightTitle = "Current version";
        public List<UnitGitDiffLine> Lines = new List<UnitGitDiffLine>();
        public int DifferenceCount;
        public int MaxLineLength;
        // From this line on, the right side is the file on disk (index -> working tree): its changes can be put back. -1: none.
        public int EditableFrom = -1;
        // "\ No newline at end of file" after the left / right side's last line (of the last section).
        public bool LeftEndsWithoutNewline;
        public bool RightEndsWithoutNewline;
    }

    internal sealed class UnitGitDiffLine
    {
        public string Left = string.Empty;
        public string Right = string.Empty;
        public UnitGitDiffLineKind Kind;
        // The exact text when what is shown differs (tabs shown as spaces); null when Left / Right are exact.
        public string RawLeft;
        public string RawRight;
    }

    /// <summary>What a diff ignores, as JetBrains IDEs offer it.</summary>
    internal enum UnitGitWhitespace
    {
        None,
        Trim,
        Ignore,
        IgnoreAndBlankLines
    }

    internal enum UnitGitDiffLineKind
    {
        Context,
        Added,
        Removed,
        Changed,
        Hunk
    }

    public enum UnitGitRemoteProvider
    {
        GitHub,
        GitLab
    }

    internal enum UnitGitResetMode
    {
        Soft,
        Mixed,
        Hard,
        Keep
    }
}
