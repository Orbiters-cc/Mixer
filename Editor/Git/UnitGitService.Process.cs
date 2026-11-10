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
    // How Git is started: the plain runner, stdin and streamed reads, path lists, the command log and the Git version.
    internal sealed partial class UnitGitService
    {
        /// <summary>Whether these paths fit on one command line (else they go on stdin, or a command must list them all).</summary>
        internal static bool FitsCommandLine(IList<string> paths) =>
            paths.Count <= MaxPathsPerCommand && paths.Sum(path => path.Length + 3) <= MaxPathCharactersPerCommand;

        /// <summary>
        /// Runs Git. <paramref name="access"/> is what the command does to the repository, declared by the caller: a read is
        /// checked against the reads Mixer Git knows and runs as a change when it is not one (UnitGitWriteGate.Effective).
        /// </summary>
        public GitCommandResult RunGit(GitAccess access, int timeoutMilliseconds, params string[] arguments)
        {
            var log = ProcessLogReceived;
            var superseded = ReadSuperseded;
            GitAccess effective = UnitGitWriteGate.Effective(access, arguments);
            return UnitGitWriteGate.Run(effective, arguments, timeoutMilliseconds,
                () => RunStreaming(ProjectRoot, timeoutMilliseconds, log, superseded, null, null, null, effective, arguments),
                line => log?.Invoke(line));
        }

        internal GitCommandResult RunGitWithEnvironment(GitAccess access, IDictionary<string, string> environment, int timeoutMilliseconds, params string[] arguments)
        {
            var log = ProcessLogReceived;
            GitAccess effective = UnitGitWriteGate.Effective(access, arguments);
            return UnitGitWriteGate.Run(effective, arguments, timeoutMilliseconds,
                () => RunStreaming(ProjectRoot, timeoutMilliseconds, log, null, environment, null, null, effective, arguments),
                line => log?.Invoke(line));
        }

        /// <summary>
        /// Runs Git with <paramref name="input"/> (complete before Git starts) written to its stdin while its output is read. A
        /// command that changes the repository takes the write gate as <see cref="RunGit"/> does; a retry on another
        /// program's index lock writes the input again.
        /// </summary>
        internal GitCommandResult RunGitWithInput(GitAccess access, int timeoutMilliseconds, byte[] input, params string[] arguments)
        {
            return RunGitWithInput(access, null, timeoutMilliseconds, input, arguments);
        }

        internal GitCommandResult RunGitWithInput(GitAccess access, IDictionary<string, string> environment, int timeoutMilliseconds, byte[] input, params string[] arguments)
        {
            var log = ProcessLogReceived;
            var superseded = ReadSuperseded;
            GitAccess effective = UnitGitWriteGate.Effective(access, arguments);
            return UnitGitWriteGate.Run(effective, arguments, timeoutMilliseconds,
                () => RunStreaming(ProjectRoot, timeoutMilliseconds, log, superseded, environment, input, null, effective, arguments),
                line => log?.Invoke(line));
        }

        /// <summary>
        /// Reads a command's stdout as raw bytes while it runs; <paramref name="output"/> returns false to stop it early
        /// (the result is then a success with <see cref="GitCommandResult.Stopped"/>). Reads only: never gated.
        /// </summary>
        internal GitCommandResult ReadGitStream(GitAccess access, int timeoutMilliseconds, Func<byte[], int, int, bool> output, params string[] arguments)
        {
            var log = ProcessLogReceived;
            var superseded = ReadSuperseded;
            GitAccess effective = UnitGitWriteGate.Effective(access, arguments);
            return UnitGitWriteGate.Run(effective, arguments, timeoutMilliseconds,
                () => RunStreaming(ProjectRoot, timeoutMilliseconds, log, superseded, null, null, output, effective, arguments),
                line => log?.Invoke(line));
        }

        private static GitCommandResult RunStreaming(string workingDirectory, int timeoutMilliseconds, UnitGitProcessLogHandler logHandler,
            Func<bool> superseded, IDictionary<string, string> environment, byte[] input, Func<byte[], int, int, bool> output, GitAccess access,
            string[] arguments)
        {
            // A change is never stopped because a newer read superseded the refresh that asked for it: it runs to its end.
            bool write = access != GitAccess.Read;
            if (write) superseded = null;
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
                EditorProcessResult run = UnitGitRunner.Run(new UnitGitRunRequest
                {
                    WorkingDirectory = workingDirectory,
                    Arguments = arguments,
                    TimeoutMilliseconds = timeoutMilliseconds,
                    Environment = environment,
                    Cancelled = superseded,
                    Write = write,
                    Input = input,
                    Output = output,
                    // A read's output only fills the log: its lines are not logged (the command, its exit and errors are).
                    OutputLine = log == null || !write ? null : (Action<string>)(line => log("stdout: " + line)),
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
                // A change stopped before its end may have left Git's lock file: say which, once (nothing deletes it).
                if (write && (run.TimedOut || run.Cancelled))
                    result.StandardError = UnitGitWriteGate.ExplainLock(result.StandardError, IndexLockOf(workingDirectory, environment));
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

        // The lock a change of the index takes: the scoped index's own (GIT_INDEX_FILE), else the repository's (by name only,
        // never starting Git: this runs when a change was just stopped).
        private static string IndexLockOf(string workingDirectory, IDictionary<string, string> environment)
        {
            if (environment != null && environment.TryGetValue("GIT_INDEX_FILE", out string scoped) && !string.IsNullOrEmpty(scoped))
                return Path.GetFullPath(Path.Combine(workingDirectory, scoped)) + ".lock";
            return UnitGitPaths.TryGetKnown(workingDirectory, "index", out string index) ? index + ".lock" : Path.Combine(workingDirectory, ".git", "index.lock");
        }

        /// <summary>
        /// Runs "git --literal-pathspecs &lt;arguments&gt;" once for all <paramref name="paths"/>, which Git reads from stdin
        /// (NUL-separated: any name, spaces, quotes, line breaks and leading dashes included). No paths run nothing: Git
        /// would read that as the whole tree. The command must take --pathspec-from-file (add, reset, restore, rm, checkout,
        /// commit, stash push).
        /// </summary>
        private GitCommandResult RunGitForPaths(int timeoutMilliseconds, IList<string> paths, params string[] arguments)
        {
            return RunGitForPaths(null, timeoutMilliseconds, paths, arguments);
        }

        /// <summary>The same with extra environment variables (a scoped index's GIT_INDEX_FILE).</summary>
        internal GitCommandResult RunGitForPaths(IDictionary<string, string> environment, int timeoutMilliseconds, IList<string> paths, params string[] arguments)
        {
            // An empty entry is no path: Git refuses it, and dropped it cannot become "everything".
            paths = paths?.Where(path => !string.IsNullOrEmpty(path)).ToList();
            if (paths == null || paths.Count == 0)
                return new GitCommandResult();
            var args = new List<string> { "--literal-pathspecs" };
            args.AddRange(arguments);
            args.Add("--pathspec-file-nul");
            byte[] writePaths = NulSeparated(paths);
            if (UnitGitRunner.CleanInput)
            {
                args.Add("--pathspec-from-file=-");
                return RunGitWithInput(GitAccess.Exclusive, environment, timeoutMilliseconds, writePaths, args.ToArray());
            }

            // Stdin would start with a byte order mark (see UnitGitRunner.CleanInput): the same list from a file.
            string file = Path.Combine(Path.GetTempPath(), "mixer-git-paths-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllBytes(file, writePaths);
                args.Add("--pathspec-from-file=" + file);
                return environment == null
                    ? RunGit(GitAccess.Exclusive, timeoutMilliseconds, args.ToArray())
                    : RunGitWithEnvironment(GitAccess.Exclusive, environment, timeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(file);
            }
        }

        /// <summary>
        /// A read that takes a list on stdin (revisions for "log --stdin", paths for "check-ignore --stdin"): on stdin when it
        /// can be clean (<see cref="UnitGitRunner.CleanInput"/>), else on as many command lines as it takes, the outputs
        /// joined. Exit 1 (check-ignore: none of these) is kept only when every part says it.
        /// </summary>
        internal GitCommandResult RunWithList(GitAccess access, IDictionary<string, string> environment, int timeoutMilliseconds, IList<string> items, bool nul,
            string[] stdinArguments, string[] argumentsBeforeItems, string[] argumentsAfterItems = null)
        {
            if (UnitGitRunner.CleanInput)
                return RunGitWithInput(access, environment, timeoutMilliseconds,
                    nul ? NulSeparated(items) : UnitGitRunner.Utf8.GetBytes(string.Join("\n", items) + "\n"), stdinArguments);
            var output = new StringBuilder();
            var error = new StringBuilder();
            int exit = 1;
            int index = 0;
            while (index < items.Count)
            {
                var batch = new List<string> { items[index++] };
                while (index < items.Count && FitsCommandLine(batch.Concat(new[] { items[index] }).ToList())) batch.Add(items[index++]);
                var args = new List<string>(argumentsBeforeItems);
                args.AddRange(batch);
                if (argumentsAfterItems != null) args.AddRange(argumentsAfterItems);
                GitCommandResult part = environment == null ? RunGit(access, timeoutMilliseconds, args.ToArray()) : RunGitWithEnvironment(access, environment, timeoutMilliseconds, args.ToArray());
                if (part.TimedOut || (part.ExitCode != 0 && part.ExitCode != 1)) return part;
                output.Append(part.StandardOutput);
                error.Append(part.StandardError);
                if (part.ExitCode == 0) exit = 0;
            }
            return new GitCommandResult { ExitCode = items.Count == 0 ? 0 : exit, StandardOutput = output.ToString(), StandardError = error.ToString() };
        }

        /// <summary>Each text in UTF-8 followed by a NUL: a path list as Git reads it with -z / --pathspec-file-nul.</summary>
        internal static byte[] NulSeparated(IEnumerable<string> texts)
        {
            var bytes = new MemoryStream();
            foreach (string text in texts)
            {
                byte[] encoded = UnitGitRunner.Utf8.GetBytes(text ?? string.Empty);
                bytes.Write(encoded, 0, encoded.Length);
                bytes.WriteByte(0);
            }
            return bytes.ToArray();
        }

        // The paths of these files that Git has in HEAD (ls-tree) or in the index (ls-files): neither takes a path list on
        // stdin, so a long selection is answered from the whole listing, a short one with the paths on the command line.
        private GitCommandResult ListPaths(IList<string> paths, List<string> listed, params string[] arguments)
        {
            var args = new List<string>(arguments);
            args.Insert(1, "-z"); // right after the verb: ls-tree reads what follows its tree as paths
            bool whole = !FitsCommandLine(paths);
            if (!whole)
            {
                args.Insert(0, "--literal-pathspecs");
                args.Add("--");
                args.AddRange(paths);
            }

            GitCommandResult result = RunGit(GitAccess.Read, DefaultTimeoutMilliseconds, args.ToArray());
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
                    var result = RunGit(GitAccess.Read, 10000, "--version");
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

                // A read's output only fills the log: its lines are not logged (the command, its exit and errors are).
                // Only gh and glab come here (Git goes through RunStreaming): their output is logged.
                bool logOutput = logHandler != null;
                var processResult = EditorProcessRunner.Run(startInfo, timeoutMilliseconds, superseded,
                    logOutput ? (Action<string>)(line => logHandler("stdout: " + line)) : null,
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
