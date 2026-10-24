using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// The capture kit on a VRChat avatar. One skinned helper mesh with the contracts' <c>Hidden/Orbiters/CaptureKit</c>
    /// shader draws, only into the frames VRChat's camera renders (never in your view or in mirrors), the capture strip
    /// (the camera's pose), the skeleton band (the pose VRChat gave the avatar: every humanoid bone after IK, locomotion
    /// and animation) and, when asked, depth frames. The Action Menu toggle Orbiters › Record (avatar parameter
    /// <see cref="RecordParameter"/>, local only) shows it and starts and stops Orbiters - N4N0's recording over OSC;
    /// Orbiters › Depth frames adds depth. A VRCFury Full Controller merges the toggles, so the avatar's own controllers,
    /// menus and parameters stay as they are.
    /// </summary>
    internal static class CaptureKit
    {
        public const string ObjectName = "Orbiters Capture Kit";
        public const string RendererName = "Kit";
        public const string RecordParameter = "OrbitersRecord";
        public const string DepthParameter = "OrbitersDepthFrames";
        /// <summary>VRChat's built-in: true on the wearer's own client.</summary>
        public const string LocalParameter = "IsLocal";
        /// <summary>The controller's layer that sets the kit material's <c>_OrbitersLocal</c> from <see cref="LocalParameter"/>.</summary>
        public const string LocalLayer = ObjectName + " · local";
        public const string ShaderName = "Hidden/Orbiters/CaptureKit";
        /// <summary>Where the kit keeps its assets: the shared controller and menu here, each avatar's mesh and material in a folder of its own.</summary>
        public const string Root = "Assets/Orbiters/Capture Kit";
        // Triangle kinds (uv0.x), as the shader reads them; bones are 0..54.
        internal const float StripKind = 64f, FillKind = 65f, HeaderKind = 66f;
        // While "Depth frames" is on, about one frame in DepthEvery is a depth frame.
        internal const float DepthEvery = 4f;
        // World size of the bounds: VRChat's "Good" bounds, or everywhere.
        const float NearSize = 4f, EverywhereSize = 20000f;

        /// <summary>
        /// Where the camera may be for the kit to draw. Unity skips a renderer whose bounds the camera does not see, and
        /// VRChat ranks the avatar by its bounds, the kit's included.
        /// </summary>
        public enum Reach
        {
            /// <summary>A 4 m cube around the hips (VRChat's "Good" bounds): the camera sees you or is within about 2 m.</summary>
            NearYou,
            /// <summary>Drawn wherever the camera looks; VRChat ranks the avatar's bounds "Very Poor".</summary>
            Everywhere,
        }

        /// <summary>The kit on an avatar as it is now.</summary>
        internal sealed class State
        {
            public bool Installed;
            public int Bones;
            public Reach Reach;
            /// <summary>The armature has other humanoid bones than when the kit was made, or the avatar another name (the
            /// identity the kit records): install it again.</summary>
            public bool Stale;
            public bool VrcFury;
        }

        static readonly int BoneMaskId = Shader.PropertyToID("_OrbitersBoneMask");
        static readonly int AvatarId = Shader.PropertyToID("_OrbitersAvatarId");
        static readonly int DepthEveryId = Shader.PropertyToID("_OrbitersDepthEvery");
        static readonly int OnlyInCameraId = Shader.PropertyToID("_OrbitersOnlyInCamera");

        /// <summary>The kit installed on <paramref name="avatar"/>, or null.</summary>
        public static Transform Find(Component avatar) => avatar == null ? null : avatar.transform.Find(ObjectName);

        /// <summary>The kit's renderer on <paramref name="avatar"/>, or null.</summary>
        public static SkinnedMeshRenderer Renderer(Component avatar)
        {
            var kit = Find(avatar);
            var child = kit != null ? kit.Find(RendererName) : null;
            return child != null ? child.GetComponent<SkinnedMeshRenderer>() : null;
        }

        /// <summary>The 16-bit identity the kit bakes for an avatar (skeleton band header): CRC-16 of its name.</summary>
        public static ushort Identity(Component avatar) => SkeletonBand.Crc16(Encoding.UTF8.GetBytes(avatar.gameObject.name));

        public static State Inspect(VRCAvatarDescriptor avatar)
        {
            var state = new State { VrcFury = VrcFury.Writer != null };
            var smr = Renderer(avatar);
            if (smr == null || smr.sharedMaterial == null) return state;
            state.Installed = true;
            state.Bones = smr.bones.Count(b => b != null);
            var hips = smr.rootBone;
            float scale = hips != null ? Mathf.Abs(hips.lossyScale.x) : 1f;
            state.Reach = smr.localBounds.size.x * scale > NearSize * 2f ? Reach.Everywhere : Reach.NearYou;
            var animator = avatar.GetComponent<Animator>();
            var material = smr.sharedMaterial;
            state.Stale = animator == null || !animator.isHuman || !material.HasProperty(BoneMaskId) ||
                          material.GetVector(BoneMaskId) != MaskWords(Mask(animator)) || smr.bones.Any(b => b == null) ||
                          !material.HasProperty(AvatarId) || Mathf.RoundToInt(material.GetFloat(AvatarId)) != Identity(avatar);
            return state;
        }

        /// <summary>
        /// Installs the kit on <paramref name="avatar"/>, or makes it again, with Undo. The mesh is made for the armature as
        /// it is now: install again after changing it. Assets go to <paramref name="root"/> (the shared controller and
        /// menu) and a folder per avatar below it.
        /// </summary>
        public static Transform Install(VRCAvatarDescriptor avatar, Reach reach, string root = Root)
        {
            if (VrcFury.Writer == null)
                throw new InvalidOperationException("The capture kit's Action Menu toggles are merged by VRCFury: add VRCFury to the project first.");
            Undo.SetCurrentGroupName("Install the capture kit");
            int group = Undo.GetCurrentGroup();
            var smr = Build(avatar, reach, root);
            var kit = smr.transform.parent.gameObject;
            var controller = SharedController(root);
            var (menu, parameters) = SharedMenu(root);
            var full = VrcFury.Writer.FullController(kit, controller, menu, parameters);
            MakeGlobal(full, RecordParameter, DepthParameter);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(group);
            return kit.transform;
        }

        /// <summary>Takes the kit off <paramref name="avatar"/>, with Undo. Its assets stay.</summary>
        public static void Remove(Component avatar)
        {
            RequireSceneObject(avatar);
            var kit = Find(avatar);
            if (kit != null) Undo.DestroyObjectImmediate(kit.gameObject);
        }

        /// <summary>
        /// The kit's object and renderer on <paramref name="avatar"/> (an earlier kit is replaced), its renderer off: the
        /// Record toggle turns it on. Mesh and material are saved in the avatar's folder below <paramref name="root"/>.
        /// </summary>
        internal static SkinnedMeshRenderer Build(VRCAvatarDescriptor avatar, Reach reach, string root)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            RequireSceneObject(avatar);
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.isHuman)
                throw new InvalidOperationException(avatar.name + " is not humanoid: the capture kit draws a humanoid's bones.");
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("Mixer's contracts have no " + ShaderName + " shader.");
            string folder = root + "/" + SafePaths.FileName(avatar.gameObject.name);
            if (!AssetPaths.EnsureFolder(folder)) throw new InvalidOperationException("Could not create " + folder + ".");

            Remove(avatar);
            var kit = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(kit, "Install the capture kit");
            kit.transform.SetParent(avatar.transform, false);
            var holder = new GameObject(RendererName);
            holder.transform.SetParent(kit.transform, false);
            var smr = holder.AddComponent<SkinnedMeshRenderer>();

            var mesh = BuildMesh(animator, holder.transform, out var bones, out ulong mask);
            mesh = SaveInPlace(mesh, folder + "/Capture kit mesh.asset");
            var material = new Material(shader);
            material.SetVector(BoneMaskId, MaskWords(mask));
            material.SetFloat(AvatarId, Identity(avatar));
            material.SetFloat(DepthEveryId, 0f);
            material.SetFloat(OnlyInCameraId, 1f);
            material = SaveInPlace(material, folder + "/Capture kit.mat");

            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[0];
            smr.sharedMaterial = material;
            smr.quality = SkinQuality.Bone1;
            smr.updateWhenOffscreen = false;
            smr.skinnedMotionVectors = false;
            // Bounds are in the hips' space: scaled back so they cover the same world size on any armature scale.
            var s = bones[0].lossyScale;
            float size = reach == Reach.Everywhere ? EverywhereSize : NearSize;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(size / Mathf.Max(1e-6f, Mathf.Abs(s.x)), size / Mathf.Max(1e-6f, Mathf.Abs(s.y)), size / Mathf.Max(1e-6f, Mathf.Abs(s.z))));
            smr.shadowCastingMode = ShadowCastingMode.Off;
            smr.receiveShadows = false;
            smr.lightProbeUsage = LightProbeUsage.Off;
            smr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            smr.allowOcclusionWhenDynamic = false;
            smr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            smr.enabled = false;
            return smr;
        }

        /// <summary>The kit goes on the avatar in a scene: a prefab asset's objects cannot take it (nor lose it) from here.</summary>
        static void RequireSceneObject(Component avatar)
        {
            if (avatar != null && (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid()))
                throw new InvalidOperationException(avatar.name + " is a prefab asset: pick the avatar in the scene (or open the prefab) to change its capture kit.");
        }

        /// <summary>
        /// The kit's mesh in <paramref name="space"/> (the renderer's transform), skinned to <paramref name="bones"/>
        /// (the hips first): the strip, band fill and three header triangles on the hips, then one triangle per humanoid
        /// bone with vertices at its origin and one metre along its local X and Y axes, each skinned to that bone only.
        /// </summary>
        internal static Mesh BuildMesh(Animator animator, Transform space, out Transform[] bones, out ulong mask)
        {
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var list = new List<Transform> { hips };
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var toSpace = space.worldToLocalMatrix;
            void Triangle(Transform bone, int index, float kind, float copy)
            {
                var o = bone.position;
                foreach (var p in new[] { o, o + bone.right, o + bone.up })
                {
                    vertices.Add(toSpace.MultiplyPoint3x4(p));
                    uvs.Add(new Vector2(kind, copy));
                    weights.Add(new BoneWeight { boneIndex0 = index, weight0 = 1f });
                }
            }
            Triangle(hips, 0, StripKind, 0f);
            Triangle(hips, 0, FillKind, 0f);
            for (int copy = 0; copy < 3; copy++) Triangle(hips, 0, HeaderKind, copy);
            mask = 0;
            for (int b = 0; b < SkeletonPose.Bones; b++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)b);
                if (bone == null) continue;
                mask |= 1UL << b;
                int index = b == (int)HumanBodyBones.Hips ? 0 : list.Count;
                if (index != 0) list.Add(bone);
                Triangle(bone, index, b, 0f);
            }
            bones = list.ToArray();
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            // The shader needs the triangles in this order: the band's fill before the cells drawn over it.
            mesh.SetTriangles(Enumerable.Range(0, vertices.Count).ToArray(), 0, false);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = list.Select(t => t.worldToLocalMatrix * space.localToWorldMatrix).ToArray();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * NearSize);
            return mesh;
        }

        /// <summary>The humanoid bones an avatar has, as the band's 64-bit mask.</summary>
        internal static ulong Mask(Animator animator)
        {
            ulong mask = 0;
            for (int b = 0; b < SkeletonPose.Bones; b++)
                if (animator.GetBoneTransform((HumanBodyBones)b) != null)
                    mask |= 1UL << b;
            return mask;
        }

        /// <summary>The mask as the material keeps it: four 16-bit words, high first.</summary>
        internal static Vector4 MaskWords(ulong mask) =>
            new Vector4((mask >> 48) & 0xFFFF, (mask >> 32) & 0xFFFF, (mask >> 16) & 0xFFFF, mask & 0xFFFF);

        // A kit made again keeps its assets (the renderer an Undo brings back still finds them): new contents, same file,
        // with Undo, so that renderer gets the contents it was made for back too. Named after the file, as Unity expects
        // of a main asset.
        static T SaveInPlace<T>(T asset, string path) where T : Object
        {
            asset.name = System.IO.Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }
            Undo.RegisterCompleteObjectUndo(existing, "Install the capture kit");
            EditorUtility.CopySerialized(asset, existing);
            existing.name = asset.name;
            Object.DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        /// <summary>
        /// The FX controller every kit shares: off, recording, recording with depth frames, following
        /// <see cref="RecordParameter"/> and <see cref="DepthParameter"/>, and the local layer (<see cref="LocalLayer"/>,
        /// added to a controller made before it). Paths are relative to the kit's object.
        /// </summary>
        internal static AnimatorController SharedController(string root)
        {
            string path = root + "/Capture kit FX.controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null)
            {
                AddLocalLayer(existing);
                return existing;
            }
            if (!AssetPaths.EnsureFolder(root)) throw new InvalidOperationException("Could not create " + root + ".");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter(RecordParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(DepthParameter, AnimatorControllerParameterType.Bool);
            var layers = controller.layers;
            layers[0].name = ObjectName;
            controller.layers = layers;
            var machine = layers[0].stateMachine;
            AnimatorState Add(string name, bool shown, float depthEvery, Vector3 position)
            {
                var clip = new AnimationClip { name = name };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(RendererName, typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0f, 1f / 60f, shown ? 1f : 0f));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(RendererName, typeof(SkinnedMeshRenderer), "material._OrbitersDepthEvery"), AnimationCurve.Constant(0f, 1f / 60f, depthEvery));
                AssetDatabase.AddObjectToAsset(clip, controller);
                var state = machine.AddState(name, position);
                state.motion = clip;
                state.writeDefaultValues = false;
                return state;
            }
            var off = Add("Off", false, 0f, new Vector3(300f, 0f));
            var recording = Add("Recording", true, 0f, new Vector3(300f, 100f));
            var depth = Add("Recording with depth frames", true, DepthEvery, new Vector3(600f, 100f));
            machine.defaultState = off;
            Transition(off, recording, (RecordParameter, AnimatorConditionMode.If), (DepthParameter, AnimatorConditionMode.IfNot));
            Transition(off, depth, (RecordParameter, AnimatorConditionMode.If), (DepthParameter, AnimatorConditionMode.If));
            Transition(recording, off, (RecordParameter, AnimatorConditionMode.IfNot));
            Transition(recording, depth, (DepthParameter, AnimatorConditionMode.If));
            Transition(depth, off, (RecordParameter, AnimatorConditionMode.IfNot));
            Transition(depth, recording, (DepthParameter, AnimatorConditionMode.IfNot));
            AddLocalLayer(controller);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// The layer that lets the kit draw on the wearer's own client only: the contracts' kit shader (0.4.0 on) draws
        /// nothing until <c>_OrbitersLocal</c> is 1, which this layer sets from VRChat's <see cref="LocalParameter"/>, so
        /// other players' copies of the avatar never put strips or depth symbols in their pictures and streams.
        /// </summary>
        internal static void AddLocalLayer(AnimatorController controller)
        {
            if (controller.layers.Any(l => l.name == LocalLayer)) return;
            if (!controller.parameters.Any(p => p.name == LocalParameter))
                controller.AddParameter(LocalParameter, AnimatorControllerParameterType.Bool);
            var machine = new AnimatorStateMachine { name = LocalLayer, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(machine, controller);
            AnimatorState Add(string name, float local, Vector3 position)
            {
                var clip = new AnimationClip { name = name };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(RendererName, typeof(SkinnedMeshRenderer), "material._OrbitersLocal"), AnimationCurve.Constant(0f, 1f / 60f, local));
                AssetDatabase.AddObjectToAsset(clip, controller);
                var state = machine.AddState(name, position);
                state.motion = clip;
                state.writeDefaultValues = false;
                return state;
            }
            var remote = Add("Remote", 0f, new Vector3(300f, 0f));
            var local = Add("Local", 1f, new Vector3(300f, 100f));
            machine.defaultState = remote;
            Transition(remote, local, (LocalParameter, AnimatorConditionMode.If));
            Transition(local, remote, (LocalParameter, AnimatorConditionMode.IfNot));
            controller.AddLayer(new AnimatorControllerLayer { name = LocalLayer, stateMachine = machine, defaultWeight = 1f });
            EditorUtility.SetDirty(controller);
        }

        static void Transition(AnimatorState from, AnimatorState to, params (string parameter, AnimatorConditionMode mode)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = 0f;
            foreach (var (parameter, mode) in conditions) t.AddCondition(mode, 0f, parameter);
        }

        /// <summary>The menu every kit shares (Orbiters › Record, Depth frames) and its parameters, both local only.</summary>
        internal static (VRCExpressionsMenu menu, VRCExpressionParameters parameters) SharedMenu(string root)
        {
            string menuPath = root + "/Capture kit menu.asset", subPath = root + "/Capture kit Orbiters menu.asset", parametersPath = root + "/Capture kit parameters.asset";
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath);
            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(parametersPath);
            // A part deleted or edited away (the Orbiters submenu, a toggle, a parameter) is made again, in place.
            if (menu != null && parameters != null && Complete(menu, parameters)) return (menu, parameters);
            if (!AssetPaths.EnsureFolder(root)) throw new InvalidOperationException("Could not create " + root + ".");
            parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[]
            {
                // Not saved: every session starts without recording.
                new VRCExpressionParameters.Parameter { name = RecordParameter, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = false, networkSynced = false },
                new VRCExpressionParameters.Parameter { name = DepthParameter, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = true, networkSynced = false },
            };
            parameters = SaveInPlace(parameters, parametersPath);
            VRCExpressionsMenu.Control Toggle(string name, string parameter) => new VRCExpressionsMenu.Control
            {
                name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1, icon = OrbitersMenuIcons.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
            };
            var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            sub.controls = new List<VRCExpressionsMenu.Control> { Toggle("Record", RecordParameter), Toggle("Depth frames", DepthParameter) };
            sub = SaveInPlace(sub, subPath);
            menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control { name = "Orbiters", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub, icon = OrbitersMenuIcons.Submenu },
            };
            menu = SaveInPlace(menu, menuPath);
            return (menu, parameters);
        }

        /// <summary>Whether the shared menu still opens the Orbiters submenu with both toggles, and the parameters hold both.</summary>
        internal static bool Complete(VRCExpressionsMenu menu, VRCExpressionParameters parameters)
        {
            var sub = menu.controls?.FirstOrDefault(c => c != null && c.type == VRCExpressionsMenu.Control.ControlType.SubMenu)?.subMenu;
            bool Declared(string name) => parameters.parameters != null && parameters.parameters.Any(p => p != null && p.name == name);
            bool Toggled(string name) => sub.controls != null && sub.controls.Any(c => c != null && c.parameter != null && c.parameter.name == name);
            return sub != null && Declared(RecordParameter) && Declared(DepthParameter) && Toggled(RecordParameter) && Toggled(DepthParameter);
        }

        // Orbiters - N4N0 listens for these names: VRCFury must not give them a namespace of their own.
        static void MakeGlobal(Component fullController, params string[] names)
        {
            var serialized = new SerializedObject(fullController);
            var list = serialized.FindProperty("content.globalParams");
            if (list == null || !list.isArray) throw new InvalidOperationException("VRCFury's Full Controller has no global parameter list.");
            foreach (string name in names)
            {
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).stringValue = name;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
