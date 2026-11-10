using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Owns every Git process <see cref="UnitGitRunner"/> starts, the ones kept alive between requests included: each runs in
    /// a Windows job that kills it and its children when closed, and all are stopped before a domain reload and when Unity
    /// quits, so none outlives the editor that started it. The same rules as Toolkit's EditorProcessLifetime, which is internal
    /// to Toolkit; this copy goes once Toolkit's runner can write stdin and stream raw output.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnitGitProcessLifetime
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Process, UnitGitProcessJob> Active = new Dictionary<Process, UnitGitProcessJob>();
        // Commands that change the repository: at a reload or quit they get time to finish before they are stopped.
        private static readonly HashSet<Process> Writes = new HashSet<Process>();
        // Processes stopped while they still ran: a job closed under them makes them exit with code 0, which is no success.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, object> StoppedRunning =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Process, object>();

        /// <summary>Whether this process ended because it was stopped (timeout, cancel, reload), not by itself.</summary>
        internal static bool WasStopped(Process process)
        {
            lock (Sync) return StoppedRunning.TryGetValue(process, out _);
        }
        // Only Process.Start itself runs under this lock (a process started at the same moment would inherit the other's
        // pipe ends); a start that waits for it longer than this goes ahead without it rather than hold the editor up.
        private static readonly object StartSync = new object();
        private const int StartLockWaitMilliseconds = 10000;
        /// <summary>How long a reload or quit waits for changes in flight to finish before it stops them.</summary>
        internal const int WriteGraceMilliseconds = 10000;
        private static readonly List<Action> StopListeners = new List<Action>();
        private static volatile bool stopping;

        internal static bool IsStopping => stopping;
        internal static int ActiveCount { get { lock (Sync) return Active.Count; } }

        static UnitGitProcessLifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorApplication.quitting += StopAll;
        }

        /// <summary>Called once when everything stops (reload, quit): owners of kept-alive processes forget them.</summary>
        internal static void OnStopping(Action listener)
        {
            lock (Sync) StopListeners.Add(listener);
        }

        /// <param name="write">The command changes the repository: a reload or quit lets it finish first (see StopAll).</param>
        internal static bool Start(Process process, bool write = false)
        {
            if (stopping) return false;
            UnitGitProcessJob job = Path.DirectorySeparatorChar == '\\' ? UnitGitProcessJob.Create() : null;
            bool started = false;
            try
            {
                bool locked = Monitor.TryEnter(StartSync, StartLockWaitMilliseconds);
                try
                {
                    if (stopping) { job?.Dispose(); return false; }
                    if (!process.Start()) { job?.Dispose(); return false; }
                    started = true;
                }
                finally
                {
                    if (locked) Monitor.Exit(StartSync);
                }

                job?.Assign(process);
                lock (Sync)
                {
                    Active.Add(process, job);
                    if (write) Writes.Add(process);
                }
            }
            catch
            {
                job?.Dispose();
                try { if (started && !process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                throw;
            }
            return true;
        }

        /// <summary>Whether a reload or quit is under way and this process should stop now (a change in flight may finish).</summary>
        internal static bool ShouldStop(bool write) => stopping && !(write && stoppingAllowsWrites);

        private static volatile bool stoppingAllowsWrites;

        /// <summary>Stops the process and everything it started, then forgets it.</summary>
        internal static void Stop(Process process)
        {
            UnitGitProcessJob job;
            lock (Sync)
            {
                Active.TryGetValue(process, out job);
                Active.Remove(process);
                Writes.Remove(process);
            }
            bool running;
            try { running = !process.HasExited; }
            catch (InvalidOperationException) { running = false; }
            if (running)
                lock (Sync)
                    if (!StoppedRunning.TryGetValue(process, out _)) StoppedRunning.Add(process, null);
            if (job != null) job.Dispose();
            else
            {
                try
                {
                    if (running && Path.DirectorySeparatorChar != '\\') StopUnixChildren(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is IOException) { }
            }
            KillRoot(process);
            if (!running) return;
            try { process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        /// <summary>
        /// How many processes of this one's job still run (it and everything it started); -1 when unknown (no job). Zero
        /// after it exited: everything it and its children wrote is in its pipes, whoever else may still hold them.
        /// </summary>
        internal static int RunningInJob(Process process)
        {
            UnitGitProcessJob job;
            lock (Sync) Active.TryGetValue(process, out job);
            return job == null ? -1 : job.ActiveProcesses();
        }


        private static void KillRoot(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private static void StopAll() => StopAll(WriteGraceMilliseconds);

        /// <summary>
        /// Before a reload and at quit: reads and kept-alive processes stop at once; a change in flight (a commit, a
        /// staging) gets up to <paramref name="graceMilliseconds"/> to finish (its runner keeps reading its output
        /// meanwhile), and only then is stopped. A killed change can leave Git's lock file behind, which nothing here
        /// deletes: the failed result says which file it is.
        /// </summary>
        internal static void StopAll(int graceMilliseconds)
        {
            Process[] reads, writes;
            Action[] listeners;
            lock (Sync)
            {
                stoppingAllowsWrites = true;
                stopping = true;
                writes = Writes.ToArray();
                reads = Active.Keys.Where(process => !Writes.Contains(process)).ToArray();
                listeners = StopListeners.ToArray();
            }
            foreach (Action listener in listeners)
            {
                try { listener(); }
                catch (Exception) { /* Stopping goes on for the others. */ }
            }
            foreach (Process process in reads) Stop(process);
            var waited = Stopwatch.StartNew();
            foreach (Process process in writes)
            {
                try
                {
                    int left = (int)Math.Max(0, graceMilliseconds - waited.ElapsedMilliseconds);
                    if (left > 0) process.WaitForExit(left);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            stoppingAllowsWrites = false;
            foreach (Process process in writes) Stop(process);
        }

        /// <summary>For tests only: <see cref="StopAll(int)"/> as a reload runs it, then starting processes is allowed again.</summary>
        internal static void StopAllThenResume(int graceMilliseconds)
        {
            StopAll(graceMilliseconds);
            stopping = false;
        }

        private static void StopUnixChildren(int root)
        {
            string listing = RunCleanup("ps", "-A -o pid= -o ppid=");
            if (listing == null) return;
            var children = new Dictionary<int, List<int>>();
            foreach (string line in listing.Split('\n'))
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !int.TryParse(parts[1], out int parent)) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(pid);
            }
            var order = new List<int>();
            var visited = new HashSet<int> { root };
            var pending = new Stack<int>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                if (!children.TryGetValue(pending.Pop(), out var list)) continue;
                foreach (int child in list) if (visited.Add(child)) { order.Add(child); pending.Push(child); }
            }
            order.Reverse();
            if (order.Count > 0) RunCleanup("kill", "-9 " + string.Join(" ", order));
        }

        // A short helper run outside the lifetime (it must work while everything is stopping); its output, or null.
        private static string RunCleanup(string file, string arguments)
        {
            try
            {
                using (var process = new Process
                       {
                           StartInfo = new ProcessStartInfo(file, arguments)
                           {
                               UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                           }
                       })
                {
                    if (!process.Start()) return null;
                    var pipe = new UnitGitPipe(process.StandardOutput.BaseStream);
                    var output = new MemoryStream();
                    var elapsed = Stopwatch.StartNew();
                    var chunk = new byte[8192];
                    while (elapsed.ElapsedMilliseconds < 750)
                    {
                        int read = pipe.Read(chunk);
                        if (read < 0) break;
                        if (read > 0) output.Write(chunk, 0, read);
                        else System.Threading.Thread.Sleep(5);
                    }
                    KillRoot(process);
                    return System.Text.Encoding.UTF8.GetString(output.ToArray());
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// One end of a child's output pipe, read only when bytes are waiting (PeekNamedPipe, poll): no read ever blocks, so no
    /// thread can be stuck in a native read when the domain unloads, and no Mono BeginRead callback is ever scheduled.
    /// </summary>
    internal sealed class UnitGitPipe
    {
        private readonly SafeFileHandle handle;
        internal bool Closed { get; private set; }

        internal UnitGitPipe(Stream stream)
        {
            handle = ((FileStream)stream).SafeFileHandle;
        }

        /// <summary>Bytes read into the start of <paramref name="buffer"/> (0: none waiting), or -1 once the writer closed the pipe.</summary>
        internal int Read(byte[] buffer)
        {
            if (Closed) return -1;
            int read = ReadAvailable(buffer);
            if (read < 0) Closed = true;
            return read;
        }

        private int ReadAvailable(byte[] buffer)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                    return PipeError(Marshal.GetLastWin32Error());
                if (available == 0) return 0;
                // Nothing else reads this handle between the peek and the read: only what is waiting is asked for.
                if (!ReadFile(handle, buffer, (uint)Math.Min(buffer.Length, available), out uint read, IntPtr.Zero))
                    return PipeError(Marshal.GetLastWin32Error());
                return read == 0 ? -1 : (int)read;
            }

            var descriptor = new PollDescriptor { File = handle.DangerousGetHandle().ToInt32(), Events = 1 };
            int ready = Poll(ref descriptor, new UIntPtr(1), 0);
            if (ready < 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 4) return 0; // EINTR
                throw new Win32Exception(error);
            }
            if (ready == 0) return 0;
            if ((descriptor.ReturnedEvents & 32) != 0) throw new IOException("Git's output pipe closed unexpectedly.");
            long length = UnixRead(descriptor.File, buffer, new UIntPtr((uint)buffer.Length)).ToInt64();
            if (length == 0) return -1;
            if (length > 0) return (int)length;
            int readError = Marshal.GetLastWin32Error();
            if (readError == 4 || readError == 11 || readError == 35) return 0;
            throw new Win32Exception(readError);
        }

        private static int PipeError(int error)
        {
            if (error == 109 || error == 232) return -1; // broken / disconnected pipe: the writer is gone
            throw new Win32Exception(error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollDescriptor { public int File; public short Events, ReturnedEvents; }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint size, out uint read, IntPtr overlapped);
        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int Poll(ref PollDescriptor descriptor, UIntPtr count, int timeout);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr UnixRead(int file, byte[] buffer, UIntPtr count);
    }

    // Kills a process and its descendants when the handle closes, even after the launcher exited and left a pipe open.
    internal sealed class UnitGitProcessJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private UnitGitProcessJob() : base(true) { }

        internal static UnitGitProcessJob Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return job;
            }
            catch { job.Dispose(); throw; }
        }

        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        internal int ActiveProcesses()
        {
            if (IsClosed || IsInvalid) return -1;
            var accounting = new Accounting();
            return QueryInformationJobObject(this, 1, ref accounting, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero)
                ? (int)accounting.ActiveProcesses
                : -1;
        }

        // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        [StructLayout(LayoutKind.Sequential)]
        private struct Accounting
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(UnitGitProcessJob job, int infoClass, ref Accounting info, uint size, IntPtr returned);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern UnitGitProcessJob CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(UnitGitProcessJob job, int infoClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(UnitGitProcessJob job, IntPtr process);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
