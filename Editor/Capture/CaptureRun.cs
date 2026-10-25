using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbiters.Contracts;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>
    /// One capture: renders the path's frames from editor updates within a time budget (the editor never stalls for
    /// long), hands them to the background writer, writes <c>groundtruth/poses.jsonl</c> as it goes and
    /// <c>capture.json</c> at the end, then runs the self-check on worker threads. Shows in Unity's progress bar with
    /// Cancel. A cancelled capture keeps the frames it rendered, as a shorter valid capture.
    /// </summary>
    internal sealed class CaptureRun
    {
        internal enum Phase { Rendering, Writing, Checking, Done, Cancelled, Failed }

        /// <summary>Main-thread time per editor update spent rendering (at least one frame when the writer has room).</summary>
        internal const double BudgetMs = 24.0;

        /// <summary>The capture in progress or the last one started from the editor (tests drive their own), for windows that open later.</summary>
        public static CaptureRun Current { get; private set; }

        public readonly CaptureSettings Settings;
        public readonly string Folder, SceneName, ScenePath;
        public readonly int FrameCount;
        public Phase State { get; private set; }
        public int Rendered { get; private set; }
        public int Written => writer?.Written ?? Rendered;
        public long Bytes { get; private set; }
        public string Error { get; private set; }
        public string PostNote { get; private set; }
        public CaptureSelfCheck.Result Check { get; private set; }
        public int CheckTotal { get; private set; }
        public int CheckDone => Volatile.Read(ref checkDone);
        public TimeSpan Elapsed => clock.Elapsed;
        public bool Busy => State == Phase.Rendering || State == Phase.Writing || State == Phase.Checking;

        /// <summary>Raised on the main thread after each rendered colour frame (not depth frames), with the colour target (previews).</summary>
        public event Action<RenderTexture> FrameRendered;
        /// <summary>Raised on the main thread when the phase changes.</summary>
        public event Action<CaptureRun> Changed;

        private readonly CapturePose[] poses;
        private readonly Scene? previewScene;
        private readonly uint[][] words;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly int threads;
        private CaptureRig rig;
        private CaptureWriter writer;
        private StreamWriter posesOut;
        private Task<CaptureSelfCheck.Result> checking;
        private int progressId = -1, checkDone;
        private bool cancelRequested, pumped;

        private CaptureRun(CaptureSettings settings, CapturePose[] poses, string folder, Scene? previewScene)
        {
            Settings = settings.Clone();
            this.poses = poses;
            this.previewScene = previewScene;
            FrameCount = poses.Length;
            Folder = folder;
            var scene = previewScene ?? SceneManager.GetActiveScene();
            SceneName = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
            ScenePath = string.IsNullOrEmpty(scene.path) ? null : scene.path;
            words = new uint[FrameCount][];
            threads = Mathf.Clamp(SystemInfo.processorCount / 4, 1, 4);
        }

        /// <summary>
        /// Starts a capture of <paramref name="poses"/> (one per frame) into a new <paramref name="folder"/>. With
        /// <paramref name="pump"/> it advances from editor updates; otherwise call <see cref="RunToEnd"/> (tests).
        /// </summary>
        public static CaptureRun Start(CaptureSettings settings, CapturePose[] poses, string folder, Scene? previewScene = null, bool pump = true)
        {
            if (poses == null || poses.Length == 0) throw new ArgumentException("The camera path has no frames.");
            if (pump && Current != null && Current.Busy) throw new InvalidOperationException("A capture is already running.");
            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0) throw new IOException("The capture folder is not empty: " + folder);
            var run = new CaptureRun(settings, poses, folder, previewScene);
            try
            {
                run.rig = new CaptureRig(run.Settings, previewScene);
                run.PostNote = run.rig.PostNote;
                run.writer = new CaptureWriter(folder, run.Settings.Width, run.Settings.Height, run.threads);
                run.posesOut = new StreamWriter(Path.Combine(folder, RawCapture.PosesFile), false, new UTF8Encoding(false)) { NewLine = "\n" };
            }
            catch
            {
                run.rig?.Dispose();
                run.writer?.Dispose();
                run.posesOut?.Dispose();
                throw;
            }
            if (pump)
            {
                Current = run;
                run.pumped = true;
                run.progressId = Progress.Start("Mixer capture", Path.GetFileName(folder), Progress.Options.None);
                Progress.RegisterCancelCallback(run.progressId, () => { run.Cancel(); return true; });
                EditorApplication.update += run.Tick;
            }
            return run;
        }

        /// <summary>Stops rendering; the frames already rendered are written and kept.</summary>
        public void Cancel()
        {
            if (!Busy) return;
            cancelRequested = true;
            cancel.Cancel();
        }

        /// <summary>Runs the whole capture now, blocking the caller (tests). False on timeout.</summary>
        public bool RunToEnd(TimeSpan timeout)
        {
            var limit = Stopwatch.StartNew();
            while (Busy)
            {
                Step(BudgetMs);
                if (limit.Elapsed > timeout)
                {
                    Fail(new TimeoutException("The capture took longer than " + timeout.TotalSeconds + " s."), false);
                    return false;
                }
                if (State != Phase.Rendering) Thread.Sleep(5);
            }
            return true;
        }

        private void Tick()
        {
            try
            {
                Step(BudgetMs);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>Advances the capture by up to <paramref name="budgetMs"/> of main-thread work.</summary>
        public void Step(double budgetMs)
        {
            switch (State)
            {
                case Phase.Rendering:
                    RenderSome(budgetMs);
                    break;
                case Phase.Writing:
                    if (writer.Error != null) Fail(writer.Error);
                    else if (writer.Finished) FinishWriting();
                    else Report(0.85f + 0.1f * writer.Written / Mathf.Max(1, Rendered), "Writing " + writer.Written + " / " + Rendered);
                    break;
                case Phase.Checking:
                    if (checking.IsCompleted) FinishChecking();
                    else Report(0.95f + 0.05f * CheckDone / Mathf.Max(1, CheckTotal), "Checking " + CheckDone + " / " + CheckTotal);
                    break;
            }
        }

        private void RenderSome(double budgetMs)
        {
            if (cancelRequested)
            {
                StopRendering();
                return;
            }
            if (writer.Error != null)
            {
                Fail(writer.Error);
                return;
            }
            var budget = Stopwatch.StartNew();
            while (Rendered < FrameCount && writer.HasRoom)
            {
                int index = Rendered;
                bool depthFrame = Settings.IsDepthFrame(index);
                var frame = rig.Render(index, poses[index], Settings.TimeOf(index), depthFrame, writer.RentColour(), writer.RentDepth(), Settings.IsRigFrame(index));
                words[index] = frame.Payload.ToWords();
                posesOut.WriteLine(JsonConvert.SerializeObject(new GroundTruthPose
                {
                    Index = index,
                    Time = frame.Time,
                    CameraToWorld = frame.Payload.CameraToWorld,
                    Projection = frame.Payload.Projection,
                    Near = frame.Payload.Near,
                    Far = frame.Payload.Far,
                    DepthFrame = depthFrame,
                }, Formatting.None));
                writer.Add(frame);
                Rendered++;
                if (!depthFrame) FrameRendered?.Invoke(rig.Target);
                if (budget.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
            Report(0.85f * Rendered / FrameCount, "Frame " + Rendered + " / " + FrameCount);
            if (Rendered == FrameCount) StopRendering();
        }

        private void StopRendering()
        {
            rig?.Dispose();
            rig = null;
            posesOut?.Dispose();
            posesOut = null;
            writer.Complete();
            SetState(Phase.Writing);
        }

        private void FinishWriting()
        {
            Bytes = writer.Bytes;
            WriteCaptureJson();
            if (cancelRequested || Rendered == 0)
            {
                Finish(Phase.Cancelled);
                return;
            }
            // Rig frames' strips come from the GPU's normalised axes (the last bit may differ from the CPU's): not checked bit for bit.
            var frames = CaptureSelfCheck.Sample(Rendered, Settings.IsDepthFrame).Where(i => !Settings.IsRigFrame(i)).ToArray();
            CheckTotal = frames.Length;
            var expectedDepth = writer;
            checking = Task.Run(() => CaptureSelfCheck.Run(Folder, frames, i => words[i], expectedDepth.ExpectedDepthCodes, threads, cancel.Token,
                n => Volatile.Write(ref checkDone, n)));
            SetState(Phase.Checking);
        }

        private void FinishChecking()
        {
            if (checking.IsFaulted && !(checking.Exception?.InnerException is OperationCanceledException))
            {
                Fail(checking.Exception?.InnerException ?? checking.Exception);
                return;
            }
            if (checking.Status == TaskStatus.RanToCompletion) Check = checking.Result;
            Finish(cancelRequested ? Phase.Cancelled : Phase.Done);
            if (Check != null)
                Debug.Log("[Mixer capture] " + FrameCount + " frames of " + SceneName + " at " + Settings.Width + "×" + Settings.Height + ", post " +
                          CaptureSettings.PostId(Settings.Post) + " → " + Folder + "\nSelf-check: " + Check + (Check.Problems.Count > 0 ? "\n" + string.Join("\n", Check.Problems) : ""));
        }

        private void WriteCaptureJson()
        {
            var capture = new RawCapture
            {
                CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Scene = ScenePath,
                World = new WorldInfo { Name = SceneName },
                Width = Settings.Width,
                Height = Settings.Height,
                Fps = Settings.Fps,
                FrameCount = Rendered,
                DepthEvery = Settings.DepthEvery,
                PostProfile = CaptureSettings.PostId(Settings.Post),
                HasGroundTruth = true,
            };
            File.WriteAllText(Path.Combine(Folder, RawCapture.FileName), JsonConvert.SerializeObject(capture, Formatting.Indented) + "\n", new UTF8Encoding(false));
        }

        private void Fail(Exception e, bool log = true)
        {
            Error = e.Message;
            if (log) Debug.LogException(e);
            cancel.Cancel();
            rig?.Dispose();
            rig = null;
            posesOut?.Dispose();
            posesOut = null;
            writer?.Abort();
            Finish(Phase.Failed);
        }

        private void Finish(Phase phase)
        {
            clock.Stop();
            if (writer != null) Bytes = Math.Max(Bytes, writer.Bytes);
            writer?.Dispose();
            writer = null;
            if (pumped)
            {
                EditorApplication.update -= Tick;
                if (progressId >= 0 && Progress.Exists(progressId))
                    Progress.Finish(progressId, phase == Phase.Done ? Progress.Status.Succeeded : phase == Phase.Failed ? Progress.Status.Failed : Progress.Status.Canceled);
                progressId = -1;
            }
            SetState(phase);
        }

        private void SetState(Phase phase)
        {
            State = phase;
            Changed?.Invoke(this);
        }

        private void Report(float progress, string text)
        {
            if (progressId >= 0 && Progress.Exists(progressId)) Progress.Report(progressId, Mathf.Clamp01(progress), text);
        }

        /// <summary>A domain reload is about to drop the managed side: release the GPU objects and stop the workers first.</summary>
        [InitializeOnLoadMethod]
        private static void HookReload() => AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            var run = Current;
            if (run == null || !run.Busy) return;
            run.cancelRequested = true;
            run.Fail(new OperationCanceledException("Scripts were recompiled during the capture."), false);
            Debug.LogWarning("[Mixer capture] Stopped by a script reload: " + run.Folder + " is incomplete.");
        };
    }
}
