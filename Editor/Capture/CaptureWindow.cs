using System;
using System.Collections.Generic;
using System.IO;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>
    /// Mixer's capture simulator: flies a camera through the open scene (a recorded Scene view flight, a path through
    /// waypoints, or an orbit), renders it the way VRChat's Stream camera sees the capture avatar (strip, depth frames,
    /// optional PPv2) and writes a raw capture with ground truth for Orbiters desktop, then checks a sample of it.
    /// Same look as the Mixer window (mixer.uss): top bar with the path kinds, a stage, cards, a footer.
    /// </summary>
    internal sealed partial class CaptureWindow : EditorWindow, IHasCustomMenu
    {
        internal const string MenuPath = "Tools/Orbiters/Mixer Capture";
        private const string MixerSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        private const string CaptureSheetPath = MixerInfo.BasePath + "/Editor/Capture/capture.uss";
        private static readonly string[] PathNames = { "Scene view", "Waypoints", "Orbit" };
        private static readonly IconGlyph[] PathIcons = { IconGlyph.Eye, IconGlyph.Branch, IconGlyph.Refresh };

        [SerializeField] private CaptureSettings settings = new CaptureSettings();
        [SerializeField] private OrbitSettings orbit = new OrbitSettings();
        [SerializeField] private List<CaptureWaypoint> waypoints = new List<CaptureWaypoint>();
        [SerializeField] private bool loopWaypoints;
        [SerializeField] private SceneViewRecording recording = new SceneViewRecording();
        [SerializeField] private bool orbitFitted;

        private VisualElement root, stage, progressFill, flash, settingsBox;
        private Image stageImage;
        private Label stageStatus, summary, footerText, footerRight;
        private SegmentedControl pathControl;
        private Button captureButton, previewButton, openButton;
        private MixerToasts toasts;
        private RenderTexture preview;
        private CaptureRun watched;
        private bool built, visible = true, dirty = true;

        [MenuItem(MenuPath, priority = 2001)]
        public static void Open()
        {
            var window = GetWindow<CaptureWindow>();
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Capture", MixerLogo.TabIcon(), "Mixer capture simulator");
            minSize = new Vector2(520f, 360f);
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyChanged += MarkPathDirty;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.hierarchyChanged -= MarkPathDirty;
            StopRecording();
            Watch(null);
            if (preview != null)
            {
                preview.Release();
                DestroyImmediate(preview);
                preview = null;
            }
        }

        private void OnBecameVisible() { visible = true; dirty = true; }
        private void OnBecameInvisible() => visible = false;

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open the captures folder"), false, OpenOutputRoot);
            menu.AddItem(new GUIContent("Change the captures folder…"), false, ChooseOutputRoot);
            if (!string.IsNullOrWhiteSpace(settings.OutputRoot))
                menu.AddItem(new GUIContent("Use Documents/Orbiters/Raw captures"), false, () => { settings.OutputRoot = ""; dirty = true; });
            menu.AddItem(new GUIContent("Open Mixer"), false, () => EditorApplication.ExecuteMenuItem(MixerInfo.MenuPath));
            menu.AddItem(new GUIContent("About Mixer…"), false, MixerAboutWindow.Open);
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-capture");
            foreach (var path in new[] { MixerSheetPath, CaptureSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }

            BuildTopBar();
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("mx-body");
            root.Add(scroll);
            BuildStage(scroll);
            settingsBox = MixerUi.Box("mx-capture-settings");
            scroll.Add(settingsBox);
            BuildPathCard(settingsBox);
            BuildFramesCard(settingsBox);
            BuildPostCard(settingsBox);
            BuildCheckCard(scroll);
            BuildFooter();
            toasts = new MixerToasts();
            root.Add(toasts);
            root.RegisterCallback<GeometryChangedEvent>(_ => ApplyWidthClasses());
            root.schedule.Execute(Tick).Every(100);
            built = true;
            if (!orbitFitted) FitOrbit(false);
            Watch(CaptureRun.Current);
            SelectPath((int)settings.Path, false);
            Refresh();
        }

        private void BuildTopBar()
        {
            var bar = MixerUi.Box("mx-topbar");
            root.Add(bar);
            var brand = MixerUi.Box("mx-brand");
            var mark = MixerUi.Box("mx-brand__mark");
            var logo = new MixerLogo.Element();
            logo.AddToClassList("mx-brand__logo");
            mark.Add(logo);
            brand.Add(mark);
            brand.Add(MixerUi.Text("Capture", "mx-brand__name"));
            bar.Add(brand);
            var options = new List<SegmentedControl.Option>();
            for (int i = 0; i < PathNames.Length; i++) options.Add(new SegmentedControl.Option(PathNames[i], PathIcons[i]));
            pathControl = new SegmentedControl(options, i => SelectPath(i, true));
            pathControl.AddToClassList("mx-capture-modes");
            var center = MixerUi.Box("mx-topbar__center");
            center.Add(pathControl);
            bar.Add(center);
            bar.Add(MixerUi.Spacer());
            bar.Add(MixerUi.Icon(IconGlyph.External, "Open the captures folder", OpenOutputRoot));
            bar.Add(MixerUi.Icon(IconGlyph.Settings, "Orbiters settings", OrbitersSettingsWindow.Open));
        }

        private void BuildStage(VisualElement parent)
        {
            var card = MixerUi.Card(null, out var body, "mx-render-card");
            parent.Add(card);
            stage = MixerUi.Box("mx-stage mx-capture-stage");
            stageImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            stageImage.AddToClassList("mx-stage__image");
            stage.Add(stageImage);
            stageStatus = MixerUi.Text("Preview shows the first frame; Capture renders them all.", "mx-stage__status");
            stage.Add(stageStatus);
            var bar = MixerUi.Box("mx-capture-progress", PickingMode.Ignore);
            progressFill = MixerUi.Box("mx-capture-progress__fill", PickingMode.Ignore);
            bar.Add(progressFill);
            stage.Add(bar);
            flash = MixerUi.Box("mx-stage__flash", PickingMode.Ignore);
            stage.Add(flash);
            body.Add(stage);
            var under = MixerUi.Row("mx-capture-under");
            summary = MixerUi.Text("", "mx-caption mx-capture-summary");
            under.Add(summary);
            under.Add(MixerUi.Spacer());
            previewButton = MixerUi.Pill("Preview", RenderPreview, "ghost", IconGlyph.Eye, "Render the first frame of the path with the strip, as a capture would");
            under.Add(previewButton);
            body.Add(under);
        }

        private void BuildFooter()
        {
            var footer = MixerUi.Box("mx-footer");
            root.Add(footer);
            footerText = MixerUi.Text("", "mx-footer__text");
            footer.Add(footerText);
            footer.Add(MixerUi.Spacer());
            footerRight = MixerUi.Text("", "mx-footer__text mx-footer__text--right");
            footer.Add(footerRight);
            openButton = MixerUi.Pill("Open folder", OpenLastFolder, "ghost", IconGlyph.External, "Show the last capture's folder");
            openButton.AddToClassList("mx-footer__button");
            footer.Add(openButton);
            captureButton = MixerUi.Pill("Capture", CaptureOrCancel, "primary", IconGlyph.Camera, "Render every frame of the path and write the raw capture");
            captureButton.AddToClassList("mx-footer__button");
            footer.Add(captureButton);
        }

        private void ApplyWidthClasses()
        {
            float width = root.resolvedStyle.width;
            root.EnableInClassList("mx-root--m", width < 1180f);
            root.EnableInClassList("mx-root--s", width < 760f);
            root.EnableInClassList("mx-root--xs", width < 620f);
            root.EnableInClassList("mx-root--short", root.resolvedStyle.height < 520f);
        }

        private void Tick()
        {
            if (!built || !visible) return;
            if (watched != CaptureRun.Current) Watch(CaptureRun.Current);
            if (recordingNow || (watched != null && watched.Busy) || pathDirty) dirty = true;
            if (!dirty) return;
            dirty = false;
            Refresh();
        }

        private void Refresh()
        {
            if (!built) return;
            var run = watched;
            bool busy = run != null && run.Busy;
            settingsBox.SetEnabled(!busy);
            pathControl.SetEnabled(!busy);
            previewButton.SetEnabled(!busy && !recordingNow);
            MixerUi.SetPillText(captureButton, busy ? "Cancel" : "Capture");
            captureButton.EnableInClassList("mx-button--primary", !busy);
            captureButton.EnableInClassList("mx-button--danger", busy);
            captureButton.SetEnabled(!recordingNow);
            openButton.SetEnabled(run != null && Directory.Exists(run.Folder));

            bool hasPath = TryBuildPath(out var poses, out string problem);
            int frames = hasPath ? poses.Length : 0;
            int depthFrames = 0;
            for (int i = 0; i < frames; i++) if (settings.IsDepthFrame(i)) depthFrames++;
            summary.text = hasPath
                ? frames + " frames · " + (frames / settings.Fps).ToString("0.0") + " s · " + MixerUi.Metres(CapturePaths.Length(poses)) + " of path" + (depthFrames > 0 ? " · " + depthFrames + " depth frames" : "")
                : problem;
            summary.EnableInClassList("mx-capture-summary--problem", !hasPath);
            footerText.text = settings.Width + " × " + settings.Height + " · " + settings.Fps.ToString("0") + " fps · " +
                              (settings.Post == CapturePost.None ? "no post" : CaptureSettings.PostLabel(settings.Post) + " post") + " · → " + DisplayRoot();

            float fraction = 0f;
            if (run != null)
            {
                switch (run.State)
                {
                    case CaptureRun.Phase.Rendering:
                        fraction = 0.85f * run.Rendered / Mathf.Max(1, run.FrameCount);
                        stageStatus.text = "Rendering " + run.Rendered + " / " + run.FrameCount + (run.Settings.IsDepthFrame(Mathf.Max(0, run.Rendered - 1)) ? " · depth frame" : "");
                        break;
                    case CaptureRun.Phase.Writing:
                        fraction = 0.85f + 0.1f * run.Written / Mathf.Max(1, run.Rendered);
                        stageStatus.text = "Writing " + run.Written + " / " + run.Rendered;
                        break;
                    case CaptureRun.Phase.Checking:
                        fraction = 0.95f + 0.05f * run.CheckDone / Mathf.Max(1, run.CheckTotal);
                        stageStatus.text = "Checking " + run.CheckDone + " / " + run.CheckTotal + " frames";
                        break;
                    case CaptureRun.Phase.Done:
                        fraction = 1f;
                        stageStatus.text = run.FrameCount + " frames · " + MixerUi.Bytes(run.Bytes) + " · " + run.Elapsed.TotalSeconds.ToString("0.0") + " s";
                        break;
                    case CaptureRun.Phase.Cancelled:
                        stageStatus.text = "Cancelled after " + run.Rendered + (run.Rendered == 1 ? " frame" : " frames") + " (kept)";
                        break;
                    case CaptureRun.Phase.Failed:
                        stageStatus.text = "Failed: " + run.Error;
                        break;
                }
                footerRight.text = busy ? run.Elapsed.TotalSeconds.ToString("0") + " s" : "";
            }
            else
            {
                footerRight.text = "";
            }
            progressFill.style.width = Length.Percent(fraction * 100f);
            MixerUi.Show(progressFill.parent, busy);
            stageStatus.EnableInClassList("mx-stage__status--over", stageImage.image != null);
            RefreshPathCard();
            RefreshFramesCard();
            RefreshPostCard();
            RefreshCheckCard();
        }

        private void Watch(CaptureRun run)
        {
            if (watched != null)
            {
                watched.FrameRendered -= OnFrameRendered;
                watched.Changed -= OnRunChanged;
            }
            watched = run;
            if (watched != null)
            {
                watched.FrameRendered += OnFrameRendered;
                watched.Changed += OnRunChanged;
            }
            dirty = true;
        }

        private void OnFrameRendered(RenderTexture target) => ShowFrame(target);

        private void OnRunChanged(CaptureRun run)
        {
            dirty = true;
            if (toasts == null) return;
            switch (run.State)
            {
                case CaptureRun.Phase.Done:
                    toasts.Show(run.Check != null && run.Check.Passed ? "Capture written and checked: every sampled frame decodes exactly." : "Capture written; the self-check found problems (see Self-check).", run.Check == null || !run.Check.Passed);
                    Flash();
                    break;
                case CaptureRun.Phase.Failed:
                    toasts.Show("Capture failed: " + run.Error, true);
                    break;
                case CaptureRun.Phase.Cancelled:
                    toasts.Show("Capture cancelled: " + run.Rendered + (run.Rendered == 1 ? " frame" : " frames") + " kept.");
                    break;
            }
        }

        private void ShowFrame(RenderTexture target)
        {
            if (target == null) return;
            int width = 960, height = Mathf.Max(1, Mathf.RoundToInt(960f * target.height / target.width));
            if (preview == null || preview.width != width || preview.height != height)
            {
                if (preview != null)
                {
                    preview.Release();
                    DestroyImmediate(preview);
                }
                preview = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "Mixer capture preview", hideFlags = HideFlags.HideAndDontSave,
                };
                preview.Create();
            }
            Graphics.Blit(target, preview);
            if (stageImage != null)
            {
                stageImage.image = preview;
                stageImage.MarkDirtyRepaint();
            }
        }

        private void Flash()
        {
            if (flash == null) return;
            flash.AddToClassList("mx-stage__flash--on");
            flash.schedule.Execute(() => flash.RemoveFromClassList("mx-stage__flash--on")).StartingIn(60);
        }

        private void RenderPreview()
        {
            if (!TryBuildPath(out var poses, out string problem))
            {
                toasts.Show(problem, true);
                return;
            }
            try
            {
                using (var rig = new CaptureRig(settings))
                {
                    rig.Render(0, poses[0], 0f, false);
                    ShowFrame(rig.Target);
                    stageStatus.text = "Preview · frame 1 of " + poses.Length + " · " + rig.PostNote;
                }
            }
            catch (Exception e)
            {
                toasts.Show("Preview failed: " + e.Message, true);
            }
            stageStatus.EnableInClassList("mx-stage__status--over", true);
        }

        private void CaptureOrCancel()
        {
            var run = watched;
            if (run != null && run.Busy)
            {
                run.Cancel();
                return;
            }
            if (!TryBuildPath(out var poses, out string problem))
            {
                toasts.Show(problem, true);
                return;
            }
            var scene = SceneManager.GetActiveScene();
            string folder = CaptureScene.NewCaptureFolder(settings.OutputRoot, scene.name, DateTime.Now);
            try
            {
                Watch(CaptureRun.Start(settings, poses, folder));
                toasts.Show("Capturing " + poses.Length + " frames of " + (string.IsNullOrEmpty(scene.name) ? "the scene" : scene.name) + "…");
            }
            catch (Exception e)
            {
                toasts.Show("Capture could not start: " + e.Message, true);
            }
            Refresh();
        }

        private string DisplayRoot()
        {
            string rootPath = string.IsNullOrWhiteSpace(settings.OutputRoot) ? CaptureScene.DefaultOutputRoot : settings.OutputRoot;
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return rootPath.StartsWith(documents, StringComparison.OrdinalIgnoreCase) ? "Documents" + rootPath.Substring(documents.Length).Replace('\\', '/') : rootPath;
        }

        private void OpenOutputRoot()
        {
            string rootPath = string.IsNullOrWhiteSpace(settings.OutputRoot) ? CaptureScene.DefaultOutputRoot : settings.OutputRoot;
            Directory.CreateDirectory(rootPath);
            EditorUtility.RevealInFinder(rootPath + Path.DirectorySeparatorChar);
        }

        private void ChooseOutputRoot()
        {
            string current = string.IsNullOrWhiteSpace(settings.OutputRoot) ? CaptureScene.DefaultOutputRoot : settings.OutputRoot;
            string picked = EditorUtility.OpenFolderPanel("Folder for raw captures", Directory.Exists(current) ? current : "", "");
            if (string.IsNullOrEmpty(picked)) return;
            settings.OutputRoot = Path.GetFullPath(picked).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ==
                                  Path.GetFullPath(CaptureScene.DefaultOutputRoot).TrimEnd(Path.DirectorySeparatorChar) ? "" : picked;
            dirty = true;
        }

        private void OpenLastFolder()
        {
            if (watched != null && Directory.Exists(watched.Folder)) EditorUtility.RevealInFinder(Path.Combine(watched.Folder, Orbiters.Contracts.RawCapture.FileName));
        }
    }
}
