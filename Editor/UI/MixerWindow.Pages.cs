using Orbiters.Toolkit.Editor;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // The pages of the modes that come in later phases: a short card each, filled as the phase lands.
    internal sealed partial class MixerWindow
    {
        private VisualElement armatureBody, joinBody, renderBody;

        private void BuildArmaturePage(VisualElement page)
        {
            page.Add(MixerUi.Card("ARMATURE", out armatureBody));
            armatureBody.Add(MixerUi.Text("Edit the rest pose, add and reparent bones. Coming in a later phase.", "mx-caption"));
        }

        private void BuildJoinPage(VisualElement page)
        {
            page.Add(MixerUi.Card("JOIN MESHES", out joinBody));
            joinBody.Add(MixerUi.Text("Join the selected meshes into one. Coming in a later phase.", "mx-caption"));
        }

        private void BuildRenderPage(VisualElement page)
        {
            page.Add(MixerUi.Card("RENDER WITH BLENDER", out renderBody));
            renderBody.Add(MixerUi.Text("Render the scene with Blender's engine. Coming in a later phase.", "mx-caption"));
        }

        private void RefreshArmaturePage(EditTarget target) { }
        private void RefreshJoinPage() { }
        private void RefreshRenderPage() { }
        private void RefreshLinkPill()
        {
            MixerUi.SetPillText(linkPill, "Not linked");
        }
    }

    /// <summary>The Blender link popup: where the link stands; filled in the link phase.</summary>
    internal sealed class MixerLinkPopup : UnityEditor.PopupWindowContent
    {
        public override UnityEngine.Vector2 GetWindowSize() => new UnityEngine.Vector2(360f, 160f);
        public override void OnGUI(UnityEngine.Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-popup");
            var sheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            root.Add(MixerUi.Text("Blender link", "mx-popup__title"));
            root.Add(MixerUi.Text("The live link to Blender arrives in the link phase: the scene streams to a Blender project and edits come back as they happen.", "mx-popup__detail"));
            root.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == UnityEngine.KeyCode.Escape) editorWindow.Close(); }, TrickleDown.TrickleDown);
        }
    }

    /// <summary>About Mixer: version and licenses.</summary>
    internal sealed class MixerAboutWindow : OrbitersAboutWindow
    {
        internal static void Open() => Open<MixerAboutWindow>("About Mixer");

        protected override Product Describe() => new Product
        {
            Name = "Mixer",
            Author = "Enzo DUTRA / blackorbit",
            Version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MixerAboutWindow).Assembly)?.version,
            LicensePath = MixerInfo.BasePath + "/LICENSE.md",
            Caption = "Mixer brings Blender's tools into Unity and links to the real Blender.",
            Logo = () =>
            {
                var logo = new MixerLogo.Element { Color = new UnityEngine.Color(0.235f, 0.949f, 0.604f), Cut = new UnityEngine.Color(0.11f, 0.118f, 0.137f) };
                logo.style.width = 80; logo.style.height = 80;
                return logo;
            },
        };
    }
}
