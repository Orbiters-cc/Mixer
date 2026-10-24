using System;
using System.Collections.Generic;

namespace Orbiters.Logger.Editor
{
    public enum TimelineMarkerKind
    {
        Commit,
        Release,
        Note
    }

    /// <summary>Something that happened in the project at a moment: drawn on the Mixer console's timeline.</summary>
    public readonly struct TimelineMarker
    {
        public readonly DateTime TimeUtc;
        public readonly TimelineMarkerKind Kind;
        public readonly string Title;
        public readonly string Detail;

        public TimelineMarker(DateTime timeUtc, TimelineMarkerKind kind, string title, string detail = null)
        {
            TimeUtc = timeUtc.Kind == DateTimeKind.Local ? timeUtc.ToUniversalTime() : timeUtc;
            Kind = kind;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>
    /// Markers other tools put on the Mixer console's timeline (Mixer Git: commits and releases). Each tool replaces its own
    /// set; only the markers inside the logged time range show. Callable from any thread.
    /// </summary>
    public static class TimelineMarkers
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, TimelineMarker[]> sets = new Dictionary<string, TimelineMarker[]>(StringComparer.Ordinal);
        private static TimelineMarker[] merged = new TimelineMarker[0];
        private static int version;

        public static void Set(string source, IEnumerable<TimelineMarker> markers)
        {
            var list = new List<TimelineMarker>(markers ?? new TimelineMarker[0]);
            lock (gate)
            {
                sets[source ?? string.Empty] = list.ToArray();
                Merge();
            }
        }

        public static void Clear(string source)
        {
            lock (gate)
            {
                if (sets.Remove(source ?? string.Empty))
                {
                    Merge();
                }
            }
        }

        internal static int Version
        {
            get
            {
                lock (gate)
                {
                    return version;
                }
            }
        }

        /// <summary>Every marker in time order.</summary>
        internal static TimelineMarker[] All
        {
            get
            {
                lock (gate)
                {
                    return merged;
                }
            }
        }

        private static void Merge()
        {
            var all = new List<TimelineMarker>();
            foreach (var set in sets.Values)
            {
                all.AddRange(set);
            }

            all.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
            merged = all.ToArray();
            version++;
        }
    }
}
