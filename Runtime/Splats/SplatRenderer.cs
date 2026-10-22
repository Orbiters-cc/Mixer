using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Draws a <see cref="SplatCloud"/> in the Scene and Game views (built-in render pipeline, compute shaders):
    /// before each camera's transparent pass, a command buffer sorts the splats back to front on the GPU, draws them as
    /// camera-facing Gaussians into a temporary target that shares the camera's depth buffer, and lays that over the
    /// camera's image. They blend as Orbiters desktop's trainer fitted them, on encoded sRGB colours (Splats.shader). A
    /// cloud's sky map goes first, behind everything the camera drew (where its depth is still the far plane), in place
    /// of the scene's own sky, as Orbiters desktop's viewer draws it. An editor tool: imported clouds live on EditorOnly
    /// objects, so builds leave them out.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Orbiters/Mixer/Splat Renderer")]
    public sealed class SplatRenderer : MonoBehaviour
    {
        public SplatCloud cloud;

        /// <summary>How the splats the keyframes saw poorly show: those seen weakly, in few keyframes, from one side
        /// only, or from much farther than the camera now is (where the cloud says what its keyframes saw; never the sky
        /// shell).</summary>
        [Tooltip("How the splats the capture saw poorly show: weakly, in few keyframes, from one side only, or from much farther than the camera now is.")]
        public PoorlySeen poorlySeen = PoorlySeen.Shown;

        /// <summary>How strongly <see cref="poorlySeen"/> acts, from 0 (as trained) to 1.</summary>
        [Range(0f, 1f)]
        public float strength = 0.75f;

        public enum PoorlySeen
        {
            Shown,
            Faded,
            Blurred,
            /// <summary>Towards the sky's colour at the horizon (the cloud's, else the scene's fog colour).</summary>
            Hazed,
        }

        /// <summary>How what lies beyond the reach of the capture's depth frames shows (where the cloud says; never the sky
        /// shell): as trained, fogged towards the sky's colour at the horizon (the cloud's, else the scene's fog colour) as
        /// 1 − e^(−beyond / <see cref="farDistance"/>), or cropped, fading out over <see cref="farDistance"/>.</summary>
        [Tooltip("How what lies beyond the reach of the capture's depth shows: as trained, fogged towards the sky's horizon, or cropped.")]
        public FarField farField = FarField.Shown;

        /// <summary>The far field's distance: the fog's thickness, the crop's soft edge (metres).</summary>
        [Range(5f, 100f)]
        public float farDistance = 20f;

        public enum FarField
        {
            Shown,
            Fogged,
            Cropped,
        }

        /// <summary>The GPU layout of a splat (64 bytes; Splats.shader and SplatSort.compute read the same): what the
        /// keyframes saw of it (negative weight where unknown), flags (bit 0: the sky shell) and how far beyond the
        /// depth frames' reach it lies.</summary>
        internal struct GpuSplat
        {
            public Vector3 position;
            public uint color;
            public Vector3 cov0;
            public Vector3 cov1;
            public Vector4 seen;
            public uint flags;
            public float beyond;
        }

        const int Stride = 64;

        ComputeBuffer splats;
        ComputeBuffer keys;
        ComputeBuffer values;
        SplatCloud uploaded;
        string uploadedHash;
        SplatCloud refused;
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
        static readonly int CameraPositionId = Shader.PropertyToID("_CameraPosition");
        static readonly int WorldToObjectId = Shader.PropertyToID("_WorldToObject");
        static readonly int ConfidenceId = Shader.PropertyToID("_Confidence");
        static readonly int FarId = Shader.PropertyToID("_Far");
        static readonly int HorizonId = Shader.PropertyToID("_Horizon");
        static readonly int AccumulationId = Shader.PropertyToID("_SplatAccumulation");
        static readonly int SceneId = Shader.PropertyToID("_SplatScene");
        static readonly int SkyMapId = Shader.PropertyToID("_SkyMap");

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
                bool known = c.seen != null && c.seen.Length == packed.Length;
                packed[i] = new GpuSplat
                {
                    position = c.positions[i],
                    color = col.r | ((uint)col.g << 8) | ((uint)col.b << 16) | ((uint)col.a << 24),
                    cov0 = cov0,
                    cov1 = cov1,
                    seen = known ? c.seen[i] : new Vector4(-1f, 0f, 0f, 0f),
                    flags = known && c.sky != null && c.sky.Length == packed.Length && c.sky[i] ? 1u : 0u,
                    beyond = known && c.beyond != null && c.beyond.Length == packed.Length ? c.beyond[i] : 0f,
                };
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
            if (cloud.Count > SplatCloud.MaxCount)
            {
                // Said once per cloud, not every frame for every camera.
                if (refused != cloud)
                    Debug.LogWarning("Mixer: " + cloud.name + " has " + cloud.Count.ToString("N0") + " splats; Mixer draws at most " + SplatCloud.MaxCount.ToString("N0") + ".", this);
                refused = cloud;
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
            // A reimport loads the cloud in place: the same object, maybe the same count, other splats.
            if (uploaded != cloud || splats == null || splats.count != cloud.Count || uploadedHash != cloud.contentHash)
            {
                ReleaseBuffers();
                int count = cloud.Count;
                paddedCount = Mathf.Max(512, Mathf.NextPowerOfTwo(count));
                splats = new ComputeBuffer(count, Stride);
                splats.SetData(Pack(cloud));
                keys = new ComputeBuffer(paddedCount, 4);
                values = new ComputeBuffer(paddedCount, 4);
                uploaded = cloud;
                uploadedHash = cloud.contentHash;
            }
            properties = properties ?? new MaterialPropertyBlock();
            return true;
        }

        /// <summary>
        /// Whether <paramref name="cam"/> shows splats of <paramref name="scene"/>: not previews (inspector previews,
        /// thumbnails) or reflection probes, and not a camera kept to another scene (Prefab Mode's Scene view, preview
        /// scenes other tools render in).
        /// </summary>
        internal static bool Shows(Camera cam, UnityEngine.SceneManagement.Scene scene)
        {
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection)
                return false;
            return !cam.scene.IsValid() || cam.scene == scene;
        }

        /// <summary>
        /// MSAA samples of what <paramref name="cam"/> draws into before its transparent pass, which the splats' target must
        /// match to share its depth buffer (with another count nothing draws). Measured in Unity 2022.3's built-in
        /// pipeline: with HDR an intermediate target, with the quality setting's MSAA when the camera allows it; without
        /// HDR the camera's own target. The depth texture is no substitute: forward rendering fills it from shadow-caster
        /// passes, which unlit shaders lack, so objects drawn with them would no longer hide the splats.
        /// </summary>
        internal static int Samples(Camera cam)
        {
            if (cam.actualRenderingPath != RenderingPath.Forward)
                return 1;
            int quality = Mathf.Max(1, QualitySettings.antiAliasing);
            if (UsesHdr(cam))
                return cam.allowMSAA ? quality : 1;
            if (cam.targetTexture != null)
                return Mathf.Max(1, cam.targetTexture.antiAliasing);
            return cam.allowMSAA ? quality : 1;
        }

        static bool UsesHdr(Camera cam)
        {
#if UNITY_EDITOR
            var tier = UnityEditor.Rendering.EditorGraphicsSettings.GetTierSettings(UnityEditor.EditorUserBuildSettings.selectedBuildTargetGroup, Graphics.activeTier);
            return cam.allowHDR && tier.hdr;
#else
            return cam.allowHDR;
#endif
        }

        /// <summary>Records this camera's sort and draw (every frame it renders).</summary>
        void Record(Camera cam)
        {
            // What this camera drew last frame goes, whether or not it draws the splats this time.
            if (buffers.TryGetValue(cam, out CommandBuffer cmd))
                cmd.Clear();
            if (!isActiveAndEnabled || (cam.cullingMask & (1 << gameObject.layer)) == 0 || !Shows(cam, gameObject.scene))
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
            int count = cloud.Count;

            int calc = sort.FindKernel("CalcKeys");
            int local = sort.FindKernel("SortLocal");
            int global = sort.FindKernel("MergeGlobal");
            int merge = sort.FindKernel("MergeLocal");
            cmd.SetComputeIntParam(sort, CountId, count);
            cmd.SetComputeIntParam(sort, PaddedId, paddedCount);
            cmd.SetComputeMatrixParam(sort, ObjectToWorldId, objectToWorld);
            cmd.SetComputeVectorParam(sort, CameraPositionId, cam.transform.position);
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
            properties.SetMatrix(WorldToObjectId, objectToWorld.inverse);
            properties.SetVector(ConfidenceId, new Vector4(poorlySeen == PoorlySeen.Shown ? 0f : Mathf.Clamp01(strength), poorlySeen == PoorlySeen.Blurred ? 1f : poorlySeen == PoorlySeen.Hazed ? 2f : 0f, 0f, 0f));
            properties.SetVector(FarId, new Vector4(farField == FarField.Fogged ? 1f : farField == FarField.Cropped ? 2f : 0f, Mathf.Max(farDistance, 0.001f), 0f, 0f));
            // The horizon (encoded sRGB, like the splats' colours): the cloud's sky, else the fog colour as authored.
            Color horizon = cloud.hasHorizon ? cloud.horizon : RenderSettings.fogColor;
            properties.SetVector(HorizonId, new Vector4(horizon.r, horizon.g, horizon.b, 1f));
            // The sky map into the camera's own target, where nothing nearer than the far plane was drawn.
            if (cloud.skyMap != null)
            {
                properties.SetTexture(SkyMapId, cloud.skyMap);
                cmd.DrawProcedural(Matrix4x4.identity, material, 2, MeshTopology.Triangles, 3, 1, properties);
            }
            // The splats on their own, depth-tested against the camera's depth buffer; then what the camera drew so far
            // under them, both in encoded sRGB, written back.
            cmd.GetTemporaryRT(AccumulationId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, Samples(cam));
            cmd.SetRenderTarget(AccumulationId, BuiltinRenderTextureType.CurrentActive);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 6, count, properties);
            cmd.GetTemporaryRT(SceneId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, SceneId);
            cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, properties);
            cmd.ReleaseTemporaryRT(AccumulationId);
            cmd.ReleaseTemporaryRT(SceneId);
        }

        void ReleaseBuffers()
        {
            splats?.Release();
            keys?.Release();
            values?.Release();
            splats = keys = values = null;
            uploaded = null;
            uploadedHash = null;
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
