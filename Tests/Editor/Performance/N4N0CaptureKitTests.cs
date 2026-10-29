using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Performance.Tests
{
    /// <summary>
    /// N4N0's capture kit and camera rig (Orbiters Toolkit; contracts' Docs/rig-frames.md, N4N0's capture kit), which make
    /// the frames Mixer's performances and reconstructions come from: what they draw into a camera's frames, decoded with
    /// the contracts' decoders as Orbiters - N4N0 does. N4N0 made in a temporary folder, its kit made for a generated
    /// avatar in a preview scene as an upload makes it.
    /// </summary>
    public sealed class N4N0CaptureKitTests
    {
        const string Folder = "Assets/__MixerN4N0CaptureKitTests";
        static readonly int CameraModeId = Shader.PropertyToID("_VRChatCameraMode");
        static readonly int MirrorModeId = Shader.PropertyToID("_VRChatMirrorMode");
        Scene scene;
        readonly List<Object> created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (VrcFury.Writer == null) Assert.Ignore("N4N0 needs VRCFury in the project.");
            scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                if (o != null)
                    Object.DestroyImmediate(o);
            created.Clear();
            if (scene.IsValid())
            {
                foreach (var root in scene.GetRootGameObjects()) Undo.ClearUndo(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        /// <summary>N4N0 on <paramref name="avatar"/> with its kit made as an upload makes it; its kit's renderer.</summary>
        (Transform orb, SkinnedMeshRenderer kit) N4N0(GameObject avatar)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", Folder.Substring("Assets/".Length));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(Folder + "/N4N0"));
            var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform)).transform;
            var kit = OrbInstaller.BuildCaptureKit(orb, avatar.GetComponent<Animator>(), avatar.name, Folder + "/Kits");
            Assert.IsNotNull(kit, "N4N0's capture kit");
            return (orb, kit);
        }

        [Test]
        public void CameraFramesCarryTheStripAndTheAvatarsPose()
        {
            var avatar = TestAvatar.Build(scene);
            avatar.name = "Kit test avatar";
            avatar.transform.SetPositionAndRotation(new Vector3(12.5f, 0.25f, -30.75f), Quaternion.Euler(0f, 35f, 0f));
            var animator = avatar.GetComponent<Animator>();
            var (_, smr) = N4N0(avatar);
            smr.enabled = true;
            // The wearer's own client (N4N0's local layer sets it from IsLocal).
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
            Assert.AreEqual(SkeletonBand.Crc16(Encoding.UTF8.GetBytes(avatar.name)), pose.Avatar, "the identity Mixer's bake looks for");
            ulong mask = 0;
            for (int b = 0; b < SkeletonPose.Bones; b++)
                if (animator.GetBoneTransform((HumanBodyBones)b) != null)
                    mask |= 1UL << b;
            Assert.AreEqual(mask, pose.Mask);
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

        /// <summary>
        /// A rig frame from real renders: the six face cameras render (the rig's renderer stamps each with the frame's
        /// time), then VRChat's camera renders the frame, its strip of kind 2 carrying the rig's pose and the faces under it.
        /// </summary>
        [Test]
        public void RigFramesCarryTheRigsPoseAndSixCurrentFaces()
        {
            var avatar = TestAvatar.Build(scene);
            avatar.transform.position = new Vector3(12.5f, 0.25f, -30.75f);
            var (orb, _) = N4N0(avatar);
            var rig = orb.Find(OrbInstaller.CaptureRigPath);
            // Where following would put it, without VRChat's constraint solver: beside the avatar, turned 30°.
            rig.GetComponent<VRCParentConstraint>().IsActive = false;
            rig.SetPositionAndRotation(avatar.transform.position + new Vector3(0.5f, 1.5f, 2f), Quaternion.Euler(0f, 30f, 0f));
            var renderer = orb.Find(OrbInstaller.RigRendererPath).GetComponent<MeshRenderer>();
            renderer.enabled = true;
            var material = renderer.sharedMaterial;
            material.SetFloat("_OrbitersLocal", 1f);
            float time = Enumerable.Range(0, 1000).Select(i => 50f + i / 90f).First(t => RigFrames.IsRigTime(t, 0));
            material.SetFloat("_OrbitersUseTimeOverride", 1f);
            material.SetFloat("_OrbitersTimeOverride", time);
            for (int face = 1; face <= RigFrames.Faces; face++)
            {
                var camera = orb.Find(OrbInstaller.RigFacePath(face)).GetComponent<Camera>();
                camera.scene = scene;
                camera.Render();
            }

            var image = Render(rig.position + rig.forward * 0.5f, 2f, out var strip, out string error);
            Assert.IsNotNull(strip, error);
            var p = strip.Value.Payload;
            Assert.IsTrue(p.IsRigFrame, "a strip of kind 2");
            Assert.AreEqual(time, p.Time);
            CollectionAssert.AreEqual(RigFrames.Projection, p.Projection);
            Assert.AreEqual((RigFrames.Near, RigFrames.Far), (p.Near, p.Far));
            var axes = new[] { rig.right, rig.up, rig.forward };
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) Assert.AreEqual(axes[col][row], p.CameraToWorld[4 * row + col], 1e-5f, "the rig's axes");
                Assert.AreEqual(rig.position[row], p.CameraToWorld[4 * row + 3], 1e-4f, "the rig's position");
            }
            Assert.IsTrue(RigFrames.TryLayout(image.Width, image.Height, out int side, out int x0, out int y0));
            uint bits = System.BitConverter.ToUInt32(System.BitConverter.GetBytes(time), 0);
            for (int t = 0; t < RigFrames.Faces; t++)
            {
                Assert.IsTrue(RigFrames.TryReadStamp(image, RigFrames.Tile(side, x0, y0, t), strip.Value.Flipped, out uint stamp), "tile " + t + ": a stamp");
                Assert.AreEqual(bits, stamp, "tile " + t + " (face " + RigFrames.TileFaces[t] + ") rendered in this frame");
            }
        }

        /// <summary>
        /// VRChat's camera 30 m from the avatar, looking away from it. With its bounds at rest (4 m around the rig at the
        /// hips) Unity culls the kit and the frame has no strip: what recordings did whenever the camera did not see the
        /// avatar. Recording, N4N0's animator gives the kit bounds the camera is inside of, and the frame carries the strip
        /// and the skeleton band.
        /// </summary>
        [Test]
        public void WhileRecordingTheKitDrawsWhereverTheCameraLooks()
        {
            var avatar = TestAvatar.Build(scene);
            avatar.name = "Kit test avatar";
            var (orb, smr) = N4N0(avatar);
            var hips = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            // The camera stands 30 m to the side and 2.5 m behind this point, looking at it: the avatar is 85° off its axis.
            var away = hips.position + new Vector3(30f, 0f, 0f);
            smr.enabled = true;
            smr.sharedMaterial.SetFloat("_OrbitersLocal", 1f);
            Render(away, 2f, out var culled, out _);
            Assert.IsNull(culled, "at rest the kit is culled out of a view without the avatar");

            var graph = PlayableGraph.Create("N4N0 capture kit test");
            try
            {
                var playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/N4N0/N4N0.controller"));
                AnimationPlayableOutput.Create(graph, "N4N0", orb.gameObject.AddComponent<Animator>()).SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                graph.Play();
                playable.SetBool("IsLocal", true);
                playable.SetBool(OrbInstaller.CaptureRecord, true);
                for (int i = 0; i < 3; i++) graph.Evaluate(.02f);
                Assert.IsTrue(smr.enabled, "recording");
                var image = Render(away, 2f, out var strip, out string error);
                Assert.IsNotNull(strip, "no strip while recording: " + error);
                Assert.IsFalse(strip.Value.Payload.IsDepthFrame || strip.Value.Payload.IsRigFrame);
                var pose = SkeletonBand.Decode(image, strip.Value.Flipped);
                Assert.IsNotNull(pose, "skeleton band");
                Assert.Less(Vector3.Distance(hips.position, new Vector3(pose.HipsX, pose.HipsY, pose.HipsZ)), 1e-4f, "the hips, out of view");
            }
            finally
            {
                graph.Destroy();
            }
        }

        /// <summary>Another player's copy of the avatar (<c>_OrbitersLocal</c> 0) draws nothing, in VRChat's camera too.</summary>
        [Test]
        public void RemoteCopiesShowNothing()
        {
            var avatar = TestAvatar.Build(scene);
            var (_, smr) = N4N0(avatar);
            smr.enabled = true;
            var hips = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            Render(hips.position, 2f, out var strip, out _);
            Assert.IsNull(strip, "a strip from a remote copy");
        }

        [Test]
        public void OrdinaryRendersShowNothing()
        {
            var avatar = TestAvatar.Build(scene);
            var (_, smr) = N4N0(avatar);
            smr.enabled = true;
            smr.sharedMaterial.SetFloat("_OrbitersLocal", 1f);
            var hips = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            Render(hips.position, 0f, out var strip, out _);
            Assert.IsNull(strip, "a strip outside VRChat's camera");
        }

        /// <summary>The identity N4N0's kit bakes is the one Mixer's bake compares a session's skeleton band with.</summary>
        [Test]
        public void TheKitsIdentityIsTheContracts()
        {
            foreach (string name in new[] { "", "MasculineCanine", "Kit test avatar", "Ünïcödé アバター", "a/b\\c" })
                Assert.AreEqual(SkeletonBand.Crc16(Encoding.UTF8.GetBytes(name)), OrbInstaller.CaptureIdentity(name), name);
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
