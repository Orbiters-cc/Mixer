using System;
using System.Collections.Generic;

namespace Orbiters.Logger.Editor
{
    /// <summary>Settings of the timings measured by the Mixer console's optional parts.</summary>
    internal static class TimingSettings
    {
        public const string AvatarUploadsPref = "Orbiters.Logger.MeasureAvatarUploads";
    }

    /// <summary>
    /// The steps of avatar builds as they run: the VRChat SDK runs every tool's build step in order (VRCFury, MCB, My
    /// Avatar, ReFit, its own), for uploads and, through VRCFury, when entering Play Mode. The optional VRChat part of
    /// the Mixer console reports each step's time here; the build, upload and Play Mode timings take the runs that happened
    /// during them. Main thread.
    /// </summary>
    internal static class BuildSteps
    {
        private const int MaxRuns = 64;

        internal sealed class Run
        {
            public string Subject = string.Empty;
            public long StartTicks;
            public long EndTicks;
            public bool Complete;
            public readonly List<TimingPart> Parts = new List<TimingPart>();
        }

        private static readonly List<Run> runs = new List<Run>();
        private static Run open;

        /// <summary>A build of <paramref name="subject"/> starts (its first step).</summary>
        public static void Begin(string subject)
        {
            Close(DateTime.UtcNow.Ticks, false);
            open = new Run { Subject = subject ?? string.Empty, StartTicks = DateTime.UtcNow.Ticks };
            runs.Add(open);
            if (runs.Count > MaxRuns)
            {
                runs.RemoveAt(0);
            }
        }

        /// <summary>One step of the open build took <paramref name="milliseconds"/>.</summary>
        public static void Step(string source, string label, double milliseconds)
        {
            if (open == null || milliseconds < 0.05d)
            {
                return;
            }

            open.Parts.Add(new TimingPart(source, "Avatar build steps", label, milliseconds));
        }

        /// <summary>The open build ended: after its last step (<paramref name="complete"/>) or because a step failed.</summary>
        public static void End(bool complete) => Close(DateTime.UtcNow.Ticks, complete);

        public static bool Running => open != null;

        private static void Close(long ticks, bool complete)
        {
            if (open == null)
            {
                return;
            }

            open.EndTicks = ticks;
            open.Complete = complete;
            open = null;
        }

        /// <summary>The builds that started at or after <paramref name="sinceTicks"/> (UTC), removed from the list.</summary>
        public static List<Run> Take(long sinceTicks)
        {
            var taken = new List<Run>();
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                var run = runs[i];
                if (run.StartTicks < sinceTicks || ReferenceEquals(run, open))
                {
                    continue;
                }

                taken.Insert(0, run);
                runs.RemoveAt(i);
            }

            return taken;
        }

        /// <summary>Adds the steps of <paramref name="taken"/> to <paramref name="profile"/>, merged by step (several avatars build in Play Mode).</summary>
        public static double AddTo(TimingProfile profile, List<Run> taken)
        {
            var merged = new Dictionary<string, (TimingPart part, int count)>(StringComparer.Ordinal);
            var order = new List<string>();
            double total = 0d;
            foreach (var run in taken)
            {
                foreach (var part in run.Parts)
                {
                    string key = part.Source + "\n" + part.Label;
                    if (merged.TryGetValue(key, out var existing))
                    {
                        merged[key] = (new TimingPart(part.Source, part.Phase, part.Label, existing.part.Milliseconds + part.Milliseconds), existing.count + 1);
                    }
                    else
                    {
                        merged[key] = (part, 1);
                        order.Add(key);
                    }

                    total += part.Milliseconds;
                }
            }

            int avatars = taken.Count;
            foreach (string key in order)
            {
                var (part, count) = merged[key];
                string label = avatars > 1 && count > 1 ? part.Label + " (" + count + " avatars)" : part.Label;
                profile.Parts.Add(new TimingPart(part.Source, part.Phase, label, part.Milliseconds));
            }

            return total;
        }
    }
}
