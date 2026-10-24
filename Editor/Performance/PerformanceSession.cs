using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Contracts;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// What an Orbiters - N4N0 session recorded besides the world's frames (contracts <c>Docs/performance.md</c>): the
    /// avatar parameters VRChat sent over OSC, the SteamVR device poses, the T-pose calibrations and the audio tracks.
    /// Loaded whole; every time is in seconds since the session started.
    /// </summary>
    internal sealed class PerformanceSession
    {
        public string Folder;
        public string Name;
        public SessionManifest Manifest;
        public ParameterTable Parameters;
        public List<ParameterChange> Changes = new List<ParameterChange>();
        public TrackingDevices Devices;
        public List<TrackingSample> Samples = new List<TrackingSample>();
        public Calibrations Calibrations;
        public AudioTracks Audio;
        /// <summary>The avatar's pose as VRChat showed it (capture kit), when the session has it.</summary>
        public List<SkeletonFrame> Skeleton = new List<SkeletonFrame>();

        public bool HasParameters => Parameters != null && Changes.Count > 0;
        public bool HasTracking => Devices != null && Samples.Count > 0;
        public bool HasCalibration => Calibrations != null && Calibrations.Items.Count > 0;
        public bool HasSkeleton => Skeleton.Count > 0;

        /// <summary>
        /// Values were recorded but the table that names them (<c>parameters.json</c>) is missing: Orbiters - N4N0 writes
        /// it when the session ends, so the session is still recording or Orbiters - N4N0 stopped first.
        /// </summary>
        public bool MissingParameterTable => Parameters == null && Changes.Count > 0;

        /// <summary>Poses were recorded but <c>tracking.json</c>, the devices they belong to, is missing (written at the end too).</summary>
        public bool MissingDeviceTable => Devices == null && Samples.Count > 0;

        /// <summary>Seconds covered by the streams (the manifest's duration when it is longer).</summary>
        public double Duration
        {
            get
            {
                double d = Manifest?.Stats?.DurationS ?? 0;
                if (Changes.Count > 0) d = Math.Max(d, Changes.Max(c => c.TimeNs) * 1e-9);
                if (Samples.Count > 0) d = Math.Max(d, Samples[Samples.Count - 1].TimeNs * 1e-9);
                if (Skeleton.Count > 0) d = Math.Max(d, Skeleton.Max(f => f.TimeNs) * 1e-9);
                if (Audio != null)
                    foreach (var t in Audio.Tracks)
                        d = Math.Max(d, (t.FirstFrameNs + t.Frames * 1e9 / Math.Max(1, t.SampleRate)) * 1e-9);
                return d;
            }
        }

        /// <summary>Whether a folder holds any performance stream.</summary>
        public static bool HasPerformance(string folder) =>
            File.Exists(Path.Combine(folder, PerformanceFiles.Parameters)) || File.Exists(Path.Combine(folder, PerformanceFiles.Tracking)) ||
            File.Exists(Path.Combine(folder, PerformanceFiles.Audio)) || File.Exists(Path.Combine(folder, PerformanceFiles.Skeleton));

        public static PerformanceSession Load(string folder)
        {
            var s = new PerformanceSession { Folder = folder, Name = Path.GetFileName(folder.TrimEnd('/', '\\')) };
            string manifest = Path.Combine(folder, "manifest.json");
            if (File.Exists(manifest)) s.Manifest = JsonConvert.DeserializeObject<SessionManifest>(File.ReadAllText(manifest));
            string table = Path.Combine(folder, PerformanceFiles.ParameterTable);
            string changes = Path.Combine(folder, PerformanceFiles.Parameters);
            if (File.Exists(table)) s.Parameters = JsonConvert.DeserializeObject<ParameterTable>(File.ReadAllText(table));
            if (File.Exists(changes)) s.Changes = ParameterChange.Read(File.ReadAllBytes(changes));
            string devices = Path.Combine(folder, PerformanceFiles.TrackingDevices);
            string samples = Path.Combine(folder, PerformanceFiles.Tracking);
            if (File.Exists(devices)) s.Devices = JsonConvert.DeserializeObject<TrackingDevices>(File.ReadAllText(devices));
            if (File.Exists(samples)) s.Samples = TrackingSample.Read(File.ReadAllBytes(samples));
            string calibration = Path.Combine(folder, PerformanceFiles.Calibration);
            if (File.Exists(calibration)) s.Calibrations = JsonConvert.DeserializeObject<Calibrations>(File.ReadAllText(calibration));
            string audio = Path.Combine(folder, PerformanceFiles.Audio);
            if (File.Exists(audio)) s.Audio = JsonConvert.DeserializeObject<AudioTracks>(File.ReadAllText(audio));
            string skeleton = Path.Combine(folder, PerformanceFiles.Skeleton);
            if (File.Exists(skeleton)) s.Skeleton = SkeletonFrame.Read(File.ReadAllBytes(skeleton));
            return s;
        }

        /// <summary>
        /// The calibration to use: the latest one made before <paramref name="seconds"/>, else the first one. Without
        /// <paramref name="beforeSession"/>, calibrations made before the session started (the latest one is copied into
        /// every session) are left out.
        /// </summary>
        public Calibrations.Calibration CalibrationAt(double seconds, bool beforeSession = true)
        {
            if (!HasCalibration) return null;
            Calibrations.Calibration best = null;
            foreach (var c in Calibrations.Items)
                if ((beforeSession || c.TimeNs >= 0) && c.TimeNs * 1e-9 <= seconds && (best == null || c.TimeNs > best.TimeNs))
                    best = c;
            return best ?? Calibrations.Items.Where(c => beforeSession || c.TimeNs >= 0).OrderBy(c => c.TimeNs).FirstOrDefault();
        }

        /// <summary>Sessions with performance streams under a library folder, newest first.</summary>
        public static List<string> FindSessions(string root)
        {
            var found = new List<string>();
            if (!Directory.Exists(root)) return found;
            foreach (var dir in Directory.GetDirectories(root))
                if (HasPerformance(dir))
                    found.Add(dir);
            return found.OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).ToList();
        }

        /// <summary><c>Documents/Orbiters/Sessions</c>, where Orbiters - N4N0 writes sessions by default.</summary>
        public static string DefaultLibrary => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Orbiters", "Sessions");
    }
}
