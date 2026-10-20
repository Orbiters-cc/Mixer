using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Contracts;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>What a tracked device stands for on the body.</summary>
    internal enum BodyTarget
    {
        Head, LeftHand, RightHand, Hips, Chest, LeftFoot, RightFoot, LeftKnee, RightKnee, LeftElbow, RightElbow,
    }

    /// <summary>The humanoid bones of the avatar copy the solver poses.</summary>
    internal sealed class BodyRig
    {
        public readonly Animator Animator;
        public readonly Transform Root, Hips, Spine, Chest, UpperChest, Neck, Head;
        public readonly Transform LeftUpperArm, LeftLowerArm, LeftHand, RightUpperArm, RightLowerArm, RightHand;
        public readonly Transform LeftUpperLeg, LeftLowerLeg, LeftFoot, RightUpperLeg, RightLowerLeg, RightFoot;

        public BodyRig(Animator animator)
        {
            Animator = animator;
            Root = animator.transform;
            Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
            Hips = B(HumanBodyBones.Hips);
            Spine = B(HumanBodyBones.Spine);
            Chest = B(HumanBodyBones.Chest);
            UpperChest = B(HumanBodyBones.UpperChest);
            Neck = B(HumanBodyBones.Neck);
            Head = B(HumanBodyBones.Head);
            LeftUpperArm = B(HumanBodyBones.LeftUpperArm);
            LeftLowerArm = B(HumanBodyBones.LeftLowerArm);
            LeftHand = B(HumanBodyBones.LeftHand);
            RightUpperArm = B(HumanBodyBones.RightUpperArm);
            RightLowerArm = B(HumanBodyBones.RightLowerArm);
            RightHand = B(HumanBodyBones.RightHand);
            LeftUpperLeg = B(HumanBodyBones.LeftUpperLeg);
            LeftLowerLeg = B(HumanBodyBones.LeftLowerLeg);
            LeftFoot = B(HumanBodyBones.LeftFoot);
            RightUpperLeg = B(HumanBodyBones.RightUpperLeg);
            RightLowerLeg = B(HumanBodyBones.RightLowerLeg);
            RightFoot = B(HumanBodyBones.RightFoot);
        }

        public bool Valid => Animator.isHuman && Hips != null && Head != null && LeftHand != null && RightHand != null && LeftFoot != null && RightFoot != null;

        /// <summary>Spine bones from the hips up to the neck (missing optional bones left out).</summary>
        public Transform[] SpineChain => new[] { Spine, Chest, UpperChest, Neck }.Where(t => t != null).ToArray();

        public Transform Of(BodyTarget target) => target switch
        {
            BodyTarget.Head => Head,
            BodyTarget.LeftHand => LeftHand,
            BodyTarget.RightHand => RightHand,
            BodyTarget.Hips => Hips,
            BodyTarget.Chest => UpperChest != null ? UpperChest : Chest != null ? Chest : Spine,
            BodyTarget.LeftFoot => LeftFoot,
            BodyTarget.RightFoot => RightFoot,
            BodyTarget.LeftKnee => LeftLowerLeg,
            BodyTarget.RightKnee => RightLowerLeg,
            BodyTarget.LeftElbow => LeftLowerArm,
            BodyTarget.RightElbow => RightLowerArm,
            _ => null,
        };

        /// <summary>Puts the copy in the T-pose its humanoid avatar was configured with.</summary>
        public void TPose()
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in Root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;
            foreach (var bone in Animator.avatar.humanDescription.skeleton)
            {
                if (!byName.TryGetValue(bone.name, out var t) || t == Root) continue;
                t.localPosition = bone.position;
                t.localRotation = bone.rotation;
                t.localScale = bone.scale;
            }
        }
    }

    /// <summary>
    /// Which device moves which bone, and how, from a T-pose calibration, the way VRChat calibrates: the person's play
    /// space is scaled so their eye height becomes the avatar's (and turned to face the avatar's forward), the headset
    /// and controllers drive the head and hands, and each tracker binds to the nearest of hips, chest, feet, knees and
    /// elbows (SteamVR tracker roles decide when they are set), keeping the bone's offset from the tracker.
    /// </summary>
    internal sealed class BodyCalibration
    {
        public struct Binding
        {
            public int Device;
            public Vector3 PositionOffset;
            public Quaternion RotationOffset;
        }

        public float Scale = 1f;
        public Quaternion PlayRotation = Quaternion.identity;
        public Vector3 PlayPivot, AvatarPivot;
        public readonly Dictionary<BodyTarget, Binding> Bindings = new Dictionary<BodyTarget, Binding>();

        /// <summary>A play-space point in the avatar's space.</summary>
        public Vector3 ToAvatar(Vector3 play) => AvatarPivot + PlayRotation * ((play - PlayPivot) * Scale);

        public Quaternion ToAvatar(Quaternion play) => PlayRotation * play;

        static readonly Dictionary<string, BodyTarget> Roles = new Dictionary<string, BodyTarget>
        {
            { "waist", BodyTarget.Hips }, { "chest", BodyTarget.Chest }, { "left_foot", BodyTarget.LeftFoot }, { "right_foot", BodyTarget.RightFoot },
            { "left_knee", BodyTarget.LeftKnee }, { "right_knee", BodyTarget.RightKnee }, { "left_elbow", BodyTarget.LeftElbow }, { "right_elbow", BodyTarget.RightElbow },
        };

        static readonly BodyTarget[] TrackerTargets =
        {
            BodyTarget.Hips, BodyTarget.Chest, BodyTarget.LeftFoot, BodyTarget.RightFoot, BodyTarget.LeftKnee, BodyTarget.RightKnee, BodyTarget.LeftElbow, BodyTarget.RightElbow,
        };

        /// <summary>
        /// Binds devices to bones. <paramref name="rig"/> is posed in its T-pose (and left so); <paramref name="eye"/> is the
        /// avatar's view position in the copy's space.
        /// </summary>
        public static BodyCalibration Build(Calibrations.Calibration calibration, TrackingDevices devices, BodyRig rig, Vector3 eye, List<string> warnings)
        {
            var c = new BodyCalibration();
            rig.TPose();
            var poses = new Dictionary<int, (Vector3 p, Quaternion r, string role, string cls)>();
            foreach (var d in calibration.Devices)
            {
                if (d.Position == null || d.Position.Length < 3 || d.Rotation == null || d.Rotation.Length < 4) continue;
                // The device's number in this session; a device the session did not see (255) is matched by serial.
                int number = d.Index;
                if (number >= devices.Devices.Count || number == 255) number = devices.Devices.FindIndex(x => !string.IsNullOrEmpty(d.Serial) && x.Serial == d.Serial);
                if (number < 0) continue;
                poses[number] = (new Vector3(d.Position[0], d.Position[1], d.Position[2]), new Quaternion(d.Rotation[0], d.Rotation[1], d.Rotation[2], d.Rotation[3]).normalized, d.Role ?? "", d.Class ?? "");
            }
            int head = poses.Where(x => x.Value.role == "head" || x.Value.cls == "hmd").Select(x => x.Key).DefaultIfEmpty(-1).First();
            if (head < 0)
            {
                warnings.Add("The calibration has no headset pose.");
                return null;
            }
            var hp = poses[head];
            float personEye = calibration.HeadHeightM > 0.5f ? calibration.HeadHeightM : hp.p.y;
            c.Scale = eye.y / Mathf.Max(0.5f, personEye);
            var forward = Vector3.ProjectOnPlane(hp.r * Vector3.forward, Vector3.up);
            float yaw = forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
            c.PlayRotation = Quaternion.Euler(0f, -yaw, 0f);
            c.PlayPivot = new Vector3(hp.p.x, 0f, hp.p.z);
            c.AvatarPivot = new Vector3(eye.x, 0f, eye.z);

            void Bind(BodyTarget target, int device, Vector3 bonePosition, Quaternion boneRotation)
            {
                var p = poses[device];
                Vector3 dp = c.ToAvatar(p.p);
                Quaternion dr = c.ToAvatar(p.r);
                var inv = Quaternion.Inverse(dr);
                c.Bindings[target] = new Binding { Device = device, PositionOffset = inv * (bonePosition - dp), RotationOffset = inv * boneRotation };
            }

            Bind(BodyTarget.Head, head, rig.Head.position, rig.Head.rotation);
            foreach (var (role, target, hand, upper) in new[] { ("left_hand", BodyTarget.LeftHand, rig.LeftHand, rig.LeftUpperArm), ("right_hand", BodyTarget.RightHand, rig.RightHand, rig.RightUpperArm) })
            {
                int device = poses.Where(x => x.Value.role == role).Select(x => x.Key).DefaultIfEmpty(-1).First();
                if (device < 0)
                {
                    warnings.Add("The calibration has no " + role.Replace('_', ' ') + " controller.");
                    continue;
                }
                // The wrist sits a few centimetres behind the grip, toward the shoulder; arm length differences are
                // left to the IK (VRChat does not stretch arms either).
                Vector3 grip = c.ToAvatar(poses[device].p);
                Vector3 towardShoulder = upper != null ? (upper.position - grip).normalized : Vector3.zero;
                Bind(target, device, grip + towardShoulder * (0.07f * c.Scale), hand.rotation);
            }

            // Trackers: SteamVR roles first, then the nearest free bone.
            var trackers = poses.Where(x => x.Key != head && x.Value.role != "left_hand" && x.Value.role != "right_hand" && x.Value.cls != "controller").Select(x => x.Key).ToList();
            var free = new HashSet<BodyTarget>(TrackerTargets.Where(t => rig.Of(t) != null));
            foreach (int device in trackers.ToList())
            {
                if (Roles.TryGetValue(poses[device].role, out var target) && free.Remove(target))
                {
                    Bind(target, device, rig.Of(target).position, rig.Of(target).rotation);
                    trackers.Remove(device);
                }
            }
            var pairs = new List<(float distance, int device, BodyTarget target)>();
            foreach (int device in trackers)
                foreach (var target in free)
                    pairs.Add((Vector3.Distance(c.ToAvatar(poses[device].p), rig.Of(target).position), device, target));
            var used = new HashSet<int>();
            foreach (var pair in pairs.OrderBy(x => x.distance))
            {
                if (used.Contains(pair.device) || !free.Contains(pair.target) || pair.distance > 0.6f * Mathf.Max(1f, c.Scale)) continue;
                Bind(pair.target, pair.device, rig.Of(pair.target).position, rig.Of(pair.target).rotation);
                used.Add(pair.device);
                free.Remove(pair.target);
            }
            foreach (int device in trackers.Where(d => !used.Contains(d)))
                warnings.Add("A tracker (" + (devices.Devices.Count > device ? devices.Devices[device].Serial : "#" + device) + ") is not near any bone the IK uses: left out.");
            return c;
        }

        /// <summary>
        /// Binds VRChat's head and wrist poses (<see cref="VrChatPoses"/>) to the head and hands, without a T-pose: the
        /// person's standing eye height is the session's usual top of the headset (90th percentile), the play space turns
        /// so the headset faces the avatar's forward at <paramref name="start"/>, the headset looking ahead is the avatar's
        /// view, and the wrists are where the hands go (VRChat places them). <paramref name="rig"/> is posed in its T-pose
        /// (and left so); <paramref name="eye"/> is the avatar's view position in the copy's space.
        /// </summary>
        public static BodyCalibration FromVrChat(VrChatPoses vr, BodyRig rig, Vector3 eye, double start, List<string> warnings)
        {
            var heads = vr.Samples(VrChatPoses.Head);
            if (heads.Count == 0)
            {
                warnings.Add("VRChat sent no headset pose.");
                return null;
            }
            var c = new BodyCalibration();
            rig.TPose();
            var heights = heads.Select(s => s.Position.y).Where(y => y > 0.3f).OrderBy(y => y).ToList();
            float personEye = heights.Count > 0 ? heights[(int)(0.9f * (heights.Count - 1))] : 1.6f;
            if (personEye < 1.1f) warnings.Add("The headset stayed below 1.1 m (seated?): the avatar is sized as if that were standing height.");
            c.Scale = eye.y / Mathf.Max(0.5f, personEye);
            if (!vr.TryPose(VrChatPoses.Head, start, out var hp, out var hr))
            {
                var first = heads.FirstOrDefault(s => s.Time >= start);
                if (first.Time < start) first = heads[heads.Count - 1];
                hp = first.Position;
                hr = first.Rotation;
            }
            c.PlayRotation = Quaternion.Euler(0f, -BodySolver.YawOf(hr), 0f);
            c.PlayPivot = new Vector3(hp.x, 0f, hp.z);
            c.AvatarPivot = new Vector3(eye.x, 0f, eye.z);
            c.Bindings[BodyTarget.Head] = new Binding { Device = VrChatPoses.Head, PositionOffset = rig.Head.position - eye, RotationOffset = rig.Head.rotation };

            foreach (var (part, target, hand, lower, side) in new[]
            {
                (VrChatPoses.LeftWrist, BodyTarget.LeftHand, rig.LeftHand, rig.LeftLowerArm, -1f),
                (VrChatPoses.RightWrist, BodyTarget.RightHand, rig.RightHand, rig.RightLowerArm, 1f),
            })
            {
                string name = side < 0 ? "left" : "right";
                if (!vr.Has(part))
                {
                    warnings.Add("VRChat sent no " + name + " wrist pose: that hand follows the animation.");
                    continue;
                }
                var axes = WristAxes(vr, part, side, out bool sure);
                if (!sure) warnings.Add("Too little " + name + " hand movement to tell how VRChat's wrist pose sits on the hand: its rotation may be off.");
                // The hand bone's own axes in the T-pose (palms down): along the forearm, and forward (the thumb's side).
                var inv = Quaternion.Inverse(hand.rotation);
                var bone = Quaternion.LookRotation(inv * (hand.position - lower.position).normalized, inv * rig.Root.forward);
                c.Bindings[target] = new Binding { Device = part, PositionOffset = Vector3.zero, RotationOffset = axes * Quaternion.Inverse(bone) };
            }
            return c;
        }

        /// <summary>
        /// How VRChat's wrist pose sits on the hand (not documented): a rotation from the hand's axes (forward toward the
        /// fingers, up the thumb's side) to the pose's own, read from the session. On average a hand points away from its
        /// shoulder (estimated under the headset), and while the hands are out in front, controllers held upright, the
        /// thumb's side is up; each estimate snaps to the nearest of the pose's axes (VRChat's frame is square to the hand),
        /// so a fair estimate gives the exact frame. <paramref name="sure"/> is false when the session shows too little:
        /// the frame is then the headset's (fingers forward, thumb up).
        /// </summary>
        internal static Quaternion WristAxes(VrChatPoses vr, int part, float side, out bool sure)
        {
            var wrists = vr.Samples(part);
            var frames = new List<Quaternion>();
            Vector3 along = Vector3.zero;
            int step = Math.Max(1, wrists.Count / 4000);
            for (int i = 0; i < wrists.Count; i += step)
            {
                var w = wrists[i];
                if (!vr.TryPose(VrChatPoses.Head, w.Time, out var hp, out var hr)) continue;
                var shoulder = hp + Quaternion.Euler(0f, BodySolver.YawOf(hr), 0f) * new Vector3(0.18f * side, -0.25f, -0.08f);
                var reach = w.Position - shoulder;
                if (reach.magnitude < 0.2f) continue;
                along += Quaternion.Inverse(w.Rotation) * reach.normalized;
                frames.Add(w.Rotation);
            }
            sure = false;
            if (frames.Count < 30 || along.magnitude < 0.5f * frames.Count) return Quaternion.identity;
            Vector3 fingers = along.normalized, thumb = Vector3.zero;
            foreach (var r in frames) thumb += Quaternion.Inverse(r) * Vector3.ProjectOnPlane(Vector3.up, r * fingers);
            if (thumb.magnitude < 0.1f * frames.Count) return Quaternion.identity;
            thumb.Normalize();
            var axes = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            float best = float.MinValue;
            Vector3 bestFingers = Vector3.forward, bestThumb = Vector3.up;
            foreach (var a in axes)
                foreach (var b in axes)
                {
                    if (Mathf.Abs(Vector3.Dot(a, b)) > 0.5f) continue;
                    float score = Vector3.Dot(fingers, a) + Vector3.Dot(thumb, b);
                    if (score <= best) continue;
                    best = score;
                    bestFingers = a;
                    bestThumb = b;
                }
            sure = Vector3.Dot(fingers, bestFingers) > 0.6f && Vector3.Dot(thumb, bestThumb) > 0.5f;
            return sure ? Quaternion.LookRotation(bestFingers, bestThumb) : Quaternion.identity;
        }
    }

    /// <summary>
    /// Poses the avatar copy from the device targets, on top of the animated pose (fingers and anything untracked keep
    /// the animation): hips from a hip tracker, else under the head with the body turning after the head like VRChat's
    /// three-point IK; the spine bends so the head reaches the headset; two-bone IK for arms and legs (elbow and knee
    /// trackers as hints); without foot trackers the feet stay where the animation put them. Parts VRChat's Tracking
    /// Control handed to the animation are left alone.
    /// </summary>
    internal sealed class BodySolver
    {
        readonly BodyRig rig;
        readonly Vector3 hipsBelowHead;
        float bodyYaw;
        bool started;

        public BodySolver(BodyRig rig)
        {
            this.rig = rig;
            // Measured in the T-pose (the copy is in it while calibrating).
            hipsBelowHead = rig.Hips.position - rig.Head.position;
        }

        public struct Pose
        {
            public bool Valid;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public void Solve(Dictionary<BodyTarget, Pose> targets, TrackingState tracking, float dt)
        {
            bool Has(BodyTarget t) => targets.TryGetValue(t, out var p) && p.Valid;
            var animLeftFoot = (rig.LeftFoot.position, rig.LeftFoot.rotation);
            var animRightFoot = (rig.RightFoot.position, rig.RightFoot.rotation);
            var animLeftKnee = rig.LeftLowerLeg.position;
            var animRightKnee = rig.RightLowerLeg.position;
            var animLeftElbow = rig.LeftLowerArm.position;
            var animRightElbow = rig.RightLowerArm.position;

            // Hips.
            if (Has(BodyTarget.Hips) && tracking.Hip)
            {
                var h = targets[BodyTarget.Hips];
                rig.Hips.SetPositionAndRotation(h.Position, h.Rotation);
            }
            else if (Has(BodyTarget.Head) && tracking.Head)
            {
                var head = targets[BodyTarget.Head];
                var forward = Vector3.ProjectOnPlane(head.Rotation * Vector3.forward, Vector3.up);
                float headYaw = forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : bodyYaw;
                if (!started) bodyYaw = headYaw;
                // The body follows the head once it turns past 25°, and drifts after it meanwhile.
                float delta = Mathf.DeltaAngle(bodyYaw, headYaw);
                if (Mathf.Abs(delta) > 25f) bodyYaw += delta - Mathf.Sign(delta) * 25f;
                bodyYaw = Mathf.LerpAngle(bodyYaw, headYaw, 1f - Mathf.Exp(-dt / 1.5f));
                var yaw = Quaternion.Euler(0f, bodyYaw, 0f);
                var animYaw = Quaternion.Euler(0f, YawOf(rig.Hips.rotation), 0f);
                rig.Hips.rotation = yaw * Quaternion.Inverse(animYaw) * rig.Hips.rotation;
                rig.Hips.position = head.Position + yaw * hipsBelowHead;
            }
            started = true;

            // Chest from a tracker, then the spine bends so the head reaches the headset.
            if (Has(BodyTarget.Chest) && tracking.Hip)
            {
                var chestBone = rig.Of(BodyTarget.Chest);
                var target = targets[BodyTarget.Chest];
                var chain = rig.SpineChain.TakeWhile(t => t != chestBone).Concat(new[] { chestBone }).Where(t => t != null).ToArray();
                Bend(chain, chestBone, target.Position, 6f);
                chestBone.rotation = target.Rotation;
            }
            if (Has(BodyTarget.Head) && tracking.Head)
            {
                var head = targets[BodyTarget.Head];
                Bend(rig.SpineChain, rig.Head, head.Position, 5f);
                rig.Head.rotation = head.Rotation;
            }

            // Arms.
            Limb(rig.LeftUpperArm, rig.LeftLowerArm, rig.LeftHand, targets, BodyTarget.LeftHand, BodyTarget.LeftElbow, tracking.LeftHand, animLeftElbow, true);
            Limb(rig.RightUpperArm, rig.RightLowerArm, rig.RightHand, targets, BodyTarget.RightHand, BodyTarget.RightElbow, tracking.RightHand, animRightElbow, true);

            // Legs: to the foot trackers, or back to where the animation planted the feet.
            Leg(rig.LeftUpperLeg, rig.LeftLowerLeg, rig.LeftFoot, targets, BodyTarget.LeftFoot, BodyTarget.LeftKnee, tracking.LeftFoot, animLeftFoot, animLeftKnee);
            Leg(rig.RightUpperLeg, rig.RightLowerLeg, rig.RightFoot, targets, BodyTarget.RightFoot, BodyTarget.RightKnee, tracking.RightFoot, animRightFoot, animRightKnee);
        }

        internal static float YawOf(Quaternion q)
        {
            var f = Vector3.ProjectOnPlane(q * Vector3.forward, Vector3.up);
            return f.sqrMagnitude > 1e-6f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : 0f;
        }

        void Limb(Transform upper, Transform lower, Transform end, Dictionary<BodyTarget, Pose> targets, BodyTarget handTarget, BodyTarget elbowTarget, bool tracked, Vector3 animElbow, bool arm)
        {
            if (!tracked || upper == null || lower == null || end == null || !targets.TryGetValue(handTarget, out var hand) || !hand.Valid) return;
            // Elbows bend toward an elbow tracker, else where the animation had them, pushed down and back a little.
            Vector3 pole = targets.TryGetValue(elbowTarget, out var e) && e.Valid ? e.Position : animElbow + (Vector3.down * 0.3f - rig.Hips.forward * 0.15f) * Mathf.Max(0.1f, (lower.position - upper.position).magnitude);
            TwoBone(upper, lower, end, hand.Position, pole);
            end.rotation = hand.Rotation;
        }

        void Leg(Transform upper, Transform lower, Transform foot, Dictionary<BodyTarget, Pose> targets, BodyTarget footTarget, BodyTarget kneeTarget, bool tracked, (Vector3 position, Quaternion rotation) animFoot, Vector3 animKnee)
        {
            if (upper == null || lower == null || foot == null) return;
            Vector3 pole = targets.TryGetValue(kneeTarget, out var k) && k.Valid ? k.Position : animKnee + rig.Hips.forward * 0.4f;
            if (tracked && targets.TryGetValue(footTarget, out var f) && f.Valid)
            {
                TwoBone(upper, lower, foot, f.Position, pole);
                foot.rotation = f.Rotation;
                return;
            }
            TwoBone(upper, lower, foot, animFoot.position, pole);
            foot.rotation = animFoot.rotation;
        }

        /// <summary>Rotates <paramref name="chain"/> (bottom to top) so that <paramref name="effector"/> moves to
        /// <paramref name="target"/>, a few degrees per bone and pass so every bone takes a share of the bend.</summary>
        public static void Bend(Transform[] chain, Transform effector, Vector3 target, float degreesPerPass)
        {
            if (chain.Length == 0) return;
            for (int pass = 0; pass < 12; pass++)
            {
                for (int i = chain.Length - 1; i >= 0; i--)
                {
                    var bone = chain[i];
                    if (bone == effector) continue;
                    Vector3 toEffector = effector.position - bone.position, toTarget = target - bone.position;
                    if (toEffector.sqrMagnitude < 1e-8f || toTarget.sqrMagnitude < 1e-8f) continue;
                    var turn = Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(toEffector, toTarget), degreesPerPass);
                    bone.rotation = turn * bone.rotation;
                }
                if ((effector.position - target).sqrMagnitude < 1e-6f) return;
            }
        }

        /// <summary>
        /// Two-bone IK: <paramref name="upper"/> and <paramref name="lower"/> turn so <paramref name="end"/> reaches
        /// <paramref name="target"/> (or points at it when out of reach), bending in the plane of <paramref name="pole"/>.
        /// </summary>
        public static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float la = Vector3.Distance(a, b), lb = Vector3.Distance(b, c);
            Vector3 toTarget = target - a;
            float lt = toTarget.magnitude;
            if (lt < 1e-5f || la < 1e-5f || lb < 1e-5f) return;
            Vector3 dir = toTarget / lt;
            lt = Mathf.Clamp(lt, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
            float along = (la * la - lb * lb + lt * lt) / (2f * lt);
            float h = Mathf.Sqrt(Mathf.Max(0f, la * la - along * along));
            Vector3 bend = Vector3.ProjectOnPlane(pole - a, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(b - a, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(Vector3.up, dir);
            bend.Normalize();
            Vector3 wantedB = a + dir * along + bend * h;
            upper.rotation = Quaternion.FromToRotation(b - a, wantedB - a) * upper.rotation;
            Vector3 b2 = lower.position, c2 = end.position;
            lower.rotation = Quaternion.FromToRotation(c2 - b2, target - b2) * lower.rotation;
        }
    }

    /// <summary>
    /// Where VRChat's joystick took the person, estimated from the velocity parameters it outputs (<c>VelocityX</c>,
    /// <c>VelocityZ</c> in m/s along the avatar's right and forward, <c>AngularY</c> in degrees a second): the part of
    /// the motion the headset did not make in the play space is joystick movement, the part of the turning the head did
    /// not do is joystick turning. Slow residues are noise and dropped. An estimate: VRChat's own world pose is not
    /// recorded.
    /// </summary>
    internal sealed class Locomotion
    {
        public float Yaw;
        public Vector3 Offset;
        Vector3 lastHead;
        float lastHeadYaw;
        bool started;

        public const float MinSpeed = 0.2f, MinTurn = 10f;

        /// <summary>Advances by <paramref name="dt"/>; <paramref name="head"/> and <paramref name="headYaw"/> are the
        /// headset in the avatar's (play) space before this transform.</summary>
        public void Step(float dt, Vector3 head, float headYaw, float velocityX, float velocityZ, float angularY)
        {
            if (!started || dt <= 0f)
            {
                started = true;
                lastHead = head;
                lastHeadYaw = headYaw;
                return;
            }
            float headTurn = Mathf.DeltaAngle(lastHeadYaw, headYaw) / dt;
            float joystickTurn = angularY - headTurn;
            if (Mathf.Abs(joystickTurn) >= MinTurn)
            {
                // Turning happens around the head: keep its world position.
                var before = Quaternion.Euler(0f, Yaw, 0f) * head + Offset;
                Yaw += joystickTurn * dt;
                Offset = before - Quaternion.Euler(0f, Yaw, 0f) * head;
            }
            var world = Quaternion.Euler(0f, Yaw + headYaw, 0f);
            Vector3 avatarVelocity = world * new Vector3(velocityX, 0f, velocityZ);
            Vector3 headVelocity = Quaternion.Euler(0f, Yaw, 0f) * Vector3.ProjectOnPlane((head - lastHead) / dt, Vector3.up);
            Vector3 joystick = avatarVelocity - headVelocity;
            if (joystick.magnitude >= MinSpeed) Offset += joystick * dt;
            lastHead = head;
            lastHeadYaw = headYaw;
        }

        public Vector3 Apply(Vector3 p) => Quaternion.Euler(0f, Yaw, 0f) * p + Offset;

        public Quaternion Apply(Quaternion r) => Quaternion.Euler(0f, Yaw, 0f) * r;
    }
}
