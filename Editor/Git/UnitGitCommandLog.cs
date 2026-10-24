using System;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>One command Mixer Git ran (git, gh or glab), with its answer. Secrets are redacted.</summary>
    public sealed class UnitGitCommandRecord
    {
        public string CommandLine = string.Empty;
        public string WorkingDirectory = string.Empty;
        public int ExitCode;
        public bool TimedOut;
        public string StandardOutput = string.Empty;
        public string StandardError = string.Empty;
        public double Milliseconds;
        public DateTime FinishedUtc;

        public bool Success => ExitCode == 0 && !TimedOut;
    }

    /// <summary>
    /// Every command Mixer Git runs, as it finishes, for tools that keep a log (Mixer console). Raised on the thread
    /// that ran the command, often not Unity's main thread: handlers must be fast and thread-safe.
    /// </summary>
    public static class UnitGitCommandLog
    {
        public static event Action<UnitGitCommandRecord> Completed;

        internal static bool HasListeners => Completed != null;

        internal static void Raise(UnitGitCommandRecord record)
        {
            var handlers = Completed;
            if (handlers == null)
            {
                return;
            }

            try
            {
                handlers(record);
            }
            catch (Exception)
            {
                // A listener's failure must never fail a Git command.
            }
        }
    }
}
