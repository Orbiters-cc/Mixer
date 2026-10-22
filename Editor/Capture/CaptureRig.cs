using System;
using Orbiters.Contracts;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>One rendered frame, read back. Buffers are bottom-up (Unity's readback order) and come from the writer's pool.</summary>
    internal sealed class CaptureFrame
    {
        public int Index;
        public float Time;
        public bool DepthFrame;
        public int Width, Height;
        /// <summary>RGBA32, the sRGB codes the render target stores, bottom row first.</summary>
        public byte[] Colour;
        /// <summary>True eye depth in metres from the camera's depth texture, 0 where nothing was drawn, bottom row first.</summary>
        public float[] Depth;
        /// <summary>The values the frame's strip must decode to, bit for bit.</summary>
        public StripPayload Payload;

        /// <summary>The depth code a true depth of <see cref="Depth"/> stands for, as the kit shader writes it: 0 (nothing
        /// there) where nothing was drawn, else the contracts' code (<see cref="DepthFrames.Unknown"/> nearer than
        /// 0.1 m).</summary>
        public static ushort Code(float depth) => depth > 0f ? DepthFrames.Quantize(depth) : (ushort)0;
    }

    /// <summary>
    /// Renders the open scene (or a preview scene) like VRChat's Stream camera with the capture avatar on: a hidden
    /// camera into an ARGB32 sRGB target with HDR and a depth texture, a quad 1 m in front with the contracts' capture
    /// overlay (strip on every frame, depth symbols on depth frames, drawn before post-processing like an avatar),
    /// optional PPv2, and a command buffer that copies the true eye depth for ground truth. Everything is
    /// HideAndDontSave and created through APIs that never dirty the scene; the overlay quad and any preset volume are
    /// only enabled during the rig's own render.
    /// </summary>
    internal sealed class CaptureRig : IDisposable
    {
        internal const string OverlayShaderName = "Hidden/Orbiters/CaptureOverlay";
        internal const string GroundTruthShaderName = "Hidden/Orbiters/Mixer/CaptureGroundTruth";
        /// <summary><c>_VRChatCameraMode</c> during the render: 2, VRChat's desktop handheld camera (which feeds the Stream camera).</summary>
        internal const uint CameraMode = 2;

        private static readonly int DepthFrameId = Shader.PropertyToID("_OrbitersDepthFrame");
        private static readonly int UseTimeOverrideId = Shader.PropertyToID("_OrbitersUseTimeOverride");
        private static readonly int TimeOverrideId = Shader.PropertyToID("_OrbitersTimeOverride");
        private static readonly int SourceId = Shader.PropertyToID("_OrbitersSource");
        private static readonly int CameraModeId = Shader.PropertyToID("_VRChatCameraMode");
        private static readonly int MirrorModeId = Shader.PropertyToID("_VRChatMirrorMode");

        public readonly int Width, Height;
        private readonly GameObject cameraObject;
        private readonly Camera camera;
        private readonly MeshRenderer overlay;
        private readonly Material overlayMaterial, groundTruthMaterial;
        private readonly RenderTexture colourTarget, depthTarget;
        private readonly Texture2D colourRead, depthRead;
        private readonly CommandBuffer groundTruth;
        private readonly CapturePostRig post;
        private Matrix4x4 renderedView, renderedProjection;
        private bool sawRender, disposed;

        /// <summary>The colour target after the last render (for previews).</summary>
        public RenderTexture Target => colourTarget;

        /// <summary>What post-processing the frames got.</summary>
        public string PostNote => post.Note;

        /// <param name="previewScene">Render only this preview scene (tests); default: the open scenes.</param>
        public CaptureRig(CaptureSettings settings, Scene? previewScene = null)
        {
            Width = settings.Width;
            Height = settings.Height;
            if (!StripLayout.TryCreate(Width, Height, out _)) throw new ArgumentException(Width + " × " + Height + " is too small for a capture strip.");
            var overlayShader = Shader.Find(OverlayShaderName);
            var groundTruthShader = Shader.Find(GroundTruthShaderName);
            if (overlayShader == null || !overlayShader.isSupported || groundTruthShader == null || !groundTruthShader.isSupported)
                throw new InvalidOperationException("The capture shaders are missing or unsupported here (" + OverlayShaderName + ", " + GroundTruthShaderName + "): reimport Mixer.");

            var scene = previewScene ?? SceneManager.GetActiveScene();
            var reference = CaptureScene.ReferenceCamera(scene);
            CaptureScene.RigCreated();
            try
            {
                cameraObject = EditorUtility.CreateGameObjectWithHideFlags(CaptureScene.ObjectPrefix + " camera", HideFlags.HideAndDontSave);
                cameraObject.SetActive(false);
                if (previewScene.HasValue) SceneManager.MoveGameObjectToScene(cameraObject, previewScene.Value);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                if (reference != null)
                {
                    camera.cullingMask = reference.cullingMask;
                    camera.clearFlags = reference.clearFlags;
                    camera.backgroundColor = reference.backgroundColor;
                }
                else if (previewScene.HasValue)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black;
                }
                if (previewScene.HasValue) camera.scene = previewScene.Value;
                camera.renderingPath = RenderingPath.Forward;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.depthTextureMode = DepthTextureMode.Depth;
                camera.fieldOfView = settings.FieldOfView;
                camera.nearClipPlane = settings.Near;
                camera.farClipPlane = settings.Far;
                camera.useOcclusionCulling = false;

                colourTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = CaptureScene.ObjectPrefix + " colour", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1,
                };
                colourTarget.Create();
                depthTarget = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    name = CaptureScene.ObjectPrefix + " depth", hideFlags = HideFlags.HideAndDontSave,
                };
                depthTarget.Create();
                camera.targetTexture = colourTarget;
                colourRead = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false) { name = CaptureScene.ObjectPrefix + " colour read", hideFlags = HideFlags.HideAndDontSave };
                depthRead = new Texture2D(Width, Height, TextureFormat.RFloat, false, true) { name = CaptureScene.ObjectPrefix + " depth read", hideFlags = HideFlags.HideAndDontSave };

                overlayMaterial = new Material(overlayShader) { name = CaptureScene.ObjectPrefix + " overlay", hideFlags = HideFlags.HideAndDontSave };
                overlayMaterial.SetFloat(UseTimeOverrideId, 1f);
                overlayMaterial.SetFloat(SourceId, StripFlags.SourceSimulator);
                groundTruthMaterial = new Material(groundTruthShader) { name = CaptureScene.ObjectPrefix + " ground truth", hideFlags = HideFlags.HideAndDontSave };

                // The overlay quad sits 1 m in front (inside any near plane up to 1 m); its shader covers the screen itself.
                var quad = EditorUtility.CreateGameObjectWithHideFlags(CaptureScene.ObjectPrefix + " overlay", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
                if (previewScene.HasValue) SceneManager.MoveGameObjectToScene(quad, previewScene.Value);
                quad.transform.SetParent(cameraObject.transform, false);
                quad.transform.localPosition = new Vector3(0f, 0f, Mathf.Max(1f, settings.Near * 2f));
                quad.layer = FirstLayer(camera.cullingMask);
                camera.cullingMask |= 1 << quad.layer;
                quad.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                overlay = quad.GetComponent<MeshRenderer>();
                overlay.sharedMaterial = overlayMaterial;
                overlay.shadowCastingMode = ShadowCastingMode.Off;
                overlay.receiveShadows = false;
                overlay.lightProbeUsage = LightProbeUsage.Off;
                overlay.reflectionProbeUsage = ReflectionProbeUsage.Off;
                overlay.allowOcclusionWhenDynamic = false;
                overlay.enabled = false;

                groundTruth = new CommandBuffer { name = "Orbiters capture ground truth" };
                groundTruth.SetRenderTarget(depthTarget);
                groundTruth.DrawProcedural(Matrix4x4.identity, groundTruthMaterial, 0, MeshTopology.Triangles, 3);
                groundTruth.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, groundTruth);

                post = new CapturePostRig(cameraObject, settings.Post, scene);
                cameraObject.SetActive(true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static int FirstLayer(int mask)
        {
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        /// <summary>
        /// Renders one frame at <paramref name="pose"/> and reads it back into the given buffers (allocated when null).
        /// Blocks for the GPU: about 20 ms at 1080p.
        /// </summary>
        public CaptureFrame Render(int index, CapturePose pose, float time, bool depthFrame, byte[] colour = null, float[] depth = null)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CaptureRig));
            cameraObject.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            overlayMaterial.SetFloat(TimeOverrideId, time);
            overlayMaterial.SetFloat(DepthFrameId, depthFrame ? 1f : 0f);
            float cameraMode = Shader.GetGlobalFloat(CameraModeId), mirrorMode = Shader.GetGlobalFloat(MirrorModeId);
            Shader.SetGlobalFloat(CameraModeId, CameraMode);
            Shader.SetGlobalFloat(MirrorModeId, 0f);
            sawRender = false;
            Camera.onPreRender += OnPreRender;
            overlay.enabled = true;
            post.BeforeRender();
            try
            {
                camera.Render();
            }
            finally
            {
                post.AfterRender();
                overlay.enabled = false;
                Camera.onPreRender -= OnPreRender;
                Shader.SetGlobalFloat(CameraModeId, cameraMode);
                Shader.SetGlobalFloat(MirrorModeId, mirrorMode);
            }
            if (!sawRender)
            {
                renderedView = camera.worldToCameraMatrix;
                renderedProjection = camera.projectionMatrix;
            }

            var active = RenderTexture.active;
            RenderTexture.active = colourTarget;
            colourRead.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            RenderTexture.active = depthTarget;
            depthRead.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            RenderTexture.active = active;

            var frame = new CaptureFrame
            {
                Index = index,
                Time = time,
                DepthFrame = depthFrame,
                Width = Width,
                Height = Height,
                Colour = colour != null && colour.Length == Width * Height * 4 ? colour : new byte[Width * Height * 4],
                Depth = depth != null && depth.Length == Width * Height ? depth : new float[Width * Height],
                Payload = Payload(time, depthFrame),
            };
            colourRead.GetRawTextureData<byte>().CopyTo(frame.Colour);
            depthRead.GetRawTextureData<float>().CopyTo(frame.Depth);
            return frame;
        }

        // The matrices the shader sees are those of the render itself: after OnPreCull, where PPv2's temporal
        // anti-aliasing jitters the projection.
        private void OnPreRender(Camera rendering)
        {
            if (rendering != camera) return;
            renderedView = rendering.worldToCameraMatrix;
            renderedProjection = rendering.projectionMatrix;
            sawRender = true;
        }

        /// <summary>
        /// The strip payload of the last render, exactly as the GPU had it: Unity sets <c>unity_CameraToWorld</c> to the
        /// affine inverse of the z-flipped view matrix (checked bit for bit; <c>transform.localToWorldMatrix</c> differs
        /// in the last bits).
        /// </summary>
        private StripPayload Payload(float time, bool depthFrame)
        {
            var cameraToWorld = Matrix4x4.identity;
            Matrix4x4.Inverse3DAffine(Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * renderedView, ref cameraToWorld);
            var rows = new float[12];
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                rows[r * 4 + c] = cameraToWorld[r, c];
            return new StripPayload
            {
                Flags = StripFlags.Compose(depthFrame ? StripFlags.KindDepth : StripFlags.KindColour, CameraMode, 0, 0, StripFlags.SourceSimulator),
                Time = time,
                CameraToWorld = rows,
                Projection = new[] { renderedProjection.m00, renderedProjection.m11, renderedProjection.m02, renderedProjection.m12 },
                Near = camera.nearClipPlane,
                Far = camera.farClipPlane,
                ScreenWidth = Width,
                ScreenHeight = Height,
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            post?.Dispose();
            if (camera != null && groundTruth != null) camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, groundTruth);
            groundTruth?.Release();
            if (camera != null) camera.targetTexture = null;
            if (cameraObject != null) Object.DestroyImmediate(cameraObject);
            Release(colourTarget);
            Release(depthTarget);
            if (colourRead != null) Object.DestroyImmediate(colourRead);
            if (depthRead != null) Object.DestroyImmediate(depthRead);
            if (overlayMaterial != null) Object.DestroyImmediate(overlayMaterial);
            if (groundTruthMaterial != null) Object.DestroyImmediate(groundTruthMaterial);
            CaptureScene.RigDisposed();
        }

        private static void Release(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            Object.DestroyImmediate(texture);
        }
    }
}
