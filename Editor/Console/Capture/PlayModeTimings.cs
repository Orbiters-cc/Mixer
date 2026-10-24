using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Measures entering Play Mode, from the Play button to the first frames: leaving Edit Mode, the script reload (each
    /// package's part, from <see cref="ReloadTimings"/>), the avatar build steps VRCFury runs on the avatars in the
    /// scene (each tool's part, from <see cref="BuildSteps"/>), then Unity's own work. The session spans the reload, so
    /// its moments are kept in <see cref="SessionState"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeTimings
    {
        internal const string EnabledPref = "Orbiters.Logger.MeasurePlayMode";
        private const string StartKey = "Orbiters.Logger.PlayMode.Start";
        private const string AfterReloadKey = "Orbiters.Logger.PlayMode.AfterReload";
        private const string EnteredKey = "Orbiters.Logger.PlayMode.Entered";
        private const string EndKey = "Orbiters.Logger.PlayMode.End";
        private const string ReloadKey = "Orbiters.Logger.PlayMode.Reload";
        private const string ReloadStartKey = "Orbiters.Logger.PlayMode.ReloadStart";
        private const string ReloadBeganKey = "Orbiters.Logger.PlayMode.ReloadBegan";
        private const double ReloadWaitSeconds = 20d;

        private static int framesAfterEnter = -1;
        private static double giveUpAt;
        private static double endSeenAt = -1d;
        private static bool updating;

        static PlayModeTimings()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (Pending)
                {
                    Set(ReloadBeganKey, Now);
                }
            };
            ReloadTimings.Claim = ClaimReload;
            if (Pending)
            {
                // The new domain of the reload Play Mode started with.
                if (Get(AfterReloadKey) == 0L)
                {
                    Set(AfterReloadKey, Now);
                }

                Watch();
            }
        }

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (!value)
                {
                    Reset();
                }
            }
        }

        private static long Now => DateTime.UtcNow.Ticks;

        private static bool Pending => Get(StartKey) > 0L;

        private static long Get(string key) =>
            long.TryParse(SessionState.GetString(key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0L;

        private static void Set(string key, long value) => SessionState.SetString(key, value.ToString(CultureInfo.InvariantCulture));

        private static void Reset()
        {
            foreach (string key in new[] { StartKey, AfterReloadKey, EnteredKey, EndKey, ReloadKey, ReloadStartKey, ReloadBeganKey })
            {
                SessionState.EraseString(key);
            }

            framesAfterEnter = -1;
            endSeenAt = -1d;
        }

        private static void Watch()
        {
            giveUpAt = EditorApplication.timeSinceStartup + ReloadWaitSeconds + 30d;
            if (!updating)
            {
                updating = true;
                EditorApplication.update += Update;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            try
            {
                switch (change)
                {
                    case PlayModeStateChange.ExitingEditMode:
                        Reset();
                        if (Enabled)
                        {
                            Set(StartKey, Now);
                            Watch();
                        }

                        break;
                    case PlayModeStateChange.EnteredPlayMode:
                        if (Pending)
                        {
                            Set(EnteredKey, Now);
                            framesAfterEnter = 0;
                            Watch();
                        }

                        break;
                    case PlayModeStateChange.ExitingPlayMode:
                        // Left before the reload was measured: log what is known.
                        if (Pending && Get(EnteredKey) > 0L)
                        {
                            if (Get(EndKey) == 0L)
                            {
                                Set(EndKey, Now);
                            }

                            Publish();
                        }
                        else
                        {
                            Reset();
                        }

                        break;
                }
            }
            catch (Exception)
            {
                Reset();
            }
        }

        // The reload Play Mode started with goes into its timing instead of a log of its own.
        private static bool ClaimReload(TimingProfile profile, long reloadStart)
        {
            if (!Pending || reloadStart < Get(StartKey) || SessionState.GetString(ReloadKey, string.Empty).Length > 0)
            {
                return false;
            }

            SessionState.SetString(ReloadKey, profile.Format());
            Set(ReloadStartKey, reloadStart);
            return true;
        }

        private static void Update()
        {
            try
            {
                if (!Pending)
                {
                    Stop();
                    return;
                }

                if (framesAfterEnter >= 0 && Get(EndKey) == 0L)
                {
                    // A few frames in, once the avatars' build steps are done.
                    if (++framesAfterEnter >= 3 && !BuildSteps.Running)
                    {
                        Set(EndKey, Now);
                    }
                }

                if (Get(EndKey) == 0L)
                {
                    if (EditorApplication.timeSinceStartup > giveUpAt && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        Reset();
                        Stop();
                    }

                    return;
                }

                // Unity writes the reload's profile a moment after it: wait for it a little.
                if (endSeenAt < 0d)
                {
                    endSeenAt = EditorApplication.timeSinceStartup;
                }

                bool reloaded = Get(ReloadBeganKey) > 0L;
                bool reloadMeasured = SessionState.GetString(ReloadKey, string.Empty).Length > 0;
                if (reloaded && !reloadMeasured && ReloadTimings.Enabled && EditorApplication.timeSinceStartup - endSeenAt < ReloadWaitSeconds)
                {
                    return;
                }

                Publish();
            }
            catch (Exception)
            {
                Reset();
                Stop();
            }
        }

        private static void Stop()
        {
            if (updating)
            {
                updating = false;
                EditorApplication.update -= Update;
            }
        }

        private static void Publish()
        {
            long start = Get(StartKey);
            long end = Get(EndKey);
            if (start <= 0L || end <= start)
            {
                Reset();
                return;
            }

            var profile = new TimingProfile
            {
                Kind = TimingKind.PlayMode,
                TotalMilliseconds = (end - start) / (double)TimeSpan.TicksPerMillisecond
            };

            long reloadBegan = Get(ReloadBeganKey);
            if (reloadBegan > start)
            {
                profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, "Leaving Edit Mode", "Scene backup and tools' Play Mode callbacks",
                    (reloadBegan - start) / (double)TimeSpan.TicksPerMillisecond));
                var reload = TimingProfile.Parse(SessionState.GetString(ReloadKey, string.Empty));
                if (reload != null)
                {
                    foreach (var share in reload.Shares)
                    {
                        foreach (var part in share.Parts)
                        {
                            profile.Parts.Add(new TimingPart(part.Source, "Script reload", part.Phase + " · " + part.Label, part.Milliseconds));
                        }
                    }
                }
                else
                {
                    long after = Get(AfterReloadKey);
                    if (after > reloadBegan)
                    {
                        profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, "Script reload", "Reloading scripts",
                            (after - reloadBegan) / (double)TimeSpan.TicksPerMillisecond));
                    }
                }
            }

            BuildSteps.AddTo(profile, BuildSteps.Take(start));
            Reset();
            Stop();
            LogCapture.Inject(profile.Message(), profile.Format(), LogType.Log);
        }
    }
}
