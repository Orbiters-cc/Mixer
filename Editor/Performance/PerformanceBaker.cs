using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Performance
{
    internal sealed class BakeOptions
    {
        public float FrameRate = 60f;
        /// <summary>Session seconds to bake (End past the session's end bakes to its end).</summary>
        public double Start, End = double.MaxValue;
        /// <summary>Face and everything the avatar's animator animates (blend shapes, toggles, materials).</summary>
        public bool Face = true;
        public bool Visemes = true;
        /// <summary>The body: exactly as VRChat showed it when the session has the capture kit's skeleton, else on top of
        /// the animated pose from the SteamVR devices calibrated by a T-pose, else from VRChat's head and wrist poses.</summary>
        public bool Body = true;
        /// <summary>Joystick movement estimated from VRChat's velocity parameters.</summary>
        public bool Locomotion = true;
        public Viewer Viewer = Viewer.Remote;
        public bool Audio = true;
        public bool Timeline = true;
        public string OutputFolder = "Assets/Orbiters/Mixer/Performances";
    }

    internal sealed class BakeResult
    {
        public AnimationClip Clip;
        public string ClipPath, TimelinePath;
        public readonly List<string> AudioPaths = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int Frames, Curves, Keys;
        public double Seconds;
        public bool BodyFromTracking;
        /// <summary>The body came from the capture kit's skeleton band (VRChat's own pose).</summary>
        public bool BodyFromSkeleton;
        /// <summary>The body came from VRChat's head and wrist poses (no T-pose needed).</summary>
        public bool BodyFromVrChat;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Turns a recorded performance into a clip for the avatar: a hidden copy of it in a preview scene plays the
    /// recorded parameters through its own playable layers (<see cref="VrcLayerStack"/>), lip sync and IK go on top,
    /// and every frame the humanoid pose (muscles and root) and whatever the layers animate (blend shapes, toggles,
    /// material properties, extra bones) are sampled into one humanoid clip. Audio tracks are aligned to the clip.
    /// </summary>
    internal static class PerformanceBaker
    {
        /// <summary>Bakes; <paramref name="progress"/> gets (fraction, label) and returns false to cancel.</summary>
        public static BakeResult Bake(PerformanceSession session, VRCAvatarDescriptor avatar, BakeOptions options, Func<float, string, bool> progress = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new BakeResult();
            if (avatar == null) throw new ArgumentException("No avatar.");
            if (avatar.GetComponent<Animator>() == null) throw new ArgumentException(avatar.name + " has no Animator.");
            double end = Math.Min(options.End, session.Duration);
            double start = Math.Max(0, Math.Min(options.Start, end));
            double rate = Math.Max(1.0, options.FrameRate);
            float dt = (float)(1.0 / rate);
            // Frames at start, start + 1/rate, … up to end; a frame that lands on end (within rounding) counts.
            int frames = Math.Max(1, (int)Math.Floor((end - start) * rate + 1e-6) + 1);
            result.Frames = frames;
            result.Seconds = (frames - 1) / rate;

            if (session.MissingParameterTable)
                result.Warnings.Add("This session has no parameters.json yet (it is still recording, or Orbiters desktop stopped before ending it): the face, gestures, toggles and VRChat's head and hand poses cannot be read.");
            if (session.MissingDeviceTable)
                result.Warnings.Add("This session has no tracking.json yet (it is still recording, or Orbiters desktop stopped before ending it): the SteamVR tracking cannot be read.");
            string blueprint = BlueprintId(avatar);
            var timeline = ParameterTimeline.Build(session, blueprint);
            AvatarChanges(timeline, blueprint, avatar.name, result.Warnings);
            var tracking = new TrackingTimeline(session);
            var recorded = new HashSet<string>(timeline.Tracks.Values.Where(t => t.IsAvatarParameter).Select(t => t.Name));
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            VrcLayerStack stack = null;
            HumanPoseHandler poses = null;
            try
            {
                copy = Object.Instantiate(avatar.gameObject);
                copy.name = avatar.name + " (Mixer performance)";
                copy.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(copy, scene);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.SetActive(true);
                var animator = copy.GetComponent<Animator>();
                var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
                bool humanoid = animator.avatar != null && animator.avatar.isHuman;
                if (!humanoid) result.Warnings.Add(avatar.name + " is not humanoid: only what its animator animates is baked, no body.");

                // Body: VRChat's own pose from the capture kit, else devices to bones from a T-pose calibration (any
                // game), else VRChat's head and wrist poses, measured on the copy's T-pose (before any animation).
                BodyRig rig = humanoid ? new BodyRig(animator) : null;
                BodyCalibration calibration = null;
                BodySolver solver = null;
                IPoseSource devices = tracking;
                SkeletonReplay skeleton = null;
                if (options.Body && humanoid && session.HasSkeleton)
                    skeleton = new SkeletonReplay(SkeletonFrames(session.Skeleton, CaptureKit.Identity(avatar), avatar.name, result.Warnings), animator);
                result.BodyFromSkeleton = skeleton != null;
                if (skeleton == null && options.Body && humanoid && rig.Valid)
                {
                    Vector3 eye = copy.transform.TransformPoint(descriptor.ViewPosition);
                    var vrchat = new VrChatPoses(timeline);
                    bool fromVrChat = vrchat.Has(VrChatPoses.Head);
                    // A calibration from before the session (Orbiters desktop copies the latest one into every session) may be
                    // days old, from another play space or other trackers: VRChat's own poses go first then.
                    var c = session.HasTracking ? session.CalibrationAt(start, beforeSession: !fromVrChat) : null;
                    if (c != null)
                        calibration = BodyCalibration.Build(c, tracking.Devices, rig, eye, result.Warnings);
                    if (calibration == null && fromVrChat)
                    {
                        calibration = BodyCalibration.FromVrChat(vrchat, rig, eye, start, result.Warnings);
                        devices = vrchat;
                        result.BodyFromVrChat = calibration != null;
                    }
                    else if (c == null && session.HasTracking)
                    {
                        result.Warnings.Add("No T-pose calibration in this session: the body follows the animation only. Calibrate in Orbiters' SteamVR tab before recording (in VRChat there is no need: it sends its head and hand poses).");
                    }
                    if (calibration != null) solver = new BodySolver(rig);
                }
                result.BodyFromTracking = solver != null && !result.BodyFromVrChat;

                stack = VrcLayerStack.Build(descriptor, animator, options.Viewer, recorded);
                result.Warnings.AddRange(stack.Warnings);
                var visemes = new VisemeEmulator(descriptor, copy.transform);
                Transform jaw = descriptor.lipSyncJawBone;
                bool emulateVisemes = options.Visemes && visemes.Style != VRC_AvatarDescriptor.LipSyncStyle.Default && visemes.Style != VRC_AvatarDescriptor.LipSyncStyle.VisemeParameterOnly;

                var curves = new CurveSet();
                var samplers = options.Face ? Samplers(copy.transform, animator, stack, descriptor, emulateVisemes, curves) : new List<Sampler>();
                int[] muscleChannels = null, rootChannels = null;
                HumanPose pose = default;
                Quaternion lastRoot = Quaternion.identity;
                if (humanoid)
                {
                    poses = new HumanPoseHandler(animator.avatar, copy.transform);
                    muscleChannels = new int[HumanTrait.MuscleCount];
                    for (int m = 0; m < muscleChannels.Length; m++)
                        muscleChannels[m] = curves.ChannelFor(EditorCurveBinding.FloatCurve("", typeof(Animator), CurveSet.MuscleProperty(HumanTrait.MuscleName[m])), 0.002f);
                    rootChannels = new[] { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" }
                        .Select((p, i) => curves.ChannelFor(EditorCurveBinding.FloatCurve("", typeof(Animator), p), i < 3 ? 0.0005f : 0.0005f)).ToArray();
                }

                var tracks = timeline.Tracks.Values.Where(t => t.IsAvatarParameter && t.Times.Count > 0).ToList();
                var steps = new ParameterSteps(tracks, tracks.Select(t => stack.TypeOf(t.Name) != AnimatorControllerParameterType.Float).ToArray());
                var locomotion = new Locomotion();
                bool moveWithVelocity = options.Locomotion && solver != null && (timeline.Has("VelocityX") || timeline.Has("VelocityZ"));
                var targets = new Dictionary<BodyTarget, BodySolver.Pose>();
                // The last pose of each target, held through dropouts and pauses (the first one before it starts).
                var held = new Dictionary<BodyTarget, BodySolver.Pose>();
                if (solver != null)
                    foreach (var pair in calibration.Bindings)
                        if (devices.TryFirstPose(pair.Value.Device, out var p0, out var r0))
                            held[pair.Key] = Target(calibration, pair.Value, p0, r0);
                int headHeld = 0, skeletonHeld = 0;
                for (int f = 0; f < frames; f++)
                {
                    double t = start + f / rate;
                    float time = (float)(f / rate);
                    // Every change up to now (at the first frame: the initial values and whatever happened before start).
                    if (f == 0)
                    {
                        steps.Latest(t, stack.Set);
                        stack.Evaluate(0f);
                    }
                    else
                    {
                        steps.Through(t - 1.0 / rate, t, stack.Set, stack.Evaluate);
                    }

                    if (emulateVisemes)
                    {
                        visemes.Step(Mathf.RoundToInt(stack.Get("Viseme")), stack.Get("Voice"), f == 0 ? 1f : dt);
                        if (stack.Tracking.Mouth) visemes.Apply(copy.transform, jaw);
                    }

                    if (skeleton != null)
                    {
                        if (!skeleton.Apply(t)) skeletonHeld++;
                    }
                    else if (solver != null)
                    {
                        targets.Clear();
                        foreach (var pair in calibration.Bindings)
                        {
                            if (devices.TryPose(pair.Value.Device, t, out var p, out var r))
                                held[pair.Key] = Target(calibration, pair.Value, p, r);
                            else if (pair.Key == BodyTarget.Head)
                                headHeld++;
                            // Snapping back to the animation would put the body at the copy's origin.
                            targets[pair.Key] = held.TryGetValue(pair.Key, out var last) ? last : default;
                        }
                        if (moveWithVelocity && targets.TryGetValue(BodyTarget.Head, out var head) && head.Valid)
                        {
                            var forward = Vector3.ProjectOnPlane(head.Rotation * Vector3.forward, Vector3.up);
                            float yaw = forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
                            locomotion.Step(f == 0 ? 0f : dt, head.Position, yaw, stack.Get("VelocityX"), stack.Get("VelocityZ"), stack.Get("AngularY"));
                            foreach (var key in targets.Keys.ToList())
                            {
                                var target = targets[key];
                                if (!target.Valid) continue;
                                targets[key] = new BodySolver.Pose { Valid = true, Position = locomotion.Apply(target.Position), Rotation = locomotion.Apply(target.Rotation) };
                            }
                        }
                        solver.Solve(targets, stack.Tracking, f == 0 ? 0f : dt);
                    }

                    if (poses != null)
                    {
                        poses.GetHumanPose(ref pose);
                        for (int m = 0; m < muscleChannels.Length; m++) curves.Key(muscleChannels[m], time, pose.muscles[m]);
                        var q = pose.bodyRotation;
                        if (Quaternion.Dot(q, lastRoot) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                        lastRoot = q;
                        curves.Key(rootChannels[0], time, pose.bodyPosition.x);
                        curves.Key(rootChannels[1], time, pose.bodyPosition.y);
                        curves.Key(rootChannels[2], time, pose.bodyPosition.z);
                        curves.Key(rootChannels[3], time, q.x);
                        curves.Key(rootChannels[4], time, q.y);
                        curves.Key(rootChannels[5], time, q.z);
                        curves.Key(rootChannels[6], time, q.w);
                    }
                    foreach (var s in samplers) s.Sample(curves, time);

                    if (progress != null && f % 30 == 0 && !progress(0.9f * f / frames, "Frame " + f + " of " + frames))
                        throw new OperationCanceledException();
                }

                if (skeletonHeld > 0)
                    result.Warnings.Add("The capture kit's pose was missing for " + (skeletonHeld / rate).ToString("0.0") + " s of the bake (VRChat's camera did not see the kit, or the session was paused): the body holds its last pose there.");
                if (headHeld > 0)
                    result.Warnings.Add("The headset's pose was missing for " + (headHeld / rate).ToString("0.0") + " s of the bake (tracking was lost, or the session was paused): the body holds its last pose there.");

                progress?.Invoke(0.92f, "Writing the clip");
                var clip = new AnimationClip { name = session.Name + " · " + avatar.name, frameRate = options.FrameRate };
                result.Keys = curves.WriteTo(clip);
                result.Curves = curves.Count;
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = false;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                string folder = AssetPaths.Normalize(options.OutputFolder) + "/" + SafePaths.FileName(session.Name, "Session");
                if (!AssetPaths.EnsureFolder(folder)) throw new IOException("Could not create " + folder + ".");
                result.ClipPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(avatar.name, "Avatar") + ".anim");
                SafePaths.RequireLength(Path.GetFullPath(result.ClipPath));
                AssetDatabase.CreateAsset(clip, result.ClipPath);
                result.Clip = clip;

                if (options.Audio && session.Audio != null)
                {
                    progress?.Invoke(0.95f, "Aligning the audio");
                    foreach (var track in session.Audio.Tracks)
                    {
                        string label = track.Kind == "mic" ? "voice" : track.Kind == "game" ? "VRChat sound" : track.Kind;
                        string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(avatar.name, "Avatar") + " " + label + ".wav");
                        try
                        {
                            SafePaths.RequireLength(Path.GetFullPath(path));
                            AudioAlign.Write(track, session.Folder, start, result.Seconds + dt, Path.GetFullPath(path));
                            AssetDatabase.ImportAsset(path);
                            result.AudioPaths.Add(path);
                        }
                        catch (Exception e)
                        {
                            result.Warnings.Add("Audio " + track.File + ": " + e.Message);
                        }
                    }
                }
                if (options.Timeline)
                {
                    result.TimelinePath = PerformanceTimeline.Create(folder, avatar.name, clip, result.AudioPaths);
                }
                AssetDatabase.SaveAssets();
                return result;
            }
            finally
            {
                stack?.Dispose();
                poses?.Dispose();
                if (copy != null) Object.DestroyImmediate(copy);
                EditorSceneManager.ClosePreviewScene(scene);
                result.Elapsed = watch.Elapsed;
            }
        }

        /// <summary>Reads one animated property of the copy every frame.</summary>
        sealed class Sampler
        {
            public int[] Channels;
            public Func<int, float> Read;

            public void Sample(CurveSet curves, float time)
            {
                for (int i = 0; i < Channels.Length; i++) curves.Key(Channels[i], time, Read(i));
            }
        }

        /// <summary>
        /// What to sample: every property the layers' clips animate except humanoid muscles and humanoid bones (the
        /// pose covers those), plus the lip sync shapes. Blend shapes, activity and transforms are read directly; anything
        /// else through the animation system.
        /// </summary>
        static List<Sampler> Samplers(Transform root, Animator animator, VrcLayerStack stack, VRCAvatarDescriptor descriptor, bool visemes, CurveSet curves)
        {
            var humanBones = new HashSet<Transform>();
            if (animator.isHuman)
                for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
                {
                    var t = animator.GetBoneTransform((HumanBodyBones)b);
                    if (t != null) humanBones.Add(t);
                }
            var bindings = new Dictionary<string, EditorCurveBinding>();
            foreach (var controller in stack.Controllers)
                foreach (var clip in controller.animationClips.Distinct())
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                        bindings[b.path + "\n" + b.type.FullName + "\n" + b.propertyName] = b;
            if (visemes && descriptor.VisemeSkinnedMesh != null && descriptor.VisemeSkinnedMesh.sharedMesh != null)
            {
                string path = AnimationUtility.CalculateTransformPath(descriptor.VisemeSkinnedMesh.transform, root);
                var names = new List<string>();
                if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && descriptor.VisemeBlendShapes != null) names.AddRange(descriptor.VisemeBlendShapes.Where(n => !string.IsNullOrEmpty(n)));
                if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && !string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName)) names.Add(descriptor.MouthOpenBlendShapeName);
                foreach (var n in names)
                {
                    var b = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + n);
                    bindings[b.path + "\n" + b.type.FullName + "\n" + b.propertyName] = b;
                }
            }
            if (visemes && descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone && descriptor.lipSyncJawBone != null)
            {
                var b = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(descriptor.lipSyncJawBone, root), typeof(Transform), "m_LocalRotation.x");
                bindings[b.path + "\n" + b.type.FullName + "\n" + b.propertyName] = b;
            }

            var samplers = new List<Sampler>();
            var transforms = new HashSet<Transform>();
            foreach (var b in bindings.Values)
            {
                if (b.type == typeof(Animator)) continue;
                var target = string.IsNullOrEmpty(b.path) ? root : root.Find(b.path);
                if (target == null) continue;
                if (b.type == typeof(Transform))
                {
                    if (!humanBones.Contains(target)) transforms.Add(target);
                    continue;
                }
                if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var smr = target.GetComponent<SkinnedMeshRenderer>();
                    int index = smr != null && smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(b.propertyName.Substring("blendShape.".Length)) : -1;
                    if (index < 0) continue;
                    samplers.Add(new Sampler { Channels = new[] { curves.ChannelFor(b, 0.25f) }, Read = _ => smr.GetBlendShapeWeight(index) });
                    continue;
                }
                if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                {
                    var go = target.gameObject;
                    samplers.Add(new Sampler { Channels = new[] { curves.ChannelFor(b, 0f, true) }, Read = _ => go.activeSelf ? 1f : 0f });
                    continue;
                }
                bool step = b.propertyName == "m_Enabled";
                var binding = b;
                samplers.Add(new Sampler
                {
                    Channels = new[] { curves.ChannelFor(b, step ? 0f : 0.001f, step) },
                    Read = _ => AnimationUtility.GetFloatValue(root.gameObject, binding, out float v) ? v : float.NaN,
                });
            }
            foreach (var t in transforms)
            {
                string path = AnimationUtility.CalculateTransformPath(t, root);
                var props = new[] { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w", "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" };
                var channels = props.Select((p, i) => curves.ChannelFor(EditorCurveBinding.FloatCurve(path, typeof(Transform), p), i < 3 ? 0.0001f : i < 7 ? 0.0005f : 0.001f)).ToArray();
                var last = t.localRotation;
                samplers.Add(new Sampler
                {
                    Channels = channels,
                    Read = i =>
                    {
                        if (i < 3) return t.localPosition[i];
                        if (i < 7)
                        {
                            var q = t.localRotation;
                            if (i == 3)
                            {
                                if (Quaternion.Dot(q, last) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                                last = q;
                            }
                            return last[i - 3];
                        }
                        return t.localScale[i - 7];
                    },
                });
            }
            return samplers;
        }

        static BodySolver.Pose Target(BodyCalibration calibration, BodyCalibration.Binding binding, Vector3 position, Quaternion rotation)
        {
            Vector3 dp = calibration.ToAvatar(position);
            Quaternion dr = calibration.ToAvatar(rotation);
            return new BodySolver.Pose { Valid = true, Position = dp + dr * binding.PositionOffset, Rotation = dr * binding.RotationOffset };
        }

        /// <summary>The avatar's blueprint id (what VRChat's <c>/avatar/change</c> names), or null before its first upload.</summary>
        internal static string BlueprintId(Component avatar)
        {
            var pipeline = avatar.GetComponent<PipelineManager>();
            return pipeline != null && !string.IsNullOrWhiteSpace(pipeline.blueprintId) ? pipeline.blueprintId.Trim() : null;
        }

        /// <summary>Says when the session changed avatars, and whether only this avatar's part is baked.</summary>
        internal static void AvatarChanges(ParameterTimeline timeline, string blueprint, string avatarName, List<string> warnings)
        {
            var changes = timeline.Avatars.OrderBy(a => a.time).ToList();
            if (changes.Select(a => a.id).Distinct().Count() < 2) return;
            if (!string.IsNullOrEmpty(blueprint) && changes.Any(a => a.id == blueprint))
            {
                warnings.Add("Avatars changed during the session: only what was recorded while " + avatarName + " was worn drives it, its other parameters hold their values meanwhile.");
                return;
            }
            var other = changes.Skip(1).FirstOrDefault(a => a.id != changes[0].id);
            string why = string.IsNullOrEmpty(blueprint) ? " " + avatarName + " has no blueprint id yet (it was never uploaded), so Mixer cannot tell which part is its own." : "";
            warnings.Add("The avatar changed at " + Clock(other.time) + " (to " + other.id + "): the parameters after that belong to another avatar." + why);
        }

        /// <summary>
        /// The skeleton frames to replay: the capture kit's on this avatar (<paramref name="identity"/>) when the session
        /// has some, leaving out another avatar's; all of them, with a warning, when none are this avatar's.
        /// </summary>
        internal static List<SkeletonFrame> SkeletonFrames(List<SkeletonFrame> frames, ushort identity, string avatarName, List<string> warnings)
        {
            bool ours = frames.Any(f => f.Pose.Avatar == identity), others = frames.Any(f => f.Pose.Avatar != identity);
            if (!ours)
            {
                warnings.Add("The capture kit in this session was on another avatar than " + avatarName + ": its pose may not fit this armature.");
                return frames;
            }
            if (!others) return frames;
            warnings.Add("The capture kit was on another avatar for part of the session: those frames are left out, and the body holds its pose there.");
            return frames.Where(f => f.Pose.Avatar == identity).ToList();
        }

        static string Clock(double seconds) => (int)(seconds / 60) + ":" + ((int)seconds % 60).ToString("00");
    }
}
