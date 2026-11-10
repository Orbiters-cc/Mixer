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
    internal sealed class UnitGitService
    {
        private const char FieldSeparator = '\x1f';
        private const char BranchFieldSeparator = '\t';
        private const int MaxUntrackedDiffPreviewBytes = 512 * 1024;
        private const int BinarySniffBytes = 4096;
        internal const int DefaultTimeoutMilliseconds = 30000;
        public const int LongTimeoutMilliseconds = 900000;
        // Windows stops a command line at 32k characters: longer path lists go to Git on stdin.
        private const int MaxPathsPerCommand = 200;
        private const int MaxPathCharactersPerCommand = 8000;
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

        private sealed class CommitRewriteInfo
        {
            public string Hash = string.Empty;
            public string TreeHash = string.Empty;
            public List<string> Parents = new List<string>();
            public string AuthorName = string.Empty;
            public string AuthorEmail = string.Empty;
            public string AuthorDate = string.Empty;
            public string Message = string.Empty;
        }

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
            if (request.HistoryOnly && previous != null)
            {
                // Older commits only ("load more"): everything else is what was read a moment ago.
                UnitGitSnapshot more = previous.CopyWithoutHistory();
                ReadHistory(more, request);
                return more;
            }

            UnitGitProjectInitializer.EnsureDefaultRules(ProjectRoot);
            // Before anything is read: a change made while reading shows as a change next time.
            snapshot.GitDirStamp = UnitGitRepositoryStamp.Read(ProjectRoot);

            // Exact renames only (a file moved unchanged, as Unity moves assets): guessing renames from similar content reads
            // every added and deleted file, which takes many seconds with big textures and models. Every untracked file is
            // listed (-uall): the Changes list shows and ticks them one by one.
            string[] statusArguments = { "status", "--porcelain=v2", "--branch", "-z", "-uall", "--find-renames=100%" };
            string[] refsArguments = { "for-each-ref", "--format=" + RefFormat, "refs/heads", "refs/remotes", "refs/tags", "refs/stash" };
            GitCommandResult status, refs;
            if (UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
            {
                status = RunGit(DefaultTimeoutMilliseconds, statusArguments);
                refs = RunGit(DefaultTimeoutMilliseconds, refsArguments);
            }
            else
            {
                // On a worker (the window's refresh): the two reads run side by side.
                var refsRead = System.Threading.Tasks.Task.Run(() => RunGit(DefaultTimeoutMilliseconds, refsArguments));
                status = RunGit(DefaultTimeoutMilliseconds, statusArguments);
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
                    var shelves = RunGit(DefaultTimeoutMilliseconds, "stash", "list");
                    if (shelves.Success)
                        snapshot.Shelves = ParseStashList(shelves.StandardOutput);
                }

                ReadTrackedIgnoredDownloads(snapshot);
            }

            return snapshot;
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
            var tracked = RunGit(DefaultTimeoutMilliseconds, "ls-files", "-z", "--cached", "--ignored", "--exclude-standard", "--", UnitGitProjectInitializer.McbDownloadsFolder);
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
            GitCommandResult previous = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--abbrev-ref", "@{-1}");
            string previousBranch = previous.Success ? FirstNonEmptyLine(previous.StandardOutput) : string.Empty;
            // A detached commit before this one prints nothing or a hash: only an existing local branch is a way back.
            snapshot.PreviousBranch = IsLocalBranch(snapshot, previousBranch) ? previousBranch : string.Empty;
            snapshot.ReturnBranch = IsLocalBranch(snapshot, visitBranch) ? visitBranch.Trim() : snapshot.PreviousBranch;
            if (snapshot.ReturnBranch.Length == 0) return;
            GitCommandResult newer = RunGit(DefaultTimeoutMilliseconds, "rev-list", "--count", "HEAD..refs/heads/" + snapshot.ReturnBranch, "--");
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
            GitCommandResult result = RunWithTrailers(withTrailers =>
                new[] { "log", "-1", "--pretty=format:" + CommitLogFormat(withTrailers), revision, "--" });
            return result.Success ? ParseCommits(result.StandardOutput, string.Empty).FirstOrDefault() : null;
        }

        /// <summary>True when HEAD is a commit on its own rather than a branch (a visit, a rebase, another tool's checkout).</summary>
        internal bool IsHeadDetached()
        {
            // Exit 1: HEAD is not a symbolic ref. A branch without commits yet is still a branch.
            return RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD").ExitCode == 1;
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
            return RunGit(DefaultTimeoutMilliseconds, "status", "--porcelain=v2", "-z", "--untracked-files=normal", "--ignore-submodules=all");
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
            GitCommandResult incoming = RunGit(DefaultTimeoutMilliseconds, "diff-tree", "-r", "-z", "--name-only", "--no-renames", "--diff-filter=A", "HEAD", target, "--");
            if (!incoming.Success) return incoming;
            List<string> paths = SplitNul(incoming.StandardOutput);
            if (paths.Count == 0) return incoming;
            GitCommandResult others = RunGit(DefaultTimeoutMilliseconds, "ls-files", "-z", "--others", "--exclude-standard");
            if (!others.Success) return others;
            // "--directory": a folder ignored as a whole (Library/) is one line, not every file in it.
            GitCommandResult ignoredOthers = RunGit(DefaultTimeoutMilliseconds, "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory");
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
            GitCommandResult result = RunGit(DefaultTimeoutMilliseconds, args.ToArray());
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
                UnitGitObjects.Answer answer = UnitGitObjects.For(ProjectRoot).Read(name, bytes, out string type, out _);
                if (answer == UnitGitObjects.Answer.Found)
                    return type == "blob" ? UnitGitRunner.Decode(bytes.GetBuffer(), (int)bytes.Length) : null;
                if (answer == UnitGitObjects.Answer.Missing) return null;
            }

            GitCommandResult blob = RunGit(DefaultTimeoutMilliseconds, "cat-file", "blob", revision + ":./" + path);
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
                left = ReadChangeStatus().StandardOutput;
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
            GitCommandResult result = RunGit(LongTimeoutMilliseconds, checkout.ToArray());
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
                left = ReadChangeStatus().StandardOutput;
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
                ? RunGit(LongTimeoutMilliseconds, "checkout", "--force", branch, "--")
                : RunGit(LongTimeoutMilliseconds, "checkout", branch, "--");
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
            GitCommandResult exists = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch);
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

            GitCommandResult shelve = RunGit(LongTimeoutMilliseconds, "stash", "push", "-m", message.Trim());
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
            GitCommandResult list = RunGit(DefaultTimeoutMilliseconds, "stash", "list", "--format=%gd%x1f%gs");
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
                : RunGit(LongTimeoutMilliseconds, "stash", "pop", shelf);
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
                    answer = UnitGitObjects.For(ProjectRoot).Read(revisionPath, file, out type, out _);
                if (answer == UnitGitObjects.Answer.Found && type == "blob") return true;
                File.Delete(destination);
                if (answer != UnitGitObjects.Answer.Failed) return false;
            }

            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--literal-pathspecs cat-file blob " + EscapeArgument(revisionPath),
                WorkingDirectory = ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            bool complete = false;
            try
            {
                EditorProcessResult result;
                using (var file = File.Create(destination))
                {
                    result = EditorProcessRunner.Run(start, LongTimeoutMilliseconds, standardOutputDestination: file);
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
            var result = RunWithTrailers(withTrailers => new[]
            {
                "-c",
                "core.quotePath=true",
                "show",
                "--no-ext-diff",
                "--name-status",
                "-M",
                "--date=format-local:%m/%d/%Y %I:%M %p",
                "--pretty=format:" + CommitDetailsFormat(withTrailers),
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
            GitCommandResult result = null;
            foreach (bool withTrailers in TrailersSupported == false ? new[] { false } : new[] { true, false })
            {
                bool trailers = withTrailers;
                result = RunGitWithInput(DefaultTimeoutMilliseconds, stream =>
                    {
                        byte[] bytes = Utf8.GetBytes(string.Join("\n", hashes) + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                    }, "log", "--no-walk=unsorted", "--stdin", "-z", "--no-ext-diff", "--name-status", "-M", "--cc",
                    "--date=format-local:%m/%d/%Y %I:%M %p", "--pretty=format:" + CommitDetailsFormat(trailers));
                if (result.Success || TrailersSupported == true || (ReadSuperseded?.Invoke() ?? false)) break;
            }

            if (result == null || !result.Success) return found;
            foreach (UnitGitCommitDetails details in ParseCommitDetailsList(result.StandardOutput, hashes))
                found[details.Commit.FullHash] = details;
            return found;
        }

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

        // Header fields, then the body, then a NUL: the body may span lines, and the file list follows it.
        private static string CommitDetailsFormat(bool withTrailers)
        {
            return "%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ad%x1f%D%x1f%cn%x1f%ce%x1f%cd%x1f" +
                   (withTrailers ? ReleaseTrailerFormat : string.Empty) + "%x1f%P%x1f%at%x1f%b%x00";
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

        public GitCommandResult Fetch()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before fetching.");
            }

            return RunGit(LongTimeoutMilliseconds, "fetch", "--all", "--prune");
        }

        public GitCommandResult PullFastForward()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before pulling.");
            }

            return RunGit(LongTimeoutMilliseconds, "pull", "--ff-only");
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
            GitCommandResult branchResult = RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            string branchRef = branchResult.Success ? FirstNonEmptyLine(branchResult.StandardOutput) : string.Empty;
            if (!branchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                return Failure("Check out a branch before pushing.");
            }

            string branch = branchRef.Substring("refs/heads/".Length);

            // The configured upstream, even when its remote branch was deleted since: push recreates it there.
            GitCommandResult upstream = RunGit(DefaultTimeoutMilliseconds, "config", "--get", "branch." + branch + ".merge");
            if (upstream.Success && FirstNonEmptyLine(upstream.StandardOutput).Length > 0)
            {
                return RunGit(LongTimeoutMilliseconds, "push");
            }

            string remote = remotes.FirstOrDefault(name => name == "origin") ?? remotes[0];
            return RunGit(LongTimeoutMilliseconds, "push", "-u", remote, branch);
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
            GitCommandResult result = RunGit(LongTimeoutMilliseconds, args.ToArray());
            if (result.Success || UnitGitConflicts.Operation(ProjectRoot, out _) != operation)
            {
                return result;
            }

            // Git stops on an empty result too (the changes are already here). Nothing conflicts and nothing is staged:
            // cancel it rather than leave an operation with nothing to resolve.
            bool conflicted = RunGit(DefaultTimeoutMilliseconds, "ls-files", "-u").StandardOutput.Trim().Length > 0;
            bool staged = RunGit(DefaultTimeoutMilliseconds, "diff", "--cached", "--quiet").ExitCode != 0;
            if (conflicted || staged)
            {
                return result;
            }

            GitCommandResult abort = RunGit(LongTimeoutMilliseconds, command[0], "--abort");
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

            GitCommandResult resolved = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", revision.Trim() + "^{commit}");
            fullHash = resolved.Success ? FirstNonEmptyLine(resolved.StandardOutput) : string.Empty;
            return fullHash.Length > 0 ? resolved : Failure("Commit " + ShortHash(revision) + " was not found.");
        }

        public GitCommandResult StageAll()
        {
            return RunGit(LongTimeoutMilliseconds, "add", "-A");
        }

        public GitCommandResult UnstageAll()
        {
            return RunGit(DefaultTimeoutMilliseconds, "reset");
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

            var result = RunGit(DefaultTimeoutMilliseconds, "log", "-1", "--format=%B", commitHash);
            if (result.Success)
            {
                result.StandardOutput = TrimTrailingNewlines(result.StandardOutput);
            }

            return result;
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

        internal GitCommandResult ReadHeadState(out UnitGitHeadState state)
        {
            state = null;
            GitCommandResult result = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "HEAD", "--symbolic-full-name", "HEAD");
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

            GitCommandResult resolved = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", commitHash.Trim() + "^{commit}");
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
            return RunGit(LongTimeoutMilliseconds, "reset", GetResetModeArgument(mode), targetHash);
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

            return RunGit(LongTimeoutMilliseconds, "stash", "push", "-u", "-m", shelfMessage);
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
                return RunGitWithInput(environment, LongTimeoutMilliseconds, stream => stream.Write(bytes, 0, bytes.Length), stdinArgs.ToArray());
            }

            string messagePath = WriteTempCommitMessage(message);
            try
            {
                var args = new List<string>(baseArguments) { "-F", messagePath };
                return environment == null
                    ? RunGit(LongTimeoutMilliseconds, args.ToArray())
                    : RunGitWithEnvironment(environment, LongTimeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(messagePath);
            }
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
                string commitMessage = squash && i == startIndex
                    ? message
                    : (!squash && selected.Contains(originalHash) ? message : source.Message);

                GitCommandResult createResult = CreateCommitFromTree(source, treeHash, parents, commitMessage);
                if (!createResult.Success)
                {
                    return createResult;
                }

                string newHash = FirstNonEmptyLine(createResult.StandardOutput);
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

            // Commit trees are unchanged. Move only the captured branch, and only if nobody advanced it.
            // Do not reset the index or whichever branch another process has since checked out.
            GitCommandResult updateResult = RunGit(DefaultTimeoutMilliseconds, "update-ref", "--no-deref",
                "-m", "Mixer Git: rewrite history", branchRef, newHead, oldHead);
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
            GitCommandResult read = RunGitWithInput(LongTimeoutMilliseconds, stream =>
            {
                byte[] bytes = Utf8.GetBytes(string.Join("\n", hashes) + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }, "log", "--no-walk=unsorted", "--stdin", "-z", "--format=%H%x1f%T%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%B");
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
        private GitCommandResult CreateCommitFromTree(CommitRewriteInfo source, string treeHash, IList<string> parents, string message)
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
            return RunCommitWithMessageFile(args, message, environment);
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
                unstagedResult = RunGit(DefaultTimeoutMilliseconds, DiffArguments(whitespace, context, false, change.Path));
                if (unstagedResult.Success)
                {
                    unstagedOutput = unstagedResult.StandardOutput;
                }
            }

            if (change.IsStaged)
            {
                stagedResult = RunGit(DefaultTimeoutMilliseconds, DiffArguments(whitespace, context, true, change.Path));
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
                GitCommandResult commit = RunGit(DefaultTimeoutMilliseconds, "show", "-s", "--format=%h%x1f%p%x1f%P", hash);
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
            UnitGitObjects.Answer answer = UnitGitObjects.For(ProjectRoot).Describe(name, out _, out long size);
            if (answer == UnitGitObjects.Answer.Found) return size;
            if (answer == UnitGitObjects.Answer.Missing) return 0;
            GitCommandResult read = RunGit(DefaultTimeoutMilliseconds, "--literal-pathspecs", "cat-file", "-s", name);
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
            return RunGit(DefaultTimeoutMilliseconds, args.ToArray());
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
            GitCommandResult renames = RunGit(DefaultTimeoutMilliseconds, "-c", "core.quotePath=true", "diff", "--no-ext-diff",
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

            if (!branch.IsRemote)
            {
                return RunGit(DefaultTimeoutMilliseconds, "checkout", branch.Name);
            }

            return RunGit(DefaultTimeoutMilliseconds, "checkout", "-t", branch.Name);
        }

        public GitCommandResult CreateBranch(string branchName, string startPoint)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return Failure("Branch name is required.");
            }

            branchName = branchName.Trim();
            var validation = RunGit(DefaultTimeoutMilliseconds, "check-ref-format", "--branch", branchName);
            if (!validation.Success)
            {
                return Failure("Invalid branch name: " + branchName);
            }

            if (string.IsNullOrWhiteSpace(startPoint))
            {
                return RunGit(DefaultTimeoutMilliseconds, "checkout", "-b", branchName);
            }

            return RunGit(DefaultTimeoutMilliseconds, "checkout", "-b", branchName, startPoint);
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

            GitCommandResult checkout = RunGit(DefaultTimeoutMilliseconds, "checkout", branch.Name);
            if (!checkout.Success)
            {
                return checkout;
            }

            GitCommandResult current = RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
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
                return RunGit(DefaultTimeoutMilliseconds, "branch", "-dr", branch.Name);
            }

            return RunGit(DefaultTimeoutMilliseconds, "branch", "-d", branch.Name);
        }

        public GitCommandResult RunGit(int timeoutMilliseconds, params string[] arguments)
        {
            var log = ProcessLogReceived;
            var superseded = ReadSuperseded;
            return UnitGitWriteGate.Run(arguments, timeoutMilliseconds,
                () => RunProcess("git", "Git command timed out.", ProjectRoot, timeoutMilliseconds, log, null, superseded, arguments),
                line => log?.Invoke(line));
        }

        internal GitCommandResult RunGitWithEnvironment(IDictionary<string, string> environment, int timeoutMilliseconds, params string[] arguments)
        {
            var log = ProcessLogReceived;
            return UnitGitWriteGate.Run(arguments, timeoutMilliseconds,
                () => RunProcess("git", "Git command timed out.", ProjectRoot, timeoutMilliseconds, log, environment, null, arguments),
                line => log?.Invoke(line));
        }

        /// <summary>
        /// Runs Git with <paramref name="input"/> written to its stdin while its output is read. A command that changes the
        /// repository takes the write gate as <see cref="RunGit"/> does; a retry on another program's index lock writes the
        /// input again.
        /// </summary>
        internal GitCommandResult RunGitWithInput(int timeoutMilliseconds, Action<Stream> input, params string[] arguments)
        {
            return RunGitWithInput(null, timeoutMilliseconds, input, arguments);
        }

        internal GitCommandResult RunGitWithInput(IDictionary<string, string> environment, int timeoutMilliseconds, Action<Stream> input, params string[] arguments)
        {
            var log = ProcessLogReceived;
            var superseded = ReadSuperseded;
            return UnitGitWriteGate.Run(arguments, timeoutMilliseconds,
                () => RunStreaming(ProjectRoot, timeoutMilliseconds, log, superseded, environment, input, null, arguments),
                line => log?.Invoke(line));
        }

        /// <summary>
        /// Reads a command's stdout as raw bytes while it runs; <paramref name="output"/> returns false to stop it early
        /// (the result is then a success with <see cref="GitCommandResult.Stopped"/>). Reads only: never gated.
        /// </summary>
        internal GitCommandResult ReadGitStream(int timeoutMilliseconds, Func<byte[], int, int, bool> output, params string[] arguments)
        {
            return RunStreaming(ProjectRoot, timeoutMilliseconds, ProcessLogReceived, ReadSuperseded, null, null, output, arguments);
        }

        private static GitCommandResult RunStreaming(string workingDirectory, int timeoutMilliseconds, UnitGitProcessLogHandler logHandler,
            Func<bool> superseded, IDictionary<string, string> environment, Action<Stream> input, Func<byte[], int, int, bool> output, string[] arguments)
        {
            if (superseded != null && superseded())
                return Failure("Read superseded.");
            var result = new GitCommandResult();
            var started = DateTime.UtcNow;
            UnitGitProcessLogHandler log = null;
            if (logHandler != null) log = line => logHandler(UnitGitRedaction.Redact(line));
            log?.Invoke("> " + FormatCommandLine("git", arguments));
            log?.Invoke("cwd: " + workingDirectory);
            try
            {
                UnitGitRunResult run = UnitGitRunner.Run(new UnitGitRunRequest
                {
                    WorkingDirectory = workingDirectory,
                    Arguments = arguments,
                    TimeoutMilliseconds = timeoutMilliseconds,
                    Environment = environment,
                    Cancelled = superseded,
                    Input = input,
                    Output = output,
                    // A read's output only fills the log: its lines are not logged (the command, its exit and errors are).
                    OutputLine = log == null || UnitGitWriteGate.IsRead(arguments) ? null : (Action<string>)(line => log("stdout: " + line)),
                    ErrorLine = log == null ? null : (Action<string>)(line => log("stderr: " + line))
                });
                result.ExitCode = run.Success || run.Stopped ? 0 : run.ExitCode == 0 ? 1 : run.ExitCode;
                result.TimedOut = run.TimedOut;
                result.Stopped = run.Stopped;
                result.StandardOutput = run.StandardOutput;
                result.StandardError = run.OutputHeldOpen ? "Git finished, but a program it started kept its output open." : run.TimedOut ? "Git command timed out." : run.StandardError;
                if (run.InputFailed)
                    result.StandardError = "Mixer Git could not hand the whole list to Git, so Git was stopped before doing anything with it. " + run.StandardError;
                if (run.Cancelled)
                    result.StandardError += (result.StandardError.Length > 0 ? "\n" : string.Empty) +
                                            (superseded?.Invoke() == true ? "Read superseded." : "Command interrupted by editor reload or shutdown.");
                if (run.TimedOut) log?.Invoke("command or output capture timed out");
                else if (!run.Cancelled) log?.Invoke("exit: " + result.ExitCode);
            }
            catch (Exception ex) when (!(ex is ThreadAbortException))
            {
                result.ExitCode = 1;
                result.StandardError = ex.Message;
                log?.Invoke("error: " + ex.Message);
            }

            RaiseCommandRecord(workingDirectory, arguments, result, started);
            return result;
        }

        /// <summary>
        /// Runs "git --literal-pathspecs &lt;arguments&gt;" once for all <paramref name="paths"/>, which Git reads from stdin
        /// (NUL-separated: any name, spaces, quotes, line breaks and leading dashes included). No paths run nothing: Git
        /// would read that as the whole tree. The command must take --pathspec-from-file (add, reset, restore, rm, checkout,
        /// commit, stash push).
        /// </summary>
        private GitCommandResult RunGitForPaths(int timeoutMilliseconds, IList<string> paths, params string[] arguments)
        {
            // An empty entry is no path: Git refuses it, and dropped it cannot become "everything".
            paths = paths?.Where(path => !string.IsNullOrEmpty(path)).ToList();
            if (paths == null || paths.Count == 0)
                return new GitCommandResult();
            var args = new List<string> { "--literal-pathspecs" };
            args.AddRange(arguments);
            args.Add("--pathspec-file-nul");
            Action<Stream> writePaths = stream =>
            {
                var bytes = new byte[0];
                foreach (string path in paths)
                {
                    int length = UnitGitRunner.Utf8.GetMaxByteCount(path.Length) + 1;
                    if (bytes.Length < length) bytes = new byte[Math.Max(length, 4096)];
                    int count = UnitGitRunner.Utf8.GetBytes(path, 0, path.Length, bytes, 0);
                    bytes[count++] = 0;
                    stream.Write(bytes, 0, count);
                }
            };
            if (UnitGitRunner.CleanInput)
            {
                args.Add("--pathspec-from-file=-");
                return RunGitWithInput(timeoutMilliseconds, writePaths, args.ToArray());
            }

            // Stdin would start with a byte order mark (see UnitGitRunner.CleanInput): the same list from a file.
            string file = Path.Combine(Path.GetTempPath(), "mixer-git-paths-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                using (var stream = File.Create(file)) writePaths(stream);
                args.Add("--pathspec-from-file=" + file);
                return RunGit(timeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(file);
            }
        }

        // The paths of these files that Git has in HEAD (ls-tree) or in the index (ls-files): neither takes a path list on
        // stdin, so a long selection is answered from the whole listing, a short one with the paths on the command line.
        private GitCommandResult ListPaths(IList<string> paths, List<string> listed, params string[] arguments)
        {
            var args = new List<string>(arguments);
            args.Insert(1, "-z"); // right after the verb: ls-tree reads what follows its tree as paths
            bool whole = paths.Count > MaxPathsPerCommand || paths.Sum(path => path.Length + 3) > MaxPathCharactersPerCommand;
            if (!whole)
            {
                args.Insert(0, "--literal-pathspecs");
                args.Add("--");
                args.AddRange(paths);
            }

            GitCommandResult result = RunGit(DefaultTimeoutMilliseconds, args.ToArray());
            if (!result.Success)
                return result;
            List<string> found = SplitNul(result.StandardOutput);
            if (whole)
            {
                var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
                found = found.Where(wanted.Contains).ToList();
            }

            listed.AddRange(found);
            return result;
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

        /// <summary>
        /// The oldest Git Mixer Git runs: path lists on stdin (--pathspec-from-file) for add, reset, restore and checkout came
        /// in 2.25, for rm and stash push in 2.26. (git restore itself, used since the first version, is 2.23.)
        /// </summary>
        internal static readonly Version MinimumGitVersion = new Version(2, 26);
        private static readonly object GitVersionSync = new object();
        private static Version gitVersion; // read once per editor session (domain): "Install Git and reopen Unity"
        private static string gitVersionText;

        public bool IsGitAvailable()
        {
            return GetGitVersion(out _) != null;
        }

        /// <summary>Git's version ("git version 2.45.1.windows.1" as 2.45.1), or null when Git cannot run.</summary>
        internal Version GetGitVersion(out string text)
        {
            lock (GitVersionSync)
            {
                if (gitVersion == null)
                {
                    var result = RunGit(10000, "--version");
                    if (!result.Success)
                    {
                        text = string.Empty;
                        return null; // not kept: a failed start (a timeout while the disk was busy) is asked again
                    }
                    gitVersionText = FirstNonEmptyLine(result.StandardOutput);
                    gitVersion = ParseGitVersion(gitVersionText);
                }

                text = gitVersionText;
                return gitVersion;
            }
        }

        internal static Version ParseGitVersion(string text)
        {
            Match match = Regex.Match(text ?? string.Empty, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success) return new Version(0, 0);
            int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
            return new Version(Part(1), Part(2), Part(3));
        }

        public GitCommandResult GitHubLogin()
        {
            if (!IsGitHubCliAvailable())
            {
                return Failure("GitHub CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitHub CLI, then reopen Unity.");
            }

            return RunGitHubCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "github.com", "--web", "--clipboard", "--git-protocol", "https", "--skip-ssh-key");
        }

        public GitCommandResult CreateGitHubRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            if (!IsGitHubCliAvailable())
            {
                return Failure("GitHub CLI was not found on PATH. Install GitHub CLI, then reopen Unity.");
            }

            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return Failure("GitHub repository name is required.");
            }

            remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
            if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("Remote '" + remoteName + "' already exists.");
            }

            return RunGitHubCli(
                LongTimeoutMilliseconds,
                "repo",
                "create",
                repositoryName.Trim(),
                "--source",
                ProjectRoot,
                "--remote",
                remoteName,
                isPrivate ? "--private" : "--public");
        }

        private GitCommandResult RunGitHubCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitHubCliExecutable(), "GitHub CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitHubCliAvailable()
        {
            return RunGitHubCli(10000, "--version").Success;
        }

        internal bool IsGitHubCliSignedIn()
        {
            return RunGitHubCli(10000, "auth", "status", "--hostname", "github.com").Success;
        }

        internal bool IsGitLabCliSignedIn()
        {
            return RunGitLabCli(10000, "auth", "status", "--hostname", "gitlab.com").Success;
        }

        public GitCommandResult GitLabLogin()
        {
            if (!IsGitLabCliAvailable())
            {
                return Failure("GitLab CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
            }

            return RunGitLabCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "gitlab.com", "--web", "--git-protocol", "https");
        }

        public GitCommandResult CreateGitLabRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            if (!IsGitLabCliAvailable())
            {
                return Failure("GitLab CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
            }

            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return Failure("GitLab repository path is required.");
            }

            remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
            if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("Remote '" + remoteName + "' already exists.");
            }

            return RunGitLabCli(
                LongTimeoutMilliseconds,
                "repo",
                "create",
                repositoryName.Trim(),
                "--remoteName",
                remoteName,
                isPrivate ? "--private" : "--public");
        }

        private GitCommandResult RunGitLabCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitLabCliExecutable(), "GitLab CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitLabCliAvailable()
        {
            return RunGitLabCli(10000, "--version").Success;
        }

        private static string GetGitHubCliExecutable()
        {
            const string executableName = "gh.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "GitHub CLI", executableName),
                Path.Combine(localAppData, "GitHub CLI", executableName),
                Path.Combine(localAppData, "Programs", "GitHub CLI", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "gh";
        }

        private static string GetGitLabCliExecutable()
        {
            const string executableName = "glab.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "glab", "bin", executableName),
                Path.Combine(localAppData, "Programs", "glab", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "glab";
        }

        private static string FindExecutableOnPath(string executableName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
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
            var result = RunGit(DefaultTimeoutMilliseconds, "remote");
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
            var result = RunGit(
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
            var result = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", "HEAD");
            return result.Success;
        }

        internal List<UnitGitCommit> GetCommits(string logSearch, int maxCount = 250, UnitGitLogFilter filter = null)
        {
            if (maxCount <= 0) return new List<UnitGitCommit>();
            var snapshot = new UnitGitSnapshot();
            ReadHistory(snapshot, new UnitGitSnapshotRequest { LogSearch = logSearch, HistoryLimit = maxCount, Filter = filter });
            return snapshot.Commits;
        }

        /// <summary>
        /// Whether this Git reads the release trailer placeholder (<c>%(trailers:key=…,valueonly,separator=…)</c>): null until a
        /// read tells. A failing read is tried without it only while that is unknown, so a log that fails for another reason
        /// (an unknown branch) is not run twice once a read with it has worked.
        /// </summary>
        internal static bool? TrailersSupported;

        private GitCommandResult RunWithTrailers(Func<bool, string[]> arguments)
        {
            bool withTrailers = TrailersSupported != false;
            GitCommandResult result = RunGit(DefaultTimeoutMilliseconds, arguments(withTrailers));
            if (result.Success)
            {
                if (withTrailers && TrailersSupported == null) TrailersSupported = true;
                return result;
            }

            if (!withTrailers || TrailersSupported == true || (ReadSuperseded?.Invoke() ?? false)) return result;
            GitCommandResult without = RunGit(DefaultTimeoutMilliseconds, arguments(false));
            if (without.Success) TrailersSupported = false;
            return without.Success ? without : result;
        }

        // One commit as the history lists it. The age is not read: it is worded when shown (UnitGitTime.GitRelative).
        // Without trailer support the release field stays, empty, so every field keeps its position.
        internal static string CommitLogFormat(bool withTrailers)
        {
            return "%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%D%x1f" + (withTrailers ? ReleaseTrailerFormat : string.Empty) + "%x1f%P%x1f%at";
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

            string root = Path.GetFullPath(ProjectRoot);
            string fullPath = Path.GetFullPath(Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar)));
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

        private static GitCommandResult RunProcess(string fileName, string timeoutMessage, string workingDirectory, int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(fileName, timeoutMessage, workingDirectory, timeoutMilliseconds, null, arguments);
        }

        private static GitCommandResult RunProcess(
            string fileName,
            string timeoutMessage,
            string workingDirectory,
            int timeoutMilliseconds,
            UnitGitProcessLogHandler logHandler,
            params string[] arguments)
        {
            return RunProcess(fileName, timeoutMessage, workingDirectory, timeoutMilliseconds, logHandler, null, null, arguments);
        }

        private static GitCommandResult RunProcess(
            string fileName,
            string timeoutMessage,
            string workingDirectory,
            int timeoutMilliseconds,
            UnitGitProcessLogHandler logHandler,
            IDictionary<string, string> environment,
            Func<bool> superseded,
            params string[] arguments)
        {
            if (superseded != null && superseded())
                return Failure("Read superseded.");
            var result = new GitCommandResult();
            var output = new StringBuilder();
            var error = new StringBuilder();
            var started = DateTime.UtcNow;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = string.Join(" ", arguments.Select(EscapeArgument).ToArray()),
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8,
                    StandardErrorEncoding = Utf8,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                // Reads (a refresh's status) must not take the index lock: a commit or a staging starting meanwhile would fail on it.
                startInfo.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
                if (logHandler != null)
                {
                    var raw = logHandler;
                    logHandler = line => raw(UnitGitRedaction.Redact(line));
                }
                if (environment != null)
                {
                    foreach (KeyValuePair<string, string> item in environment)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Key))
                        {
                            startInfo.EnvironmentVariables[item.Key] = item.Value ?? string.Empty;
                        }
                    }
                }

                logHandler?.Invoke("> " + FormatCommandLine(fileName, arguments));
                logHandler?.Invoke("cwd: " + startInfo.WorkingDirectory);

                var processResult = EditorProcessRunner.Run(startInfo, timeoutMilliseconds, superseded,
                    logHandler == null ? null : (Action<string>)(line => logHandler("stdout: " + line)),
                    logHandler == null ? null : (Action<string>)(line => logHandler("stderr: " + line)));
                result.ExitCode = processResult.Success ? 0 : processResult.ExitCode == 0 ? 1 : processResult.ExitCode;
                result.TimedOut = processResult.TimedOut;
                output.Append(processResult.StandardOutput);
                error.Append(processResult.StandardError);
                if (processResult.Cancelled)
                    error.AppendLine(superseded?.Invoke() == true ? "Read superseded." : "Command interrupted by editor reload or shutdown.");
                if (processResult.TimedOut)
                    logHandler?.Invoke("command or output capture timed out");
                else if (!processResult.Cancelled)
                    logHandler?.Invoke("exit: " + result.ExitCode);
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                error.AppendLine(ex.Message);
                logHandler?.Invoke("error: " + ex.Message);
            }

            result.StandardOutput = output.ToString();
            result.StandardError = result.TimedOut
                ? timeoutMessage
                : error.ToString();
            RaiseCommandRecord(workingDirectory, arguments, result, started, fileName);
            return result;
        }

        private static void RaiseCommandRecord(string workingDirectory, string[] arguments, GitCommandResult result, DateTime started, string fileName = "git")
        {
            if (!UnitGitCommandLog.HasListeners) return;
            var finished = DateTime.UtcNow;
            UnitGitCommandLog.Raise(new UnitGitCommandRecord
            {
                CommandLine = UnitGitRedaction.Redact(FormatCommandLine(fileName, arguments)),
                WorkingDirectory = workingDirectory ?? string.Empty,
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
                StandardOutput = UnitGitRedaction.Redact(result.StandardOutput),
                StandardError = UnitGitRedaction.Redact(result.StandardError),
                Milliseconds = (finished - started).TotalMilliseconds,
                FinishedUtc = finished
            });
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult
            {
                ExitCode = 1,
                StandardError = message
            };
        }

        internal static string FormatCommandLine(string fileName, params string[] arguments)
        {
            string suffix = arguments == null || arguments.Length == 0
                ? string.Empty
                : " " + string.Join(" ", arguments.Select(EscapeArgument).ToArray());
            return EscapeArgument(fileName) + suffix;
        }

        internal static string EscapeArgument(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            if (argument.Length == 0)
            {
                return "\"\"";
            }

            bool requiresQuotes = argument.Any(char.IsWhiteSpace) ||
                                  argument.IndexOf('"') >= 0 ||
                                  argument.IndexOf('%') >= 0 ||
                                  argument.IndexOf(';') >= 0;

            if (!requiresQuotes)
            {
                return argument;
            }

            var escaped = new StringBuilder();
            escaped.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    escaped.Append('\\', backslashes * 2 + 1);
                    escaped.Append('"');
                    backslashes = 0;
                    continue;
                }

                escaped.Append('\\', backslashes);
                backslashes = 0;
                escaped.Append(c);
            }

            escaped.Append('\\', backslashes * 2);
            escaped.Append('"');
            return escaped.ToString();
        }
    }
}
