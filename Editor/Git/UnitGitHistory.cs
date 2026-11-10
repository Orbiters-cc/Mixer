using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The history as one <c>git log</c> read as it streams: "load more" and a search continue where the reading stopped
    /// instead of reading from the top again (or paging with --skip). Git blocks on its full pipe while nobody reads, so a
    /// kept process costs nothing until more rows are wanted. A new <see cref="Key"/> (another filter, search or refs)
    /// starts over. The process stops after a minute unused (Git on Windows cannot delete a pack file a reader holds open)
    /// and at reload and quit; the next read starts a new one where the last left off. Thread-safe.
    /// </summary>
    internal sealed class UnitGitHistory : IDisposable
    {
        private const int ReadTimeoutMilliseconds = 60000;
        private const int IdleMilliseconds = 60000;
        private static readonly object LiveSync = new object();
        private static readonly HashSet<UnitGitHistory> Live = new HashSet<UnitGitHistory>();
        private static Timer idleTimer;

        private readonly object gate = new object();
        private readonly MemoryStream record = new MemoryStream();
        private readonly List<UnitGitCommit> commits = new List<UnitGitCommit>();
        private UnitGitLiveProcess process;
        private string key, root, search;
        private string[] arguments = Array.Empty<string>();
        private int consumed; // commits Git printed and this read, matching the search or not
        private bool ended, withTrailers = true;
        private volatile bool disposed;
        private long lastUse;

        static UnitGitHistory()
        {
            UnitGitProcessLifetime.OnStopping(() =>
            {
                lock (LiveSync)
                {
                    idleTimer?.Dispose();
                    idleTimer = null;
                    Live.Clear();
                }
            });
        }

        /// <summary>How many Git processes this reader started (one per history, unless stopped while idle).</summary>
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
                Interlocked.Exchange(ref lastUse, Stopwatch.GetTimestamp());
                if (key != historyKey || root != projectRoot)
                {
                    StopProcess();
                    commits.Clear();
                    consumed = 0;
                    ended = false;
                    withTrailers = UnitGitService.TrailersSupported != false;
                    key = historyKey;
                    root = projectRoot;
                    search = logSearch;
                    arguments = new List<string>(logArguments).ToArray();
                }

                GitCommandResult status = new GitCommandResult();
                // One more than asked: whether there are more is known without reading a page ahead.
                while (commits.Count <= count && !ended)
                {
                    if (cancelled?.Invoke() == true) return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." };
                    status = ReadNext(cancelled, log);
                    if (!status.Success) return status;
                }

                result.AddRange(commits.GetRange(0, Math.Min(count, commits.Count)));
                more = commits.Count > count;
                Register(true);
                return status;
            }
        }

        // Reads one commit (or the end) from the stream, starting Git where the last process stopped.
        private GitCommandResult ReadNext(Func<bool> cancelled, UnitGitProcessLogHandler log)
        {
            // A local: Dispose may drop the field from another thread (the window closing) while this reads.
            UnitGitLiveProcess live = process;
            if (live == null)
            {
                var args = new List<string> { "log", "-z", "--pretty=format:" + UnitGitService.CommitLogFormat(withTrailers) };
                if (consumed > 0) args.Add("--skip=" + consumed.ToString(CultureInfo.InvariantCulture));
                args.AddRange(arguments);
                log?.Invoke(UnitGitRedaction.Redact("> " + UnitGitService.FormatCommandLine("git", args.ToArray())));
                live = UnitGitLiveProcess.Start(root, args, null, false);
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
                consumed++;
                if (UnitGitService.TrailersSupported == null && withTrailers) UnitGitService.TrailersSupported = true;
                if (UnitGitService.MatchesSearch(commit, search)) commits.Add(commit);
                return new GitCommandResult();
            }

            // Stopped inside a commit: what was read of it is gone, so the next read starts a new process after the last
            // whole commit.
            if (timedOut) { StopProcess(); return new GitCommandResult { ExitCode = 1, TimedOut = true, StandardError = "Reading the history timed out." }; }
            if (cancelled?.Invoke() == true) { StopProcess(); return new GitCommandResult { ExitCode = 1, StandardError = "Read superseded." }; }
            if (UnitGitProcessLifetime.IsStopping) { StopProcess(); return new GitCommandResult { ExitCode = 1, StandardError = "Command interrupted by editor reload or shutdown." }; }

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

            // Git too old for the release trailer placeholder: once, the same history without it (the field stays, empty).
            if (consumed == 0 && withTrailers && UnitGitService.TrailersSupported != true)
            {
                withTrailers = false;
                GitCommandResult retry = ReadNext(cancelled, log);
                if (retry.Success) UnitGitService.TrailersSupported = false;
                else withTrailers = true;
                return retry;
            }

            return new GitCommandResult { ExitCode = exit == 0 ? 1 : exit, StandardError = string.IsNullOrWhiteSpace(error) ? "git log failed." : error };
        }

        private void StopProcess()
        {
            Interlocked.Exchange(ref process, null)?.Dispose();
        }

        private void Register(bool live)
        {
            lock (LiveSync)
            {
                if (live && process != null)
                {
                    Live.Add(this);
                    if (idleTimer == null) idleTimer = new Timer(_ => StopIdle(), null, IdleMilliseconds / 3, IdleMilliseconds / 3);
                }
                else Live.Remove(this);
            }
        }

        private static void StopIdle()
        {
            UnitGitHistory[] all;
            lock (LiveSync) all = new List<UnitGitHistory>(Live).ToArray();
            foreach (UnitGitHistory history in all)
            {
                if (!Monitor.TryEnter(history.gate)) continue; // reading now: not idle
                try
                {
                    double idle = (Stopwatch.GetTimestamp() - Interlocked.Read(ref history.lastUse)) * 1000d / Stopwatch.Frequency;
                    if (idle < IdleMilliseconds) continue;
                    history.StopProcess();
                    history.Register(false);
                }
                finally
                {
                    Monitor.Exit(history.gate);
                }
            }
        }

        /// <summary>Stops the reading now, even while another thread reads (it then ends as superseded): never waits.</summary>
        public void Dispose()
        {
            disposed = true;
            StopProcess();
            Register(false);
        }
    }
}
