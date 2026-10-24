using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Makes one project report: each part is a step (the scene's in the editor, files and Git on worker threads, so
    /// the editor keeps drawing), the AI diagnosis runs while the scene is exported, and everything is packed in one
    /// zip file. A part that fails is marked and the report is still made; only packing can fail the report. The zip is
    /// written under a temporary name and renamed once complete: a cancel or a script reload leaves nothing behind.
    /// </summary>
    internal sealed class ReportBuilder
    {
        private const string PartialsKey = "Orbiters.Logger.Report.Partials";
        private const string StoppedKey = "Orbiters.Logger.Report.StoppedByReload";

        private readonly ReportContext context;
        private readonly string destination;

        /// <summary>Where the report will be saved.</summary>
        public string Destination => destination;
        // The steps as nested coroutines: a step yielding another one runs it to its end first.
        private readonly Stack<IEnumerator> routine = new Stack<IEnumerator>();
        private readonly ReportStep project;
        private readonly ReportStep logs;
        private readonly ReportStep git;
        private readonly ReportStep scene;
        private readonly ReportStep export;
        private readonly ReportStep diagnosis;
        private readonly ReportStep packing;
        private DiagnosisClient client;
        private SceneFiles sceneFiles;
        private Task working;
        private string failure;
        // Cancel and the zip taking its name exclude each other: once renamed, the report is made.
        private readonly object gate = new object();
        private volatile bool cancelled;
        private bool committed;

        public readonly List<ReportStep> Steps = new List<ReportStep>();

        /// <summary>From 0 to 1, for the progress bar.</summary>
        public float Progress { get; private set; }

        public bool Finished { get; private set; }

        /// <summary>When it finished (editor time).</summary>
        public double FinishedAt { get; private set; }
        public bool Cancelled => cancelled;

        /// <summary>The report file once made.</summary>
        public string Result { get; private set; }

        public long ResultBytes { get; private set; }

        /// <summary>Why the report could not be made.</summary>
        public string Error { get; private set; }

        public Diagnosis Diagnosis => context.Diagnosis;
        public string DiagnosisError => context.DiagnosisError;
        public IReadOnlyList<LogDigestGroup> Groups => context.Logs?.Groups;
        public ReportContext Context => context;

        /// <summary>The diagnosis is being asked again (after a failure), outside the report file.</summary>
        public bool AskingAgain => retry != null;

        /// <summary>The diagnosis asked again is being written into the report file.</summary>
        public bool AddingDiagnosis { get; private set; }

        /// <summary>Why the diagnosis asked again could not go into the report file.</summary>
        public string DiagnosisFileError { get; private set; }

        private DiagnosisClient retry;

        /// <summary>
        /// Asks for the diagnosis again from what the report found (signed in since, or Orbiters was busy). The answer
        /// shows in the window, then goes into the report file.
        /// </summary>
        public void AskAgain()
        {
            if (retry != null || !Finished || AddingDiagnosis)
            {
                return;
            }

            context.Diagnosis = null;
            context.DiagnosisError = null;
            DiagnosisInFile = false;
            DiagnosisFileError = null;
            retry = DiagnosisClient.Start(context);
            EditorApplication.update += PollRetry;
            Changed?.Invoke();
        }

        private void PollRetry()
        {
            if (retry == null || !retry.Poll())
            {
                return;
            }

            EditorApplication.update -= PollRetry;
            context.Diagnosis = retry.Error == null ? retry.Result : null;
            context.DiagnosisError = retry.Error;
            retry = null;
            if (context.Diagnosis != null)
            {
                Complete(diagnosis, LoggerUi.Plural(context.Diagnosis.causes?.Length ?? 0, "likely cause"));
                AddDiagnosisToFile();
            }

            Changed?.Invoke();
        }

        /// <summary>Whether the report file holds the diagnosis shown (false while it is being added).</summary>
        public bool DiagnosisInFile { get; private set; }

        // The diagnosis asked again goes into the report file too: diagnosis.md and a README that includes it. The zip
        // is written again beside the old one (streamed, never held in memory) and swapped in once complete.
        private void AddDiagnosisToFile()
        {
            if (Result == null || !File.Exists(Result))
            {
                DiagnosisFileError = "The report file was moved or deleted.";
                return;
            }

            const string name = "diagnosis.md";
            if (!context.Entries.Any(e => e.Name == name))
            {
                context.Add(name, "What Orbiters' AI thinks is wrong, and how to fix it");
            }

            var texts = new Dictionary<string, string>
            {
                [name] = context.Privacy.Mask(DiagnosisClient.Markdown(context.Diagnosis, context.Logs?.Groups)),
                ["README.md"] = context.Privacy.Mask(ReportReadme.Write(context, Steps))
            };
            string path = Result;
            Track(ReportZip.PartialOf(path));
            AddingDiagnosis = true;
            var update = Task.Run(() =>
            {
                ReportZip.Rewrite(path, texts);
                return new FileInfo(path).Length;
            });
            void Wait()
            {
                if (!update.IsCompleted)
                {
                    return;
                }

                EditorApplication.update -= Wait;
                AddingDiagnosis = false;
                if (update.Status == TaskStatus.RanToCompletion)
                {
                    ResultBytes = update.Result;
                    DiagnosisInFile = true;
                }
                else
                {
                    DiagnosisFileError = "It could not be added to the report file (" + (update.Exception?.GetBaseException().Message ?? "stopped") + ").";
                }

                Changed?.Invoke();
            }

            EditorApplication.update += Wait;
        }

        public event Action Changed;

        private ReportBuilder(ReportContext context, string destination)
        {
            this.context = context;
            this.destination = destination;
            project = Add("Project details", LoggerGlyph.Info, true);
            logs = Add("Logs", LoggerGlyph.List, context.Has(ReportPart.Logs));
            git = Add("Git history", LoggerGlyph.Branch, context.Has(ReportPart.Git));
            scene = Add("Scene description", LoggerGlyph.Hierarchy, context.Has(ReportPart.Scene));
            diagnosis = Add("AI diagnosis", LoggerGlyph.Sparkle, context.Has(ReportPart.Diagnosis));
            export = Add("Scene export", LoggerGlyph.Package, context.Has(ReportPart.SceneExport));
            packing = Add("Packing the report", LoggerGlyph.Folder, true);
            if (context.Anonymize && context.Has(ReportPart.SceneExport))
            {
                export.State = ReportStepState.Skipped;
                export.Detail = "Left out: a Unity package can't be anonymized";
            }

            routine.Push(Run());
        }

        private ReportStep Add(string title, LoggerGlyph glyph, bool included)
        {
            var step = new ReportStep(title, glyph);
            if (!included)
            {
                step.State = ReportStepState.Skipped;
                step.Detail = "Not included";
            }

            Steps.Add(step);
            return step;
        }

        /// <summary>Starts a report of <paramref name="parts"/> saved in <paramref name="folder"/>.</summary>
        internal static ReportBuilder Start(IEnumerable<ReportPart> parts, string note, string folder, bool anonymize)
        {
            string staging = Path.Combine(Path.GetFullPath("Temp"), "OrbitersReport-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            var context = new ReportContext(staging, ReportPrivacy.ForThisComputer(anonymize), note, parts);
            var builder = new ReportBuilder(context, ReportOptions.FreePath(folder, context.CreatedLocal));
            EditorApplication.update += builder.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += builder.OnBeforeReload;
            return builder;
        }

        public void Cancel()
        {
            if (Finished)
            {
                return;
            }

            lock (gate)
            {
                if (committed)
                {
                    // Already saved under its name: the report ends as made on the next frame.
                    return;
                }

                cancelled = true;
            }

            Error = "Cancelled";
            End();
        }

        // A script reload ends the domain this report runs in: it stops, and the window says so after the reload. The zip
        // may already have taken its name (renamed just before the reload, the last frame not run yet): then it is saved.
        private void OnBeforeReload()
        {
            if (!Finished)
            {
                string saved = File.Exists(destination) ? destination : string.Empty;
                SessionState.SetString(StoppedKey, DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + saved);
            }

            Cancel();
        }

        /// <summary>
        /// Once after a script reload ended a report in the last minutes (the window says so when it opens): false when
        /// there is nothing to say; <paramref name="saved"/> is the report file when it was complete, else null.
        /// </summary>
        internal static bool TakeReloadNotice(out string saved)
        {
            saved = null;
            string notice = SessionState.GetString(StoppedKey, string.Empty);
            if (notice.Length == 0)
            {
                return false;
            }

            SessionState.EraseString(StoppedKey);
            int bar = notice.IndexOf('|');
            string stamp = bar < 0 ? notice : notice.Substring(0, bar);
            string path = bar < 0 ? string.Empty : notice.Substring(bar + 1);
            if (!long.TryParse(stamp, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ticks) ||
                DateTime.UtcNow.Ticks - ticks >= TimeSpan.TicksPerMinute * 10)
            {
                return false;
            }

            saved = path.Length > 0 && File.Exists(path) ? path : null;
            return true;
        }

        private bool TryCommit()
        {
            lock (gate)
            {
                if (cancelled)
                {
                    return false;
                }

                committed = true;
                return true;
            }
        }

        private void Tick()
        {
            if (Finished)
            {
                return;
            }

            try
            {
                var current = routine.Peek();
                if (current.MoveNext())
                {
                    if (current.Current is IEnumerator inner)
                    {
                        routine.Push(inner);
                    }
                }
                else
                {
                    routine.Pop();
                    if (routine.Count == 0)
                    {
                        End();
                    }
                }
            }
            catch (Exception exception)
            {
                Error = exception.Message;
                Fail(Steps.FirstOrDefault(s => s.State == ReportStepState.Running) ?? packing, exception.Message);
                End();
            }

            Changed?.Invoke();
        }

        private void End()
        {
            if (Finished)
            {
                return;
            }

            Finished = true;
            FinishedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            client?.Dispose();
            foreach (var step in Steps.Where(s => s.State == ReportStepState.Running || s.State == ReportStepState.Waiting))
            {
                step.State = ReportStepState.Skipped;
                step.Detail = Cancelled ? "Cancelled" : step.Detail;
            }

            // After the work still running (a cancelled packing stops at its next file): it reads that folder.
            string staging = context.Staging;
            (working ?? Task.CompletedTask).ContinueWith(_ => Delete(staging));
            Changed?.Invoke();
        }

        private IEnumerator Run()
        {
            // Project details (main thread: Unity's settings).
            Begin(project, 0.02f);
            yield return null;
            context.Project = Guard(project, ProjectFacts.Gather);
            if (context.Project != null)
            {
                yield return Work(project, () =>
                {
                    context.Project.Write(context);
                    return "Unity " + context.Project.Unity + " · " + LoggerUi.Plural(context.Project.Packages.Count, "package");
                }, 0.02f, 0.06f);
            }

            // Logs: the digest feeds the README and the AI only when the logs are part of the report.
            if (context.Has(ReportPart.Logs))
            {
                Begin(logs, 0.06f);
                yield return null;
                LogReport.Snapshot snapshot = null;
                context.Logs = Guard(logs, () =>
                {
                    var digest = LogReport.Digest(LogCapture.Store, context.Privacy);
                    snapshot = LogReport.Take(LogCapture.Store);
                    return digest;
                });
                if (snapshot != null)
                {
                    yield return Work(logs, () => LogReport.Write(context, snapshot, p => Progress = Mathf.Lerp(0.06f, 0.36f, p)), 0.06f, 0.36f);
                }
            }

            // Git (worker: Git commands).
            if (context.Has(ReportPart.Git))
            {
                Begin(git, 0.36f);
                string root = GitCli.ProjectRoot;
                var extras = ReportOptions.CollectGitExtras(root);
                yield return Work(git, () => GitReport.Write(context, root, extras, p => Progress = Mathf.Lerp(0.36f, 0.5f, p)), 0.36f, 0.5f);
            }

            // Scene description: the hierarchy on the main thread, the file tree on a worker.
            if (context.Has(ReportPart.Scene))
            {
                Begin(scene, 0.5f);
                yield return null;
                string summary = Guard(scene, () => SceneReport.WriteHierarchy(context));
                if (summary != null)
                {
                    scene.Detail = summary + " · reading the files it uses…";
                    Changed?.Invoke();
                    yield return null;
                    var files = Guard(scene, () => SceneReport.Dependencies(context.Scene));
                    if (files != null)
                    {
                        yield return Work(scene, () =>
                        {
                            SceneReport.Measure(files);
                            SceneReport.WriteFiles(context, files);
                            sceneFiles = files;
                            return summary + " · " + LoggerUi.Plural(files.Paths.Length, "file") + " used";
                        }, 0.5f, 0.6f);
                    }
                }
            }

            // The AI reads what the steps found while the scene is exported.
            if (context.Has(ReportPart.Diagnosis))
            {
                Begin(diagnosis, 0.6f);
                diagnosis.Detail = "Orbiters is reading the report…";
                client = DiagnosisClient.Start(context);
            }

            if (context.Has(ReportPart.SceneExport) && !context.Anonymize)
            {
                Begin(export, 0.62f);
                export.Detail = "Exporting with every file it uses…";
                Changed?.Invoke();
                // Two frames: the step shows as running before Unity's export holds the editor.
                yield return null;
                yield return null;
                string done = Guard(export, () =>
                {
                    // The scenes to export without making them part of the report's description (that part may be off).
                    var digest = context.Has(ReportPart.Scene) && context.Scene != null ? context.Scene : SceneReport.Look();
                    var files = sceneFiles;
                    if (files == null)
                    {
                        files = SceneReport.Dependencies(digest);
                        SceneReport.Measure(files);
                    }

                    ReportZip.EnsureSpace(context.Staging, files.Bytes, "the scene export");
                    return SceneReport.Export(context, digest);
                });
                if (done != null)
                {
                    Complete(export, done);
                }
            }

            if (client != null)
            {
                Progress = Mathf.Max(Progress, 0.85f);
                double started = EditorApplication.timeSinceStartup;
                while (!client.Poll())
                {
                    diagnosis.Detail = "Orbiters is reading the report… " + (int)(EditorApplication.timeSinceStartup - started) + " s";
                    yield return null;
                }

                context.Diagnosis = client.Result;
                context.DiagnosisError = client.Error;
                if (client.Result != null && client.Error == null)
                {
                    context.WriteText("diagnosis.md", DiagnosisClient.Markdown(client.Result, context.Logs?.Groups), "What Orbiters' AI thinks is wrong, and how to fix it");
                    DiagnosisInFile = true;
                    Complete(diagnosis, LoggerUi.Plural(client.Result.causes?.Length ?? 0, "likely cause"));
                }
                else
                {
                    Fail(diagnosis, client.Error);
                }
            }

            // Packing: the README, then the zip (worker), written beside its final name and renamed once complete.
            Begin(packing, 0.9f);
            Track(ReportZip.PartialOf(destination));
            bool packed = false;
            yield return Work(packing, () =>
            {
                context.WriteText("README.md", ReportReadme.Write(context, Steps), "This file");
                ReportZip.EnsureSpace(Path.GetDirectoryName(destination), ReportZip.SizeOf(context.Staging), "the report");
                packed = ReportZip.Pack(context.Staging, destination, () => cancelled, TryCommit, p => Progress = Mathf.Lerp(0.9f, 1f, p));
                if (!packed)
                {
                    return "Cancelled";
                }

                ResultBytes = new FileInfo(destination).Length;
                return SceneReport.Size(ResultBytes);
            }, 0.9f, 1f);

            if (packing.State == ReportStepState.Failed)
            {
                Error = failure;
                yield break;
            }

            if (packed)
            {
                Result = destination;
                Progress = 1f;
            }
        }

        private void Begin(ReportStep step, float progress)
        {
            step.State = ReportStepState.Running;
            Progress = Mathf.Max(Progress, progress);
            Changed?.Invoke();
        }

        private void Complete(ReportStep step, string detail)
        {
            step.State = ReportStepState.Done;
            step.Detail = detail ?? string.Empty;
            Changed?.Invoke();
        }

        private void Fail(ReportStep step, string error)
        {
            step.State = ReportStepState.Failed;
            step.Detail = string.IsNullOrEmpty(error) ? "Failed" : error;
            failure = step.Detail;
            Changed?.Invoke();
        }

        // Runs the main-thread part of a step; a failure marks the step and returns null, and the report goes on.
        private T Guard<T>(ReportStep step, Func<T> action) where T : class
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                Fail(step, exception.Message);
                return null;
            }
        }

        // Runs a step's work on a worker thread, waiting a frame at a time.
        private IEnumerator Work(ReportStep step, Func<string> work, float from, float to)
        {
            var task = Task.Run(work);
            working = task;
            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            while (!task.IsCompleted)
            {
                if (Cancelled)
                {
                    yield break;
                }

                yield return null;
            }

            if (task.IsFaulted)
            {
                var error = task.Exception?.GetBaseException();
                Fail(step, error?.Message ?? "Failed");
            }
            else
            {
                Complete(step, task.Result);
            }

            Progress = Mathf.Max(Progress, to);
        }

        // Partial zips being written, so the next domain removes those a script reload cut short.
        private static void Track(string partial)
        {
            string tracked = SessionState.GetString(PartialsKey, string.Empty);
            SessionState.SetString(PartialsKey, tracked.Length == 0 ? partial : tracked + "\n" + partial);
        }

        [InitializeOnLoadMethod]
        private static void CleanUpAfterReload()
        {
            string tracked = SessionState.GetString(PartialsKey, string.Empty);
            if (tracked.Length > 0)
            {
                var left = new List<string>();
                foreach (string partial in tracked.Split('\n'))
                {
                    ReportZip.TryDelete(partial);
                    if (File.Exists(partial))
                    {
                        left.Add(partial);
                    }
                }

                SessionState.SetString(PartialsKey, string.Join("\n", left));
            }

            // Staging folders a reload or a crash left in Temp (Unity empties Temp only when it closes).
            string temp;
            try
            {
                temp = Path.GetFullPath("Temp");
            }
            catch (Exception)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    foreach (string folder in Directory.Exists(temp) ? Directory.GetDirectories(temp, "OrbitersReport-*") : Array.Empty<string>())
                    {
                        Delete(folder);
                    }
                }
                catch (Exception)
                {
                    // Left for Unity to empty.
                }
            });
        }

        private static void Delete(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && folder.Contains("OrbitersReport-"))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception)
            {
                // Unity empties Temp when it closes.
            }
        }
    }
}
