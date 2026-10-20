using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>Each brush moves a sphere the way its name says; masks hold vertices; shapes rename, duplicate and mirror.</summary>
    public sealed class MixerBrushTests
    {
        private Scene scene;
        private GameObject root;
        private MeshRenderer renderer;
        private Mesh mesh;
        private EditTarget target;

        // Sessions open before the test (another tool's, the user's): left alone.
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerBrushFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            renderer = root.AddComponent<MeshRenderer>();
            var filter = root.AddComponent<MeshFilter>();
            mesh = Sphere(40, 60, 0.5f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/Sphere.asset");
            filter.sharedMesh = mesh;
            MixerSceneTool.Symmetry = false;
            target = MixerSession.Edit(renderer);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerBrushFixture");
        }

        private static Mesh Sphere(int rings, int segments, float radius)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float theta = 2f * Mathf.PI * s / segments;
                    v.Add(new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta)) * radius);
                }
            }
            int w = segments + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * w + s, b = a + 1, c = a + w, d = c + 1;
                    t.AddRange(new[] { a, b, c, b, d, c });
                }
            var m = new Mesh { name = "Sphere", vertices = v.ToArray(), triangles = t.ToArray() };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private PositionEdit Stroke(string brush, Vector3 from, Vector3 to, float radius = 0.15f, float strength = 1f, bool invert = false) =>
            MixerSceneTool.StrokeBetween(target, Brushes.Named(brush), from, to, 6, radius, strength, Vector3.forward, invert);

        private float RadiusAt(int v) => target.Document.Positions[v].magnitude;

        [Test]
        public void InflateMovesOutAndCtrlMovesIn()
        {
            var front = new Vector3(0, 0, -0.5f);
            var edit = Stroke("Inflate", front, front + Vector3.right * 0.2f);
            Assert.That(edit, Is.Not.Null);
            float mean = edit.Indices.Average(RadiusAt);
            Assert.That(mean, Is.GreaterThan(0.5f + 0.002f), "inflate pushes the surface out along the normals");
            foreach (int i in edit.Indices) Assert.That(RadiusAt(i), Is.GreaterThanOrEqualTo(0.5f - 1e-4f));
            var back = Stroke("Inflate", front, front + Vector3.right * 0.2f, invert: true);
            float after = back.Indices.Average(RadiusAt);
            Assert.That(after, Is.LessThan(mean), "Ctrl pulls it in");
        }

        [Test]
        public void ABrushHeldStillKeepsWorkingUntilTheRelease()
        {
            // Pressed on the sphere's front and never moved: each dab (the still pointer's) inflates it further, at the
            // surface where it is now; Grab, which only follows the pointer, does nothing held.
            var front = new Vector3(0, 0, -0.5f);
            var hit = target.Bvh.ClosestPoint(front, 0.1f);
            var stroke = new SculptStroke(target, Brushes.Named("Inflate"), 0.15f, 1f, Falloff.Smooth, false, Vector3.forward);
            Assert.That(stroke.WorksWhileHeld, Is.True);
            stroke.Begin(hit.Position, target.Bvh.FaceNormal(hit.Triangle), new Ray(hit.Position - Vector3.forward, Vector3.forward), false);
            int nearest = 0;
            float best = float.MaxValue;
            for (int v = 0; v < target.Document.VertexCount; v++) { float d = (target.Document.Positions[v] - front).sqrMagnitude; if (d < best) { best = d; nearest = v; } }
            float afterPress = RadiusAt(nearest);
            for (int i = 0; i < 5; i++)
            {
                var again = target.Bvh.Raycast(front - Vector3.forward, Vector3.forward, 10f, backfaces: false);
                Assert.That(again.Found, Is.True, "the still pointer's ray still meets the surface");
                stroke.Dab(again.Position, target.Bvh.FaceNormal(again.Triangle), false);
            }
            float held = RadiusAt(nearest);
            var edit = stroke.End(false);
            Assert.That(held, Is.GreaterThan(afterPress + 0.005f), "holding the pointer still keeps inflating");
            Assert.That(edit, Is.Not.Null, "the held stroke is one undo step");
            Assert.That(new SculptStroke(target, Brushes.Named("Grab"), 0.15f, 1f, Falloff.Smooth, false, Vector3.forward).WorksWhileHeld, Is.False, "Grab only follows the pointer");
        }

        [Test]
        public void FlattenLevelsAndSmoothRelaxesTheSurface()
        {
            var front = new Vector3(0, 0, -0.5f);
            var bumped = Stroke("Inflate", front, front + Vector3.right * 0.02f, 0.12f);
            float before = bumped.Indices.Select(RadiusAt).Max() - bumped.Indices.Select(RadiusAt).Min();
            Stroke("Flatten", front, front + Vector3.right * 0.02f, 0.2f);
            var radii = bumped.Indices.Select(RadiusAt).ToList();
            float spread = radii.Max() - radii.Min();
            Assert.That(spread, Is.LessThan(before), "flatten brings the bump toward one plane");
            var rough = Stroke("Inflate", front, front + Vector3.right * 0.02f, 0.05f, 1f);
            var neighborsBefore = Roughness(rough.Indices);
            Stroke("Smooth", front, front + Vector3.right * 0.02f, 0.2f);
            Assert.That(Roughness(rough.Indices), Is.LessThan(neighborsBefore), "smooth reduces the difference from the neighbours");
        }

        // Mean distance of each vertex from the average of its neighbours: high for a sharp bump.
        private float Roughness(IEnumerable<int> vertices)
        {
            var n = target.Document.Neighbors;
            var p = target.Document.Positions;
            float total = 0f; int count = 0;
            foreach (int v in vertices)
            {
                int w = n.Weld[v];
                if (n.Adjacent[w].Length == 0) continue;
                var mean = Vector3.zero;
                foreach (int a in n.Adjacent[w]) mean += p[n.Representative[a]];
                mean /= n.Adjacent[w].Length;
                total += (p[v] - mean).magnitude; count++;
            }
            return count > 0 ? total / count : 0f;
        }

        [Test]
        public void GrabFollowsTheCursorAndPinchGathers()
        {
            var front = new Vector3(0, 0, -0.5f);
            var grab = Stroke("Grab", front, front + Vector3.right * 0.1f, 0.15f);
            float meanX = grab.Indices.Average(i => target.Document.Positions[i].x - mesh.vertices[i].x);
            Assert.That(meanX, Is.GreaterThan(0.02f), "the grabbed region moved with the cursor");
            Undo.PerformUndo();
            var pinch = Stroke("Pinch", front, front + Vector3.right * 0.01f, 0.2f);
            float spreadBefore = pinch.Indices.Average(i => new Vector2(mesh.vertices[i].x, mesh.vertices[i].y).magnitude);
            float spreadAfter = pinch.Indices.Average(i => new Vector2(target.Document.Positions[i].x, target.Document.Positions[i].y).magnitude);
            Assert.That(spreadAfter, Is.LessThan(spreadBefore), "pinch pulls vertices toward the centre");
        }

        [Test]
        public void MaskHoldsVerticesStill()
        {
            var front = new Vector3(0, 0, -0.5f);
            for (int pass = 0; pass < 4; pass++) MixerSceneTool.StrokeBetween(target, Brushes.Named("Mask"), front, front + Vector3.right * 0.01f, 2, 0.2f, 1f, Vector3.forward);
            Assert.That(target.Document.MaskedCount, Is.GreaterThan(10), "four passes of the mask brush mask the centre fully");
            var masked = Enumerable.Range(0, target.Document.VertexCount).Where(v => target.Document.Mask[v] > 0.9f).ToList();
            var before = masked.Select(v => target.Document.Positions[v]).ToArray();
            Stroke("Inflate", front, front + Vector3.right * 0.01f, 0.1f);
            for (int i = 0; i < masked.Count; i++) Assert.That(Vector3.Distance(target.Document.Positions[masked[i]], before[i]), Is.LessThan(0.002f), "a masked vertex barely moves");
            MixerSession.InvertMask(target);
            Assert.That(target.Document.Mask[masked[0]], Is.LessThan(0.1f));
            Undo.PerformUndo();
            Assert.That(target.Document.Mask[masked[0]], Is.GreaterThan(0.9f), "mask operations undo");
            MixerSession.ClearMask(target);
            Assert.That(target.Document.HasMask, Is.False);
        }

        [Test]
        public void ShapesRenameDuplicateAndMirror()
        {
            MixerSession.SetLayer(target, "Bump", true);
            var right = new Vector3(0.5f, 0, 0);
            Stroke("Inflate", right, right + Vector3.forward * 0.01f, 0.15f);
            var bump = target.Document.Shape("Bump");
            int movedRight = bump.Deltas.Count(d => d.sqrMagnitude > 0f);
            Assert.That(movedRight, Is.GreaterThan(5));
            MixerSession.MirrorShape(target, "Bump", fromPositiveX: true);
            int movedAll = bump.Deltas.Count(d => d.sqrMagnitude > 0f);
            Assert.That(movedAll, Is.GreaterThan(movedRight * 1.5f), "the +X bump is copied to -X");
            var leftMost = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => mesh.vertices[v].x).First();
            Assert.That(bump.Deltas[leftMost].x, Is.LessThan(-1e-4f), "the mirrored delta points out on the left");
            Undo.PerformUndo();
            Assert.That(bump.Deltas.Count(d => d.sqrMagnitude > 0f), Is.EqualTo(movedRight), "mirror undoes");
            Undo.PerformRedo();

            MixerSession.DuplicateShape(target, "Bump", "Bump2");
            Assert.That(target.Document.HasShape("Bump2"), Is.True);
            Assert.That(target.Document.Layer, Is.EqualTo("Bump2"));
            MixerSession.RenameShape(target, "Bump2", "Bumps");
            Assert.That(target.Document.HasShape("Bump2"), Is.False);
            Assert.That(target.Document.HasShape("Bumps"), Is.True);
            Undo.PerformUndo();
            Assert.That(target.Document.HasShape("Bump2"), Is.True, "rename undoes");
            Undo.PerformUndo();
            Assert.That(target.Document.HasShape("Bump2"), Is.False, "duplicate undoes");
            var asset = MixerSession.Commit(target);
            Assert.That(asset.blendShapeCount, Is.EqualTo(1));
            Assert.That(asset.GetBlendShapeName(0), Is.EqualTo("Bump"));
        }

        [Test]
        public void ABrushNamedByACallerIsThatBrushOrNone()
        {
            Assert.That(Brushes.TryNamed("smooth"), Is.SameAs(Brushes.Named("Smooth")), "any case");
            Assert.That(Brushes.TryNamed(" Clay "), Is.SameAs(Brushes.Named("Clay")));
            Assert.That(Brushes.TryNamed("Smoosh"), Is.Null, "an unknown name is no brush (the MCP tool refuses it), never Grab");
            Assert.That(Brushes.Named("Smoosh"), Is.SameAs(Brushes.All[0]), "a saved preference naming an old brush falls back to Grab");
        }
    }
}
