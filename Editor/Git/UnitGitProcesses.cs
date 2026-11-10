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

        // Commands that only read (or, like fetch and push, never touch the index): never gated.
        private static readonly HashSet<string> ReadVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "log", "show", "diff", "ls-files", "ls-tree", "cat-file", "rev-parse", "rev-list", "for-each-ref",
            "merge-base", "check-ignore", "check-attr", "show-ref", "describe", "blame", "name-rev", "version", "--version",
            "grep", "shortlog", "count-objects", "var", "ls-remote", "fetch", "push", "bundle", "hash-object", "merge-tree",
            "help", "diff-tree", "diff-index", "diff-files", "cherry", "range-diff", "check-ref-format", "verify-commit",
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

        internal static bool IsRead(IList<string> arguments)
        {
            string verb = Verb(arguments);
            if (ReadVerbs.Contains(verb)) return true;
            var rest = arguments.SkipWhile(argument => argument != verb).Skip(1).ToList();
            switch (verb)
            {
                case "stash": return rest.Count > 0 && (rest[0] == "list" || rest[0] == "show");
                case "config": return rest.Any(argument => argument.StartsWith("--get", StringComparison.Ordinal) || argument == "--list" || argument == "-l");
                case "remote": return rest.Count == 0 || rest[0] == "-v" || rest[0] == "get-url" || rest[0] == "show";
                case "branch":
                case "tag":
                    if (rest.Any(argument => argument == "-d" || argument == "-D" || argument == "--delete" || argument == "-m" || argument == "-M" ||
                                             argument == "-c" || argument == "-C" || argument == "-u" || argument == "-f" ||
                                             argument.StartsWith("--set-upstream", StringComparison.Ordinal) || argument == "--unset-upstream"))
                        return false;
                    return rest.Count == 0 || rest.Any(argument => argument == "--list" || argument == "-l" || argument == "--show-current" ||
                                                                   argument.StartsWith("--format", StringComparison.Ordinal) || argument == "-v" || argument == "-vv" ||
                                                                   argument == "-a" || argument == "-r");
                case "symbolic-ref": return rest.Count(argument => !argument.StartsWith("-", StringComparison.Ordinal)) <= 1;
                case "worktree": return rest.Count > 0 && rest[0] == "list";
                default: return false;
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
            if (IsRead(arguments)) return run();
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
