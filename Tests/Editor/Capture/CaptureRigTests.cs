using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Contracts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Capture.Tests
{
    /// <summary>
    /// The capture overlay on the GPU, checked against the contracts' CPU decoders: strips decode upright and bit for bit
    /// to the pose the rig reports, depth symbols to the quantised true depth, and the rig leaves nothing behind.
    /// Renders only the tests' own preview scene.
    /// </summary>
    public sealed class CaptureRigTests
    {
        private CaptureTestScene scene;

        [SetUp]
        public void SetUp() => scene = new CaptureTestScene();

        [TearDown]
        public void TearDown() => scene.Dispose();

        private static readonly CapturePose[] Poses =
        {
            new CapturePose(new Vector3(0f, 0.5f, -4f), Quaternion.identity),
            new CapturePose(new Vector3(-4f, 1.2f, 0.3f), Quaternion.Euler(0f, 90f, 0f)),
            new CapturePose(new Vector3(0.3f, 6f, -0.2f), Quaternion.Euler(90f, 0f, 0f)),
            new CapturePose(new Vector3(3.1f, 2.2f, -3.7f), Quaternion.Euler(23.5f, -38.25f, 3.1f)),
        };

        [Test]
        public void StripsDecodeUprightToTheExactPose([Values(1280, 1920)] int width)
        {
            int height = width * 9 / 16;
            using (var rig = new CaptureRig(CaptureTestScene.Settings(width, height), scene.Scene))
            {
                for (int i = 0; i < Poses.Length; i++)
                {
                    float time = 41.25f + i / 30f;
                    var frame = rig.Render(i, Poses[i], time, false);
                    Assert.That(CaptureStrip.TryDecode(CaptureTestScene.TopDown(frame), out var decoded, out string error), Is.True, "pose " + i + ": " + error);
                    Assert.That(decoded.Flipped, Is.False, "frames are stored top-down");
                    Assert.That(decoded.CorrectedBits, Is.EqualTo(0));
                    Assert.That(decoded.Payload.ToWords(), Is.EqualTo(frame.Payload.ToWords()), "pose " + i + " decodes bit for bit");
                    Assert.That(BitConverter.SingleToInt32Bits(decoded.Payload.Time), Is.EqualTo(BitConverter.SingleToInt32Bits(time)));
                    Assert.That(decoded.Payload.Flags, Is.EqualTo(StripFlags.Compose(StripFlags.KindColour, CaptureRig.CameraMode, 0, 0, StripFlags.SourceSimulator)));
                    Assert.That(decoded.Payload.ScreenWidth, Is.EqualTo(width));
                    var expected = Matrix4x4.TRS(Poses[i].Position, Poses[i].Rotation, Vector3.one);
                    for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 4; c++)
                        Assert.That(decoded.Payload.CameraToWorld[r * 4 + c], Is.EqualTo(expected[r, c]).Within(1e-5f), "camera-to-world row " + r + " column " + c);
                }
            }
        }

        [Test]
        public void DepthSymbolsCarryTheTrueDepth()
        {
            using (var rig = new CaptureRig(CaptureTestScene.Settings(1280, 720), scene.Scene))
            {
                var frame = rig.Render(3, Poses[0], 2f, true);
                var image = CaptureTestScene.TopDown(frame);
                Assert.That(CaptureStrip.TryDecode(image, out var strip, out _), Is.True);
                Assert.That(strip.Payload.IsDepthFrame, Is.True);
                Assert.That(DepthFrames.TryDecode(image, false, out var layout, out var codes, out int tilesOk, out int rejected), Is.True);
                Assert.That(rejected, Is.EqualTo(0));
                int samples = 0, drawn = 0;
                for (int gy = 0; gy < layout.GridHeight; gy++)
                for (int gx = 0; gx < layout.GridWidth; gx++)
                {
                    if (!layout.GridSampled(gx, gy)) continue;
                    layout.GridPixel(gx, gy, out int x, out int y);
                    float z = CaptureTestScene.DepthAt(frame, x, y);
                    int expected = CaptureFrame.Code(z);
                    Assert.That(Math.Abs(codes[gy * layout.GridWidth + gx] - expected), Is.LessThanOrEqualTo(1), "sample at " + x + ", " + y + " (" + z + " m)");
                    samples++;
                    if (expected != 0) drawn++;
                }
                Assert.That(tilesOk, Is.EqualTo(layout.Regions().Count), "every tile of the layout reads: 268 at 720p, 13 of them with cells twice as large");
                Assert.That(drawn, Is.GreaterThan(samples / 3), "the floor and the boxes fill much of the view");
                // The camera at z = -4 looks straight at the middle box, whose front face is at z = -0.5.
                float centre = CaptureTestScene.DepthAt(frame, 640, 360);
                Assert.That(centre, Is.EqualTo(4f + CaptureTestScene.FrontFaceZ).Within(0.01f), "ground truth is the eye depth in metres");
            }
        }

        [Test]
        public void TheRigRestoresGlobalsAndLeavesNothingBehind()
        {
            Shader.SetGlobalFloat("_VRChatCameraMode", 0f);
            using (var rig = new CaptureRig(CaptureTestScene.Settings(640, 360), scene.Scene))
                rig.Render(0, Poses[3], 0f, false);
            Assert.That(Shader.GetGlobalFloat("_VRChatCameraMode"), Is.EqualTo(0f));
            var left = Resources.FindObjectsOfTypeAll<Object>().Where(o => o != null && o.name.StartsWith(CaptureScene.ObjectPrefix, StringComparison.Ordinal)).Select(o => o.name + " (" + o.GetType().Name + ")").ToList();
            Assert.That(left, Is.Empty);
        }

#if MIXER_CAPTURE_PPV2
        [Test]
        public void TypicalPostProcessingKeepsStripAndDepth()
        {
            var settings = CaptureTestScene.Settings(1920, 1080);
            settings.Post = CapturePost.Typical;
            using (var rig = new CaptureRig(settings, scene.Scene))
            {
                StringAssert.StartsWith("Typical world", rig.PostNote);
                var colour = rig.Render(0, Poses[3], 1f, false);
                Assert.That(CaptureStrip.TryDecode(CaptureTestScene.TopDown(colour), out var decoded, out string error), Is.True, error);
                Assert.That(decoded.Payload.ToWords(), Is.EqualTo(colour.Payload.ToWords()));
                var depth = rig.Render(1, Poses[3], 1.1f, true);
                var image = CaptureTestScene.TopDown(depth);
                Assert.That(DepthFrames.TryDecode(image, false, out var layout, out var codes, out _, out _), Is.True);
                int samples = 0, within = 0;
                for (int gy = 0; gy < layout.GridHeight; gy++)
                for (int gx = 0; gx < layout.GridWidth; gx++)
                {
                    if (!layout.GridSampled(gx, gy)) continue;
                    layout.GridPixel(gx, gy, out int x, out int y);
                    samples++;
                    if (Math.Abs(codes[gy * layout.GridWidth + gx] - CaptureFrame.Code(CaptureTestScene.DepthAt(depth, x, y))) <= 1) within++;
                }
                Assert.That(within / (float)samples, Is.GreaterThan(0.99f), within + " of " + samples + " depth samples within ±1 under typical post-processing");
            }
        }
#endif
    }
}
