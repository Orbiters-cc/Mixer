using System;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// How much time the activity chart above the list covers: the whole history (the chart then stretches as the session
    /// grows, so an old burst of logs shrinks to a sliver) or a sliding window that keeps its scale. Only the chart and the
    /// sparklines follow it; the list and the counts always cover every log.
    /// </summary>
    internal static class TimelineWindow
    {
        private const string Pref = "Orbiters.Logger.TimelineWindowMinutes";

        /// <summary>The choices in minutes; 0 is the whole history.</summary>
        internal static readonly int[] Choices = { 0, 5, 15, 30, 60, 360 };

        /// <summary>Counts the changes, so open windows redraw their chart.</summary>
        internal static int Version { get; private set; }

        /// <summary>Minutes the chart covers, 0 for the whole history.</summary>
        internal static int Minutes
        {
            get => Math.Max(0, EditorPrefs.GetInt(Pref, 0));
            set
            {
                EditorPrefs.SetInt(Pref, Math.Max(0, value));
                Version++;
            }
        }

        internal static string Label(int minutes)
        {
            if (minutes <= 0)
            {
                return "All history";
            }

            return minutes < 60 ? minutes + " min" : minutes / 60 + " h";
        }

        /// <summary>The chart's first moment: the oldest log, or <paramref name="to"/> minus the window.</summary>
        internal static long From(long firstLog, long to, int minutes) =>
            minutes <= 0 ? firstLog : to - TimeSpan.TicksPerMinute * minutes;
    }
}
