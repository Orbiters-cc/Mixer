#if LOGGER_VRCSDK
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Logger.Editor.VRChat
{
    /// <summary>
    /// Measures avatar builds and uploads from the VRChat SDK panel: from Build &amp; Publish (or Build &amp; Test) to the end
    /// of the upload, with each tool's build step (<see cref="AvatarBuildProbes"/>), the asset bundle and the upload.
    /// Logged as a timing log once the upload ends (or the build, when nothing is uploaded).
    /// </summary>
    [InitializeOnLoad]
    internal static class AvatarUploadTimings
    {
        private const double UploadWaitSeconds = 4d;

        private static IVRCSdkAvatarBuilderApi attached;
        private static Session session;

        private sealed class Session
        {
            public long Requested;
            public long BuildStarted;
            public long BuildEnded;
            public long UploadStarted;
            public long UploadEnded;
            public string Subject = string.Empty;
            public string Outcome = string.Empty;
            public bool Failed;
            public double WaitUntil;
            // Editor.log length and Unity's callback trackers when the build steps ended and when the build did.
            public long StepsLog = -1L;
            public long EndLog = -1L;
            public Dictionary<string, EditorTrackers.Total> StepsTrackers;
            public Dictionary<string, EditorTrackers.Total> EndTrackers;
        }

        static AvatarUploadTimings()
        {
            VRCSdkControlPanel.OnSdkPanelEnable -= OnPanelEnable;
            VRCSdkControlPanel.OnSdkPanelEnable += OnPanelEnable;
            VRCSdkControlPanel.OnSdkPanelDisable -= OnPanelDisable;
            VRCSdkControlPanel.OnSdkPanelDisable += OnPanelDisable;
            AssemblyReloadEvents.beforeAssemblyReload -= Detach;
            AssemblyReloadEvents.beforeAssemblyReload += Detach;
            EditorApplication.delayCall += TryAttach;
        }

        internal static bool Enabled => EditorPrefs.GetBool(TimingSettings.AvatarUploadsPref, true);

        private static long Now => DateTime.UtcNow.Ticks;

        private static void OnPanelEnable(object sender, EventArgs args)
        {
            EditorApplication.delayCall -= TryAttach;
            EditorApplication.delayCall += TryAttach;
        }

        private static void OnPanelDisable(object sender, EventArgs args) => Detach();

        private static void TryAttach()
        {
            try
            {
                if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null ||
                    ReferenceEquals(builder, attached))
                {
                    return;
                }

                Detach();
                attached = builder;
                attached.OnSdkBuildStart += OnBuildStart;
                attached.OnSdkBuildSuccess += OnBuildSuccess;
                attached.OnSdkBuildError += OnBuildError;
                attached.OnSdkBuildFinish += OnBuildFinish;
                attached.OnSdkUploadStart += OnUploadStart;
                attached.OnSdkUploadError += OnUploadError;
                attached.OnSdkUploadFinish += OnUploadFinish;
            }
            catch (Exception)
            {
                // The SDK panel changed: builds are then not timed.
            }
        }

        private static void Detach()
        {
            if (attached == null)
            {
                return;
            }

            attached.OnSdkBuildStart -= OnBuildStart;
            attached.OnSdkBuildSuccess -= OnBuildSuccess;
            attached.OnSdkBuildError -= OnBuildError;
            attached.OnSdkBuildFinish -= OnBuildFinish;
            attached.OnSdkUploadStart -= OnUploadStart;
            attached.OnSdkUploadError -= OnUploadError;
            attached.OnSdkUploadFinish -= OnUploadFinish;
            attached = null;
        }

        /// <summary>Build &amp; Publish or Build &amp; Test was clicked: the SDK asks the build callbacks, this one first.</summary>
        internal static void Requested()
        {
            if (!Enabled)
            {
                return;
            }

            session = new Session { Requested = Now };
            TryAttach();
        }

        private static void OnBuildStart(object sender, object target)
        {
            if (!Enabled)
            {
                return;
            }

            if (session == null || session.BuildStarted > 0L)
            {
                session = new Session { Requested = Now };
            }

            session.BuildStarted = Now;
            var avatar = target as GameObject;
            session.Subject = avatar != null ? avatar.name : string.Empty;
        }

        private static void OnBuildSuccess(object sender, string bundlePath)
        {
            if (session != null)
            {
                session.BuildEnded = Now;
                Mark(false);
            }
        }

        /// <summary>The last build step ran: what follows is Unity's asset bundle build.</summary>
        internal static void StepsDone()
        {
            if (session != null && session.BuildStarted > 0L && session.BuildEnded == 0L)
            {
                Mark(true);
            }
        }

        // Unity's code editor integration regenerates the project files after imports, like a postprocessor.
        private const string ProjectSync = "SyncVS.PostprocessSyncProject";

        private static bool IsAssetCallback(string tracker) =>
            tracker.IndexOf(".OnPostprocess", StringComparison.Ordinal) > 0 || tracker.IndexOf(".OnPreprocess", StringComparison.Ordinal) > 0 ||
            tracker == ProjectSync;

        private static void Mark(bool steps)
        {
            long log = ReloadTimings.LogLength();
            var trackers = EditorTrackers.Snapshot(IsAssetCallback);
            if (steps)
            {
                session.StepsLog = log;
                session.StepsTrackers = trackers;
            }
            else if (session.EndLog < 0L)
            {
                session.EndLog = log;
                session.EndTrackers = trackers;
            }
        }

        private static void OnBuildError(object sender, string message)
        {
            if (session == null)
            {
                return;
            }

            AvatarBuildProbes.Abort();
            session.BuildEnded = Now;
            Mark(false);
            session.Failed = true;
            session.Outcome = Reason("build failed", message);
        }

        private static void OnBuildFinish(object sender, string message)
        {
            if (session == null || session.BuildStarted == 0L)
            {
                // Blocked before it started (a build callback said no): nothing was built.
                session = null;
                return;
            }

            if (session.BuildEnded == 0L)
            {
                session.BuildEnded = Now;
                Mark(false);
            }

            if (session.Failed)
            {
                Publish(TimingKind.AvatarBuild);
                return;
            }

            // Build & Publish uploads right after the build; Build & Test doesn't.
            session.WaitUntil = EditorApplication.timeSinceStartup + UploadWaitSeconds;
            EditorApplication.update -= WaitForUpload;
            EditorApplication.update += WaitForUpload;
        }

        private static void WaitForUpload()
        {
            if (session == null || session.UploadStarted > 0L)
            {
                EditorApplication.update -= WaitForUpload;
                return;
            }

            if (EditorApplication.timeSinceStartup >= session.WaitUntil)
            {
                EditorApplication.update -= WaitForUpload;
                Publish(TimingKind.AvatarBuild);
            }
        }

        private static void OnUploadStart(object sender, EventArgs args)
        {
            if (session != null && session.BuildStarted > 0L)
            {
                session.UploadStarted = Now;
            }
        }

        private static void OnUploadError(object sender, string message)
        {
            if (session == null)
            {
                return;
            }

            session.Failed = true;
            session.Outcome = Reason("upload failed", message);
        }

        private static void OnUploadFinish(object sender, string message)
        {
            if (session == null || session.UploadStarted == 0L)
            {
                return;
            }

            session.UploadEnded = Now;
            Publish(TimingKind.AvatarUpload);
        }

        private static string Reason(string what, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return what;
            }

            string line = message.Trim();
            int newline = line.IndexOf('\n');
            if (newline > 0)
            {
                line = line.Substring(0, newline).Trim();
            }

            return what + ": " + (line.Length > 120 ? line.Substring(0, 120) + "…" : line);
        }

        /// <summary>
        /// Splits the asset bundle build (after the last build step): each asset postprocessor that ran meanwhile, by
        /// its package, from Unity's callback trackers; then, from what Unity wrote to Editor.log, the script compilation
        /// for the build, the asset imports and, as the rest, shader compilation, writing and compressing the bundle.
        /// </summary>
        private static void AddBundleParts(TimingProfile profile, Session current, double window)
        {
            const string phase = "Asset bundle";
            var log = current.StepsLog >= 0L && current.EndLog > current.StepsLog ? BundleBuildLog.Read(ReloadTimings.LogPath, current.StepsLog, current.EndLog) : null;
            var callbacks = EditorTrackers.Between(current.StepsTrackers, current.EndTrackers);
            if ((log == null || !log.Found) && callbacks.Count == 0)
            {
                profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, phase, "Building the avatar's asset bundle", window));
                return;
            }

            log = log ?? new BundleBuildLog();
            Dictionary<string, string> sources = null;
            double allAssets = 0d;
            double perAsset = 0d;
            foreach (var (name, milliseconds, calls) in callbacks)
            {
                int dot = name.IndexOf('.');
                string type = dot > 0 ? name.Substring(0, dot) : name;
                string method = dot > 0 ? name.Substring(dot + 1) : string.Empty;
                bool afterImports = method == "OnPostprocessAllAssets" || name == ProjectSync;
                if (afterImports)
                {
                    allAssets += milliseconds;
                }
                else
                {
                    perAsset += milliseconds;
                }

                if (milliseconds < 20d)
                {
                    continue;
                }

                sources = sources ?? PostprocessorSources();
                string times = calls > 1 ? " ×" + calls.ToString(CultureInfo.InvariantCulture) : string.Empty;
                string label = name == ProjectSync
                    ? "Code editor project files sync" + times
                    : type + "." + method + times + (afterImports ? " (asset postprocessor, runs after every import)" : " (asset postprocessor)");
                profile.Parts.Add(new TimingPart(sources.TryGetValue(type, out string source) ? source : TimingProfile.UnitySource, phase, label, milliseconds));
            }

            double claimed = allAssets + perAsset;
            if (log.ScriptMilliseconds > 0d)
            {
                profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, phase, "Compiling scripts for the build", log.ScriptMilliseconds));
                claimed += log.ScriptMilliseconds;
            }

            if (log.Refreshes > 0)
            {
                double otherCallbacks = Math.Max(0d, log.CallbackMilliseconds - allAssets);
                double imports = Math.Max(0d, log.RefreshMilliseconds - log.CallbackMilliseconds - perAsset);
                string count = log.Imports > 0
                    ? log.Imports.ToString(CultureInfo.InvariantCulture) + " for the build target (" + log.CachedImports.ToString(CultureInfo.InvariantCulture) + " from the cache)"
                    : "for the build target";
                profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, phase, "Asset imports: " + count, imports));
                if (otherCallbacks >= 50d)
                {
                    profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, phase, "Other asset database callbacks", otherCallbacks));
                }

                claimed += imports + otherCallbacks;
            }

            string rest = (log.ShaderPasses > 0 ? "Compiling " + log.ShaderPasses.ToString(CultureInfo.InvariantCulture) + " shader passes, writing" : "Writing") +
                          " and compressing the bundle" +
                          (log.CompressedSize.Length > 0 ? " (" + log.CompressedSize + ", " + log.UncompressedSize + " uncompressed)" : string.Empty);
            profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, phase, rest, Math.Max(0d, window - claimed)));
        }

        // Asset postprocessors by the name Unity's trackers give them (the type without namespace), with their package.
        private static Dictionary<string, string> PostprocessorSources()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var assemblies = AssemblySources.Take();
                foreach (var type in TypeCache.GetTypesDerivedFrom<AssetPostprocessor>())
                {
                    if (!sources.ContainsKey(type.Name))
                    {
                        sources[type.Name] = assemblies.ForType(type.FullName, type.Assembly.GetName().Name);
                    }
                }
            }
            catch (Exception)
            {
                // Unattributed postprocessors count as Unity's.
            }

            return sources;
        }

        private static double Ms(long from, long to) => to > from ? (to - from) / (double)TimeSpan.TicksPerMillisecond : 0d;

        private static void Publish(TimingKind kind)
        {
            var current = session;
            session = null;
            if (current == null || current.BuildStarted == 0L)
            {
                return;
            }

            try
            {
                long start = current.Requested > 0L ? current.Requested : current.BuildStarted;
                long end = kind == TimingKind.AvatarUpload ? current.UploadEnded : current.BuildEnded;
                var profile = new TimingProfile
                {
                    Kind = kind,
                    Subject = current.Subject,
                    Outcome = current.Outcome,
                    TotalMilliseconds = Ms(start, end)
                };

                var runs = BuildSteps.Take(current.BuildStarted);
                long firstStep = 0L;
                long lastStep = 0L;
                foreach (var run in runs)
                {
                    firstStep = firstStep == 0L ? run.StartTicks : Math.Min(firstStep, run.StartTicks);
                    lastStep = Math.Max(lastStep, run.EndTicks);
                }

                profile.Parts.Add(new TimingPart("VRChat SDK", "Before the build", "Checks and build callbacks", Ms(start, current.BuildStarted)));
                if (firstStep > 0L)
                {
                    profile.Parts.Add(new TimingPart("VRChat SDK", "Preparing", "Saving the scene and copying the avatar", Ms(current.BuildStarted, firstStep)));
                    BuildSteps.AddTo(profile, runs);
                    if (lastStep > 0L)
                    {
                        AddBundleParts(profile, current, Ms(lastStep, current.BuildEnded));
                    }
                }

                if (kind == TimingKind.AvatarUpload)
                {
                    profile.Parts.Add(new TimingPart("VRChat SDK", "Upload", "Between build and upload", Ms(current.BuildEnded, current.UploadStarted)));
                    profile.Parts.Add(new TimingPart("VRChat SDK", "Upload", "Uploading to VRChat", Ms(current.UploadStarted, current.UploadEnded)));
                }

                LogCapture.Inject(profile.Message(), profile.Format(), current.Failed ? LogType.Warning : LogType.Log);
            }
            catch (Exception)
            {
                // A timing that can't be put together is left out.
            }
        }
    }

    /// <summary>Notes when Build &amp; Publish or Build &amp; Test was clicked: the SDK asks build callbacks first, this one first of all.</summary>
    internal sealed class LoggerBuildRequestedHook : IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => int.MinValue;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            if (requestedBuildType == VRCSDKRequestedBuildType.Avatar)
            {
                AvatarUploadTimings.Requested();
            }

            return true;
        }
    }
}
#endif
