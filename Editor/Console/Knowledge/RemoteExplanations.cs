using System;
using System.IO;
using UnityEditor;
using UnityEngine.Networking;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Keeps the explanations up to date from the Orbiters server between Mixer console releases. The last list received is
    /// kept per user (so every project has it at once, offline too) and asked for again every few hours with its
    /// revision: the server only answers with a list when it changed. Signed-in members (Orbiters Toolkit installed)
    /// also get the entries for members. Failures are silent: the Mixer console's own list stays.
    /// </summary>
    [InitializeOnLoad]
    internal static class RemoteExplanations
    {
        internal const string EnabledPref = "Orbiters.Logger.RemoteExplanations";
        private const double RefreshHours = 6d;
        private const int TimeoutSeconds = 20;

        private static UnityWebRequest request;
        private static string requestUrl;
        private static bool started;

        static RemoteExplanations()
        {
            EditorApplication.update += Start;
            // A new account or server: its own list.
            OrbitersLink.Changed += () => Refresh(force: true);
        }

        /// <summary>The address of the list on the server in use (Orbiters Toolkit can point at another one).</summary>
        private static string Endpoint() => OrbitersLink.ApiUrl("logger/explanations");

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                {
                    Refresh(force: true);
                }
                else
                {
                    LogExplanations.SetRemote(null);
                }
            }
        }

        /// <summary>When the list in use was received (UTC), or null.</summary>
        internal static DateTime? ReceivedAt
        {
            get
            {
                try
                {
                    string path = CachePath(Endpoint());
                    return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        internal static bool Busy => request != null;

        // After every package's startup code ran (Orbiters Toolkit sets the endpoint and token then).
        private static void Start()
        {
            EditorApplication.update -= Start;
            if (started || !Enabled)
            {
                return;
            }

            started = true;
            LoadCache();
            Refresh(force: false);
        }

        /// <summary>Asks the server again; without <paramref name="force"/>, only when the list is a few hours old.</summary>
        internal static void Refresh(bool force)
        {
            if (!Enabled || request != null)
            {
                return;
            }

            string url;
            try
            {
                url = Endpoint();
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            var received = ReceivedAt;
            if (!force && received.HasValue && DateTime.UtcNow - received.Value < TimeSpan.FromHours(RefreshHours))
            {
                return;
            }

            try
            {
                requestUrl = url;
                request = UnityWebRequest.Get(url);
                request.timeout = TimeoutSeconds;
                string revision = CachedRevision(url);
                if (!string.IsNullOrEmpty(revision) && !force)
                {
                    request.SetRequestHeader("If-None-Match", "\"" + revision + "\"");
                }

                string token = OrbitersLink.SafeToken();
                if (!string.IsNullOrEmpty(token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }

                request.SendWebRequest();
                EditorApplication.update += Poll;
            }
            catch (Exception)
            {
                request?.Dispose();
                request = null;
            }
        }

        private static void Poll()
        {
            if (request == null)
            {
                EditorApplication.update -= Poll;
                return;
            }

            if (!request.isDone)
            {
                return;
            }

            EditorApplication.update -= Poll;
            var done = request;
            string url = requestUrl;
            request = null;
            try
            {
                if (done.responseCode == 304)
                {
                    // Unchanged: the copy counts as fresh again.
                    File.SetLastWriteTimeUtc(CachePath(url), DateTime.UtcNow);
                }
                else if (done.result == UnityWebRequest.Result.Success && done.responseCode == 200)
                {
                    string json = done.downloadHandler.text;
                    var entries = KnownLogs.Parse(json);
                    string path = CachePath(url);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, json);
                    LogExplanations.SetRemote(entries);
                }
            }
            catch (Exception)
            {
                // A list that can't be read or saved leaves the current one.
            }
            finally
            {
                done.Dispose();
            }
        }

        private static void LoadCache()
        {
            try
            {
                string path = CachePath(Endpoint());
                if (File.Exists(path))
                {
                    LogExplanations.SetRemote(KnownLogs.Parse(File.ReadAllText(path)));
                }
            }
            catch (Exception)
            {
                // No usable copy: the Mixer console's own list until the server answers.
            }
        }

        private static string CachedRevision(string url)
        {
            try
            {
                string path = CachePath(url);
                if (!File.Exists(path))
                {
                    return null;
                }

                var file = UnityEngine.JsonUtility.FromJson<ExplanationFile>(File.ReadAllText(path));
                return file?.revision;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // One copy per server (production and a local development server keep their own), shared by every project.
        private static string CachePath(string url)
        {
            string host = "orbiters";
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = uri.Host + (uri.IsDefaultPort ? string.Empty : "-" + uri.Port);
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Logger");
            return Path.Combine(folder, "explanations-" + host + ".json");
        }
    }
}
