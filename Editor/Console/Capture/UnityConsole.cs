using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>One entry of Unity's console, as <c>UnityEditor.LogEntries</c> keeps it.</summary>
    internal struct UnityConsoleRow
    {
        public string Message;
        public string File;
        public int Line;
        public int Column;
        public int Mode;
        public int InstanceId;
        public int CallstackStart;

        /// <summary>The message without its stack trace.</summary>
        public string Condition
        {
            get
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return string.Empty;
                }

                int end = CallstackStart > 0 && CallstackStart <= Message.Length ? CallstackStart : Message.Length;
                while (end > 0 && (Message[end - 1] == '\n' || Message[end - 1] == '\r'))
                {
                    end--;
                }

                return end == Message.Length ? Message : Message.Substring(0, end);
            }
        }

        public string Stack => CallstackStart > 0 && CallstackStart < Message.Length ? Message.Substring(CallstackStart) : string.Empty;
    }

    /// <summary>
    /// Unity's console store (<c>UnityEditor.LogEntries</c>, internal), reached through compiled delegates so reading
    /// rows costs about as much as Unity's own console does. Every member degrades to "unavailable" if a future Unity
    /// changes the API; the live capture keeps working without it.
    /// </summary>
    internal static class UnityConsole
    {
        // ConsoleWindow.Mode
        public const int ModeError = 1 << 0;
        public const int ModeAssert = 1 << 1;
        public const int ModeFatal = 1 << 4;
        public const int ModeAssetImportError = 1 << 6;
        public const int ModeAssetImportWarning = 1 << 7;
        public const int ModeScriptingError = 1 << 8;
        public const int ModeScriptingWarning = 1 << 9;
        public const int ModeScriptCompileError = 1 << 11;
        public const int ModeScriptCompileWarning = 1 << 12;
        public const int ModeStickyError = 1 << 13;
        public const int ModeScriptingException = 1 << 17;
        public const int ModeGraphCompileError = 1 << 20;
        public const int ModeScriptingAssertion = 1 << 21;

        // ConsoleWindow.ConsoleFlags
        public const int FlagCollapse = 1 << 0;
        public const int FlagClearOnPlay = 1 << 1;
        public const int FlagErrorPause = 1 << 2;
        public const int FlagLogLevelLog = 1 << 7;
        public const int FlagLogLevelWarning = 1 << 8;
        public const int FlagLogLevelError = 1 << 9;
        public const int FlagShowTimestamp = 1 << 10;
        public const int FlagClearOnBuild = 1 << 11;
        public const int FlagClearOnRecompile = 1 << 12;
        public const int AllLevels = FlagLogLevelLog | FlagLogLevelWarning | FlagLogLevelError;

        private delegate void CountsDelegate(ref int errors, ref int warnings, ref int logs);
        private delegate void LinesDelegate(int row, int lines, ref int mode, ref string text);

        private static readonly Func<int> startGettingEntries;
        private static readonly Action endGettingEntries;
        private static readonly Func<int, object, bool> getEntry;
        private static readonly LinesDelegate getLines;
        private static readonly CountsDelegate getCounts;
        private static readonly Func<int> getFlags;
        private static readonly Action<int, bool> setFlag;
        private static readonly Func<string> getFilteringText;
        private static readonly Action<string> setFilteringText;
        private static readonly Action clear;
        private static readonly Func<object> newEntry;
        private static readonly Func<object, string> entryMessage;
        private static readonly Func<object, string> entryFile;
        private static readonly Func<object, int> entryLine;
        private static readonly Func<object, int> entryColumn;
        private static readonly Func<object, int> entryMode;
        private static readonly Func<object, int> entryInstanceId;
        private static readonly Func<object, int> entryCallstackStart;

        static UnityConsole()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                var entries = assembly.GetType("UnityEditor.LogEntries");
                var entryType = assembly.GetType("UnityEditor.LogEntry");
                if (entries == null || entryType == null)
                {
                    return;
                }

                const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                startGettingEntries = Static0<int>(entries.GetMethod("StartGettingEntries", Static));
                endGettingEntries = Expression.Lambda<Action>(Expression.Call(entries.GetMethod("EndGettingEntries", Static))).Compile();

                var row = Expression.Parameter(typeof(int), "row");
                var boxed = Expression.Parameter(typeof(object), "entry");
                getEntry = Expression.Lambda<Func<int, object, bool>>(
                    Expression.Call(entries.GetMethod("GetEntryInternal", Static), row, Expression.Convert(boxed, entryType)), row, boxed).Compile();

                var lines = Expression.Parameter(typeof(int), "lines");
                var mode = Expression.Parameter(typeof(int).MakeByRefType(), "mode");
                var text = Expression.Parameter(typeof(string).MakeByRefType(), "text");
                getLines = Expression.Lambda<LinesDelegate>(
                    Expression.Call(entries.GetMethod("GetLinesAndModeFromEntryInternal", Static), row, lines, mode, text), row, lines, mode, text).Compile();

                var errors = Expression.Parameter(typeof(int).MakeByRefType(), "errors");
                var warnings = Expression.Parameter(typeof(int).MakeByRefType(), "warnings");
                var logs = Expression.Parameter(typeof(int).MakeByRefType(), "logs");
                getCounts = Expression.Lambda<CountsDelegate>(
                    Expression.Call(entries.GetMethod("GetCountsByType", Static), errors, warnings, logs), errors, warnings, logs).Compile();

                var flagsProperty = entries.GetProperty("consoleFlags", Static);
                getFlags = Expression.Lambda<Func<int>>(Expression.Property(null, flagsProperty)).Compile();
                var bit = Expression.Parameter(typeof(int), "bit");
                var value = Expression.Parameter(typeof(bool), "value");
                setFlag = Expression.Lambda<Action<int, bool>>(Expression.Call(entries.GetMethod("SetConsoleFlag", Static), bit, value), bit, value).Compile();
                getFilteringText = Static0<string>(entries.GetMethod("GetFilteringText", Static));
                var filter = Expression.Parameter(typeof(string), "filter");
                setFilteringText = Expression.Lambda<Action<string>>(Expression.Call(entries.GetMethod("SetFilteringText", Static), filter), filter).Compile();
                clear = Expression.Lambda<Action>(Expression.Call(entries.GetMethod("Clear", Static))).Compile();

                newEntry = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(entryType), typeof(object))).Compile();
                entryMessage = Field<string>(entryType, "message");
                entryFile = Field<string>(entryType, "file");
                entryLine = Field<int>(entryType, "line");
                entryColumn = Field<int>(entryType, "column");
                entryMode = Field<int>(entryType, "mode");
                entryInstanceId = Field<int>(entryType, "instanceID");
                // Unity 2022.2+: where the stack trace starts in the message.
                entryCallstackStart = entryType.GetField("callstackTextStartUTF16", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                    ? Field<int>(entryType, "callstackTextStartUTF16")
                    : null;
                Available = true;
            }
            catch (Exception)
            {
                Available = false;
            }
        }

        /// <summary>False when this Unity version's console can't be read: history import and enrichment are skipped.</summary>
        public static bool Available { get; }

        private static Func<T> Static0<T>(MethodInfo method) => Expression.Lambda<Func<T>>(Expression.Call(method)).Compile();

        private static Func<object, T> Field<T>(Type type, string name)
        {
            var boxed = Expression.Parameter(typeof(object), "entry");
            return Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(boxed, type), name), boxed).Compile();
        }

        public static int Flags
        {
            get
            {
                try
                {
                    return Available ? getFlags() : AllLevels;
                }
                catch (Exception)
                {
                    return AllLevels;
                }
            }
        }

        public static bool HasFlag(int flag) => (Flags & flag) != 0;

        public static void SetFlag(int flag, bool on)
        {
            if (!Available)
            {
                return;
            }

            try
            {
                setFlag(flag, on);
            }
            catch (Exception)
            {
                // Unity changed the API: leave its console as it is.
            }
        }

        /// <summary>Rows are every entry in order only when Unity's console shows everything (no collapse, levels or search).</summary>
        public static bool RowsAreComplete
        {
            get
            {
                if (!Available)
                {
                    return false;
                }

                int flags = Flags;
                if ((flags & FlagCollapse) != 0 || (flags & AllLevels) != AllLevels)
                {
                    return false;
                }

                try
                {
                    return string.IsNullOrEmpty(getFilteringText());
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// While alive, Unity's console lists every entry in order with timestamps (collapse, level filters and search
        /// off), then its settings come back. Nothing repaints in between, so the Console window doesn't flicker.
        /// </summary>
        public sealed class ShowAllScope : IDisposable
        {
            private readonly int savedFlags;
            private readonly string savedFilter;
            private readonly bool changedFlags;
            private readonly bool changedFilter;

            public ShowAllScope(bool timestamps)
            {
                if (!Available)
                {
                    return;
                }

                try
                {
                    savedFlags = getFlags();
                    savedFilter = getFilteringText() ?? string.Empty;
                    int wanted = (savedFlags & ~FlagCollapse) | AllLevels | (timestamps ? FlagShowTimestamp : 0);
                    if (wanted != savedFlags)
                    {
                        changedFlags = true;
                        Apply(savedFlags, wanted);
                    }

                    if (savedFilter.Length > 0)
                    {
                        changedFilter = true;
                        setFilteringText(string.Empty);
                    }
                }
                catch (Exception)
                {
                    changedFlags = false;
                    changedFilter = false;
                }
            }

            /// <summary>True when the scope had to change Unity's console settings (each change re-filters its entries).</summary>
            public bool Changed => changedFlags || changedFilter;

            public void Dispose()
            {
                try
                {
                    if (changedFilter)
                    {
                        setFilteringText(savedFilter);
                    }

                    if (changedFlags)
                    {
                        Apply(getFlags(), savedFlags);
                    }
                }
                catch (Exception)
                {
                    // Leave the console as it is.
                }
            }

            private static void Apply(int from, int to)
            {
                foreach (int flag in new[] { FlagCollapse, FlagLogLevelLog, FlagLogLevelWarning, FlagLogLevelError, FlagShowTimestamp })
                {
                    if ((from & flag) != (to & flag))
                    {
                        setFlag(flag, (to & flag) != 0);
                    }
                }
            }
        }

        /// <summary>Entries in Unity's console, whatever its filters show.</summary>
        public static int Total()
        {
            if (!Available)
            {
                return 0;
            }

            try
            {
                int errors = 0, warnings = 0, logs = 0;
                getCounts(ref errors, ref warnings, ref logs);
                return errors + warnings + logs;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static void Clear()
        {
            if (!Available)
            {
                return;
            }

            try
            {
                clear();
            }
            catch (Exception)
            {
                // Nothing to clear with.
            }
        }

        public static LogVariant VariantOf(int mode)
        {
            if ((mode & ModeScriptCompileError) != 0)
            {
                return LogVariant.CompileError;
            }

            if ((mode & ModeScriptCompileWarning) != 0)
            {
                return LogVariant.CompileWarning;
            }

            if ((mode & ModeScriptingException) != 0)
            {
                return LogVariant.Exception;
            }

            if ((mode & (ModeAssert | ModeScriptingAssertion)) != 0)
            {
                return LogVariant.Assert;
            }

            if ((mode & (ModeError | ModeFatal | ModeAssetImportError | ModeScriptingError | ModeStickyError | ModeGraphCompileError)) != 0)
            {
                return LogVariant.Error;
            }

            if ((mode & (ModeAssetImportWarning | ModeScriptingWarning)) != 0)
            {
                return LogVariant.Warning;
            }

            return LogVariant.Log;
        }

        /// <summary>
        /// Reads rows between <see cref="Open"/> and <see cref="Dispose"/>, like Unity's console window does each repaint.
        /// </summary>
        public sealed class Reader : IDisposable
        {
            private readonly object entry;
            private bool open;

            private Reader()
            {
                entry = newEntry();
            }

            /// <summary>Rows Unity's console shows right now (all entries when <see cref="RowsAreComplete"/>).</summary>
            public int Count { get; private set; }

            public static Reader Open()
            {
                if (!Available)
                {
                    return null;
                }

                try
                {
                    var reader = new Reader();
                    reader.Count = startGettingEntries();
                    reader.open = true;
                    return reader;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>The first <paramref name="lines"/> lines of a row as the console shows them (with "[hh:mm:ss] " when timestamps are on).</summary>
            public string Lines(int row, int lines, out int mode)
            {
                mode = 0;
                string text = string.Empty;
                getLines(row, lines, ref mode, ref text);
                return text ?? string.Empty;
            }

            public bool TryRead(int row, out UnityConsoleRow result)
            {
                result = default;
                if (row < 0 || row >= Count || !getEntry(row, entry))
                {
                    return false;
                }

                result.Message = entryMessage(entry) ?? string.Empty;
                result.File = entryFile(entry);
                result.Line = entryLine(entry);
                result.Column = entryColumn(entry);
                result.Mode = entryMode(entry);
                result.InstanceId = entryInstanceId(entry);
                result.CallstackStart = entryCallstackStart != null ? entryCallstackStart(entry) : -1;
                if (result.CallstackStart < 0)
                {
                    result.CallstackStart = GuessCallstackStart(result.Message);
                }

                return true;
            }

            public void Dispose()
            {
                if (!open)
                {
                    return;
                }

                open = false;
                try
                {
                    endGettingEntries();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }
        }

        // Older Unity: the stack trace starts at the first line that looks like a frame.
        private static int GuessCallstackStart(string message)
        {
            int index = 0;
            while (index < message.Length)
            {
                int end = message.IndexOf('\n', index);
                if (end < 0)
                {
                    break;
                }

                int next = end + 1;
                if (next < message.Length)
                {
                    int lineEnd = message.IndexOf('\n', next);
                    string line = lineEnd < 0 ? message.Substring(next) : message.Substring(next, lineEnd - next);
                    if (line.Contains(" (at ") || line.StartsWith("UnityEngine.", StringComparison.Ordinal) || line.Contains(":") && line.Contains(" ("))
                    {
                        return next;
                    }
                }

                index = next;
            }

            return message.Length;
        }

        /// <summary>"[21:50:43] text" → seconds since midnight and the text, when Unity's console shows timestamps.</summary>
        public static bool TrySplitTimestamp(string line, out int secondsOfDay, out string text)
        {
            secondsOfDay = -1;
            text = line ?? string.Empty;
            if (line == null || line.Length < 11 || line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']')
            {
                return false;
            }

            int Digit(int i) => line[i] - '0';
            for (int i = 1; i < 9; i++)
            {
                if (i != 3 && i != 6 && (line[i] < '0' || line[i] > '9'))
                {
                    return false;
                }
            }

            secondsOfDay = (Digit(1) * 10 + Digit(2)) * 3600 + (Digit(4) * 10 + Digit(5)) * 60 + Digit(7) * 10 + Digit(8);
            text = line.Length > 10 && line[10] == ' ' ? line.Substring(11) : line.Substring(10);
            return true;
        }
    }
}
