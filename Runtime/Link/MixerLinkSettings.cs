using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer
{
    /// <summary>
    /// What a scene sends to Blender, kept as an asset per scene under <c>Assets/Orbiters/Mixer/Link</c>: objects the
    /// user took out of the Blender project (their children go with them) and the general exclusions.
    /// </summary>
    public sealed class MixerLinkSettings : ScriptableObject
    {
        /// <summary>GlobalObjectIds of the objects left out of Blender.</summary>
        public List<string> excludedIds = new List<string>();
        public bool excludeInactive;
        public bool excludeEditorOnly = true;
        /// <summary>Where the Blender project of this scene is (absolute path of the .blend), once made.</summary>
        public string blendPath;
    }
}
