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
    internal sealed partial class UnitGitService
    {
        private const char FieldSeparator = '\x1f';
        private const char BranchFieldSeparator = '\t';
        private const int MaxUntrackedDiffPreviewBytes = 512 * 1024;
        private const int BinarySniffBytes = 4096;
        internal const int DefaultTimeoutMilliseconds = 30000;
        public const int LongTimeoutMilliseconds = 900000;
        // Windows stops a command line at 32k characters: longer path lists go to Git on stdin.
        internal const int MaxPathsPerCommand = 200;
        internal const int MaxPathCharactersPerCommand = 8000;
        private const string NoTextualDifferences = "No textual differences to display.";
        private const string ReleaseTrailerFormat = "%(trailers:key=" + UnitGitReleases.TrailerKey + ",valueonly,separator=%x2C)";
        // Git writes UTF-8 (content, messages, quoted paths); the system code page would garble anything beyond ASCII.
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly Regex AheadRegex = new Regex(@"ahead\s+(\d+)", RegexOptions.Compiled);
        private static readonly Regex BehindRegex = new Regex(@"behind\s+(\d+)", RegexOptions.Compiled);
        // Git's extended header lines between "diff --git" and the first hunk.
        private static readonly string[] DiffHeaderPrefixes =
        {
            "index ", "--- ", "+++ ", "new file mode ", "deleted file mode ", "old mode ", "new mode ",
            "similarity index ", "dissimilarity index ", "rename from ", "rename to ", "copy from ", "copy to "
        };

        public UnitGitService()
            : this(GetUnityProjectRoot())
        {
        }

        public UnitGitService(string projectRoot)
        {
            ProjectRoot = projectRoot;
        }

        public string ProjectRoot { get; private set; }

        public UnitGitProcessLogHandler ProcessLogReceived { get; set; }
        public Func<bool> ReadSuperseded { get; set; }

        public UnitGitSnapshot BuildSnapshot(string logSearch, int historyLimit = 300)
        {
            return BuildSnapshot(logSearch, historyLimit, null);
        }

        /// <param name="visitBranch">The branch a visit returns to (<see cref="UnitGitVisit.branch"/>): while HEAD is detached,
        /// the snapshot counts the commits it has after HEAD. Empty: the branch checked out before HEAD was detached.</param>
        public UnitGitSnapshot BuildSnapshot(string logSearch, int historyLimit, UnitGitLogFilter filter, string visitBranch = null)
        {
            using (var history = new UnitGitHistory())
            {
                return BuildSnapshot(new UnitGitSnapshotRequest
                {
                    LogSearch = logSearch, HistoryLimit = historyLimit, Filter = filter, VisitBranch = visitBranch, History = history
                });
            }
        }

        // for-each-ref fields: the branch list (as "git branch --all --format" printed it) and the full id for the signature.
        private const string RefFormat = "%(refname)%09%(refname:short)%09%(HEAD)%09%(upstream:short)%09%(objectname:short)%09%(objectname)";

        /// <summary>
        /// The repository as the window shows it, read in slices: status and refs always (at once, both lock-free reads); the
        /// remotes when Git's config changed, the shelves when the stash did, the history when the refs, the filter or the
        /// search changed (and then on from where <see cref="UnitGitSnapshotRequest.History"/> stopped), and HEAD's message
        /// only when asked. Everything else is taken from <see cref="UnitGitSnapshotRequest.Previous"/>.
        /// </summary>
        internal UnitGitSnapshot BuildSnapshot(UnitGitSnapshotRequest request)
        {
            Version version = GetGitVersion(out string versionText);
            var snapshot = new UnitGitSnapshot
            {
                ProjectRoot = ProjectRoot,
                GitAvailable = version != null,
                IsUnityProject = IsUnityProject(ProjectRoot),
                HasRepository = HasRepository(ProjectRoot)
            };

            if (!snapshot.GitAvailable)
            {
                snapshot.LastError = "Git was not found on PATH. Install Git and reopen Unity.";
                return snapshot;
            }

            if (version < MinimumGitVersion)
            {
                snapshot.LastError = "Mixer Git needs Git " + MinimumGitVersion.ToString(2) + " or newer, and this is " + versionText + ". Update Git and reopen Unity.";
                return snapshot;
            }

            if (!snapshot.IsUnityProject)
            {
                snapshot.LastError = "No Unity project markers were found beside the Assets folder.";
                return snapshot;
            }

            if (!snapshot.HasRepository)
            {
                snapshot.LastError = "No Git repository exists at the Unity project root.";
                return snapshot;
            }

            UnitGitSnapshot previous = request.Previous;
            if (previous != null && (previous.ProjectRoot != ProjectRoot || !previous.HasRepository || !string.IsNullOrEmpty(previous.LastError)))
                previous = null;
            // Older commits only ("load more"): everything else is what was read a moment ago, as long as the refs did not
            // move since (else more of the old history would be mixed with the new: then the whole refresh runs).
            if (request.HistoryOnly && previous != null && CurrentRefsSignature() == previous.RefsSignature)
            {
                UnitGitSnapshot more = previous.CopyWithoutHistory();
                ReadHistory(more, request);
                return more;
            }

            UnitGitProjectInitializer.EnsureDefaultRules(ProjectRoot);

            // Exact renames only (a file moved unchanged, as Unity moves assets): guessing renames from similar content reads
            // every added and deleted file, which takes many seconds with big textures and models. Every untracked file is
            // listed (-uall): the Changes list shows and ticks them one by one.
            string[] statusArguments = { "status", "--porcelain=v2", "--branch", "-z", "-uall", "--find-renames=100%" };
            string[] refsArguments = { "for-each-ref", "--format=" + RefFormat, "refs/heads", "refs/remotes", "refs/tags", "refs/stash" };
            GitCommandResult status, refs;
            if (UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
            {
                status = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, statusArguments);
                refs = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, refsArguments);
            }
            else
            {
                // On a worker (the window's refresh): the two reads run side by side.
                var refsRead = System.Threading.Tasks.Task.Run(() => RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, refsArguments));
                status = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, statusArguments);
                refs = refsRead.Result;
            }

            if (!status.Success)
            {
                snapshot.HasCommits = HasCommits();
                snapshot.LastError = status.Message;
                return snapshot;
            }

            ParseStatusV2(status.StandardOutput, snapshot);
            if (refs.Success)
            {
                snapshot.Branches = ParseBranches(refs.StandardOutput);
                snapshot.RefsSignature = Signature(snapshot.Head + "\n" + snapshot.CurrentBranch + "\n" + refs.StandardOutput);
            }

            snapshot.ConfigStamp = UnitGitRepositoryStamp.Of(ProjectRoot, "config");
            snapshot.Remotes = previous != null && previous.ConfigStamp.Length > 0 && previous.ConfigStamp == snapshot.ConfigStamp
                ? previous.Remotes
                : GetRemoteNames();
            snapshot.Releases = UnitGitReleases.Load(ProjectRoot);
            if (snapshot.IsDetached && snapshot.HasCommits) ReadDetachedHead(snapshot, request.VisitBranch);
            if (snapshot.HasCommits)
            {
                ReadHistory(snapshot, request);
                if (request.ReadHeadMessage)
                {
                    if (previous != null && previous.HeadMessage != null && previous.Head == snapshot.Head)
                        snapshot.HeadMessage = previous.HeadMessage;
                    else
                    {
                        var message = GetHeadCommitMessage();
                        if (message.Success)
                            snapshot.HeadMessage = message.StandardOutput;
                    }
                }

                // The stash's tip and its reflog (dropping an older shelf only rewrites the reflog).
                string stash = refs.StandardOutput.Split('\n').FirstOrDefault(line => line.StartsWith("refs/stash\t", StringComparison.Ordinal)) ?? string.Empty;
                snapshot.StashStamp = stash + "|" + UnitGitRepositoryStamp.Of(ProjectRoot, "logs/refs/stash");
                if (previous != null && previous.StashStamp == snapshot.StashStamp && refs.Success)
                    snapshot.Shelves = previous.Shelves;
                else
                {
                    var shelves = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "stash", "list");
                    if (shelves.Success)
                        snapshot.Shelves = ParseStashList(shelves.StandardOutput);
                }

                ReadTrackedIgnoredDownloads(snapshot);
            }

            return snapshot;
        }

        // The refs signature as a full refresh computes it, from for-each-ref and HEAD alone (no status).
        private string CurrentRefsSignature()
        {
            GitCommandResult refs = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "for-each-ref", "--format=" + RefFormat, "refs/heads", "refs/remotes", "refs/tags", "refs/stash");
            GitCommandResult head = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "HEAD", "--symbolic-full-name", "HEAD");
            if (!refs.Success || !head.Success) return null;
            string[] lines = SplitLines(head.StandardOutput).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length < 2) return null;
            string branch = lines[1].StartsWith("refs/heads/", StringComparison.Ordinal) ? lines[1].Substring("refs/heads/".Length) : string.Empty;
            return Signature(lines[0] + "\n" + branch + "\n" + refs.StandardOutput);
        }

        // The history slice: read on (or again) through the request's history, which keeps where it stopped.
        private void ReadHistory(UnitGitSnapshot snapshot, UnitGitSnapshotRequest request)
        {
            int limit = Math.Max(1, request.HistoryLimit);
            var arguments = new List<string>();
            AddLogFilterArguments(arguments, request.Filter);
            string search = (request.LogSearch ?? string.Empty).Trim();
            // The refs signature makes a new commit, branch, tag or fetch read the history from the top again.
            string key = snapshot.RefsSignature + "\u001f" + search + "\u001f" + string.Join("\u001f", arguments);
            UnitGitHistory history = request.History ?? new UnitGitHistory();
            try
            {
                GitCommandResult read = history.Read(ProjectRoot, key, arguments, search, limit, ReadSuperseded, out List<UnitGitCommit> commits, out bool more, ProcessLogReceived);
                if (ReadSuperseded?.Invoke() ?? false) return;
                if (!read.Success)
                    throw new InvalidOperationException("Could not read Git history: " + read.Message);
                snapshot.Commits = commits;
                snapshot.HasMoreCommits = more;
            }
            finally
            {
                if (request.History == null) history.Dispose();
            }
        }

        // A short, stable fingerprint of a text (FNV-1a, 64 bits).
        internal static string Signature(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char character in text ?? string.Empty)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private void ReadTrackedIgnoredDownloads(UnitGitSnapshot snapshot)
        {
            if (!Directory.Exists(Path.Combine(ProjectRoot, UnitGitProjectInitializer.McbDownloadsFolder))) return;
            var tracked = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "ls-files", "-z", "--cached", "--ignored", "--exclude-standard", "--", UnitGitProjectInitializer.McbDownloadsFolder);
            if (!tracked.Success) return;
            foreach (string entry in tracked.StandardOutput.Split('\0'))
            {
                string path = entry.Trim('\r', '\n');
                if (path.Length == 0) continue;
                snapshot.TrackedIgnoredDownloads.Add(path);
                var file = new FileInfo(Path.Combine(ProjectRoot, path));
                if (file.Exists) snapshot.TrackedIgnoredDownloadBytes += file.Length;
            }
        }

        // The visited commit, the branch to go back to and how far ahead of it that branch is: only while HEAD is detached.
        private void ReadDetachedHead(UnitGitSnapshot snapshot, string visitBranch)
        {
            snapshot.DetachedCommit = ReadCommit("HEAD");
            snapshot.DetachedBy = UnitGitConflicts.DetachedBy(ProjectRoot);
            GitCommandResult previous = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--abbrev-ref", "@{-1}");
            string previousBranch = previous.Success ? FirstNonEmptyLine(previous.StandardOutput) : string.Empty;
            // A detached commit before this one prints nothing or a hash: only an existing local branch is a way back.
            snapshot.PreviousBranch = IsLocalBranch(snapshot, previousBranch) ? previousBranch : string.Empty;
            snapshot.ReturnBranch = IsLocalBranch(snapshot, visitBranch) ? visitBranch.Trim() : snapshot.PreviousBranch;
            if (snapshot.ReturnBranch.Length == 0) return;
            GitCommandResult newer = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-list", "--count", "HEAD..refs/heads/" + snapshot.ReturnBranch, "--");
            if (newer.Success && int.TryParse(FirstNonEmptyLine(newer.StandardOutput), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                snapshot.NewerCommits = count;
        }

        private static bool IsLocalBranch(UnitGitSnapshot snapshot, string name)
        {
            return !string.IsNullOrWhiteSpace(name) && snapshot.Branches.Any(branch => !branch.IsRemote && branch.Name == name.Trim());
        }

        // One commit as the Log shows it, or null.
        private UnitGitCommit ReadCommit(string revision)
        {
            GitCommandResult result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "log", "-1", "--pretty=format:" + CommitLogFormat(), revision, "--");
            return result.Success ? ParseCommits(result.StandardOutput, string.Empty).FirstOrDefault() : null;
        }

        /// <summary>True when HEAD is a commit on its own rather than a branch (a visit, a rebase, another tool's checkout).</summary>
        internal bool IsHeadDetached()
        {
            // Exit 1: HEAD is not a symbolic ref. A branch without commits yet is still a branch.
            return RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD").ExitCode == 1;
        }

        /// <summary>
        /// Files changed since the last commit: tracked ones (edited, staged, deleted…) and new, untracked ones. Submodules are
        /// left out: neither shelving nor discarding touches what is checked out in them.
        /// </summary>
        internal GitCommandResult ReadChangeCount(out int tracked, out int untracked)
        {
            tracked = 0;
            untracked = 0;
            GitCommandResult status = ReadChangeStatus();
            if (!status.Success) return status;
            foreach (UnitGitStatusEntry change in ParseStatusV2Output(status.StandardOutput).Changes)
            {
                if (change.IsUntracked) untracked++;
                else if (change.IndexStatus != '!') tracked++;
            }

            return status;
        }

        private GitCommandResult ReadChangeStatus()
        {
            // A folder of new files counts once (normal, not -uall): only whether there are changes matters here.
            return RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "status", "--porcelain=v2", "-z", "--untracked-files=normal", "--ignore-submodules=all");
        }

        /// <summary>
        /// Files Git does not track that stand where <paramref name="target"/> has files of its own. Git refuses to check out
        /// over new files (<paramref name="untracked"/>) but overwrites ignored ones without a word (<paramref name="ignored"/>),
        /// and the next checkout of a commit without them deletes them.
        /// </summary>
        internal GitCommandResult ReadFilesInTheWay(string target, out List<string> untracked, out List<string> ignored)
        {
            untracked = new List<string>();
            ignored = new List<string>();
            // What the target has and HEAD has not: the only files a checkout writes where Git tracks nothing yet.
            GitCommandResult incoming = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "diff-tree", "-r", "-z", "--name-only", "--no-renames", "--diff-filter=A", "HEAD", target, "--");
            if (!incoming.Success) return incoming;
            List<string> paths = SplitNul(incoming.StandardOutput);
            if (paths.Count == 0) return incoming;
            GitCommandResult others = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "ls-files", "-z", "--others", "--exclude-standard");
            if (!others.Success) return others;
            // "--directory": a folder ignored as a whole (Library/) is one line, not every file in it.
            GitCommandResult ignoredOthers = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory");
            if (!ignoredOthers.Success) return ignoredOthers;
            string root = ProjectRoot;
            Func<string, bool> exists = path => File.Exists(Path.Combine(root, path));
            untracked.AddRange(UnitGitVisitChecks.InTheWay(paths, SplitNul(others.StandardOutput), exists));
            ignored.AddRange(UnitGitVisitChecks.InTheWay(paths, SplitNul(ignoredOthers.StandardOutput), exists));
            return incoming;
        }

        /// <summary>
        /// Commits HEAD has that no branch, tag or remote branch has (after <paramref name="since"/>, the visited commit, when
        /// given): checking out a branch leaves them to Git's reflog, out of the Log.
        /// </summary>
        internal GitCommandResult ReadCommitsOnNoBranch(string since, out int count)
        {
            count = 0;
            // Not "--all": a shelf made here (refs/stash) would count as keeping them.
            var args = new List<string> { "rev-list", "--count", "HEAD", "--not", "--branches", "--tags", "--remotes" };
            if (!string.IsNullOrWhiteSpace(since) && ResolveCommit(since, out string hash).Success) args.Add(hash);
            args.Add("--");
            GitCommandResult result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, args.ToArray());
            if (result.Success) int.TryParse(FirstNonEmptyLine(result.StandardOutput), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
            return result;
        }

        /// <summary>A file as committed ("HEAD", a hash), its path relative to the project; null when that commit has none.</summary>
        internal string ReadFileAt(string revision, string path)
        {
            string name = revision + ":" + path;
            if (UnitGitObjects.IsStable(name))
            {
                var bytes = new MemoryStream();
                UnitGitObjects.Answer answer;
                string type;
                using (var objects = UnitGitObjects.Open(ProjectRoot)) answer = objects.Read(name, bytes, out type, out _);
                if (answer == UnitGitObjects.Answer.Found)
                    return type == "blob" ? UnitGitRunner.Decode(bytes.GetBuffer(), (int)bytes.Length) : null;
                if (answer == UnitGitObjects.Answer.Missing) return null;
            }

            GitCommandResult blob = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "cat-file", "blob", revision + ":./" + path);
            return blob.Success ? blob.StandardOutput : null;
        }

        /// <summary>
        /// Checks out one commit without moving any branch ("git checkout --detach"): every later commit stays on its branch
        /// and in the Log. Local changes are shelved first under <paramref name="shelfMessage"/> (and come back if the checkout
        /// fails), or with <paramref name="discardChanges"/> tracked files are put back (new files stay). With neither, edited
        /// tracked files refuse the visit. From a branch every change is shelved; from a visited commit only what stands in the
        /// way, and other new files stay as with Discard. Ignored files the commit has its own copy of refuse the visit unless
        /// <paramref name="overwriteIgnored"/>: Git would overwrite them, and never shelves them.
        /// </summary>
        internal GitCommandResult VisitCommit(string revision, string shelfMessage, bool discardChanges, out bool shelved, bool overwriteIgnored = false)
        {
            shelved = false;
            GitCommandResult resolved = ResolveCommit(revision, out string hash);
            if (!resolved.Success) return resolved;
            GitCommandResult running = RefuseRunningOperation();
            if (running != null) return running;

            GitCommandResult changes = ReadChangeCount(out int tracked, out int untracked);
            if (!changes.Success) return changes;
            GitCommandResult way = ReadFilesInTheWay(hash, out List<string> inTheWay, out List<string> ignored);
            if (!way.Success) return way;
            if (ignored.Count > 0 && !overwriteIgnored)
                return Failure(Plural(ignored.Count, "ignored file") + " would be overwritten by " + ShortHash(hash) + "'s copy (" +
                               UnitGitVisitChecks.ListPaths(ignored, 3, ", ") + "). Move them out of the project first.");

            bool fromBranch = !IsHeadDetached();
            string left = null;
            if (!string.IsNullOrWhiteSpace(shelfMessage) && (fromBranch ? tracked + untracked : tracked + inTheWay.Count) > 0)
            {
                GitCommandResult shelve = fromBranch ? ShelveAll(shelfMessage) : ShelveChanges(shelfMessage, inTheWay);
                if (!shelve.Success) return shelve;
                shelved = true;
                // What Git sees right after shelving, to put the shelf back onto only that (see PutShelfBack). A read that
                // did not finish (a reload) is no answer: null, and the shelf then stays where it is.
                GitCommandResult after = ReadChangeStatus();
                left = after.Success ? after.StandardOutput : null;
            }
            else if (!discardChanges && tracked > 0)
            {
                return Failure(Plural(tracked, "file") + " changed since the last commit. Shelve or commit them before visiting " + ShortHash(hash) + ".");
            }
            else if (!discardChanges && inTheWay.Count > 0)
            {
                return Failure(NewFilesInTheWay(inTheWay, ShortHash(hash)));
            }

            var checkout = new List<string> { "checkout" };
            if (discardChanges && !shelved) checkout.Add("--force");
            // Under the check above: Git refuses rather than overwrite an ignored file (with --force it overwrites anyway).
            if (!overwriteIgnored) checkout.Add("--no-overwrite-ignore");
            checkout.Add("--detach");
            checkout.Add(hash);
            GitCommandResult result = RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, checkout.ToArray());
            if (result.Success || !shelved) return result;

            // Nothing was visited: the shelved changes go back where they were.
            return PutShelfBack(result, shelfMessage, left, out shelved);
        }

        /// <summary>
        /// Back to <paramref name="branch"/> after a visit, then brings back the shelf made before visiting
        /// (<paramref name="shelfMessage"/>), if any. What changed while visiting refuses it, unless <paramref name="changedShelf"/>
        /// shelves it (edited files and the new files in the branch's way) or <paramref name="discardChanges"/> puts it back;
        /// other new files always stay (nothing is cleaned). Nothing is shelved before the way back is known to be open.
        /// </summary>
        internal GitCommandResult ReturnFromVisit(string branch, bool discardChanges, string shelfMessage, string changedShelf = null)
        {
            branch = (branch ?? string.Empty).Trim();
            GitCommandResult open = CanReturnTo(branch);
            if (!open.Success) return open;

            GitCommandResult changes = ReadChangeCount(out int tracked, out _);
            if (!changes.Success) return changes;
            GitCommandResult way = ReadFilesInTheWay("refs/heads/" + branch, out List<string> inTheWay, out _);
            if (!way.Success) return way;
            bool shelved = false;
            string left = null;
            if (!string.IsNullOrWhiteSpace(changedShelf) && tracked + inTheWay.Count > 0)
            {
                GitCommandResult shelve = ShelveChanges(changedShelf, inTheWay);
                if (!shelve.Success) return shelve;
                shelved = true;
                // What Git sees right after shelving, to put the shelf back onto only that (see PutShelfBack). A read that
                // did not finish (a reload) is no answer: null, and the shelf then stays where it is.
                GitCommandResult after = ReadChangeStatus();
                left = after.Success ? after.StandardOutput : null;
            }
            else if (!discardChanges && tracked > 0)
            {
                return Failure(Plural(tracked, "file") + " changed while visiting. Discard or shelve them before going back to ‘" + branch + "’.");
            }
            else if (!discardChanges && inTheWay.Count > 0)
            {
                return Failure(NewFilesInTheWay(inTheWay, "‘" + branch + "’"));
            }

            // "--" after the branch: a file called like the branch is never read as a path to restore.
            GitCommandResult checkout = discardChanges && !shelved
                ? RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", "--force", branch, "--")
                : RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", branch, "--");
            if (!checkout.Success) return shelved ? PutShelfBack(checkout, changedShelf, left, out _) : checkout;
            if (FindShelf(shelfMessage).Length == 0) return checkout;

            GitCommandResult restored = UnshelveByMessage(shelfMessage);
            return restored.Success
                ? checkout
                : Failure("Back on ‘" + branch + "’, but your changes from before the visit stay on the shelf ‘" + shelfMessage.Trim() + "’: " + restored.Message);
        }

        /// <summary>Whether Back can check out <paramref name="branch"/>: it exists, and no merge, rebase or bisect is under way.</summary>
        internal GitCommandResult CanReturnTo(string branch)
        {
            branch = (branch ?? string.Empty).Trim();
            if (branch.Length == 0 || branch.StartsWith("-", StringComparison.Ordinal))
                return Failure("Choose the branch to go back to.");
            GitCommandResult exists = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch);
            if (!exists.Success)
                return Failure("Branch ‘" + branch + "’ no longer exists. Choose another branch to go back to.");
            return RefuseRunningOperation() ?? exists;
        }

        // A merge, cherry-pick, revert, rebase or bisect under way: a checkout would leave it half done.
        private GitCommandResult RefuseRunningOperation()
        {
            if (UnitGitConflicts.Operation(ProjectRoot, out string current) != UnitGitOperation.None)
                return Failure("Finish or cancel the current operation first (" + current + ").");
            return UnitGitConflicts.IsBisecting(ProjectRoot) ? Failure("Finish the bisect first (git bisect reset).") : null;
        }

        /// <summary>
        /// Shelves the edited tracked files and the new files given, and no other new file: those stay on disk. The new files
        /// are staged first, so a plain "stash push" takes them.
        /// </summary>
        internal GitCommandResult ShelveChanges(string message, IList<string> newFiles)
        {
            bool staged = newFiles != null && newFiles.Count > 0;
            if (staged)
            {
                GitCommandResult add = RunGitForPaths(LongTimeoutMilliseconds, newFiles, "add");
                if (!add.Success) return add;
            }

            GitCommandResult shelve = RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "stash", "push", "-m", message.Trim());
            if (!shelve.Success && staged) RunGitForPaths(LongTimeoutMilliseconds, newFiles, "reset", "-q");
            return shelve;
        }

        // After a failed checkout the shelf goes back only onto the files as they were (before: what Git saw right after
        // shelving). Git LFS without its objects, or Git stopped midway, leaves part of the other commit written, and a
        // shelf popped onto that mixes the two versions.
        private GitCommandResult PutShelfBack(GitCommandResult checkout, string shelfMessage, string before, out bool stillShelved)
        {
            stillShelved = true;
            string shelf = "‘" + shelfMessage.Trim() + "’";
            GitCommandResult state = ReadChangeStatus();
            if (!state.Success || before == null || state.StandardOutput != before)
                return Failure(checkout.Message + "\nGit stopped partway, so some files are already the other version. Your changes stay on the shelf " + shelf +
                               ": roll the changed files back in Local Changes first, then bring the shelf back.");
            GitCommandResult restored = UnshelveByMessage(shelfMessage);
            stillShelved = !restored.Success;
            return Failure(checkout.Message + (restored.Success ? "\nYour changes are back." : "\nYour changes stay on the shelf " + shelf + ": " + restored.Message));
        }

        private static string NewFilesInTheWay(IList<string> paths, string target)
        {
            bool one = paths.Count == 1;
            return Plural(paths.Count, "new file") + (one ? " is" : " are") + " where " + target + " has its own (" + UnitGitVisitChecks.ListPaths(paths, 3, ", ") +
                   "). Discard or shelve " + (one ? "it" : "them") + " first.";
        }

        // "-z" output: one path per NUL, exactly as Git has it (no quoting).
        private static List<string> SplitNul(string text)
        {
            return (text ?? string.Empty).Split('\0').Select(entry => entry.Trim('\r', '\n')).Where(entry => entry.Length > 0).ToList();
        }

        /// <summary>The shelf ("stash@{n}") made with exactly this message, or empty.</summary>
        internal string FindShelf(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            GitCommandResult list = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "stash", "list", "--format=%gd%x1f%gs");
            return list.Success ? FindShelfIn(list.StandardOutput, message) : string.Empty;
        }

        // In "stash list --format=%gd%x1f%gs" output: "stash@{0}<US>On main: message" (a detached HEAD says "On (no branch)").
        internal static string FindShelfIn(string stashList, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            string wanted = message.Trim();
            foreach (string line in SplitLines(stashList))
            {
                int separator = line.IndexOf(FieldSeparator);
                if (separator <= 0) continue;
                string subject = line.Substring(separator + 1).TrimEnd();
                if (subject == wanted || subject.EndsWith(": " + wanted, StringComparison.Ordinal)) return line.Substring(0, separator);
            }

            return string.Empty;
        }

        // Pops the shelf with this message. A conflict keeps the shelf and leaves the conflicted files, as "stash pop" does.
        private GitCommandResult UnshelveByMessage(string message)
        {
            string shelf = FindShelf(message);
            return shelf.Length == 0
                ? Failure("The shelf ‘" + message.Trim() + "’ was not found.")
                : RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "stash", "pop", shelf);
        }

        /// <summary>
        /// Writes one version of a file byte for byte ("HEAD:path", ":2:path", "hash:path"): models and textures are binary,
        /// and <see cref="RunGit"/> reads output as text. False when that version does not exist.
        /// </summary>
        public bool WriteBlob(string revisionPath, string destination)
        {
            if (UnitGitObjects.IsStable(revisionPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
                UnitGitObjects.Answer answer;
                string type;
                using (var file = File.Create(destination))
                using (var objects = UnitGitObjects.Open(ProjectRoot))
                    answer = objects.Read(revisionPath, file, out type, out _);
                if (answer == UnitGitObjects.Answer.Found && type == "blob") return true;
                File.Delete(destination);
                if (answer != UnitGitObjects.Answer.Failed) return false;
            }

            // A process of its own (an index stage, a name with a line break), through the same runner as every Git command.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            bool complete = false;
            try
            {
                GitCommandResult result;
                using (var file = File.Create(destination))
                {
                    result = ReadGitStream(GitAccess.Read, LongTimeoutMilliseconds, (bytes, offset, count) =>
                    {
                        file.Write(bytes, offset, count);
                        return true;
                    }, "--literal-pathspecs", "cat-file", "blob", revisionPath);
                }
                complete = result.Success;
                return complete;
            }
            finally
            {
                if (!complete) File.Delete(destination);
            }
        }

        /// <summary>Stops tracking these files: they stay on disk and their removal is staged for the next commit.</summary>
        public GitCommandResult StopTracking(IList<string> paths)
        {
            var result = RunGitForPaths(LongTimeoutMilliseconds, paths, "rm", "--cached", "--quiet", "--ignore-unmatch");
            if (result.Success)
                result.StandardOutput = "Stopped tracking " + paths.Count + " files. They stay on disk; commit to record it.";
            return result;
        }

        /// <summary>Includes these files in the next commit as they are on disk, deletions included.</summary>
        public GitCommandResult Stage(IList<string> paths)
        {
            List<string> selected = SelectedPaths(paths);
            if (selected.Count == 0)
                return Failure("No files selected.");
            return RunGitForPaths(LongTimeoutMilliseconds, selected, "add", "-A");
        }

        /// <summary>Leaves these files out of the next commit again. Their content on disk is unchanged.</summary>
        public GitCommandResult Unstage(IList<string> paths)
        {
            List<string> selected = SelectedPaths(paths);
            if (selected.Count == 0)
                return Failure("No files selected.");
            if (HasCommits())
                return RunGitForPaths(LongTimeoutMilliseconds, selected, "reset", "-q", "HEAD");
            // Before the first commit there is no HEAD to reset to: everything staged is new. --cached keeps the files on disk.
            return RunGitForPaths(LongTimeoutMilliseconds, selected, "rm", "--cached", "-r", "-f", "-q", "--ignore-unmatch");
        }

        /// <summary>
        /// Discards the local changes of tracked files: the last commit's version comes back, staged and on disk. Files that
        /// were only added to the index become untracked again and stay on disk. Untracked files are never touched; pass
        /// both paths of a rename.
        /// </summary>
        public GitCommandResult Rollback(IList<string> paths)
        {
            List<string> selected = SelectedPaths(paths);
            if (selected.Count == 0)
                return Failure("No files selected.");

            // Only paths Git lists are passed on, so a selected untracked file never reaches restore (which would fail) or rm.
            var committed = new List<string>();
            if (HasCommits())
            {
                GitCommandResult listed = ListPaths(selected, committed, "ls-tree", "-r", "--name-only", "HEAD");
                if (!listed.Success)
                    return listed;
            }

            var indexed = new List<string>();
            GitCommandResult index = ListPaths(selected, indexed, "ls-files", "--cached");
            if (!index.Success)
                return index;

            var inHead = new HashSet<string>(committed, StringComparer.Ordinal);
            List<string> added = indexed.Where(path => !inHead.Contains(path)).Distinct(StringComparer.Ordinal).ToList();
            if (committed.Count > 0)
            {
                GitCommandResult restored = RunGitForPaths(LongTimeoutMilliseconds, committed, "restore", "--source=HEAD", "--staged", "--worktree");
                if (!restored.Success)
                    return restored;
            }

            if (added.Count > 0)
            {
                // Not in HEAD, so restore would delete them from disk. -f: dropping the staged content is the point of a rollback.
                GitCommandResult removed = RunGitForPaths(LongTimeoutMilliseconds, added, "rm", "--cached", "-f", "-q");
                if (!removed.Success)
                    return removed;
            }

            if (committed.Count == 0 && added.Count == 0)
                return new GitCommandResult { StandardOutput = "Nothing to roll back: the selected files are not tracked." };

            var message = new StringBuilder();
            if (committed.Count > 0)
                message.Append("Rolled back ").Append(Plural(committed.Count, "file")).Append(" to the last commit.");
            if (added.Count > 0)
                message.Append(message.Length > 0 ? " " : string.Empty)
                    .Append("Untracked again and kept on disk: ").Append(Plural(added.Count, "added file")).Append('.');
            return new GitCommandResult { StandardOutput = message.ToString() };
        }

        public UnitGitCommitDetails GetCommitDetails(string fullHash)
        {
            if (string.IsNullOrWhiteSpace(fullHash))
            {
                return null;
            }

            // Quoted paths keep the file list ASCII whatever the output encoding, and tabs inside names escaped.
            var result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, new[]
            {
                "-c",
                "core.quotePath=true",
                "show",
                "--no-ext-diff",
                "--name-status",
                "-M",
                "--date=format-local:%m/%d/%Y %I:%M %p",
                "--pretty=format:" + CommitDetailsFormat(),
                fullHash
            });

            if (!result.Success)
            {
                return new UnitGitCommitDetails
                {
                    Commit = new UnitGitCommit
                    {
                        FullHash = fullHash,
                        Subject = result.Message
                    }
                };
            }

            return ParseCommitDetails(result.StandardOutput);
        }

        /// <summary>
        /// The details of several commits (a multi-selection in the Log) from one "git log --no-walk=unsorted --stdin": the
        /// same fields and file lists as <see cref="GetCommitDetails(string)"/> gives one by one (merges as their combined
        /// change, as git show lists it). Commits Git could not read are left out.
        /// </summary>
        internal Dictionary<string, UnitGitCommitDetails> GetCommitDetails(IList<string> fullHashes)
        {
            var found = new Dictionary<string, UnitGitCommitDetails>(StringComparer.Ordinal);
            List<string> hashes = (fullHashes ?? Array.Empty<string>())
                .Where(hash => !string.IsNullOrWhiteSpace(hash) && !hash.Trim().StartsWith("-", StringComparison.Ordinal))
                .Select(hash => hash.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (hashes.Count == 0) return found;
            string[] options = { "log", "--no-walk=unsorted", "-z", "--no-ext-diff", "--name-status", "-M", "--cc",
                "--date=format-local:%m/%d/%Y %I:%M %p", "--pretty=format:" + CommitDetailsFormat() };
            GitCommandResult result = RunWithList(GitAccess.Read, null, DefaultTimeoutMilliseconds, hashes, false, options.Concat(new[] { "--stdin" }).ToArray(), options, new[] { "--" });
            if (!result.Success) return found;
            foreach (UnitGitCommitDetails details in ParseCommitDetailsList(result.StandardOutput, hashes))
                found[details.Commit.FullHash] = details;
            return found;
        }

        // Header fields, then the body, then a NUL: the body may span lines, and the file list follows it.
        private static string CommitDetailsFormat()
        {
            return "%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ad%x1f%D%x1f%cn%x1f%ce%x1f%cd%x1f" + ReleaseTrailerFormat + "%x1f%P%x1f%at%x1f%b%x00";
        }

        public GitCommandResult Fetch()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before fetching.");
            }

            return RunGit(GitAccess.Change, LongTimeoutMilliseconds, "fetch", "--all", "--prune");
        }

        public GitCommandResult PullFastForward()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before pulling.");
            }

            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "pull", "--ff-only");
        }

        /// <summary>
        /// Pushes the current branch. A branch without upstream is published to origin (else the first remote) and
        /// starts tracking it there. Never forces.
        /// </summary>
        public GitCommandResult Push()
        {
            List<string> remotes = GetRemoteNames();
            if (remotes.Count == 0)
            {
                return Failure("This repository has no remote. Set one up first.");
            }

            // The full ref: --short would print "heads/main" when a tag is also called main.
            GitCommandResult branchResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            string branchRef = branchResult.Success ? FirstNonEmptyLine(branchResult.StandardOutput) : string.Empty;
            if (!branchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                return Failure("Check out a branch before pushing.");
            }

            string branch = branchRef.Substring("refs/heads/".Length);

            // The configured upstream, even when its remote branch was deleted since: push recreates it there.
            GitCommandResult upstream = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "config", "--get", "branch." + branch + ".merge");
            if (upstream.Success && FirstNonEmptyLine(upstream.StandardOutput).Length > 0)
            {
                return RunGit(GitAccess.Change, LongTimeoutMilliseconds, "push");
            }

            string remote = remotes.FirstOrDefault(name => name == "origin") ?? remotes[0];
            return RunGit(GitAccess.Change, LongTimeoutMilliseconds, "push", "-u", remote, branch);
        }

        /// <summary>Copies a commit onto the current branch. Conflicts fail and leave Git's cherry-pick in progress.</summary>
        public GitCommandResult CherryPick(string fullHash)
        {
            return ApplyCommit(fullHash, UnitGitOperation.CherryPick, "cherry-pick");
        }

        /// <summary>Adds a commit that undoes this one. Conflicts fail and leave Git's revert in progress.</summary>
        public GitCommandResult RevertCommit(string fullHash)
        {
            return ApplyCommit(fullHash, UnitGitOperation.Revert, "revert", "--no-edit");
        }

        private GitCommandResult ApplyCommit(string fullHash, UnitGitOperation operation, params string[] command)
        {
            GitCommandResult resolved = ResolveCommit(fullHash, out string hash);
            if (!resolved.Success)
            {
                return resolved;
            }

            if (UnitGitConflicts.Operation(ProjectRoot, out string current) != UnitGitOperation.None)
            {
                return Failure("Finish or cancel the current operation first (" + current + ").");
            }

            var args = new List<string>(command) { hash };
            GitCommandResult result = RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, args.ToArray());
            if (result.Success || UnitGitConflicts.Operation(ProjectRoot, out _) != operation)
            {
                return result;
            }

            // Git stops on an empty result too (the changes are already here). Nothing conflicts and nothing is staged:
            // cancel it rather than leave an operation with nothing to resolve.
            // Both reads must have answered (a reload stops them at once): unless both say so, the operation stays for the user.
            GitCommandResult unmerged = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "ls-files", "-u");
            GitCommandResult index = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "diff", "--cached", "--quiet");
            bool conflicted = !unmerged.Success || unmerged.StandardOutput.Trim().Length > 0;
            bool staged = index.ExitCode != 0; // 1: something staged; an interrupted or failed read is not 0 either
            if (conflicted || staged)
            {
                return result;
            }

            GitCommandResult abort = RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, command[0], "--abort");
            return abort.Success
                ? Failure("Nothing to apply: the changes of " + ShortHash(hash) + " are already on this branch.")
                : abort;
        }

        // The full hash of a commit, refusing text Git would read as an option.
        private GitCommandResult ResolveCommit(string revision, out string fullHash)
        {
            fullHash = string.Empty;
            if (string.IsNullOrWhiteSpace(revision) || revision.Trim().StartsWith("-", StringComparison.Ordinal))
            {
                return Failure("Select a commit first.");
            }

            GitCommandResult resolved = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", revision.Trim() + "^{commit}");
            fullHash = resolved.Success ? FirstNonEmptyLine(resolved.StandardOutput) : string.Empty;
            return fullHash.Length > 0 ? resolved : Failure("Commit " + ShortHash(revision) + " was not found.");
        }

        public GitCommandResult StageAll()
        {
            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "add", "-A");
        }

        public GitCommandResult UnstageAll()
        {
            return RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "reset");
        }

        public GitCommandResult Commit(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RunCommitWithMessageFile(new[] { "commit" }, message);
        }

        public GitCommandResult Commit(string message, string trailingParagraph)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            if (string.IsNullOrWhiteSpace(trailingParagraph))
            {
                return Commit(message);
            }

            return RunCommitWithMessageFile(new[] { "commit" }, message.Trim() + "\n\n" + trailingParagraph.Trim());
        }

        public GitCommandResult CommitAmend(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RunCommitWithMessageFile(new[] { "commit", "--amend" }, message);
        }

        public GitCommandResult GetHeadCommitMessage()
        {
            return GetCommitMessage("HEAD");
        }

        public GitCommandResult GetCommitMessage(string commitHash)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            var result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "log", "-1", "--format=%B", commitHash);
            if (result.Success)
            {
                result.StandardOutput = TrimTrailingNewlines(result.StandardOutput);
            }

            return result;
        }

        internal GitCommandResult ReadHeadState(out UnitGitHeadState state)
        {
            state = null;
            GitCommandResult result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "HEAD", "--symbolic-full-name", "HEAD");
            if (!result.Success)
            {
                return result;
            }

            string[] lines = SplitLines(result.StandardOutput).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length < 2)
            {
                return Failure("Could not read the current branch.");
            }

            state = new UnitGitHeadState { Head = lines[0], BranchRef = lines[1] == "HEAD" ? string.Empty : lines[1] };
            return result;
        }

        /// <summary>Resets HEAD only while it is still the branch and commit the user confirmed.</summary>
        internal GitCommandResult ResetCurrentBranch(string commitHash, UnitGitResetMode mode, UnitGitHeadState expected)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            GitCommandResult resolved = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", commitHash.Trim() + "^{commit}");
            if (!resolved.Success)
            {
                return resolved;
            }

            GitCommandResult current = ReadHeadState(out UnitGitHeadState state);
            if (!current.Success)
            {
                return current;
            }

            GitCommandResult changed = CheckHeadUnchanged("Reset", expected, state, true);
            if (changed != null)
            {
                return changed;
            }

            string targetHash = FirstNonEmptyLine(resolved.StandardOutput);
            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "reset", GetResetModeArgument(mode), targetHash);
        }

        private static GitCommandResult CheckHeadUnchanged(string operation, UnitGitHeadState expected, UnitGitHeadState current, bool sameCommit)
        {
            if (expected == null)
            {
                return Failure(operation + " has no confirmed branch. Nothing was changed; reopen the dialog to try again.");
            }

            if (!string.Equals(expected.BranchRef, current.BranchRef, StringComparison.Ordinal))
            {
                return Failure(operation + " was prepared for " + DescribeBranch(expected) + ", but " + DescribeBranch(current) +
                               " is checked out now. Nothing was changed; reopen the dialog to try again.");
            }

            if (sameCommit && !string.Equals(expected.Head, current.Head, StringComparison.Ordinal))
            {
                return Failure(operation + " was prepared for " + DescribeBranch(expected) + " at " + ShortHash(expected.Head) +
                               ", but it now points to " + ShortHash(current.Head) + ". Nothing was changed; reopen the dialog to try again.");
            }

            return null;
        }

        private static string DescribeBranch(UnitGitHeadState state)
        {
            return string.IsNullOrEmpty(state.BranchRef) ? "a detached HEAD" : "branch '" + state.BranchName + "'";
        }

        public GitCommandResult ShelveAll(string message)
        {
            string shelfMessage = string.IsNullOrWhiteSpace(message)
                ? "Mixer Git shelf " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                : message.Trim();

            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "stash", "push", "-u", "-m", shelfMessage);
        }

        private static string GetResetModeArgument(UnitGitResetMode mode)
        {
            switch (mode)
            {
                case UnitGitResetMode.Soft:
                    return "--soft";
                case UnitGitResetMode.Hard:
                    return "--hard";
                case UnitGitResetMode.Keep:
                    return "--keep";
                default:
                    return "--mixed";
            }
        }

        internal GitCommandResult RunCommitWithMessageFile(IEnumerable<string> baseArguments, string message,
            IDictionary<string, string> environment = null)
        {
            if (UnitGitRunner.CleanInput)
            {
                // The message on stdin: no temporary file to write and delete per commit.
                byte[] bytes = UnitGitRunner.Utf8.GetBytes(NormalizeCommitMessage(message) + "\n");
                var stdinArgs = new List<string>(baseArguments) { "-F", "-" };
                return RunGitWithInput(GitAccess.Exclusive, environment, LongTimeoutMilliseconds, bytes, stdinArgs.ToArray());
            }

            string messagePath = WriteTempCommitMessage(message);
            try
            {
                var args = new List<string>(baseArguments) { "-F", messagePath };
                return environment == null
                    ? RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, args.ToArray())
                    : RunGitWithEnvironment(GitAccess.Exclusive, environment, LongTimeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(messagePath);
            }
        }

        public UnitGitDiff GetFileDiff(UnitGitStatusEntry change) => GetFileDiff(change, UnitGitWhitespace.None);

        /// <summary>
        /// A changed file against its last commit, whole when it is not huge (so the viewer can fold and unfold what did not
        /// change), with the chosen whitespace ignored. Staged and unstaged changes show as two sections.
        /// </summary>
        public UnitGitDiff GetFileDiff(UnitGitStatusEntry change, UnitGitWhitespace whitespace)
        {
            var diff = new UnitGitDiff();
            if (change == null || string.IsNullOrWhiteSpace(change.Path))
            {
                AddDiffMessage(diff, "Select a changed file to inspect its diff.");
                return diff;
            }

            diff.Path = change.Path;
            diff.LeftTitle = "Repository";
            diff.RightTitle = "Current version";

            GitCommandResult unstagedResult = null;
            GitCommandResult stagedResult = null;
            string unstagedOutput = string.Empty;
            string stagedOutput = string.Empty;

            var file = new FileInfo(Path.Combine(ProjectRoot, change.Path));
            string context = ContextArgument(file.Exists ? file.Length : 0);
            if (change.IsUnstaged && !change.IsUntracked)
            {
                unstagedResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, DiffArguments(whitespace, context, false, change.Path));
                if (unstagedResult.Success)
                {
                    unstagedOutput = unstagedResult.StandardOutput;
                }
            }

            if (change.IsStaged)
            {
                stagedResult = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, DiffArguments(whitespace, context, true, change.Path));
                if (stagedResult.Success)
                {
                    stagedOutput = stagedResult.StandardOutput;
                }
            }

            bool hasStagedDiff = !string.IsNullOrWhiteSpace(stagedOutput);
            bool hasUnstagedDiff = !string.IsNullOrWhiteSpace(unstagedOutput);
            if (hasStagedDiff || hasUnstagedDiff)
            {
                if (hasStagedDiff && hasUnstagedDiff)
                {
                    diff.LeftTitle = "HEAD / Index";
                    diff.RightTitle = "Index / Working tree";
                    AppendDiffSection(diff, "Staged changes (HEAD -> index)");
                    ParseUnifiedDiff(stagedOutput, diff);
                    AppendDiffSection(diff, "Unstaged changes (index -> working tree)");
                    diff.EditableFrom = diff.Lines.Count;
                    ParseUnifiedDiff(unstagedOutput, diff);
                }
                else if (hasStagedDiff)
                {
                    diff.LeftTitle = "HEAD";
                    diff.RightTitle = "Staged";
                    ParseUnifiedDiff(stagedOutput, diff);
                }
                else
                {
                    diff.EditableFrom = 0;
                    ParseUnifiedDiff(unstagedOutput, diff);
                }

                if (diff.Lines.Count == 0)
                {
                    diff.EditableFrom = -1;
                    AddDiffMessage(diff, NoTextualDifferences);
                }

                return diff;
            }

            if (change.IsUntracked)
            {
                PopulateUntrackedDiff(diff, change.Path);
                return diff;
            }

            GitCommandResult failedResult = stagedResult != null && !stagedResult.Success
                ? stagedResult
                : unstagedResult;
            AddDiffMessage(diff, failedResult != null && !string.IsNullOrWhiteSpace(failedResult.Message)
                ? failedResult.Message
                : NoTextualDifferences);

            return diff;
        }

        /// <summary>
        /// One file of one commit against the commit's first parent: the left side is empty for a root commit or an added
        /// file, the right side for a deleted file. A renamed file is compared with its previous path.
        /// </summary>
        public UnitGitDiff GetCommitFileDiff(string fullHash, string path) => GetCommitFileDiff(fullHash, path, UnitGitWhitespace.None);

        /// <param name="hint">What the Log knows of the commit and the file (its details): with it, the diff is one read.
        /// Without it, the commit's parents and a rename source are read first.</param>
        public UnitGitDiff GetCommitFileDiff(string fullHash, string path, UnitGitWhitespace whitespace, UnitGitCommitFileHint hint = null)
        {
            var diff = new UnitGitDiff { Path = path ?? string.Empty, LeftTitle = "Before", RightTitle = ShortHash(fullHash) };
            if (string.IsNullOrWhiteSpace(fullHash) || fullHash.Trim().StartsWith("-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path))
            {
                AddDiffMessage(diff, "Select a changed file to inspect its diff.");
                return diff;
            }

            string hash = fullHash.Trim();
            string[] parents;
            if (hint != null && hint.Parents != null && !string.IsNullOrEmpty(hint.ShortHash))
            {
                parents = hint.Parents;
                diff.RightTitle = hint.ShortHash;
                // Git abbreviates every id of a repository to the same length (longer only to tell two apart).
                if (parents.Length > 0)
                    diff.LeftTitle = parents[0].Substring(0, Math.Min(parents[0].Length, Math.Max(hint.ShortHash.Length, 4)));
            }
            else
            {
                GitCommandResult commit = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "show", "-s", "--format=%h%x1f%p%x1f%P", hash);
                string[] fields = (SplitLines(commit.StandardOutput).FirstOrDefault(line => line.IndexOf(FieldSeparator) >= 0) ?? string.Empty)
                    .Split(FieldSeparator);
                if (!commit.Success || fields.Length < 3)
                {
                    AddDiffMessage(diff, commit.Success ? "Could not read commit " + ShortHash(hash) + "." : commit.Message);
                    return diff;
                }

                parents = ParseParents(fields[2]);
                string[] shortParents = ParseParents(fields[1]);
                diff.RightTitle = fields[0];
                if (parents.Length > 0)
                {
                    diff.LeftTitle = shortParents.Length > 0 ? shortParents[0] : ShortHash(parents[0]);
                }
            }

            string context = ContextArgument(CommittedSize(hash + ":" + path));
            string previousPath = hint?.PreviousPath ?? string.Empty;
            GitCommandResult result = previousPath.Length > 0
                ? ReadCommitFileDiff(hash, parents, whitespace, context, previousPath, path)
                : ReadCommitFileDiff(hash, parents, whitespace, context, path);
            // The details found renames already (the same detection, against the first parent), except on a merge, whose
            // details list the combined change.
            bool renameKnown = hint != null && parents.Length < 2;
            if (!renameKnown && result.Success && parents.Length > 0 && AddsFile(result.StandardOutput) && !(ReadSuperseded?.Invoke() ?? false))
            {
                // A path limit hides renames from Git: look up where an added file came from and compare the two paths.
                previousPath = FindRenameSource(parents[0], hash, path);
                if (previousPath.Length > 0)
                {
                    result = ReadCommitFileDiff(hash, parents, whitespace, context, previousPath, path);
                }
            }

            if (!result.Success)
            {
                AddDiffMessage(diff, result.Message);
                return diff;
            }

            ParseUnifiedDiff(result.StandardOutput, diff);
            if (diff.Lines.Count == 0)
            {
                AddDiffMessage(diff, previousPath.Length > 0 ? "Renamed from " + previousPath + " without changes." : NoTextualDifferences);
            }

            return diff;
        }

        // A committed file's size, from the kept-alive object reader when it can answer (0 when unknown).
        private long CommittedSize(string name)
        {
            UnitGitObjects.Answer answer;
            long size;
            using (var objects = UnitGitObjects.Open(ProjectRoot)) answer = objects.Describe(name, out _, out size);
            if (answer == UnitGitObjects.Answer.Found) return size;
            if (answer == UnitGitObjects.Answer.Missing) return 0;
            GitCommandResult read = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "--literal-pathspecs", "cat-file", "-s", name);
            return read.Success && long.TryParse(read.StandardOutput.Trim(), out long bytes) ? bytes : 0;
        }

        private GitCommandResult ReadCommitFileDiff(string hash, string[] parents, UnitGitWhitespace whitespace, string context, params string[] paths)
        {
            // git show compares a root commit with nothing; git diff compares any other one with its first parent, merges too.
            var args = parents.Length == 0
                ? new List<string> { "--literal-pathspecs", "show", "--no-ext-diff", "-M", context, "--format=" }
                : new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "-M", context };
            args.AddRange(WhitespaceArguments(whitespace));
            if (parents.Length == 0)
            {
                args.Add(hash);
            }
            else
            {
                args.Add(parents[0]);
                args.Add(hash);
            }

            args.Add("--");
            args.AddRange(paths);
            return RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, args.ToArray());
        }

        // Files up to a few megabytes come whole (the viewer folds what did not change), bigger ones with 80 lines around
        // each change. (A context of int.MaxValue lines makes Git repeat hunks: a million is plenty.)
        private const long WholeFileDiffBytes = 3 * 1024 * 1024;

        private static string ContextArgument(long bytes)
        {
            return bytes <= WholeFileDiffBytes ? "--unified=1000000" : "--unified=80";
        }

        private static string[] DiffArguments(UnitGitWhitespace whitespace, string context, bool cached, string path)
        {
            var args = new List<string> { "--literal-pathspecs", "diff" };
            if (cached)
            {
                args.Add("--cached");
            }

            args.Add("--no-ext-diff");
            args.Add(context);
            args.AddRange(WhitespaceArguments(whitespace));
            args.Add("--");
            args.Add(path);
            return args.ToArray();
        }

        internal static IEnumerable<string> WhitespaceArguments(UnitGitWhitespace whitespace)
        {
            switch (whitespace)
            {
                case UnitGitWhitespace.Trim:
                    return new[] { "--ignore-space-change" };
                case UnitGitWhitespace.Ignore:
                    return new[] { "--ignore-all-space" };
                case UnitGitWhitespace.IgnoreAndBlankLines:
                    return new[] { "--ignore-all-space", "--ignore-blank-lines" };
                default:
                    return Array.Empty<string>();
            }
        }

        private string FindRenameSource(string parent, string hash, string path)
        {
            GitCommandResult renames = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "-c", "core.quotePath=true", "diff", "--no-ext-diff",
                "--name-status", "-M", "--diff-filter=R", parent, hash);
            if (!renames.Success)
            {
                return string.Empty;
            }

            foreach (string line in SplitLines(renames.StandardOutput))
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 3 && UnquotePath(parts[2]) == path)
                {
                    return UnquotePath(parts[1]);
                }
            }

            return string.Empty;
        }

        private static bool AddsFile(string unifiedDiff)
        {
            return SplitLines(unifiedDiff)
                .TakeWhile(line => !line.StartsWith("@@", StringComparison.Ordinal))
                .Any(line => line.StartsWith("new file mode ", StringComparison.Ordinal));
        }

        private static void AddDiffMessage(UnitGitDiff diff, string message)
        {
            diff.Lines.Add(new UnitGitDiffLine
            {
                Right = message,
                Kind = UnitGitDiffLineKind.Context
            });
        }

        public GitCommandResult Checkout(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = "No branch selected."
                };
            }

            // A checkout writes the whole project: the long timeout, as a visit has (stopped midway, it leaves two versions mixed).
            if (!branch.IsRemote)
            {
                return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", branch.Name);
            }

            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", "-t", branch.Name);
        }

        public GitCommandResult CreateBranch(string branchName, string startPoint)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return Failure("Branch name is required.");
            }

            branchName = branchName.Trim();
            var validation = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "check-ref-format", "--branch", branchName);
            if (!validation.Success)
            {
                return Failure("Invalid branch name: " + branchName);
            }

            if (string.IsNullOrWhiteSpace(startPoint))
            {
                return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", "-b", branchName);
            }

            return RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", "-b", branchName, startPoint);
        }

        public GitCommandResult UpdateBranch(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return Failure("Select a branch first.");
            }

            if (branch.IsRemote)
            {
                return Fetch();
            }

            branch = GetBranches().FirstOrDefault(candidate => !candidate.IsRemote && candidate.FullRef == branch.FullRef);
            if (branch == null)
                return Failure("The selected branch no longer exists. Refresh the branch list.");
            if (string.IsNullOrWhiteSpace(branch.Upstream))
                return Failure("Selected branch has no upstream. Fetch still works, but there is nothing to fast-forward.");

            GitCommandResult checkout = RunGit(GitAccess.Exclusive, LongTimeoutMilliseconds, "checkout", branch.Name);
            if (!checkout.Success)
            {
                return checkout;
            }

            GitCommandResult current = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            if (!current.Success || FirstNonEmptyLine(current.StandardOutput) != branch.FullRef)
            {
                return Failure("The current branch changed during Update. Refresh and try again.");
            }

            return PullFastForward();
        }

        public GitCommandResult DeleteBranch(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return Failure("Select a branch first.");
            }

            if (branch.IsCurrent)
            {
                return Failure("Cannot delete the current branch. Checkout another branch first.");
            }

            if (branch.IsRemote)
            {
                return RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "branch", "-dr", branch.Name);
            }

            return RunGit(GitAccess.Exclusive, DefaultTimeoutMilliseconds, "branch", "-d", branch.Name);
        }

        private static List<string> SelectedPaths(IList<string> paths)
        {
            return paths == null
                ? new List<string>()
                : paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
        }

        private static string Plural(int count, string noun)
        {
            return count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
        }

        public static string GetUnityProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        public static bool IsUnityProject(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(projectRoot, "Assets")) &&
                   Directory.Exists(Path.Combine(projectRoot, "ProjectSettings")) &&
                   File.Exists(Path.Combine(projectRoot, "Packages", "manifest.json"));
        }

        public static bool HasRepository(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            string gitPath = Path.Combine(projectRoot, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        private bool HasRemotes()
        {
            return GetRemoteNames().Count > 0;
        }

        internal List<string> GetRemoteNames()
        {
            var result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "remote");
            if (!result.Success)
            {
                return new List<string>();
            }

            return SplitLines(result.StandardOutput)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<UnitGitBranch> GetBranches()
        {
            var result = RunGit(GitAccess.Read, 
                DefaultTimeoutMilliseconds,
                "branch",
                "--all",
                "--format=%(refname)\t%(refname:short)\t%(HEAD)\t%(upstream:short)\t%(objectname:short)");

            if (!result.Success)
            {
                return new List<UnitGitBranch>();
            }

            return ParseBranches(result.StandardOutput);
        }

        internal bool HasCommits()
        {
            var result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, "rev-parse", "--verify", "HEAD");
            return result.Success;
        }

        internal List<UnitGitCommit> GetCommits(string logSearch, int maxCount = 250, UnitGitLogFilter filter = null)
        {
            if (maxCount <= 0) return new List<UnitGitCommit>();
            var snapshot = new UnitGitSnapshot();
            ReadHistory(snapshot, new UnitGitSnapshotRequest { LogSearch = logSearch, HistoryLimit = maxCount, Filter = filter });
            return snapshot.Commits;
        }

        // One commit as the history lists it. The age is not read: it is worded when shown (UnitGitTime.GitRelative).
        // The release trailer placeholder's key=, valueonly and separator= options are all in Git 2.26, the oldest Mixer Git runs.
        internal static string CommitLogFormat()
        {
            return "%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%D%x1f" + ReleaseTrailerFormat + "%x1f%P%x1f%at";
        }

        private static void AddLogFilterArguments(List<string> args, UnitGitLogFilter filter)
        {
            if (filter != null && !string.IsNullOrWhiteSpace(filter.Author))
            {
                // --fixed-strings and --regexp-ignore-case apply to --author as well: "Ada [VR]" is matched as typed.
                args.Add("--fixed-strings");
                args.Add("--regexp-ignore-case");
                args.Add("--author=" + filter.Author.Trim());
            }

            if (filter != null && filter.Since.HasValue)
            {
                // Unspecified kinds are local, like a date picked in the editor. Git reads the explicit UTC offset.
                args.Add("--since=" + filter.Since.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+0000'", CultureInfo.InvariantCulture));
            }

            string branch = (filter?.Branch ?? string.Empty).Trim();
            if (branch.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Could not read Git history: '" + branch + "' is not a branch.");
            }

            // One file or folder's history, as JetBrains' "Show History": parents rewritten so the graph joins the commits
            // shown, and a file followed through its renames.
            string path = (filter?.Path ?? string.Empty).Trim().Replace('\\', '/');
            if (path.Length > 0)
            {
                args.Add("--parents");
                if (Path.HasExtension(path)) args.Add("--follow");
            }

            // "--" keeps a branch named like a file from being read as a path.
            args.Add(branch.Length > 0 ? branch : "--all");
            args.Add("--");
            if (path.Length > 0) args.Add(":(literal)" + path);
        }

        private void PopulateUntrackedDiff(UnitGitDiff diff, string projectPath)
        {
            string fullPath = GetSafeProjectPath(projectPath);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Untracked file is not available on disk.",
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (info.Length > MaxUntrackedDiffPreviewBytes)
                {
                    AddSkippedUntrackedPreview(diff, "large file", info.Length);
                    return;
                }

                if (LooksBinaryFile(fullPath))
                {
                    AddSkippedUntrackedPreview(diff, "binary file", info.Length);
                    return;
                }
            }
            catch (Exception ex)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Unable to inspect this untracked file: " + ex.Message,
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath);
            }
            catch (Exception ex)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Unable to display this untracked file: " + ex.Message,
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            foreach (string line in lines)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = line,
                    Kind = UnitGitDiffLineKind.Added
                });
                diff.DifferenceCount++;
            }
        }

        private static void AddSkippedUntrackedPreview(UnitGitDiff diff, string reason, long bytes)
        {
            diff.Lines.Add(new UnitGitDiffLine
            {
                Right = "Untracked " + reason + " preview skipped (" + FormatBytes(bytes) + ").",
                Kind = UnitGitDiffLineKind.Context
            });
        }

        private static bool LooksBinaryFile(string fullPath)
        {
            var buffer = new byte[BinarySniffBytes];
            int bytesRead;
            using (var stream = File.OpenRead(fullPath))
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);
            }

            int suspiciousControls = 0;
            for (int i = 0; i < bytesRead; i++)
            {
                byte value = buffer[i];
                if (value == 0)
                {
                    return true;
                }

                if (value < 32 && value != 9 && value != 10 && value != 12 && value != 13)
                {
                    suspiciousControls++;
                }
            }

            return bytesRead > 0 && suspiciousControls > bytesRead / 10;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024f).ToString("0.#") + " KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (1024f * 1024f)).ToString("0.#") + " MB";
            }

            return (bytes / (1024f * 1024f * 1024f)).ToString("0.#") + " GB";
        }

        private string GetSafeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return string.Empty;
            }

            string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar)));
            // With the separator: "…/Project2/x" is not inside "…/Project".
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return fullPath;
        }

        private static void AppendDiffSection(UnitGitDiff diff, string title)
        {
            diff.LeftEndsWithoutNewline = false;
            diff.RightEndsWithoutNewline = false;
            if (diff.Lines.Count > 0)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Kind = UnitGitDiffLineKind.Context
                });
            }

            diff.Lines.Add(new UnitGitDiffLine
            {
                Left = title,
                Right = title,
                Kind = UnitGitDiffLineKind.Hunk
            });
        }

        private static string WriteTempCommitMessage(string message)
        {
            string path = Path.Combine(Path.GetTempPath(), "unitgit-message-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, NormalizeCommitMessage(message) + "\n", new UTF8Encoding(false));
            return path;
        }

        private static string NormalizeCommitMessage(string message)
        {
            return string.IsNullOrWhiteSpace(message)
                ? string.Empty
                : message.Trim();
        }

        private static void DeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string FirstNonEmptyLine(string text)
        {
            return SplitLines(text)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
        }

        private static string TrimTrailingNewlines(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.TrimEnd('\r', '\n');
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return string.Empty;
            }

            string trimmed = hash.Trim();
            return trimmed.Length <= 8 ? trimmed : trimmed.Substring(0, 8);
        }
    }
}
