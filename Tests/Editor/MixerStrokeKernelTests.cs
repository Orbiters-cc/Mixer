using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The stroke kernels on the flat topology against the managed loops they replaced (kept here, as they were): the
    /// gather under the brush, the move into the document and the world picture, the normals around what moved, and the
    /// welds made by refining the position weld. Every comparison is exact unless it says otherwise: the kernels repeat
    /// the managed code operation for operation.
    /// </summary>
    public sealed class MixerStrokeKernelTests
    {
        // A bumpy cylinder with a UV seam (the first and last column share their places), so welds have members of two.
        private static Mesh Fixture()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(12, 10, 0.3f, 2f);
            var positions = mesh.vertices;
            for (int v = 0; v < positions.Length; v++) positions[v] += positions[v].normalized * (0.01f * Mathf.Sin(v * 1.7f));
            // The seam's two columns stay at one place (the bump above moves each by its own index).
            for (int r = 0; r <= 10; r++) positions[r * 13 + 12] = positions[r * 13];
            mesh.vertices = positions;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- The old managed code ------------------------------------------------------------------------------------

        private static float OldCurve(Falloff falloff, float t)
        {
            t = Mathf.Clamp01(t);
            switch (falloff)
            {
                case Falloff.Sphere: return Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                case Falloff.Linear: return 1f - t;
                case Falloff.Sharp: { float u = 1f - t; return u * u * u; }
                case Falloff.Constant: return t < 1f ? 1f : 0f;
                default: { float u = 1f - t; return u * u * (3f - 2f * u); }
            }
        }

        private static void OldGather(MeshBvh bvh, int[] tris, MeshNeighbors n, Vector3[] world, float[] mask, int[] gathered, int mark, Vector3 center, float radius, Falloff falloff, bool ignoreMask, List<int> welded, List<float> weights)
        {
            var triangles = new List<int>();
            bvh.TrianglesNear(center, radius, triangles);
            welded.Clear();
            weights.Clear();
            float r2 = radius * radius;
            float inverse = 1f / radius;
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
                    float weight = OldCurve(falloff, (float)System.Math.Sqrt(d2) * inverse);
                    if (!ignoreMask) weight *= mask == null ? 1f : 1f - Mathf.Clamp01(mask[rep]);
                    if (weight <= 0f) continue;
                    welded.Add(w);
                    weights.Add(weight);
                }
            }
        }

        private static Vector3 OldFaceNormal(int w, MeshNeighbors weld, int[] triangles, Vector3[] positions)
        {
            float x = 0f, y = 0f, z = 0f;
            foreach (int t in weld.TrianglesAround(w).ToArray())
            {
                int i = t * 3;
                Vector3 a = positions[triangles[i]], b = positions[triangles[i + 1]], c = positions[triangles[i + 2]];
                float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
                float vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                x += uy * vz - uz * vy;
                y += uz * vx - ux * vz;
                z += ux * vy - uy * vx;
            }
            float length = (float)System.Math.Sqrt(x * x + y * y + z * z);
            return length > 0f ? new Vector3(x / length, y / length, z / length) : Vector3.zero;
        }

        private static void OldTurn(int w, MeshNeighbors weld, int[] triangles, Vector3[] display, Vector3[] before, Vector3[] originalNormals, Vector3[] normals)
        {
            var now = OldFaceNormal(w, weld, triangles, display);
            var was = before[w];
            foreach (int m in weld.MembersOf(w).ToArray())
            {
                if (!MeshDocument.Differs(now, was)) { normals[m] = originalNormals[m]; continue; }
                var n = originalNormals[m] + (now - was);
                float length = (float)System.Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                normals[m] = length > 1e-6f ? n / length : now;
            }
        }

        private static void AssertSame(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x), what + " x");
            Assert.That(actual.y, Is.EqualTo(expected.y), what + " y");
            Assert.That(actual.z, Is.EqualTo(expected.z), what + " z");
        }

        // ---- Flat topology -------------------------------------------------------------------------------------------

        [Test]
        public void TheFlatWeldReadsWhatTheSpansHold()
        {
            var mesh = Fixture();
            try
            {
                // Vertices no triangle uses: welded vertices without neighbours or triangles at the start and the end.
                var positions = new List<Vector3> { new Vector3(5f, 0f, 0f) };
                positions.AddRange(mesh.vertices);
                positions.Add(new Vector3(-5f, 0f, 0f));
                var triangles = mesh.triangles.Select(i => i + 1).ToArray();
                var n = MeshNeighbors.Build(positions.ToArray(), triangles, 1e-5f);
                var flat = FlatWeld.Of(n);
                Assert.That(FlatWeld.Of(n), Is.SameAs(flat), "made once per weld");
                Assert.That(n.AdjacentTo(0).Length, Is.EqualTo(0), "the first welded vertex has no neighbours");
                var members = flat.AllMembers.ToArray();
                var adjacent = flat.AllAdjacent.ToArray();
                var around = flat.AllTriangles.ToArray();
                for (int w = 0; w < n.WeldedCount; w++)
                {
                    CollectionAssert.AreEqual(n.MembersOf(w).ToArray(), Run(members, flat.MemberStart, w), "members of " + w);
                    CollectionAssert.AreEqual(n.AdjacentTo(w).ToArray(), Run(adjacent, flat.AdjacentStart, w), "neighbours of " + w);
                    CollectionAssert.AreEqual(n.TrianglesAround(w).ToArray(), Run(around, flat.TriangleStart, w), "triangles of " + w);
                }
                // A weld without any triangle: the relations are empty.
                var lone = FlatWeld.Of(MeshNeighbors.Build(new[] { Vector3.zero, Vector3.one }, new int[0], 1e-5f));
                Assert.That(lone.AdjacentStart[lone.WeldedCount], Is.EqualTo(0));
                Assert.That(lone.AllTriangles.Length, Is.EqualTo(0));
                Assert.That(lone.AllMembers.Length, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static int[] Run(int[] values, int[] start, int w)
        {
            var result = new int[start[w + 1] - start[w]];
            for (int i = 0; i < result.Length; i++) result[i] = values[start[w] + i];
            return result;
        }

        // ---- Gather -------------------------------------------------------------------------------------------------

        [Test]
        public void TheGatherKernelFindsWhatTheManagedGatherFound([Values(0, 1, 2, 3, 4)] int falloffIndex)
        {
            var falloff = (Falloff)falloffIndex;
            var mesh = Fixture();
            try
            {
                var world = mesh.vertices.Select(p => Quaternion.Euler(10f, 20f, 30f) * p + new Vector3(1f, 2f, 3f)).ToArray();
                var triangles = mesh.triangles;
                var n = MeshNeighbors.Build(mesh.vertices, triangles, 1e-5f);
                var bvh = MeshBvh.Build(world, triangles);
                var mask = new float[world.Length];
                for (int v = 0; v < mask.Length; v++) mask[v] = v % 7 == 0 ? 1f : v % 5 == 0 ? 0.4f : v % 11 == 0 ? 1.5f : 0f;
                var oldMarks = new int[n.WeldedCount];
                var newMarks = new int[n.WeldedCount];
                var welded = new List<int>();
                var weights = new List<float>();
                var gotWelded = new int[n.WeldedCount];
                var gotWeights = new float[n.WeldedCount];
                int mark = 0, checkedAny = 0;
                foreach (var center in new[] { world[40], world[77], (world[3] + world[100]) * 0.5f, world[0] + Vector3.right * 5f })
                {
                    foreach (float radius in new[] { 0.05f, 0.3f, 3f })
                    {
                        foreach (bool ignoreMask in new[] { false, true })
                        {
                            mark++;
                            OldGather(bvh, triangles, n, world, mask, oldMarks, mark, center, radius, falloff, ignoreMask, welded, weights);
                            int count = StrokeKernels.Gather(bvh, triangles, n, world, mask, newMarks, mark, center, radius, falloff, ignoreMask, gotWelded, gotWeights);
                            Assert.That(count, Is.EqualTo(welded.Count), "as many at radius " + radius);
                            CollectionAssert.AreEqual(welded, gotWelded.Take(count).ToArray(), "the same welded vertices in the same order");
                            CollectionAssert.AreEqual(weights, gotWeights.Take(count).ToArray(), "the same weights");
                            CollectionAssert.AreEqual(oldMarks, newMarks, "the same marks left");
                            checkedAny += count;
                        }
                    }
                }
                Assert.That(checkedAny, Is.GreaterThan(100), "the brushes met the mesh");
                // Without a mask the mask is not read.
                mark++;
                OldGather(bvh, triangles, n, world, null, oldMarks, mark, world[40], 0.3f, falloff, false, welded, weights);
                int unmasked = StrokeKernels.Gather(bvh, triangles, n, world, null, newMarks, mark, world[40], 0.3f, falloff, false, gotWelded, gotWeights);
                CollectionAssert.AreEqual(weights, gotWeights.Take(unmasked).ToArray());
                for (float t = -0.5f; t <= 1.5f; t += 0.03125f) Assert.That(Brush.Curve(falloff, t), Is.EqualTo(OldCurve(falloff, t)), "the curve at " + t);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ---- Move ---------------------------------------------------------------------------------------------------

        [Test]
        public void TheMoveKernelPlacesVerticesAsThePerVertexSkinningDid([Values(false, true)] bool withOffset)
        {
            var mesh = Fixture();
            var root = new GameObject("MixerMoveKernelFixture");
            try
            {
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
                var doc = new MeshDocument(mesh);
                // Influences that take each branch of the blend: three bones, unnormalized, none, all at zero.
                doc.SetInfluences(10, new[] { new MeshDocument.Influence(0, 0.3f), new MeshDocument.Influence(1, 0.3f) });
                doc.SetBoneWeight(20, 1, 0.8f, normalize: true);
                doc.SetInfluences(30, new MeshDocument.Influence[0]);
                doc.SetInfluences(31, new[] { new MeshDocument.Influence(1, 0f) });
                doc.SetInfluences(32, new[] { new MeshDocument.Influence(0, 0.49997f), new MeshDocument.Influence(1, 0.5f) });
                var n = doc.Neighbors;
                foreach (bool skinned in new[] { true, false })
                {
                    if (!skinned) { renderer.bones = new Transform[0]; }
                    var map = SkinningMap.For(renderer, mesh, doc);
                    Assert.That(map.Skinned, Is.EqualTo(skinned));
                    int count = doc.VertexCount;
                    var layerOld = (Vector3[])doc.Positions.Clone();
                    var layerNew = (Vector3[])doc.Positions.Clone();
                    var displayOld = (Vector3[])doc.Positions.Clone();
                    var displayNew = (Vector3[])doc.Positions.Clone();
                    var offset = withOffset ? Enumerable.Range(0, count).Select(v => new Vector3(0f, 0.001f * v, -0.002f * v)).ToArray() : null;
                    var worldOld = new Vector3[count];
                    var worldNew = new Vector3[count];
                    var moveOf = new Vector3[n.WeldedCount];
                    for (int w = 0; w < moveOf.Length; w++) moveOf[w] = new Vector3(Mathf.Sin(w), Mathf.Cos(w * 0.7f), 0.3f) * 0.01f;
                    // Every other welded vertex, all members, each once (as a stroke step moves them).
                    var vertices = new IndexList();
                    for (int w = 0; w < n.WeldedCount; w += 2) foreach (int v in n.MembersOf(w)) vertices.Add(v);
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        int v = vertices[i];
                        var d = moveOf[n.Weld[v]];
                        layerOld[v] += d;
                        var p = displayOld[v] + d;
                        displayOld[v] = p;
                        if (offset != null) p += offset[v];
                        worldOld[v] = map.PointToWorld(v, p);
                    }
                    StrokeKernels.Move(vertices.Items, vertices.Count, n.Weld, moveOf, layerNew, displayNew, offset, worldNew, map);
                    for (int v = 0; v < count; v++)
                    {
                        AssertSame(layerNew[v], layerOld[v], "layer " + v);
                        AssertSame(displayNew[v], displayOld[v], "shown " + v);
                        // The kernel blends the matrix and applies it as the managed code did; Mono and Burst may still round
                        // the last bit of a sum differently: within a few float steps of the coordinates (about 1e-6 here).
                        Assert.That(Vector3.Distance(worldNew[v], worldOld[v]), Is.LessThan(2e-6f), (skinned ? "skinned " : "static ") + "world " + v);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }

        // ---- Normals ------------------------------------------------------------------------------------------------

        [Test]
        public void TheNormalKernelsTurnTheNormalsAsTheManagedLoopsDid()
        {
            var mesh = Fixture();
            try
            {
                var original = mesh.vertices;
                var triangles = mesh.triangles;
                var originalNormals = mesh.normals;
                var position = MeshNeighbors.Build(original, triangles, 1e-5f);
                var weld = position.Refine(original, triangles, 1e-5f, originalNormals, 0.5f);
                var flat = FlatWeld.Of(weld);
                var before = new Vector3[weld.WeldedCount];
                StrokeKernels.FaceNormals(flat, triangles, original, before);
                for (int w = 0; w < weld.WeldedCount; w++) AssertSame(before[w], OldFaceNormal(w, weld, triangles, original), "face normal of " + w);

                // Some vertices moved, one triangle squashed to nothing (a zero face normal), the rest where they were.
                var display = (Vector3[])original.Clone();
                for (int v = 0; v < display.Length; v += 3) display[v] += new Vector3(0.02f * Mathf.Sin(v), 0.01f, -0.015f * Mathf.Cos(v));
                display[triangles[1]] = display[triangles[0]];
                display[triangles[2]] = display[triangles[0]];
                var expected = new Vector3[display.Length];
                var actual = new Vector3[display.Length];
                for (int w = 0; w < weld.WeldedCount; w++) OldTurn(w, weld, triangles, display, before, originalNormals, expected);
                StrokeKernels.Turn(flat, triangles, null, weld.WeldedCount, display, before, originalNormals, actual);
                for (int v = 0; v < display.Length; v++) AssertSame(actual[v], expected[v], "every normal, " + v);

                // A ring: only its members are written.
                var ring = new[] { 5, 9, 2, weld.WeldedCount - 1 };
                var ringExpected = new Vector3[display.Length];
                var ringActual = new Vector3[display.Length];
                foreach (int w in ring) OldTurn(w, weld, triangles, display, before, originalNormals, ringExpected);
                StrokeKernels.Turn(flat, triangles, ring, ring.Length, display, before, originalNormals, ringActual);
                for (int v = 0; v < display.Length; v++) AssertSame(ringActual[v], ringExpected[v], "ring normal " + v);
                Assert.That(ringActual.Count(x => x != Vector3.zero), Is.EqualTo(ring.Sum(w => weld.MembersOf(w).Length)), "nothing outside the ring");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ---- Welds by refinement ------------------------------------------------------------------------------------

        [Test]
        public void TheNormalAndLinkWeldsRefinedFromThePositionWeldAreTheOnesBuiltAlone()
        {
            var mesh = Fixture();
            try
            {
                var doc = new MeshDocument(mesh);
                var built = MeshNeighbors.Build(doc.OriginalPositions, doc.Triangles, MeshDocument.WeldTolerance(mesh), doc.OriginalNormals, 0.5f);
                CollectionAssert.AreEqual(built.Weld, doc.NormalWeld.Weld, "the normal weld");
                Assert.That(doc.NormalWeld.WeldedCount, Is.LessThan(doc.VertexCount), "the seam welds");

                // A card: two faces on the same welded vertices (the second pass splits it), and keys that keep a seam apart.
                var positions = new List<Vector3>(doc.OriginalPositions);
                var normals = new List<Vector3>(doc.OriginalNormals);
                var triangles = new List<int>(doc.Triangles);
                int start = positions.Count;
                positions.AddRange(new[] { new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), new Vector3(3f, 1f, 0f), new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), new Vector3(3f, 1f, 0f) });
                normals.AddRange(Enumerable.Repeat(Vector3.forward, 6));
                triangles.AddRange(new[] { start, start + 1, start + 2, start + 3, start + 5, start + 4 });
                var keys = new long[positions.Count];
                for (int v = 0; v < keys.Length; v++) keys[v] = v % 13 == 0 ? 7 : 0;
                var p = positions.ToArray();
                var t = triangles.ToArray();
                var nrm = normals.ToArray();
                float tolerance = 1e-5f;
                var byPosition = MeshNeighbors.Build(p, t, tolerance);
                foreach (var k in new[] { null, keys })
                {
                    var alone = MeshDataCodec.LinkWeld(p, t, nrm, tolerance, k);
                    var refined = MeshDataCodec.LinkWeld(p, t, nrm, tolerance, k, byPosition);
                    CollectionAssert.AreEqual(alone.Weld, refined.Weld, "the link weld" + (k != null ? " with keys" : ""));
                    Assert.That(MeshDataCodec.Topology(refined, t), Is.EqualTo(MeshDataCodec.Topology(alone, t)), "the topology Blender holds");
                    Assert.That(refined.Weld[start], Is.Not.EqualTo(refined.Weld[start + 3]), "the card's copy has vertices of its own");
                }
                CollectionAssert.AreEqual(MeshDataCodec.LinkWeld(doc.OriginalPositions, doc.Triangles, doc.OriginalNormals, MeshDocument.WeldTolerance(mesh), MeshDataCodec.LinkWeldKeys(mesh)).Weld, doc.LinkWeld.Weld, "the document's link weld");
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
