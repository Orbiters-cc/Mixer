using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>The editing loop on a skinned cylinder in a preview scene: strokes, Ctrl+Z through Unity, layers, commit, autosave.</summary>
    public sealed class MixerCoreTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;
        private string assetFolder;

        // Sessions open before the test (another tool's, the user's): left alone.
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            var lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            var upper = new GameObject("Upper").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            mesh = Cylinder(32, 24, 0.3f, 2f);
            assetFolder = "Assets/Orbiters/MixerTests";
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(assetFolder);
            AssetDatabase.CreateAsset(mesh, assetFolder + "/Fixture.asset");
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
            MixerSceneTool.Symmetry = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerFixture");
        }

        // A skinned cylinder along +Y, lower half on bone 0 and upper half on bone 1 (bones at y = 0 and y = 1), with one
        // blendshape ("Wide") that pushes the top ring out.
        private static Mesh Cylinder(int segments, int rings, float radius, float height)
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

        private static Vector3[] Snapshot(EditTarget target) => (Vector3[])target.Document.Positions.Clone();

        private static float MaxDistance(Vector3[] a, Vector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }

        [Test]
        public void EditingShowsAWorkingMeshAndStrokesMoveVertices()
        {
            var target = MixerSession.Edit(renderer);
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(mesh), "the renderer shows a working copy");
            Assert.That(renderer.sharedMesh.name, Does.Contain("Mixer working"));
            Assert.That(MixerMeshGuard.OriginalOf(renderer.sharedMesh), Is.SameAs(mesh));
            var before = Snapshot(target);
            var edit = MixerSceneTool.StrokeBetween(target, Brushes.Named("Grab"), new Vector3(0.3f, 1.5f, 0f), new Vector3(0.6f, 1.5f, 0f), 6, 0.25f, 1f, Vector3.forward);
            Assert.That(edit, Is.Not.Null);
            Assert.That(edit.Indices.Length, Is.GreaterThan(5));
            target.Flush();
            Assert.That(MaxDistance(before, target.Document.Positions), Is.GreaterThan(0.1f), "the grab pulled the side out");
            Assert.That(renderer.sharedMesh.vertices[edit.Indices[0]], Is.EqualTo(target.Document.Positions[edit.Indices[0]]), "the working mesh shows the document");
            Assert.That(mesh.vertices[edit.Indices[0]], Is.EqualTo(before[edit.Indices[0]]), "the original is untouched");
        }

        [Test]
        public void UnitysUndoWalksMixersStrokes()
        {
            var target = MixerSession.Edit(renderer);
            int countBefore = MixerHistory.Count, cursorBefore = MixerHistory.Cursor;
            var s0 = Snapshot(target);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            var s1 = Snapshot(target);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(-0.3f, 1.5f, 0f), new Vector3(-0.3f, 1.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            var s2 = Snapshot(target);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Grab"), new Vector3(0f, 1f, 0.3f), new Vector3(0f, 1f, 0.6f), 4, 0.25f, 1f, Vector3.right);
            var s3 = Snapshot(target);
            Assert.That(MaxDistance(s0, s1), Is.GreaterThan(1e-4f));
            Assert.That(MaxDistance(s1, s2), Is.GreaterThan(1e-4f));
            Assert.That(MaxDistance(s2, s3), Is.GreaterThan(1e-4f));
            Assert.That(MixerHistory.Count, Is.EqualTo(countBefore + 3));
            Assert.That(Undo.GetCurrentGroupName(), Does.Contain("Mixer:"));

            Undo.PerformUndo();
            Assert.That(MaxDistance(Snapshot(target), s2), Is.LessThan(1e-6f), "one Ctrl+Z undid the last stroke");
            Undo.PerformUndo();
            Assert.That(MaxDistance(Snapshot(target), s1), Is.LessThan(1e-6f), "a second Ctrl+Z undid the second stroke");
            Undo.PerformRedo();
            Assert.That(MaxDistance(Snapshot(target), s2), Is.LessThan(1e-6f), "Ctrl+Y redid it");
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(MaxDistance(Snapshot(target), s0), Is.LessThan(1e-6f), "back at the start");
            Assert.That(MixerHistory.Cursor, Is.EqualTo(cursorBefore));
            Undo.PerformRedo(); Undo.PerformRedo(); Undo.PerformRedo();
            Assert.That(MaxDistance(Snapshot(target), s3), Is.LessThan(1e-6f), "redo all the way");
        }

        [Test]
        public void CommitSavesANativeMeshAssetAndKeepsTheOriginal()
        {
            var target = MixerSession.Edit(renderer);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            var edited = Snapshot(target);
            int imports = 0;
            MixerTestHooks.ImportCounter = () => imports++;
            var asset = MixerSession.Commit(target);
            Assert.That(asset, Is.Not.Null);
            Assert.That(AssetDatabase.Contains(asset), Is.True);
            Assert.That(AssetDatabase.GetAssetPath(asset), Does.EndWith(".asset"));
            Assert.That(renderer.sharedMesh, Is.SameAs(asset), "the renderer shows the committed asset");
            Assert.That(MaxDistance(asset.vertices, edited), Is.LessThan(1e-6f));
            Assert.That(asset.blendShapeCount, Is.EqualTo(1), "the original shape is kept");
            Assert.That(mesh.vertices[0], Is.EqualTo(new Vector3(0.3f, 0f, 0f)), "the original asset is untouched");
            Assert.That(MixerSession.Targets, Has.No.Member(target), "the session is closed");
            Assert.That(asset.bindposes.Length, Is.EqualTo(2));
        }

        [Test]
        public void ShapeLayersBecomeBlendshapesOnCommit()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Puff", createIfMissing: true);
            Assert.That(target.Document.Layer, Is.EqualTo("Puff"));
            var baseBefore = Snapshot(target);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 1f, 0f), new Vector3(0.3f, 1.1f, 0f), 3, 0.3f, 1f, Vector3.forward);
            Assert.That(MaxDistance(baseBefore, target.Document.Positions), Is.LessThan(1e-7f), "a shape layer leaves the base alone");
            var layer = target.Document.Shape("Puff");
            Assert.That(layer.Deltas.Count(d => d.sqrMagnitude > 0f), Is.GreaterThan(5));
            // Editing an existing shape rebuilds the list; the original one keeps its deltas.
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 2f, 0f), new Vector3(0.3f, 1.95f, 0f), 2, 0.2f, 1f, Vector3.forward);
            var asset = MixerSession.Commit(target);
            Assert.That(asset.blendShapeCount, Is.EqualTo(2));
            Assert.That(asset.GetBlendShapeName(0), Is.EqualTo("Wide"));
            Assert.That(asset.GetBlendShapeName(1), Is.EqualTo("Puff"));
            var deltas = new Vector3[asset.vertexCount];
            asset.GetBlendShapeFrameVertices(1, 0, deltas, null, null);
            Assert.That(deltas.Count(d => d.sqrMagnitude > 0f), Is.GreaterThan(5));
            asset.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
            Assert.That(deltas.Count(d => d.sqrMagnitude > 0f), Is.GreaterThan(30), "Wide still pushes the top ring out");
        }

        [Test]
        public void AutosaveRoundTripsEdits()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Puff", createIfMissing: true);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 1f, 0f), new Vector3(0.3f, 1.1f, 0f), 3, 0.3f, 1f, Vector3.forward);
            MixerSession.SetLayer(target, null, false);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Grab"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), 3, 0.25f, 1f, Vector3.forward);
            var basePositions = Snapshot(target);
            var puff = (Vector3[])target.Document.Shape("Puff").Deltas.Clone();
            MixerAutosave.Write(target);
            var deadline = System.DateTime.UtcNow.AddSeconds(5);
            while (!MixerAutosave.Exists(target.Id) && System.DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(20);
            System.Threading.Thread.Sleep(100);
            Assert.That(MixerAutosave.Exists(target.Id), Is.True);

            var fresh = new EditTarget(renderer, mesh, target.Working);
            Assert.That(MixerAutosave.Restore(fresh), Is.True);
            Assert.That(MaxDistance(fresh.Document.Positions, basePositions), Is.LessThan(1e-7f));
            Assert.That(MaxDistance(fresh.Document.Shape("Puff").Deltas, puff), Is.LessThan(1e-7f));
            MixerAutosave.Delete(target.Id);
        }

        [Test]
        public void RevertPutsTheOriginalBack()
        {
            var target = MixerSession.Edit(renderer);
            var working = renderer.sharedMesh;
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            MixerSession.Revert(target);
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(working == null, Is.True, "the working mesh is destroyed");
            Assert.That(MixerSession.Targets, Has.No.Member(target), "the session is closed");
        }

        [Test]
        public void UndoPutsTheNormalsBackToo()
        {
            var target = MixerSession.Edit(renderer);
            var before = (Vector3[])target.Document.Normals.Clone();
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            Assert.That(MaxDistance(before, target.Document.Normals), Is.GreaterThan(1e-3f), "the stroke changed the normals");
            Undo.PerformUndo();
            Assert.That(MaxDistance(before, target.Document.Normals), Is.LessThan(1e-4f), "Ctrl+Z put the normals back");
            target.Flush();
            Assert.That(MaxDistance(before, target.Working.normals), Is.LessThan(1e-4f), "and the working mesh shows them");
        }
    }

    internal static class MixerTestHooks
    {
        public static System.Action ImportCounter;
    }
}
