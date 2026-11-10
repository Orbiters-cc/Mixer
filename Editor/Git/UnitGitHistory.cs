using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The history as one <c>git log</c> per read, read as it streams and stopped as soon as the read has its rows: nothing
    /// outlives a read (on Windows a running Git keeps the repository's folder and the pack files it maps from being moved
    /// or deleted). The commits read are kept: "load more" and a search with the same <see cref="Key"/> start Git again
    /// after the last commit read (--skip), and check that this commit comes first again, else read from the top. A new key
    /// (another filter, search or refs) starts over. Thread-safe.
    /// </summary>
    internal sealed class UnitGitHistory : IDisposable
    {
        private const int ReadTimeoutMilliseconds = 60000;

        private readonly object gate = new object();
        private readonly MemoryStream record = new MemoryStream();
        private readonly List<UnitGitCommit> commits = new List<UnitGitCommit>();
        private EditorLiveProcess process;
        private string key, root, search;
        private string[] arguments = Array.Empty<string>();
        private int consumed; // commits Git printed and this read, matching the search or not
        private string lastConsumed = string.Empty; // the id of the last of them
        private bool resuming; // a process started after commits were read: its first commit must be lastConsumed
        private bool ended;
        private volatile bool disposed;

        /// <summary>How many Git processes this reader started (one per read that needed more commits).</summary>
        internal int ProcessesStarted { get; private set; }

        /// <summary>What the commits held were read for; reads with another key start over.</summary>
        internal string Key { get { lock (gate) return key; } }

        /// <summary>
        /// The first <paramref name="count"/> commits of this history that match <paramref name="logSearch"/> (fewer when
        /// the history has no more), reading on from where the last read stopped when <paramref name="historyKey"/> is the
        /// same. <paramref name="more"/>: the history has commits after them.
        /// </summary>
        internal GitCommandResult Read(string projectRoot, string historyKey, IList<string> logArguments, string logSearch, int count,
            Func<bool> cancelled, out List<UnitGitCommit> result, out bool more, UnitGitProcessLogHandler log = null)
        {
            result = new List<UnitGitCommit>();
            more = false;
            lock (gate)
            {
                if (disposed) return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." };
                if (key != historyKey || root != projectRoot)
                {
                    StopProcess();
                    commits.Clear();
                    consumed = 0;
                    lastConsumed = string.Empty;
                    resuming = false;
                    ended = false;
                    key = historyKey;
                    root = projectRoot;
                    search = logSearch;
                    arguments = new List<string>(logArguments).ToArray();
                }

                GitCommandResult status = new GitCommandResult();
                try
                {
                    // One more than asked: whether there are more is known without reading a page ahead.
                    while (commits.Count <= count && !ended)
                    {
                        if (cancelled?.Invoke() == true) return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." };
                        status = ReadNext(cancelled, log);
                        if (!status.Success) return status;
                    }
                }
                finally
                {
                    // Stopped and waited for before the read returns, on every path.
                    StopProcess();
                }

                result.AddRange(commits.GetRange(0, Math.Min(count, commits.Count)));
                more = commits.Count > count;
                return status;
            }
        }

        // Reads one commit (or the end) from the stream, starting Git where the last process stopped.
        private GitCommandResult ReadNext(Func<bool> cancelled, UnitGitProcessLogHandler log)
        {
            // A local: Dispose may drop the field from another thread (the window closing) while this reads.
            EditorLiveProcess live = process;
            if (live == null)
            {
                var args = new List<string> { "log", "-z", "--pretty=format:" + UnitGitService.CommitLogFormat() };
                // After an idle stop: from the last commit already read, which must come first again (else the history
                // moved under the same refs signature, and it is read again from the top).
                resuming = consumed > 0;
                if (resuming) args.Add("--skip=" + (consumed - 1).ToString(CultureInfo.InvariantCulture));
                args.AddRange(arguments);
                log?.Invoke(UnitGitRedaction.Redact("> " + UnitGitService.FormatCommandLine("git", args.ToArray())));
                live = UnitGitRunner.StartLive(root, args, null, false);
                ProcessesStarted++;
                if (live == null) return new GitCommandResult { ExitCode = 1, StandardError = "Command interrupted by editor reload or shutdown." };
                live.CloseInput();
                process = live;
                if (disposed) StopProcess();
            }

            if (live.ReadRecord(0, record, ReadTimeoutMilliseconds, cancelled, out bool timedOut))
            {
                string text = UnitGitRunner.Utf8.GetString(record.GetBuffer(), 0, (int)record.Length).TrimStart('\n');
                UnitGitCommit commit = UnitGitService.ParseCommit(text);
                if (commit == null) return new GitCommandResult();
                if (resuming)
                {
                    resuming = false;
                    if (commit.FullHash == lastConsumed) return new GitCommandResult();
                    StopProcess();
                    commits.Clear();
                    consumed = 0;
                    lastConsumed = string.Empty;
                    return new GitCommandResult();
                }
                consumed++;
                lastConsumed = commit.FullHash;
                if (UnitGitService.MatchesSearch(commit, search)) commits.Add(commit);
                return new GitCommandResult();
            }

            // Stopped inside a commit: what was read of it is gone, so the next read starts a new process after the last
            // whole commit.
            if (timedOut) { StopProcess(); return new GitCommandResult { ExitCode = 1, TimedOut = true, StandardError = "Reading the history timed out." }; }
            if (cancelled?.Invoke() == true) { StopProcess(); return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." }; }
            if (EditorProcessRunner.IsStopping) { StopProcess(); return new GitCommandResult { ExitCode = 1, StandardError = "Command interrupted by editor reload or shutdown." }; }

            // The end of the stream: Git is done, or failed (an unknown branch in the filter).
            var waited = Stopwatch.StartNew();
            while (!live.HasExited && waited.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            live.DrainError();
            int exit = live.HasExited ? live.ExitCode : -1;
            string error = live.StandardError;
            if (disposed) return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." };
            StopProcess();
            if (exit == 0)
            {
                ended = true;
                return new GitCommandResult();
            }

            return new GitCommandResult { ExitCode = exit == 0 ? 1 : exit, StandardError = string.IsNullOrWhiteSpace(error) ? "git log failed." : error };
        }

        private void StopProcess()
        {
            Interlocked.Exchange(ref process, null)?.Dispose();
        }

        /// <summary>Stops the process only, keeping what was read (the next read resumes).</summary>
        internal void StopReading()
        {
            lock (gate) StopProcess();
        }

        /// <summary>Stops the reading now, even while another thread reads (it then ends as superseded): never waits.</summary>
        public void Dispose()
        {
            disposed = true;
            StopProcess();
        }
    }
}
