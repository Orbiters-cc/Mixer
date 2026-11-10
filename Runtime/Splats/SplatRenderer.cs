using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Draws a <see cref="SplatCloud"/> in the Scene and Game views (built-in render pipeline, compute shaders):
    /// before each camera's transparent pass, a command buffer sorts the splats back to front on the GPU (again only
    /// once the camera has moved enough to reorder them), measures each one's footprint once in a compute pass (again
    /// only when the view changed), draws them as camera-facing Gaussians into a temporary target that shares the
    /// camera's depth buffer, and lays that over the camera's image (<see cref="Pipeline"/>). They blend as Orbiters - N4N0's trainer fitted them, on encoded sRGB colours (Splats.shader). A
    /// cloud's sky map goes first, behind everything the camera drew (where its depth is still the far plane), in place
    /// of the scene's own sky, as Orbiters - N4N0's viewer draws it. An editor tool: imported clouds live on EditorOnly
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

        /// <summary>How the renderer sorts and projects the splats (all draw the same picture; the others are kept to
        /// compare with in Unity and to fall back to).</summary>
        public enum Pipeline
        {
            /// <summary>Each camera's order sorted again only once its eye moved enough to reorder splats
            /// (<see cref="SplatSorting.ResortMinimum"/>), each splat projected once by a compute pass that also leaves out
            /// what is off screen, and drawn from those quads; nothing is sorted or projected while the view holds still.</summary>
            Cached,
            /// <summary>Sorted as <see cref="Cached"/>, projected by every corner in the vertex shader.</summary>
            SortWhenMoved,
            /// <summary>Sorted for every camera every frame, projected by every corner in the vertex shader (as before the
            /// cached pipeline).</summary>
            EveryFrame,
        }

        /// <summary>The pipeline every splat renderer uses; not saved, <see cref="Pipeline.Cached"/> after each reload.
        /// Where the projection pass cannot run (it failed to compile, or this GPU lacks what it needs),
        /// <see cref="Pipeline.Cached"/> draws as <see cref="Pipeline.SortWhenMoved"/>.</summary>
        public static Pipeline pipeline = Pipeline.Cached;

        /// <summary>One camera's command buffer and back-to-front order. What its commands wrote is only taken as done
        /// once the camera has rendered (<see cref="Confirm"/>): until then the order counts as unknown, and is sorted
        /// again if the camera's render never came.</summary>
        sealed class View
        {
            public CommandBuffer Commands;
            public ComputeBuffer Order;
            public SplatSorting.Sorted Sorted, PendingSort;
            public bool HasSorted, HasPendingSort;
            public ProjectionKey PendingProjection;
            public int PendingProjectionWrite;
            public bool HasPendingProjection;

            public void Reset()
            {
                Commands.Clear();
                HasPendingSort = false;
                HasPendingProjection = false;
            }

            public void ReleaseOrder()
            {
                Order?.Release();
                Order = null;
                HasSorted = HasPendingSort = HasPendingProjection = false;
            }
        }

        /// <summary>Everything the projection pass reads besides the splats' bytes (named by the upload): the camera,
        /// its order (by stamp), the cloud's transform, the view and projection, the screen, the eye, and the looks. The
        /// quads are written again whenever any of it differs, compared exactly.</summary>
        struct ProjectionKey : IEquatable<ProjectionKey>
        {
            public int Camera, Upload, OrderStamp;
            public Matrix4x4 ObjectToWorld, View, Projection;
            public Vector4 Screen, Focal, Eye, Confidence, Far, Horizon;

            public bool Equals(ProjectionKey o) =>
                Camera == o.Camera && Upload == o.Upload && OrderStamp == o.OrderStamp && ObjectToWorld.Equals(o.ObjectToWorld) && View.Equals(o.View) &&
                Projection.Equals(o.Projection) && Screen.Equals(o.Screen) && Focal.Equals(o.Focal) && Eye.Equals(o.Eye) && Confidence.Equals(o.Confidence) &&
                Far.Equals(o.Far) && Horizon.Equals(o.Horizon);

            public override bool Equals(object obj) => obj is ProjectionKey k && Equals(k);

            public override int GetHashCode() => Camera ^ (OrderStamp * 397);
        }

        ComputeBuffer splats;
        ComputeBuffer seen;
        /// <summary>The sort's keys, shared by every camera: each sort fills and uses them within one command buffer.</summary>
        ComputeBuffer keys;
        /// <summary>The quads of the last camera projected, shared by every camera (<see cref="projected"/> says whose).</summary>
        ComputeBuffer quads;
        SplatCloud uploaded;
        string uploadedHash;
        bool uploadedSeen;
        int upload, stamps, projectionWrites;
        SplatCloud refused;
        int paddedCount;
        SplatSorting.Step[] schedule;
        Material material;
        ComputeShader sort, project;
        int calcKernel, localKernel, globalKernel, mergeKernel, projectKernel = -1;
        bool projectRefused;
        MaterialPropertyBlock properties;
        ProjectionKey projected;
        bool hasProjected;
        readonly Dictionary<Camera, View> views = new Dictionary<Camera, View>();
        readonly List<Camera> dead = new List<Camera>();
        float sweepAt;

        static readonly int SplatsId = Shader.PropertyToID("_Splats");
        static readonly int SeenId = Shader.PropertyToID("_Seen");
        static readonly int HasSeenId = Shader.PropertyToID("_HasSeen");
        static readonly int OrderId = Shader.PropertyToID("_Order");
        static readonly int QuadsId = Shader.PropertyToID("_Quads");
        static readonly int KeysId = Shader.PropertyToID("_Keys");
        static readonly int ValuesId = Shader.PropertyToID("_Values");
        static readonly int CountId = Shader.PropertyToID("_Count");
        static readonly int PaddedId = Shader.PropertyToID("_PaddedCount");
        static readonly int KId = Shader.PropertyToID("_K");
        static readonly int JId = Shader.PropertyToID("_J");
        static readonly int ObjectToWorldId = Shader.PropertyToID("_ObjectToWorld");
        static readonly int CameraPositionId = Shader.PropertyToID("_CameraPosition");
        static readonly int WorldToObjectId = Shader.PropertyToID("_WorldToObject");
        static readonly int ViewId = Shader.PropertyToID("_View");
        static readonly int ProjectionId = Shader.PropertyToID("_Projection");
        static readonly int FocalId = Shader.PropertyToID("_Focal");
        static readonly int ScreenId = Shader.PropertyToID("_Screen");
        static readonly int EyeInCloudId = Shader.PropertyToID("_EyeInCloud");
        static readonly int ConfidenceId = Shader.PropertyToID("_Confidence");
        static readonly int FarId = Shader.PropertyToID("_Far");
        static readonly int HorizonId = Shader.PropertyToID("_Horizon");
        static readonly int AccumulationId = Shader.PropertyToID("_SplatAccumulation");
        static readonly int SceneId = Shader.PropertyToID("_SplatScene");
        static readonly int SkyMapId = Shader.PropertyToID("_SkyMap");

        void OnEnable()
        {
            Camera.onPreCull += Record;
            Camera.onPostRender += Confirm;
        }

        void OnDisable()
        {
            Camera.onPreCull -= Record;
            Camera.onPostRender -= Confirm;
            Release();
        }

        void OnDestroy()
        {
            Release();
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
            {
                sort = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/orbiters.mixer/Runtime/Splats/SplatSort.compute");
                if (sort != null)
                {
                    calcKernel = sort.FindKernel("CalcKeys");
                    localKernel = sort.FindKernel("SortLocal");
                    globalKernel = sort.FindKernel("MergeGlobal");
                    mergeKernel = sort.FindKernel("MergeLocal");
                }
            }
            if (project == null && !projectRefused)
            {
                project = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/orbiters.mixer/Runtime/Splats/SplatProject.compute");
                projectKernel = project != null && project.HasKernel("Project") ? project.FindKernel("Project") : -1;
                if (projectKernel < 0 || !project.IsSupported(projectKernel))
                {
                    // Said once: the cached pipeline then projects in the vertex shader.
                    Debug.LogWarning("Mixer: the splats' projection pass cannot run here (SplatProject.compute); they are projected in the vertex shader instead.", this);
                    project = null;
                    projectKernel = -1;
                    projectRefused = true;
                }
            }
#endif
            if (sort == null || !SystemInfo.supportsComputeShaders)
                return false;
            // A reimport loads the cloud in place: the same object, maybe the same count, other splats (the hash is of
            // everything uploaded; whether it says what the keyframes saw is checked too, for clouds built in code).
            if (uploaded != cloud || splats == null || splats.count != cloud.Count || uploadedHash != cloud.contentHash || uploadedSeen != cloud.HasSeen)
            {
                ReleaseBuffers();
                int count = cloud.Count;
                paddedCount = SplatSorting.Padded(count);
                schedule = SplatSorting.Schedule(paddedCount);
                // The cloud's own bytes, as they are: it keeps them in this layout (SplatLayout).
                splats = new ComputeBuffer(count, SplatLayout.Stride);
                splats.SetData(cloud.packed);
                // Without a record of what the keyframes saw, one placeholder: the shaders read none (_HasSeen).
                seen = new ComputeBuffer(cloud.HasSeen ? count : 1, SplatLayout.SeenStride);
                if (cloud.HasSeen)
                    seen.SetData(cloud.packedSeen);
                keys = new ComputeBuffer(paddedCount, 4);
                uploaded = cloud;
                uploadedHash = cloud.contentHash;
                uploadedSeen = cloud.HasSeen;
                upload++;
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

        /// <summary>Records this camera's sort (when its order no longer serves), projection (cached pipeline, when what
        /// it reads changed) and draw, every frame it renders.</summary>
        void Record(Camera cam)
        {
            // What this camera drew last frame goes, whether or not it draws the splats this time.
            if (views.TryGetValue(cam, out View view))
                view.Reset();
            if (!isActiveAndEnabled || (cam.cullingMask & (1 << gameObject.layer)) == 0 || !Shows(cam, gameObject.scene))
                return;
            if (!Ready())
                return;
            SweepDeadCameras(view == null);
            if (view == null)
            {
                view = new View { Commands = new CommandBuffer { name = "Orbiters splats" } };
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, view.Commands);
                views[cam] = view;
            }
            CommandBuffer cmd = view.Commands;
            Pipeline path = pipeline == Pipeline.Cached && project == null ? Pipeline.SortWhenMoved : pipeline;
            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            Matrix4x4 worldToObject = objectToWorld.inverse;
            Vector3 eye = cam.transform.position;
            int count = cloud.Count;

            if (view.Order == null)
                view.Order = new ComputeBuffer(paddedCount, 4);
            if (path == Pipeline.EveryFrame || !view.HasSorted || !view.Sorted.Serves(upload, objectToWorld, eye, cloud.bounds))
            {
                RecordSort(cmd, view.Order, count, objectToWorld, eye);
                // Unknown until the camera has rendered.
                view.HasSorted = false;
                view.PendingSort = new SplatSorting.Sorted { Upload = upload, ObjectToWorld = objectToWorld, Eye = eye, Stamp = ++stamps };
                view.HasPendingSort = true;
            }
            int orderStamp = view.HasPendingSort ? view.PendingSort.Stamp : view.Sorted.Stamp;

            var confidence = new Vector4(poorlySeen == PoorlySeen.Shown ? 0f : Mathf.Clamp01(strength), poorlySeen == PoorlySeen.Blurred ? 1f : poorlySeen == PoorlySeen.Hazed ? 2f : 0f, 0f, 0f);
            var far = new Vector4(farField == FarField.Fogged ? 1f : farField == FarField.Cropped ? 2f : 0f, Mathf.Max(farDistance, 0.001f), 0f, 0f);
            // The horizon (encoded sRGB, like the splats' colours): the cloud's sky, else the fog colour as authored.
            Color sky = cloud.hasHorizon ? cloud.horizon : RenderSettings.fogColor;
            var horizon = new Vector4(sky.r, sky.g, sky.b, 1f);
            properties.SetBuffer(SplatsId, splats);
            properties.SetBuffer(SeenId, seen);
            properties.SetInt(HasSeenId, uploadedSeen ? 1 : 0);
            properties.SetBuffer(OrderId, view.Order);
            properties.SetInt(CountId, count);
            properties.SetMatrix(ObjectToWorldId, objectToWorld);
            properties.SetMatrix(WorldToObjectId, worldToObject);
            properties.SetVector(ConfidenceId, confidence);
            properties.SetVector(FarId, far);
            properties.SetVector(HorizonId, horizon);

            int splatPass = 0;
            if (path == Pipeline.Cached)
            {
                splatPass = 3;
                Matrix4x4 projection = cam.projectionMatrix;
                var screen = new Vector4(cam.pixelWidth, cam.pixelHeight, 0f, 0f);
                // In pixels per unit of tangent, both positive: the quads are measured with y up, and pass 3 mirrors them
                // where the GPU's projection flips y.
                var focal = new Vector4(Mathf.Abs(projection.m00) * screen.x * 0.5f, Mathf.Abs(projection.m11) * screen.y * 0.5f,
                    1.3f / Mathf.Abs(projection.m00), 1.3f / Mathf.Abs(projection.m11));
                Vector3 eyeInCloud = worldToObject.MultiplyPoint3x4(eye);
                var key = new ProjectionKey
                {
                    Camera = cam.GetInstanceID(), Upload = upload, OrderStamp = orderStamp, ObjectToWorld = objectToWorld, View = cam.worldToCameraMatrix,
                    Projection = projection, Screen = screen, Focal = focal, Eye = eyeInCloud, Confidence = confidence, Far = far, Horizon = horizon,
                };
                if (quads == null)
                    quads = new ComputeBuffer(Mathf.Max(1, count), QuadStride);
                if (!hasProjected || !projected.Equals(key))
                {
                    cmd.SetComputeIntParam(project, CountId, count);
                    cmd.SetComputeIntParam(project, HasSeenId, uploadedSeen ? 1 : 0);
                    cmd.SetComputeMatrixParam(project, ObjectToWorldId, objectToWorld);
                    cmd.SetComputeMatrixParam(project, ViewId, key.View);
                    cmd.SetComputeMatrixParam(project, ProjectionId, projection);
                    cmd.SetComputeVectorParam(project, FocalId, focal);
                    cmd.SetComputeVectorParam(project, ScreenId, screen);
                    cmd.SetComputeVectorParam(project, EyeInCloudId, eyeInCloud);
                    cmd.SetComputeVectorParam(project, ConfidenceId, confidence);
                    cmd.SetComputeVectorParam(project, FarId, far);
                    cmd.SetComputeVectorParam(project, HorizonId, horizon);
                    cmd.SetComputeBufferParam(project, projectKernel, SplatsId, splats);
                    cmd.SetComputeBufferParam(project, projectKernel, SeenId, seen);
                    cmd.SetComputeBufferParam(project, projectKernel, OrderId, view.Order);
                    cmd.SetComputeBufferParam(project, projectKernel, QuadsId, quads);
                    cmd.DispatchCompute(project, projectKernel, (count + 255) / 256, 1, 1);
                    // The shared quads are unknown from now until this camera has rendered, and then only if no other
                    // camera wrote them in between.
                    hasProjected = false;
                    view.PendingProjection = key;
                    view.PendingProjectionWrite = ++projectionWrites;
                    view.HasPendingProjection = true;
                }
                properties.SetBuffer(QuadsId, quads);
                properties.SetVector(ScreenId, screen);
            }

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
            cmd.DrawProcedural(Matrix4x4.identity, material, splatPass, MeshTopology.Triangles, 6, count, properties);
            cmd.GetTemporaryRT(SceneId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, SceneId);
            cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, properties);
            cmd.ReleaseTemporaryRT(AccumulationId);
            cmd.ReleaseTemporaryRT(SceneId);
        }

        /// <summary>Bytes of one quad SplatProject.compute writes (SplatProjection.hlsl's SplatQuad).</summary>
        internal const int QuadStride = 36;

        /// <summary>After <paramref name="cam"/> rendered: what its commands wrote is there now.</summary>
        void Confirm(Camera cam)
        {
            if (!views.TryGetValue(cam, out View view))
                return;
            if (view.HasPendingSort)
            {
                view.Sorted = view.PendingSort;
                view.HasSorted = true;
                view.HasPendingSort = false;
            }
            if (view.HasPendingProjection)
            {
                // Another camera rendered inside this one's render and wrote the quads too: whose they are is not known.
                hasProjected = view.PendingProjectionWrite == projectionWrites;
                projected = view.PendingProjection;
                view.HasPendingProjection = false;
            }
        }

        /// <summary>SplatSort.compute's dispatches (<see cref="SplatSorting.Schedule"/>) into <paramref name="order"/>.</summary>
        void RecordSort(CommandBuffer cmd, ComputeBuffer order, int count, Matrix4x4 objectToWorld, Vector3 eye)
        {
            cmd.SetComputeIntParam(sort, CountId, count);
            cmd.SetComputeIntParam(sort, PaddedId, paddedCount);
            cmd.SetComputeMatrixParam(sort, ObjectToWorldId, objectToWorld);
            cmd.SetComputeVectorParam(sort, CameraPositionId, eye);
            cmd.SetComputeBufferParam(sort, calcKernel, SplatsId, splats);
            cmd.SetComputeBufferParam(sort, calcKernel, KeysId, keys);
            cmd.SetComputeBufferParam(sort, calcKernel, ValuesId, order);
            cmd.SetComputeBufferParam(sort, localKernel, KeysId, keys);
            cmd.SetComputeBufferParam(sort, localKernel, ValuesId, order);
            cmd.SetComputeBufferParam(sort, globalKernel, KeysId, keys);
            cmd.SetComputeBufferParam(sort, globalKernel, ValuesId, order);
            cmd.SetComputeBufferParam(sort, mergeKernel, KeysId, keys);
            cmd.SetComputeBufferParam(sort, mergeKernel, ValuesId, order);
            int k = -1;
            foreach (SplatSorting.Step step in schedule)
            {
                switch (step.Kernel)
                {
                    case SplatSorting.Kernel.CalcKeys:
                        cmd.DispatchCompute(sort, calcKernel, step.Groups, 1, 1);
                        break;
                    case SplatSorting.Kernel.SortLocal:
                        cmd.DispatchCompute(sort, localKernel, step.Groups, 1, 1);
                        break;
                    case SplatSorting.Kernel.MergeGlobal:
                        if (step.K != k)
                            cmd.SetComputeIntParam(sort, KId, k = step.K);
                        cmd.SetComputeIntParam(sort, JId, step.J);
                        cmd.DispatchCompute(sort, globalKernel, step.Groups, 1, 1);
                        break;
                    default:
                        if (step.K != k)
                            cmd.SetComputeIntParam(sort, KId, k = step.K);
                        cmd.DispatchCompute(sort, mergeKernel, step.Groups, 1, 1);
                        break;
                }
            }
        }

        /// <summary>Scene views come and go: forgets destroyed cameras and frees their orders, when a camera arrives and
        /// otherwise every two seconds.</summary>
        void SweepDeadCameras(bool now)
        {
            float time = Time.realtimeSinceStartup;
            if (!now && time < sweepAt)
                return;
            sweepAt = time + 2f;
            dead.Clear();
            foreach (KeyValuePair<Camera, View> pair in views)
                if (pair.Key == null)
                    dead.Add(pair.Key);
            foreach (Camera c in dead)
            {
                View v = views[c];
                v.ReleaseOrder();
                v.Commands.Release();
                views.Remove(c);
            }
            dead.Clear();
        }

        void ReleaseBuffers()
        {
            splats?.Release();
            seen?.Release();
            keys?.Release();
            quads?.Release();
            splats = seen = keys = quads = null;
            hasProjected = false;
            foreach (View v in views.Values)
                v.ReleaseOrder();
            uploaded = null;
            uploadedHash = null;
        }

        void Release()
        {
            foreach (KeyValuePair<Camera, View> pair in views)
            {
                if (pair.Key != null)
                    pair.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, pair.Value.Commands);
                pair.Value.Commands.Release();
            }
            ReleaseBuffers();
            views.Clear();
            if (material != null)
                DestroyImmediate(material);
            material = null;
        }
    }
}
