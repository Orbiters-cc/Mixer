using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Capture.Tests
{
    /// <summary>Camera paths: orbits look at their centre, waypoint paths pass through every point at constant speed, recordings resample.</summary>
    public sealed class CapturePathTests
    {
        [Test]
        public void OrbitStartsInFrontCirclesAndLooksAtTheCentre()
        {
            var center = new Vector3(1f, 0.8f, -2f);
            var poses = CapturePaths.Orbit(center, 2f, 0.5f, 1f, 36);
            Assert.That(poses.Length, Is.EqualTo(36));
            Assert.That(poses[0].Position.z, Is.GreaterThan(center.z + 1.9f), "the first frame is in front of the target (+Z, where avatars face)");
            foreach (var pose in poses)
            {
                var flat = new Vector2(pose.Position.x - center.x, pose.Position.z - center.z);
                Assert.That(flat.magnitude, Is.EqualTo(2f).Within(1e-4f));
                Assert.That(pose.Position.y, Is.EqualTo(center.y + 0.5f).Within(1e-5f));
                Assert.That(Vector3.Dot(pose.Rotation * Vector3.forward, (center - pose.Position).normalized), Is.GreaterThan(0.99999f));
            }
            Assert.That(Vector3.Distance(poses[18].Position, 2f * new Vector3(center.x, poses[0].Position.y, center.z) - poses[0].Position), Is.LessThan(1e-4f),
                "half a lap later the camera is on the other side");
            Assert.That(Vector3.Distance(poses[35].Position, poses[0].Position), Is.GreaterThan(0.1f), "a closed lap does not repeat its first view");
        }

        [Test]
        public void SplinePassesThroughEveryWaypointAtConstantSpeed()
        {
            var points = new List<CapturePose>
            {
                new CapturePose(new Vector3(0, 1, 0), Quaternion.Euler(0, 0, 0)),
                new CapturePose(new Vector3(4, 1, 0), Quaternion.Euler(0, 90, 0)),
                new CapturePose(new Vector3(4, 2, 3), Quaternion.Euler(10, 180, 0)),
                new CapturePose(new Vector3(-1, 1.5f, 5), Quaternion.Euler(0, 270, 0)),
            };
            var poses = CapturePaths.Spline(points, false, 241);
            Assert.That(Vector3.Distance(poses[0].Position, points[0].Position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(poses[240].Position, points[3].Position), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(poses[0].Rotation, points[0].Rotation), Is.LessThan(0.05f));
            Assert.That(Quaternion.Angle(poses[240].Rotation, points[3].Rotation), Is.LessThan(0.05f));
            foreach (var p in points)
                Assert.That(poses.Min(q => Vector3.Distance(q.Position, p.Position)), Is.LessThan(0.06f), "the path goes through " + p.Position);
            var steps = Enumerable.Range(1, poses.Length - 1).Select(i => Vector3.Distance(poses[i - 1].Position, poses[i].Position)).ToList();
            Assert.That(steps.Max() / steps.Min(), Is.LessThan(1.05f), "constant speed along the path");
            for (int i = 1; i < poses.Length; i++)
                Assert.That(Quaternion.Angle(poses[i - 1].Rotation, poses[i].Rotation), Is.LessThan(5f), "rotations turn smoothly");
        }

        [Test]
        public void LoopingSplineComesBackToItsStart()
        {
            var points = new List<CapturePose>
            {
                new CapturePose(new Vector3(0, 0, 0), Quaternion.identity),
                new CapturePose(new Vector3(3, 0, 0), Quaternion.Euler(0, 120, 0)),
                new CapturePose(new Vector3(1.5f, 0, 3), Quaternion.Euler(0, 240, 0)),
            };
            var poses = CapturePaths.Spline(points, true, 90);
            var steps = Enumerable.Range(1, poses.Length - 1).Select(i => Vector3.Distance(poses[i - 1].Position, poses[i].Position)).ToList();
            float closing = Vector3.Distance(poses[poses.Length - 1].Position, poses[0].Position);
            Assert.That(closing, Is.EqualTo(steps.Average()).Within(steps.Average() * 0.06f), "the last frame is one step before the first");
        }

        [Test]
        public void OneWaypointHoldsStill()
        {
            var pose = new CapturePose(new Vector3(1, 2, 3), Quaternion.Euler(5, 6, 7));
            var poses = CapturePaths.Spline(new List<CapturePose> { pose }, false, 10);
            Assert.That(poses.All(p => p.Position == pose.Position && p.Rotation == pose.Rotation), Is.True);
        }

        [Test]
        public void RecordingIsResampledAtTheFrameRate()
        {
            var recording = new SceneViewRecording();
            recording.Add(3f, new Vector3(0, 0, 0), Quaternion.identity);
            recording.Add(3.4f, new Vector3(0.4f, 0, 0), Quaternion.identity);
            recording.Add(4f, new Vector3(1, 0, 0), Quaternion.Euler(0, 90, 0));
            recording.Add(4f, new Vector3(1, 0, 0), Quaternion.Euler(0, 90, 0));
            recording.Add(5f, new Vector3(1, 0, 2), Quaternion.Euler(0, 90, 0));
            Assert.That(recording.Count, Is.EqualTo(4), "a repeated time replaces the last sample");
            var poses = CapturePaths.Resample(recording, 10f);
            Assert.That(poses.Length, Is.EqualTo(21), "2 s at 10 fps, both ends included");
            Assert.That(Vector3.Distance(poses[5].Position, new Vector3(0.5f, 0, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(poses[15].Position, new Vector3(1, 0, 1)), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(poses[7].Rotation, Quaternion.Euler(0, 45, 0)), Is.LessThan(0.01f), "rotations are interpolated spherically");
            Assert.That(Vector3.Distance(poses[20].Position, new Vector3(1, 0, 2)), Is.LessThan(1e-4f));
        }

        [Test]
        public void FrameCountsAndDepthFrames()
        {
            Assert.That(CapturePaths.FrameCount(8f, 30f), Is.EqualTo(240));
            Assert.That(CapturePaths.FrameCount(0.01f, 24f), Is.EqualTo(1));
            var settings = new CaptureSettings { DepthEvery = 4 };
            Assert.That(Enumerable.Range(0, 12).Where(settings.IsDepthFrame), Is.EqualTo(new[] { 3, 7, 11 }));
            settings.DepthEvery = 0;
            Assert.That(Enumerable.Range(0, 12).Any(settings.IsDepthFrame), Is.False);
            settings.Fps = 30f;
            Assert.That(settings.TimeOf(30), Is.EqualTo(1f));
        }
    }
}
