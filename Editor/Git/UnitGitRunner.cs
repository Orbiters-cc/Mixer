using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>One Git command for <see cref="UnitGitRunner.Run"/>.</summary>
    internal sealed class UnitGitRunRequest
    {
        public string WorkingDirectory = string.Empty;
        public IList<string> Arguments = Array.Empty<string>();
        public int TimeoutMilliseconds = UnitGitService.DefaultTimeoutMilliseconds;
        public IDictionary<string, string> Environment;
        public Func<bool> Cancelled;
        /// <summary>Git's whole stdin, complete before Git starts (nothing can fail halfway through a list but the pipe
        /// itself), written on a thread of its own while the outputs are read so no pipe can fill up and stall both sides.
        /// Null: stdin is closed at once.</summary>
        public byte[] Input;
        /// <summary>For a command that changes the repository: the index lock it may take. If the command has to be stopped
        /// (timeout, reload) and leaves this lock behind, the lock is removed when it is provably that command's.</summary>
        public string IndexLock;
        /// <summary>Git's stdout as raw bytes, as they come. Return false to stop: the process is stopped and the result says
        /// <see cref="UnitGitRunResult.Stopped"/>. Null: stdout is kept and decoded as UTF-8 text.</summary>
        public Func<byte[], int, int, bool> Output;
        /// <summary>Each line of stdout (only when <see cref="Output"/> is null) and of stderr, for the command log.</summary>
        public Action<string> OutputLine, ErrorLine;
    }

    internal sealed class UnitGitRunResult
    {
        public int ExitCode = -1;
        public bool TimedOut, Cancelled, Stopped;
        /// <summary>Git stopped reading its input before the end (it failed); its exit code and error say why.</summary>
        public bool InputFailed;
        /// <summary>Git exited, but a program it started kept its output open: what was read may be incomplete.</summary>
        public bool OutputHeldOpen;
        public string StandardOutput = string.Empty, StandardError = string.Empty;
        public bool Success => ExitCode == 0 && !TimedOut && !Cancelled && !Stopped && !InputFailed;
    }

    /// <summary>
    /// Runs Git with what Toolkit's runner has not (yet): a stdin writer, raw stdout as it streams with an early stop, and
    /// processes kept alive between requests (<see cref="UnitGitLiveProcess"/>). Pipes are read only when bytes wait
    /// (<see cref="UnitGitPipe"/>), stdin is written on its own thread, and every process is stopped on timeout, cancel,
    /// early stop, reload and quit (<see cref="UnitGitProcessLifetime"/>).
    /// </summary>
    internal static class UnitGitRunner
    {
        private const int DrainTimeoutMilliseconds = 1000;
        internal static readonly Encoding Utf8 = new UTF8Encoding(false);

        // Mono's Process writes the encoding's preamble to stdin as soon as the process starts (its StreamWriter flushes at
        // once), with Console.InputEncoding unless told otherwise: with Windows' "UTF-8 for worldwide language support" that
        // is a byte order mark in front of the first path, revision or name. Every start therefore names UTF-8 without one,
        // and the bytes Mixer Git writes go to the base stream, never through a writer.
        private static readonly PropertyInfo InputEncoding = typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");

        /// <summary>
        /// Whether Git's stdin starts with exactly the bytes written (the start info could name a preamble-free encoding,
        /// or the console's has no preamble). When false, <see cref="Run"/> refuses input rather than send it with a mark
        /// in front; path lists and messages then go in a file. Tests set <see cref="ForceNoCleanInput"/>.
        /// </summary>
        internal static bool CleanInput
        {
            get
            {
                if (ForceNoCleanInput) return false;
                if (InputEncoding != null && InputEncoding.CanWrite) return true;
                try { return Console.InputEncoding.GetPreamble().Length == 0; }
                catch (IOException) { return true; }
            }
        }

        internal static bool ForceNoCleanInput;

        /// <summary>
        /// Prepended to every Git command: what Git prints of commits is UTF-8 and what it records is marked UTF-8, whatever
        /// the user's i18n settings (a commit stored in another encoding is re-encoded when read), since Mixer Git reads and
        /// writes UTF-8.
        /// </summary>
        internal static readonly string[] EncodingArguments = { "-c", "i18n.logOutputEncoding=UTF-8", "-c", "i18n.commitEncoding=UTF-8" };

        internal static ProcessStartInfo StartInfo(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", EncodingArguments.Concat(arguments).Select(UnitGitService.EscapeArgument).ToArray()),
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                CreateNoWindow = true
            };
            if (InputEncoding != null && InputEncoding.CanWrite) InputEncoding.SetValue(start, Utf8);
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            // Reads (a refresh's status) must not take the index lock: a commit or a staging starting meanwhile would fail on it.
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            if (environment != null)
                foreach (KeyValuePair<string, string> item in environment)
                    if (!string.IsNullOrWhiteSpace(item.Key)) start.EnvironmentVariables[item.Key] = item.Value ?? string.Empty;
            return start;
        }

        /// <summary>Runs one command to its end (or its timeout, cancel or early stop). Call from a worker for long commands.</summary>
        internal static UnitGitRunResult Run(UnitGitRunRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.TimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(request));
            var result = new UnitGitRunResult();
            if (UnitGitProcessLifetime.IsStopping || request.Cancelled?.Invoke() == true) { result.Cancelled = true; return result; }

            if (request.Input != null && !CleanInput)
                throw new InvalidOperationException("Git's input would start with a byte order mark on this system.");

            UnitGitLiveProcess process = UnitGitLiveProcess.Start(request.WorkingDirectory, request.Arguments, request.Environment, request.Input != null,
                UnitGitIndexLock.Guard(request.IndexLock));
            if (process == null) { result.Cancelled = true; return result; }
            Thread writer = null;
            var text = request.Output == null ? new MemoryStream() : null;
            var lines = request.Output == null && request.OutputLine != null ? new LineSplitter(request.OutputLine) : null;
            var errorLines = request.ErrorLine != null ? new LineSplitter(request.ErrorLine) : null;
            int inputFailed = 0;
            try
            {
                if (request.Input != null)
                {
                    Stream input = process.Input;
                    byte[] bytes = request.Input;
                    writer = new Thread(() =>
                    {
                        // The input is complete before Git starts: only the pipe can fail here, when Git stopped reading (it
                        // failed, and says why). Stdin is closed only after the last byte, so a cut list never reaches Git
                        // as a complete one.
                        try
                        {
                            for (int offset = 0; offset < bytes.Length; offset += 65536)
                                input.Write(bytes, offset, Math.Min(65536, bytes.Length - offset));
                            input.Flush();
                            process.CloseInput();
                        }
                        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                        {
                            Interlocked.Exchange(ref inputFailed, 1);
                        }
                    }) { IsBackground = true, Name = "Mixer Git stdin" };
                    writer.Start();
                }
                else process.CloseInput();

                var elapsed = Stopwatch.StartNew();
                long exitedAt = -1;
                var idle = new UnitGitRunner.IdleWait();
                while (true)
                {
                    if (UnitGitProcessLifetime.IsStopping || request.Cancelled?.Invoke() == true) { result.Cancelled = true; break; }
                    if (elapsed.ElapsedMilliseconds >= request.TimeoutMilliseconds) { result.TimedOut = true; break; }

                    bool read = false;
                    int count = process.ReadOutput();
                    if (count > 0)
                    {
                        read = true;
                        if (request.Output != null)
                        {
                            if (!request.Output(process.Chunk, 0, count)) { result.Stopped = true; break; }
                        }
                        else
                        {
                            text.Write(process.Chunk, 0, count);
                            lines?.Add(process.Chunk, count);
                        }
                    }
                    read |= process.DrainError(errorLines);

                    if (process.HasExited)
                    {
                        if (exitedAt < 0) exitedAt = elapsed.ElapsedMilliseconds;
                        if (process.OutputClosed && process.ErrorClosed)
                        {
                            result.ExitCode = process.ExitCode;
                            break;
                        }
                        // Another handle to a pipe keeps it open. When nothing of Git's job runs any more, all it wrote has
                        // arrived (the holder is a process that inherited the handle while starting at the same moment, and
                        // writes nothing): what is left is read and the run is complete. Else a descendant may still write:
                        // never wait for its end without limit.
                        if (elapsed.ElapsedMilliseconds - exitedAt >= DrainTimeoutMilliseconds)
                        {
                            if (process.JobEmpty)
                            {
                                while (true)
                                {
                                    int rest = process.ReadOutput();
                                    if (rest <= 0) break;
                                    if (request.Output != null) { if (!request.Output(process.Chunk, 0, rest)) { result.Stopped = true; break; } }
                                    else { text.Write(process.Chunk, 0, rest); lines?.Add(process.Chunk, rest); }
                                }
                                process.DrainError(errorLines);
                                if (!result.Stopped) result.ExitCode = process.ExitCode;
                                break;
                            }
                            result.TimedOut = result.OutputHeldOpen = true;
                            break;
                        }
                    }

                    if (read) idle.Reset();
                    else idle.Wait();
                }
            }
            finally
            {
                process.Dispose();
                writer?.Join(1000);
                if (Interlocked.CompareExchange(ref inputFailed, 0, 0) != 0 && result.ExitCode != 0) result.InputFailed = true;
                lines?.Finish();
                errorLines?.Finish();
                if (text != null) result.StandardOutput = Decode(text.GetBuffer(), (int)text.Length);
                result.StandardError = process.StandardError;
            }
            return result;
        }

        /// <summary>
        /// The pause between two looks at a pipe with nothing in it. Thread.Sleep(1) lasts a whole timer tick on Windows
        /// (about 15 ms), far longer than Git takes to answer one request of a kept-alive process: the first two
        /// milliseconds only yield, then the wait grows to 1 and 5 ms ticks.
        /// </summary>
        internal struct IdleWait
        {
            private Stopwatch since;

            internal void Reset() => since = null;

            internal void Wait()
            {
                if (since == null) since = Stopwatch.StartNew();
                long waited = since.ElapsedMilliseconds;
                if (waited < 2) Thread.Yield();
                else Thread.Sleep(waited < 50 ? 1 : 5);
            }
        }

        /// <summary>UTF-8 text without a leading byte order mark, as Toolkit's runner decodes Git's output.</summary>
        internal static string Decode(byte[] bytes, int count)
        {
            int start = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Utf8.GetString(bytes, start, count - start);
        }

        /// <summary>Raw bytes in, one callback per line out (CR, LF or CRLF), for the command log.</summary>
        internal sealed class LineSplitter
        {
            private readonly Action<string> onLine;
            private readonly Decoder decoder = Utf8.GetDecoder();
            private readonly StringBuilder pending = new StringBuilder();
            private char[] characters = new char[1024];
            private bool previousCarriageReturn, atStart = true;

            internal LineSplitter(Action<string> onLine) { this.onLine = onLine; }

            internal void Add(byte[] bytes, int count)
            {
                int needed = Utf8.GetMaxCharCount(count);
                if (characters.Length < needed) characters = new char[needed];
                int decoded = decoder.GetChars(bytes, 0, count, characters, 0, false);
                Split(decoded);
            }

            internal void Finish()
            {
                Split(decoder.GetChars(Array.Empty<byte>(), 0, 0, characters, 0, true));
                if (pending.Length > 0) Emit();
            }

            private void Split(int count)
            {
                int start = atStart && count > 0 && characters[0] == '﻿' ? 1 : 0;
                if (count > 0) atStart = false;
                for (int i = start; i < count; i++)
                {
                    char character = characters[i];
                    if (character == '\r') { Emit(); previousCarriageReturn = true; }
                    else if (character == '\n')
                    {
                        if (!previousCarriageReturn) Emit();
                        previousCarriageReturn = false;
                    }
                    else { pending.Append(character); previousCarriageReturn = false; }
                }
            }

            private void Emit()
            {
                string line = pending.ToString();
                pending.Clear();
                onLine(line);
            }
        }
    }

    /// <summary>
    /// Git's index lock after a command that changes the repository had to be stopped (a timeout, a reload): killed, Git
    /// leaves "index.lock" behind and every later change fails on it. The lock is removed only when it is provably the
    /// stopped command's: it did not exist when the command started, it was created after, and no other Git runs at all
    /// (another program's Git may be the one holding it). Anything else leaves it for the user, as Git would.
    /// </summary>
    internal static class UnitGitIndexLock
    {
        /// <summary>The cleanup to run if the command is stopped while running; null when there is no lock to watch.</summary>
        internal static Action Guard(string lockPath)
        {
            if (string.IsNullOrEmpty(lockPath)) return null;
            bool existed;
            try { existed = File.Exists(lockPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            if (existed) return null;
            DateTime started = DateTime.UtcNow.AddSeconds(-2); // file times are coarse
            return () => Release(lockPath, started);
        }

        private static void Release(string lockPath, DateTime startedUtc)
        {
            try
            {
                var file = new FileInfo(lockPath);
                if (!file.Exists || file.CreationTimeUtc < startedUtc) return;
                HashSet<int> ours = UnitGitProcessLifetime.OwnedIds();
                foreach (Process git in Process.GetProcessesByName("git"))
                {
                    using (git)
                    {
                        // Our own kept-alive readers never take the lock; any other Git might be the one holding it now.
                        if (!ours.Contains(git.Id)) return;
                    }
                }
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>
    /// A Git process with its pipes, owned by <see cref="UnitGitProcessLifetime"/>: either one command run to its end by
    /// <see cref="UnitGitRunner.Run"/>, or a process kept alive and asked one thing after the other (<c>cat-file --batch</c>,
    /// a <c>log</c> read page by page). Not thread-safe: one owner reads it at a time; only stdin may be written from
    /// another thread. Dispose stops it and everything it started.
    /// </summary>
    internal sealed class UnitGitLiveProcess : IDisposable
    {
        private const int MaxErrorCharacters = 64 * 1024;
        private readonly Process process;
        private readonly UnitGitPipe output, error;
        private readonly byte[] errorChunk = new byte[4096];
        private readonly StringBuilder errorText = new StringBuilder();
        private StreamWriter writer;
        private readonly Stream input;
        // Bytes read from stdout and not yet consumed by a Read* call: buffer[start..end).
        private byte[] buffer = new byte[64 * 1024];
        private int start, end;
        private int disposed;

        /// <summary>The last bytes <see cref="ReadOutput"/> read.</summary>
        internal readonly byte[] Chunk = new byte[64 * 1024];

        private UnitGitLiveProcess(Process process)
        {
            this.process = process;
            output = new UnitGitPipe(process.StandardOutput.BaseStream);
            error = new UnitGitPipe(process.StandardError.BaseStream);
            // Bytes go to the base stream; the writer (which never buffers anything) is what closes it, so the process's
            // own Dispose finds it closed and has nothing to flush.
            writer = process.StandardInput;
            input = writer.BaseStream;
        }

        /// <summary>Starts Git; null when the editor is stopping (reload, quit). Throws when Git cannot start.</summary>
        internal static UnitGitLiveProcess Start(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input,
            Action onStopped = null)
        {
            var process = new Process { StartInfo = UnitGitRunner.StartInfo(workingDirectory, arguments, environment, input) };
            bool started = false;
            try
            {
                started = UnitGitProcessLifetime.Start(process, onStopped);
                if (!started) { process.Dispose(); return null; }
                return new UnitGitLiveProcess(process);
            }
            catch
            {
                if (started) UnitGitProcessLifetime.Stop(process);
                process.Dispose();
                throw;
            }
        }

        internal Stream Input => input;
        internal bool OutputClosed => output.Closed && start == end;
        internal bool ErrorClosed => error.Closed;
        internal bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        internal bool JobEmpty => UnitGitProcessLifetime.RunningInJob(process) == 0;
        internal int ExitCode { get { try { return process.ExitCode; } catch (InvalidOperationException) { return -1; } } }
        internal string StandardError { get { lock (errorText) return errorText.ToString(); } }
        internal bool IsDisposed => disposed != 0;

        internal void CloseInput()
        {
            StreamWriter closing = Interlocked.Exchange(ref writer, null);
            try { closing?.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Writes to stdin and flushes. A request to a kept-alive process is small: it never fills the pipe.</summary>
        internal void Write(byte[] bytes)
        {
            if (writer == null) throw new IOException("Git's input is closed.");
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
        }

        /// <summary>Reads what stdout has waiting into <see cref="Chunk"/>: the count, 0 when nothing waits, -1 at its end.
        /// For <see cref="UnitGitRunner.Run"/> only: it bypasses the buffer the Read* calls use.</summary>
        internal int ReadOutput() => output.Read(Chunk);

        /// <summary>Reads what stderr has waiting (Git stalls once its stderr pipe is full). True when something was read.</summary>
        internal bool DrainError(UnitGitRunner.LineSplitter lines = null)
        {
            bool any = false;
            while (true)
            {
                int count = error.Read(errorChunk);
                if (count <= 0) return any;
                any = true;
                lines?.Add(errorChunk, count);
                lock (errorText)
                {
                    if (errorText.Length < MaxErrorCharacters) errorText.Append(UnitGitRunner.Utf8.GetString(errorChunk, 0, count));
                }
            }
        }

        /// <summary>
        /// Waits until stdout has unread bytes. False at stdout's end, on timeout, on cancel and while the editor stops;
        /// <paramref name="timedOut"/> tells a timeout apart.
        /// </summary>
        private bool Fill(Stopwatch elapsed, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            timedOut = false;
            var idle = new UnitGitRunner.IdleWait();
            while (start == end)
            {
                if (disposed != 0 || UnitGitProcessLifetime.IsStopping || cancelled?.Invoke() == true) return false;
                if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) { timedOut = true; return false; }
                if (buffer.Length - end < Chunk.Length && start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (buffer.Length - end < Chunk.Length) Array.Resize(ref buffer, buffer.Length * 2);
                int count = output.Read(Chunk);
                bool drained = DrainError();
                if (count < 0) return false;
                if (count > 0)
                {
                    Buffer.BlockCopy(Chunk, 0, buffer, end, count);
                    end += count;
                    return true;
                }
                if (drained) idle.Reset();
                else idle.Wait();
            }
            return true;
        }

        /// <summary>
        /// Reads up to and without the next <paramref name="delimiter"/> into <paramref name="record"/> (cleared first), and
        /// consumes the delimiter. False when stdout ended, timed out or was cancelled before one came; a last record without
        /// a delimiter is still returned (true) when stdout ends after it.
        /// </summary>
        internal bool ReadRecord(byte delimiter, MemoryStream record, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            record.SetLength(0);
            var elapsed = Stopwatch.StartNew();
            timedOut = false;
            while (true)
            {
                int at = Array.IndexOf(buffer, delimiter, start, end - start);
                if (at >= 0)
                {
                    record.Write(buffer, start, at - start);
                    start = at + 1;
                    return true;
                }
                record.Write(buffer, start, end - start);
                start = end = 0;
                if (!Fill(elapsed, timeoutMilliseconds, cancelled, out timedOut)) return !timedOut && record.Length > 0 && output.Closed;
            }
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes of stdout to <paramref name="sink"/> (null: skipped).</summary>
        internal bool ReadExactly(long count, Stream sink, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            var elapsed = Stopwatch.StartNew();
            timedOut = false;
            while (count > 0)
            {
                if (start == end && !Fill(elapsed, timeoutMilliseconds, cancelled, out timedOut)) return false;
                int take = (int)Math.Min(count, end - start);
                sink?.Write(buffer, start, take);
                start += take;
                count -= take;
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                UnitGitProcessLifetime.Stop(process);
                // Stdin closes only once Git is gone: a list cut short is never read to its end.
                try { process.WaitForExit(2000); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            finally
            {
                CloseInput();
                try { process.Dispose(); }
                catch (InvalidOperationException) { } // ObjectDisposedException included
                catch (IOException) { }
            }
        }
    }
}
