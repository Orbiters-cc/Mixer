using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Processes;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal sealed partial class MixerWindow
    {
        private void RefreshLinkPill()
        {
            bool connected = MixerLinkService.Connected;
            bool running = MixerLinkService.Running;
            linkPill.EnableInClassList("mx-link-pill--on", connected);
            linkPill.EnableInClassList("mx-link-pill--busy", running && !connected && MixerLinkService.Enabled);
            var peer = MixerLinkService.Peer;
            MixerUi.SetPillText(linkPill, connected ? "Blender " + (peer != null && !string.IsNullOrEmpty(peer.blender) ? peer.blender.Split(' ')[0] : "linked") : running ? "Waiting for Blender" : "Not linked");
            linkPill.tooltip = connected ? "Blender is linked: the scene streams both ways. Click for details." : running ? "Listening on port " + MixerLinkService.Port + ". Open the scene in Blender from here." : "The Blender link is off. Click to turn it on.";
        }
    }

    /// <summary>The Blender link popup: where it stands, open the scene in Blender, what is excluded, the log.</summary>
    internal sealed class MixerLinkPopup : PopupWindowContent
    {
        private VisualElement root, log;
        private Label status, detail, excludedLabel;
        private Button openButton, toggleButton;

        public override Vector2 GetWindowSize() => new Vector2(420f, 360f);
        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            root = editorWindow.rootVisualElement;
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-popup");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            root.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) editorWindow.Close(); }, TrickleDown.TrickleDown);
            var header = MixerUi.Row();
            var icon = new VectorIcon(IconGlyph.Blender); icon.AddToClassList("mx-icon"); icon.style.marginRight = 8;
            header.Add(icon);
            header.Add(MixerUi.Text("Blender link", "mx-popup__title"));
            root.Add(header);
            status = MixerUi.Text("", "mx-popup__detail");
            root.Add(status);
            detail = MixerUi.Text("", "mx-caption");
            root.Add(detail);
            var actions = MixerUi.Row();
            actions.style.marginTop = 6;
            openButton = MixerUi.Pill("Open scene in Blender", OpenInBlender, "primary", IconGlyph.External, "Start Blender on this scene's project; the Mixer extension is installed if needed and the scene streams in.");
            actions.Add(openButton);
            toggleButton = MixerUi.Pill(MixerLinkService.Enabled ? "Turn link off" : "Turn link on", () => { MixerLinkService.Enabled = !MixerLinkService.Enabled; Refresh(); }, "ghost");
            actions.Add(toggleButton);
            root.Add(actions);
            var blenderRow = MixerUi.Row();
            blenderRow.style.marginTop = 8;
            string blender = BlenderInstalls.GetSelectedExecutablePath();
            blenderRow.Add(MixerUi.Text(string.IsNullOrEmpty(blender) ? "Blender: not found" : "Blender: " + blender, "mx-caption"));
            blenderRow.Add(MixerUi.Spacer());
            blenderRow.Add(MixerUi.Pill("Pick…", () => { BlenderInstalls.PickExecutable(); Refresh(); }, "ghost", null, "Choose the Blender executable"));
            root.Add(blenderRow);
            excludedLabel = MixerUi.Text("", "mx-caption");
            root.Add(excludedLabel);
            var excludedRow = MixerUi.Row();
            excludedRow.Add(MixerUi.Pill("Mixer Hierarchy", MixerHierarchyWindow.Open, "ghost", IconGlyph.Hierarchy, "Choose what goes to Blender from the Mixer Hierarchy"));
            excludedRow.Add(MixerUi.Pill("Resend scene", () => MixerLinkService.SendGraph(force: true), "ghost", IconGlyph.Refresh, "Send the whole scene to Blender again"));
            root.Add(excludedRow);
            root.Add(MixerUi.Text("LOG", "mx-card__title"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            log = MixerUi.Box("mx-log");
            scroll.Add(log);
            root.Add(scroll);
            MixerLinkService.Changed += Refresh;
            Refresh();
        }

        public override void OnClose() => MixerLinkService.Changed -= Refresh;

        private void Refresh()
        {
            if (root == null) return;
            bool connected = MixerLinkService.Connected;
            var peer = MixerLinkService.Peer;
            status.text = connected ? "Blender " + (peer?.blender ?? "") + " is linked (extension " + (peer?.extension ?? "?") + ")."
                : MixerLinkService.Running ? "Listening on port " + MixerLinkService.Port + ": Blender connects on its own once it runs the Mixer extension with this scene."
                : MixerLinkService.Enabled ? "Starting the link…" : "The link is off.";
            var scene = SceneManager.GetActiveScene();
            var settings = MixerLinkService.SettingsFor(scene, false);
            string blend = settings != null ? settings.blendPath : null;
            detail.text = !string.IsNullOrEmpty(blend) && File.Exists(blend) ? "Project: " + blend : "No Blender project for " + scene.name + " yet: Open scene in Blender makes one under Assets/Orbiters/Mixer/Blender.";
            int excluded = settings != null ? settings.excludedIds.Count : 0;
            int owned = MixerLinkService.BlenderOwned.Count;
            excludedLabel.text = (excluded == 0 ? "Everything in the scene goes to Blender" : excluded + (excluded == 1 ? " object is" : " objects are") + " kept out of Blender") + (owned > 0 ? " · " + owned + " being edited in Blender" : "") + ".";
            MixerUi.SetPillText(toggleButton, MixerLinkService.Enabled ? "Turn link off" : "Turn link on");
            openButton.SetEnabled(!string.IsNullOrEmpty(BlenderInstalls.GetSelectedExecutablePath()));
            log.Clear();
            foreach (string line in MixerLinkService.Log.Reverse().Take(30)) log.Add(MixerUi.Text(line, "mx-log__line"));
        }

        private void OpenInBlender()
        {
            string error = BlenderProjectLauncher.Open();
            if (error != null) MixerSession.Say(error, true); else MixerSession.Say("Blender is starting on " + SceneManager.GetActiveScene().name + ".");
            Refresh();
        }
    }
}
