using System;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>Renders with Blender's engine; filled in the render phase. Holds the link's render results meanwhile.</summary>
    internal static class MixerRenderer
    {
        public static event Action<MixerLinkProtocol.RenderResult> ResultReceived;

        internal static void OnResult(MixerLinkProtocol.RenderResult result)
        {
            if (result == null) return;
            ResultReceived?.Invoke(result);
        }
    }
}
