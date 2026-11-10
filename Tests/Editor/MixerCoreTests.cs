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

        // Undo and redo bring only the vertices they moved into the world picture: it ends where a full rebuild puts it.
        [Test]
        public void UndoKeepsTheWorldPictureRightWithoutARebuild()
        {
            var target = MixerSession.Edit(renderer);
            var worldBefore = (Vector3[])target.World.Clone();
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Grab"), new Vector3(0.3f, 1.5f, 0f), new Vector3(0.6f, 1.5f, 0f), 6, 0.25f, 1f, Vector3.forward);
            var worldAfter = (Vector3[])target.World.Clone();
            Assert.That(MaxDistance(worldBefore, worldAfter), Is.GreaterThan(0.1f));
            Undo.PerformUndo();
            Assert.That(MaxDistance(target.World, worldBefore), Is.LessThan(1e-5f), "undone in the world picture too");
            Undo.PerformRedo();
            Assert.That(MaxDistance(target.World, worldAfter), Is.LessThan(1e-5f), "redone");
            var full = new Vector3[target.World.Length];
            target.Skin.WorldOf(target.Display, null, full);
            Assert.That(MaxDistance(target.World, full), Is.LessThan(1e-5f), "what a full rebuild gives (no shape shows)");
            var hit = target.Bvh.Raycast(new Vector3(2f, 1.5f, 0f), Vector3.left, 5f);
            Assert.That(hit.Found, Is.True, "the BVH follows the picture");
        }

        // A session opened outside the brush modes builds no brush structures until Sculpt or Weight Paint is entered;
        // brushes driven by code still work (the structures are built when first asked for).
        [Test]
        public void BrushStructuresWaitForABrushMode()
        {
            var modeBefore = MixerSceneTool.Mode;
            try
            {
                MixerSceneTool.Mode = MixerMode.None;
                var target = MixerSession.Edit(renderer);
                Assert.That(target.Prewarmed, Is.False, "opened in Object mode");
                MixerSceneTool.Mode = MixerMode.Sculpt;
                Assert.That(target.Prewarmed, Is.True, "Sculpt starts them");
                var edit = MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
                Assert.That(edit, Is.Not.Null);
            }
            finally
            {
                MixerSceneTool.Mode = MixerMode.None;
                MixerSceneTool.Mode = modeBefore;
            }
        }

        // Listeners learn what changed: a session starting, a stroke's geometry, a layer, a group, the coarse event each time.
        [Test]
        public void SessionEventsSayWhatChanged()
        {
            var seen = new List<(EditTarget target, SessionChange change)>();
            int coarse = 0;
            System.Action<EditTarget, SessionChange> edited = (t, c) => seen.Add((t, c));
            System.Action changed = () => coarse++;
            MixerSession.Edited += edited;
            MixerSession.Changed += changed;
            try
            {
                var target = MixerSession.Edit(renderer);
                Assert.That(seen.Last().change, Is.EqualTo(SessionChange.Sessions));
                Assert.That(seen.Last().target, Is.SameAs(target));
                MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
                Assert.That(seen.Last().change, Is.EqualTo(SessionChange.Geometry), "a stroke moved vertices, nothing else");
                MixerSession.SetLayer(target, "Wide", createIfMissing: false);
                Assert.That(seen.Last().change & SessionChange.Layer, Is.EqualTo(SessionChange.Layer));
                MixerSession.AddGroup(target, "Belt");
                Assert.That(seen.Last().change, Is.EqualTo(SessionChange.Groups));
                MixerSession.Revert(target);
                Assert.That(seen.Last().change, Is.EqualTo(SessionChange.Sessions));
                Assert.That(coarse, Is.EqualTo(seen.Count), "the coarse event follows every typed one");
            }
            finally
            {
                MixerSession.Edited -= edited;
                MixerSession.Changed -= changed;
            }
        }

        // The commit writes the shapes from buffers it reuses: an edited shape with an in-between gets the frames FramesOf
        // describes (with their normals turned), and an untouched shape its source's frames exactly.
        [Test]
        public void ACommitBuildWritesTheFramesFramesOfDescribes()
        {
            var deltas = new Vector3[mesh.vertexCount];
            var half = new Vector3[mesh.vertexCount];
            var vertices = mesh.vertices;
            for (int v = 0; v < deltas.Length; v++) if (vertices[v].y < 0.5f) { deltas[v] = new Vector3(0f, 0f, 0.2f); half[v] = new Vector3(0f, 0f, 0.05f); }
            mesh.AddBlendShapeFrame("Kick", 50f, half, null, null);
            mesh.AddBlendShapeFrame("Kick", 100f, deltas, null, null);
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Kick", createIfMissing: false);
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.2f, 0f), new Vector3(0.3f, 0.3f, 0f), 3, 0.2f, 1f, Vector3.forward);
            var doc = target.Document;
            var built = MeshBuilder.Build(target);
            try
            {
                Assert.That(built.blendShapeCount, Is.EqualTo(2));
                int kick = built.GetBlendShapeIndex("Kick");
                var expected = MeshBuilder.FramesOf(doc, doc.ShapeSource, "Kick");
                Assert.That(built.GetBlendShapeFrameCount(kick), Is.EqualTo(expected.Count));
                Assert.That(expected.Count, Is.EqualTo(2));
                var got = new Vector3[mesh.vertexCount];
                var gotNormals = new Vector3[mesh.vertexCount];
                var baseNormals = MeshBuilder.WeldedNormals(doc.Positions, doc);
                for (int f = 0; f < expected.Count; f++)
                {
                    built.GetBlendShapeFrameVertices(kick, f, got, gotNormals, null);
                    Assert.That(built.GetBlendShapeFrameWeight(kick, f), Is.EqualTo(expected[f].weight));
                    Assert.That(MaxDistance(got, expected[f].deltas), Is.LessThan(1e-6f), "frame " + f);
                    var shaped = new Vector3[mesh.vertexCount];
                    for (int v = 0; v < shaped.Length; v++) shaped[v] = doc.Positions[v] + expected[f].deltas[v];
                    var normals = MeshBuilder.WeldedNormals(shaped, doc);
                    for (int v = 0; v < normals.Length; v++) normals[v] -= baseNormals[v];
                    Assert.That(MaxDistance(gotNormals, normals), Is.LessThan(1e-5f), "normals of frame " + f);
                }
                int wide = built.GetBlendShapeIndex("Wide"), source = doc.ShapeSource.GetBlendShapeIndex("Wide");
                var sourceDeltas = new Vector3[mesh.vertexCount];
                var sourceNormals = new Vector3[mesh.vertexCount];
                built.GetBlendShapeFrameVertices(wide, 0, got, gotNormals, null);
                doc.ShapeSource.GetBlendShapeFrameVertices(source, 0, sourceDeltas, sourceNormals, null);
                Assert.That(MaxDistance(got, sourceDeltas), Is.EqualTo(0f), "an untouched shape is copied as it is");
                Assert.That(MaxDistance(gotNormals, sourceNormals), Is.EqualTo(0f));
            }
            finally { Object.DestroyImmediate(built); }
        }

        // The flush uploads only the pages that changed: after strokes, an undo, a layer switch and a shape at half weight,
        // the working mesh still holds exactly what the document shows, everywhere.
        [Test]
        public void ThePagedFlushLeavesTheWorkingMeshEqualToTheDocument()
        {
            var target = MixerSession.Edit(renderer);
            void Same(string when)
            {
                target.Flush();
                Assert.That(MaxDistance(target.Working.vertices, target.Display), Is.LessThan(1e-7f), "positions " + when);
                Assert.That(MaxDistance(target.Working.normals, target.Document.Normals), Is.LessThan(1e-7f), "normals " + when);
                var expected = new Vector3[target.Document.VertexCount];
                target.Document.DisplayPositions(expected);
                Assert.That(MaxDistance(target.Display, expected), Is.LessThan(1e-7f), "the shown positions are the document's " + when);
            }
            Same("at the start");
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 0.5f, 0f), new Vector3(0.3f, 0.6f, 0f), 3, 0.2f, 1f, Vector3.forward);
            Same("after a stroke");
            Undo.PerformUndo();
            Same("after an undo");
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            Same("on another layer");
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Grab"), new Vector3(0.3f, 2f, 0f), new Vector3(0.5f, 2f, 0f), 3, 0.25f, 1f, Vector3.forward);
            Same("after a stroke on the layer");
            MixerSession.SetLayerWeight(target, 0.5f);
            Same("with the layer at half weight");
            MixerSession.SetLayer(target, null, false);
            Same("back on the base");
        }
    }

    internal static class MixerTestHooks
    {
        public static System.Action ImportCounter;
    }
}
