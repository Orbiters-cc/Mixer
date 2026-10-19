using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>Rest-pose editing: bones move, the skin stays; reset to the bind pose; leaf bones; commit.</summary>
    public sealed class MixerArmatureTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;
        private Transform lower, upper;

        // Sessions open before the test (another tool's, the user's): left alone.
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerArmatureFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("Upper").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            mesh = MixerCoreTestsFixtures.Cylinder(16, 12, 0.3f, 2f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/ArmatureFixture.asset");
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
        }

        [TearDown]
        public void TearDown()
        {
            ArmatureEdit.Leave();
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Mode = MixerMode.None;
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerArmatureFixture");
        }

        private static Vector3[] Baked(SkinnedMeshRenderer r)
        {
            var baked = new Mesh();
            r.BakeMesh(baked, true);
            var vertices = baked.vertices;
            var m = r.transform.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = m.MultiplyPoint3x4(vertices[i]);
            Object.DestroyImmediate(baked);
            return vertices;
        }

        private static float MaxDistance(Vector3[] a, Vector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }

        [Test]
        public void MovingABoneInRestPoseModeKeepsTheSkinStill()
        {
            var target = MixerSession.Edit(renderer);
            MixerSceneTool.Mode = MixerMode.Armature;
            ArmatureEdit.Active = true;
            var before = Baked(renderer);
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(upper, "Move bone");
            upper.localPosition += new Vector3(0.4f, 0.2f, 0f);
            upper.localRotation = Quaternion.Euler(0, 0, 30f);
            // Without rest-pose handling the skin would follow the bone.
            target.RefreshWorld();
            Assert.That(MaxDistance(before, Baked(renderer)), Is.GreaterThan(0.1f), "the move poses the mesh at first");
            int changed = ArmatureEdit.Recompute(target, joinCurrentGroup: false);
            Assert.That(changed, Is.EqualTo(1), "only the moved bone's bind pose changes");
            Assert.That(MaxDistance(before, Baked(renderer)), Is.LessThan(1e-4f), "with the new bind pose the skin is back where it was");
            Assert.That(target.Document.BindPosesEdited, Is.True);
            var editedPose = target.Document.BindPoses[1];
            Undo.PerformUndo();
            Assert.That(target.Document.BindPoses[1], Is.Not.EqualTo(editedPose), "undo restores the bind pose");
            Undo.PerformRedo();
            Assert.That(target.Document.BindPoses[1], Is.EqualTo(editedPose));
            var asset = MixerSession.Commit(target);
            Assert.That(asset.bindposes[1], Is.EqualTo(editedPose), "the committed mesh carries the new bind pose");
        }

        [Test]
        public void ResetToBindPoseAndLeafBones()
        {
            var target = MixerSession.Edit(renderer);
            var rest = Baked(renderer);
            upper.localPosition += new Vector3(0.3f, 0f, 0f);
            upper.localRotation = Quaternion.Euler(0, 0, 45f);
            target.RefreshWorld();
            Assert.That(MaxDistance(rest, Baked(renderer)), Is.GreaterThan(0.1f));
            int moved = ArmatureEdit.ResetToBindPose(target);
            Assert.That(moved, Is.GreaterThan(0));
            Assert.That(MaxDistance(rest, Baked(renderer)), Is.LessThan(1e-4f), "the bones are back in the bind pose");
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f));

            var tip = ArmatureEdit.AddLeafBone(target, upper, "Tip");
            Assert.That(tip, Is.Not.Null);
            Assert.That(tip.parent, Is.SameAs(upper));
            Assert.That(renderer.bones.Length, Is.EqualTo(3));
            Assert.That(target.Document.BindPoses.Length, Is.EqualTo(3));
            Assert.That(renderer.sharedMesh.bindposes.Length, Is.EqualTo(3), "the working mesh got the bind pose");
            Assert.That(MaxDistance(rest, Baked(renderer)), Is.LessThan(1e-4f), "a new unweighted bone changes nothing");
            // Weights can go onto the new bone and it deforms with it.
            int probe = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], new Vector3(0.3f, 2f, 0f))).First();
            target.Document.SetBoneWeight(probe, 2, 1f, normalize: true);
            target.FlushWeights(force: true);
            var withTip = Baked(renderer);
            tip.localPosition += Vector3.right * 0.5f;
            target.RefreshWorld();
            Assert.That(Vector3.Distance(Baked(renderer)[probe], withTip[probe]), Is.GreaterThan(0.3f), "the tip bone now moves its vertex");
            var asset = MixerSession.Commit(target);
            Assert.That(asset.bindposes.Length, Is.EqualTo(3));
            Assert.That(asset.GetBonesPerVertex()[probe], Is.GreaterThanOrEqualTo(1));
        }
    }
}
