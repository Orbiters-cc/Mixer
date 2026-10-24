using System;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>The three severities the window filters by.</summary>
    internal enum LogLevel : byte
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>What a message is, finer than its level: the Unity log type, or a compiler message.</summary>
    internal enum LogVariant : byte
    {
        Log,
        Warning,
        Error,
        Exception,
        Assert,
        CompileError,
        CompileWarning
    }

    [Flags]
    internal enum OccurrenceFlags : byte
    {
        None = 0,
        /// <summary>The time was read from Unity's console (to the second) or estimated, not taken when the log arrived.</summary>
        ApproximateTime = 1,
        /// <summary>Read back from Unity's console: logged before the Mixer console listened, or a log that never reached it live.</summary>
        FromUnityConsole = 2,
        /// <summary>Logged from a thread other than Unity's main thread.</summary>
        BackgroundThread = 4
    }

    [Flags]
    internal enum MessageFlags : byte
    {
        None = 0,
        /// <summary>A compiler message (error or warning).</summary>
        Compile = 1,
        /// <summary>A compile error fixed by a later compilation: hidden, as in Unity's console.</summary>
        Resolved = 2
    }

    /// <summary>How messages are gathered into groups.</summary>
    internal enum GroupingMode : byte
    {
        /// <summary>Messages with exactly the same text.</summary>
        SameText = 0,
        /// <summary>Messages whose text only differs by numbers, ids or hashes.</summary>
        SimilarText = 1
    }

    internal static class LogVariants
    {
        public static LogLevel Level(LogVariant variant)
        {
            switch (variant)
            {
                case LogVariant.Log:
                    return LogLevel.Info;
                case LogVariant.Warning:
                case LogVariant.CompileWarning:
                    return LogLevel.Warning;
                default:
                    return LogLevel.Error;
            }
        }

        public static LogVariant From(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return LogVariant.Warning;
                case LogType.Error:
                    return LogVariant.Error;
                case LogType.Exception:
                    return LogVariant.Exception;
                case LogType.Assert:
                    return LogVariant.Assert;
                default:
                    return LogVariant.Log;
            }
        }

        public static bool IsCompile(LogVariant variant) => variant == LogVariant.CompileError || variant == LogVariant.CompileWarning;

        public static string Label(LogVariant variant)
        {
            switch (variant)
            {
                case LogVariant.Warning:
                    return "Warning";
                case LogVariant.Error:
                    return "Error";
                case LogVariant.Exception:
                    return "Exception";
                case LogVariant.Assert:
                    return "Assertion";
                case LogVariant.CompileError:
                    return "Compile error";
                case LogVariant.CompileWarning:
                    return "Compile warning";
                default:
                    return "Log";
            }
        }

        public static string Label(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Warning:
                    return "Warning";
                case LogLevel.Error:
                    return "Error";
                default:
                    return "Log";
            }
        }
    }

    /// <summary>Markers drawn on the timeline: what the editor was doing when logs came in.</summary>
    internal enum SessionEventKind : byte
    {
        EnterPlayMode,
        ExitPlayMode,
        Compile,
        CompileFailed,
        DomainReload,
        Build
    }

    internal readonly struct SessionEvent
    {
        public readonly long Time;
        public readonly SessionEventKind Kind;

        public SessionEvent(long time, SessionEventKind kind)
        {
            Time = time;
            Kind = kind;
        }

        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case SessionEventKind.EnterPlayMode:
                        return "Entered Play Mode";
                    case SessionEventKind.ExitPlayMode:
                        return "Left Play Mode";
                    case SessionEventKind.Compile:
                        return "Scripts compiled";
                    case SessionEventKind.CompileFailed:
                        return "Compilation failed";
                    case SessionEventKind.DomainReload:
                        return "Scripts reloaded";
                    default:
                        return "Build";
                }
            }
        }
    }
}
