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
    /// <summary>
    /// What a Git command does to the repository, declared by every call into the runner (the people writing a call know
    /// whether it writes); the runner's guard only ever makes a declared read stricter (UnitGitWriteGate.Effective).
    /// </summary>
    internal enum GitAccess
    {
        /// <summary>Reads only: no gate; a superseded read may stop it; a reload stops it at once.</summary>
        Read,
        /// <summary>
        /// Changes only what Git's own locks keep safe beside local changes, and is never stopped as a superseded read; a
        /// reload gives it the grace for changes. It waits until no exclusive change runs, then runs outside the write
        /// gate, so a slow network never holds a commit behind it. Only the commands Mixer Git really builds are this:
        /// "fetch --all --prune" writes objects (packs written whole, then renamed), FETCH_HEAD, the remote-tracking refs
        /// refs/remotes/&lt;remote&gt;/* (and prunes them) and auto-followed new tags refs/tags/* (never overwriting one), each
        /// under its own lock file, and never the index, the work tree or the checked-out branch. With refspecs the user
        /// configured it can write any other local ref, refs/heads/* included (but the checked-out branch). Every local ref
        /// change Mixer Git makes either carries its expected old value (the rewrite's update-ref, commit, merge, reset and
        /// stash, which Git checks) or creates a ref that must not exist (checkout -b); branch -d and -dr delete without one,
        /// which is why a change of this kind waits for the gate to be free before it starts. Left: a branch delete started
        /// while such a fetch runs can delete a branch that fetch just moved, which only a refspec writing refs/heads/*
        /// allows, and deleting it is what the user asked. "push" and "push -u" write the remote, then their
        /// remote-tracking ref and (with -u) the branch's upstream in the config, under the config's lock. "bundle create"
        /// and "clone" write a file or a new repository outside this one.
        /// </summary>
        Change,
        /// <summary>Changes the index, the work tree, local refs or the configuration: one at a time (the write gate), never
        /// stopped as a superseded read, the reload grace.</summary>
        Exclusive
    }

    internal static class UnitGitWriteGate
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly int[] LockRetryDelays = { 100, 200, 400, 700, 1000, 1500, 2000 };

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

        // Verbs with no mode that writes, whatever their options: a command line made of them and any options is a read,
        // but for the few options that write a file of their own (--output) or run another program.
        private static readonly HashSet<string> ReadOnlyVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "log", "show", "diff", "diff-tree", "diff-index", "diff-files", "ls-files", "ls-tree", "cat-file", "rev-parse",
            "rev-list", "for-each-ref", "merge-base", "check-ignore", "check-attr", "show-ref", "describe", "name-rev", "var",
            "ls-remote", "check-ref-format", "count-objects", "--version", "version",
            // Mixer Git runs merge-file on copies in a temporary folder only (the conflict view's parts): it writes nothing in
            // the repository.
            "merge-file"
        };

        private static readonly string[] WritingOptions = { "--output", "--ext-diff", "--textconv", "--open-files-in-pager", "-O" };

        /// <summary>
        /// The guard behind each call's declared access: true only for the exact read-only shapes Mixer Git builds (a verb
        /// with no writing mode, or a reading mode of stash, config, remote, branch, tag, symbolic-ref, bundle, worktree
        /// with exactly the options those calls use), after only the global options Mixer Git itself puts first
        /// (--literal-pathspecs, -c core.quotePath=...). Everything else, including what it cannot parse, other global
        /// options (-C, --git-dir, --no-pager, -c anything else) and combined short flags, may write.
        /// </summary>
        internal static bool KnownRead(IList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0) return false;
            int at = 0;
            while (at < arguments.Count && (arguments[at] ?? string.Empty).StartsWith("-", StringComparison.Ordinal) && arguments[at] != "--version")
            {
                string option = arguments[at];
                if (option == "--literal-pathspecs") { at++; continue; }
                if (option == "-c" && at + 1 < arguments.Count &&
                    (arguments[at + 1] == "core.quotePath=true" || arguments[at + 1] == "core.quotePath=false")) { at += 2; continue; }
                return false;
            }
            if (at >= arguments.Count) return false;
            string verb = arguments[at];
            var rest = arguments.Skip(at + 1).Select(argument => argument ?? string.Empty).ToList();
            if (ReadOnlyVerbs.Contains(verb))
                return !rest.Any(argument => WritingOptions.Any(option => argument == option || argument.StartsWith(option + "=", StringComparison.Ordinal)));

            bool Only(IEnumerable<string> tokens, params string[] allowed) => tokens.All(token => allowed.Contains(token));
            var options = rest.Where(argument => argument.StartsWith("-", StringComparison.Ordinal)).ToList();
            var names = rest.Where(argument => !argument.StartsWith("-", StringComparison.Ordinal)).ToList();
            switch (verb)
            {
                case "stash":
                    // "stash list [--format=...]", "stash show [--name-status] [-z] [<stash>]".
                    if (rest.Count == 0) return false;
                    if (rest[0] == "list") return rest.Skip(1).All(token => token.StartsWith("--format=", StringComparison.Ordinal));
                    if (rest[0] == "show") return Only(rest.Skip(1).Where(token => token.StartsWith("-", StringComparison.Ordinal)), "--name-status", "-z", "-M", "-p") &&
                                                  rest.Skip(1).Count(token => !token.StartsWith("-", StringComparison.Ordinal)) <= 1;
                    return false;
                case "config":
                    // "config [--bool] --get <name>", "config --get-regexp <pattern>", "config --list".
                {
                    bool listing = options.Contains("--list") || options.Contains("-l");
                    bool getting = options.Contains("--get") || options.Contains("--get-all") || options.Contains("--get-regexp");
                    return Only(options, "--get", "--get-all", "--get-regexp", "--bool", "--list", "-l", "--null", "-z") &&
                           (listing ? !getting && names.Count == 0 : getting && names.Count >= 1 && names.Count <= 2);
                }
                case "remote":
                    return rest.Count == 0 || (rest.Count == 1 && rest[0] == "-v") || (rest.Count == 2 && rest[0] == "get-url");
                case "branch":
                case "tag":
                    // Listing only; a name is a pattern only after --list.
                    return Only(options.Where(option => !option.StartsWith("--format=", StringComparison.Ordinal) && !option.StartsWith("--sort=", StringComparison.Ordinal)),
                               "--list", "-l", "-a", "-r", "--all", "--remotes", "-v", "-vv", "--show-current") &&
                           (names.Count == 0 || options.Contains("--list") || options.Contains("-l"));
                case "symbolic-ref":
                    return Only(options, "--quiet", "-q", "--short") && names.Count == 1;
                case "bundle":
                    return rest.Count == 2 && (rest[0] == "list-heads" || rest[0] == "verify");
                case "worktree":
                    return rest.Count >= 1 && rest[0] == "list" && Only(rest.Skip(1), "--porcelain", "-z");
                default:
                    return false;
            }
        }

        /// <summary>Whether this command line is one of the known reads (see <see cref="KnownRead"/>).</summary>
        internal static bool IsRead(IList<string> arguments) => KnownRead(arguments);

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// What a call actually runs as: its declared access, but a call declared a read whose command line is not a known
        /// read shape runs as an exclusive change (a wrong declaration fails safe) and is reported once. A declared change is
        /// never downgraded.
        /// </summary>
        internal static GitAccess Effective(GitAccess declared, IList<string> arguments)
        {
            if (declared != GitAccess.Read || KnownRead(arguments)) return declared;
            string line = string.Join(" ", arguments);
            bool first;
            lock (Reported) first = Reported.Add(line);
            if (first) UnityEngine.Debug.LogWarning("Mixer Git: \"git " + line + "\" was declared a read but is not one of the reads it knows; it runs as a change.");
            return GitAccess.Exclusive;
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
        /// <summary>Runs a Git command as its access says: reads at once, changes one at a time with short waits on another
        /// program's index lock, and changes Git keeps safe beside local ones once no exclusive change runs.</summary>
        internal static GitCommandResult Run(GitAccess access, IList<string> arguments, int timeoutMilliseconds, Func<GitCommandResult> run, Action<string> log)
        {
            if (access == GitAccess.Read) return run();
            if (access == GitAccess.Change)
            {
                // A change Git keeps safe beside local changes (see GitAccess.Change) waits until no exclusive change runs,
                // then runs without holding the gate: a rewrite or a branch delete in progress blocks a fetch from starting,
                // a fetch in progress does not block a commit. The main thread never waits (as below).
                bool onMain = UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread();
                if (!Gate.Wait(onMain ? 0 : Math.Max(1000, timeoutMilliseconds)))
                {
                    if (!onMain)
                        return new GitCommandResult { ExitCode = 1, TimedOut = true, StandardError = "Another Git change is still running. Try again once it is done." };
                }
                else Gate.Release();
                return run();
            }
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
