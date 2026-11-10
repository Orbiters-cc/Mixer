using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Captures every log of the editor session, whether the Mixer console window is open or not, so opening it is instant.
    /// <list type="bullet">
    /// <item>Live: <see cref="Application.logMessageReceivedThreaded"/> queues each log (any thread) with its time; the
    /// main thread files them into the <see cref="LogStore"/> a few milliseconds per editor update.</item>
    /// <item>History: what Unity's console already held when the Mixer console started is read in, newest session first.</item>
    /// <item>Compiler messages come from the compilation pipeline and stay until a compilation no longer reports them.</item>
    /// <item>Script reloads: the store is saved before and read back after, plus what Unity logged in between.</item>
    /// </list>
    /// Nothing here may log or throw: the Mixer console would capture its own errors in a loop.
    /// </summary>
    [InitializeOnLoad]
    internal static class LogCapture
    {
        private const int MaxQueued = 1_000_000;
        // Characters of text the queue may hold (64 MB of UTF-16) while the main thread is busy: a flood of long, distinct
        // logs is bounded by its size, not only by its number.
        private const long MaxQueuedChars = 32L * 1024 * 1024;
        private const int MaxReloadGapRows = 50_000;
        internal const string MaxLogsPref = "Orbiters.Logger.MaxLogs";
        internal const string GroupingPref = "Orbiters.Logger.Grouping";

        private readonly struct RawLog
        {
            public readonly long Time;
            public readonly SharedText Condition;
            public readonly SharedText Stack;
            public readonly LogType Type;
            public readonly bool Background;
            public readonly bool Injected;
            /// <summary>A timing's measured profile (its text form is <see cref="Stack"/>), or null.</summary>
            public readonly TimingProfile Timing;

            public RawLog(long time, SharedText condition, SharedText stack, LogType type, bool background, bool injected, TimingProfile timing)
            {
                Time = time;
                Condition = condition;
                Stack = stack;
                Type = type;
                Background = background;
                Injected = injected;
                Timing = timing;
            }
        }

        [ThreadStatic] private static QueueTexts.Recent recent;
        private static readonly QueueTexts queueTexts = new QueueTexts(MaxQueuedChars);

        private static readonly ConcurrentQueue<RawLog> queue = new ConcurrentQueue<RawLog>();
        private static readonly ConsoleMirror mirror = new ConsoleMirror();
        private static readonly HashSet<string> compileCycle = new HashSet<string>(StringComparer.Ordinal);
        private static readonly int mainThread;
        private static int queued;
        private static int dropped;
        private static ConsoleHistory history;
        // Logs Unity received while scripts reloaded: filed before any live log, so live logs wait in the queue for it.
        private static ConsoleHistory gap;
        private static bool compileErrors;
        private static bool reloading;
        private static int lastVersion = -1;
        private static long lastReport;
        private static double nextLedger;
        private static double nextLedgerSave = 300d;

        static LogCapture()
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            Application.logMessageReceivedThreaded += OnLog;
            try
            {
                Start();
            }
            catch (Exception)
            {
                Store = Store ?? NewStore();
            }

            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += () =>
            {
                UnityConsole.ShowAll.Release();
                LogLedger.Save(Store);
                LogSnapshot.Delete();
            };
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>The session's logs. Main thread only.</summary>
        public static LogStore Store { get; private set; }

        /// <summary>New logs were filed, the store was cleared or history arrived. Raised at most once per editor update.</summary>
        public static event Action Changed;

        /// <summary>The import of Unity's console history in progress, or null.</summary>
        public static ConsoleHistory History => history != null && history.Prepends ? history : null;

        /// <summary>Logs dropped because they arrived faster than they could be filed (over a million, or 64 MB of text, queued).</summary>
        public static int Dropped => dropped;

        public static long Now => DateTime.UtcNow.Ticks;

        private static void Start()
        {
            long now = Now;
            // An editor that crashed mid-import left Unity's console settings borrowed.
            UnityConsole.ShowAll.Recover();
            int unityRows = UnityConsole.Total();
            string anchor = unityRows > 0 ? ConsoleHistory.Anchor(unityRows - 1) : string.Empty;
            var restored = LogSnapshot.TryLoad(out var info, out string problem);
            if (problem != null)
            {
                // One recovery for every abnormal start (no valid head, a head skipped, a save that failed): an empty store
                // and Unity's console read again through the history import, whose progress is saved across reloads. The
                // session files stay until a save of that recovery succeeds. Queued like any log: the one line about it.
                restored = null;
                Debug.LogWarning("[Mixer console] The logs from before the script reload could not be read back (" + problem +
                    "): the Mixer console reads Unity's console again. Logs only the Mixer console had (timings, Git commands) are lost.");
            }

            if (restored != null)
            {
                Attach(restored);
                // Every restored message was noted just before the reload.
                LogLedger.Restored(restored);
                // Scripts only reload once every assembly compiles: earlier compile errors are fixed.
                Store.ResolveCompile(-1, errorsOnly: true);
                Store.AddEvent(now, SessionEventKind.DomainReload);
                if (info.HistoryTo > info.HistoryFrom && info.HistoryFrom >= 0)
                {
                    history = new ConsoleHistory(info.HistoryFrom, info.HistoryTo, ConsoleHistory.Placement.Before, now,
                        (info.HistoryTo - 1, info.HistoryAnchor));
                }

                // Logs Unity received while scripts reloaded, before the Mixer console listened again. Read a slice per
                // editor update like the history (not here: this runs while the editor waits for the reload).
                if (info.UnityRows < unityRows && unityRows - info.UnityRows <= MaxReloadGapRows)
                {
                    gap = new ConsoleHistory(info.UnityRows, unityRows, ConsoleHistory.Placement.After, now,
                        (info.UnityRows - 1, info.UnityAnchor), (unityRows - 1, anchor));
                }
            }
            else
            {
                Attach(NewStore());
                if (unityRows > 0)
                {
                    // Read a slice per editor update, never here: a project opening waits for this constructor.
                    history = new ConsoleHistory(0, unityRows, ConsoleHistory.Placement.Before, now, (unityRows - 1, anchor));
                }
            }

            mirror.Reset(unityRows);
        }

        // Ids from LogStore.HistoryRoom: Unity's console history goes in front without renumbering anything.
        private static LogStore NewStore() => new LogStore(LogStore.HistoryRoom);

        private static void Attach(LogStore store)
        {
            Store = store;
            Store.MaxOccurrences = EditorPrefs.GetInt(MaxLogsPref, LogStore.DefaultMaxOccurrences);
            Store.SetGrouping((GroupingMode)EditorPrefs.GetInt(GroupingPref, (int)GroupingMode.SameText));
            // Compactions are prepared on a worker and land in Update a few milliseconds at a time.
            Store.CompactInBackground = true;
        }

        // Any thread. Must stay fast and never throw or log: no lock, two interlocked counters, one queue write.
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                Enqueue(condition, stackTrace, type, injected: false, timing: null);
            }
            catch (Exception)
            {
                // Never let a log callback fail.
            }
        }

        private static void Enqueue(string condition, string stack, LogType type, bool injected, TimingProfile timing)
        {
            long time = DateTime.UtcNow.Ticks;
            if (Interlocked.Increment(ref queued) > MaxQueued)
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
                return;
            }

            // Cut as the store would file them, so one huge log can't fill the queue's budget.
            if (!queueTexts.Admit(recent ?? (recent = new QueueTexts.Recent()), LogStore.Capped(condition), LogStore.Capped(stack),
                    out var sharedCondition, out var sharedStack))
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
                return;
            }

            queue.Enqueue(new RawLog(time, sharedCondition, sharedStack, type, Thread.CurrentThread.ManagedThreadId != mainThread, injected, timing));
        }

        /// <summary>
        /// Files a log that never goes through Unity's console (another tool's activity, such as Mixer Git's commands).
        /// Any thread.
        /// </summary>
        internal static void Inject(string condition, string stack, LogType type) => Enqueue(condition, stack, type, injected: true, timing: null);

        /// <summary>
        /// Files a timing (never sent to Unity's console) as the log of its text form, with the measured profile kept as
        /// it is (<see cref="LogStore.AddTiming"/>). Any thread.
        /// </summary>
        internal static void Inject(TimingProfile profile, LogType type)
        {
            if (profile != null)
            {
                Enqueue(profile.Message(), profile.Format(), type, injected: true, timing: profile);
            }
        }

        private static void Update()
        {
            if (reloading || Store == null)
            {
                return;
            }

            try
            {
                if (Store.Texts.Faulted)
                {
                    // The session file was damaged after it was read back: never show placeholders as logs.
                    Debug.LogWarning("[Mixer console] Some logs from before the script reload could not be read back: the Mixer console reads Unity's console again. Logs only the Mixer console had (timings, Git commands) are lost.");
                    RebuildFromUnityConsole();
                }

                Drain(6d);
                Store.StepMaintenance();
                // Texts read back lazily (a search over a restored session) add stripped copies while no log arrives.
                Store.KeepWithinTextBudget();
                if (history != null && history.Step(Store, ImportBudget()))
                {
                    if (history.Error != null)
                    {
                        Report(history.Error);
                    }

                    history = null;
                    lastVersion = -1;
                }

                if (history == null && gap == null)
                {
                    // An import that was dropped (cleared, rebuilt) gives Unity's console settings back here.
                    UnityConsole.ShowAll.Release();
                }

                // Console rows are only matched once every queued log is filed: a row whose log still waits in the
                // queue would look like one the Mixer console never received.
                if (queue.IsEmpty && gap == null)
                {
                    mirror.Tick(Store, Now, 2d, OnCompileRow);
                }
                if (Store.Version != lastVersion || history != null)
                {
                    lastVersion = Store.Version;
                    Changed?.Invoke();
                }

                // How long each message has been appearing: new messages now and then, a save every few minutes.
                double time = EditorApplication.timeSinceStartup;
                if (time >= nextLedger && history == null)
                {
                    nextLedger = time + 1d;
                    LogLedger.Track(Store);
                    if (time >= nextLedgerSave)
                    {
                        nextLedgerSave = time + 300d;
                        LogLedger.Save(Store);
                    }
                }
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        // Faster while nobody watches the window load; gentler while the editor is busy otherwise.
        private static double ImportBudget() => EditorApplication.isPlaying ? 4d : LoggerWindow.IsOpen ? 12d : 6d;

        /// <summary>Files queued logs for up to <paramref name="budgetMs"/> milliseconds (all of them when 0).</summary>
        internal static bool Drain(double budgetMs)
        {
            if (Store == null || gap != null && !StepGap(budgetMs))
            {
                return false;
            }

            if (queue.IsEmpty)
            {
                return false;
            }

            var clock = Stopwatch.StartNew();
            int filed = 0;
            while (queue.TryDequeue(out var raw))
            {
                Interlocked.Decrement(ref queued);
                // Released first: a log that fails to file must not keep its charge for ever.
                queueTexts.Release(raw.Condition);
                queueTexts.Release(raw.Stack);
                Ingest(raw);
                filed++;
                if (budgetMs > 0 && (filed & 63) == 0 && clock.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    break;
                }
            }

            return filed > 0;
        }

        // True once the reload's gap is filed (all of it when the budget is 0).
        private static bool StepGap(double budgetMs)
        {
            if (budgetMs > 0d)
            {
                if (!gap.Step(Store, budgetMs))
                {
                    return false;
                }
            }
            else
            {
                while (!gap.Step(Store, 50d))
                {
                }
            }

            if (gap.Error != null)
            {
                Report(gap.Error);
            }

            gap = null;
            return true;
        }

        private static void Ingest(RawLog raw)
        {
            var variant = LogVariants.From(raw.Type);
            string condition = raw.Condition?.Text ?? string.Empty;
            string stack = raw.Stack?.Text ?? string.Empty;
            if (ReloadTimings.IsOwnNotice(condition))
            {
                // Unity's notice that the Mixer console's reload measuring is on, at every reload: not a problem to report.
                return;
            }

            if (!raw.Injected && stack.Length == 0 && (variant == LogVariant.Error || variant == LogVariant.Warning) && IsCompilerMessage(condition, out bool error))
            {
                // Also reported by the compilation pipeline: one copy, which a later compilation replaces.
                if (compileCycle.Add(condition))
                {
                    Store.AddCompile(raw.Time, condition, error, 0, null, 0, 0);
                }

                return;
            }

            if (raw.Timing != null)
            {
                Store.AddTiming(raw.Time, raw.Timing, variant, raw.Background ? OccurrenceFlags.BackgroundThread : OccurrenceFlags.None);
                return;
            }

            int occurrence = Store.Add(raw.Time, condition, stack, variant, raw.Background ? OccurrenceFlags.BackgroundThread : OccurrenceFlags.None);
            if (!raw.Injected)
            {
                // Only logs Unity also shows wait for their console row.
                mirror.Track(Store, occurrence, condition, LogVariants.Level(variant));
            }
        }

        internal static bool IsCompilerMessage(string condition, out bool error)
        {
            error = condition.IndexOf("): error ", StringComparison.Ordinal) > 0;
            bool warning = !error && condition.IndexOf("): warning ", StringComparison.Ordinal) > 0;
            if (!error && !warning)
            {
                return false;
            }

            return StackTraces.TryParseCompilerLocation(condition, out _, out _, out _);
        }

        // A compiler message in Unity's console the Mixer console doesn't know (logged before it listened): keep it.
        private static void OnCompileRow(UnityConsoleRow row, LogVariant variant)
        {
            string text = row.Condition;
            if (compileCycle.Contains(text) || Store.HasActiveCompileMessage(text, out _))
            {
                return;
            }

            compileCycle.Add(text);
            Store.AddCompile(Now, text, variant == LogVariant.CompileError, 0, row.File, row.Line, row.Column);
        }

        private static void OnCompilationStarted(object context)
        {
            try
            {
                Drain(0d);
                compileCycle.Clear();
                compileErrors = false;
                if (UnityConsole.HasFlag(UnityConsole.FlagClearOnRecompile))
                {
                    Clear(alsoUnityConsole: false);
                }
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            try
            {
                Drain(0d);
                long now = Now;
                int assembly = Store.AssemblyId(Path.GetFileNameWithoutExtension(assemblyPath));
                // This assembly's earlier messages are replaced by what this compilation reports.
                Store.ResolveCompile(assembly, errorsOnly: false);
                foreach (var message in messages ?? new CompilerMessage[0])
                {
                    string text = message.message ?? string.Empty;
                    bool error = message.type == CompilerMessageType.Error;
                    compileErrors |= error;
                    if (!compileCycle.Add(text))
                    {
                        if (Store.HasActiveCompileMessage(text, out int existing) && Store.Message(existing).Assembly == 0)
                        {
                            Store.SetAssembly(existing, assembly);
                        }

                        continue;
                    }

                    Store.AddCompile(now, text, error, assembly, message.file, message.line, message.column);
                }
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        private static void OnCompilationFinished(object context)
        {
            try
            {
                Store.AddEvent(Now, compileErrors ? SessionEventKind.CompileFailed : SessionEventKind.Compile);
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            try
            {
                if (change == PlayModeStateChange.ExitingEditMode)
                {
                    Drain(0d);
                    if (UnityConsole.HasFlag(UnityConsole.FlagClearOnPlay))
                    {
                        // Unity clears its own console as Play Mode starts.
                        Clear(alsoUnityConsole: false);
                    }

                    Store.AddEvent(Now, SessionEventKind.EnterPlayMode);
                }
                else if (change == PlayModeStateChange.ExitingPlayMode)
                {
                    Drain(0d);
                    Store.AddEvent(Now, SessionEventKind.ExitPlayMode);
                }
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        internal static void OnBuild()
        {
            try
            {
                Drain(0d);
                if (UnityConsole.HasFlag(UnityConsole.FlagClearOnBuild))
                {
                    Clear(alsoUnityConsole: false);
                }

                Store.AddEvent(Now, SessionEventKind.Build);
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        private static void BeforeReload()
        {
            reloading = true;
            try
            {
                Drain(0d);
                // A compaction being prepared ends with this domain: the store is saved as it is.
                Store.StopMaintenance();
                int unityRows = UnityConsole.Total();
                var info = new SnapshotInfo
                {
                    UnityRows = unityRows,
                    UnityAnchor = unityRows > 0 ? ConsoleHistory.Anchor(unityRows - 1) : string.Empty
                };
                if (history != null && history.Prepends && !history.Finished)
                {
                    info.HistoryFrom = history.From;
                    info.HistoryTo = history.To;
                    info.HistoryAnchor = history.EndAnchor;
                }

                LogSnapshot.Save(Store, info);
                LogLedger.Save(Store);
            }
            catch (Exception)
            {
                // The last committed session stays: the next domain reads it, then Unity's console for the rest, and says so.
                LogSnapshot.SaveFailed();
            }
            finally
            {
                // The next domain borrows them again if the import goes on.
                UnityConsole.ShowAll.Release();
            }
        }

        /// <summary>Empties the Mixer console (and Unity's console). Compile errors still standing stay.</summary>
        public static void Clear(bool alsoUnityConsole = true)
        {
            Drain(0d);
            history = null;
            Store.Clear(Now);
            if (alsoUnityConsole)
            {
                UnityConsole.Clear();
            }

            mirror.Reset(UnityConsole.Total());
            lastVersion = -1;
            Changed?.Invoke();
        }

        /// <summary>Starts over from what Unity's console holds (times to the second).</summary>
        public static void RebuildFromUnityConsole()
        {
            Drain(0d);
            var store = NewStore();
            Attach(store);
            long now = Now;
            int unityRows = UnityConsole.Total();
            history = unityRows > 0
                ? new ConsoleHistory(0, unityRows, ConsoleHistory.Placement.Before, now, (unityRows - 1, ConsoleHistory.Anchor(unityRows - 1)))
                : null;
            mirror.Reset(unityRows);
            lastVersion = -1;
            Changed?.Invoke();
        }

        public static void SetMaxLogs(int value)
        {
            EditorPrefs.SetInt(MaxLogsPref, value);
            Store.MaxOccurrences = value;
            if (Store.Count > Store.MaxOccurrences)
            {
                Store.Trim(Store.MaxOccurrences * 4 / 5);
            }

            Changed?.Invoke();
        }

        public static void SetGrouping(GroupingMode mode)
        {
            EditorPrefs.SetInt(GroupingPref, (int)mode);
            Store.SetGrouping(mode);
            Changed?.Invoke();
        }

        // At most one report every ten seconds, so a failure can't flood the log it is captured in.
        private static void Report(Exception exception)
        {
            long now = Now;
            if (now - lastReport < TimeSpan.TicksPerSecond * 10)
            {
                return;
            }

            lastReport = now;
            Debug.LogWarning("[Mixer console] " + exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
        }
    }

    internal sealed class LoggerBuildHook : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report) => LogCapture.OnBuild();
    }
}
