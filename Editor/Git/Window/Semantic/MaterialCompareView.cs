using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Orbiters.UnitGit.Editor.Semantic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>Real material versions on identical lit spheres. No objects enter the user's scene.</summary>
    internal sealed class MaterialCompareView : VisualElement
    {
        private readonly Material[] materials = new Material[2];
        private readonly PreviewRenderUtility[] previews = new PreviewRenderUtility[2];
        private readonly Label status;
        private readonly IMGUIContainer viewport;
        private readonly Task<string[]> read;
        private readonly string assetPath;
        private Mesh sphere;
        private Vector2 orbit = new Vector2(20, -25);
        private float distance = 4.5f;
        private bool ready, disposed;

        public MaterialCompareView(string assetPath, Func<string> before, Func<string> after, string left, string right)
        {
            this.assetPath = assetPath;
            AddToClassList("ugm");
            var stage = new VisualElement(); stage.AddToClassList("ugm-stage"); Add(stage);
            var bar = new VisualElement(); bar.AddToClassList("ugm-bar"); stage.Add(bar);
            foreach (var title in new[] { left, right })
            {
                var caption = new Label(title); caption.AddToClassList("ugm-caption"); bar.Add(caption);
            }
            viewport = new IMGUIContainer(Draw); viewport.style.height = 210; stage.Add(viewport);
            status = new Label("Loading material previews…"); status.AddToClassList("ugm-status"); Add(status);
            read = Task.Run(() => new[] { before(), after() });
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
        }

        private void Poll()
        {
            if (!read.IsCompleted || disposed) return;
            EditorApplication.update -= Poll;
            try
            {
                if (read.IsFaulted) throw read.Exception.GetBaseException();
                sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                for (int i = 0; i < 2; i++)
                {
                    materials[i] = ReadMaterial(read.Result[i], assetPath);
                    if (materials[i] == null) continue;
                    previews[i] = new PreviewRenderUtility();
                    previews[i].camera.fieldOfView = 30;
                    previews[i].camera.nearClipPlane = .01f;
                    previews[i].camera.farClipPlane = 20;
                    previews[i].camera.clearFlags = CameraClearFlags.SolidColor;
                    previews[i].camera.backgroundColor = new Color(.12f, .13f, .15f);
                    previews[i].ambientColor = new Color(.35f, .35f, .35f);
                    previews[i].lights[0].intensity = 1.2f;
                    previews[i].lights[0].transform.rotation = Quaternion.Euler(35, 35, 0);
                    previews[i].lights[1].intensity = .6f;
                    previews[i].lights[1].transform.rotation = Quaternion.Euler(340, 215, 0);
                }
                ready = true;
                status.text = "Drag to rotate · Scroll to zoom · Double-click to reset. Shaders and textures use the current project versions.";
            }
            catch (Exception ex) { status.text = "Material preview unavailable: " + ex.Message; }
            viewport.MarkDirtyRepaint();
        }

        internal static Material ReadMaterial(string yaml, string originalPath)
        {
            if (string.IsNullOrWhiteSpace(yaml)) return null;
            string scratch = Path.Combine(Application.temporaryCachePath, "UnitGitMaterial-" + Guid.NewGuid().ToString("N") + ".mat");
            string asset = null;
            try
            {
                var document = UnityYaml.Parse(yaml).Documents.FirstOrDefault(d => d.ClassId == 21);
                if (document == null) throw new InvalidDataException("This version contains no material.");
                string originalName = document.Body.Text("m_Name") ?? Path.GetFileNameWithoutExtension(originalPath);
                // Unity requires a native asset's root name to match its filename. Normalize only the
                // temporary copy, including wrapped/quoted names, so renamed versions and names containing
                // path characters remain safe to preview. Restore the authored name on the in-memory clone.
                var nameField = new Regex(@"^  m_Name:[^\r\n]*(?:\r?\n(?:    [^\r\n]+|[ \t]*(?=\r?$)))*", RegexOptions.Multiline);
                File.WriteAllText(scratch, nameField.Replace(yaml, "  m_Name: material", 1));
                asset = ModelVersions.Import(scratch, originalPath, "material");
                var source = AssetDatabase.LoadAssetAtPath<Material>(asset);
                if (source == null) throw new InvalidDataException("Unity could not read this material version.");
                return new Material(source) { name = originalName, hideFlags = HideFlags.HideAndDontSave };
            }
            finally
            {
                ModelVersions.Release(asset);
                if (File.Exists(scratch)) File.Delete(scratch);
            }
        }

        private void Draw()
        {
            var rect = viewport.contentRect;
            if (!ready || rect.width < 20 || rect.height < 20) return;
            var e = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                GUIUtility.hotControl = control;
                if (e.clickCount == 2) { orbit = new Vector2(20, -25); distance = 4.5f; }
                e.Use();
            }
            if (e.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            { orbit += new Vector2(-e.delta.y, -e.delta.x) * .6f; e.Use(); viewport.MarkDirtyRepaint(); }
            if (e.type == EventType.MouseUp && GUIUtility.hotControl == control) { GUIUtility.hotControl = 0; e.Use(); }
            if (e.type == EventType.ScrollWheel && rect.Contains(e.mousePosition))
            { distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * .05f), 2.5f, 9); e.Use(); viewport.MarkDirtyRepaint(); }
            if (e.type != EventType.Repaint) return;
            for (int i = 0; i < 2; i++)
            {
                float half = (rect.width - 6) * .5f;
                var side = new Rect(rect.x + i * (half + 6), rect.y, half, rect.height);
                var material = materials[i];
                if (material == null || material.shader == null || !material.shader.isSupported)
                {
                    EditorGUI.DrawRect(side, new Color(.12f, .13f, .15f));
                    GUI.Label(side, material == null ? "Not in this version" : "Shader missing or unsupported", EditorStyles.centeredGreyMiniLabel);
                    continue;
                }
                var preview = previews[i];
                preview.BeginPreview(side, GUIStyle.none);
                preview.camera.aspect = side.width / side.height;
                preview.camera.transform.SetPositionAndRotation(new Vector3(0, 0, -distance), Quaternion.identity);
                preview.DrawMesh(sphere, Matrix4x4.Rotate(Quaternion.Euler(orbit.x, orbit.y, 0)), material, 0);
                preview.Render(true);
                GUI.DrawTexture(side, preview.EndPreview(), ScaleMode.StretchToFill, false);
            }
        }

        private void Dispose()
        {
            disposed = true;
            EditorApplication.update -= Poll;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            for (int i = 0; i < 2; i++)
            {
                previews[i]?.Cleanup(); previews[i] = null;
                if (materials[i] != null) Object.DestroyImmediate(materials[i]);
                materials[i] = null;
            }
        }
    }
}
