using System;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal sealed partial class MixerWindow
    {
        private readonly MixerRenderer.Settings renderSettings = new MixerRenderer.Settings();
        private SegmentedControl engineControl, sizeControl;
        private Slider samplesSlider;
        private Label samplesValue, renderCaption, renderStatus;
        private ToggleSwitch transparentSwitch;
        private VisualElement renderStage, renderFlash;
        private Image renderImage;
        private Button renderButton, saveAsButton, saveProjectButton;
        private PopupField<string> cameraField;
        private static readonly (string label, int w, int h)[] Sizes = { ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160), ("Square", 2048, 2048), ("Portrait", 1080, 1920) };

        private void BuildRenderPage(VisualElement page)
        {
            var stageCard = MixerUi.Card(null, out var stageBody, "mx-render-card");
            page.Add(stageCard);
            renderStage = MixerUi.Box("mx-stage");
            renderImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            renderImage.AddToClassList("mx-stage__image");
            renderStage.Add(renderImage);
            renderStatus = MixerUi.Text("No render yet.", "mx-stage__status");
            renderStage.Add(renderStatus);
            renderFlash = MixerUi.Box("mx-stage__flash", PickingMode.Ignore);
            renderStage.Add(renderFlash);
            stageBody.Add(renderStage);
            var saveRow = MixerUi.Row();
            saveRow.style.marginTop = 8;
            saveRow.Add(MixerUi.Spacer());
            saveAsButton = MixerUi.Pill("Save as…", () =>
            {
                string path = EditorUtility.SaveFilePanel("Save render", "", "render.png", "png");
                if (!string.IsNullOrEmpty(path) && MixerRenderer.LastPath != null) { File.Copy(MixerRenderer.LastPath, path, true); toasts.Show("Saved " + Path.GetFileName(path)); }
            }, "ghost", IconGlyph.Download, "Save the PNG anywhere");
            saveProjectButton = MixerUi.Pill("Save to project", () => { string p = MixerRenderer.SaveToProject(); if (p != null) toasts.Show("Saved " + p); }, "ghost", IconGlyph.Plus, "Copy the PNG under Assets/Orbiters/Mixer/Renders");
            saveRow.Add(saveAsButton);
            saveRow.Add(saveProjectButton);
            stageBody.Add(saveRow);

            var settings = MixerUi.Card("RENDER WITH BLENDER", out var body);
            page.Add(settings);
            renderCaption = MixerUi.Text("", "mx-caption");
            body.Add(renderCaption);
            engineControl = new SegmentedControl(new[] { "Eevee", "Cycles" }, i => renderSettings.Engine = i == 1 ? "cycles" : "eevee");
            engineControl.AddToClassList("orb-segmented--compact");
            engineControl.SetIndex(0);
            body.Add(MixerUi.Setting("Engine", "Eevee is quick; Cycles traces light", engineControl, first: true));
            sizeControl = new SegmentedControl(Sizes.Select(s => s.label), i => { renderSettings.Width = Sizes[i].w; renderSettings.Height = Sizes[i].h; });
            sizeControl.AddToClassList("orb-segmented--compact");
            sizeControl.SetIndex(0);
            body.Add(MixerUi.Setting("Size", "Pixels of the image", sizeControl));
            samplesSlider = new Slider(8f, 512f) { value = renderSettings.Samples };
            samplesSlider.AddToClassList("mx-slider");
            samplesValue = MixerUi.Text(renderSettings.Samples.ToString(), "mx-chip mx-chip__text");
            samplesSlider.RegisterValueChangedCallback(evt => { renderSettings.Samples = Mathf.RoundToInt(evt.newValue); samplesValue.text = renderSettings.Samples.ToString(); });
            body.Add(MixerUi.Setting("Samples", "More is cleaner and slower", Pair(samplesSlider, samplesValue)));
            cameraField = new PopupField<string>(new System.Collections.Generic.List<string> { "Scene view" }, 0);
            cameraField.AddToClassList("mx-popupfield");
            cameraField.RegisterValueChangedCallback(evt => renderSettings.Camera = evt.newValue == "Scene view" ? null : UnityEngine.Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c.name == evt.newValue));
            body.Add(MixerUi.Setting("Camera", "The Scene view's eye, or a camera of the scene", cameraField));
            transparentSwitch = new ToggleSwitch(false, on => renderSettings.Transparent = on);
            body.Add(MixerUi.Setting("Transparent background", "No sky behind the objects", transparentSwitch));
            var actions = MixerUi.Row();
            actions.Add(MixerUi.Spacer());
            renderButton = MixerUi.Pill("Render", StartRender, "primary", IconGlyph.Render, "Render with Blender: over the link when Blender is open, else with a background Blender");
            actions.Add(renderButton);
            actions.style.marginTop = 8;
            body.Add(actions);
            MixerRenderer.Changed += OnRenderChanged;
        }

        private void StartRender()
        {
            string error = MixerRenderer.Render(renderSettings);
            if (error != null) { toasts.Show(error, true); return; }
            toasts.Show(MixerLinkService.Connected ? "Blender is rendering…" : "A background Blender is starting to render…");
            chromeDirty = true;
        }

        private void OnRenderChanged()
        {
            chromeDirty = true;
            var job = MixerRenderer.Current;
            if (job != null && job.Done && job.Ok && renderFlash != null)
            {
                renderFlash.AddToClassList("mx-stage__flash--on");
                renderFlash.schedule.Execute(() => renderFlash.RemoveFromClassList("mx-stage__flash--on")).StartingIn(60);
                WakeGlow();
            }
        }

        private void RefreshRenderPage()
        {
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>().Where(c => c != null && c.gameObject.scene.IsValid()).Select(c => c.name).Distinct().ToList();
            cameras.Insert(0, "Scene view");
            if (!cameras.SequenceEqual(cameraField.choices)) cameraField.choices = cameras;
            var job = MixerRenderer.Current;
            bool running = job != null && !job.Done;
            renderButton.SetEnabled(!running);
            MixerUi.SetPillText(renderButton, running ? "Rendering…" : "Render");
            renderCaption.text = MixerLinkService.Connected ? "Blender is linked: it renders the scene as it holds it."
                : "No Blender is linked: a background Blender takes the scene from the link, renders it and quits (the Mixer extension must have been installed once by Open scene in Blender).";
            if (MixerRenderer.LastImage != null) { renderImage.image = MixerRenderer.LastImage; renderImage.style.display = DisplayStyle.Flex; }
            else renderImage.style.display = DisplayStyle.None;
            renderStatus.text = running ? "Rendering with Blender" + (job.Background ? " in the background" : "") + "… " + (DateTime.Now - job.Started).TotalSeconds.ToString("0") + " s"
                : job != null && job.Done && !job.Ok ? "Failed: " + job.Error
                : MixerRenderer.LastImage != null ? MixerRenderer.LastImage.width + " × " + MixerRenderer.LastImage.height : "No render yet.";
            renderStatus.EnableInClassList("mx-stage__status--over", MixerRenderer.LastImage != null);
            saveAsButton.SetEnabled(MixerRenderer.LastImage != null);
            saveProjectButton.SetEnabled(MixerRenderer.LastImage != null);
            if (running) root.schedule.Execute(() => chromeDirty = true).StartingIn(1000);
        }
    }
}
