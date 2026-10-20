using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// A session's bind poses where Unity's Undo can see them: recorded in the same undo step as the bone move or the
    /// added bone that changed them, so Ctrl+Z gives the bones and the bind poses back together, also after a script
    /// reload (Mixer's own history does not survive one, Unity's does). Hidden, never saved, one per edited mesh; in a file
    /// of its own so Unity finds its class again after a reload.
    /// </summary>
    internal sealed class MixerBindPoseHolder : ScriptableObject
    {
        /// <summary>The session's <see cref="EditTarget.Id"/>.</summary>
        public string targetId;
        public Matrix4x4[] bindPoses = new Matrix4x4[0];
    }
}
