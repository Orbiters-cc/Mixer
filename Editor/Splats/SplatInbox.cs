using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Where Orbiters - N4N0's "Send to Mixer" lands: each editor keeps a folder
    /// <c>%LOCALAPPDATA%/Orbiters/Mixer/inbox/&lt;pid&gt;/</c> with an <c>endpoint.json</c> naming its project, and
    /// imports the requests (<c>*.json</c>) dropped next to it. The folder goes away when the editor quits, and the next
    /// editor to start clears the folders of editors that crashed; Orbiters - N4N0 skips folders whose process is gone.
    /// </summary>
    [InitializeOnLoad]
    static class SplatInbox
    {
        public const string EndpointKind = "orbiters.mixer.inbox";
        public const string RequestKind = "orbiters.mixer.request";
        public const string ImportSplats = "import-splats";

        /// <summary>A request that cannot be read (locked, no access) is tried again this often, and given up after <see cref="MaxTries"/>.</summary>
        const double RetrySeconds = 1.0;
        const int MaxTries = 60;

        [Serializable]
        internal sealed class Endpoint
        {
            public string kind = EndpointKind;
            public int pid;
            public string projectPath;
            public string projectName;
            public string updatedUtc;
        }

        [Serializable]
        internal sealed class Request
        {
            public string kind;
            public string action;
            /// <summary>The session folder.</summary>
            public string session;
            public string title;
        }

        static readonly string Inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Mixer", "inbox");
        static readonly string Folder = Path.Combine(Inbox, Process.GetCurrentProcess().Id.ToString());
        static FileSystemWatcher watcher;
        static volatile bool pending = true;
        static double retryAt;
        static readonly Dictionary<string, int> tries = new Dictionary<string, int>();

        static SplatInbox()
        {
            // Asset import workers load editor code too: only the editor the person works in takes requests.
            if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess())
                return;
            try
            {
                Directory.CreateDirectory(Folder);
                SweepDead(Inbox, Process.GetCurrentProcess().Id);
                string project = Path.GetDirectoryName(Application.dataPath);
                var endpoint = new Endpoint { pid = Process.GetCurrentProcess().Id, projectPath = project, projectName = Path.GetFileName(project), updatedUtc = DateTime.UtcNow.ToString("o") };
                string tmp = Path.Combine(Folder, "endpoint.json.part");
                File.WriteAllText(tmp, JsonUtility.ToJson(endpoint, true));
                File.Copy(tmp, Path.Combine(Folder, "endpoint.json"), true);
                File.Delete(tmp);
                watcher = new FileSystemWatcher(Folder, "*.json") { EnableRaisingEvents = true };
                watcher.Created += (_, __) => pending = true;
                watcher.Renamed += (_, __) => pending = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("Mixer: Orbiters - N4N0 cannot send splats here (" + e.Message + ").");
                return;
            }
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += () => watcher?.Dispose();
            EditorApplication.quitting += () =>
            {
                watcher?.Dispose();
                try { Directory.Delete(Folder, true); } catch (Exception) { }
            };
        }

        /// <summary>
        /// Clears the folders under <paramref name="inbox"/> of editors that are gone (they crashed, so they never removed
        /// theirs): a process that later gets the same number would look like a live editor to Orbiters - N4N0, and what
        /// it sends would never be imported. Only the files an inbox holds are deleted, never links or anything else.
        /// </summary>
        internal static void SweepDead(string inbox, int self)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(inbox);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            foreach (string dir in dirs)
            {
                if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid == self || IsEditor(pid))
                    continue;
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    foreach (string file in Directory.GetFiles(dir, "*.json"))
                        File.Delete(file);
                    foreach (string file in Directory.GetFiles(dir, "*.json.part"))
                        File.Delete(file);
                    Directory.Delete(dir, false);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Another editor sweeping at the same time, or something else in there: left as it is.
                }
            }
        }

        /// <summary>Whether a Unity editor runs as <paramref name="pid"/>; true when that cannot be told.</summary>
        static bool IsEditor(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && process.ProcessName.Equals("Unity", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false; // No such process.
            }
            catch (InvalidOperationException)
            {
                return false; // It has just exited.
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>A request from its JSON, or null when it is not one this inbox handles.</summary>
        internal static Request Parse(string json)
        {
            try
            {
                var r = JsonUtility.FromJson<Request>(json);
                return r != null && r.kind == RequestKind && r.action == ImportSplats && !string.IsNullOrEmpty(r.session) ? r : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The splats of a request's session. Any process on this computer can drop a request, so the session must be a
        /// full path on a local drive: a network path would make the editor fetch a file from another machine (and give
        /// it the person's Windows credentials).
        /// </summary>
        internal static string SessionSplats(string session)
        {
            if (string.IsNullOrWhiteSpace(session) || !Path.IsPathRooted(session))
                throw new ArgumentException("The request names no full session folder.");
            string full = Path.GetFullPath(session);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException("Splats are only taken from sessions on this computer, not " + session + ".");
            var drive = new DriveInfo(Path.GetPathRoot(full));
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                throw new ArgumentException("Splats are only taken from sessions on this computer's own drives, not " + session + ".");
            return Path.Combine(full, "reconstruction", "splats.ply");
        }

        static void Poll()
        {
            if (!pending || EditorApplication.timeSinceStartup < retryAt || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            pending = false;
            string[] files;
            try
            {
                files = Directory.GetFiles(Folder, "*.json");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            foreach (string file in files)
            {
                if (Path.GetFileName(file) == "endpoint.json")
                    continue;
                string json;
                try
                {
                    json = File.ReadAllText(file);
                    File.Delete(file);
                    tries.Remove(file);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Still being written: again in a moment, not every editor update. One that never opens is given up.
                    tries.TryGetValue(file, out int n);
                    tries[file] = ++n;
                    if (n == MaxTries)
                        UnityEngine.Debug.LogWarning("Mixer: a request from Orbiters - N4N0 cannot be read (" + e.Message + "): " + file);
                    if (n < MaxTries)
                    {
                        pending = true;
                        retryAt = EditorApplication.timeSinceStartup + RetrySeconds;
                    }
                    continue;
                }
                Request r = Parse(json);
                if (r == null)
                    continue;
                try
                {
                    string ply = SessionSplats(r.session);
                    SplatImport.Import(ply, string.IsNullOrEmpty(r.title) ? SplatImport.TitleOf(ply) : r.title);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError("Mixer: the splats Orbiters - N4N0 sent could not be imported: " + e.Message);
                }
            }
        }
    }
}
