using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>Camera paths as one pose per frame: orbits, smooth waypoint paths and resampled Scene view recordings.</summary>
    internal static class CapturePaths
    {
        /// <summary>Samples per spline segment for the arc-length table (constant speed along the path).</summary>
        private const int SamplesPerSegment = 64;

        public static int FrameCount(float duration, float fps) => Mathf.Max(1, Mathf.RoundToInt(duration * fps));

        /// <summary>
        /// A circle of <paramref name="radius"/> around <paramref name="center"/>, <paramref name="height"/> above it,
        /// starting in front of it (+Z, where avatars face) and looking at the centre. A whole number of laps never
        /// repeats its first view.
        /// </summary>
        public static CapturePose[] Orbit(Vector3 center, float radius, float height, float laps, int frames)
        {
            var poses = new CapturePose[Mathf.Max(0, frames)];
            for (int i = 0; i < poses.Length; i++)
            {
                float angle = 2f * Mathf.PI * laps * i / poses.Length;
                var position = center + new Vector3(Mathf.Sin(angle) * radius, height, Mathf.Cos(angle) * radius);
                poses[i] = new CapturePose(position, LookAt(position, center));
            }
            return poses;
        }

        /// <summary>A radius and height that frame <paramref name="bounds"/> for a vertical field of view.</summary>
        public static void OrbitFor(Bounds bounds, float fieldOfView, out float radius, out float height)
        {
            float extent = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float half = Mathf.Max(5f, fieldOfView * 0.5f) * Mathf.Deg2Rad;
            radius = extent / Mathf.Sin(half) * 1.05f;
            height = bounds.extents.y * 0.35f;
        }

        public static Quaternion LookAt(Vector3 from, Vector3 to)
        {
            var forward = to - from;
            if (forward.sqrMagnitude < 1e-12f) return Quaternion.identity;
            var up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>
        /// A path through <paramref name="points"/> (centripetal Catmull-Rom for positions, Catmull-Rom on aligned
        /// quaternions for rotations) at constant speed. Open paths start at the first point and end at the last.
        /// </summary>
        public static CapturePose[] Spline(IList<CapturePose> points, bool loop, int frames)
        {
            var poses = new CapturePose[Mathf.Max(0, frames)];
            int n = points.Count;
            if (n == 0 || poses.Length == 0) return n == 0 ? Array.Empty<CapturePose>() : poses;
            if (n == 1)
            {
                for (int i = 0; i < poses.Length; i++) poses[i] = points[0];
                return poses;
            }
            var rotations = AlignedRotations(points);
            int segments = loop ? n : n - 1;
            var lengths = new float[segments * SamplesPerSegment + 1];
            var previous = Position(points, loop, 0, 0f);
            for (int s = 0; s < segments; s++)
            for (int k = 1; k <= SamplesPerSegment; k++)
            {
                var p = Position(points, loop, s, k / (float)SamplesPerSegment);
                int at = s * SamplesPerSegment + k;
                lengths[at] = lengths[at - 1] + Vector3.Distance(previous, p);
                previous = p;
            }
            float total = lengths[lengths.Length - 1];
            for (int i = 0; i < poses.Length; i++)
            {
                float u = loop ? i / (float)poses.Length : poses.Length > 1 ? i / (float)(poses.Length - 1) : 0f;
                float along = total > 1e-6f ? ParameterAt(lengths, u * total) : u * segments * SamplesPerSegment;
                float param = along / SamplesPerSegment;
                int segment = Mathf.Min((int)param, segments - 1);
                float t = Mathf.Clamp01(param - segment);
                poses[i] = new CapturePose(Position(points, loop, segment, t), Rotation(rotations, loop, segment, t));
            }
            return poses;
        }

        /// <summary>
        /// One pose per frame at <paramref name="fps"/> from the recording's first sample to its last, interpolated
        /// between samples (positions linearly, rotations spherically).
        /// </summary>
        public static CapturePose[] Resample(SceneViewRecording recording, float fps)
        {
            int count = recording.Count;
            if (count == 0) return Array.Empty<CapturePose>();
            int frames = Mathf.FloorToInt(recording.Duration * fps + 1e-3f) + 1;
            var poses = new CapturePose[frames];
            float start = recording.Times[0];
            int j = 0;
            for (int i = 0; i < frames; i++)
            {
                float time = start + i / fps;
                while (j < count - 2 && recording.Times[j + 1] <= time) j++;
                if (count == 1)
                {
                    poses[i] = new CapturePose(recording.Positions[0], recording.Rotations[0]);
                    continue;
                }
                float t0 = recording.Times[j], t1 = recording.Times[j + 1];
                float u = t1 > t0 ? Mathf.Clamp01((time - t0) / (t1 - t0)) : 1f;
                poses[i] = new CapturePose(Vector3.Lerp(recording.Positions[j], recording.Positions[j + 1], u),
                    Quaternion.Slerp(recording.Rotations[j], recording.Rotations[j + 1], u));
            }
            return poses;
        }

        /// <summary>Path length of a pose list (for the window's summary).</summary>
        public static float Length(IList<CapturePose> poses)
        {
            float length = 0f;
            for (int i = 1; i < poses.Count; i++) length += Vector3.Distance(poses[i - 1].Position, poses[i].Position);
            return length;
        }

        private static float ParameterAt(float[] lengths, float target)
        {
            int lo = 0, hi = lengths.Length - 1;
            if (target <= 0f) return 0f;
            if (target >= lengths[hi]) return hi;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (lengths[mid] < target) lo = mid;
                else hi = mid;
            }
            float span = lengths[hi] - lengths[lo];
            return lo + (span > 0f ? (target - lengths[lo]) / span : 0f);
        }

        private static Vector3 Point(IList<CapturePose> points, bool loop, int i)
        {
            int n = points.Count;
            if (loop) return points[((i % n) + n) % n].Position;
            if (i < 0) return 2f * points[0].Position - points[1].Position;
            if (i >= n) return 2f * points[n - 1].Position - points[n - 2].Position;
            return points[i].Position;
        }

        /// <summary>Centripetal Catmull-Rom (Barry and Goldman's pyramid) on segment <paramref name="segment"/>.</summary>
        private static Vector3 Position(IList<CapturePose> points, bool loop, int segment, float t)
        {
            Vector3 p0 = Point(points, loop, segment - 1), p1 = Point(points, loop, segment);
            Vector3 p2 = Point(points, loop, segment + 1), p3 = Point(points, loop, segment + 2);
            float t0 = 0f;
            float t1 = t0 + Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
            float tt = Mathf.Lerp(t1, t2, t);
            var a1 = (t1 - tt) / (t1 - t0) * p0 + (tt - t0) / (t1 - t0) * p1;
            var a2 = (t2 - tt) / (t2 - t1) * p1 + (tt - t1) / (t2 - t1) * p2;
            var a3 = (t3 - tt) / (t3 - t2) * p2 + (tt - t2) / (t3 - t2) * p3;
            var b1 = (t2 - tt) / (t2 - t0) * a1 + (tt - t0) / (t2 - t0) * a2;
            var b2 = (t3 - tt) / (t3 - t1) * a2 + (tt - t1) / (t3 - t1) * a3;
            return (t2 - tt) / (t2 - t1) * b1 + (tt - t1) / (t2 - t1) * b2;
        }

        private static float Knot(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Sqrt(Vector3.Distance(a, b)), 1e-3f);

        /// <summary>Rotations flipped onto one hemisphere in order, so interpolation takes the short way.</summary>
        private static Quaternion[] AlignedRotations(IList<CapturePose> points)
        {
            var q = new Quaternion[points.Count];
            for (int i = 0; i < q.Length; i++)
            {
                q[i] = points[i].Rotation.normalized;
                if (i > 0 && Quaternion.Dot(q[i - 1], q[i]) < 0f) q[i] = new Quaternion(-q[i].x, -q[i].y, -q[i].z, -q[i].w);
            }
            return q;
        }

        private static Quaternion RotationPoint(Quaternion[] q, bool loop, int i, Quaternion reference)
        {
            int n = q.Length;
            var r = loop ? q[((i % n) + n) % n] : q[Mathf.Clamp(i, 0, n - 1)];
            return Quaternion.Dot(reference, r) < 0f ? new Quaternion(-r.x, -r.y, -r.z, -r.w) : r;
        }

        private static Quaternion Rotation(Quaternion[] q, bool loop, int segment, float t)
        {
            var q1 = RotationPoint(q, loop, segment, q[Mathf.Clamp(segment, 0, q.Length - 1)]);
            var q0 = RotationPoint(q, loop, segment - 1, q1);
            var q2 = RotationPoint(q, loop, segment + 1, q1);
            var q3 = RotationPoint(q, loop, segment + 2, q2);
            float t2 = t * t, t3 = t2 * t;
            var r = new Vector4(
                CatmullRom(q0.x, q1.x, q2.x, q3.x, t, t2, t3), CatmullRom(q0.y, q1.y, q2.y, q3.y, t, t2, t3),
                CatmullRom(q0.z, q1.z, q2.z, q3.z, t, t2, t3), CatmullRom(q0.w, q1.w, q2.w, q3.w, t, t2, t3));
            if (r.sqrMagnitude < 1e-12f) return q1;
            r.Normalize();
            return new Quaternion(r.x, r.y, r.z, r.w);
        }

        private static float CatmullRom(float a, float b, float c, float d, float t, float t2, float t3) =>
            0.5f * (2f * b + (c - a) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (3f * b - a - 3f * c + d) * t3);
    }
}
