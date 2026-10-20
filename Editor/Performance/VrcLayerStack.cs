using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>Whose view the replay reproduces: VRChat runs some logic only for the wearer (IsLocal).</summary>
    internal enum Viewer
    {
        /// <summary>As other players saw the avatar (IsLocal false, local-only parameter drivers do not run).</summary>
        Remote,
        /// <summary>As the wearer saw it in a mirror.</summary>
        Local,
    }

    /// <summary>Which body parts follow tracking rather than the animation (VRChat's Tracking Control behaviours).</summary>
    internal sealed class TrackingState
    {
        public bool Head = true, LeftHand = true, RightHand = true, Hip = true, LeftFoot = true, RightFoot = true;
        public bool LeftFingers = true, RightFingers = true, Eyes = true, Mouth = true;
    }

    /// <summary>
    /// The avatar's playable layers stacked the way VRChat stacks them (Base, Additive, Sitting, T-pose, IK pose,
    /// Gesture, Action, FX; default controllers and masks where the avatar uses defaults), on an animator the caller
    /// owns (a hidden copy of the avatar), driven by parameter values. VRChat's state behaviours run through the SDK's
    /// own hooks: layer and playable layer control blend weights, tracking control decides what IK may override, and
    /// parameter drivers fill in animator-only parameters (recorded values always win: VRChat reported them after its
    /// drivers ran). One stack is live at a time.
    /// </summary>
    internal sealed class VrcLayerStack : IDisposable
    {
        sealed class Blend
        {
            public float From, To, Duration, Elapsed;
        }

        sealed class Layer
        {
            public AnimLayerType Type;
            public AnimatorControllerPlayable Playable;
            public int Input;
            public readonly Dictionary<string, AnimatorControllerParameterType> Parameters = new Dictionary<string, AnimatorControllerParameterType>();
            public Blend Weight;
            public readonly Dictionary<int, Blend> SubLayers = new Dictionary<int, Blend>();
        }

        const string SdkSamples = "Packages/com.vrchat.avatars/Samples/AV3 Demo Assets/Animation/";

        static readonly Dictionary<AnimLayerType, int> Order = new Dictionary<AnimLayerType, int>
        {
            { AnimLayerType.Base, 0 }, { AnimLayerType.Additive, 1 }, { AnimLayerType.Sitting, 2 }, { AnimLayerType.TPose, 3 },
            { AnimLayerType.IKPose, 4 }, { AnimLayerType.Gesture, 5 }, { AnimLayerType.Action, 6 }, { AnimLayerType.FX, 7 },
        };

        static VrcLayerStack live;

        PlayableGraph graph;
        AnimationLayerMixerPlayable mixer;
        readonly Animator animator;
        readonly Viewer viewer;
        readonly ISet<string> recorded;
        readonly List<Layer> layers = new List<Layer>();
        readonly Dictionary<string, float> values = new Dictionary<string, float>();
        readonly System.Random random = new System.Random(20261020);
        readonly List<AvatarMask> ownMasks = new List<AvatarMask>();
        readonly List<RuntimeAnimatorController> controllers = new List<RuntimeAnimatorController>();
        public readonly TrackingState Tracking = new TrackingState();
        public readonly List<string> Warnings = new List<string>();

        VrcLayerStack(Animator animator, Viewer viewer, ISet<string> recorded)
        {
            this.animator = animator;
            this.viewer = viewer;
            this.recorded = recorded ?? new HashSet<string>();
        }

        /// <summary>Every parameter any layer knows.</summary>
        public IEnumerable<string> ParameterNames => layers.SelectMany(l => l.Parameters.Keys).Distinct();

        /// <summary>The controllers the layers play (the avatar's, or VRChat's defaults).</summary>
        public IReadOnlyList<RuntimeAnimatorController> Controllers => controllers;

        public static VrcLayerStack Build(VRCAvatarDescriptor descriptor, Animator animator, Viewer viewer, ISet<string> recorded)
        {
            if (live != null) throw new InvalidOperationException("Another performance replay is running.");
            var stack = new VrcLayerStack(animator, viewer, recorded);
            live = stack;
            Hooks.Install();
            try
            {
                stack.Create(descriptor);
            }
            catch
            {
                stack.Dispose();
                throw;
            }
            return stack;
        }

        void Create(VRCAvatarDescriptor d)
        {
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var all = new List<VRCAvatarDescriptor.CustomAnimLayer>();
            if (d.baseAnimationLayers != null) all.AddRange(d.baseAnimationLayers);
            if (d.specialAnimationLayers != null) all.AddRange(d.specialAnimationLayers);
            all = all.Where(l => Order.ContainsKey(l.type)).OrderBy(l => Order[l.type]).ToList();

            graph = PlayableGraph.Create("Mixer performance");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            // Input 0 stays empty: the layers blend over the avatar's own pose, like VRChat's stack.
            mixer = AnimationLayerMixerPlayable.Create(graph, all.Count + 1);
            AnimationPlayableOutput.Create(graph, "Mixer performance", animator).SetSourcePlayable(mixer);

            for (int i = 0; i < all.Count; i++)
            {
                var spec = all[i];
                bool isDefault = !d.customizeAnimationLayers || spec.isDefault;
                var controller = isDefault ? DefaultController(spec.type) : spec.animatorController;
                var mask = isDefault || (spec.mask == null && spec.type == AnimLayerType.FX) ? DefaultMask(spec.type) : spec.mask;
                int input = i + 1;
                var layer = new Layer { Type = spec.type, Input = input };
                if (controller != null)
                {
                    controllers.Add(controller);
                    layer.Playable = AnimatorControllerPlayable.Create(graph, controller);
                    graph.Connect(layer.Playable, 0, mixer, input);
                    for (int p = 0; p < layer.Playable.GetParameterCount(); p++)
                    {
                        var parameter = layer.Playable.GetParameter(p);
                        layer.Parameters[parameter.name] = parameter.type;
                    }
                }
                else if (!isDefault)
                {
                    Warnings.Add(spec.type + " layer has no controller.");
                }
                // Poses, the Action and Sitting layers start silent; VRChat's behaviours (or Seated) turn them up.
                bool silent = spec.type == AnimLayerType.Action || spec.type == AnimLayerType.Sitting || spec.type == AnimLayerType.TPose || spec.type == AnimLayerType.IKPose;
                mixer.SetInputWeight(input, controller == null || silent ? 0f : 1f);
                if (spec.type == AnimLayerType.Additive) mixer.SetLayerAdditive((uint)input, true);
                if (mask != null) mixer.SetLayerMaskFromAvatarMask((uint)input, mask);
                layers.Add(layer);
            }
            graph.Play();
            ApplyDefaults(d);
            graph.Evaluate(0f);
        }

        static RuntimeAnimatorController DefaultController(AnimLayerType type)
        {
            string file = type switch
            {
                AnimLayerType.Base => "vrc_AvatarV3LocomotionLayer",
                AnimLayerType.Additive => "vrc_AvatarV3IdleLayer",
                AnimLayerType.Gesture => "vrc_AvatarV3HandsLayer",
                AnimLayerType.Action => "vrc_AvatarV3ActionLayer",
                AnimLayerType.Sitting => "vrc_AvatarV3SittingLayer",
                AnimLayerType.TPose => "vrc_AvatarV3UtilityTPose",
                AnimLayerType.IKPose => "vrc_AvatarV3UtilityIKPose",
                _ => null,
            };
            return file == null ? null : AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(SdkSamples + "Controllers/" + file + ".controller");
        }

        AvatarMask DefaultMask(AnimLayerType type)
        {
            switch (type)
            {
                case AnimLayerType.Gesture:
                    return AssetDatabase.LoadAssetAtPath<AvatarMask>(SdkSamples + "Masks/vrc_HandsOnly.mask");
                case AnimLayerType.FX:
                    // FX animates no muscles: every humanoid part off, every transform through.
                    var mask = new AvatarMask { name = "Mixer FX (no muscles)", hideFlags = HideFlags.HideAndDontSave };
                    for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
                    ownMasks.Add(mask);
                    return mask;
                default:
                    return null;
            }
        }

        /// <summary>Values VRChat sets itself, then the expression parameters' defaults (recorded values come later).</summary>
        void ApplyDefaults(VRCAvatarDescriptor d)
        {
            var parameters = d.expressionParameters != null ? d.expressionParameters.parameters : null;
            if (parameters != null)
                foreach (var p in parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name))
                        Set(p.name, p.defaultValue);
            float eye = d.ViewPosition.y * d.transform.lossyScale.y;
            Set("IsLocal", viewer == Viewer.Local ? 1f : 0f);
            Set("VRMode", 1f);
            Set("TrackingType", 3f);
            Set("AvatarVersion", 3f);
            Set("IsAnimatorEnabled", 1f);
            Set("Grounded", 1f);
            Set("Upright", 1f);
            Set("ScaleFactor", 1f);
            Set("ScaleFactorInverse", 1f);
            Set("EyeHeightAsMeters", eye);
            Set("EyeHeightAsPercent", Mathf.Clamp01((eye - 0.2f) / 4.8f));
        }

        public float Get(string name) => values.TryGetValue(name, out float v) ? v : 0f;

        /// <summary>Sets a parameter on every layer that has it, converted to that layer's type.</summary>
        public void Set(string name, float value)
        {
            values[name] = value;
            foreach (var layer in layers)
            {
                if (!layer.Parameters.TryGetValue(name, out var type)) continue;
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        layer.Playable.SetFloat(name, value);
                        break;
                    case AnimatorControllerParameterType.Int:
                        layer.Playable.SetInteger(name, (int)value);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        layer.Playable.SetBool(name, Mathf.Abs(value) > 1e-6f);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        if (Mathf.Abs(value) > 1e-6f) layer.Playable.SetTrigger(name);
                        break;
                }
            }
            if (name == "Seated")
                foreach (var layer in layers)
                    if (layer.Type == AnimLayerType.Sitting && layer.Playable.IsValid())
                        mixer.SetInputWeight(layer.Input, Mathf.Abs(value) > 1e-6f ? 1f : 0f);
        }

        /// <summary>Advances blends and the graph by <paramref name="dt"/> seconds (the animator writes the copy's pose).</summary>
        public void Evaluate(float dt)
        {
            foreach (var layer in layers)
            {
                if (layer.Weight != null)
                {
                    layer.Weight.Elapsed += dt;
                    float k = layer.Weight.Duration <= 0f ? 1f : Mathf.Clamp01(layer.Weight.Elapsed / layer.Weight.Duration);
                    mixer.SetInputWeight(layer.Input, Mathf.Lerp(layer.Weight.From, layer.Weight.To, k));
                    if (k >= 1f) layer.Weight = null;
                }
                List<int> done = null;
                foreach (var pair in layer.SubLayers)
                {
                    var blend = pair.Value;
                    blend.Elapsed += dt;
                    float k = blend.Duration <= 0f ? 1f : Mathf.Clamp01(blend.Elapsed / blend.Duration);
                    if (pair.Key < layer.Playable.GetLayerCount()) layer.Playable.SetLayerWeight(pair.Key, Mathf.Lerp(blend.From, blend.To, k));
                    if (k >= 1f) (done ??= new List<int>()).Add(pair.Key);
                }
                if (done != null)
                    foreach (int i in done)
                        layer.SubLayers.Remove(i);
            }
            graph.Evaluate(dt);
        }

        Layer Find(string blendableLayerName)
        {
            // The SDK's blendable layer enums share the layer type names (Action, FX, Gesture, Additive).
            if (!Enum.TryParse(blendableLayerName, out AnimLayerType type)) return null;
            return layers.FirstOrDefault(l => l.Type == type && l.Playable.IsValid());
        }

        void OnPlayableLayerControl(VRC_PlayableLayerControl control)
        {
            var layer = Find(control.layer.ToString());
            if (layer == null) return;
            layer.Weight = new Blend { From = mixer.GetInputWeight(layer.Input), To = control.goalWeight, Duration = control.blendDuration };
        }

        void OnAnimatorLayerControl(VRC_AnimatorLayerControl control)
        {
            var layer = Find(control.playable.ToString());
            if (layer == null || control.layer < 0 || control.layer >= layer.Playable.GetLayerCount()) return;
            layer.SubLayers[control.layer] = new Blend { From = layer.Playable.GetLayerWeight(control.layer), To = control.goalWeight, Duration = control.blendDuration };
        }

        void OnTrackingControl(VRC_AnimatorTrackingControl c)
        {
            const VRC_AnimatorTrackingControl.TrackingType keep = VRC_AnimatorTrackingControl.TrackingType.NoChange;
            bool Tracked(VRC_AnimatorTrackingControl.TrackingType t, bool current) => t == keep ? current : t == VRC_AnimatorTrackingControl.TrackingType.Tracking;
            var s = Tracking;
            s.Head = Tracked(c.trackingHead, s.Head);
            s.LeftHand = Tracked(c.trackingLeftHand, s.LeftHand);
            s.RightHand = Tracked(c.trackingRightHand, s.RightHand);
            s.Hip = Tracked(c.trackingHip, s.Hip);
            s.LeftFoot = Tracked(c.trackingLeftFoot, s.LeftFoot);
            s.RightFoot = Tracked(c.trackingRightFoot, s.RightFoot);
            s.LeftFingers = Tracked(c.trackingLeftFingers, s.LeftFingers);
            s.RightFingers = Tracked(c.trackingRightFingers, s.RightFingers);
            s.Eyes = Tracked(c.trackingEyes, s.Eyes);
            s.Mouth = Tracked(c.trackingMouth, s.Mouth);
        }

        void OnParameterDriver(VRC_AvatarParameterDriver driver)
        {
            // Remote players only run drivers that are not local-only.
            if (viewer == Viewer.Remote && driver.localOnly) return;
            foreach (var p in driver.parameters)
            {
                if (string.IsNullOrEmpty(p.name) || recorded.Contains(p.name)) continue;
                switch (p.type)
                {
                    case VRC_AvatarParameterDriver.ChangeType.Set:
                        Set(p.name, p.value);
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Add:
                        Set(p.name, Get(p.name) + p.value);
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Random:
                        if (random.NextDouble() <= p.chance || p.chance <= 0f)
                            Set(p.name, p.valueMin + (float)random.NextDouble() * (p.valueMax - p.valueMin));
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Copy:
                        float v = Get(p.source);
                        if (p.convertRange) v = Mathf.Lerp(p.destMin, p.destMax, Mathf.InverseLerp(p.sourceMin, p.sourceMax, v));
                        Set(p.name, v);
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
            foreach (var m in ownMasks) UnityEngine.Object.DestroyImmediate(m);
            ownMasks.Clear();
            if (live == this)
            {
                live = null;
                Hooks.Uninstall();
            }
        }

        /// <summary>
        /// The SDK's behaviours call static <c>Initialize</c> hooks when they are created and their instance
        /// <c>ApplySettings</c> when their state is entered; VRChat's client (and Gesture Manager) subscribe the same way.
        /// Only calls for the live stack's animator are acted on.
        /// </summary>
        static class Hooks
        {
            static bool installed;

            public static void Install()
            {
                if (installed) return;
                installed = true;
                VRC_AnimatorLayerControl.Initialize += InitLayerControl;
                VRC_PlayableLayerControl.Initialize += InitPlayableControl;
                VRC_AnimatorTrackingControl.Initialize += InitTrackingControl;
                VRC_AvatarParameterDriver.OnApplySettings += Driver;
            }

            public static void Uninstall()
            {
                if (!installed) return;
                installed = false;
                VRC_AnimatorLayerControl.Initialize -= InitLayerControl;
                VRC_PlayableLayerControl.Initialize -= InitPlayableControl;
                VRC_AnimatorTrackingControl.Initialize -= InitTrackingControl;
                VRC_AvatarParameterDriver.OnApplySettings -= Driver;
            }

            static bool Ours(Animator animator) => live != null && animator == live.animator;

            static void InitLayerControl(VRC_AnimatorLayerControl c)
            {
                c.ApplySettings -= LayerControl;
                c.ApplySettings += LayerControl;
            }

            static void InitPlayableControl(VRC_PlayableLayerControl c)
            {
                c.ApplySettings -= PlayableControl;
                c.ApplySettings += PlayableControl;
            }

            static void InitTrackingControl(VRC_AnimatorTrackingControl c)
            {
                c.ApplySettings -= TrackingControl;
                c.ApplySettings += TrackingControl;
            }

            static void LayerControl(VRC_AnimatorLayerControl c, Animator a)
            {
                if (Ours(a)) live.OnAnimatorLayerControl(c);
            }

            static void PlayableControl(VRC_PlayableLayerControl c, Animator a)
            {
                if (Ours(a)) live.OnPlayableLayerControl(c);
            }

            static void TrackingControl(VRC_AnimatorTrackingControl c, Animator a)
            {
                if (Ours(a)) live.OnTrackingControl(c);
            }

            static void Driver(VRC_AvatarParameterDriver d, Animator a)
            {
                if (Ours(a)) live.OnParameterDriver(d);
            }
        }
    }
}
