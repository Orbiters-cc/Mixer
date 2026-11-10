using System;
using System.Collections.Generic;
using System.IO;

namespace Orbiters.Logger.Editor
{
    internal enum StackFrameKind : byte
    {
        /// <summary>Code with a source file: what a click opens.</summary>
        User,
        /// <summary>UnityEngine.Debug and logger wrappers: how the message was logged, not where.</summary>
        Logging,
        /// <summary>Engine or framework code without a source file.</summary>
        Internal,
        /// <summary>Not a frame: "Rethrow as …", "--- End of inner exception stack trace ---".</summary>
        Note
    }

    internal readonly struct StackFrame
    {
        public readonly string Text;
        public readonly string Method;
        public readonly string File;
        public readonly int Line;
        public readonly StackFrameKind Kind;

        public StackFrame(string text, string method, string file, int line, StackFrameKind kind)
        {
            Text = text;
            Method = method;
            File = file;
            Line = line;
            Kind = kind;
        }

        public bool HasFile => !string.IsNullOrEmpty(File);
    }

    /// <summary>
    /// Reads Unity stack traces: <c>Class:Method (args) (at Assets/File.cs:12)</c> for logs,
    /// <c>Class.Method (args) [0x00000] in File.cs:12</c> for exceptions.
    /// </summary>
    internal static class StackTraces
    {
        private static readonly string[] LoggingPrefixes =
        {
            "UnityEngine.Debug:", "UnityEngine.Debug.", "UnityEngine.Logger:", "UnityEngine.Logger.",
            "UnityEngine.DebugLogHandler:", "UnityEngine.DebugLogHandler.", "UnityEngine.Assertions.Assert:", "UnityEngine.Assertions.Assert.",
            "UnityEngine.Application:CallLogCallback", "UnityEngine.Debug:CallOverridenDebugHandler"
        };

        private static readonly string[] InternalPrefixes =
        {
            "UnityEngine.", "UnityEditor.", "UnityEditorInternal.", "Unity.", "System.", "Mono.", "Microsoft.", "(wrapper"
        };

        public static List<StackFrame> Parse(string stack)
        {
            var frames = new List<StackFrame>();
            if (string.IsNullOrEmpty(stack))
            {
                return frames;
            }

            int start = 0;
            while (start < stack.Length)
            {
                int end = stack.IndexOf('\n', start);
                if (end < 0)
                {
                    end = stack.Length;
                }

                int length = end - start;
                if (length > 0 && stack[end - 1] == '\r')
                {
                    length--;
                }

                if (length > 0)
                {
                    string line = stack.Substring(start, length).Trim();
                    if (line.Length > 0)
                    {
                        frames.Add(ParseFrame(line));
                    }
                }

                start = end + 1;
            }

            return frames;
        }

        public static StackFrame ParseFrame(string line)
        {
            if (line.StartsWith("Rethrow as ", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                return new StackFrame(line, line, null, 0, StackFrameKind.Note);
            }

            string method = line;
            string file = null;
            int lineNumber = 0;

            // "(at path:line)" at the end, path may contain spaces and parentheses.
            int at = line.LastIndexOf(" (at ", StringComparison.Ordinal);
            if (at >= 0 && line.EndsWith(")", StringComparison.Ordinal))
            {
                method = line.Substring(0, at);
                string location = line.Substring(at + 5, line.Length - at - 6);
                SplitLocation(location, out file, out lineNumber);
            }
            else
            {
                // Mono exception frames: "Method (args) [0x00012] in C:\path\File.cs:42" or "in <hash>:0".
                int inIndex = line.LastIndexOf(" in ", StringComparison.Ordinal);
                if (inIndex > 0 && line.IndexOf('(') >= 0 && line.IndexOf('(') < inIndex)
                {
                    string location = line.Substring(inIndex + 4);
                    method = line.Substring(0, inIndex);
                    int bracket = method.LastIndexOf(" [0x", StringComparison.Ordinal);
                    if (bracket > 0)
                    {
                        method = method.Substring(0, bracket);
                    }

                    SplitLocation(location, out file, out lineNumber);
                }
            }

            StackFrameKind kind;
            if (IsLogging(method))
            {
                kind = StackFrameKind.Logging;
            }
            else if (!string.IsNullOrEmpty(file))
            {
                kind = StackFrameKind.User;
            }
            else
            {
                kind = IsNote(line) ? StackFrameKind.Note : StackFrameKind.Internal;
            }

            return new StackFrame(line, method, file, lineNumber, kind);
        }

        private static bool IsNote(string line) => line.IndexOf('(') < 0 && line.IndexOf(':') < 0;

        private static void SplitLocation(string location, out string file, out int line)
        {
            file = null;
            line = 0;
            if (string.IsNullOrEmpty(location) || location[0] == '<')
            {
                return;
            }

            int colon = location.LastIndexOf(':');
            if (colon <= 0)
            {
                return;
            }

            string number = location.Substring(colon + 1);
            if (!int.TryParse(number, out line))
            {
                line = 0;
                return;
            }

            file = location.Substring(0, colon);
            if (file.Length == 0 || file[0] == '<')
            {
                file = null;
                line = 0;
            }
        }

        public static bool IsLogging(string method)
        {
            // Harmony and other patchers wrap Debug.Log: "(wrapper dynamic-method) UnityEngine.Debug:UnityEngine.Debug.LogError_Patch1 (object)".
            if (method.StartsWith("(wrapper ", StringComparison.Ordinal))
            {
                int close = method.IndexOf(") ", StringComparison.Ordinal);
                if (close > 0)
                {
                    method = method.Substring(close + 2);
                }
            }

            foreach (string prefix in LoggingPrefixes)
            {
                if (method.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsEngine(string method)
        {
            foreach (string prefix in InternalPrefixes)
            {
                if (method.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The frame that logged the message: the first frame after UnityEngine.Debug and the project's own logger
        /// wrappers (<c>MCBLogger:Log</c>, <c>McpLog:Info</c>…), preferring one with a source file. -1 when none.
        /// </summary>
        public static int FindCallsite(List<StackFrame> frames)
        {
            int first = -1;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Kind == StackFrameKind.Logging || frames[i].Kind == StackFrameKind.Note)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                if (!frames[i].HasFile)
                {
                    continue;
                }

                // A wrapper such as MyLogger.Info: keep going while a later frame has a file too.
                if (IsLoggerWrapper(frames[i].Method) && HasLaterUserFrame(frames, i))
                {
                    continue;
                }

                return i;
            }

            return first;
        }

        private static bool HasLaterUserFrame(List<StackFrame> frames, int index)
        {
            for (int i = index + 1; i < frames.Count; i++)
            {
                if (frames[i].Kind == StackFrameKind.User && !IsLoggerWrapper(frames[i].Method))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A method of a type named like a logger (<c>MCBLogger</c>, <c>McpLog</c>, <c>DebugUtil</c>…).</summary>
        public static bool IsLoggerWrapper(string method)
        {
            string type = TypeName(method);
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            return type.EndsWith("Log", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("Logger", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("Logging", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("Logs", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("Debug", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("DebugUtil", StringComparison.OrdinalIgnoreCase) ||
                   type.EndsWith("Console", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"Namespace.Type:Method (args)" or "Namespace.Type.Method (args)" → "Namespace.Type".</summary>
        public static string QualifiedType(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return string.Empty;
            }

            int paren = method.IndexOf(" (", StringComparison.Ordinal);
            string name = paren > 0 ? method.Substring(0, paren) : method;
            int colon = name.LastIndexOf(':');
            if (colon > 0)
            {
                return name.Substring(0, colon);
            }

            int generic = name.IndexOf('[');
            if (generic > 0)
            {
                name = name.Substring(0, generic);
            }

            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        public static string TypeName(string method)
        {
            string qualified = QualifiedType(method);
            int nested = qualified.LastIndexOf('/');
            if (nested >= 0)
            {
                qualified = qualified.Substring(0, nested);
            }

            int dot = qualified.LastIndexOf('.');
            return dot >= 0 ? qualified.Substring(dot + 1) : qualified;
        }

        /// <summary>"Namespace.Type:Method (args)" → "Type.Method", compiler-generated names tidied.</summary>
        public static string ShortMethod(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return string.Empty;
            }

            int paren = method.IndexOf(" (", StringComparison.Ordinal);
            string name = paren > 0 ? method.Substring(0, paren) : method;
            int colon = name.LastIndexOf(':');
            string type;
            string member;
            if (colon > 0)
            {
                type = name.Substring(0, colon);
                member = name.Substring(colon + 1);
            }
            else
            {
                int dot = name.LastIndexOf('.');
                type = dot > 0 ? name.Substring(0, dot) : string.Empty;
                member = dot > 0 ? name.Substring(dot + 1) : name;
            }

            // Async state machines: "Type/<Method>d__12:MoveNext" → "Type.Method".
            int nested = type.LastIndexOf('/');
            if (nested >= 0)
            {
                string inner = type.Substring(nested + 1);
                type = type.Substring(0, nested);
                if (inner.StartsWith("<", StringComparison.Ordinal))
                {
                    int close = inner.IndexOf('>');
                    if (close > 1)
                    {
                        member = inner.Substring(1, close - 1);
                    }
                }
                else
                {
                    type = type + "." + inner;
                }
            }

            int typeDot = type.LastIndexOf('.');
            string shortType = typeDot >= 0 ? type.Substring(typeDot + 1) : type;
            if (member.StartsWith("<", StringComparison.Ordinal))
            {
                int close = member.IndexOf('>');
                if (close > 1)
                {
                    member = member.Substring(1, close - 1);
                }
            }

            return string.IsNullOrEmpty(shortType) ? member : shortType + "." + member;
        }

        /// <summary>
        /// A project-relative path Unity can open: <c>./Library/PackageCache/name@hash/x</c> → <c>Packages/name/x</c>,
        /// <c>./Assets/x</c> → <c>Assets/x</c>; absolute paths inside the project become relative.
        /// </summary>
        public static string NormalizePath(string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return string.Empty;
            }

            string path = file.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path.Substring(2);
            }

            const string cache = "Library/PackageCache/";
            int cacheIndex = path.IndexOf(cache, StringComparison.OrdinalIgnoreCase);
            if (cacheIndex >= 0)
            {
                string rest = path.Substring(cacheIndex + cache.Length);
                int slash = rest.IndexOf('/');
                string folder = slash > 0 ? rest.Substring(0, slash) : rest;
                int version = folder.IndexOf('@');
                if (version > 0)
                {
                    folder = folder.Substring(0, version);
                }

                return "Packages/" + folder + (slash > 0 ? rest.Substring(slash) : string.Empty);
            }

            string root = ProjectRoot;
            if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length).TrimStart('/');
            }

            return path;
        }

        private static string projectRoot;

        public static string ProjectRoot
        {
            get
            {
                if (projectRoot == null)
                {
                    try
                    {
                        projectRoot = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
                    }
                    catch (Exception)
                    {
                        projectRoot = string.Empty;
                    }
                }

                return projectRoot;
            }
        }

        public static string FileName(string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return string.Empty;
            }

            int slash = Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\'));
            return slash >= 0 ? file.Substring(slash + 1) : file;
        }

        /// <summary>"Assets/Foo.cs(10,5): error CS0103: …" → file, line and column of a compiler message.</summary>
        public static bool TryParseCompilerLocation(string message, out string file, out int line, out int column)
        {
            file = null;
            line = 0;
            column = 0;
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            int colon = message.IndexOf("): ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                return false;
            }

            int open = message.LastIndexOf('(', colon);
            if (open <= 0)
            {
                return false;
            }

            string[] parts = message.Substring(open + 1, colon - open - 1).Split(',');
            if (parts.Length < 1 || !int.TryParse(parts[0], out line))
            {
                return false;
            }

            if (parts.Length > 1)
            {
                int.TryParse(parts[1], out column);
            }

            file = message.Substring(0, open);
            return file.Length > 0;
        }
    }
}
