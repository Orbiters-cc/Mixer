using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>Weight painting on the skinned cylinder: normalization, undo, commit, groups and their sidecar, mirror names.</summary>
    public sealed class MixerWeightTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerWeightFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            var lower = new GameObject("Leg_L").transform; lower.SetParent(root.transform, false);
            var upper = new GameObject("Leg_R").transform; upper.SetParent(root.transform, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            mesh = MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/WeightFixture.asset");
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
            MixerSceneTool.Symmetry = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) MixerSession.Revert(t);
            MixerHistory.Clear();
            MixerSceneTool.Mode = MixerMode.None;
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerWeightFixture");
        }

        [Test]
        public void DrawingABoneKeepsTheSumAtOneAndUndoes()
        {
            var target = MixerSession.Edit(renderer);
            // The bottom ring belongs to bone 0 only; paint bone 1 there.
            var bottom = new Vector3(0.3f, 0.1f, 0f);
            int probe = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], bottom)).First();
            Assert.That(target.Document.BoneWeight(probe, 1), Is.EqualTo(0f));
            var edit = MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Draw, WeightChannel.OfBone(1), bottom, bottom + Vector3.forward * 0.3f, 8, 0.25f, 1f, 1f, normalize: true);
            Assert.That(edit, Is.Not.Null);
            float w1 = target.Document.BoneWeight(probe, 1), w0 = target.Document.BoneWeight(probe, 0);
            Assert.That(w1, Is.GreaterThan(0.2f), "bone 1 gained weight");
            Assert.That(w0 + w1, Is.EqualTo(1f).Within(1e-4f), "normalized");
            Undo.PerformUndo();
            Assert.That(target.Document.BoneWeight(probe, 1), Is.EqualTo(0f), "undo restores the influences");
            Assert.That(target.Document.BoneWeight(probe, 0), Is.EqualTo(1f).Within(1e-5f));
            Undo.PerformRedo();
            Assert.That(target.Document.BoneWeight(probe, 1), Is.EqualTo(w1).Within(1e-5f));
            target.FlushWeights(force: true);
            var uploaded = renderer.sharedMesh.GetAllBoneWeights();
            Assert.That(uploaded.Length, Is.GreaterThan(mesh.vertexCount), "the working mesh now holds two influences on painted vertices");
            var asset = MixerSession.Commit(target);
            var perVertex = asset.GetBonesPerVertex();
            Assert.That(perVertex[probe], Is.EqualTo(2), "the committed mesh keeps both bones on the painted vertex");
        }

        [Test]
        public void ReplaceWithoutNormalizeAndBlurBehave()
        {
            var target = MixerSession.Edit(renderer);
            var top = new Vector3(0.3f, 1.9f, 0f);
            int probe = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], top)).First();
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Replace, WeightChannel.OfBone(1), top, top + Vector3.forward * 0.2f, 6, 0.2f, 1f, 0.5f, normalize: false);
            Assert.That(target.Document.BoneWeight(probe, 1), Is.EqualTo(0.5f).Within(0.05f), "replace sets the value");
            Assert.That(target.Document.BoneWeight(probe, 0), Is.EqualTo(0f).Within(1e-4f), "without normalization the other bone keeps its (zero) weight");
            // A sharp edge: paint bone 1 to 1 on a small spot, then blur around it; the spot's neighbours gain some.
            var side = new Vector3(0f, 1f, 0.3f);
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Replace, WeightChannel.OfBone(0), side, side, 1, 0.05f, 1f, 1f, normalize: false);
            int spot = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], side)).First();
            float before = target.Document.BoneWeight(spot, 0);
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Blur, WeightChannel.OfBone(0), side, side, 1, 0.2f, 1f, 1f, normalize: false);
            Assert.That(target.Document.BoneWeight(spot, 0), Is.LessThan(before), "blur spreads the spot");
        }

        [Test]
        public void GroupsArePaintedSavedBesideTheMeshAndLoadedBack()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.AddGroup(target, "Belt");
            var mid = new Vector3(0.3f, 1f, 0f);
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Draw, WeightChannel.OfGroup("Belt"), mid, mid + Vector3.forward * 0.3f, 6, 0.25f, 1f, 1f, normalize: true);
            var group = target.Document.Group("Belt");
            Assert.That(group.Weights.Count, Is.GreaterThan(5));
            Undo.PerformUndo();
            Assert.That(target.Document.Group("Belt").Weights.Count, Is.EqualTo(0), "group painting undoes");
            Undo.PerformRedo();
            int painted = target.Document.Group("Belt").Weights.Count;
            // Something must change on the mesh too for a commit to happen.
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), mid, mid + Vector3.forward * 0.1f, 2, 0.1f, 1f, Vector3.forward);
            var asset = MixerSession.Commit(target);
            string sidecar = MixerSidecar.PathFor(AssetDatabase.GetAssetPath(asset));
            Assert.That(System.IO.File.Exists(sidecar), Is.True, "the groups are saved beside the asset");
            var again = MixerSession.Edit(renderer);
            Assert.That(again.Document.Group("Belt"), Is.Not.Null, "editing the committed mesh loads its groups");
            Assert.That(again.Document.Group("Belt").Weights.Count, Is.EqualTo(painted));
            MixerSession.RenameGroup(again, "Belt", "Sash");
            Assert.That(again.Document.Group("Sash"), Is.Not.Null);
            MixerSession.RemoveGroup(again, "Sash");
            Assert.That(again.Document.Groups, Is.Empty);
            Undo.PerformUndo();
            Assert.That(again.Document.Group("Sash"), Is.Not.Null, "removing a group undoes");
        }

        [Test]
        public void MirrorBoneNamesFollowTheUsualConventions()
        {
            Assert.That(WeightStroke.MirrorName("Leg_L"), Is.EqualTo("Leg_R"));
            Assert.That(WeightStroke.MirrorName("Leg_R"), Is.EqualTo("Leg_L"));
            Assert.That(WeightStroke.MirrorName("Hand.L"), Is.EqualTo("Hand.R"));
            Assert.That(WeightStroke.MirrorName("LeftArm"), Is.EqualTo("RightArm"));
            Assert.That(WeightStroke.MirrorName("Right Hand"), Is.EqualTo("Left Hand"));
            Assert.That(WeightStroke.MirrorName("Spine"), Is.EqualTo("Spine"));
            var target = MixerSession.Edit(renderer);
            Assert.That(WeightStroke.MirrorBone(target, 0), Is.EqualTo(1), "Leg_L mirrors to Leg_R");
            Assert.That(WeightStroke.MirrorBone(target, 1), Is.EqualTo(0));
        }
    }

    internal static class MixerCoreTestsFixtures
    {
        public static Mesh Cylinder(int segments, int rings, float radius, float height)
        {
            var vertices = new List<Vector3>();
            var weights = new List<BoneWeight>();
            var triangles = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float y = height * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float a = 2f * Mathf.PI * s / segments;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius));
                    float t = Mathf.Clamp01((y - 0.5f) / 1f);
                    weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1f - t, boneIndex1 = 1, weight1 = t });
                }
            }
            int w = segments + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * w + s, b = a + 1, c = a + w, d = c + 1;
                    triangles.AddRange(new[] { a, b, c, b, d, c });
                }
            var m = new Mesh { name = "Fixture", vertices = vertices.ToArray(), triangles = triangles.ToArray() };
            m.boneWeights = weights.ToArray();
            m.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(0, -1f, 0)) };
            m.RecalculateNormals();
            m.RecalculateBounds();
            var deltas = new Vector3[vertices.Count];
            for (int i = 0; i < deltas.Length; i++) if (vertices[i].y > height - 1e-4f) deltas[i] = new Vector3(vertices[i].x, 0, vertices[i].z);
            m.AddBlendShapeFrame("Wide", 100f, deltas, null, null);
            return m;
        }
    }
}
