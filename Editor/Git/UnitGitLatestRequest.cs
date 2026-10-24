using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbiters.UnitGit.Editor
{
    // One running read and one replaceable pending read. Poll and Request run on the editor thread.
    internal sealed class UnitGitLatestRequest<T> : IDisposable
    {
        private Task<T> running;
        private Func<Func<bool>, T> pending;
        private int version;
        private int runningVersion;

        public bool IsBusy { get { return running != null || pending != null; } }

        public void Request(Func<Func<bool>, T> read)
        {
            Interlocked.Increment(ref version);
            pending = read;
            StartPending();
        }

        public bool Poll(out T result, out Exception error)
        {
            result = default(T);
            error = null;
            if (running == null || !running.IsCompleted)
                return false;

            bool current = runningVersion == Volatile.Read(ref version);
            if (running.IsFaulted)
                error = running.Exception.GetBaseException();
            else if (current && !running.IsCanceled)
                result = running.Result;
            running = null;
            StartPending();
            return current;
        }

        private void StartPending()
        {
            if (running != null || pending == null)
                return;
            var read = pending;
            pending = null;
            int requestVersion = version;
            runningVersion = requestVersion;
            running = Task.Run(() => read(() => requestVersion != Volatile.Read(ref version)));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref version);
            pending = null;
        }
    }
}
