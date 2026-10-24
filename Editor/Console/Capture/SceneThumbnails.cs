using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Small screenshots of the Scene view window for the undo timeline: what the window shows once it has drawn the
    /// step's result, read from its own framebuffer (no extra render, nothing on the desktop), scaled down on the GPU,
    /// read back without waiting and compressed to a small JPEG on a worker thread. While the Scene view is hidden
    /// behind another tab of its dock, its camera renders the scene instead (without the handles). At most
    /// <see cref="MaxThumbnails"/> are kept (the oldest go first).
    /// </summary>
    internal static class SceneThumbnails
    {
        private const int Width = 240;
        private const int MaxThumbnails = 600;
        private const int Quality = 72;

        private static readonly object gate = new object();
        private static readonly Dictionary<int, byte[]> images = new Dictionary<int, byte[]>();
        private static readonly Queue<int> order = new Queue<int>();
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo Host = typeof(EditorWindow).GetField("m_Parent", Members);
        private static int nextId;
        private static UndoStep pending;
        private static bool repainted;
        private static int repaints;
        private static bool started;

        public static void Start()
        {
            if (started)
            {
                return;
            }

            started = true;
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.update += Update;
        }

        public static void Stop()
        {
            if (!started)
            {
                return;
            }

            started = false;
            SceneView.duringSceneGui -= OnSceneGui;
            EditorApplication.update -= Update;
            pending = null;
            lock (gate)
            {
                images.Clear();
                order.Clear();
            }
        }

        /// <summary>Snapshots the Scene view for <paramref name="step"/> once it has drawn the step's result.</summary>
        public static void Capture(UndoStep step)
        {
            pending = step;
            repaints = 0;
            var view = SceneView.lastActiveSceneView;
            // A hidden Scene view never draws: its camera renders the step at the next update.
            repainted = view != null && !Shown(view, out _);
            view?.Repaint();
        }

        /// <summary>The JPEG of a snapshot, or null.</summary>
        public static byte[] Get(int id)
        {
            lock (gate)
            {
                return id >= 0 && images.TryGetValue(id, out var jpg) ? jpg : null;
            }
        }

        /// <summary>Keeps a snapshot (read back after a script reload) and returns its id.</summary>
        public static int Add(byte[] jpg)
        {
            if (jpg == null || jpg.Length == 0)
            {
                return -1;
            }

            lock (gate)
            {
                int id = nextId++;
                images[id] = jpg;
                order.Enqueue(id);
                while (order.Count > MaxThumbnails)
                {
                    images.Remove(order.Dequeue());
                }

                return id;
            }
        }

        // The Scene view drew a frame after the step: its target texture now shows it.
        private static void OnSceneGui(SceneView view)
        {
            if (pending != null && Event.current != null && Event.current.type == EventType.Repaint && view == SceneView.lastActiveSceneView)
            {
                // Two frames drawn since the request: the window's buffer holds one showing the step's result.
                if (++repaints >= 2) repainted = true;
                else view.Repaint();
            }
        }

        private static void Update()
        {
            if (pending == null || !repainted)
            {
                return;
            }

            var step = pending;
            pending = null;
            repainted = false;
            try
            {
                var view = SceneView.lastActiveSceneView;
                if (view == null || !SystemInfo.supportsAsyncGPUReadback)
                {
                    return;
                }

                var small = Shown(view, out object host) ? GrabWindow(view, host) : RenderCamera(view);
                if (small == null)
                {
                    return;
                }

                int width = small.width, height = small.height;
                AsyncGPUReadback.Request(small, 0, TextureFormat.RGBA32, request =>
                {
                    try
                    {
                        if (request.hasError)
                        {
                            return;
                        }

                        byte[] pixels = request.GetData<byte>().ToArray();
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                byte[] jpg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, Quality);
                                step.Thumbnail = Add(jpg);
                            }
                            catch (Exception)
                            {
                                // The step simply has no snapshot.
                            }
                        });
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(small);
                    }
                });
            }
            catch (Exception)
            {
                // No snapshot for this step.
            }
        }

        // Whether the Scene view is the tab its dock shows (its window can then be read back).
        private static bool Shown(SceneView view, out object host)
        {
            host = Host?.GetValue(view);
            return host?.GetType().GetProperty("actualView", Members)?.GetValue(host) as EditorWindow == view;
        }

        // What the window shows, handles and grid included, scaled down to the snapshot's size.
        private static RenderTexture GrabWindow(SceneView view, object host)
        {
            var grab = host.GetType().GetMethod("GrabPixels", Members, null, new[] { typeof(RenderTexture), typeof(Rect) }, null);
            float scale = EditorGUIUtility.pixelsPerPoint;
            int fullWidth = Mathf.RoundToInt(view.position.width * scale);
            int fullHeight = Mathf.RoundToInt(view.position.height * scale);
            if (grab == null || fullWidth <= 0 || fullHeight <= 0)
            {
                return null;
            }

            // Editor pixels are already display-encoded: linear targets keep them as they are.
            var full = RenderTexture.GetTemporary(fullWidth, fullHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var small = RenderTexture.GetTemporary(Width, HeightFor(fullWidth, fullHeight), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                grab.Invoke(host, new object[] { full, new Rect(0, 0, fullWidth, fullHeight) });
                // The window's framebuffer is upside down for textures: flipped while scaling down.
                Graphics.Blit(full, small, new Vector2(1f, -1f), new Vector2(0f, 1f));
                return small;
            }
            catch (Exception)
            {
                RenderTexture.ReleaseTemporary(small);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(full);
            }
        }

        // The Scene view hidden behind another tab: its camera renders the scene alone, at twice the size then scaled down.
        private static RenderTexture RenderCamera(SceneView view)
        {
            var camera = view.camera;
            if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
            {
                return null;
            }

            int height = HeightFor(camera.pixelWidth, camera.pixelHeight);
            // Display-encoded targets, so the bytes read back are what a screen shows.
            var full = RenderTexture.GetTemporary(Width * 2, height * 2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var small = RenderTexture.GetTemporary(Width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previousTarget = camera.targetTexture;
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = full;
                camera.Render();
                Graphics.Blit(full, small);
                return small;
            }
            catch (Exception)
            {
                RenderTexture.ReleaseTemporary(small);
                throw;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(full);
            }
        }

        private static int HeightFor(int fullWidth, int fullHeight) => Mathf.Clamp(Mathf.RoundToInt(Width * (float)fullHeight / fullWidth), 16, Width * 2);
    }
}
