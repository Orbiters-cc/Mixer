using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using Orbiters.Contracts;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Capture.Tests
{
    /// <summary>
    /// A whole capture into a temporary folder: the raw capture layout of Docs/raw-capture.md, files Orbiters desktop can
    /// read back, ground truth that matches the frames, a self-check that passes, and Cancel keeping a valid shorter capture.
    /// </summary>
    public sealed class CaptureRunTests
    {
        private CaptureTestScene scene;
        private string folder;

        [SetUp]
        public void SetUp()
        {
            scene = new CaptureTestScene();
            folder = Path.Combine(Path.GetTempPath(), "OrbitersMixerCaptureTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            scene.Dispose();
            var parent = Path.GetDirectoryName(folder);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }

        private static CapturePose[] Orbit(int frames) => CapturePaths.Orbit(new Vector3(0f, 0.5f, 0f), 5f, 1.5f, 1f, frames);

        [Test]
        public void WritesARawCaptureThatDecodesAndChecks()
        {
            var settings = CaptureTestScene.Settings(640, 360, 3);
            var run = CaptureRun.Start(settings, Orbit(12), folder, scene.Scene, false);
            Assert.That(run.RunToEnd(TimeSpan.FromSeconds(120)), Is.True, run.Error);
            Assert.That(run.State, Is.EqualTo(CaptureRun.Phase.Done), run.Error);

            var check = run.Check;
            Assert.That(check.StripChecked, Is.EqualTo(12), "small captures are checked whole");
            Assert.That(check.Passed, Is.True, check + "\n" + string.Join("\n", check.Problems));
            Assert.That(check.DepthFramesChecked, Is.EqualTo(4), "frames 2, 5, 8 and 11");
            Assert.That(check.DepthSamples, Is.GreaterThan(0));

            var capture = JsonConvert.DeserializeObject<RawCapture>(File.ReadAllText(Path.Combine(folder, RawCapture.FileName)));
            Assert.That(capture.Format, Is.EqualTo(RawCapture.FormatName));
            Assert.That(capture.Version, Is.EqualTo(1));
            Assert.That(capture.Source, Is.EqualTo("unity-simulator"));
            Assert.That((capture.Width, capture.Height, capture.FrameCount, capture.DepthEvery), Is.EqualTo((640, 360, 12, 3)));
            Assert.That(capture.Fps, Is.EqualTo(30f));
            Assert.That(capture.PostProfile, Is.EqualTo("none"));
            Assert.That(capture.HasGroundTruth, Is.True);
            Assert.That(capture.World?.Name, Is.Not.Null.And.Not.Empty);
            Assert.That(DateTime.TryParse(capture.CreatedUtc, out _), Is.True);

            var lines = File.ReadAllLines(Path.Combine(folder, RawCapture.PosesFile));
            Assert.That(lines.Length, Is.EqualTo(12));
            for (int i = 0; i < 12; i++)
            {
                var pose = JsonConvert.DeserializeObject<GroundTruthPose>(lines[i]);
                Assert.That(pose.Index, Is.EqualTo(i));
                Assert.That(pose.DepthFrame, Is.EqualTo(i % 3 == 2));
                Assert.That(pose.Time, Is.EqualTo(settings.TimeOf(i)));
                var image = Qoi.Decode(File.ReadAllBytes(Path.Combine(folder, RawCapture.FramesDir, RawCapture.FrameFile(i))));
                Assert.That((image.Width, image.Height), Is.EqualTo((640, 360)));
                Assert.That(CaptureStrip.TryDecode(image, out var decoded, out string error), Is.True, error);
                // Exact floats are compared as text: Unity's Mono parses "-0.0" as +0.0, so the parsed pose cannot show the sign.
                var fromStrip = new GroundTruthPose
                {
                    Index = i, Time = decoded.Payload.Time, CameraToWorld = decoded.Payload.CameraToWorld, Projection = decoded.Payload.Projection,
                    Near = decoded.Payload.Near, Far = decoded.Payload.Far, DepthFrame = decoded.Payload.IsDepthFrame,
                };
                Assert.That(lines[i], Is.EqualTo(JsonConvert.SerializeObject(fromStrip)), "poses.jsonl holds what frame " + i + "'s strip says, bit for bit");

                var depth = GroundTruthDepth.Decode(File.ReadAllBytes(Path.Combine(folder, RawCapture.GroundTruthDepthDir, RawCapture.GroundTruthDepthFile(i))), out int w, out int h);
                Assert.That((w, h), Is.EqualTo((640, 360)));
                Assert.That(depth.Count(z => z > 0f), Is.GreaterThan(640 * 360 / 4), "the floor and the boxes have depth");
                Assert.That(depth.Max(), Is.LessThan(40f), "the floor ends 10 m away");
            }
            Assert.That(Directory.GetFiles(Path.Combine(folder, RawCapture.FramesDir)).Length, Is.EqualTo(12));
            Assert.That(Directory.GetFiles(Path.Combine(folder, RawCapture.GroundTruthDepthDir)).Length, Is.EqualTo(12));
        }

        [Test]
        public void CancelKeepsTheRenderedFramesAsAValidCapture()
        {
            var run = CaptureRun.Start(CaptureTestScene.Settings(640, 360), Orbit(40), folder, scene.Scene, false);
            run.Step(0);
            Assert.That(run.Rendered, Is.GreaterThan(0));
            run.Cancel();
            Assert.That(run.RunToEnd(TimeSpan.FromSeconds(60)), Is.True, run.Error);
            Assert.That(run.State, Is.EqualTo(CaptureRun.Phase.Cancelled));
            Assert.That(run.Rendered, Is.LessThan(40));
            var capture = JsonConvert.DeserializeObject<RawCapture>(File.ReadAllText(Path.Combine(folder, RawCapture.FileName)));
            Assert.That(capture.FrameCount, Is.EqualTo(run.Rendered));
            Assert.That(Directory.GetFiles(Path.Combine(folder, RawCapture.FramesDir)).Length, Is.EqualTo(run.Rendered));
            Assert.That(File.ReadAllLines(Path.Combine(folder, RawCapture.PosesFile)).Length, Is.EqualTo(run.Rendered));
        }

        [Test]
        public void RefusesAFolderThatIsNotEmpty()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep me");
            Assert.Throws<IOException>(() => CaptureRun.Start(CaptureTestScene.Settings(640, 360), Orbit(4), folder, scene.Scene, false));
            Assert.That(File.ReadAllText(Path.Combine(folder, "notes.txt")), Is.EqualTo("keep me"));
        }

        [Test]
        public void NewFoldersAreNamedAfterTheSceneAndTime()
        {
            string parent = Path.GetDirectoryName(folder);
            Directory.CreateDirectory(parent);
            var now = new DateTime(2026, 10, 20, 1, 2, 3);
            string first = CaptureScene.NewCaptureFolder(parent, "My: World", now);
            Assert.That(Path.GetFileName(first), Is.EqualTo("My_ World 2026-10-20 01-02-03"));
            Directory.CreateDirectory(first);
            try
            {
                Assert.That(Path.GetFileName(CaptureScene.NewCaptureFolder(parent, "My: World", now)), Is.EqualTo("My_ World 2026-10-20 01-02-03 (2)"));
            }
            finally
            {
                Directory.Delete(first);
            }
            StringAssert.EndsWith(Path.Combine("Orbiters", "Raw captures"), CaptureScene.DefaultOutputRoot);
        }
    }
}
