using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Keeps the session's logs across script reloads (which wipe managed memory): what arrived since the last save is
    /// written before a reload and the store is put back after it (<see cref="SessionFile"/>). The files belong to this
    /// editor process; another session (or a crash) leaves some that are ignored and replaced.
    /// </summary>
    internal static class LogSnapshot
    {
        private static readonly SessionFile session = new SessionFile(DirectoryPath);

        public static string DirectoryPath => Path.Combine("Library", "Orbiters", "Logger", "Session");

        private static long EditorStartTicks => DateTime.UtcNow.Ticks - (long)(EditorApplication.timeSinceStartup * TimeSpan.TicksPerSecond);

        private static int ProcessId
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().Id;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public static void Save(LogStore store, SnapshotInfo info)
        {
            session.Save(store, info, ProcessId, EditorStartTicks);
            session.Close();
        }

        /// <summary>A save before a reload failed: the last committed session stays for the next domain (see <see cref="SessionFile.MarkSaveFailed"/>).</summary>
        public static void SaveFailed() => session.MarkSaveFailed();

        public static void Delete()
        {
            session.Delete();
            try
            {
                // Where the session went before it had its own folder.
                string old = Path.Combine("Library", "Orbiters", "Logger", "session.bin");
                if (File.Exists(old))
                {
                    File.Delete(old);
                }
            }
            catch (Exception)
            {
                // Left for the next quit.
            }
        }

        /// <summary>
        /// The store saved by this editor session, or null. <paramref name="problem"/> says why a saved session could not
        /// be read back (null when there was none to read).
        /// </summary>
        public static LogStore TryLoad(out SnapshotInfo info, out string problem)
        {
            // Nothing is deleted here: the files of the last committed session go only once a later save succeeds.
            return session.Load(ProcessId, EditorStartTicks, out info, out problem);
        }
    }
}
