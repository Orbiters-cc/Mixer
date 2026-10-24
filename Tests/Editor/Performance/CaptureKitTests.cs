using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Performance.Tests
{
    /// <summary>
    /// The capture kit: its mesh, what its shader draws into a camera's frames (decoded with the contracts' decoders,
    /// as Orbiters - N4N0 does) and its installation. Generated avatars in preview scenes, assets in a temporary folder.
    /// </summary>
    public sealed class CaptureKitTests
    {
        const string Folder = "Assets/__MixerCaptureKitTests";
        static readonly int CameraModeId = Shader.PropertyToID("_VRChatCameraMode");
        static readonly int MirrorModeId = Shader.PropertyToID("_VRChatMirrorMode");
        Scene scene;
        readonly List<Object> created = new List<Object>();

        [SetUp]
        public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                if (o != null)
                    Object.DestroyImmediate(o);
            created.Clear();
            foreach (var root in scene.GetRootGameObjects()) Undo.ClearUndo(root);
            EditorSceneManager.ClosePreviewScene(scene);
            if (AssetDatabase.IsValidFolder(Folder))
            {
                // A kit made again records its assets' old contents for Undo.
                foreach (string guid in AssetDatabase.FindAssets("", new[] { Folder }))
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)))
                        if (asset != null)
                            Undo.ClearUndo(asset);
                AssetDatabase.DeleteAsset(Folder);
            }
        }

        [Test]
        public void MeshCarriesEveryHumanoidBone()
        {
            var avatar = TestAvatar.Build(scene);
            var animator = avatar.GetComponent<Animator>();
            var space = new GameObject("Kit").transform;
            space.SetParent(avatar.transform, false);
            space.localPosition = new Vector3(0.3f, -0.2f, 0.1f);
            var mesh = CaptureKit.BuildMesh(animator, space, out var bones, out ulong mask);
            created.Add(mesh);
            var present = Enumerable.Range(0, SkeletonPose.Bones).Where(b => animator.GetBoneTransform((HumanBodyBones)b) != null).ToList();
            Assert.AreEqual(present.Count, bones.Length);
            Assert.AreSame(animator.GetBoneTransform(HumanBodyBones.Hips), bones[0]);
            Assert.AreEqual(present.Aggregate(0UL, (m, b) => m | 1UL << b), mask);
            Assert.AreEqual(3 * (5 + present.Count), mesh.vertexCount);
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            var kinds = Enumerable.Range(0, mesh.vertexCount / 3).Select(t => uvs[3 * t]).ToList();
            Assert.AreEqual(new Vector2(CaptureKit.StripKind, 0f), kinds[0]);
            Assert.AreEqual(new Vector2(CaptureKit.FillKind, 0f), kinds[1]);
            for (int copy = 0; copy < 3; copy++) Assert.AreEqual(new Vector2(CaptureKit.HeaderKind, copy), kinds[2 + copy]);
            CollectionAssert.AreEqual(present.Select(b => (float)b), kinds.Skip(5).Select(k => k.x));
            Assert.AreEqual(Enumerable.Range(0, mesh.vertexCount), mesh.GetTriangles(0), "triangles in mesh order");

            // Skinned through the bind poses, each bone's triangle sits at the bone with axes one metre long.
            var weights = mesh.boneWeights;
            var vertices = mesh.vertices;
            for (int t = 5; t < mesh.vertexCount / 3; t++)
            {
                int index = weights[3 * t].boneIndex0;
                Assert.AreEqual(1f, weights[3 * t].weight0);
                var bone = bones[index];
                Assert.AreEqual(present[t - 5], (int)uvs[3 * t].x);
                var toWorld = bone.localToWorldMatrix * mesh.bindposes[index];
                Vector3 o = toWorld.MultiplyPoint3x4(vertices[3 * t]), x = toWorld.MultiplyPoint3x4(vertices[3 * t + 1]), y = toWorld.MultiplyPoint3x4(vertices[3 * t + 2]);
                Assert.Less(Vector3.Distance(bone.position, o), 1e-5f);
                Assert.Less(Vector3.Distance(bone.right, x - o), 1e-5f);
                Assert.Less(Vector3.Distance(bone.up, y - o), 1e-5f);
            }
        }

        [Test]
        public void CameraFramesCarryTheStripAndTheAvatarsPose()
        {
            var avatar = TestAvatar.Build(scene);
            avatar.name = "Kit test avatar";
            avatar.transform.SetPositionAndRotation(new Vector3(12.5f, 0.25f, -30.75f), Quaternion.Euler(0f, 35f, 0f));
            var animator = avatar.GetComponent<Animator>();
            var smr = CaptureKit.Build(avatar.GetComponent<VRCAvatarDescriptor>(), CaptureKit.Reach.NearYou, Folder);
            smr.enabled = true;
            // The wearer's own client (the kit's local layer sets it from IsLocal).
            smr.sharedMaterial.SetFloat("_OrbitersLocal", 1f);
            // A pose: the arm raised and twisted, the head turned, the hips moved after the kit was made.
            animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation = Quaternion.Euler(10f, 20f, 75f);
            animator.GetBoneTransform(HumanBodyBones.LeftLowerArm).localRotation = Quaternion.Euler(-30f, 5f, 40f);
            animator.GetBoneTransform(HumanBodyBones.Head).localRotation = Quaternion.Euler(15f, -50f, 8f);
            animator.GetBoneTransform(HumanBodyBones.RightUpperLeg).localRotation = Quaternion.Euler(-60f, 0f, 3f);
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            hips.position += new Vector3(0.2f, -0.1f, 0.4f);
            hips.rotation = Quaternion.Euler(4f, 20f, -6f) * hips.rotation;

            var image = Render(hips.position, 2f, out var strip, out string error);
            Assert.IsNotNull(strip, error);
            Assert.IsFalse(strip.Value.Payload.IsDepthFrame);
            var pose = SkeletonBand.Decode(image, strip.Value.Flipped);
            Assert.IsNotNull(pose, "skeleton band");
            Assert.AreEqual(strip.Value.Payload.Time, pose.Time, "the band's time is the strip's");
            Assert.AreEqual(CaptureKit.Identity(avatar.GetComponent<VRCAvatarDescriptor>()), pose.Avatar);
            Assert.AreEqual(CaptureKit.Mask(animator), pose.Mask);
            Assert.Less(Vector3.Distance(hips.position, new Vector3(pose.HipsX, pose.HipsY, pose.HipsZ)), 1e-4f, "hips");
            for (int b = 0; b < SkeletonPose.Bones; b++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)b);
                if (bone == null) continue;
                var r = pose.Rotations[b];
                float angle = Quaternion.Angle(bone.rotation, new Quaternion(r[0], r[1], r[2], r[3]));
                Assert.Less(angle, 0.05f, ((HumanBodyBones)b) + " is " + angle + "° off");
            }
        }

        /// <summary>Another player's copy of the avatar (<c>_OrbitersLocal</c> 0) draws nothing, in VRChat's camera too.</summary>
        [Test]
        public void RemoteCopiesShowNothing()
        {
            var avatar = TestAvatar.Build(scene);
            var smr = CaptureKit.Build(avatar.GetComponent<VRCAvatarDescriptor>(), CaptureKit.Reach.NearYou, Folder);
            smr.enabled = true;
            var hips = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            Render(hips.position, 2f, out var strip, out _);
            Assert.IsNull(strip, "a strip from a remote copy");
        }

        [Test]
        public void OrdinaryRendersShowNothing()
        {
            var avatar = TestAvatar.Build(scene);
            var smr = CaptureKit.Build(avatar.GetComponent<VRCAvatarDescriptor>(), CaptureKit.Reach.NearYou, Folder);
            smr.enabled = true;
            var hips = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            Render(hips.position, 0f, out var strip, out _);
            Assert.IsNull(strip, "a strip outside VRChat's camera");
        }

        [Test]
        public void InstallMergesTheRecordToggleWithItsOwnName()
        {
            if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not in this project.");
            var avatar = TestAvatar.Build(scene);
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var kit = CaptureKit.Install(descriptor, CaptureKit.Reach.Everywhere, Folder);
            Assert.AreSame(kit, CaptureKit.Find(descriptor));
            var smr = CaptureKit.Renderer(descriptor);
            Assert.IsFalse(smr.enabled, "hidden until Record");
            var state = CaptureKit.Inspect(descriptor);
            Assert.IsTrue(state.Installed && !state.Stale);
            Assert.AreEqual(CaptureKit.Reach.Everywhere, state.Reach);

            var fury = kit.GetComponents(VrcFury.Component).Single();
            var content = VrcFury.Content(fury);
            Assert.IsTrue(VrcFury.IsGlobalParameter(content, CaptureKit.RecordParameter), "Orbiters - N4N0 listens for OrbitersRecord by name");
            Assert.IsTrue(VrcFury.IsGlobalParameter(content, CaptureKit.DepthParameter));

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/Capture kit FX.controller");
            CollectionAssert.AreEquivalent(new[] { CaptureKit.RecordParameter, CaptureKit.DepthParameter, CaptureKit.LocalParameter }, controller.parameters.Select(p => p.name));
            // The local layer: remote by default, local while IsLocal, each setting the material's _OrbitersLocal.
            var local = controller.layers.Single(l => l.name == CaptureKit.LocalLayer);
            Assert.AreEqual(1f, local.defaultWeight);
            Assert.AreEqual("Remote", local.stateMachine.defaultState.name);
            var set = local.stateMachine.states.Select(s => s.state).ToDictionary(s => s.name, s => AnimationUtility.GetEditorCurve((AnimationClip)s.motion, EditorCurveBinding.FloatCurve(CaptureKit.RendererName, typeof(SkinnedMeshRenderer), "material._OrbitersLocal")).Evaluate(0f));
            Assert.AreEqual(0f, set["Remote"]);
            Assert.AreEqual(1f, set["Local"]);
            // A controller made before the layer gets it, once.
            controller.RemoveLayer(System.Array.FindIndex(controller.layers, l => l.name == CaptureKit.LocalLayer));
            CaptureKit.SharedController(Folder);
            CaptureKit.SharedController(Folder);
            Assert.AreEqual(1, controller.layers.Count(l => l.name == CaptureKit.LocalLayer));
            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(Folder + "/Capture kit parameters.asset");
            var record = parameters.parameters.Single(p => p.name == CaptureKit.RecordParameter);
            Assert.IsFalse(record.saved || record.networkSynced, "local and not saved");
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(Folder + "/Capture kit menu.asset");
            var sub = menu.controls.Single().subMenu;
            Assert.AreEqual(CaptureKit.RecordParameter, sub.controls.Single(c => c.name == "Record").parameter.name);

            // Installing again replaces the kit and keeps its assets.
            var mesh = smr.sharedMesh;
            CaptureKit.Install(descriptor, CaptureKit.Reach.NearYou, Folder);
            Assert.AreEqual(1, avatar.transform.Cast<Transform>().Count(t => t.name == CaptureKit.ObjectName));
            Assert.AreSame(mesh, CaptureKit.Renderer(descriptor).sharedMesh);
            Assert.AreEqual(CaptureKit.Reach.NearYou, CaptureKit.Inspect(descriptor).Reach);
            CaptureKit.Remove(descriptor);
            Assert.IsNull(CaptureKit.Find(descriptor));
        }

        [Test]
        public void ARenamedAvatarNeedsTheKitAgain()
        {
            var avatar = TestAvatar.Build(scene);
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            CaptureKit.Build(descriptor, CaptureKit.Reach.NearYou, Folder);
            var state = CaptureKit.Inspect(descriptor);
            Assert.IsTrue(state.Installed && !state.Stale);
            // The kit records the avatar's identity (its name's): recordings after a rename would not match it.
            avatar.name = "Renamed avatar";
            Assert.IsTrue(CaptureKit.Inspect(descriptor).Stale);
        }

        [Test]
        public void TheKitGoesOnlyOnAvatarsInAScene()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", Folder.Substring("Assets/".Length));
            var source = new GameObject("Prefab avatar");
            SceneManager.MoveGameObjectToScene(source, scene);
            source.AddComponent<Animator>();
            source.AddComponent<VRCAvatarDescriptor>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, Folder + "/Prefab avatar.prefab");
            var descriptor = prefab.GetComponent<VRCAvatarDescriptor>();
            var e = Assert.Throws<System.InvalidOperationException>(() => CaptureKit.Build(descriptor, CaptureKit.Reach.NearYou, Folder));
            StringAssert.Contains("prefab asset", e.Message);
            Assert.Throws<System.InvalidOperationException>(() => CaptureKit.Remove(descriptor));
            Assert.IsFalse(SceneManager.GetActiveScene().GetRootGameObjects().Any(g => g.name == CaptureKit.ObjectName), "nothing left in the open scene");
        }

        [Test]
        public void ASharedMenuMissingItsSubmenuIsMadeAgain()
        {
            var (menu, parameters) = CaptureKit.SharedMenu(Folder);
            Assert.IsTrue(CaptureKit.Complete(menu, parameters));
            AssetDatabase.DeleteAsset(Folder + "/Capture kit Orbiters menu.asset");
            Assert.IsFalse(CaptureKit.Complete(menu, parameters), "the Orbiters submenu would be empty in VRChat");
            var (again, againParameters) = CaptureKit.SharedMenu(Folder);
            Assert.AreSame(menu, again, "made again in place");
            Assert.AreSame(parameters, againParameters);
            Assert.IsTrue(CaptureKit.Complete(again, againParameters));
        }

        /// <summary>
        /// Renders the preview scene from 2.5 m in front of <paramref name="target"/> with <c>_VRChatCameraMode</c> set
        /// to <paramref name="cameraMode"/>; returns the frame top-down and its decoded strip (null when none).
        /// </summary>
        ImageBuffer Render(Vector3 target, float cameraMode, out StripDecode? strip, out string error)
        {
            const int width = 1280, height = 720;
            var go = new GameObject("Kit test camera");
            SceneManager.MoveGameObjectToScene(go, scene);
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.scene = scene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.3f, 0.45f, 0.65f, 1f);
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 1000f;
            go.transform.position = target + new Vector3(0.4f, 0.3f, 2.5f);
            go.transform.LookAt(target);
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var read = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            created.Add(rt);
            created.Add(read);
            camera.targetTexture = rt;
            float mode = Shader.GetGlobalFloat(CameraModeId), mirror = Shader.GetGlobalFloat(MirrorModeId);
            Shader.SetGlobalFloat(CameraModeId, cameraMode);
            Shader.SetGlobalFloat(MirrorModeId, 0f);
            var active = RenderTexture.active;
            try
            {
                camera.Render();
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = active;
                Shader.SetGlobalFloat(CameraModeId, mode);
                Shader.SetGlobalFloat(MirrorModeId, mirror);
                camera.targetTexture = null;
            }
            // Unity reads bottom-up; frames are top-down.
            var bottomUp = read.GetRawTextureData<byte>().ToArray();
            var data = new byte[bottomUp.Length];
            for (int y = 0; y < height; y++)
                System.Array.Copy(bottomUp, (height - 1 - y) * width * 4, data, y * width * 4, width * 4);
            var image = new ImageBuffer(width, height, data);
            strip = CaptureStrip.TryDecode(image, out var decoded, out error) ? decoded : (StripDecode?)null;
            return image;
        }
    }
}
