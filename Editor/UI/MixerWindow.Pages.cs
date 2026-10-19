using Orbiters.Toolkit.Editor;

namespace Orbiters.Mixer.Editor
{
    // Every mode page lives in its own partial file: Sculpt, Weights, Armature, Join, Render, Link.
    internal sealed partial class MixerWindow
    {
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
