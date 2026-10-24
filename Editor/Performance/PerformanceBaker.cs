using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

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
        /// <summary>The animators played were the avatar's as an upload builds them (VRCFury, Modular Avatar...).</summary>
        public bool Built;
        /// <summary>Animated properties left out: on objects or components the build adds, which the scene avatar doesn't have.</summary>
        public int LeftOut;
        public TimeSpan Elapsed;
    }

    /// <summary>A bake under way (<see cref="PerformanceBaker.Start"/>): the avatar's build, then the bake, on later editor frames.</summary>
    internal sealed class BakeJob
    {
        public BakeResult Result;
        /// <summary>Why it failed; an <see cref="OperationCanceledException"/> when it was cancelled.</summary>
        public Exception Error;
        public bool Done;
        /// <summary>Runs once, when the bake is done, failed or was cancelled.</summary>
        public event Action Completed;

        internal void Finish(BakeResult result, Exception error)
        {
            if (Done) return;
            Result = result;
            Error = error;
            Done = true;
            try { Completed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    /// <summary>
    /// Turns a recorded performance into a clip for the avatar: a hidden copy of it in a preview scene plays the
    /// recorded parameters through its own playable layers (<see cref="VrcLayerStack"/>), lip sync and IK go on top,
    /// and every frame the humanoid pose (muscles and root) and whatever the layers animate (blend shapes, toggles,
    /// material properties, extra bones) are sampled into one humanoid clip. Audio tracks are aligned to the clip.
    /// The copy is built the way an upload builds it first (<see cref="Start"/>), so what VRCFury and Modular Avatar add
    /// only then plays too, under the parameter names VRChat recorded; the clip still names the scene avatar's objects.
    /// </summary>
    internal static class PerformanceBaker
    {
        // Build steps the bake leaves out (as My Avatar's checks do): My Avatar's optimization merges meshes and renames or
        // folds objects (the clip would name objects the scene avatar doesn't have), and VRCFury's compressor only carries
        // the parameters over the network (the recording has every parameter's own value).
        static readonly string[] LeftOutSteps =
        {
            "Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Structure",
            "Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Materials",
            "VF.Hooks.ParameterCompressorHook",
        };

        /// <summary>
        /// Bakes the avatar as VRChat runs it: a hidden copy goes through the SDK's build steps as an upload's would
        /// (<see cref="HiddenAvatarBuild"/>: VRCFury, Modular Avatar, MCB, My Avatar; their face tracking, toggles and merged
        /// animators), then is baked on a later editor frame and closed. When the build fails, the avatar is baked as the
        /// scene has it, with a warning saying what failed. <paramref name="progress"/> as for <see cref="Bake"/>;
        /// <paramref name="skip"/> leaves more build steps out.
        /// </summary>
        public static BakeJob Start(PerformanceSession session, VRCAvatarDescriptor avatar, BakeOptions options, Func<float, string, bool> progress = null, Func<Type, bool> skip = null)
        {
            Check(avatar);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var job = new BakeJob();
            var source = avatar.gameObject;
            HiddenAvatarBuild build = null;
            ScenePaths paths = null;
            bool scheduled = false;

            void Finish()
            {
                BakeResult result = null;
                Exception error = null;
                try
                {
                    if (avatar == null) throw new InvalidOperationException("The avatar is gone.");
                    if (build != null && build.Cancelled) throw new OperationCanceledException();
                    if (build != null && build.Error == null)
                    {
                        // Play Mode closed every copy after the build.
                        if (build.Copy == null) throw new InvalidOperationException("The avatar's built copy was closed before the bake (Play Mode started).");
                        result = BakeOn(session, avatar, build.Copy, paths, options, progress, null);
                        result.Built = true;
                    }
                    else
                    {
                        string note = build == null ? null
                            : "The avatar could not be built as an upload builds it (" + build.Error + "): what VRCFury and Modular Avatar add at upload (face tracking, toggles, merged animators) is not in the bake.";
                        result = Bake(session, avatar, options, progress, note);
                        // A VRCFury test copy is built already.
                        result.Built = build == null;
                    }
                    result.Elapsed = watch.Elapsed;
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    build?.Release();
                }
                job.Finish(result, error);
            }

            // Never in the build's own callback: it also ends inside script reloads and Play Mode changes, and may end
            // before Start returns. A reload drops this with the build.
            void Later()
            {
                if (scheduled) return;
                scheduled = true;
                EditorApplication.CallbackFunction once = null;
                once = () =>
                {
                    EditorApplication.update -= once;
                    Finish();
                };
                EditorApplication.update += once;
            }

            // VRCFury refuses to build its test copies again: they are built already.
            if (VrcFury.IsTestCopy(source))
            {
                Later();
                return job;
            }
            build = HiddenAvatarBuild.Start(source, type => LeftOutSteps.Contains(type.FullName) || skip != null && skip(type), copy => paths = new ScenePaths(copy, source));
            build.Completed += Later;
            if (build.Finished) Later();
            return job;
        }

        /// <summary>
        /// Bakes the avatar as the scene has it, without the build steps (what VRCFury or Modular Avatar add at upload is
        /// left out; <see cref="Start"/> builds it first). <paramref name="progress"/> gets (fraction, label) and returns
        /// false to cancel.
        /// </summary>
        public static BakeResult Bake(PerformanceSession session, VRCAvatarDescriptor avatar, BakeOptions options, Func<float, string, bool> progress = null) =>
            Bake(session, avatar, options, progress, null);

        static BakeResult Bake(PerformanceSession session, VRCAvatarDescriptor avatar, BakeOptions options, Func<float, string, bool> progress, string note)
        {
            Check(avatar);
            using (var hidden = new HiddenAvatarCopy(avatar.gameObject))
                return BakeOn(session, avatar, hidden.Copy, new ScenePaths(hidden.Copy, avatar.gameObject), options, progress, note);
        }

        static void Check(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) throw new ArgumentException("No avatar.");
            if (avatar.GetComponent<Animator>() == null) throw new ArgumentException(avatar.name + " has no Animator.");
        }

        /// <summary>
        /// Bakes on <paramref name="copy"/>, a hidden copy of <paramref name="avatar"/> (built or not) that the caller closes;
        /// <paramref name="paths"/> places its objects on the scene avatar. <paramref name="note"/> is the first warning.
        /// </summary>
        static BakeResult BakeOn(PerformanceSession session, VRCAvatarDescriptor avatar, GameObject copy, ScenePaths paths, BakeOptions options, Func<float, string, bool> progress, string note)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new BakeResult();
            if (note != null) result.Warnings.Add(note);
            double end = Math.Min(options.End, session.Duration);
            double start = Math.Max(0, Math.Min(options.Start, end));
            double rate = Math.Max(1.0, options.FrameRate);
            float dt = (float)(1.0 / rate);
            // Frames at start, start + 1/rate, … up to end; a frame that lands on end (within rounding) counts.
            int frames = Math.Max(1, (int)Math.Floor((end - start) * rate + 1e-6) + 1);
            result.Frames = frames;
            result.Seconds = (frames - 1) / rate;

            if (session.MissingParameterTable)
                result.Warnings.Add("This session has no parameters.json yet (it is still recording, or Orbiters - N4N0 stopped before ending it): the face, gestures, toggles and VRChat's head and hand poses cannot be read.");
            if (session.MissingDeviceTable)
                result.Warnings.Add("This session has no tracking.json yet (it is still recording, or Orbiters - N4N0 stopped before ending it): the SteamVR tracking cannot be read.");
            string blueprint = BlueprintId(avatar);
            var timeline = ParameterTimeline.Build(session, blueprint);
            AvatarChanges(timeline, blueprint, avatar.name, result.Warnings);
            var tracking = new TrackingTimeline(session);
            var recorded = new HashSet<string>(timeline.Tracks.Values.Where(t => t.IsAvatarParameter).Select(t => t.Name));
            VrcLayerStack stack = null;
            HumanPoseHandler poses = null;
            try
            {
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var animator = copy.GetComponent<Animator>();
                if (animator == null) throw new InvalidOperationException("The build removed " + avatar.name + "'s Animator.");
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
                    // A calibration from before the session (Orbiters - N4N0 copies the latest one into every session) may be
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
                var left = new List<string>();
                var samplers = options.Face ? Samplers(copy.transform, animator, stack, descriptor, emulateVisemes, curves, paths, left) : new List<Sampler>();
                result.LeftOut = left.Count;
                if (left.Count > 0)
                {
                    var names = left.Distinct().ToList();
                    result.Warnings.Add(left.Count + (left.Count == 1 ? " animated property is" : " animated properties are") + " on what the build adds (VRCFury, Modular Avatar), which " +
                                        avatar.name + " in the scene doesn't have: left out of the clip (" + string.Join(", ", names.Take(3)) + (names.Count > 3 ? "…" : "") + ").");
                }
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
        /// else through the animation system. Read on the copy, keyed on the scene avatar (<paramref name="paths"/>); what
        /// the scene avatar doesn't have goes in <paramref name="left"/> (the copy's paths).
        /// </summary>
        static List<Sampler> Samplers(Transform root, Animator animator, VrcLayerStack stack, VRCAvatarDescriptor descriptor, bool visemes, CurveSet curves, ScenePaths paths, List<string> left)
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
            var transforms = new Dictionary<Transform, string>();
            foreach (var b in bindings.Values)
            {
                if (b.type == typeof(Animator)) continue;
                var target = string.IsNullOrEmpty(b.path) ? root : root.Find(b.path);
                if (target == null) continue;
                // The clip animates the scene avatar: the object where it was before the build moved or renamed it, and only
                // what the scene avatar has (an object, component or blend shape a build step made has no target there).
                var scene = b;
                scene.path = paths.Of(target);
                if (scene.path == null || !paths.Takes(scene))
                {
                    left.Add(b.path);
                    continue;
                }
                if (b.type == typeof(Transform))
                {
                    if (!humanBones.Contains(target)) transforms[target] = scene.path;
                    continue;
                }
                if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var smr = target.GetComponent<SkinnedMeshRenderer>();
                    int index = smr != null && smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(b.propertyName.Substring("blendShape.".Length)) : -1;
                    if (index < 0) continue;
                    samplers.Add(new Sampler { Channels = new[] { curves.ChannelFor(scene, 0.25f) }, Read = _ => smr.GetBlendShapeWeight(index) });
                    continue;
                }
                if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                {
                    var go = target.gameObject;
                    samplers.Add(new Sampler { Channels = new[] { curves.ChannelFor(scene, 0f, true) }, Read = _ => go.activeSelf ? 1f : 0f });
                    continue;
                }
                bool step = b.propertyName == "m_Enabled";
                var binding = b;
                samplers.Add(new Sampler
                {
                    Channels = new[] { curves.ChannelFor(scene, step ? 0f : 0.001f, step) },
                    Read = _ => AnimationUtility.GetFloatValue(root.gameObject, binding, out float v) ? v : float.NaN,
                });
            }
            foreach (var pair in transforms)
            {
                var t = pair.Key;
                string path = pair.Value;
                var props = new[] { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w", "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" };
                var channels = props.Select((p, i) => curves.ChannelFor(EditorCurveBinding.FloatCurve(path, typeof(Transform), p), i < 3 ? 0.0001f : i < 7 ? 0.0005f : 0.001f)).ToArray();
                // Relative to its parent on the scene avatar: a build step may have moved it under another object (an
                // armature merge keeps it where it was in the world).
                int slash = path.LastIndexOf('/');
                var parent = paths.Find(slash < 0 ? "" : path.Substring(0, slash));
                bool moved = parent != null && parent != t.parent;
                Vector3 position = default, scale = default;
                var last = moved ? Quaternion.Inverse(parent.rotation) * t.rotation : t.localRotation;
                samplers.Add(new Sampler
                {
                    Channels = channels,
                    Read = i =>
                    {
                        if (i == 0)
                        {
                            Quaternion q;
                            if (moved)
                            {
                                position = parent.InverseTransformPoint(t.position);
                                q = Quaternion.Inverse(parent.rotation) * t.rotation;
                                Vector3 s = t.lossyScale, p = parent.lossyScale;
                                scale = new Vector3(Ratio(s.x, p.x), Ratio(s.y, p.y), Ratio(s.z, p.z));
                            }
                            else
                            {
                                position = t.localPosition;
                                q = t.localRotation;
                                scale = t.localScale;
                            }
                            if (Quaternion.Dot(q, last) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                            last = q;
                        }
                        return i < 3 ? position[i] : i < 7 ? last[i - 3] : scale[i - 7];
                    },
                });
            }
            return samplers;
        }

        static float Ratio(float a, float b) => Mathf.Abs(b) > 1e-8f ? a / b : a;

        /// <summary>
        /// Where each object of a copy sits on the scene avatar it was copied from: read before any build step ran (VRCFury's
        /// and Modular Avatar's armature merges move and rename objects), so the clip names the scene avatar's objects.
        /// </summary>
        internal sealed class ScenePaths
        {
            readonly Dictionary<Transform, string> paths = new Dictionary<Transform, string>();
            readonly Dictionary<string, Transform> objects = new Dictionary<string, Transform>();
            readonly GameObject scene;

            public ScenePaths(GameObject copy, GameObject sceneAvatar)
            {
                scene = sceneAvatar;
                foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                {
                    string path = AnimationUtility.CalculateTransformPath(t, copy.transform);
                    paths[t] = path;
                    // Like Transform.Find, the first of two objects with one name.
                    if (!objects.ContainsKey(path)) objects[path] = t;
                }
            }

            /// <summary>The object's path on the scene avatar; null for one a build step made.</summary>
            public string Of(Transform copied) => copied != null && paths.TryGetValue(copied, out var path) ? path : null;

            /// <summary>The copy's object that was at <paramref name="path"/> before the build (null when a step removed it).</summary>
            public Transform Find(string path) => objects.TryGetValue(path, out var t) && t != null ? t : null;

            /// <summary>Whether the scene avatar has what the binding animates (the object, its component, the blend shape).</summary>
            public bool Takes(EditorCurveBinding binding)
            {
                var target = scene == null ? null : string.IsNullOrEmpty(binding.path) ? scene.transform : scene.transform.Find(binding.path);
                if (target == null) return false;
                if (binding.type == typeof(Transform) || binding.type == typeof(GameObject)) return true;
                if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var smr = target.GetComponent<SkinnedMeshRenderer>();
                    return smr != null && smr.sharedMesh != null && smr.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0;
                }
                return AnimationUtility.GetFloatValue(scene, binding, out _);
            }
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
