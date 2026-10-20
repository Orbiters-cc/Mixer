using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Where Orbiters desktop's "Send to Mixer" lands: each editor keeps a folder
    /// <c>%LOCALAPPDATA%/Orbiters/Mixer/inbox/&lt;pid&gt;/</c> with an <c>endpoint.json</c> naming its project, and
    /// imports the requests (<c>*.json</c>) dropped next to it. The folder goes away when the editor quits; Orbiters
    /// desktop skips folders whose process is gone.
    /// </summary>
    [InitializeOnLoad]
    static class SplatInbox
    {
        public const string EndpointKind = "orbiters.mixer.inbox";
        public const string RequestKind = "orbiters.mixer.request";
        public const string ImportSplats = "import-splats";

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

        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Mixer", "inbox", Process.GetCurrentProcess().Id.ToString());
        static FileSystemWatcher watcher;
        static volatile bool pending = true;

        static SplatInbox()
        {
            // Asset import workers load editor code too: only the editor the person works in takes requests.
            if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess())
                return;
            try
            {
                Directory.CreateDirectory(Folder);
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
                UnityEngine.Debug.LogWarning("Mixer: Orbiters desktop cannot send splats here (" + e.Message + ").");
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

        static void Poll()
        {
            if (!pending || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            pending = false;
            foreach (string file in Directory.GetFiles(Folder, "*.json"))
            {
                if (Path.GetFileName(file) == "endpoint.json")
                    continue;
                string json;
                try
                {
                    json = File.ReadAllText(file);
                    File.Delete(file);
                }
                catch (IOException)
                {
                    pending = true; // Still being written.
                    continue;
                }
                Request r = Parse(json);
                if (r == null)
                    continue;
                string ply = Path.Combine(r.session, "reconstruction", "splats.ply");
                try
                {
                    SplatImport.Import(ply, string.IsNullOrEmpty(r.title) ? SplatImport.TitleOf(ply) : r.title);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError("Mixer: the splats Orbiters desktop sent could not be imported: " + e.Message);
                }
            }
        }
    }
}
