using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// Joining a skinned cylinder and a static cube: one skinned mesh, bones united, shapes kept with their in-betweens,
    /// sources hidden, undoable; a part whose bind poses do not fit its bones stays where it is; a mesh being edited in
    /// Mixer joins as a commit would make it.
    /// </summary>
    public sealed class MixerJoinTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer skinned;
        private MeshRenderer cube;
        private Transform lower, upper;
        private Material material;
        private HashSet<EditTarget> targetsBefore;
        private Object[] selectionBefore;
        private bool symmetryBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            // A join selects its result: the user's selection comes back after the test.
            selectionBefore = Selection.objects;
            symmetryBefore = MixerSceneTool.Symmetry;
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerJoinFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("Upper").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            skinned = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = MixerCoreTestsFixtures.Cylinder(12, 6, 0.3f, 2f);
            var half = new Vector3[mesh.vertexCount];
            var full = new Vector3[mesh.vertexCount];
            for (int v = 0; v < full.Length; v++) if (mesh.vertices[v].y < 0.5f) { full[v] = new Vector3(0f, -0.2f, 0f); half[v] = new Vector3(0f, -0.05f, 0f); }
            mesh.AddBlendShapeFrame("Steps", 50f, half, null, null);
            mesh.AddBlendShapeFrame("Steps", 100f, full, null, null);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/JoinFixture.asset");
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { lower, upper };
            skinned.rootBone = lower;
            material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, "Assets/Orbiters/MixerTests/JoinMaterial.mat");
            skinned.sharedMaterial = material;
            var cubeObject = new GameObject("Hat");
            cubeObject.transform.SetParent(root.transform, false);
            cubeObject.transform.localPosition = new Vector3(0, 2.5f, 0);
            cubeObject.transform.localScale = Vector3.one * 0.4f;
            cubeObject.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            cube = cubeObject.AddComponent<MeshRenderer>();
            cube.sharedMaterial = material;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            foreach (var go in scene.GetRootGameObjects()) Object.DestroyImmediate(go);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerJoinFixture");
            Selection.objects = selectionBefore;
            MixerSceneTool.Symmetry = symmetryBefore;
        }

        private static Vector3[] BakedWorld(SkinnedMeshRenderer r)
        {
            var baked = new Mesh();
            r.BakeMesh(baked, true);
            var vertices = baked.vertices;
            Object.DestroyImmediate(baked);
            var m = r.transform.localToWorldMatrix;
            return vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
        }

        [Test]
        public void JoinMakesOneSkinnedMeshThatStaysWhereThePartsWere()
        {
            var cubeWorld = cube.bounds.center;
            var result = MeshJoin.Join(new Renderer[] { skinned, cube }, new MeshJoin.Options { MergeSameMaterials = true, HideSources = true });
            Assert.That(result.Vertices, Is.EqualTo(skinned.sharedMesh.vertexCount + 24));
            Assert.That(result.Bones, Is.EqualTo(3), "the cylinder's two bones plus the cube's transform");
            Assert.That(result.Slots, Is.EqualTo(1), "one material shared, one slot");
            Assert.That(result.Shapes, Is.EqualTo(2));
            Assert.That(result.Mesh.GetBlendShapeName(0), Is.EqualTo("Wide"));
            Assert.That(AssetDatabase.Contains(result.Mesh), Is.True);
            Assert.That(skinned.gameObject.activeSelf, Is.False, "sources are hidden");
            Assert.That(cube.gameObject.activeSelf, Is.False);
            // The cube's vertices, skinned to the cube's transform, are still around the cube's old centre.
            var cubeVertices = BakedWorld(result.Renderer).Skip(skinned.sharedMesh.vertexCount).ToList();
            var centre = cubeVertices.Aggregate(Vector3.zero, (a, b) => a + b) / cubeVertices.Count;
            Assert.That(Vector3.Distance(centre, cubeWorld), Is.LessThan(1e-3f), "the hat stays on the head");
            Undo.PerformUndo();
            Assert.That(skinned.gameObject.activeSelf, Is.True, "undo brings the sources back");
            Assert.That(result.Renderer == null, Is.True, "and removes the joined object");
        }

        [Test]
        public void JoinKeepsInBetweenFramesAndUvChannels()
        {
            var result = MeshJoin.Join(new Renderer[] { skinned, cube }, new MeshJoin.Options());
            int steps = result.Mesh.GetBlendShapeIndex("Steps");
            Assert.That(result.Mesh.GetBlendShapeFrameCount(steps), Is.EqualTo(2), "the in-between frame is kept");
            Assert.That(result.Mesh.GetBlendShapeFrameWeight(steps, 0), Is.EqualTo(50f));
            Assert.That(result.Mesh.GetBlendShapeFrameWeight(steps, 1), Is.EqualTo(100f));
            var half = new Vector3[result.Mesh.vertexCount];
            result.Mesh.GetBlendShapeFrameVertices(steps, 0, half, null, null);
            int bottom = Enumerable.Range(0, skinned.sharedMesh.vertexCount).First(v => skinned.sharedMesh.vertices[v].y < 0.1f);
            Assert.That(Vector3.Distance(half[bottom], new Vector3(0f, -0.05f, 0f)), Is.LessThan(1e-5f));
            Assert.That(result.Mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0), Is.True);
            var bounds = result.Renderer.localBounds;
            Assert.That(bounds.size.y, Is.GreaterThan(2.5f), "the bounds cover the joined parts in the root bone's space");
        }

        [Test]
        public void APartWhoseBindPosesDoNotFitItsBonesStaysWhereItIs()
        {
            // A skinned part whose renderer has three bones for the mesh's two bind poses: Unity cannot skin it, so it
            // shows at its transform. (The mesh itself is valid: its bone indices are within its bind poses.)
            var cubeMesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            cubeMesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            cubeMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1f }, cubeMesh.vertexCount).ToArray();
            AssetDatabase.CreateAsset(cubeMesh, "Assets/Orbiters/MixerTests/Odd.asset");
            var odd = new GameObject("Odd");
            odd.transform.SetParent(root.transform, false);
            odd.transform.localPosition = new Vector3(1f, 0.5f, 0f);
            var oddRenderer = odd.AddComponent<SkinnedMeshRenderer>();
            oddRenderer.sharedMesh = cubeMesh;
            oddRenderer.bones = new[] { lower, upper, root.transform };
            oddRenderer.sharedMaterial = material;
            var expected = cubeMesh.vertices.Select(v => odd.transform.TransformPoint(v)).ToArray();
            var result = MeshJoin.Join(new Renderer[] { skinned, oddRenderer }, new MeshJoin.Options());
            var joined = BakedWorld(result.Renderer).Skip(skinned.sharedMesh.vertexCount).ToArray();
            float worst = 0f;
            for (int i = 0; i < expected.Length; i++) worst = Mathf.Max(worst, Vector3.Distance(joined[i], expected[i]));
            Assert.That(worst, Is.LessThan(1e-3f), "the odd part rides its own transform instead of the first bone");
        }

        [Test]
        public void AMeshBeingEditedJoinsAsACommitWouldMakeIt()
        {
            MixerSceneTool.Symmetry = false;
            var target = MixerSession.Edit(skinned);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.6f, 2f, 0f), new Vector3(0.6f, 1.95f, 0f), 2, 0.3f, 1f, Vector3.forward);
            var basePositions = (Vector3[])target.Document.Positions.Clone();
            var wide = (Vector3[])target.Document.Shape("Wide").Deltas.Clone();
            var result = MeshJoin.Join(new Renderer[] { skinned, cube }, new MeshJoin.Options { HideSources = false });
            var joined = result.Mesh.vertices;
            float worst = 0f;
            for (int v = 0; v < basePositions.Length; v++) worst = Mathf.Max(worst, Vector3.Distance(joined[v], basePositions[v]));
            Assert.That(worst, Is.LessThan(1e-5f), "the base, not the active shape baked into it");
            var deltas = new Vector3[result.Mesh.vertexCount];
            int index = result.Mesh.GetBlendShapeIndex("Wide");
            result.Mesh.GetBlendShapeFrameVertices(index, result.Mesh.GetBlendShapeFrameCount(index) - 1, deltas, null, null);
            worst = 0f;
            for (int v = 0; v < wide.Length; v++) worst = Mathf.Max(worst, Vector3.Distance(deltas[v], wide[v]));
            Assert.That(worst, Is.LessThan(1e-5f), "the edited shape as edited");
        }
    }
}
