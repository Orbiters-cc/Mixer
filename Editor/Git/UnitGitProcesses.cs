using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>Hides credentials (URL passwords and tokens, Authorization headers, GitHub/GitLab tokens) in what Mixer Git shows or logs.</summary>
    internal static class UnitGitRedaction
    {
        private const string Mask = "***";
        private static readonly Regex UrlCredentials = new Regex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<user>[^/@\s:'""<>]+)(?::(?<password>[^/@\s'""<>]*))?@", RegexOptions.Compiled);
        private static readonly Regex Authorization = new Regex(@"(?i)(?<name>\bauthorization\s*[:=]\s*)(?<scheme>(?:bearer|basic|token)\s+)?[^\s'""]+", RegexOptions.Compiled);
        private static readonly Regex KnownTokens = new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{20,})", RegexOptions.Compiled);

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0)
                text = UrlCredentials.Replace(text, match =>
                {
                    // "https://user:secret@host" keeps the user; a lone "https://token@host" is the secret itself.
                    if (match.Groups["password"].Success)
                        return match.Groups["scheme"].Value + match.Groups["user"].Value + ":" + Mask + "@";
                    string user = match.Groups["user"].Value;
                    return LooksLikeSecret(user) ? match.Groups["scheme"].Value + Mask + "@" : match.Value;
                });
            if (text.IndexOf("uthorization", StringComparison.OrdinalIgnoreCase) >= 0)
                text = Authorization.Replace(text, match => match.Groups["name"].Value + match.Groups["scheme"].Value + Mask);
            return KnownTokens.Replace(text, Mask);
        }

        // Account names are short words; tokens are long runs of letters and digits.
        private static bool LooksLikeSecret(string user) =>
            KnownTokens.IsMatch(user) || user.Length >= 20 && user.Count(char.IsDigit) >= 3;
    }

    /// <summary>
    /// Git commands that change the repository run one at a time, and a lock another program holds for a moment is waited
    /// out. Reads never take Git's optional index lock (GIT_OPTIONAL_LOCKS=0), so a refresh never makes a commit fail.
    /// </summary>
    internal static class UnitGitWriteGate
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly int[] LockRetryDelays = { 100, 200, 400, 700, 1000, 1500, 2000 };

        // Commands that only read, whatever their options: never gated, stopped by a superseded read, stopped at once at a
        // reload. Commands whose options decide are classified in Classify.
        private static readonly HashSet<string> ReadVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "log", "show", "diff", "ls-files", "ls-tree", "cat-file", "rev-parse", "rev-list", "for-each-ref",
            "merge-base", "check-ignore", "check-attr", "show-ref", "describe", "blame", "name-rev", "version", "--version",
            "grep", "shortlog", "count-objects", "var", "ls-remote", "help", "diff-tree", "diff-index", "diff-files", "cherry",
            "range-diff", "check-ref-format", "verify-commit", "verify-tag",
            // Mixer Git runs merge-file on copies in a temporary folder only (the conflict view's parts): it reads the
            // repository's settings and writes nothing in it, so a superseded read may stop it and it takes no write gate.
            "merge-file"
        };

        // One step that takes the index lock before doing anything: failing on the lock changed nothing, so it can run again.
        private static readonly HashSet<string> RetryableVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "add", "rm", "mv", "reset", "restore", "commit", "update-index", "checkout", "switch"
        };

        internal static string Verb(IList<string> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                string argument = arguments[i] ?? string.Empty;
                if (argument == "-c" || argument == "-C" || argument == "--git-dir" || argument == "--work-tree" || argument == "--namespace")
                {
                    i++;
                    continue;
                }
                if (argument.StartsWith("-", StringComparison.Ordinal) && argument != "--version") continue;
                return argument;
            }
            return string.Empty;
        }

        /// <summary>What a Git command does to the repository, for the write gate and for stopping it.</summary>
        internal enum Kind
        {
            /// <summary>Reads only: no gate; a superseded read may stop it; a reload stops it at once.</summary>
            Read,
            /// <summary>
            /// Changes something, but only what Git's own locks keep safe beside the local changes, so it does not wait for
            /// the write gate nor hold local changes behind it. These are the network commands: fetch writes objects (packs
            /// written whole, then renamed) and remote-tracking refs and FETCH_HEAD, each under its own lock file, and
            /// never the index, the work tree or the checked-out branch; push reads the local refs and writes the remote,
            /// then its remote-tracking refs (and, with -u, the branch's upstream in the config, which Git also writes
            /// under a lock: at worst one of two writers fails, nothing is half written); bundle create and clone write a
            /// file or a new repository outside this one. A local commit beside them takes other locks (index, its
            /// branch), or meets a held one and fails cleanly, and the gate's retries cover the index. Never stopped
            /// because a read was superseded; a reload gives it the grace for changes.
            /// </summary>
            Change,
            /// <summary>Changes the index, the work tree, local refs or the configuration: one at a time (the write gate),
            /// never stopped because a read was superseded, the reload grace.</summary>
            Exclusive
        }

        /// <summary>Whether the command only reads (see <see cref="Kind"/>).</summary>
        internal static bool IsRead(IList<string> arguments) => Classify(arguments) == Kind.Read;

        /// <summary>
        /// Classifies by the operation and its options, not the verb alone. Anything not known to read is a change, and a
        /// change not known to be safe beside local changes is exclusive.
        /// </summary>
        internal static Kind Classify(IList<string> arguments)
        {
            string verb = Verb(arguments);
            if (ReadVerbs.Contains(verb)) return Kind.Read;
            var rest = arguments.SkipWhile(argument => argument != verb).Skip(1).ToList();
            string first = rest.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal)) ?? string.Empty;
            bool Has(params string[] options) => rest.Any(argument => options.Contains(argument));
            bool Starts(string prefix) => rest.Any(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
            switch (verb)
            {
                case "fetch":
                    // --update-head-ok lets fetch move the checked-out branch: that is a local change of HEAD.
                    return Has("--update-head-ok") ? Kind.Exclusive : Kind.Change;
                case "push":
                case "clone":
                    return Kind.Change;
                case "bundle":
                    if (first == "verify" || first == "list-heads") return Kind.Read;
                    return first == "create" ? Kind.Change : Kind.Exclusive; // unbundle writes objects into this repository
                case "hash-object":
                    return Has("-w") ? Kind.Exclusive : Kind.Read;
                case "merge-tree":
                    // The old form only prints; --write-tree writes the merged trees' objects.
                    return Has("--write-tree") ? Kind.Exclusive : Kind.Read;
                case "stash":
                    return rest.Count > 0 && (rest[0] == "list" || rest[0] == "show") ? Kind.Read : Kind.Exclusive;
                case "config":
                    // Reading: --get, --get-all, --get-regexp, --get-urlmatch, --list; anything else sets or unsets a value.
                    return Starts("--get") || Has("--list", "-l") ? Kind.Read : Kind.Exclusive;
                case "remote":
                    return rest.Count == 0 || rest[0] == "-v" || rest[0] == "get-url" || rest[0] == "show" ? Kind.Read : Kind.Exclusive;
                case "branch":
                case "tag":
                    if (Has("-d", "-D", "--delete", "-m", "-M", "-c", "-C", "-u", "-f", "--force", "--unset-upstream", "--edit-description") ||
                        Starts("--set-upstream") || Starts("--track"))
                        return Kind.Exclusive;
                    return rest.Count == 0 || Has("--list", "-l", "--show-current", "-v", "-vv", "-a", "-r", "--all", "--remotes", "--contains", "--merged",
                        "--no-merged", "--points-at") || Starts("--format") ? Kind.Read : Kind.Exclusive;
                case "symbolic-ref":
                    // One name reads it; two set it; -d deletes it.
                    return !Has("-d", "--delete") && rest.Count(argument => !argument.StartsWith("-", StringComparison.Ordinal)) <= 1 ? Kind.Read : Kind.Exclusive;
                case "reflog":
                    return first == "expire" || first == "delete" ? Kind.Exclusive : Kind.Read;
                case "worktree":
                    return first == "list" ? Kind.Read : Kind.Exclusive;
                case "notes":
                    return first == "" || first == "list" || first == "show" ? Kind.Read : Kind.Exclusive;
                case "submodule":
                    return first == "" || first == "status" || first == "summary" ? Kind.Read : Kind.Exclusive;
                case "lfs":
                    if (first == "fetch" || first == "push") return Kind.Change; // objects in .git/lfs, or the remote; never the work tree
                    return first == "ls-files" || first == "status" || first == "env" || first == "version" || first == "locks" ? Kind.Read : Kind.Exclusive;
                default:
                    // update-ref, gc, prune, repack, pack-refs, commit-graph, init, fast-import, checkout-index, commit, add,
                    // and anything not listed here.
                    return Kind.Exclusive;
            }
        }

        private static readonly Regex LockFile = new Regex(@"Unable to create '([^']+\.lock)': File exists", RegexOptions.Compiled);

        /// <summary>
        /// Adds, once, what to do about a lock file: another program may be using the repository; if none is, deleting the
        /// file unlocks it. <paramref name="lockPath"/>: the file a stopped change may have left (else the one Git names).
        /// Mixer Git never deletes a lock itself: an embedded Git (an IDE's) can hold one with no git process to see.
        /// </summary>
        internal static string ExplainLock(string message, string lockPath = null)
        {
            message = message ?? string.Empty;
            Match named = LockFile.Match(message);
            string file = named.Success ? named.Groups[1].Value : lockPath;
            if (string.IsNullOrEmpty(file) || message.IndexOf(LockAdvice, StringComparison.Ordinal) >= 0) return message;
            string start = named.Success ? string.Empty : "Git was stopped before it finished and may have left its lock file '" + file + "' behind. ";
            return message.TrimEnd() + (message.Trim().Length > 0 ? "\n" : string.Empty) + start +
                   "The repository is locked by '" + System.IO.Path.GetFileName(file) + "': " + LockAdvice + " '" + file + "' unlocks it.";
        }

        private const string LockAdvice = "another program may be using the repository; if none is, deleting";

        internal static bool IsLockContention(GitCommandResult result) =>
            result != null && !result.Success && !result.TimedOut &&
            (result.StandardError ?? string.Empty).IndexOf("index.lock': File exists", StringComparison.Ordinal) >= 0;

        /// <summary>Runs a Git command: reads at once, changes one at a time with short waits on another program's index lock.</summary>
        internal static GitCommandResult Run(IList<string> arguments, int timeoutMilliseconds, Func<GitCommandResult> run, Action<string> log)
        {
            // Reads, and changes Git keeps safe beside local ones (see Kind.Change), never wait for the gate.
            if (Classify(arguments) != Kind.Exclusive) return run();
            // The editor's main thread never waits on the gate (a long background write would freeze Unity): it relies on the retries.
            bool mainThread = UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread();
            bool entered = Gate.Wait(mainThread ? 0 : Math.Max(1000, timeoutMilliseconds));
            // A worker that waited its whole timeout: another change still runs, and two at once is what the gate prevents.
            if (!entered && !mainThread)
                return new GitCommandResult { ExitCode = 1, TimedOut = true, StandardError = "Another Git change is still running. Try again once it is done." };
            try
            {
                bool retryable = RetryableVerbs.Contains(Verb(arguments));
                for (int attempt = 0; ; attempt++)
                {
                    GitCommandResult result = run();
                    if (!result.Success && LockFile.IsMatch(result.StandardError ?? string.Empty) &&
                        (!retryable || attempt >= LockRetryDelays.Length || !IsLockContention(result)))
                        result.StandardError = ExplainLock(result.StandardError);
                    if (!retryable || attempt >= LockRetryDelays.Length || !IsLockContention(result)) return result;
                    log?.Invoke("waiting for another Git process to release the index lock");
                    Thread.Sleep(LockRetryDelays[attempt]);
                }
            }
            finally
            {
                if (entered) Gate.Release();
            }
        }
    }
}
