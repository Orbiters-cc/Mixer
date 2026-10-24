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
        private const int SynchronousHistoryRows = 20_000;
        private const int MaxReloadGapRows = 50_000;
        internal const string MaxLogsPref = "Orbiters.Logger.MaxLogs";
        internal const string GroupingPref = "Orbiters.Logger.Grouping";

        private readonly struct RawLog
        {
            public readonly long Time;
            public readonly string Condition;
            public readonly string Stack;
            public readonly LogType Type;
            public readonly bool Background;
            public readonly bool Injected;

            public RawLog(long time, string condition, string stack, LogType type, bool background, bool injected = false)
            {
                Time = time;
                Condition = condition;
                Stack = stack;
                Type = type;
                Background = background;
                Injected = injected;
            }
        }

        private static readonly ConcurrentQueue<RawLog> queue = new ConcurrentQueue<RawLog>();
        private static readonly ConsoleMirror mirror = new ConsoleMirror();
        private static readonly HashSet<string> compileCycle = new HashSet<string>(StringComparer.Ordinal);
        private static readonly int mainThread;
        private static int queued;
        private static int dropped;
        private static ConsoleHistory history;
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
                Store = Store ?? new LogStore();
            }

            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += () =>
            {
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

        /// <summary>Logs dropped because they arrived faster than they could be filed (over a million queued).</summary>
        public static int Dropped => dropped;

        public static long Now => DateTime.UtcNow.Ticks;

        private static void Start()
        {
            long now = Now;
            int unityRows = UnityConsole.Total();
            string anchor = unityRows > 0 ? ConsoleHistory.Anchor(unityRows - 1) : string.Empty;
            var restored = LogSnapshot.TryLoad(out var info);
            if (restored != null)
            {
                Attach(restored);
                // Scripts only reload once every assembly compiles: earlier compile errors are fixed.
                Store.ResolveCompile(-1, errorsOnly: true);
                Store.AddEvent(now, SessionEventKind.DomainReload);
                if (info.HistoryTo > info.HistoryFrom && info.HistoryFrom >= 0)
                {
                    history = new ConsoleHistory(info.HistoryFrom, info.HistoryTo, ConsoleHistory.Placement.Before, now,
                        (info.HistoryTo - 1, info.HistoryAnchor));
                }

                // Logs Unity received while scripts reloaded, before the Mixer console listened again.
                if (info.UnityRows <= unityRows && unityRows - info.UnityRows <= MaxReloadGapRows)
                {
                    var gap = new ConsoleHistory(info.UnityRows, unityRows, ConsoleHistory.Placement.After, now,
                        (info.UnityRows - 1, info.UnityAnchor), (unityRows - 1, anchor));
                    while (!gap.Step(Store, 50))
                    {
                    }
                }
            }
            else
            {
                Attach(new LogStore());
                if (unityRows > 0)
                {
                    history = new ConsoleHistory(0, unityRows, ConsoleHistory.Placement.Before, now, (unityRows - 1, anchor));
                    if (unityRows <= SynchronousHistoryRows)
                    {
                        while (!history.Step(Store, 50))
                        {
                        }

                        history = null;
                    }
                }
            }

            mirror.Reset(unityRows);
        }

        private static void Attach(LogStore store)
        {
            Store = store;
            Store.MaxOccurrences = EditorPrefs.GetInt(MaxLogsPref, LogStore.DefaultMaxOccurrences);
            Store.SetGrouping((GroupingMode)EditorPrefs.GetInt(GroupingPref, (int)GroupingMode.SameText));
            Store.Shifted += mirror.Shift;
        }

        // Any thread. Must stay fast and never throw or log.
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (Interlocked.Increment(ref queued) > MaxQueued)
                {
                    Interlocked.Decrement(ref queued);
                    Interlocked.Increment(ref dropped);
                    return;
                }

                queue.Enqueue(new RawLog(DateTime.UtcNow.Ticks, condition, stackTrace, type, Thread.CurrentThread.ManagedThreadId != mainThread));
            }
            catch (Exception)
            {
                // Never let a log callback fail.
            }
        }

        /// <summary>
        /// Files a log that never goes through Unity's console (another tool's activity, such as Mixer Git's commands).
        /// Any thread.
        /// </summary>
        internal static void Inject(string condition, string stack, LogType type)
        {
            if (Interlocked.Increment(ref queued) > MaxQueued)
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
                return;
            }

            queue.Enqueue(new RawLog(DateTime.UtcNow.Ticks, condition, stack, type, Thread.CurrentThread.ManagedThreadId != mainThread, injected: true));
        }

        private static void Update()
        {
            if (reloading || Store == null)
            {
                return;
            }

            try
            {
                Drain(6d);
                if (history != null && history.Step(Store, ImportBudget()))
                {
                    history = null;
                    lastVersion = -1;
                }

                // Console rows are only matched once every queued log is filed: a row whose log still waits in the
                // queue would look like one the Mixer console never received.
                if (queue.IsEmpty)
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
            if (queue.IsEmpty || Store == null)
            {
                return false;
            }

            var clock = Stopwatch.StartNew();
            int filed = 0;
            while (queue.TryDequeue(out var raw))
            {
                Interlocked.Decrement(ref queued);
                Ingest(raw);
                filed++;
                if (budgetMs > 0 && (filed & 63) == 0 && clock.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    break;
                }
            }

            return filed > 0;
        }

        private static void Ingest(RawLog raw)
        {
            var variant = LogVariants.From(raw.Type);
            string condition = raw.Condition ?? string.Empty;
            string stack = raw.Stack ?? string.Empty;
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

            int occurrence = Store.Add(raw.Time, condition, stack, variant, raw.Background ? OccurrenceFlags.BackgroundThread : OccurrenceFlags.None);
            if (!raw.Injected)
            {
                // Only logs Unity also shows wait for their console row.
                mirror.Track(occurrence, condition, LogVariants.Level(variant));
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
                            Store.Message(existing).Assembly = assembly;
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
                // Without a snapshot the next domain reads Unity's console instead.
                LogSnapshot.Delete();
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
            var store = new LogStore();
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
