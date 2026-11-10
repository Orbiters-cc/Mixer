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
        /// <summary>Writes Git's stdin on a thread of its own, while the outputs are read (no pipe can fill up and stall
        /// both sides). The stream is closed after it returns. Null: stdin is closed at once.</summary>
        public Action<Stream> Input;
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
        /// <summary>Writing stdin failed while Git still ran: Git was stopped before it could read an end of input.</summary>
        public bool InputFailed;
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

        // Mono's Process writes stdin with Console.InputEncoding: with Windows' "UTF-8 for worldwide language support" that
        // is UTF-8 with a byte order mark, which would land in front of the first path. Where the start info can name the
        // encoding it is set to UTF-8 without one; where it cannot and the console's would add one, stdin is not used.
        private static readonly PropertyInfo InputEncoding = typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");

        /// <summary>False when Git's stdin would start with a byte order mark: send paths and messages in a file instead.</summary>
        internal static bool CleanInput
        {
            get
            {
                if (InputEncoding != null && InputEncoding.CanWrite) return true;
                try { return Console.InputEncoding.GetPreamble().Length == 0; }
                catch (IOException) { return true; }
            }
        }

        internal static ProcessStartInfo StartInfo(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", arguments.Select(UnitGitService.EscapeArgument).ToArray()),
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                CreateNoWindow = true
            };
            if (input && InputEncoding != null && InputEncoding.CanWrite) InputEncoding.SetValue(start, Utf8);
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

            UnitGitLiveProcess process = UnitGitLiveProcess.Start(request.WorkingDirectory, request.Arguments, request.Environment, request.Input != null);
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
                    Action<Stream> write = request.Input;
                    writer = new Thread(() =>
                    {
                        // Stdin is closed only once everything is written. A list cut short must never reach Git as a complete
                        // one (an empty path list is the whole tree to "add -A"): a failed write leaves stdin open, and the
                        // loop below stops the process before it can read an end.
                        try
                        {
                            write(input);
                            process.CloseInput();
                        }
                        catch (Exception)
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
                    // Git still running after its input failed (else it stopped reading on its own, and its error says why).
                    if (inputFailed != 0 && !process.HasExited)
                    {
                        process.Dispose();
                        result.InputFailed = true;
                        break;
                    }

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
                        // A descendant may still hold a pipe: never wait for its end without limit.
                        if (elapsed.ElapsedMilliseconds - exitedAt >= DrainTimeoutMilliseconds) { result.TimedOut = true; break; }
                    }

                    if (read) idle.Reset();
                    else idle.Wait();
                }
            }
            finally
            {
                process.Dispose();
                writer?.Join(1000);
                if (Interlocked.CompareExchange(ref inputFailed, 0, 0) != 0) result.InputFailed = true;
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
        internal static UnitGitLiveProcess Start(string workingDirectory, IList<string> arguments, IDictionary<string, string> environment, bool input)
        {
            var process = new Process { StartInfo = UnitGitRunner.StartInfo(workingDirectory, arguments, environment, input) };
            bool started = false;
            try
            {
                started = UnitGitProcessLifetime.Start(process);
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
