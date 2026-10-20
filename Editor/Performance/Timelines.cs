using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Contracts;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// The recorded OSC values as step functions of time. Avatar parameters are keyed by their animator name
    /// (<c>v2/JawOpen</c>, <c>Viseme</c>); other values by their recorded name (<c>/avatar/eyeheight</c>,
    /// <c>/tracking/vrsystem/head/pose[4]</c>).
    /// </summary>
    internal sealed class ParameterTimeline
    {
        internal sealed class Track
        {
            public string Name, Address;
            public bool IsAvatarParameter;
            public readonly List<double> Times = new List<double>();
            public readonly List<float> Values = new List<float>();
            public readonly List<ParameterKind> Kinds = new List<ParameterKind>();

            /// <summary>Index of the last change at or before <paramref name="seconds"/>; -1 before the first.</summary>
            public int IndexAt(double seconds)
            {
                int lo = 0, hi = Times.Count - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (Times[mid] <= seconds)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                return found;
            }

            /// <summary>The value at a time (the first value before the first change).</summary>
            public float ValueAt(double seconds)
            {
                if (Times.Count == 0) return 0f;
                int i = IndexAt(seconds);
                return Values[Math.Max(0, i)];
            }
        }

        public readonly Dictionary<string, Track> Tracks = new Dictionary<string, Track>();
        /// <summary>Avatar changes (<c>/avatar/change</c>): when and which avatar.</summary>
        public readonly List<(double time, string id)> Avatars = new List<(double, string)>();

        public const string ParametersPrefix = "/avatar/parameters/";

        public static ParameterTimeline Build(PerformanceSession session)
        {
            var timeline = new ParameterTimeline();
            if (session.Parameters == null) return timeline;
            var byId = new Dictionary<ushort, Track>();
            foreach (var p in session.Parameters.Parameters)
            {
                var track = new Track { Name = p.Name, Address = p.Address, IsAvatarParameter = p.Address != null && p.Address.StartsWith(ParametersPrefix, StringComparison.Ordinal) };
                byId[p.Id] = track;
                timeline.Tracks[p.Name] = track;
            }
            // Initial values come first at time 0; the rest is in arrival order. A stable sort keeps both.
            foreach (var c in session.Changes.Select((c, i) => (c, i)).OrderBy(x => x.c.TimeNs).ThenBy(x => x.i).Select(x => x.c))
            {
                if (!byId.TryGetValue(c.Id, out var track)) continue;
                track.Times.Add(c.TimeNs * 1e-9);
                track.Values.Add(c.AsFloat);
                track.Kinds.Add(c.Kind);
            }
            foreach (var a in session.Parameters.Avatars) timeline.Avatars.Add((a.TimeNs * 1e-9, a.Id));
            return timeline;
        }

        public float ValueAt(string name, double seconds, float fallback = 0f) => Tracks.TryGetValue(name, out var t) && t.Times.Count > 0 ? t.ValueAt(seconds) : fallback;

        public bool Has(string name) => Tracks.TryGetValue(name, out var t) && t.Times.Count > 0;
    }

    /// <summary>Device poses as functions of time (play space, Unity conventions).</summary>
    internal interface IPoseSource
    {
        /// <summary>The device's pose at a time; false without a sample close enough.</summary>
        bool TryPose(int device, double seconds, out Vector3 position, out Quaternion rotation);
    }

    /// <summary>The SteamVR device poses as functions of time, interpolated between samples.</summary>
    internal sealed class TrackingTimeline : IPoseSource
    {
        readonly List<TrackingSample>[] perDevice;
        public readonly TrackingDevices Devices;

        /// <summary>A pose is only trusted this close to a valid sample (dropouts are not bridged further).</summary>
        public const double MaxGap = 0.25;

        public TrackingTimeline(PerformanceSession session)
        {
            Devices = session.Devices ?? new TrackingDevices();
            perDevice = new List<TrackingSample>[Devices.Devices.Count];
            for (int i = 0; i < perDevice.Length; i++) perDevice[i] = new List<TrackingSample>();
            foreach (var s in session.Samples)
                if (s.Device < perDevice.Length && s.PoseValid)
                    perDevice[s.Device].Add(s);
            foreach (var list in perDevice) list.Sort((a, b) => a.TimeNs.CompareTo(b.TimeNs));
        }

        public int Count => perDevice.Length;

        /// <summary>The device number with this role (<c>head</c>, <c>left_hand</c>, ...), or -1.</summary>
        public int FindRole(string role) => Devices.Devices.FindIndex(d => d.Role == role);

        public static Vector3 Position(TrackingSample s) => new Vector3(s.PositionX, s.PositionY, s.PositionZ);
        public static Quaternion Rotation(TrackingSample s) => new Quaternion(s.RotationX, s.RotationY, s.RotationZ, s.RotationW);

        /// <summary>The device's pose at a time (play space, Unity conventions); false without a sample close enough.</summary>
        public bool TryPose(int device, double seconds, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (device < 0 || device >= perDevice.Length) return false;
            var list = perDevice[device];
            if (list.Count == 0) return false;
            ulong t = (ulong)Math.Max(0, Math.Round(seconds * 1e9));
            int lo = 0, hi = list.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (list[mid].TimeNs <= t) lo = mid;
                else hi = mid - 1;
            }
            var a = list[lo];
            if (a.TimeNs > t)
            {
                if ((a.TimeNs - t) * 1e-9 > MaxGap) return false;
                position = Position(a);
                rotation = Rotation(a);
                return true;
            }
            if (lo + 1 < list.Count)
            {
                var b = list[lo + 1];
                double span = (b.TimeNs - a.TimeNs) * 1e-9;
                if (span <= MaxGap * 2)
                {
                    float k = span > 0 ? (float)((t - a.TimeNs) * 1e-9 / span) : 0f;
                    position = Vector3.Lerp(Position(a), Position(b), k);
                    rotation = Quaternion.Slerp(Rotation(a), Rotation(b), k);
                    return true;
                }
            }
            if ((t - a.TimeNs) * 1e-9 > MaxGap) return false;
            position = Position(a);
            rotation = Rotation(a);
            return true;
        }
    }

    /// <summary>
    /// The headset and wrist poses VRChat sends to OSCQuery services that ask for them (<c>/tracking/vrsystem/head/pose</c>,
    /// <c>leftwrist</c>, <c>rightwrist</c>: position in metres of the real play space, then Euler degrees the way
    /// <c>Quaternion.Euler</c> takes them, Unity axes), recorded one value per argument, as functions of time. VRChat places
    /// the wrists itself, from the controllers, so they need no T-pose.
    /// </summary>
    internal sealed class VrChatPoses : IPoseSource
    {
        public const int Head = 0, LeftWrist = 1, RightWrist = 2;
        static readonly string[] Parts = { "head", "leftwrist", "rightwrist" };

        /// <summary>OSC sends changes only: a pose holds this long without a new message (a hand held still), not more.</summary>
        public const double MaxHold = 1.0;

        public struct Sample
        {
            public double Time;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        readonly List<Sample>[] samples = { new List<Sample>(), new List<Sample>(), new List<Sample>() };

        public VrChatPoses(ParameterTimeline timeline)
        {
            for (int part = 0; part < Parts.Length; part++)
            {
                var tracks = new ParameterTimeline.Track[6];
                bool complete = true;
                for (int k = 0; k < 6 && complete; k++)
                    complete = timeline.Tracks.TryGetValue(Address(part) + "[" + k + "]", out tracks[k]) && tracks[k].Times.Count > 0;
                if (!complete) continue;
                // A message writes its six values at one time: a pose at every time one of them changed.
                foreach (double time in tracks.SelectMany(t => t.Times).Distinct().OrderBy(t => t))
                {
                    float V(int k) => tracks[k].ValueAt(time);
                    samples[part].Add(new Sample { Time = time, Position = new Vector3(V(0), V(1), V(2)), Rotation = Quaternion.Euler(V(3), V(4), V(5)) });
                }
            }
        }

        public static string Address(int part) => "/tracking/vrsystem/" + Parts[part] + "/pose";

        public bool Has(int part) => samples[part].Count > 0;

        public IReadOnlyList<Sample> Samples(int part) => samples[part];

        /// <summary>The pose at a time, interpolated between messages, held <see cref="MaxHold"/> after the last.</summary>
        public bool TryPose(int device, double seconds, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (device < 0 || device >= samples.Length || samples[device].Count == 0) return false;
            var list = samples[device];
            int lo = 0, hi = list.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (list[mid].Time <= seconds) lo = mid;
                else hi = mid - 1;
            }
            var a = list[lo];
            if (a.Time > seconds)
            {
                if (a.Time - seconds > MaxHold) return false;
            }
            else if (lo + 1 < list.Count && list[lo + 1].Time - a.Time <= MaxHold)
            {
                var b = list[lo + 1];
                double span = b.Time - a.Time;
                float k = span > 0 ? (float)((seconds - a.Time) / span) : 0f;
                position = Vector3.Lerp(a.Position, b.Position, k);
                rotation = Quaternion.Slerp(a.Rotation, b.Rotation, k);
                return true;
            }
            else if (seconds - a.Time > MaxHold)
            {
                return false;
            }
            position = a.Position;
            rotation = a.Rotation;
            return true;
        }
    }
}
