using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>Weight painting on the skinned cylinder: normalization, a vertex's last bone, undo, commit, groups and their sidecar, mirror names.</summary>
    public sealed class MixerWeightTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;

        // Sessions open before the test (another tool's, the user's): left alone, with the mode and focus they had.
        private HashSet<EditTarget> targetsBefore;
        private MixerMode modeBefore;
        private EditTarget focusBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            modeBefore = MixerSceneTool.Mode;
            focusBefore = MixerSceneTool.FocusTarget;
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
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Mode = MixerMode.None;
            MixerSceneTool.FocusTarget = focusBefore;
            MixerSceneTool.Mode = modeBefore;
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
        public void WeightPaintShowsTheHeaviestBoneOnEntry()
        {
            var target = MixerSession.Edit(renderer);
            MixerSceneTool.SetChannel(WeightChannel.None);
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.Mode = MixerMode.Weights;
            int heaviest = Toolkit.Editor.XRay.XRayWeightPaintService.MostWeightedBone(renderer);
            Assert.That(heaviest, Is.GreaterThanOrEqualTo(0));
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(heaviest), "a bone is picked when none was");
            Assert.That(Toolkit.Editor.XRay.XRayWeightPaintService.ActiveRenderers, Has.Member(renderer), "its weights show on the mesh");
            MixerSceneTool.Mode = MixerMode.None;
            Assert.That(Toolkit.Editor.XRay.XRayWeightPaintService.ActiveRenderers, Has.No.Member(renderer), "leaving the mode hides them");
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
        public void AVertexNeverLosesItsLastBone()
        {
            var target = MixerSession.Edit(renderer);
            var doc = target.Document;
            // The top ring is on bone 1 only: Replace with 0, Normalize off, would leave it no bone.
            var top = new Vector3(0.3f, 1.9f, 0f);
            int probe = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], top)).First();
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Replace, WeightChannel.OfBone(1), top, top + Vector3.forward * 0.2f, 6, 0.2f, 1f, 0f, normalize: false);
            Assert.That(doc.EnsureInfluences()[probe].Count, Is.EqualTo(1));
            Assert.That(doc.BoneWeight(probe, 1), Is.GreaterThan(0f), "its last bone stays");
            // Blender sending a vertex without groups: the same.
            doc.SetInfluences(probe, new MeshDocument.Influence[0]);
            Assert.That(doc.BoneWeight(probe, 1), Is.GreaterThan(0f));
            // Each bone of a vertex painted away in turn: the last one stays, not the first bone of the mesh.
            int middle = Enumerable.Range(0, mesh.vertexCount).First(v => Mathf.Abs(mesh.vertices[v].y - 0.75f) < 1e-4f);
            Assert.That(doc.BoneWeight(middle, 0), Is.EqualTo(0.75f).Within(1e-4f));
            doc.SetBoneWeight(middle, 0, 0f, normalize: false);
            doc.SetBoneWeight(middle, 1, 0f, normalize: false);
            Assert.That(doc.BoneWeight(middle, 0), Is.EqualTo(0f));
            Assert.That(doc.BoneWeight(middle, 1), Is.EqualTo(0.25f).Within(1e-4f), "as it was");

            var asset = MixerSession.Commit(target);
            var counts = asset.GetBonesPerVertex();
            var weights = asset.GetAllBoneWeights();
            foreach (int v in new[] { probe, middle })
            {
                int start = 0;
                for (int i = 0; i < v; i++) start += counts[i];
                Assert.That(counts[v], Is.EqualTo(1));
                Assert.That(weights[start].boneIndex, Is.EqualTo(1), "the commit skins it to that bone, not to bone 0");
                Assert.That(weights[start].weight, Is.EqualTo(1f).Within(1e-6f), "fully");
            }
        }

        // With symmetry, a dab on the middle line reaches each vertex straight and through its mirror: it is painted once,
        // with the stronger of the two, not twice (two lerps by a give 1 - (1 - a)^2).
        [Test]
        public void ASymmetricDabOnTheMiddleLinePaintsEachVertexOnce()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.AddGroup(target, "Belt");
            MixerSceneTool.Symmetry = true;
            try
            {
                var front = new Vector3(0f, 1f, 0.3f);
                // Draw moves toward the value by strength x falloff x 0.5: one dab is at most 0.5 at strength 1.
                MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Draw, WeightChannel.OfGroup("Belt"), front, front, 0, 0.25f, 1f, 1f, normalize: false);
            }
            finally
            {
                MixerSceneTool.Symmetry = false;
            }
            var group = target.Document.Group("Belt");
            var positions = target.Document.Positions;
            Assert.That(group.Weights.Keys.Any(v => positions[v].x > 0.02f) && group.Weights.Keys.Any(v => positions[v].x < -0.02f), Is.True, "the dab straddles the middle line");
            Assert.That(group.Weights.Values.Max(), Is.LessThanOrEqualTo(0.5f + 1e-4f), "no vertex painted twice");
            foreach (var pair in group.Weights)
            {
                var p = positions[pair.Key];
                int mirror = Enumerable.Range(0, positions.Length).First(v => (positions[v] - new Vector3(-p.x, p.y, p.z)).sqrMagnitude < 1e-8f);
                Assert.That(group.Weights.TryGetValue(mirror, out float other) ? other : 0f, Is.EqualTo(pair.Value).Within(1e-5f), "both sides alike");
            }
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

        // Another skinned cylinder of the fixture on the given bones (of the body's rig, or its own).
        private SkinnedMeshRenderer Skinned(string name, Transform[] bones)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var skinned = go.AddComponent<SkinnedMeshRenderer>();
            var m = MixerCoreTestsFixtures.Cylinder(16, 8, 0.35f, 2f);
            AssetDatabase.CreateAsset(m, "Assets/Orbiters/MixerTests/" + name + "Fixture.asset");
            skinned.sharedMesh = m;
            skinned.bones = bones;
            skinned.rootBone = bones[0];
            return skinned;
        }

        [Test]
        public void TheWeightChannelBelongsToItsMesh()
        {
            var lower = root.transform.Find("Leg_L");
            var upper = root.transform.Find("Leg_R");
            var hand = new GameObject("Hand").transform;
            hand.SetParent(root.transform, false);
            // The shirt lists the same two bones the other way round; the glove has Leg_L and a Hand the body has not.
            var shirt = MixerSession.Edit(Skinned("Shirt", new[] { upper, lower }));
            var glove = MixerSession.Edit(Skinned("Glove", new[] { lower, hand }));
            var body = MixerSession.Edit(renderer);
            MixerSceneTool.FocusTarget = body;
            MixerSceneTool.Mode = MixerMode.Weights;
            MixerSceneTool.SetChannel(body, WeightChannel.OfBone(1));
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(1), "the body paints Leg_R");
            Assert.That(MixerSceneTool.ChannelOn(shirt).Bone, Is.EqualTo(0), "the shirt reads the same bone at its own index");
            Assert.That(MixerSceneTool.ChannelOn(glove).IsNone, Is.True, "the glove's index 1 is its Hand, not Leg_R");

            var on = MixerSceneTool.WeightStrokeChannel(shirt, out string refusal);
            Assert.That(refusal, Is.Null);
            Assert.That(on.Bone, Is.EqualTo(0), "a stroke on the shirt paints its Leg_R");
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(shirt), "the stroked mesh becomes the focus");
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(0));

            on = MixerSceneTool.WeightStrokeChannel(glove, out refusal);
            Assert.That(refusal, Is.Not.Null, "the glove's stroke is refused instead of painting its Hand");
            Assert.That(on.IsNone, Is.True);
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(glove), "the glove's own weights show");
            var shown = MixerSceneTool.Channel;
            Assert.That(shown.IsNone, Is.False, "with its heaviest bone");
            on = MixerSceneTool.WeightStrokeChannel(glove, out refusal);
            Assert.That(refusal, Is.Null, "the next stroke paints what the glove shows");
            Assert.That(on.Bone, Is.EqualTo(shown.Bone));

            // A vertex group goes along by name, only to a mesh that has it.
            MixerSession.AddGroup(body, "Belt");
            MixerSceneTool.SetChannel(body, WeightChannel.OfGroup("Belt"));
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(body), "picking a channel of a mesh focuses it");
            Assert.That(MixerSceneTool.ChannelOn(shirt).IsNone, Is.True, "the shirt has no Belt group");
            MixerSession.AddGroup(shirt, "Belt");
            Assert.That(MixerSceneTool.ChannelOn(shirt).Group, Is.EqualTo("Belt"));
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
