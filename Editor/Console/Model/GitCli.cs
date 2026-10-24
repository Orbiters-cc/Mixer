using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Orbiters.Logger.Editor
{
    /// <summary>What a Git command printed and how it ended.</summary>
    internal readonly struct GitResult
    {
        /// <summary>Git exited with 0.</summary>
        public readonly bool Success;

        /// <summary>Git ran and exited in time (false: not installed, could not start, or stopped at the timeout).</summary>
        public readonly bool Completed;
        public readonly string Output;
        public readonly string Error;

        public GitResult(bool success, bool completed, string output, string error)
        {
            Success = success;
            Completed = completed;
            Output = output ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public static GitResult NotRun(string error) => new GitResult(false, false, null, error);
    }

    /// <summary>
    /// Runs Git in the project without a window, for the timeline and the project report. Call it from a worker thread:
    /// it waits for Git to finish (or the timeout to pass, then stops Git and what it started). Read-only use: Git
    /// takes no optional locks (a commit made meanwhile never meets "index.lock exists") and never asks for a password.
    /// </summary>
    internal static class GitCli
    {
        private static readonly Lazy<string> Found = new Lazy<string>(Find);

        /// <summary>Tests point this at another Git (or at nothing).</summary>
        internal static string ExecutableOverride;

        internal static string ProjectRoot
        {
            get
            {
                try
                {
                    return Path.GetFullPath(".");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>Git's full path, or null when it is not installed.</summary>
        internal static string Executable => ExecutableOverride ?? Found.Value;

        /// <summary>Whether <paramref name="root"/> is a Git work tree (a ".git" folder, or a file in a linked worktree).</summary>
        internal static bool IsRepository(string root) =>
            !string.IsNullOrEmpty(root) && (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")));

        internal static GitResult Run(string root, params string[] arguments) => Run(root, 15000, arguments);

        /// <summary>Runs <c>git <paramref name="arguments"/></c> in <paramref name="root"/>; each argument is quoted as needed.</summary>
        internal static GitResult Run(string root, int timeoutMs, params string[] arguments)
        {
            string git = Executable;
            if (string.IsNullOrEmpty(git) || !File.Exists(git))
            {
                return GitResult.NotRun("Git is not installed.");
            }

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return GitResult.NotRun("The project folder can't be read.");
            }

            var command = new StringBuilder("-c core.quotepath=off -c core.fsmonitor=false -c color.ui=never");
            foreach (string argument in arguments)
            {
                command.Append(' ').Append(Quote(argument));
            }

            // The folder is the working directory, not an argument: nothing to quote, nothing a folder name can break.
            var start = new ProcessStartInfo(git, command.ToString())
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            Process process;
            try
            {
                process = Process.Start(start);
            }
            catch (Exception exception)
            {
                return GitResult.NotRun("Git could not be started: " + exception.Message);
            }

            if (process == null)
            {
                return GitResult.NotRun("Git could not be started.");
            }

            using (process)
            {
                // Both streams read at once: a full stderr pipe would otherwise block Git while stdout is read.
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                Observe(output);
                Observe(error);
                bool exited;
                try
                {
                    exited = process.WaitForExit(Math.Max(0, timeoutMs));
                }
                catch (Exception)
                {
                    exited = false;
                }

                if (!exited)
                {
                    StopTree(process);
                    // The reads end once the pipes close; a helper still holding them must not keep this thread.
                    WaitQuietly(output, error, 2000);
                    return GitResult.NotRun("Git took longer than " + Math.Max(1, timeoutMs / 1000) + " s.");
                }

                // A process Git started (a daemon) can keep the pipes open: wait a little, then take what came.
                WaitQuietly(output, error, 5000);
                int code;
                try
                {
                    code = process.ExitCode;
                }
                catch (Exception)
                {
                    code = -1;
                }

                return new GitResult(code == 0, true, output.Status == TaskStatus.RanToCompletion ? output.Result : null,
                    error.Status == TaskStatus.RanToCompletion ? error.Result : null);
            }
        }

        /// <summary>One argument for the command line, quoted the way Windows and Mono split them back.</summary>
        internal static string Quote(string argument)
        {
            argument = argument ?? string.Empty;
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '"' }) < 0)
            {
                return argument;
            }

            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                // Backslashes before a quote are doubled, and the quote escaped; elsewhere they stay as they are.
                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }

        private static void Observe(Task task) =>
            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        private static void WaitQuietly(Task first, Task second, int milliseconds)
        {
            try
            {
                Task.WaitAll(new[] { first, second }, milliseconds);
            }
            catch (Exception)
            {
                // A read that failed has no output to give.
            }
        }

        // Git with what it started (Git LFS, credential helpers, remote helpers): Process.Kill only stops Git itself.
        private static void StopTree(Process process)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Wait(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + process.Id + " /T /F");
                }
                else if (File.Exists("/usr/bin/pkill"))
                {
                    Wait("/usr/bin/pkill", "-KILL -P " + process.Id);
                }
            }
            catch (Exception)
            {
                // Git is stopped below either way.
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.WaitForExit(3000);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        private static void Wait(string file, string arguments)
        {
            if (!File.Exists(file))
            {
                return;
            }

            using (var helper = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true }))
            {
                helper?.WaitForExit(5000);
            }
        }

        // PATH folders that are absolute and outside the project (Windows looks in the current folder, the project,
        // before PATH: a git.exe shipped in a project would run), then the usual install folders. On macOS /usr/bin/git
        // is a stub that asks to install the command line tools: the real one is used once they are installed.
        private static string Find()
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string name = windows ? "git.exe" : "git";
            string project = ProjectRoot;
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                try
                {
                    string folder = entry.Trim().Trim('"');
                    if (folder.Length == 0 || !Path.IsPathRooted(folder))
                    {
                        continue;
                    }

                    folder = Path.GetFullPath(folder);
                    if (!string.IsNullOrEmpty(project) && folder.StartsWith(project.TrimEnd('\\', '/'), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(folder, name);
                    if (mac && candidate == "/usr/bin/git")
                    {
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // A malformed PATH entry.
                }
            }

            string[] known = windows
                ? new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
                }
                : mac
                    ? new[]
                    {
                        "/opt/homebrew/bin/git", "/usr/local/bin/git", "/Library/Developer/CommandLineTools/usr/bin/git",
                        "/Applications/Xcode.app/Contents/Developer/usr/bin/git"
                    }
                    : new[] { "/usr/bin/git", "/usr/local/bin/git" };
            return known.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
        }
    }
}
