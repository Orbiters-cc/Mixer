using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Sends the shown positions and normals of a target to its working mesh. When the mesh keeps positions and normals
    /// in a vertex stream of their own (position, normal and optionally tangent, as Unity lays out skinned meshes), the
    /// stream is mirrored in a staging array and only the changed pages are copied and uploaded
    /// (<see cref="Mesh.SetVertexBufferData{T}(T[],int,int,int,int,MeshUpdateFlags)"/> on sub-ranges): a stroke costs
    /// what it touched, not two copies of the whole mesh a frame. Any other layout (interleaved with UVs, half-precision
    /// normals) takes the whole-array path. The layout is checked before every upload: a change Unity makes to it (a
    /// bone weight upload may lay the mesh out again) rebuilds the mirror.
    /// </summary>
    internal sealed class WorkingMeshUploader
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PositionNormal { public Vector3 Position, Normal; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PositionNormalTangent { public Vector3 Position, Normal; public Vector4 Tangent; }

        private const MeshUpdateFlags Quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
        private const MeshUpdateFlags Notify = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        private PositionNormal[] pn;
        private PositionNormalTangent[] pnt;
        private int stream = -1, stride;

        /// <summary>The last upload went through the vertex stream (only the changed pages); false: whole arrays.</summary>
        public bool Partial => stride != 0;
        /// <summary>Vertices the last upload sent (for the profiler and tests).</summary>
        public int LastUploaded { get; private set; }

        /// <summary>
        /// The stride (24 or 40) of the stream that holds exactly position and normal (and a float tangent) of
        /// <paramref name="mesh"/>, in that order from its start; 0 when the mesh is laid out any other way.
        /// </summary>
        internal static int StreamLayout(Mesh mesh, out int stream)
        {
            stream = -1;
            if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.Position) || !mesh.HasVertexAttribute(VertexAttribute.Normal)) return 0;
            int s = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            if (!Is(mesh, VertexAttribute.Position, s, 0, 3) || !Is(mesh, VertexAttribute.Normal, s, 12, 3)) return 0;
            int bytes = mesh.GetVertexBufferStride(s);
            if (bytes == 24) { stream = s; return 24; }
            if (bytes == 40 && mesh.HasVertexAttribute(VertexAttribute.Tangent) && Is(mesh, VertexAttribute.Tangent, s, 24, 4)) { stream = s; return 40; }
            return 0;
        }

        private static bool Is(Mesh mesh, VertexAttribute attribute, int stream, int offset, int dimension) =>
            mesh.GetVertexAttributeStream(attribute) == stream && mesh.GetVertexAttributeOffset(attribute) == offset
            && mesh.GetVertexAttributeFormat(attribute) == VertexAttributeFormat.Float32 && mesh.GetVertexAttributeDimension(attribute) == dimension;

        /// <summary>
        /// Uploads <paramref name="positions"/> and <paramref name="normals"/> for the vertices <paramref name="pages"/>
        /// marks (all of them for null). The mesh's users (a skinned renderer) are told once, with the last range: a
        /// skinned renderer keeps showing what it skinned last until its mesh says it changed.
        /// </summary>
        public void Upload(Mesh mesh, Vector3[] positions, Vector3[] normals, VertexPages pages)
        {
            int count = positions.Length;
            int s = -1;
            int layout = mesh.vertexCount == count ? StreamLayout(mesh, out s) : 0;
            if (layout == 0 || normals.Length != count)
            {
                Release();
                mesh.SetNormals(normals, 0, normals.Length, Quiet);
                mesh.SetVertices(positions, 0, positions.Length, Notify);
                LastUploaded = count;
                return;
            }
            bool fresh = layout != stride || s != stream || (layout == 24 ? pn == null || pn.Length != count : pnt == null || pnt.Length != count);
            if (fresh)
            {
                // A new mirror: everything goes, and the tangents (which Mixer never changes) come from the mesh once.
                Release();
                stride = layout;
                stream = s;
                if (layout == 24) pn = new PositionNormal[count];
                else
                {
                    pnt = new PositionNormalTangent[count];
                    var tangents = mesh.tangents;
                    if (tangents != null && tangents.Length == count) for (int v = 0; v < count; v++) pnt[v].Tangent = tangents[v];
                }
                pages = null;
            }
            if (pages == null || pages.All)
            {
                Fill(positions, normals, 0, count);
                Send(mesh, 0, count, Notify);
                LastUploaded = count;
                return;
            }
            // Each run is sent when the next is known, so the last one, and only it, tells the renderers.
            int page = 0, sent = 0, pendingStart = 0, pendingCount = 0;
            while (pages.NextRun(ref page, out int start, out int run))
            {
                Fill(positions, normals, start, run);
                if (pendingCount > 0) Send(mesh, pendingStart, pendingCount, Quiet);
                pendingStart = start;
                pendingCount = run;
                sent += run;
            }
            if (pendingCount > 0) Send(mesh, pendingStart, pendingCount, Notify);
            LastUploaded = sent;
        }

        private void Fill(Vector3[] positions, Vector3[] normals, int start, int count)
        {
            int end = start + count;
            if (pn != null) for (int v = start; v < end; v++) { pn[v].Position = positions[v]; pn[v].Normal = normals[v]; }
            else for (int v = start; v < end; v++) { pnt[v].Position = positions[v]; pnt[v].Normal = normals[v]; }
        }

        private void Send(Mesh mesh, int start, int count, MeshUpdateFlags flags)
        {
            if (pn != null) mesh.SetVertexBufferData(pn, start, start, count, stream, flags);
            else mesh.SetVertexBufferData(pnt, start, start, count, stream, flags);
        }

        private void Release()
        {
            pn = null;
            pnt = null;
            stride = 0;
            stream = -1;
        }
    }
}
