using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Orbiters.UnitGit.Editor;
using UnityEditor;

namespace Orbiters.Logger.Editor.UnitGit
{
    /// <summary>
    /// With Mixer Git in the project, the Mixer console's timeline shows its commits and releases. Releases come from
    /// Mixer Git's release catalog, commits from <c>git log</c> on a worker thread (the last 30 days; the timeline only shows
    /// what falls inside the logged time). Refreshed when Mixer Git records something, and every minute while a
    /// Mixer console window is open.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnitGitTimeline
    {
        private const string Source = "unitgit";
        private const double RefreshSeconds = 60d;
        private static double nextRefresh;
        private static double nextHeadCheck;
        private static DateTime lastHeadWrite;
        private static int running;

        static UnitGitTimeline()
        {
            UnitGitReleases.ChangedExternally += Refresh;
            UnitGitCommandLog.Completed += OnCommand;
            EditorApplication.update += Update;
        }

        private static void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= nextHeadCheck)
            {
                // A commit (from Mixer Git or anywhere else) writes .git/logs/HEAD: show it at once.
                nextHeadCheck = now + 1d;
                try
                {
                    var write = File.GetLastWriteTimeUtc(Path.Combine(".git", "logs", "HEAD"));
                    if (write != lastHeadWrite)
                    {
                        bool first = lastHeadWrite == default;
                        lastHeadWrite = write;
                        if (!first)
                        {
                            Refresh();
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // No repository: nothing to watch.
                }
            }

            if (now < nextRefresh || !LoggerWindow.IsOpen)
            {
                return;
            }

            Refresh();
        }

        // Each Git command Mixer Git runs becomes a log: the command and its result as the message, what it printed as
        // the output. Not sent to Unity's console. Called on the thread that ran the command.
        private static void OnCommand(UnitGitCommandRecord record)
        {
            if (record == null)
            {
                return;
            }

            string result = record.TimedOut ? "timed out" : record.ExitCode == 0 ? "ok" : "exit " + record.ExitCode;
            string condition = "[Mixer Git] " + record.CommandLine + "  →  " + result;
            var output = new StringBuilder(LogStoreOutputPrefix);
            output.Append(record.Milliseconds.ToString("0", CultureInfo.InvariantCulture)).Append(" ms in ").Append(record.WorkingDirectory).Append('\n');
            Append(output, record.StandardOutput);
            if (!string.IsNullOrWhiteSpace(record.StandardError))
            {
                output.Append("stderr:\n");
                Append(output, record.StandardError);
            }

            var type = record.TimedOut ? UnityEngine.LogType.Error : record.ExitCode == 0 ? UnityEngine.LogType.Log : UnityEngine.LogType.Warning;
            LogCapture.Inject(condition, output.ToString(), type);
        }

        private const string LogStoreOutputPrefix = StackTraces.OutputPrefix;
        private const int MaxOutput = 4000;

        private static void Append(StringBuilder builder, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            text = text.TrimEnd();
            builder.Append(text.Length > MaxOutput ? text.Substring(0, MaxOutput) + "\n… (" + (text.Length - MaxOutput) + " more characters)" : text).Append('\n');
        }

        private static void Refresh()
        {
            nextRefresh = EditorApplication.timeSinceStartup + RefreshSeconds;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                return;
            }

            string root;
            var markers = new List<TimelineMarker>();
            try
            {
                root = Path.GetFullPath(".");
                if (!GitCli.IsRepository(root))
                {
                    TimelineMarkers.Clear(Source);
                    running = 0;
                    return;
                }

                AddReleases(root, markers);
            }
            catch (Exception)
            {
                running = 0;
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AddCommits(root, markers);
                    TimelineMarkers.Set(Source, markers);
                }
                catch (Exception)
                {
                    // Without Git history the timeline just has no commits.
                }
                finally
                {
                    running = 0;
                }
            });
        }

        private static void AddReleases(string root, List<TimelineMarker> markers)
        {
            if (!File.Exists(UnitGitReleases.GetReleasesFilePath(root)))
            {
                return;
            }

            var file = UnitGitReleases.Load(root);
            foreach (var release in file.releases)
            {
                if (release == null || file.hiddenReleaseIds.Contains(release.id) ||
                    !DateTime.TryParse(release.date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    continue;
                }

                string what = string.IsNullOrEmpty(release.name) ? release.title : release.name;
                string title = (string.IsNullOrEmpty(release.tool) ? "Release" : release.tool + " release") + ": " + what +
                               (string.IsNullOrEmpty(release.version) ? string.Empty : " " + release.version);
                markers.Add(new TimelineMarker(DateTime.SpecifyKind(date, DateTimeKind.Utc), TimelineMarkerKind.Release, title, release.scope));
            }
        }

        private static void AddCommits(string root, List<TimelineMarker> markers)
        {
            var result = GitCli.Run(root, 10000, "log", "-n", "500", "--since=30.days", "--format=%h%x1f%at%x1f%s%x1f%an");
            if (!result.Success)
            {
                return;
            }

            foreach (string line in result.Output.Split('\n'))
            {
                string[] parts = line.TrimEnd('\r').Split('\u001f');
                if (parts.Length < 4 || !long.TryParse(parts[1], out long seconds))
                {
                    continue;
                }

                var time = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                markers.Add(new TimelineMarker(time, TimelineMarkerKind.Commit, parts[2], parts[0] + " · " + parts[3]));
            }
        }
    }
}
