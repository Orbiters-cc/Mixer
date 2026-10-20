using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Contracts;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Performance.Tests
{
    /// <summary>
    /// Mixer's performance import. Everything runs on generated objects in preview scenes and a temporary asset folder:
    /// the open scene, the selection and the project are left as they were.
    /// </summary>
    public sealed class PerformanceTests
    {
        const string Folder = "Assets/__MixerPerformanceTests";
        Scene scene;

        [SetUp]
        public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        // ------------------------------------------------------------------------------------------- curves, names

        [Test]
        public void CurveReductionKeepsTheShapeWithinTolerance()
        {
            var times = Enumerable.Range(0, 601).Select(i => i / 60f).ToList();
            var values = times.Select(t => Mathf.Sin(t * 2f) * 50f + (t > 5f ? 20f : 0f)).ToList();
            var kept = CurveSet.Reduce(times, values, 0.25f);
            Assert.Less(kept.Count, 200, "far fewer keys than samples");
            Assert.AreEqual(0, kept[0]);
            Assert.AreEqual(600, kept[kept.Count - 1]);
            for (int k = 0; k + 1 < kept.Count; k++)
                for (int i = kept[k] + 1; i < kept[k + 1]; i++)
                {
                    float u = (times[i] - times[kept[k]]) / (times[kept[k + 1]] - times[kept[k]]);
                    float line = values[kept[k]] + (values[kept[k + 1]] - values[kept[k]]) * u;
                    Assert.LessOrEqual(Mathf.Abs(values[i] - line), 0.25f + 1e-4f);
                }
        }

        [Test]
        public void FingerMusclesUseTheClipSpelling()
        {
            Assert.AreEqual("LeftHand.Thumb.1 Stretched", CurveSet.MuscleProperty("Left Thumb 1 Stretched"));
            Assert.AreEqual("RightHand.Little.Spread", CurveSet.MuscleProperty("Right Little Spread"));
            Assert.AreEqual("Spine Front-Back", CurveSet.MuscleProperty("Spine Front-Back"));
            // Every muscle name maps to a property Unity knows for humanoid clips.
            var known = new HashSet<string>(HumanTrait.MuscleName.Select(CurveSet.MuscleProperty));
            Assert.AreEqual(HumanTrait.MuscleCount, known.Count);
        }

        // ------------------------------------------------------------------------------------------- timelines

        static PerformanceSession Session(params (double t, ushort id, float value)[] changes)
        {
            var s = new PerformanceSession { Name = "Test", Parameters = new ParameterTable() };
            foreach (var (name, id) in new[] { ("Smile", (ushort)0), ("Viseme", (ushort)1), ("Voice", (ushort)2) })
                s.Parameters.Parameters.Add(new ParameterTable.ParameterInfo { Id = id, Name = name, Address = "/avatar/parameters/" + name, Kind = id == 1 ? "int" : "float" });
            foreach (var (t, id, value) in changes)
                s.Changes.Add(id == 1 ? ParameterChange.Int((ulong)(t * 1e9), id, (int)value) : ParameterChange.Float((ulong)(t * 1e9), id, value));
            return s;
        }

        [Test]
        public void ParametersAreStepFunctionsOfTime()
        {
            var timeline = ParameterTimeline.Build(Session((0, 0, 0f), (0.5, 0, 1f), (1.0, 1, 10f), (1.5, 1, 0f)));
            Assert.AreEqual(0f, timeline.ValueAt("Smile", 0.49));
            Assert.AreEqual(1f, timeline.ValueAt("Smile", 0.5));
            Assert.AreEqual(10f, timeline.ValueAt("Viseme", 1.2));
            Assert.AreEqual(0f, timeline.ValueAt("Viseme", 2.0));
            Assert.AreEqual(7f, timeline.ValueAt("Missing", 1.0, 7f));
            Assert.IsTrue(timeline.Tracks["Smile"].IsAvatarParameter);
        }

        [Test]
        public void TrackingInterpolatesAndDoesNotBridgeDropouts()
        {
            var s = new PerformanceSession { Devices = new TrackingDevices() };
            s.Devices.Devices.Add(new TrackingDevices.Device { Class = "hmd", Role = "head" });
            TrackingSample At(double t, float x, float yaw) => new TrackingSample
            {
                TimeNs = (ulong)(t * 1e9), Device = 0, Flags = TrackingSample.FlagPoseValid | TrackingSample.FlagConnected, Result = 200,
                PositionX = x, PositionY = 1.6f, RotationY = Mathf.Sin(yaw * Mathf.Deg2Rad / 2f), RotationW = Mathf.Cos(yaw * Mathf.Deg2Rad / 2f),
            };
            s.Samples.Add(At(1.0, 0f, 0f));
            s.Samples.Add(At(1.1, 1f, 90f));
            s.Samples.Add(At(3.0, 2f, 0f));
            var tracking = new TrackingTimeline(s);
            Assert.AreEqual(0, tracking.FindRole("head"));
            Assert.IsTrue(tracking.TryPose(0, 1.05, out var p, out var r));
            Assert.AreEqual(0.5f, p.x, 1e-4f);
            Assert.AreEqual(45f, r.eulerAngles.y, 0.1f);
            Assert.IsFalse(tracking.TryPose(0, 2.0, out _, out _), "1.9 s without samples is a dropout");
            Assert.IsTrue(tracking.TryPose(0, 3.1, out p, out _));
            Assert.AreEqual(2f, p.x);
        }

        // ------------------------------------------------------------------------------------------- body

        [Test]
        public void TwoBoneIkReachesTargetsAndBendsTowardThePole()
        {
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            var c = new GameObject("c").transform;
            SceneManager.MoveGameObjectToScene(a.gameObject, scene);
            b.SetParent(a, false);
            c.SetParent(b, false);
            b.localPosition = new Vector3(0.3f, 0f, 0f);
            c.localPosition = new Vector3(0.25f, 0f, 0f);
            var target = new Vector3(0.2f, 0.3f, 0.1f);
            BodySolver.TwoBone(a, b, c, target, new Vector3(0.1f, 0.1f, 1f));
            Assert.Less(Vector3.Distance(c.position, target), 1e-3f);
            Assert.Greater(b.position.z, 0f, "the elbow went toward the pole");
            Assert.AreEqual(0.3f, Vector3.Distance(a.position, b.position), 1e-4f, "bones keep their length");
            // Out of reach: the limb points at the target, straight.
            var far = new Vector3(2f, 0f, 0f);
            BodySolver.TwoBone(a, b, c, far, Vector3.forward);
            Assert.Greater(Vector3.Dot((c.position - a.position).normalized, far.normalized), 0.999f);
        }

        [Test]
        public void TPoseCalibrationBindsDevicesToBones()
        {
            var avatar = TestAvatar.Build(scene);
            var rig = new BodyRig(avatar.GetComponent<Animator>());
            Assert.IsTrue(rig.Valid);
            var c = new Calibrations.Calibration { HeadHeightM = 1.75f, Source = "overlay" };
            var devices = new TrackingDevices();
            void Device(string cls, string role, string serial, Vector3 p)
            {
                devices.Devices.Add(new TrackingDevices.Device { Class = cls, Role = role, Serial = serial });
                c.Devices.Add(new Calibrations.DevicePose { Index = (byte)(devices.Devices.Count - 1), Class = cls, Role = role, Serial = serial, Position = new[] { p.x, p.y, p.z }, Rotation = new[] { 0f, 0f, 0f, 1f } });
            }
            // A person 10 % taller than the avatar, standing at (1, 0, 2) of the play space, facing +Z.
            Device("hmd", "head", "H", new Vector3(1f, 1.75f, 2f));
            Device("controller", "left_hand", "L", new Vector3(1f - 0.8f, 1.5f, 2f));
            Device("controller", "right_hand", "R", new Vector3(1f + 0.8f, 1.5f, 2f));
            Device("tracker", "", "T1", new Vector3(1f, 1.05f, 2.05f));
            Device("tracker", "", "T2", new Vector3(1f - 0.1f, 0.1f, 2f));
            Device("tracker", "", "T3", new Vector3(1f + 0.1f, 0.1f, 2f));
            var warnings = new List<string>();
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var calibration = BodyCalibration.Build(c, devices, rig, avatar.transform.TransformPoint(descriptor.ViewPosition), warnings);
            Assert.IsNotNull(calibration, string.Join("\n", warnings));
            Assert.AreEqual(descriptor.ViewPosition.y / 1.75f, calibration.Scale, 1e-4f);
            CollectionAssert.AreEquivalent(new[] { BodyTarget.Head, BodyTarget.LeftHand, BodyTarget.RightHand, BodyTarget.Hips, BodyTarget.LeftFoot, BodyTarget.RightFoot }, calibration.Bindings.Keys);
            Assert.AreEqual(3, calibration.Bindings[BodyTarget.Hips].Device);
            Assert.AreEqual(4, calibration.Bindings[BodyTarget.LeftFoot].Device, "the tracker on the person's left foot (-X)");
            // The headset maps onto the avatar's eyes, as VRChat places the view.
            Assert.Less(Vector3.Distance(calibration.ToAvatar(new Vector3(1f, 1.75f, 2f)), avatar.transform.TransformPoint(descriptor.ViewPosition)), 1e-3f);

            // Moving the right controller 20 cm up and 20 cm in (the arm was straight in the T-pose, so the elbow bends)
            // brings the right hand there; the hips stay on their tracker.
            var solver = new BodySolver(rig);
            var targets = new Dictionary<BodyTarget, BodySolver.Pose>();
            foreach (var pair in calibration.Bindings)
            {
                var d = c.Devices[pair.Value.Device];
                var p = new Vector3(d.Position[0], d.Position[1], d.Position[2]);
                if (pair.Key == BodyTarget.RightHand) p += new Vector3(-0.2f, 0.2f, 0f);
                var dp = calibration.ToAvatar(p);
                var dr = calibration.ToAvatar(Quaternion.identity);
                targets[pair.Key] = new BodySolver.Pose { Valid = true, Position = dp + dr * pair.Value.PositionOffset, Rotation = dr * pair.Value.RotationOffset };
            }
            solver.Solve(targets, new TrackingState(), 1f / 60f);
            Assert.Less(Vector3.Distance(rig.RightHand.position, targets[BodyTarget.RightHand].Position), 0.02f);
            Assert.Less(Vector3.Distance(rig.Hips.position, targets[BodyTarget.Hips].Position), 1e-4f);
            Assert.Less(Vector3.Distance(rig.Head.position, targets[BodyTarget.Head].Position), 0.02f);
        }

        /// <summary>A session in which VRChat sent its head and wrist poses 90 times a second for <paramref name="seconds"/>.</summary>
        static PerformanceSession VrChatSession(double seconds, Func<double, int, (Vector3 position, Quaternion rotation)> pose)
        {
            var s = new PerformanceSession { Name = "VRChat poses", Parameters = new ParameterTable() };
            var ids = new ushort[3, 6];
            ushort id = 0;
            for (int part = 0; part < 3; part++)
                for (int k = 0; k < 6; k++)
                {
                    ids[part, k] = id;
                    s.Parameters.Parameters.Add(new ParameterTable.ParameterInfo { Id = id++, Name = VrChatPoses.Address(part) + "[" + k + "]", Address = VrChatPoses.Address(part), Kind = "float" });
                }
            for (int i = 0; i <= seconds * 90; i++)
            {
                double t = i / 90.0;
                for (int part = 0; part < 3; part++)
                {
                    var (p, r) = pose(t, part);
                    var e = r.eulerAngles;
                    float[] v = { p.x, p.y, p.z, e.x, e.y, e.z };
                    for (int k = 0; k < 6; k++) s.Changes.Add(ParameterChange.Float((ulong)Math.Round(t * 1e9), ids[part, k], v[k]));
                }
            }
            return s;
        }

        [Test]
        public void VrChatHeadAndWristsMoveTheBodyWithoutACalibration()
        {
            // Eyes at 1.75 m, standing at (1, 0, 2) facing +X, hands out in front (controllers upright) but hanging every
            // other second for a while. VRChat's wrist frames are made odd on purpose: the left has the fingers along +X
            // and the thumb along -Z, the right the fingers along -Y and the thumb along +X.
            var face = Quaternion.Euler(0f, 90f, 0f);
            Vector3 fwd = face * Vector3.forward, right = face * Vector3.right;
            var head = new Vector3(1f, 1.75f, 2f);
            var frames = new[] { Quaternion.LookRotation(Vector3.right, Vector3.back), Quaternion.LookRotation(Vector3.down, Vector3.right) };
            (Vector3 wrist, Vector3 fingers, Vector3 thumb) Hand(double t, float side)
            {
                var shoulder = head + face * new Vector3(0.18f * side, -0.25f, -0.08f);
                if (t % 2.0 > 1.6) return (shoulder + Vector3.down * 0.55f, Vector3.down, fwd);
                float sway = Mathf.Sin((float)t * 3f) * 0.1f;
                var fingers = (fwd * 0.95f + Vector3.down * (0.15f + sway) - right * side * 0.2f).normalized;
                return (shoulder + fwd * 0.4f + Vector3.down * 0.1f + Vector3.up * sway * 0.5f, fingers, Vector3.ProjectOnPlane(Vector3.up, fingers).normalized);
            }
            (Vector3, Quaternion) Pose(double t, int part)
            {
                if (part == VrChatPoses.Head) return (head + fwd * Mathf.Sin((float)t) * 0.05f, face * Quaternion.Euler(Mathf.Sin((float)t * 2f) * 10f, 0f, 0f));
                var (wrist, fingers, thumb) = Hand(t, part == VrChatPoses.LeftWrist ? -1f : 1f);
                return (wrist, Quaternion.LookRotation(fingers, thumb) * Quaternion.Inverse(frames[part - 1]));
            }
            var session = VrChatSession(6.0, Pose);
            var vr = new VrChatPoses(ParameterTimeline.Build(session));
            Assert.IsTrue(vr.Has(VrChatPoses.Head) && vr.Has(VrChatPoses.LeftWrist) && vr.Has(VrChatPoses.RightWrist));
            Assert.IsTrue(vr.TryPose(VrChatPoses.Head, 1.0 + 0.5 / 90.0, out var midway, out _));
            Assert.AreEqual(1.75f, midway.y, 1e-4f);
            Assert.IsTrue(vr.TryPose(VrChatPoses.Head, 6.5, out _, out _), "a pose holds without new messages");
            Assert.IsFalse(vr.TryPose(VrChatPoses.Head, 7.5, out _, out _), "but not for long");

            // The wrist frames are read from the movement.
            for (int part = VrChatPoses.LeftWrist; part <= VrChatPoses.RightWrist; part++)
            {
                var axes = BodyCalibration.WristAxes(vr, part, part == VrChatPoses.LeftWrist ? -1f : 1f, out bool sure);
                Assert.IsTrue(sure, "wrist " + part);
                Assert.Less(Quaternion.Angle(axes, frames[part - 1]), 0.1f, "wrist " + part);
            }

            var avatar = TestAvatar.Build(scene);
            var rig = new BodyRig(avatar.GetComponent<Animator>());
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var eye = avatar.transform.TransformPoint(descriptor.ViewPosition);
            var warnings = new List<string>();
            var calibration = BodyCalibration.FromVrChat(vr, rig, eye, 0.0, warnings);
            Assert.IsNotNull(calibration);
            CollectionAssert.IsEmpty(warnings);
            Assert.AreEqual(eye.y / 1.75f, calibration.Scale, 1e-3f);
            Assert.Less(Vector3.Angle(calibration.ToAvatar(face) * Vector3.forward, Vector3.forward), 0.01f, "the person's forward is the avatar's");
            var fingersBone = Quaternion.Inverse(rig.RightHand.rotation) * (rig.RightHand.position - rig.RightLowerArm.position).normalized;
            var thumbBone = Quaternion.Inverse(rig.RightHand.rotation) * Vector3.forward;

            // A second in, the hands reach the wrists, the right hand's fingers and thumb point where the person's do,
            // and the head sits at the eyes.
            const double at = 1.0;
            var targets = new Dictionary<BodyTarget, BodySolver.Pose>();
            foreach (var pair in calibration.Bindings)
            {
                Assert.IsTrue(vr.TryPose(pair.Value.Device, at, out var p, out var r));
                Vector3 dp = calibration.ToAvatar(p);
                Quaternion dr = calibration.ToAvatar(r);
                targets[pair.Key] = new BodySolver.Pose { Valid = true, Position = dp + dr * pair.Value.PositionOffset, Rotation = dr * pair.Value.RotationOffset };
            }
            new BodySolver(rig).Solve(targets, new TrackingState(), 1f / 60f);
            var (rightWrist, rightFingers, rightThumb) = Hand(at, 1f);
            Assert.Less(Vector3.Distance(rig.RightHand.position, calibration.ToAvatar(rightWrist)), 0.02f);
            Assert.Less(Vector3.Angle(rig.RightHand.rotation * fingersBone, calibration.PlayRotation * rightFingers), 0.5f);
            Assert.Less(Vector3.Angle(rig.RightHand.rotation * thumbBone, calibration.PlayRotation * rightThumb), 0.5f);
            Assert.Less(Vector3.Distance(rig.Head.position, targets[BodyTarget.Head].Position), 0.02f);

            // The bake takes that path by itself.
            var result = PerformanceBaker.Bake(session, descriptor, new BakeOptions { FrameRate = 30f, End = 2.0, Face = false, Audio = false, Timeline = false, OutputFolder = Folder });
            Assert.IsTrue(result.BodyFromVrChat, string.Join("\n", result.Warnings));
            Assert.IsFalse(result.BodyFromTracking);
        }

        [Test]
        public void SkeletonFramesPoseTheAvatarExactly()
        {
            var avatar = TestAvatar.Build(scene);
            var animator = avatar.GetComponent<Animator>();
            Quaternion Elbow(float degrees) => Quaternion.Euler(0f, 0f, degrees);
            SkeletonFrame Frame(double t, float degrees, float x)
            {
                var f = new SkeletonFrame { TimeNs = (ulong)(t * 1e9) };
                f.Pose.HipsX = x;
                f.Pose.HipsY = 0.95f;
                for (int b = 0; b < SkeletonPose.Bones; b++)
                    if (animator.GetBoneTransform((HumanBodyBones)b) != null)
                        f.Pose.Rotations[b] = new[] { 0f, 0f, 0f, 1f };
                var q = Elbow(degrees);
                f.Pose.Rotations[(int)HumanBodyBones.LeftLowerArm] = new[] { q.x, q.y, q.z, q.w };
                return f;
            }
            var replay = new SkeletonReplay(new List<SkeletonFrame> { Frame(1.0, 0f, 0f), Frame(1.1, 90f, 1f) }, animator);
            Assert.IsTrue(replay.Apply(1.05));
            var elbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Assert.AreEqual(45f, Quaternion.Angle(Quaternion.identity, elbow.rotation), 0.01f);
            Assert.AreEqual(0.5f, animator.GetBoneTransform(HumanBodyBones.Hips).position.x, 1e-5f);
            // The hand is a child of the elbow: its own world rotation stays as recorded (identity), not turned by 45°.
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation), 0.01f);
            Assert.IsFalse(replay.Apply(3.0), "after the last frame by more than the gap");
        }

        // ------------------------------------------------------------------------------------------- audio

        [Test]
        public void AudioLandsOnTheSessionClock()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixer-performance-audio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "audio"));
            try
            {
                // 1 kHz mono: sample n holds n / 10000. First frame at 0.25 s; the device clock runs 1 % fast.
                const int rate = 1000;
                var samples = Enumerable.Range(0, 3000).Select(n => n / 10000f).ToArray();
                using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "audio", "mic.wav"))))
                {
                    w.Write(WavHeader(rate, 1, samples.Length));
                    foreach (float v in samples) w.Write(v);
                }
                var track = new AudioTracks.Track { File = "audio/mic.wav", Kind = "mic", SampleRate = rate, Channels = 1, Frames = samples.Length, FirstFrameNs = 250_000_000 };
                track.Sync.Add(new long[] { 0, 250_000_000 });
                track.Sync.Add(new long[] { 1010, 1_250_000_000 });
                track.Sync.Add(new long[] { 2020, 2_250_000_000 });
                string output = Path.Combine(dir, "aligned.wav");
                AudioAlign.Write(track, dir, 0.5, 1.5, output);
                var bytes = File.ReadAllBytes(output);
                Assert.AreEqual(1, BitConverter.ToInt16(bytes, 20), "PCM");
                Assert.AreEqual(16, BitConverter.ToInt16(bytes, 34));
                int count = BitConverter.ToInt32(bytes, 40) / 2;
                Assert.AreEqual(1500, count);
                short Sample(int i) => BitConverter.ToInt16(bytes, 44 + i * 2);
                // Output sample 0 plays at 0.5 s: source frame 0.25 s × 1010 = 252.5.
                Assert.AreEqual(252.5f / 10000f * 32767f, Sample(0), 2f);
                // At 1.25 s (output 750) the source is exactly at frame 1010.
                Assert.AreEqual(1010f / 10000f * 32767f, Sample(750), 2f);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        static byte[] WavHeader(int rate, int channels, int frames)
        {
            var h = new byte[58];
            int data = frames * channels * 4;
            void Put(int at, string s) { for (int i = 0; i < 4; i++) h[at + i] = (byte)s[i]; }
            Put(0, "RIFF");
            BitConverter.GetBytes(50 + data).CopyTo(h, 4);
            Put(8, "WAVE");
            Put(12, "fmt ");
            BitConverter.GetBytes(18).CopyTo(h, 16);
            BitConverter.GetBytes((short)3).CopyTo(h, 20);
            BitConverter.GetBytes((short)channels).CopyTo(h, 22);
            BitConverter.GetBytes(rate).CopyTo(h, 24);
            BitConverter.GetBytes(rate * channels * 4).CopyTo(h, 28);
            BitConverter.GetBytes((short)(channels * 4)).CopyTo(h, 32);
            BitConverter.GetBytes((short)32).CopyTo(h, 34);
            Put(38, "fact");
            BitConverter.GetBytes(4).CopyTo(h, 42);
            BitConverter.GetBytes(frames).CopyTo(h, 46);
            Put(50, "data");
            BitConverter.GetBytes(data).CopyTo(h, 54);
            return h;
        }

        // ------------------------------------------------------------------------------------------- bake

        [Test]
        public void BakesTheAnimatorsResponseAndTheVisemes()
        {
            var avatar = TestAvatar.Build(scene, withFx: true, folder: Folder);
            var session = Session((0, 0, 0f), (0.5, 0, 1f), (1.0, 1, 10f), (1.0, 2, 0.8f), (1.5, 1, 0f), (2.0, 0, 0f));
            var result = PerformanceBaker.Bake(session, avatar.GetComponent<VRCAvatarDescriptor>(), new BakeOptions { FrameRate = 30f, End = 2.0, Audio = false, Timeline = false, OutputFolder = Folder });
            Assert.IsNotNull(result.Clip, string.Join("\n", result.Warnings));
            Assert.AreEqual(61, result.Frames);
            var bindings = AnimationUtility.GetCurveBindings(result.Clip);
            AnimationCurve Curve(string property) => AnimationUtility.GetEditorCurve(result.Clip, bindings.First(b => b.propertyName == property));

            // The FX blend tree follows Smile (0 → 1 at 0.5 s, back to 0 at 2 s).
            var smile = Curve("blendShape.Smile");
            Assert.AreEqual(0f, smile.Evaluate(0.25f), 0.5f);
            Assert.AreEqual(100f, smile.Evaluate(1.0f), 0.5f);
            // Viseme 10 (aa) at voice 0.8 from 1 s to 1.5 s, eased.
            var aa = Curve("blendShape.vrc.v_aa");
            Assert.AreEqual(0f, aa.Evaluate(0.9f), 0.5f);
            Assert.Greater(aa.Evaluate(1.4f), 95f);
            Assert.Less(aa.Evaluate(1.9f), 5f);
            // A humanoid clip: every muscle and the root.
            Assert.IsTrue(bindings.Any(b => b.propertyName == "RootT.y"));
            Assert.AreEqual(HumanTrait.MuscleCount, bindings.Count(b => b.type == typeof(Animator) && !b.propertyName.StartsWith("Root")));
            Assert.IsFalse(result.BodyFromTracking);
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<AnimationClip>(result.ClipPath) != null);
        }
    }

    /// <summary>A small humanoid with a VRChat descriptor, a face mesh with visemes and, optionally, an FX layer.</summary>
    internal static class TestAvatar
    {
        static readonly (string bone, string parent, Vector3 position)[] Skeleton =
        {
            ("Hips", null, new Vector3(0f, 0.95f, 0f)),
            ("Spine", "Hips", new Vector3(0f, 1.05f, 0f)),
            ("Chest", "Spine", new Vector3(0f, 1.2f, 0f)),
            ("Neck", "Chest", new Vector3(0f, 1.45f, 0f)),
            ("Head", "Neck", new Vector3(0f, 1.55f, 0f)),
            ("LeftShoulder", "Chest", new Vector3(-0.05f, 1.4f, 0f)),
            ("LeftUpperArm", "LeftShoulder", new Vector3(-0.17f, 1.4f, 0f)),
            ("LeftLowerArm", "LeftUpperArm", new Vector3(-0.45f, 1.4f, 0f)),
            ("LeftHand", "LeftLowerArm", new Vector3(-0.7f, 1.4f, 0f)),
            ("RightShoulder", "Chest", new Vector3(0.05f, 1.4f, 0f)),
            ("RightUpperArm", "RightShoulder", new Vector3(0.17f, 1.4f, 0f)),
            ("RightLowerArm", "RightUpperArm", new Vector3(0.45f, 1.4f, 0f)),
            ("RightHand", "RightLowerArm", new Vector3(0.7f, 1.4f, 0f)),
            ("LeftUpperLeg", "Hips", new Vector3(-0.09f, 0.9f, 0f)),
            ("LeftLowerLeg", "LeftUpperLeg", new Vector3(-0.09f, 0.5f, 0f)),
            ("LeftFoot", "LeftLowerLeg", new Vector3(-0.09f, 0.08f, 0f)),
            ("RightUpperLeg", "Hips", new Vector3(0.09f, 0.9f, 0f)),
            ("RightLowerLeg", "RightUpperLeg", new Vector3(0.09f, 0.5f, 0f)),
            ("RightFoot", "RightLowerLeg", new Vector3(0.09f, 0.08f, 0f)),
        };

        public static GameObject Build(Scene scene, bool withFx = false, string folder = null)
        {
            var root = new GameObject("Test avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var armature = new GameObject("Armature").transform;
            armature.SetParent(root.transform, false);
            var bones = new Dictionary<string, Transform>();
            foreach (var (bone, parent, position) in Skeleton)
            {
                var t = new GameObject(bone).transform;
                t.SetParent(parent == null ? armature : bones[parent], false);
                t.position = position;
                bones[bone] = t;
            }
            var human = Skeleton.Select(s => new HumanBone { humanName = s.bone, boneName = s.bone, limit = new HumanLimit { useDefaultValues = true } }).ToArray();
            var skeleton = root.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
            var description = new HumanDescription
            {
                human = human, skeleton = skeleton, upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.hideFlags = HideFlags.DontSave;
            Assert.IsTrue(avatar.isValid && avatar.isHuman, "generated humanoid");
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;

            // Face: a mesh with a Smile shape and the "aa" viseme.
            var face = new GameObject("Body");
            face.transform.SetParent(root.transform, false);
            var smr = face.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "Test face", hideFlags = HideFlags.DontSave, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            var deltas = new[] { Vector3.zero, Vector3.zero, Vector3.up * 0.1f };
            mesh.AddBlendShapeFrame("Smile", 100f, deltas, null, null);
            mesh.AddBlendShapeFrame("vrc.v_aa", 100f, deltas, null, null);
            smr.sharedMesh = mesh;

            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.ViewPosition = new Vector3(0f, 1.6f, 0.06f);
            descriptor.lipSync = VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape;
            descriptor.VisemeSkinnedMesh = smr;
            descriptor.VisemeBlendShapes = Enumerable.Range(0, 15).Select(i => i == 10 ? "vrc.v_aa" : "").ToArray();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Base, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Additive, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Gesture, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Action, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = !withFx, isEnabled = true, animatorController = withFx ? FxController(folder) : null },
            };
            descriptor.specialAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Sitting, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.TPose, isDefault = true, isEnabled = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.IKPose, isDefault = true, isEnabled = true },
            };
            return root;
        }

        /// <summary>An FX controller whose 1D blend tree drives the Smile shape from the Smile parameter.</summary>
        static RuntimeAnimatorController FxController(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            AnimationClip Clip(string name, float weight)
            {
                var clip = new AnimationClip { name = name };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Constant(0f, 1f, weight));
                AssetDatabase.CreateAsset(clip, folder + "/" + name + ".anim");
                return clip;
            }
            var off = Clip("Smile off", 0f);
            var on = Clip("Smile on", 100f);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Smile", AnimatorControllerParameterType.Float);
            controller.CreateBlendTreeInController("Smile", out var tree, 0);
            tree.blendParameter = "Smile";
            tree.AddChild(off, 0f);
            tree.AddChild(on, 1f);
            AssetDatabase.SaveAssets();
            return controller;
        }
    }
}
