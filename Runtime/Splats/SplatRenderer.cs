using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Draws a <see cref="SplatCloud"/> in the Scene and Game views (built-in render pipeline, compute shaders):
    /// before each camera's transparent pass, a command buffer sorts the splats back to front on the GPU and draws them
    /// as camera-facing Gaussians. An editor tool: imported clouds live on EditorOnly objects, so builds leave them out.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Orbiters/Mixer/Splat Renderer")]
    public sealed class SplatRenderer : MonoBehaviour
    {
        public SplatCloud cloud;

        /// <summary>The GPU layout of a splat (40 bytes; Splats.shader and SplatSort.compute read the same).</summary>
        internal struct GpuSplat
        {
            public Vector3 position;
            public uint color;
            public Vector3 cov0;
            public Vector3 cov1;
        }

        const int Stride = 40;

        ComputeBuffer splats;
        ComputeBuffer keys;
        ComputeBuffer values;
        SplatCloud uploaded;
        int paddedCount;
        Material material;
        ComputeShader sort;
        MaterialPropertyBlock properties;
        readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();

        static readonly int SplatsId = Shader.PropertyToID("_Splats");
        static readonly int OrderId = Shader.PropertyToID("_Order");
        static readonly int KeysId = Shader.PropertyToID("_Keys");
        static readonly int ValuesId = Shader.PropertyToID("_Values");
        static readonly int CountId = Shader.PropertyToID("_Count");
        static readonly int PaddedId = Shader.PropertyToID("_PaddedCount");
        static readonly int KId = Shader.PropertyToID("_K");
        static readonly int JId = Shader.PropertyToID("_J");
        static readonly int ObjectToWorldId = Shader.PropertyToID("_ObjectToWorld");
        static readonly int ViewRow2Id = Shader.PropertyToID("_ViewRow2");

        void OnEnable()
        {
            Camera.onPreCull += Record;
        }

        void OnDisable()
        {
            Camera.onPreCull -= Record;
            Release();
        }

        void OnDestroy()
        {
            Release();
        }

        /// <summary>Lower triangle of R·S²·Rᵀ, the splat's covariance: (xx, xy, xz) and (yy, yz, zz).</summary>
        internal static void Covariance(Quaternion rotation, Vector3 scale, out Vector3 cov0, out Vector3 cov1)
        {
            Matrix4x4 r = Matrix4x4.Rotate(Normalized(rotation));
            Vector3 s2 = Vector3.Scale(scale, scale);
            float At(int i, int j) => r[i, 0] * r[j, 0] * s2.x + r[i, 1] * r[j, 1] * s2.y + r[i, 2] * r[j, 2] * s2.z;
            cov0 = new Vector3(At(0, 0), At(0, 1), At(0, 2));
            cov1 = new Vector3(At(1, 1), At(1, 2), At(2, 2));
        }

        static Quaternion Normalized(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-12f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        internal static GpuSplat[] Pack(SplatCloud c)
        {
            var packed = new GpuSplat[c.Count];
            for (int i = 0; i < packed.Length; i++)
            {
                Covariance(c.rotations[i], c.scales[i], out Vector3 cov0, out Vector3 cov1);
                Color32 col = c.colors[i];
                packed[i] = new GpuSplat { position = c.positions[i], color = col.r | ((uint)col.g << 8) | ((uint)col.b << 16) | ((uint)col.a << 24), cov0 = cov0, cov1 = cov1 };
            }
            return packed;
        }

        bool Ready()
        {
            if (cloud == null || cloud.Count == 0)
            {
                Release();
                return false;
            }
            if (material == null)
            {
                Shader shader = Shader.Find("Hidden/Orbiters/Mixer/Splats");
                if (shader == null)
                    return false;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
#if UNITY_EDITOR
            if (sort == null)
                sort = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/orbiters.mixer/Runtime/Splats/SplatSort.compute");
#endif
            if (sort == null || !SystemInfo.supportsComputeShaders)
                return false;
            if (uploaded != cloud || splats == null || splats.count != cloud.Count)
            {
                ReleaseBuffers();
                int count = cloud.Count;
                paddedCount = Mathf.Max(512, Mathf.NextPowerOfTwo(count));
                splats = new ComputeBuffer(count, Stride);
                splats.SetData(Pack(cloud));
                keys = new ComputeBuffer(paddedCount, 4);
                values = new ComputeBuffer(paddedCount, 4);
                uploaded = cloud;
            }
            properties = properties ?? new MaterialPropertyBlock();
            return true;
        }

        /// <summary>Records this camera's sort and draw (every frame it renders).</summary>
        void Record(Camera cam)
        {
            // What this camera drew last frame goes, whether or not it draws the splats this time.
            if (buffers.TryGetValue(cam, out CommandBuffer cmd))
                cmd.Clear();
            if (!isActiveAndEnabled || (cam.cullingMask & (1 << gameObject.layer)) == 0)
                return;
            // Scene views come and go: forget the buffers of destroyed cameras.
            if (buffers.Count > 4)
            {
                var dead = new List<Camera>();
                foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers)
                    if (pair.Key == null)
                        dead.Add(pair.Key);
                foreach (Camera c in dead)
                {
                    buffers[c].Release();
                    buffers.Remove(c);
                }
            }
            if (!Ready())
                return;
            if (cmd == null)
            {
                cmd = new CommandBuffer { name = "Orbiters splats" };
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cmd);
                buffers[cam] = cmd;
            }
            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            Matrix4x4 view = cam.worldToCameraMatrix;
            int count = cloud.Count;

            int calc = sort.FindKernel("CalcKeys");
            int local = sort.FindKernel("SortLocal");
            int global = sort.FindKernel("MergeGlobal");
            int merge = sort.FindKernel("MergeLocal");
            cmd.SetComputeIntParam(sort, CountId, count);
            cmd.SetComputeIntParam(sort, PaddedId, paddedCount);
            cmd.SetComputeMatrixParam(sort, ObjectToWorldId, objectToWorld);
            cmd.SetComputeVectorParam(sort, ViewRow2Id, view.GetRow(2));
            cmd.SetComputeBufferParam(sort, calc, SplatsId, splats);
            foreach (int k in new[] { calc, local, global, merge })
            {
                cmd.SetComputeBufferParam(sort, k, KeysId, keys);
                cmd.SetComputeBufferParam(sort, k, ValuesId, values);
            }
            cmd.DispatchCompute(sort, calc, (paddedCount + 255) / 256, 1, 1);
            cmd.DispatchCompute(sort, local, paddedCount / 512, 1, 1);
            for (int k = 1024; k <= paddedCount; k <<= 1)
            {
                cmd.SetComputeIntParam(sort, KId, k);
                for (int j = k >> 1; j >= 512; j >>= 1)
                {
                    cmd.SetComputeIntParam(sort, JId, j);
                    cmd.DispatchCompute(sort, global, (paddedCount / 2 + 255) / 256, 1, 1);
                }
                cmd.DispatchCompute(sort, merge, paddedCount / 512, 1, 1);
            }

            properties.SetBuffer(SplatsId, splats);
            properties.SetBuffer(OrderId, values);
            properties.SetInt(CountId, count);
            properties.SetMatrix(ObjectToWorldId, objectToWorld);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 6, count, properties);
        }

        void ReleaseBuffers()
        {
            splats?.Release();
            keys?.Release();
            values?.Release();
            splats = keys = values = null;
            uploaded = null;
        }

        void Release()
        {
            foreach (KeyValuePair<Camera, CommandBuffer> pair in buffers)
            {
                if (pair.Key != null)
                    pair.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, pair.Value);
                pair.Value.Release();
            }
            buffers.Clear();
            ReleaseBuffers();
            if (material != null)
                DestroyImmediate(material);
            material = null;
        }
    }
}
