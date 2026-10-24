using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Unity's editor performance trackers: the editor times each callback it runs (every asset postprocessor's
    /// OnPostprocessAllAssets, every update and delay call…) under a name such as
    /// "TMPro_TexturePostProcessor.OnPostprocessAllAssets", adding up calls and seconds. Two snapshots taken around some
    /// work tell what each callback cost during it. The API is internal: without it, snapshots are null.
    /// </summary>
    internal static class EditorTrackers
    {
        internal readonly struct Total
        {
            public readonly double Seconds;
            public readonly int Calls;

            public Total(double seconds, int calls)
            {
                Seconds = seconds;
                Calls = calls;
            }
        }

        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool resolved;
        private static MethodInfo names, totalTime, sampleCount;

        /// <summary>Main thread: the trackers whose name passes <paramref name="keep"/>, or null when Unity's API is missing.</summary>
        internal static Dictionary<string, Total> Snapshot(Func<string, bool> keep)
        {
            try
            {
                if (!Resolve())
                {
                    return null;
                }

                var snapshot = new Dictionary<string, Total>(StringComparer.Ordinal);
                foreach (string name in (string[])names.Invoke(null, null))
                {
                    if (name != null && keep(name))
                    {
                        var args = new object[] { name };
                        snapshot[name] = new Total((double)totalTime.Invoke(null, args), (int)sampleCount.Invoke(null, args));
                    }
                }

                return snapshot;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>What each tracker added between two snapshots, in milliseconds, with the number of calls.</summary>
        internal static List<(string name, double milliseconds, int calls)> Between(Dictionary<string, Total> before, Dictionary<string, Total> after)
        {
            var grown = new List<(string, double, int)>();
            if (before == null || after == null)
            {
                return grown;
            }

            foreach (var pair in after)
            {
                before.TryGetValue(pair.Key, out var start);
                int calls = pair.Value.Calls - start.Calls;
                double milliseconds = (pair.Value.Seconds - start.Seconds) * 1000d;
                if (calls > 0 && milliseconds > 0d)
                {
                    grown.Add((pair.Key, milliseconds, calls));
                }
            }

            return grown;
        }

        private static bool Resolve()
        {
            if (!resolved)
            {
                resolved = true;
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.Profiling.EditorPerformanceTracker");
                names = type?.GetMethod("GetAvailableTrackers", Static, null, Type.EmptyTypes, null);
                totalTime = type?.GetMethod("GetTotalTime", Static, null, new[] { typeof(string) }, null);
                sampleCount = type?.GetMethod("GetSampleCount", Static, null, new[] { typeof(string) }, null);
            }

            return names != null && totalTime != null && sampleCount != null;
        }
    }
}
