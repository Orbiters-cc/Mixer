using System.Collections.Generic;
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
    }
}
