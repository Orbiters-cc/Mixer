using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Opt-in timings of real strokes (EditorPrefs <c>Orbiters.Mixer.Diagnostics.StrokeTimings</c>), one line each in
    /// <c>Library/Orbiters/Mixer/stroke-timings.log</c>: from the press to the first move, to the first upload and to the
    /// first frame the skinned renderer really shows it (its skinned vertices read back from the GPU), the frames of the
    /// stroke and the garbage collections during it and since the last stroke. What a stroke feels like depends on the
    /// whole editor, which a benchmark of the brush alone does not see.
    /// </summary>
    internal static class StrokeTimings
    {
        private const string Pref = "Orbiters.Mixer.Diagnostics.StrokeTimings";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static bool? enabled;
        private static bool active;
        private static double down, firstMove, firstShown, firstVisible, lastFrame, longest, total;
        private static int frames, collectionsAtDown, collectionsAtLastEnd = -1;
        // The skinned vertex watched on the GPU: its value before the stroke moved it, and what read-backs found since.
        private static SkinnedMeshRenderer watched;
        private static int watchedVertex = -1, readbacks, readbacksBack;
        private static Vector3 skinnedBefore, skinnedLast;
        // Through the whole stroke: uploads, changes seen on the GPU, and the longest wait for one while uploads went on.
        private static int uploads, seenChanges, uploadsSinceSeen;
        private static double lastSeen, longestUnseen;
        private static bool haveBefore, reading;

        public static bool Enabled
        {
            get => enabled ?? (enabled = EditorPrefs.GetBool(Pref, false)).Value;
            set { enabled = value; EditorPrefs.SetBool(Pref, value); }
        }

        private static double Now => Clock.Elapsed.TotalMilliseconds;

        /// <summary>The press that may start a stroke.</summary>
        public static void Press()
        {
            if (!Enabled) return;
            active = true;
            down = lastFrame = Now;
            firstMove = firstShown = firstVisible = -1;
            longest = total = 0;
            frames = readbacks = readbacksBack = 0;
            uploads = seenChanges = uploadsSinceSeen = 0;
            lastSeen = -1;
            longestUnseen = 0;
            watched = null;
            watchedVertex = -1;
            haveBefore = false;
            collectionsAtDown = GC.CollectionCount(0);
        }

        /// <summary>A step moved vertices; <paramref name="vertex"/> is one of them (watched on the GPU from the first step).</summary>
        public static void Moved(EditTarget target, int vertex)
        {
            if (!active || firstMove >= 0) return;
            firstMove = Now;
            var skinned = target != null ? target.Skinned : null;
            if (skinned == null) return;
            watched = skinned;
            watchedVertex = vertex;
            if ((skinned.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0) skinned.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            // Before this step's upload: what the renderer shows now.
            Read(true);
        }

        /// <summary>The moves went to the renderer.</summary>
        public static void Uploaded()
        {
            if (!active || firstMove < 0) return;
            if (firstShown < 0) firstShown = Now;
            uploads++;
            uploadsSinceSeen++;
        }

        /// <summary>A Scene view repaint during the stroke.</summary>
        public static void Frame()
        {
            if (!active) return;
            double now = Now;
            double frame = now - lastFrame;
            lastFrame = now;
            if (frames > 0 || frame < 1000) { longest = Math.Max(longest, frame); total += frame; frames++; }
            if (firstShown >= 0 && haveBefore) Read(false);
        }

        // Reads the watched skinned vertex back from the GPU without waiting; the first value that differs from the one
        // before the stroke is when the stroke showed.
        private static void Read(bool before)
        {
            // One read-back at a time: no queue of them while the GPU is behind.
            if (watched == null || watchedVertex < 0 || reading || !SystemInfo.supportsAsyncGPUReadback) return;
            GraphicsBuffer buffer = null;
            try
            {
                buffer = watched.GetVertexBuffer();
                if (buffer == null || watchedVertex >= buffer.count) { buffer?.Dispose(); return; }
                double asked = Now;
                readbacks++;
                var held = buffer;
                reading = true;
                AsyncGPUReadback.Request(buffer, 12, watchedVertex * buffer.stride, request =>
                {
                    reading = false;
                    held.Dispose();
                    if (request.hasError) return;
                    readbacksBack++;
                    var data = request.GetData<float>();
                    var p = new Vector3(data[0], data[1], data[2]);
                    if (before) { skinnedBefore = skinnedLast = p; haveBefore = true; return; }
                    if (!active) return;
                    if (firstVisible < 0 && (p - skinnedBefore).sqrMagnitude > 1e-12f) { firstVisible = asked; lastSeen = asked; skinnedLast = p; seenChanges++; uploadsSinceSeen = 0; return; }
                    if (firstVisible >= 0 && (p - skinnedLast).sqrMagnitude > 1e-12f)
                    {
                        // A wait only counts while uploads went on (a still brush shows nothing new, rightly).
                        if (uploadsSinceSeen > 0 && lastSeen >= 0) longestUnseen = Math.Max(longestUnseen, asked - lastSeen);
                        lastSeen = asked;
                        skinnedLast = p;
                        seenChanges++;
                        uploadsSinceSeen = 0;
                    }
                });
            }
            catch (Exception) { reading = false; buffer?.Dispose(); }
        }

        /// <summary>The stroke ended: one line in the log.</summary>
        public static void End(string brush, string target)
        {
            if (!active) return;
            active = false;
            int collections = GC.CollectionCount(0);
            var c = CultureInfo.InvariantCulture;
            string line = DateTime.Now.ToString("HH:mm:ss", c) + "  " + brush + " on " + target
                          + "  press→move " + (firstMove >= 0 ? (firstMove - down).ToString("0", c) + " ms" : "-")
                          + "  press→upload " + (firstShown >= 0 ? (firstShown - down).ToString("0", c) + " ms" : "-")
                          + "  press→shown " + (firstVisible >= 0 ? (firstVisible - down).ToString("0", c) + " ms" : watched != null ? "never (" + readbacksBack + "/" + readbacks + " read-backs)" : "-")
                          + "  uploads " + uploads + " seen " + seenChanges + " longest unseen " + longestUnseen.ToString("0", c) + " ms"
                          + "  frames " + frames + " avg " + (frames > 0 ? (total / frames).ToString("0.0", c) : "-") + " ms max " + longest.ToString("0", c) + " ms"
                          + "  GC during " + (collections - collectionsAtDown)
                          + (collectionsAtLastEnd >= 0 ? " between " + (collectionsAtDown - collectionsAtLastEnd) : "")
                          + "  heap " + (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576) + " MB";
            collectionsAtLastEnd = collections;
            try
            {
                string path = Path.Combine("Library", "Orbiters", "Mixer", "stroke-timings.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}
