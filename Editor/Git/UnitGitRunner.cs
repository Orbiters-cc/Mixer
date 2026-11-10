using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>One Git command for <see cref="UnitGitRunner.Run"/>.</summary>
    internal sealed class UnitGitRunRequest
    {
        public string WorkingDirectory = string.Empty;
        public IList<string> Arguments = Array.Empty<string>();
        public int TimeoutMilliseconds = UnitGitService.DefaultTimeoutMilliseconds;
        public IDictionary<string, string> Environment;
        public Func<bool> Cancelled;
        /// <summary>Git's whole stdin, complete before Git starts (nothing can fail halfway through a list but the pipe
        /// itself), written on a thread of its own while the outputs are read so no pipe can fill up and stall both sides.
        /// Null: stdin is closed at once.</summary>
        public byte[] Input;
        /// <summary>The command changes the repository: a reload or quit lets it finish (up to
        /// <see cref="EditorProcessRunner.WriteGraceMilliseconds"/>) instead of stopping it at once.</summary>
        public bool Write;
        /// <summary>Git's stdout as raw bytes, as they come. Return false to stop: the process is stopped and the result says
        /// <see cref="EditorProcessResult.Stopped"/>. Null: stdout is kept and decoded as UTF-8 text.</summary>
        public Func<byte[], int, int, bool> Output;
        /// <summary>Each line of stdout (only when <see cref="Output"/> is null) and of stderr, for the command log.</summary>
        public Action<string> OutputLine, ErrorLine;
    }

    /// <summary>
    /// Git's policy on top of Toolkit's runner (<see cref="EditorProcessRunner"/>, which owns the processes: a job each, one
    /// start lock for every process the editor starts, stops at reload and quit with a grace for changes): how Git is
    /// started (UTF-8 everywhere, no prompts, no optional locks) and what its stdin may hold.
    /// </summary>
    internal static class UnitGitRunner
    {
        internal static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Whether Git's stdin starts with exactly the bytes written (<see cref="EditorProcessRunner.CleanInput"/>). When
        /// false, path lists and messages go in a file instead. Tests set <see cref="ForceNoCleanInput"/>.
        /// </summary>
        internal static bool CleanInput => !ForceNoCleanInput && EditorProcessRunner.CleanInput;

        internal static bool ForceNoCleanInput;

        /// <summary>
        /// Prepended to every Git command: what Git prints of commits is UTF-8 and what it records is marked UTF-8, whatever
        /// the user's i18n settings (a commit stored in another encoding is re-encoded when read), since Mixer Git reads and
        /// writes UTF-8.
        /// </summary>
        internal static readonly string[] EncodingArguments = { "-c", "i18n.logOutputEncoding=UTF-8", "-c", "i18n.commitEncoding=UTF-8" };

        internal static ProcessStartInfo StartInfo(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", EncodingArguments.Concat(arguments).Select(UnitGitService.EscapeArgument).ToArray()),
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                CreateNoWindow = true
            };
            if (input) EditorProcessRunner.UseCleanInput(start);
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            // Reads (a refresh's status) must not take the index lock: a commit or a staging starting meanwhile would fail on it.
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            if (environment != null)
                foreach (KeyValuePair<string, string> item in environment)
                    if (!string.IsNullOrWhiteSpace(item.Key)) start.EnvironmentVariables[item.Key] = item.Value ?? string.Empty;
            return start;
        }

        /// <summary>Runs one command to its end (or its timeout, cancel or early stop). Call from a worker for long commands.</summary>
        internal static EditorProcessResult Run(UnitGitRunRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Input != null && !CleanInput)
                throw new InvalidOperationException("Git's input would start with a byte order mark on this system.");
            return EditorProcessRunner.Run(new EditorProcessRequest
            {
                StartInfo = StartInfo(request.WorkingDirectory, request.Arguments, request.Environment, request.Input != null),
                TimeoutMilliseconds = request.TimeoutMilliseconds,
                Cancelled = request.Cancelled,
                Input = request.Input,
                Write = request.Write,
                Output = request.Output,
                OutputLine = request.OutputLine,
                ErrorLine = request.ErrorLine
            });
        }

        /// <summary>
        /// Starts Git kept alive between requests (<c>cat-file --batch</c>, a <c>log</c> read as it streams); null when the
        /// editor is stopping (reload, quit). Throws when Git cannot start.
        /// </summary>
        internal static EditorLiveProcess StartLive(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input)
        {
            return EditorLiveProcess.Start(StartInfo(workingDirectory, arguments, environment, input));
        }

        /// <summary>UTF-8 text without a leading byte order mark, as the runner decodes Git's output.</summary>
        internal static string Decode(byte[] bytes, int count)
        {
            int start = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Utf8.GetString(bytes, start, count - start);
        }
    }
}
