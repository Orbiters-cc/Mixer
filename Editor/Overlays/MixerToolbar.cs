using Orbiters.Toolkit.Editor.XRay;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's switches in the Scene view's XRay toolbar ("Extra" menu): sculpt mode on and off, and the mesh edges
    /// overlay the Toolkit draws, handy while sculpting.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerToolbar
    {
        static MixerToolbar()
        {
            XRayExternalGizmoRegistry.Register("orbiters.mixer.sculpt", "Mixer sculpt",
                () => MixerSceneTool.Mode == MixerMode.Sculpt,
                on => { MixerSceneTool.Mode = on ? MixerMode.Sculpt : MixerMode.None; if (on && !MixerSession.Active) MixerWindow.Open(); },
                "Sculpt the edited meshes with the Mixer brushes");
            MixerSceneTool.Changed += XRayExternalGizmoRegistry.NotifyChanged;
        }
    }
}
