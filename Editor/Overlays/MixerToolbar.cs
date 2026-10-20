using Orbiters.Toolkit.Editor.XRay;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's switches in the Scene view's Avatar Gizmos toolbar ("Extra" menu): sculpt mode on and off, and the mesh edges
    /// overlay the Toolkit draws, handy while sculpting.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerToolbar
    {
        static MixerToolbar()
        {
            XRayExternalGizmoRegistry.Register("orbiters.mixer.sculpt", "Mixer sculpt",
                () => MixerSceneTool.Mode == MixerMode.Sculpt,
                // As the mode strip does: Sculpt opens the selected mesh in Mixer (or says why it cannot).
                on => { if (on) MixerModes.Enter(MixerMode.Sculpt); else if (MixerSceneTool.Mode == MixerMode.Sculpt) MixerModes.Enter(MixerMode.None); },
                "Sculpt the edited meshes with the Mixer brushes");
            MixerSceneTool.Changed += XRayExternalGizmoRegistry.NotifyChanged;
        }
    }
}
