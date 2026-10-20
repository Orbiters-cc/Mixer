using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
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
        /// <summary>The body: exactly as VRChat showed it when the session has the capture kit's skeleton, else from the
        /// SteamVR devices (needs a T-pose calibration) on top of the animated pose.</summary>
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

            var timeline = ParameterTimeline.Build(session);
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

                // Body: VRChat's own pose from the capture kit, else devices to bones from the calibration, measured
                // on the copy's T-pose (before any animation).
                BodyRig rig = humanoid ? new BodyRig(animator) : null;
                BodyCalibration calibration = null;
                BodySolver solver = null;
                SkeletonReplay skeleton = options.Body && humanoid && session.HasSkeleton ? new SkeletonReplay(session.Skeleton, animator) : null;
                result.BodyFromSkeleton = skeleton != null;
                ushort identity = CaptureKit.Identity(avatar);
                if (skeleton != null && session.Skeleton.All(f => f.Pose.Avatar != identity))
                    result.Warnings.Add("The capture kit in this session was on another avatar than " + avatar.name + ": its pose may not fit this armature.");
                if (skeleton == null && options.Body && humanoid && rig.Valid && session.HasTracking)
                {
                    var c = session.CalibrationAt(start);
                    if (c == null)
                    {
                        result.Warnings.Add("No T-pose calibration in this session: the body follows the animation only. Calibrate in Orbiters' SteamVR tab before recording.");
                    }
                    else
                    {
                        Vector3 eye = copy.transform.TransformPoint(descriptor.ViewPosition);
                        calibration = BodyCalibration.Build(c, tracking.Devices, rig, eye, result.Warnings);
                        if (calibration != null) solver = new BodySolver(rig);
                    }
                }
                result.BodyFromTracking = solver != null;

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
                var cursors = new int[tracks.Count];
                var locomotion = new Locomotion();
                bool moveWithVelocity = options.Locomotion && solver != null && (timeline.Has("VelocityX") || timeline.Has("VelocityZ"));
                var targets = new Dictionary<BodyTarget, BodySolver.Pose>();
                for (int f = 0; f < frames; f++)
                {
                    double t = start + f / rate;
                    float time = (float)(f / rate);
                    // Every change up to now (at the first frame: the initial values and whatever happened before start).
                    for (int k = 0; k < tracks.Count; k++)
                    {
                        var track = tracks[k];
                        int i = cursors[k];
                        if (i >= track.Times.Count || track.Times[i] > t) continue;
                        while (i + 1 < track.Times.Count && track.Times[i + 1] <= t) i++;
                        stack.Set(track.Name, track.Values[i]);
                        cursors[k] = i + 1;
                    }
                    stack.Evaluate(f == 0 ? 0f : dt);

                    if (emulateVisemes)
                    {
                        visemes.Step(Mathf.RoundToInt(stack.Get("Viseme")), stack.Get("Voice"), f == 0 ? 1f : dt);
                        if (stack.Tracking.Mouth) visemes.Apply(copy.transform, jaw);
                    }

                    if (skeleton != null)
                    {
                        skeleton.Apply(t);
                    }
                    else if (solver != null)
                    {
                        targets.Clear();
                        foreach (var pair in calibration.Bindings)
                        {
                            if (!tracking.TryPose(pair.Value.Device, t, out var p, out var r))
                            {
                                targets[pair.Key] = default;
                                continue;
                            }
                            Vector3 dp = calibration.ToAvatar(p);
                            Quaternion dr = calibration.ToAvatar(r);
                            targets[pair.Key] = new BodySolver.Pose { Valid = true, Position = dp + dr * pair.Value.PositionOffset, Rotation = dr * pair.Value.RotationOffset };
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

                progress?.Invoke(0.92f, "Writing the clip");
                var clip = new AnimationClip { name = session.Name + " · " + avatar.name, frameRate = options.FrameRate };
                result.Keys = curves.WriteTo(clip);
                result.Curves = curves.Count;
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = false;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                string folder = EnsureFolder(options.OutputFolder.TrimEnd('/') + "/" + Safe(session.Name));
                result.ClipPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(avatar.name) + ".anim");
                AssetDatabase.CreateAsset(clip, result.ClipPath);
                result.Clip = clip;

                if (options.Audio && session.Audio != null)
                {
                    progress?.Invoke(0.95f, "Aligning the audio");
                    foreach (var track in session.Audio.Tracks)
                    {
                        string label = track.Kind == "mic" ? "voice" : track.Kind == "game" ? "VRChat sound" : track.Kind;
                        string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(avatar.name) + " " + label + ".wav");
                        try
                        {
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

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }

        static string EnsureFolder(string folder)
        {
            string[] parts = folder.Split('/');
            string path = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }
            return path;
        }
    }
}
