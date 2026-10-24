#if LOGGER_VRCSDK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEditor.Callbacks;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Logger.Editor.VRChat
{
    /// <summary>
    /// Times every tool's step of avatar builds. The VRChat SDK keeps the steps (its avatar preprocess callbacks) in a
    /// list it runs by order; a probe goes in front of each group of steps that share an order, and one after the last.
    /// Each probe notes the time, so the gap between two probes is the time the steps between them took. The tools'
    /// steps themselves are left untouched. Runs for uploads, test builds and, through VRCFury, entering Play Mode.
    /// </summary>
    internal static class AvatarBuildProbes
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        // Steps whose code lives in Orbiters Toolkit but which belong to a tool, as people know them.
        private static readonly (string prefix, string source)[] ToolSteps =
        {
            ("Orbiters.Toolkit.Editor.VRChat.Refit.", "ReFit"),
            ("Orbiters.Toolkit.Editor.VRChat.Attachments.", "My Avatar"),
        };

        private sealed class Group
        {
            public int Order;
            public readonly List<Type> Steps = new List<Type>();
            public string Source;
            public string Label;
        }

        private static readonly List<Group> groups = new List<Group>();
        private static long[] hits = new long[0];
        private static int last = -1;
        private static bool described;

        [DidReloadScripts(int.MaxValue)]
        private static void Install()
        {
            try
            {
                var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", Static);
                if (!(field?.GetValue(null) is List<IVRCSDKPreprocessAvatarCallback> list))
                {
                    return;
                }

                list.RemoveAll(callback => callback is LoggerBuildStepProbe probe && probe.Active);
                var byOrder = new SortedDictionary<int, Group>();
                foreach (var callback in list)
                {
                    if (callback == null || callback is LoggerBuildStepProbe)
                    {
                        continue;
                    }

                    int order;
                    try
                    {
                        order = callback.callbackOrder;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!byOrder.TryGetValue(order, out var group))
                    {
                        group = new Group { Order = order };
                        byOrder[order] = group;
                    }

                    group.Steps.Add(callback.GetType());
                }

                groups.Clear();
                groups.AddRange(byOrder.Values);
                hits = new long[groups.Count + 1];
                last = -1;
                described = false;
                // The SDK sorts with a stable sort on each build: a probe put first in the list runs before the steps
                // that share its order; the closing probe, put last, after every step.
                for (int i = groups.Count - 1; i >= 0; i--)
                {
                    list.Insert(0, new LoggerBuildStepProbe(groups[i].Order, i));
                }

                list.Add(new LoggerBuildStepProbe(int.MaxValue, groups.Count));
            }
            catch (Exception)
            {
                // Without probes, builds are timed as a whole.
            }
        }

        internal static void Hit(int index, GameObject avatar)
        {
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (index < 0 || index >= hits.Length)
                {
                    return;
                }

                if (index == 0)
                {
                    Describe();
                    BuildSteps.Begin(AvatarName(avatar));
                }
                else if (last >= 0 && last < index && last < groups.Count)
                {
                    var group = groups[last];
                    BuildSteps.Step(group.Source, group.Label, (now - hits[last]) * 1000d / Stopwatch.Frequency);
                }

                hits[index] = now;
                last = index;
                if (index == groups.Count)
                {
                    BuildSteps.End(true);
                    last = -1;
                    AvatarUploadTimings.StepsDone();
                }
            }
            catch (Exception)
            {
                // Timing must never fail a build.
            }
        }

        /// <summary>The build failed or was cancelled before its last step: the step running then took the rest.</summary>
        internal static void Abort()
        {
            if (last >= 0 && last < groups.Count && BuildSteps.Running)
            {
                var group = groups[last];
                BuildSteps.Step(group.Source, group.Label + " (failed)", (Stopwatch.GetTimestamp() - hits[last]) * 1000d / Stopwatch.Frequency);
            }

            BuildSteps.End(false);
            last = -1;
        }

        // Which tool each group of steps belongs to, worked out once, at the first build.
        private static void Describe()
        {
            if (described)
            {
                return;
            }

            described = true;
            var packages = new Dictionary<Assembly, string>();
            foreach (var group in groups)
            {
                var sources = new List<string>();
                var names = new List<string>();
                foreach (var type in group.Steps)
                {
                    string source = SourceOf(type, packages);
                    if (!sources.Contains(source))
                    {
                        sources.Add(source);
                    }

                    names.Add(StepName(type));
                }

                group.Source = string.Join(" + ", sources);
                group.Label = string.Join(", ", names);
            }
        }

        private static string SourceOf(Type type, Dictionary<Assembly, string> packages)
        {
            string fullName = type.FullName ?? type.Name;
            foreach (var (prefix, source) in ToolSteps)
            {
                if (fullName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return source;
                }
            }

            if (!packages.TryGetValue(type.Assembly, out string name))
            {
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly);
                    name = info != null ? SourceCatalog.NameForPackage(info.name) : null;
                }
                catch (Exception)
                {
                    name = null;
                }

                name = name ?? SourceCatalog.NameForNamespace(fullName) ?? "Project";
                packages[type.Assembly] = name;
            }

            return name;
        }

        // "VF.Hooks.PreProcessingFailureCheckHook+FailureCheckStart" → "PreProcessingFailureCheckHook.FailureCheckStart".
        private static string StepName(Type type)
        {
            string name = type.Name;
            for (var outer = type.DeclaringType; outer != null; outer = outer.DeclaringType)
            {
                name = outer.Name + "." + name;
            }

            return name;
        }

        private static string AvatarName(GameObject avatar)
        {
            if (avatar == null)
            {
                return string.Empty;
            }

            string name = avatar.name;
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7).TrimEnd() : name;
        }
    }

    /// <summary>
    /// A probe among the avatar build steps (see <see cref="AvatarBuildProbes"/>). The SDK also makes one itself, as it
    /// does of every step type it finds: that one has no place in the order and does nothing.
    /// </summary>
    internal sealed class LoggerBuildStepProbe : IVRCSDKPreprocessAvatarCallback
    {
        private readonly int order = int.MaxValue;
        private readonly int index = -1;

        public LoggerBuildStepProbe()
        {
        }

        internal LoggerBuildStepProbe(int order, int index)
        {
            this.order = order;
            this.index = index;
        }

        internal bool Active => index >= 0;

        public int callbackOrder => order;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            if (index >= 0)
            {
                AvatarBuildProbes.Hit(index, avatarGameObject);
            }

            return true;
        }
    }
}
#endif
