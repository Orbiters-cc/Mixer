using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The editing core's primitives on small synthetic data, each against the plain way it replaced: dirty pages and the
    /// paged upload of the working mesh.
    /// </summary>
    public sealed class MixerKernelTests
    {
        // ---- Dirty pages -------------------------------------------------------------------------------------------

        private static List<(int start, int count)> Runs(VertexPages pages)
        {
            var runs = new List<(int, int)>();
            int page = 0;
            while (pages.NextRun(ref page, out int start, out int count)) runs.Add((start, count));
            return runs;
        }

        [Test]
        public void DirtyPagesMergeNeighboursIntoRunsAndEndAtTheVertexCount()
        {
            int size = VertexPages.PageSize;
            var pages = new VertexPages(size * 5 + 10);
            Assert.That(pages.Any, Is.False);
            Assert.That(Runs(pages), Is.Empty);
            pages.Add(3);
            pages.Add(size + 1);
            pages.Add(size * 3 + 7);
            pages.Add(size * 5 + 9);
            pages.Add(-1);
            pages.Add(size * 6);
            Assert.That(pages.Any, Is.True);
            Assert.That(Runs(pages), Is.EqualTo(new List<(int, int)> { (0, 2 * size), (3 * size, size), (5 * size, 10) }));
            Assert.That(pages.MarkedVertexCount(), Is.EqualTo(3 * size + 10));
            pages.Clear();
            Assert.That(pages.Any, Is.False);
            Assert.That(Runs(pages), Is.Empty);
        }

        [Test]
        public void AllPagesAreOneRunAndAUnionKeepsBoth()
        {
            int size = VertexPages.PageSize;
            var pages = new VertexPages(size * 70);
            pages.AddRange(size * 65 - 1, 2);
            Assert.That(Runs(pages), Is.EqualTo(new List<(int, int)> { (size * 64, size * 2) }), "a range over a word boundary");
            var other = new VertexPages(size * 70);
            other.Add(0);
            pages.UnionWith(other);
            Assert.That(Runs(pages).Count, Is.EqualTo(2));
            other.AddAll();
            pages.UnionWith(other);
            Assert.That(pages.All, Is.True);
            Assert.That(Runs(pages), Is.EqualTo(new List<(int, int)> { (0, size * 70) }));
        }

        // ---- The paged upload -------------------------------------------------------------------------------------

        // A mesh of `count` vertices in a row, with positions and normals in a stream of their own (as Unity lays out
        // skinned meshes) or interleaved with a UV (as it lays out most others).
        private static Mesh Strip(int count, bool ownStream, bool tangents = false)
        {
            var layout = new List<VertexAttributeDescriptor>
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
            };
            if (tangents) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, 0));
            layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, ownStream ? 1 : 0));
            var mesh = new Mesh();
            mesh.SetVertexBufferParams(count, layout.ToArray());
            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var uvs = new Vector2[count];
            var tangent = new Vector4[count];
            for (int v = 0; v < count; v++) { positions[v] = new Vector3(v, 0f, 0f); normals[v] = Vector3.up; uvs[v] = new Vector2(v, 1f); tangent[v] = new Vector4(1f, 0f, 0f, -1f); }
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (tangents) mesh.SetTangents(tangent);
            var triangles = new List<int>();
            for (int v = 0; v + 2 < count; v += 3) triangles.AddRange(new[] { v, v + 1, v + 2 });
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        [Test]
        public void TheUploadSendsOnlyTheChangedPagesAndKeepsTheRest([Values(false, true)] bool tangents)
        {
            int count = VertexPages.PageSize * 4;
            var mesh = Strip(count, ownStream: true, tangents);
            try
            {
                Assert.That(WorkingMeshUploader.StreamLayout(mesh, out int stream), Is.EqualTo(tangents ? 40 : 24));
                Assert.That(stream, Is.EqualTo(0));
                var positions = mesh.vertices;
                var normals = mesh.normals;
                var uploader = new WorkingMeshUploader();
                uploader.Upload(mesh, positions, normals, null);
                Assert.That(uploader.Partial, Is.True);
                Assert.That(uploader.LastUploaded, Is.EqualTo(count), "the first upload sends everything");

                var pages = new VertexPages(count);
                int moved = VertexPages.PageSize * 2 + 5;
                positions[moved] += Vector3.up;
                normals[moved] = Vector3.forward;
                // Not marked: must not reach the mesh (it shows the upload went by page).
                positions[3] += Vector3.down;
                pages.Add(moved);
                uploader.Upload(mesh, positions, normals, pages);
                Assert.That(uploader.LastUploaded, Is.EqualTo(VertexPages.PageSize));
                var shown = mesh.vertices;
                var shownNormals = mesh.normals;
                Assert.That(shown[moved], Is.EqualTo(positions[moved]));
                Assert.That(shownNormals[moved], Is.EqualTo(Vector3.forward));
                Assert.That(shown[3], Is.EqualTo(new Vector3(3f, 0f, 0f)), "an unmarked page keeps what the mesh had");
                var uvs = new List<Vector2>();
                mesh.GetUVs(0, uvs);
                Assert.That(uvs[moved], Is.EqualTo(new Vector2(moved, 1f)), "the other stream is untouched");
                if (tangents) Assert.That(mesh.tangents[moved], Is.EqualTo(new Vector4(1f, 0f, 0f, -1f)), "tangents ride along unchanged");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AnInterleavedMeshIsUploadedWhole()
        {
            int count = 300;
            var mesh = Strip(count, ownStream: false);
            try
            {
                Assert.That(WorkingMeshUploader.StreamLayout(mesh, out _), Is.EqualTo(0));
                var positions = mesh.vertices;
                var normals = mesh.normals;
                positions[10] = new Vector3(5f, 5f, 5f);
                var pages = new VertexPages(count);
                var uploader = new WorkingMeshUploader();
                uploader.Upload(mesh, positions, normals, pages);
                Assert.That(uploader.Partial, Is.False);
                Assert.That(uploader.LastUploaded, Is.EqualTo(count));
                Assert.That(mesh.vertices[10], Is.EqualTo(new Vector3(5f, 5f, 5f)));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ---- Influence slots --------------------------------------------------------------------------------------

        // The influences as the document kept them before the slots: a list per vertex, edited as SetBoneWeight and
        // SetInfluences did then. The slots must give the same influences after the same edits.
        private sealed class InfluenceLists
        {
            public readonly List<MeshDocument.Influence>[] Lists;

            public InfluenceLists(Mesh mesh)
            {
                var perVertex = mesh.GetBonesPerVertex().ToArray();
                var weights = mesh.GetAllBoneWeights().ToArray();
                Lists = new List<MeshDocument.Influence>[mesh.vertexCount];
                int cursor = 0;
                for (int v = 0; v < Lists.Length; v++)
                {
                    Lists[v] = new List<MeshDocument.Influence>();
                    for (int k = 0; k < perVertex[v]; k++) { var bw = weights[cursor + k]; if (bw.weight > 0f) Lists[v].Add(new MeshDocument.Influence(bw.boneIndex, bw.weight)); }
                    cursor += perVertex[v];
                }
            }

            public void SetBoneWeight(int v, int bone, float weight, bool normalize)
            {
                var list = Lists[v];
                var strongest = Strongest(list);
                weight = Mathf.Clamp01(weight);
                int index = -1;
                float others = 0f;
                for (int i = 0; i < list.Count; i++) { if (list[i].Bone == bone) index = i; else others += list[i].Weight; }
                if (normalize)
                {
                    if (others > 1e-6f)
                    {
                        float scale = (1f - weight) / others;
                        for (int i = 0; i < list.Count; i++) if (list[i].Bone != bone) list[i] = new MeshDocument.Influence(list[i].Bone, list[i].Weight * scale);
                    }
                    else weight = 1f;
                }
                if (index >= 0) list[index] = new MeshDocument.Influence(bone, weight); else if (weight > 0f) list.Add(new MeshDocument.Influence(bone, weight));
                for (int i = list.Count - 1; i >= 0; i--) if (list[i].Weight < 0.001f) list.RemoveAt(i);
                if (list.Count == 0 && strongest.Weight > 0f) list.Add(strongest);
            }

            public void SetInfluences(int v, MeshDocument.Influence[] influences)
            {
                var list = Lists[v];
                var strongest = Strongest(list);
                list.Clear();
                list.AddRange(influences);
                if (list.Count == 0 && strongest.Weight > 0f) list.Add(strongest);
            }

            private static MeshDocument.Influence Strongest(List<MeshDocument.Influence> list)
            {
                var best = default(MeshDocument.Influence);
                foreach (var i in list) if (i.Weight > best.Weight) best = i;
                return best;
            }
        }

        private static void SameInfluences(MeshDocument doc, InfluenceLists lists, int v)
        {
            var expected = lists.Lists[v];
            Assert.That(doc.InfluenceCount(v), Is.EqualTo(expected.Count), "influences of vertex " + v);
            for (int i = 0; i < expected.Count; i++)
            {
                var got = doc.InfluenceAt(v, i);
                if (i > 0) Assert.That(got.Weight, Is.LessThanOrEqualTo(doc.InfluenceAt(v, i - 1).Weight), "strongest first");
                Assert.That(doc.BoneWeight(v, expected[i].Bone), Is.EqualTo(expected[i].Weight).Within(1e-6f), "bone " + expected[i].Bone + " of vertex " + v);
            }
        }

        [Test]
        public void InfluenceSlotsEditLikeTheListsDid()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(8, 6, 0.3f, 2f);
            try
            {
                var doc = new MeshDocument(mesh);
                var lists = new InfluenceLists(mesh);
                Assert.That(doc.InfluencesLoaded, Is.False);
                for (int v = 0; v < mesh.vertexCount; v++) SameInfluences(doc, lists, v);
                var random = new System.Random(7);
                for (int step = 0; step < 2000; step++)
                {
                    int v = random.Next(mesh.vertexCount);
                    if (random.Next(10) == 0)
                    {
                        // Up to 9 influences: more than the slots the cylinder needed (it grows them).
                        var set = new MeshDocument.Influence[random.Next(10)];
                        for (int i = 0; i < set.Length; i++) set[i] = new MeshDocument.Influence(i + random.Next(2) * 9, (float)random.NextDouble());
                        doc.SetInfluences(v, set);
                        lists.SetInfluences(v, set);
                    }
                    else
                    {
                        int bone = random.Next(6);
                        float weight = random.Next(5) == 0 ? 0f : (float)random.NextDouble();
                        bool normalize = random.Next(3) != 0;
                        doc.SetBoneWeight(v, bone, weight, normalize);
                        lists.SetBoneWeight(v, bone, weight, normalize);
                    }
                    SameInfluences(doc, lists, v);
                }
                for (int v = 0; v < mesh.vertexCount; v++) SameInfluences(doc, lists, v);
                Assert.That(doc.InfluenceSlots, Is.GreaterThanOrEqualTo(9), "the slots grew for nine influences");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PackedBoneWeightsAreTheListsNormalized()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(8, 6, 0.3f, 2f);
            try
            {
                var doc = new MeshDocument(mesh);
                var lists = new InfluenceLists(mesh);
                doc.SetInfluences(3, new MeshDocument.Influence[0]);
                lists.SetInfluences(3, new MeshDocument.Influence[0]);
                doc.SetInfluences(5, new[] { new MeshDocument.Influence(1, 0.2f), new MeshDocument.Influence(0, 0.6f) });
                lists.SetInfluences(5, new[] { new MeshDocument.Influence(1, 0.2f), new MeshDocument.Influence(0, 0.6f) });
                doc.BuildBoneWeights(out var perVertex, out var packed);
                try
                {
                    int cursor = 0;
                    for (int v = 0; v < mesh.vertexCount; v++)
                    {
                        var list = lists.Lists[v];
                        if (list.Count == 0)
                        {
                            Assert.That(perVertex[v], Is.EqualTo(1), "a vertex without bones gets one");
                            Assert.That(packed[cursor].boneIndex, Is.EqualTo(0));
                            Assert.That(packed[cursor].weight, Is.EqualTo(1f));
                            cursor++;
                            continue;
                        }
                        float sum = 0f;
                        foreach (var i in list) sum += i.Weight;
                        Assert.That(perVertex[v], Is.EqualTo(list.Count));
                        float packedSum = 0f;
                        for (int k = 0; k < list.Count; k++, cursor++)
                        {
                            var bw = packed[cursor];
                            if (k > 0) Assert.That(bw.weight, Is.LessThanOrEqualTo(packed[cursor - 1].weight), "strongest first");
                            var match = list.Find(i => i.Bone == bw.boneIndex);
                            Assert.That(bw.weight, Is.EqualTo(match.Weight / sum).Within(1e-6f));
                            packedSum += bw.weight;
                        }
                        Assert.That(packedSum, Is.EqualTo(1f).Within(1e-5f));
                    }
                    Assert.That(cursor, Is.EqualTo(packed.Length));
                }
                finally { perVertex.Dispose(); packed.Dispose(); }
                doc.CopyBoneWeights(out var counts, out var raw);
                Assert.That(counts[3], Is.EqualTo(lists.Lists[3].Count), "the link's copy is not filled in");
                Assert.That(raw[counts.Take(5).Sum(c => (int)c)].weight, Is.EqualTo(0.6f), "nor normalized");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ---- Skinning on demand and the world picture's kernels ----------------------------------------------------

        // The skinning as it was: a blended matrix per vertex from the document's weights, normalized, the renderer's
        // transform for a vertex without weight; the world position through MultiplyPoint3x4.
        private static Matrix4x4 OldBlend(MeshDocument doc, Matrix4x4[] boneMatrices, Matrix4x4 transform, int v)
        {
            var m = Matrix4x4.zero;
            float total = 0f;
            for (int i = 0; i < doc.InfluenceCount(v); i++)
            {
                var influence = doc.InfluenceAt(v, i);
                if (influence.Bone < 0 || influence.Bone >= boneMatrices.Length || influence.Weight <= 0f) continue;
                for (int k = 0; k < 16; k++) m[k] += boneMatrices[influence.Bone][k] * influence.Weight;
                total += influence.Weight;
            }
            if (total <= 1e-6f) return transform;
            if (Mathf.Abs(total - 1f) > 1e-4f) for (int k = 0; k < 16; k++) m[k] *= 1f / total;
            return m;
        }

        [Test]
        public void TheSkinJobMatchesThePerVertexMatricesItReplaced()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(8, 6, 0.3f, 2f);
            var root = new GameObject("MixerSkinFixture");
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
                // Weights the document changed: the map reads them where they are (one with three bones, one unnormalized).
                doc.SetInfluences(10, new[] { new MeshDocument.Influence(0, 0.3f), new MeshDocument.Influence(1, 0.3f) });
                doc.SetBoneWeight(20, 1, 0.8f, normalize: true);
                var map = SkinningMap.For(renderer, mesh, doc);
                Assert.That(map.Skinned, Is.True);
                var bindposes = mesh.bindposes;
                var boneMatrices = new[] { lower.localToWorldMatrix * bindposes[0], upper.localToWorldMatrix * bindposes[1] };
                var transform = body.transform.localToWorldMatrix;
                var positions = mesh.vertices;
                var offset = new Vector3[positions.Length];
                for (int v = 0; v < offset.Length; v++) offset[v] = new Vector3(0f, 0.01f * v, 0f);
                var world = new Vector3[positions.Length];
                map.WorldOf(positions, offset, world);
                for (int v = 0; v < positions.Length; v++)
                {
                    var m = OldBlend(doc, boneMatrices, transform, v);
                    var expected = m.MultiplyPoint3x4(positions[v] + offset[v]);
                    Assert.That(Vector3.Distance(world[v], expected), Is.LessThan(1e-5f), "vertex " + v + " through the job");
                    Assert.That(Vector3.Distance(map.PointToWorld(v, positions[v] + offset[v]), expected), Is.LessThan(1e-5f), "vertex " + v + " on demand");
                    var direction = new Vector3(0.2f, -0.1f, 0.3f);
                    Assert.That(Vector3.Distance(map.ToMeshVector(v, map.VectorToWorld(v, direction)), direction), Is.LessThan(1e-4f), "a move goes back to mesh space");
                }
                // A pose change is seen; the same pose is not.
                Assert.That(map.Refresh(renderer, mesh), Is.False);
                upper.localRotation = Quaternion.Euler(0f, 90f, 0f);
                Assert.That(map.Refresh(renderer, mesh), Is.True);
                // Without bones (a static mesh's case) the renderer's transform alone.
                renderer.bones = new Transform[0];
                map.MarkStale();
                Assert.That(map.Refresh(renderer, mesh), Is.True);
                Assert.That(map.Skinned, Is.False);
                map.WorldOf(positions, null, world);
                for (int v = 0; v < positions.Length; v++) Assert.That(Vector3.Distance(world[v], transform.MultiplyPoint3x4(positions[v])), Is.LessThan(1e-5f));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TheBoundsKernelIsTheSmallestBox()
        {
            Assert.That(MeshKernels.Bounds(new Vector3[0], out _, out _), Is.False);
            var points = new[] { new Vector3(1f, -2f, 3f), new Vector3(-4f, 5f, 0.5f), new Vector3(2f, 0f, -6f) };
            Assert.That(MeshKernels.Bounds(points, out var min, out var max), Is.True);
            Assert.That(min, Is.EqualTo(new Vector3(-4f, -2f, -6f)));
            Assert.That(max, Is.EqualTo(new Vector3(2f, 5f, 3f)));
        }

        // ---- Layer views ------------------------------------------------------------------------------------------

        [Test]
        public void ALayerViewReadsWhatThePerVertexAccessorsRead()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(8, 6, 0.3f, 2f);
            try
            {
                var deltas = new Vector3[mesh.vertexCount];
                for (int v = 0; v < deltas.Length; v++) deltas[v] = new Vector3(0.01f * v, 0f, 0f);
                mesh.AddBlendShapeFrame("Lean", 100f, deltas, null, null);
                var doc = new MeshDocument(mesh);
                doc.Layer = "Lean";
                doc.LayerWeight = 0.5f;
                var shown = doc.ShownView();
                var lean = doc.View("Lean");
                var none = doc.View(null);
                for (int v = 0; v < mesh.vertexCount; v++)
                {
                    Assert.That(shown[v], Is.EqualTo(doc.LayerPosition(v)));
                    Assert.That(lean[v], Is.EqualTo(doc.GetLayerPosition("Lean", v)));
                    Assert.That(none[v], Is.EqualTo(doc.Positions[v]));
                }
                var indices = new[] { 1, 5 };
                doc.SetLayerPositions("Lean", indices, new[] { Vector3.one, Vector3.up });
                Assert.That(doc.GetLayerPosition("Lean", 1), Is.EqualTo(Vector3.one));
                Assert.That(doc.GetLayerPosition("Lean", 5), Is.EqualTo(Vector3.up));
                Assert.That(doc.Shape("Lean").Edited, Is.True);
                doc.SetLayerPositions(null, indices, new[] { Vector3.zero, Vector3.zero });
                Assert.That(doc.Positions[5], Is.EqualTo(Vector3.zero));
                Assert.That(doc.MovedBase.Contains(5), Is.True);
                Assert.That(doc.Dirty.Any, Is.True);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ---- Session records -------------------------------------------------------------------------------------

        [Test]
        public void ASessionKeptForAReloadIsParsedOnce()
        {
            var full = MixerSession.PendingSession.Parse("GlobalObjectId_V1-2-abc\t1234\tGlobalObjectId_V1-1-def\tSmile\t3:25\t-987654321\t5678");
            Assert.That(full.Id, Is.EqualTo("GlobalObjectId_V1-2-abc"));
            Assert.That(full.Working, Is.EqualTo(1234));
            Assert.That(full.Original, Is.EqualTo("GlobalObjectId_V1-1-def"));
            Assert.That(full.Layer, Is.EqualTo("Smile"));
            Assert.That(full.Held, Is.EqualTo("3:25"));
            Assert.That(full.HasFingerprint, Is.True);
            Assert.That(full.Fingerprint, Is.EqualTo(-987654321L));
            Assert.That(full.Renderer, Is.EqualTo(5678));
            var shortLine = MixerSession.PendingSession.Parse("a\t0\tb\t\t");
            Assert.That(shortLine.Layer, Is.Null, "an empty layer is the base");
            Assert.That(shortLine.HasFingerprint, Is.False);
            Assert.That(shortLine.Renderer, Is.EqualTo(0));
            Assert.That(MixerSession.PendingSession.Parse("a\t1"), Is.Null, "too short to restore anything");
        }

        // ---- The history's budget ---------------------------------------------------------------------------------

        private sealed class SizedEntry : HistoryEntry
        {
            public long Size;
            public override long Bytes => Size;
            public override void Undo() { }
            public override void Redo() { }
        }

        private sealed class HeavyCommit : HistoryEntry
        {
            public override long Bytes => 4L << 30;
            public override void Undo() { }
            public override void Redo() { }
        }

        [Test]
        public void InPlaceCommitsAreKeptByCountOutsideTheCap()
        {
            var list = new List<HistoryEntry> { new SizedEntry { Size = 100 } };
            for (int i = 0; i < MixerHistory.KeptCommitCopies; i++) list.Add(new InPlaceCommitEdit { Label = "Commit " + i });
            list.Add(new SizedEntry { Size = 50 });
            Assert.That(MixerHistory.BudgetedBytes(list), Is.EqualTo(150), "the commits' copies are not part of the cap");
            Assert.That(MixerHistory.CommitCopyToDrop(list, list.Count), Is.EqualTo(-1), "as many commits as are kept");
            list.Add(new InPlaceCommitEdit { Label = "Commit more" });
            Assert.That(MixerHistory.CommitCopyToDrop(list, list.Count), Is.EqualTo(1), "the oldest commit goes, the strokes stay");
            Assert.That(MixerHistory.CommitCopyToDrop(list, 1), Is.EqualTo(-1), "an undone commit (waiting for redo) is not dropped");
            Assert.That(MixerHistory.BudgetedBytes(new List<HistoryEntry> { new HeavyCommit() }), Is.EqualTo(4L << 30), "only Mixer's own commits are kept outside");
        }
    }
}
