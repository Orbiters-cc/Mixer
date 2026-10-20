using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Opt-in timings of real strokes (EditorPrefs <c>Orbiters.Mixer.Diagnostics.StrokeTimings</c>), one line each in
    /// <c>Library/Orbiters/Mixer/stroke-timings.log</c>: from the press to the first move and to the first upload, the
    /// frames of the stroke (average and longest) and the garbage collections during it and since the last stroke. What a
    /// stroke feels like depends on the whole editor, which a benchmark of the brush alone does not see.
    /// </summary>
    internal static class StrokeTimings
    {
        private const string Pref = "Orbiters.Mixer.Diagnostics.StrokeTimings";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static bool? enabled;
        private static bool active;
        private static double down, firstMove, firstShown, lastFrame, longest, total;
        private static int frames, collectionsAtDown, collectionsAtLastEnd = -1;

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
            firstMove = firstShown = -1;
            longest = total = 0;
            frames = 0;
            collectionsAtDown = GC.CollectionCount(0);
        }

        /// <summary>A step moved vertices.</summary>
        public static void Moved()
        {
            if (active && firstMove < 0) firstMove = Now;
        }

        /// <summary>The moves went to the renderer.</summary>
        public static void Uploaded()
        {
            if (active && firstMove >= 0 && firstShown < 0) firstShown = Now;
        }

        /// <summary>A Scene view repaint during the stroke.</summary>
        public static void Frame()
        {
            if (!active) return;
            double now = Now;
            double frame = now - lastFrame;
            lastFrame = now;
            if (frames > 0 || frame < 1000) { longest = Math.Max(longest, frame); total += frame; frames++; }
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
