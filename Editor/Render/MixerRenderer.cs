using System;
using System.IO;
using Orbiters.Toolkit.Editor.Processes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Renders the scene with Blender's engine. With Blender linked, the request goes over the link and Blender
    /// renders what it holds of the scene; otherwise a Blender runs in the background, connects to the link, receives
    /// the scene and renders it, then leaves. The result lands as a PNG under Library and is shown without any import.
    /// </summary>
    internal static class MixerRenderer
    {
        public sealed class Settings
        {
            public string Engine = "eevee";
            public int Width = 1920, Height = 1080;
            public int Samples = 64;
            public bool Transparent;
            /// <summary>The camera to render from; null for the Scene view's.</summary>
            public Camera Camera;
        }

        public sealed class Job
        {
            public string Id;
            public string Output;
            public Settings Settings;
            public DateTime Started;
            public bool Done, Ok;
            public string Error;
            public bool Background;
        }

        public static Job Current { get; private set; }
        public static Texture2D LastImage { get; private set; }
        public static string LastPath { get; private set; }
        public static event Action Changed;

        public static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Orbiters", "Mixer", "render"));

        /// <summary>Starts a render; returns an error text or null.</summary>
        public static string Render(Settings settings)
        {
            if (Current != null && !Current.Done) return "A render is running.";
            Directory.CreateDirectory(Folder);
            var job = new Job { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), Settings = settings, Started = DateTime.Now };
            job.Output = Path.Combine(Folder, "render-" + job.Id + ".png");
            var request = Request(job);
            Current = job;
            if (MixerLinkService.Connected)
            {
                MixerLinkService.Send(MixerLinkProtocol.FrameType.RenderRequest, MixerLinkProtocol.Json(request));
                Changed?.Invoke();
                return null;
            }
            // No Blender linked: a background one takes the scene from the link and renders it.
            string blender = BlenderInstalls.GetSelectedExecutablePath();
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) { Current = null; return "Blender was not found. Pick its executable in the Orbiters settings."; }
            MixerLinkService.Enabled = true;
            MixerLinkService.EnsureRunning();
            if (!MixerLinkService.Running) { Current = null; return "The Blender link could not listen."; }
            string requestPath = Path.Combine(Folder, "request-" + job.Id + ".json");
            File.WriteAllText(requestPath, Newtonsoft.Json.JsonConvert.SerializeObject(request));
            string script = Path.GetFullPath(MixerInfo.BasePath + "/Editor/Render/Python~/render_job.py");
            job.Background = true;
            var cli = new BlenderCli.Job { Script = script, LogPath = Path.Combine(Folder, "render-" + job.Id + ".log"), TimeoutSeconds = 3600 };
            cli.ScriptArguments.Add(requestPath);
            cli.ScriptArguments.Add(MixerLinkProtocol.EndpointPath);
            cli.ScriptArguments.Add(EditorPrefs.GetString(BlenderProjectLauncher.SourcePref, ""));
            BlenderCli.Run(cli, result =>
            {
                if (job != Current || job.Done) return;
                if (File.Exists(job.Output)) Finish(job, true, null);
                else Finish(job, false, result.Summary ?? "Blender rendered nothing; see " + cli.LogPath);
            });
            Changed?.Invoke();
            return null;
        }

        private static object Request(Job job)
        {
            var camera = job.Settings.Camera != null ? job.Settings.Camera : SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            var t = camera != null ? camera.transform : null;
            return new
            {
                jobId = job.Id,
                output = job.Output,
                width = job.Settings.Width,
                height = job.Settings.Height,
                samples = job.Settings.Samples,
                engine = job.Settings.Engine,
                transparent = job.Settings.Transparent,
                camera = new
                {
                    p = t != null ? new[] { t.position.x, t.position.y, t.position.z } : new[] { 0f, 1f, -3f },
                    r = t != null ? new[] { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w } : new[] { 0f, 0f, 0f, 1f },
                    fov = camera != null ? camera.fieldOfView : 60f,
                    near = camera != null ? camera.nearClipPlane : 0.05f,
                    far = camera != null ? camera.farClipPlane : 1000f,
                },
            };
        }

        internal static void OnResult(MixerLinkProtocol.RenderResult result)
        {
            if (result == null || Current == null || Current.Done || result.jobId != Current.Id) return;
            Finish(Current, result.ok && File.Exists(result.path ?? Current.Output), result.error);
        }

        private static void Finish(Job job, bool ok, string error)
        {
            job.Done = true;
            job.Ok = ok;
            job.Error = error;
            if (ok)
            {
                try
                {
                    var bytes = File.ReadAllBytes(job.Output);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Mixer render" };
                    if (ImageConversion.LoadImage(texture, bytes))
                    {
                        if (LastImage != null) UnityEngine.Object.DestroyImmediate(LastImage);
                        LastImage = texture;
                        LastPath = job.Output;
                    }
                    else { UnityEngine.Object.DestroyImmediate(texture); job.Ok = false; job.Error = "The render file could not be read."; }
                }
                catch (Exception e) { job.Ok = false; job.Error = e.Message; }
            }
            MixerSession.Say(job.Ok ? "Rendered in " + (DateTime.Now - job.Started).TotalSeconds.ToString("0") + " s." : "Render failed: " + (job.Error ?? "unknown"), !job.Ok);
            Changed?.Invoke();
        }

        /// <summary>Copies the last render into the project (one PNG import, asked for by the user).</summary>
        public static string SaveToProject()
        {
            if (string.IsNullOrEmpty(LastPath) || !File.Exists(LastPath)) return null;
            string folder = "Assets/Orbiters/Mixer/Renders";
            Orbiters.Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(folder);
            string target = folder + "/" + Path.GetFileName(LastPath);
            File.Copy(LastPath, target, true);
            AssetDatabase.ImportAsset(target);
            return target;
        }
    }
}
