using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>Joining a skinned cylinder and a static cube: one skinned mesh, bones united, shapes kept, sources hidden, undoable.</summary>
    public sealed class MixerJoinTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer skinned;
        private MeshRenderer cube;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerJoinFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            var lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            var upper = new GameObject("Upper").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            skinned = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = MixerCoreTestsFixtures.Cylinder(12, 6, 0.3f, 2f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/JoinFixture.asset");
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { lower, upper };
            skinned.rootBone = lower;
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, "Assets/Orbiters/MixerTests/JoinMaterial.mat");
            skinned.sharedMaterial = material;
            var cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeObject.name = "Hat";
            cubeObject.transform.SetParent(root.transform, false);
            cubeObject.transform.localPosition = new Vector3(0, 2.5f, 0);
            cubeObject.transform.localScale = Vector3.one * 0.4f;
            cube = cubeObject.GetComponent<MeshRenderer>();
            cube.sharedMaterial = material;
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerJoinFixture");
        }

        [Test]
        public void JoinMakesOneSkinnedMeshThatStaysWhereThePartsWere()
        {
            var cubeWorld = cube.bounds.center;
            var result = MeshJoin.Join(new Renderer[] { skinned, cube }, new MeshJoin.Options { MergeSameMaterials = true, HideSources = true });
            Assert.That(result.Vertices, Is.EqualTo(skinned.sharedMesh.vertexCount + 24));
            Assert.That(result.Bones, Is.EqualTo(3), "the cylinder's two bones plus the cube's transform");
            Assert.That(result.Slots, Is.EqualTo(1), "one material shared, one slot");
            Assert.That(result.Shapes, Is.EqualTo(1));
            Assert.That(result.Mesh.GetBlendShapeName(0), Is.EqualTo("Wide"));
            Assert.That(AssetDatabase.Contains(result.Mesh), Is.True);
            Assert.That(skinned.gameObject.activeSelf, Is.False, "sources are hidden");
            Assert.That(cube.gameObject.activeSelf, Is.False);
            // The cube's vertices, skinned to the cube's transform, are still around the cube's old centre.
            var baked = new Mesh();
            result.Renderer.BakeMesh(baked, true);
            var vertices = baked.vertices;
            var m = result.Renderer.transform.localToWorldMatrix;
            var cubeVertices = vertices.Skip(skinned.sharedMesh.vertexCount).Select(v => m.MultiplyPoint3x4(v)).ToList();
            var centre = cubeVertices.Aggregate(Vector3.zero, (a, b) => a + b) / cubeVertices.Count;
            Assert.That(Vector3.Distance(centre, cubeWorld), Is.LessThan(1e-3f), "the hat stays on the head");
            Object.DestroyImmediate(baked);
            Undo.PerformUndo();
            Assert.That(skinned.gameObject.activeSelf, Is.True, "undo brings the sources back");
            Assert.That(result.Renderer == null, Is.True, "and removes the joined object");
        }
    }
}
