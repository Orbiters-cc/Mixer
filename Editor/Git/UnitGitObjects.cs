using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Git objects (file versions, commits, trees) read through one <c>git cat-file --batch</c> (and --batch-check) for the
    /// length of one operation, instead of one process per object. The caller opens it for its operation and disposes it at
    /// the end, on every path: its processes are then stopped and waited for, so none outlives the operation (on Windows a
    /// running Git keeps the repository's folder and the pack files it maps from being moved or deleted). Only names that
    /// start with a full object id are asked this way (<see cref="IsStable"/>); anything else, and anything the batch
    /// process fails on, goes through a process of its own.
    /// </summary>
    internal sealed class UnitGitObjects : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 30000;
        private static readonly Regex StableName = new Regex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})(?:[\^~][0-9]*)*(?::|$)", RegexOptions.Compiled);

        private readonly string root;
        private readonly object gate = new object();
        private readonly MemoryStream header = new MemoryStream();
        private EditorLiveProcess contents, info;
        private bool disposed;

        private UnitGitObjects(string root)
        {
            this.root = root;
        }

        /// <summary>A reader of the repository at <paramref name="projectRoot"/> for one operation: dispose it at its end.</summary>
        internal static UnitGitObjects Open(string projectRoot)
        {
            return new UnitGitObjects(Path.GetFullPath(projectRoot ?? string.Empty));
        }

        /// <summary>
        /// True for "&lt;full id&gt;", "&lt;full id&gt;:path", "&lt;full id&gt;^:path" and the like: names a kept-alive process
        /// answers exactly like a new one. Names with a line break cannot be sent one per line.
        /// </summary>
        internal static bool IsStable(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf('\n') < 0 && name.IndexOf('\r') < 0 && StableName.IsMatch(name);
        }

        internal enum Answer
        {
            Found,
            Missing,
            Failed // the batch process could not answer: ask with a process of its own
        }

        /// <summary>Copies the object's bytes, exactly as stored, to <paramref name="sink"/> (null: skipped).</summary>
        internal Answer Read(string name, Stream sink, out string type, out long size)
        {
            return Ask(name, sink, true, out type, out size);
        }

        /// <summary>The object's type and size, without its content.</summary>
        internal Answer Describe(string name, out string type, out long size)
        {
            return Ask(name, null, false, out type, out size);
        }

        /// <summary>The object as UTF-8 text (a leading byte order mark dropped), or null when missing or unreadable.</summary>
        internal string ReadText(string name, out Answer answer)
        {
            var bytes = new MemoryStream();
            answer = Read(name, bytes, out _, out _);
            return answer == Answer.Found ? UnitGitRunner.Decode(bytes.GetBuffer(), (int)bytes.Length) : null;
        }

        private Answer Ask(string name, Stream sink, bool withContents, out string type, out long size)
        {
            type = string.Empty;
            size = 0;
            if (!IsStable(name)) return Answer.Failed;
            lock (gate)
            {
                if (disposed) return Answer.Failed;
                EditorLiveProcess process = withContents ? contents : info;
                bool fresh = false;
                if (process == null || process.IsDisposed || process.HasExited)
                {
                    process?.Dispose();
                    process = StartBatch(withContents);
                    if (withContents) contents = process; else info = process;
                    if (process == null) return Answer.Failed;
                    fresh = true;
                }

                try
                {
                    process.Write(UnitGitRunner.Utf8.GetBytes(name + "\n"));
                    if (!process.ReadRecord((byte)'\n', header, RequestTimeoutMilliseconds, null, out _))
                        return Fail(withContents);
                    string line = UnitGitRunner.Utf8.GetString(header.GetBuffer(), 0, (int)header.Length);
                    // "<name> missing", "<name> ambiguous" (the name is echoed: it may contain spaces), else "<id> <type> <size>".
                    if (line.EndsWith(" missing", StringComparison.Ordinal) || line.EndsWith(" ambiguous", StringComparison.Ordinal))
                        return Answer.Missing;
                    string[] fields = line.Split(' ');
                    if (fields.Length != 3 || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out size))
                        return Fail(withContents);
                    type = fields[1];
                    if (!withContents) return Answer.Found;
                    // The content, then one line feed.
                    if (!process.ReadExactly(size, sink, Math.Max(RequestTimeoutMilliseconds, (int)Math.Min(int.MaxValue, size / 1024)), null, out _) ||
                        !process.ReadExactly(1, null, RequestTimeoutMilliseconds, null, out _))
                        return Fail(withContents);
                    return Answer.Found;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    // A process that died between two requests: one more try with a new one.
                    Fail(withContents);
                    return fresh ? Answer.Failed : Ask(name, sink, withContents, out type, out size);
                }
            }
        }

        private Answer Fail(bool withContents)
        {
            if (withContents) { contents?.Dispose(); contents = null; }
            else { info?.Dispose(); info = null; }
            return Answer.Failed;
        }

        private EditorLiveProcess StartBatch(bool withContents)
        {
            EditorLiveProcess process = null;
            try
            {
                process = UnitGitRunner.StartLive(root, new[] { "cat-file", withContents ? "--batch" : "--batch-check" }, null, true);
                if (process == null) return null;
                // An empty first request answers " missing": anything stdin may have gained in front of it (a byte order mark
                // from the console's encoding) is consumed with it, and the process is known to answer.
                process.Write(new[] { (byte)'\n' });
                for (int line = 0; line < 2; line++)
                {
                    if (!process.ReadRecord((byte)'\n', header, RequestTimeoutMilliseconds, null, out _)) break;
                    if (UnitGitRunner.Utf8.GetString(header.GetBuffer(), 0, (int)header.Length).EndsWith(" missing", StringComparison.Ordinal))
                        return process;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
            }
            process?.Dispose();
            return null;
        }

        /// <summary>Stops the processes and waits for them to end.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                contents?.Dispose();
                info?.Dispose();
                contents = info = null;
            }
        }
    }
}
