using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Orbiters.Logger.Editor
{
    /// <summary>What Orbiters' AI made of a report: a headline, a summary and the likely causes with their fixes.</summary>
    [Serializable]
    internal sealed class Diagnosis
    {
        public string headline;
        public string summary;
        public DiagnosisCause[] causes = Array.Empty<DiagnosisCause>();
    }

    [Serializable]
    internal sealed class DiagnosisCause
    {
        public string title;
        public string likelihood;
        public string explanation;
        public string[] steps = Array.Empty<string>();
        public string[] related = Array.Empty<string>();
    }

    /// <summary>
    /// Asks Orbiters for the AI diagnosis of a report (<c>POST logger/diagnosis</c>, signed-in members); Orbiters passes
    /// it to a third-party AI provider. It sends the note and the project facts, and from the parts the report includes
    /// the most important log messages (Logs), the Git state (Git history) and the scene numbers (Scene description),
    /// all masked; never files. Started on the main thread, polled with <see cref="Poll"/>.
    /// </summary>
    internal sealed class DiagnosisClient : IDisposable
    {
        // Longer than the server gives the AI (up to 180 s): giving up first would leave the server answering a request
        // nobody reads, and an immediate "Ask again" would only hear that the previous one is still running.
        private const int TimeoutSeconds = 200;
        private UnityWebRequest request;

        public bool Done { get; private set; }
        public Diagnosis Result { get; private set; }
        public string Error { get; private set; }

        public static DiagnosisClient Start(ReportContext context)
        {
            var client = new DiagnosisClient();
            string token = OrbitersLink.SafeToken();
            if (string.IsNullOrEmpty(token))
            {
                client.Finish(null, "Connect your Orbiters account to get the AI diagnosis.");
                return client;
            }

            try
            {
                byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(BuildRequest(context)));
                client.request = new UnityWebRequest(OrbitersLink.ApiUrl("logger/diagnosis"), UnityWebRequest.kHttpVerbPOST)
                {
                    uploadHandler = new UploadHandlerRaw(body) { contentType = "application/json" },
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = TimeoutSeconds
                };
                client.request.SetRequestHeader("Content-Type", "application/json");
                client.request.SetRequestHeader("Authorization", "Bearer " + token);
                client.request.SendWebRequest();
            }
            catch (Exception exception)
            {
                client.Finish(null, "Could not reach Orbiters: " + exception.Message);
            }

            return client;
        }

        /// <summary>True once an answer (or a failure) arrived.</summary>
        public bool Poll()
        {
            if (Done || request == null || !request.isDone)
            {
                return Done;
            }

            try
            {
                string text = request.downloadHandler?.text;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var diagnosis = JsonUtility.FromJson<Diagnosis>(text);
                    Finish(diagnosis, diagnosis == null || string.IsNullOrWhiteSpace(diagnosis.summary) && (diagnosis.causes?.Length ?? 0) == 0
                        ? "Orbiters' AI did not find an answer this time."
                        : null);
                }
                else
                {
                    Finish(null, ErrorOf(request.responseCode, text, request.error));
                }
            }
            catch (Exception)
            {
                Finish(null, "Orbiters answered in a way the Mixer console can't read. Update Mixer.");
            }

            return true;
        }

        private void Finish(Diagnosis result, string error)
        {
            Result = result;
            Error = error;
            Done = true;
            request?.Dispose();
            request = null;
        }

        public void Dispose()
        {
            request?.Abort();
            request?.Dispose();
            request = null;
        }

        [Serializable]
        private sealed class ErrorBody
        {
            public string error;
        }

        internal static string ErrorOf(long status, string body, string transport)
        {
            string message = null;
            try
            {
                message = string.IsNullOrEmpty(body) ? null : JsonUtility.FromJson<ErrorBody>(body)?.error;
            }
            catch (Exception)
            {
                // Not JSON: an error page.
            }

            // 401 is always the sign-in; a 403 with a reason (AI features turned off in the account) says it.
            if (status == 401 || (status == 403 && string.IsNullOrEmpty(message)))
            {
                return "Your Orbiters sign-in expired. Connect again to get the AI diagnosis.";
            }

            if (status == 429)
            {
                return "Too many diagnoses in a short time. Try again in a while.";
            }

            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }

            return status == 0 ? "Could not reach Orbiters (" + transport + ")." : "Orbiters could not make a diagnosis (" + status + ").";
        }

        // ---- Request --------------------------------------------------------------------------------------------

#pragma warning disable 0649, 0414
        [Serializable]
        internal sealed class Request
        {
            public string note;
            public ProjectPart project = new ProjectPart();
            public LogsPart logs = new LogsPart();
            public GitPart git;
            public ScenePart scene;
        }

        [Serializable]
        internal sealed class ProjectPart
        {
            public string name;
            public string unity;
            public string platform;
            public string os;
            public string renderPipeline;
            public PackagePart[] packages = Array.Empty<PackagePart>();
        }

        [Serializable]
        internal sealed class PackagePart
        {
            public string name;
            public string version;
        }

        [Serializable]
        internal sealed class LogsPart
        {
            public int total;
            public int errors;
            public int warnings;
            public GroupPart[] groups = Array.Empty<GroupPart>();
        }

        [Serializable]
        internal sealed class GroupPart
        {
            public string id;
            public string level;
            public int count;
            public string source;
            public string text;
            public string stack;
            public string firstSeen;
            public string lastSeen;
            public string known;
        }

        [Serializable]
        internal sealed class GitPart
        {
            public string branch;
            public int changedFiles;
            public string[] recent = Array.Empty<string>();
        }

        [Serializable]
        internal sealed class ScenePart
        {
            public string name;
            public int objects;
            public int missingScripts;
            public int inactive;
            public string[] roots = Array.Empty<string>();
        }
#pragma warning restore 0649, 0414

        /// <summary>
        /// The request body, every text masked and cut to what the server accepts. Only the parts the report includes go
        /// in: logs with Logs, the Git state with Git history, the scene with Scene description.
        /// </summary>
        internal static Request BuildRequest(ReportContext context)
        {
            string M(string text, int max) => context.Privacy.MaskAndClip(text ?? string.Empty, max);
            var request = new Request { note = M(context.Note, 2000) };
            var project = context.Project;
            if (project != null)
            {
                request.project = new ProjectPart
                {
                    name = M(project.Name, 120),
                    unity = M(project.Unity, 40),
                    platform = M(project.Platform, 60),
                    os = M(project.OperatingSystem, 160),
                    renderPipeline = M(project.RenderPipeline, 80),
                    packages = project.Packages.Take(250).Select(p => new PackagePart { name = M(p.Key, 120), version = M(p.Value, 80) }).ToArray()
                };
            }

            var logs = context.Has(ReportPart.Logs) ? context.Logs : null;
            if (logs != null)
            {
                request.logs = new LogsPart
                {
                    total = logs.Total,
                    errors = logs.Errors,
                    warnings = logs.Warnings,
                    groups = logs.Groups.Take(40).Select(g => new GroupPart
                    {
                        id = g.Id,
                        level = g.Level == LogLevel.Error ? "error" : g.Level == LogLevel.Warning ? "warning" : "log",
                        count = g.Count,
                        source = M(g.Source, 80),
                        text = M(g.Text, 1500),
                        stack = M(g.Stack, 2000),
                        firstSeen = LoggerUi.Moment(g.FirstSeen),
                        lastSeen = LoggerUi.Moment(g.LastSeen),
                        known = M(g.Known, 200)
                    }).ToArray()
                };
            }

            var git = context.Has(ReportPart.Git) ? context.Git : null;
            request.git = git != null && git.Repository && git.Error == null
                ? new GitPart { branch = M(git.Branch, 160), changedFiles = git.Changed, recent = git.Recent.Take(15).Select(s => M(s, 200)).ToArray() }
                : new GitPart { branch = git == null ? "(not included)" : git.Repository ? "(Git did not answer)" : "(no Git repository)", recent = Array.Empty<string>() };
            var scene = context.Has(ReportPart.Scene) ? context.Scene : null;
            request.scene = scene != null
                ? new ScenePart
                {
                    name = M(string.Join(", ", scene.Names), 160),
                    objects = scene.Objects,
                    missingScripts = scene.MissingScripts,
                    inactive = scene.Inactive,
                    roots = scene.Roots.Take(40).Select(r => M(r, 120)).ToArray()
                }
                : new ScenePart { name = "(not included)", roots = Array.Empty<string>() };
            return request;
        }

        // ---- Markdown -------------------------------------------------------------------------------------------

        /// <summary>
        /// The diagnosis as Markdown, the related log groups quoted under each cause. The AI's words are escaped: a log
        /// that talked the AI into writing a link or HTML still shows as plain text.
        /// </summary>
        internal static string Markdown(Diagnosis diagnosis, IReadOnlyList<LogDigestGroup> groups, int level = 1)
        {
            var text = new StringBuilder();
            string heading = new string('#', level) + " ";
            string causeHeading = new string('#', level + 1) + " ";
            text.Append(heading).Append("AI diagnosis\n\n");
            if (!string.IsNullOrWhiteSpace(diagnosis.headline))
            {
                text.Append("**").Append(MarkdownText.Escape(OneLine(diagnosis.headline))).Append("**\n\n");
            }

            if (!string.IsNullOrWhiteSpace(diagnosis.summary))
            {
                text.Append(MarkdownText.Escape(diagnosis.summary.Trim())).Append("\n\n");
            }

            int number = 1;
            foreach (var cause in diagnosis.causes ?? Array.Empty<DiagnosisCause>())
            {
                text.Append(causeHeading).Append(number++).Append(". ").Append(MarkdownText.Escape(OneLine(cause.title))).Append("  (")
                    .Append(Likelihood(cause.likelihood)).Append(" likelihood)\n\n");
                if (!string.IsNullOrWhiteSpace(cause.explanation))
                {
                    text.Append(MarkdownText.Escape(cause.explanation.Trim())).Append("\n\n");
                }

                int step = 1;
                foreach (string line in cause.steps ?? Array.Empty<string>())
                {
                    text.Append(step++).Append(". ").Append(MarkdownText.Escape(OneLine(line))).Append('\n');
                }

                var related = (cause.related ?? Array.Empty<string>()).Select(id => groups?.FirstOrDefault(g => g.Id == id)).Where(g => g != null).ToList();
                if (related.Count > 0)
                {
                    text.Append("\nLogs this explains:\n");
                    foreach (var group in related)
                    {
                        text.Append("- `").Append(FirstLine(group.Text)).Append("` ×").Append(group.Count).Append('\n');
                    }
                }

                text.Append('\n');
            }

            text.Append("_Written by Orbiters' AI from this report's logs and project facts. It can be wrong: check before you change your project._\n");
            return text.ToString();
        }

        /// <summary>One of high, medium or low, whatever the answer held.</summary>
        internal static string Likelihood(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "high" || value == "low" ? value : "medium";
        }

        private static string OneLine(string text) => (text ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');

        internal static string FirstLine(string text)
        {
            text = (text ?? string.Empty).Trim();
            int end = text.IndexOf('\n');
            return LogReport.Clip(end >= 0 ? text.Substring(0, end).TrimEnd('\r') : text, 160).Replace('`', '\'');
        }
    }
}
