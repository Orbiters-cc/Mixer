using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine.Networking;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Keeps the explanations up to date from the Orbiters server between Mixer console releases. The last list received is
    /// kept per user (so every project has it at once, offline too) and asked for again every few hours with its
    /// revision: the server only answers with a list when it changed (a running editor looks every few minutes whether
    /// that is due, less often after failures). Signed-in members (Orbiters Toolkit installed) also get the entries for
    /// members. An answer only counts for the server and account it was asked for: signing out, another server or turning
    /// the list off while it downloads drops it. Failures are silent: the Mixer console's own list stays.
    /// </summary>
    [InitializeOnLoad]
    internal static class RemoteExplanations
    {
        internal const string EnabledPref = "Orbiters.Logger.RemoteExplanations";
        private const double RefreshHours = 6d;
        private const int TimeoutSeconds = 20;
        // How often a running editor looks whether the list is due, and the longest wait after failed attempts.
        private const double CheckSeconds = 600d;
        private const double MaxBackoffSeconds = RefreshHours * 3600d;

        private static UnityWebRequest request;
        private static string requestUrl;
        // The server and account the request under way was made for.
        private static string requestContext;
        private static bool started;
        private static int failures;
        private static double nextCheck;

        static RemoteExplanations()
        {
            EditorApplication.update += Start;
            // A new account or server: its own list (the copy kept for that server at once, then the server's answer).
            OrbitersLink.Changed += OnLinkChanged;
        }

        private static void OnLinkChanged()
        {
            if (!started || !Enabled)
            {
                return;
            }

            failures = 0;
            LoadCache();
            Refresh(force: true);
        }

        /// <summary>The address of the list on the server in use (Orbiters Toolkit can point at another one).</summary>
        private static string Endpoint() => OrbitersLink.ApiUrl("logger/explanations");

        // The server and the account (a hash of its token, never the token itself) a list is for.
        internal static string Context(string url, string token) =>
            url + "|" + (string.IsNullOrEmpty(token) ? "-" : token.GetHashCode().ToString(CultureInfo.InvariantCulture));

        private static string CurrentContext()
        {
            try
            {
                string url = Endpoint();
                return string.IsNullOrEmpty(url) ? null : Context(url, OrbitersLink.SafeToken());
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                {
                    failures = 0;
                    Refresh(force: true);
                }
                else
                {
                    // A download under way is dropped: its answer must not bring the list back.
                    Cancel();
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

        /// <summary>
        /// Whether an answer to a request made for <paramref name="askedFor"/> may be used now, with the list
        /// <paramref name="enabled"/> and the server and account at <paramref name="now"/>.
        /// </summary>
        internal static bool Applies(string askedFor, string now, bool enabled) => enabled && askedFor != null && askedFor == now;

        /// <summary>How long a running editor waits before it looks again, after <paramref name="failures"/> failed attempts in a row.</summary>
        internal static double WaitAfter(int failures) =>
            failures <= 0 ? CheckSeconds : Math.Min(MaxBackoffSeconds, CheckSeconds * Math.Pow(2d, Math.Min(failures, 16)));

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
            nextCheck = EditorApplication.timeSinceStartup + CheckSeconds;
            EditorApplication.update += Tick;
        }

        // Now and then: the list is asked for again once it is RefreshHours old, later after failed attempts.
        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextCheck)
            {
                return;
            }

            nextCheck = EditorApplication.timeSinceStartup + WaitAfter(failures);
            Refresh(force: false);
        }

        private static void Cancel()
        {
            EditorApplication.update -= Poll;
            if (request == null)
            {
                return;
            }

            try
            {
                request.Abort();
            }
            catch (Exception)
            {
                // It had finished.
            }

            request.Dispose();
            request = null;
            requestContext = null;
        }

        /// <summary>Asks the server again; without <paramref name="force"/>, only when the list is a few hours old.</summary>
        internal static void Refresh(bool force)
        {
            if (!Enabled)
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

            // One request at a time: the same question is answered by the one under way, another server or account drops
            // it and asks anew.
            string token = OrbitersLink.SafeToken();
            string context = Context(url, token);
            if (request != null)
            {
                if (context == requestContext)
                {
                    return;
                }

                Cancel();
            }

            var received = ReceivedAt;
            if (!force && received.HasValue && DateTime.UtcNow - received.Value < TimeSpan.FromHours(RefreshHours))
            {
                return;
            }

            try
            {
                requestUrl = url;
                requestContext = context;
                request = UnityWebRequest.Get(url);
                request.timeout = TimeoutSeconds;
                string revision = CachedRevision(url);
                if (!string.IsNullOrEmpty(revision) && !force)
                {
                    request.SetRequestHeader("If-None-Match", "\"" + revision + "\"");
                }

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
                requestContext = null;
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
            string context = requestContext;
            request = null;
            requestContext = null;
            try
            {
                if (!Applies(context, CurrentContext(), Enabled))
                {
                    // Asked for another server or account, or turned off since: not the list to use.
                    return;
                }

                if (done.responseCode == 304)
                {
                    // Unchanged: the copy counts as fresh again.
                    failures = 0;
                    File.SetLastWriteTimeUtc(CachePath(url), DateTime.UtcNow);
                }
                else if (done.result == UnityWebRequest.Result.Success && done.responseCode == 200)
                {
                    failures = 0;
                    string json = done.downloadHandler.text;
                    var entries = KnownLogs.Parse(json);
                    string path = CachePath(url);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, json);
                    LogExplanations.SetRemote(entries);
                }
                else
                {
                    failures++;
                }
            }
            catch (Exception)
            {
                // A list that can't be read or saved leaves the current one.
                failures++;
            }
            finally
            {
                done.Dispose();
            }
        }

        // The copy kept for the server in use, or none (the Mixer console's own list until the server answers).
        private static void LoadCache()
        {
            try
            {
                string path = CachePath(Endpoint());
                LogExplanations.SetRemote(File.Exists(path) ? KnownLogs.Parse(File.ReadAllText(path)) : null);
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
