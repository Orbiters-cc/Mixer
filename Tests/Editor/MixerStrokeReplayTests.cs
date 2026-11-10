using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// Whole strokes through the Burst kernels against the same strokes through the managed steps they replaced, on two
    /// sessions of the same mesh placed alike: several dabs along a path, a radius that ends on a vertex, symmetry on,
    /// one brush after another, so every dab gathers from the world picture the earlier ones left and the brushes read
    /// it. The document's positions, the world picture and the normals must stay the same to rounding: the kernels round
    /// every step to single precision where Mono evaluates in double (see <see cref="StrokeKernels"/>), the world picture
    /// a brush reads may differ by a float step of the coordinates, and a dab feeds the next. The bound is absolute and
    /// scales with the mesh: per dab a few steps (16 Epsilon) of its largest coordinate, summed over the dabs. It is far
    /// below anything a brush does (a dab moves millimetres) and would fail on any wrong vertex or move.
    /// </summary>
    public sealed class MixerStrokeReplayTests
    {
        private const string Folder = "Assets/Orbiters/MixerTests/StrokeReplay";
        private Scene scene;
        private readonly List<GameObject> roots = new List<GameObject>();
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            scene = EditorSceneManager.NewPreviewScene();
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(Folder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            foreach (var r in roots) if (r != null) Object.DestroyImmediate(r);
            roots.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerReplayStatic");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerReplaySkinned");
        }

        private static void RequireBurst()
        {
            if (!StrokeKernels.RunsBurst())
                Assert.Ignore("Burst did not run the stroke kernels in this editor (Burst off, or compiling disabled): the comparison would set managed code against itself.");
        }

        private EditTarget StaticSession(Mesh mesh)
        {
            var root = new GameObject("MixerReplayStatic");
            SceneManager.MoveGameObjectToScene(root, scene);
            roots.Add(root);
            root.transform.SetPositionAndRotation(new Vector3(0.3f, -0.2f, 1.1f), Quaternion.Euler(12f, 37f, -8f));
            root.transform.localScale = new Vector3(1.2f, 0.9f, 1.05f);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            return MixerSession.Edit(root.AddComponent<MeshRenderer>());
        }

        private EditTarget SkinnedSession(Mesh mesh)
        {
            var root = new GameObject("MixerReplaySkinned");
            SceneManager.MoveGameObjectToScene(root, scene);
            roots.Add(root);
            root.transform.position = new Vector3(1f, 2f, 3f);
            var lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            var upper = new GameObject("Upper").transform; upper.SetParent(lower, false);
            upper.localPosition = new Vector3(0f, 1f, 0f);
            upper.localRotation = Quaternion.Euler(30f, 0f, 45f);
            lower.localScale = new Vector3(1f, 1.5f, 1f);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            return MixerSession.Edit(renderer);
        }

        [Test]
        public void StrokesOnAStaticMeshGiveExactlyWhatTheManagedStepsGave()
        {
            RequireBurst();
            var mesh = MixerCoreTestsFixtures.Cylinder(24, 20, 0.3f, 2f);
            AssetDatabase.CreateAsset(mesh, Folder + "/Static.asset");
            var burst = StaticSession(mesh);
            var managed = StaticSession(mesh);
            Replay(burst, managed);
        }

        [Test]
        public void StrokesOnASkinnedMeshStayWithinTheirBound()
        {
            RequireBurst();
            var mesh = MixerCoreTestsFixtures.Cylinder(24, 20, 0.3f, 2f);
            AssetDatabase.CreateAsset(mesh, Folder + "/Skinned.asset");
            var burst = SkinnedSession(mesh);
            var managed = SkinnedSession(mesh);
            Assert.That(burst.Skin.Skinned, Is.True);
            Replay(burst, managed);
        }

        private const float Epsilon = 1.1920929e-7f;

        // Four brushes in turn, six dabs each along the side of the cylinder, symmetry on, on both sessions; compared after
        // every stroke, within a bound that grows with the dabs made so far.
        private static void Replay(EditTarget burst, EditTarget managed)
        {
            float scale = 0f;
            foreach (var p in burst.World) scale = Mathf.Max(scale, Mathf.Max(Mathf.Abs(p.x), Mathf.Max(Mathf.Abs(p.y), Mathf.Abs(p.z))));
            int dabs = 0;
            Assert.That(burst.World, Is.EqualTo(managed.World), "both sessions start alike");
            var reference = new ManagedStroke(managed);
            var world = (Vector3[])burst.World.Clone();
            int count = world.Length;
            // A path over the surface (vertex places), and a radius that ends on a vertex: the gather's boundary test.
            var path = new[] { 30, 31, 57, 58, 84, 85 }.Select(v => world[v % count]).ToArray();
            float radius = Vector3.Distance(path[0], world[30 + 2 * 25]);
            Vector3 view = Vector3.forward;
            foreach (string name in new[] { "Inflate", "Smooth", "Crease", "Flatten" })
            {
                var brush = Brushes.Named(name);
                var stroke = new SculptStroke(burst, brush, radius, 0.8f, Falloff.Smooth, true, view);
                var normal = Normal(path[0]);
                stroke.Begin(path[0], normal, new Ray(path[0] - view, view), false);
                reference.Begin(brush, radius, 0.8f, Falloff.Smooth, view, path[0], normal);
                for (int i = 1; i < path.Length; i++)
                {
                    stroke.Dab(path[i], Normal(path[i]), false);
                    reference.Dab(path[i], Normal(path[i]));
                }
                var edit = stroke.End(false);
                Assert.That(edit, Is.Not.Null, name + " moved something");
                reference.End();
                dabs += path.Length;
                float bound = dabs * 16f * Epsilon * scale;
                Compare(burst.Document.Positions, managed.Document.Positions, bound, name + ": positions");
                // The world picture is the positions through the transform or the skin (scale up to 1.5) plus its own steps.
                Compare(burst.World, managed.World, 2f * bound, name + ": world picture");
                // A normal turns by about the positions' difference over an edge (the shortest is ~0.08 m here): unit vectors.
                Compare(burst.Document.Normals, managed.Document.Normals, 2f * bound / 0.08f + 16f * Epsilon, name + ": normals");
            }
        }

        private static Vector3 Normal(Vector3 p) => new Vector3(p.x, 0f, p.z).normalized;

        private static void Compare(Vector3[] actual, Vector3[] expected, float bound, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            float worst = 0f;
            int at = -1;
            for (int v = 0; v < actual.Length; v++)
            {
                float d = Vector3.Distance(actual[v], expected[v]);
                if (d > worst) { worst = d; at = v; }
            }
            Assert.That(worst, Is.LessThanOrEqualTo(bound), what + ": the largest difference, at vertex " + at);
        }

        /// <summary>A sculpt stroke as SculptStroke stepped it before the kernels: managed gather, brush, mirror, move.</summary>
        private sealed class ManagedStroke
        {
            private readonly EditTarget target;
            private readonly int[] gathered, moveStamp, mirrorStamp;
            private readonly Vector3[] moveOf, mirrorOf;
            private readonly List<int> local = new List<int>(), mirrored = new List<int>(), moved = new List<int>(), strokeMoved = new List<int>();
            private readonly List<int> triangles = new List<int>();
            private BrushContext ctx;
            private Brush brush;
            private Falloff falloff;
            private Vector3[] moves = new Vector3[16];
            private int mark;

            public ManagedStroke(EditTarget target)
            {
                this.target = target;
                int w = target.Document.Neighbors.WeldedCount;
                gathered = new int[w]; moveStamp = new int[w]; mirrorStamp = new int[w];
                moveOf = new Vector3[w]; mirrorOf = new Vector3[w];
            }

            public void Begin(Brush brush, float radius, float strength, Falloff falloff, Vector3 view, Vector3 point, Vector3 normal)
            {
                this.brush = brush;
                this.falloff = falloff;
                int w = target.Document.Neighbors.WeldedCount;
                ctx = new BrushContext(new int[w], new float[w]) { Target = target, Radius = radius, Strength = strength, ViewDirection = view.normalized };
                ctx.Center = point; ctx.Previous = point; ctx.Normal = normal; ctx.Invert = false; ctx.First = true;
                strokeMoved.Clear();
                target.BeginStroke();
                Gather(point);
                Apply();
                ctx.First = false;
            }

            public void Dab(Vector3 point, Vector3 normal)
            {
                ctx.Previous = ctx.Center;
                ctx.Center = point;
                ctx.Normal = normal;
                Gather(point);
                Apply();
                ctx.First = false;
            }

            public void End() => target.RefreshNormals(strokeMoved);

            private void Gather(Vector3 center)
            {
                var doc = target.Document;
                var n = doc.Neighbors;
                var world = target.World;
                var tris = doc.Triangles;
                mark++;
                target.Bvh.TrianglesNear(center, ctx.Radius, triangles);
                ctx.Count = 0;
                float r2 = ctx.Radius * ctx.Radius;
                float inverse = 1f / ctx.Radius;
                foreach (int t in triangles)
                {
                    for (int k = t * 3, end = k + 3; k < end; k++)
                    {
                        int w = n.Weld[tris[k]];
                        if (gathered[w] == mark) continue;
                        gathered[w] = mark;
                        int rep = n.Representative[w];
                        var p = world[rep];
                        float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 > r2) continue;
                        float weight = Brush.Curve(falloff, (float)System.Math.Sqrt(d2) * inverse) * doc.Free(rep);
                        if (weight <= 0f) continue;
                        ctx.Welded[ctx.Count] = w;
                        ctx.Weight[ctx.Count] = weight;
                        ctx.Count++;
                    }
                }
                if (moves.Length < ctx.Count) moves = new Vector3[Mathf.NextPowerOfTwo(ctx.Count)];
            }

            private void Apply()
            {
                if (ctx.Count == 0) return;
                brush.Step(ctx, moves);
                var doc = target.Document;
                var n = doc.Neighbors;
                var skin = target.Skin;
                int step = ++mark;
                local.Clear();
                for (int i = 0; i < ctx.Count; i++)
                {
                    var m = moves[i];
                    if (m.sqrMagnitude < 1e-16f) continue;
                    int w = ctx.Welded[i];
                    Add(local, moveStamp, moveOf, step, w, skin.ToMeshVector(n.Representative[w], m));
                }
                var mirror = target.Mirror;
                mirrored.Clear();
                foreach (int w in local)
                {
                    int other = mirror.Of[w];
                    if (other < 0) continue;
                    var d = moveOf[w];
                    if (other == w) { if (mirrorStamp[w] != step) { mirrorStamp[w] = step; mirrored.Add(w); } mirrorOf[w] = new Vector3(0f, d.y, d.z) - d; continue; }
                    Add(mirrored, mirrorStamp, mirrorOf, step, other, new Vector3(-d.x, d.y, d.z));
                }
                foreach (int w in mirrored) Add(local, moveStamp, moveOf, step, w, mirrorOf[w]);
                // The move as EditTarget.MoveWelded did it per vertex (no layer: the base; no other shapes: offset 0).
                var display = target.Display;
                moved.Clear();
                foreach (int w in local)
                {
                    var d = moveOf[w];
                    foreach (int v in n.MembersOf(w))
                    {
                        doc.Positions[v] += d;
                        var p = display[v] + d;
                        display[v] = p;
                        p += Vector3.zero;
                        target.World[v] = skin.PointToWorld(v, p);
                        moved.Add(v);
                        strokeMoved.Add(v);
                    }
                }
                target.Bvh.RefitVertices(moved, n);
            }

            private static void Add(List<int> list, int[] stamps, Vector3[] values, int step, int w, Vector3 d)
            {
                if (stamps[w] != step) { stamps[w] = step; values[w] = d; list.Add(w); }
                else values[w] += d;
            }
        }
    }
}
