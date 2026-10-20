using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Gaussian splats of a reconstructed world (Orbiters desktop's Reconstruct), in Unity's world: imported from a
    /// <c>.splats</c> file (the session's <c>splats.ply</c>) by Mixer's importer and drawn by <see cref="SplatRenderer"/>.
    /// </summary>
    public sealed class SplatCloud : ScriptableObject
    {
        /// <summary>Centres, metres.</summary>
        public Vector3[] positions = new Vector3[0];

        /// <summary>Orientations of the Gaussians' axes.</summary>
        public Quaternion[] rotations = new Quaternion[0];

        /// <summary>Standard deviations along the rotated axes, metres.</summary>
        public Vector3[] scales = new Vector3[0];

        /// <summary>Colour as captured (encoded sRGB) and opacity.</summary>
        public Color32[] colors = new Color32[0];

        /// <summary>Where most of the splats are (the 2nd to 98th percentile per axis, so not the far sky shell): what
        /// framing looks at.</summary>
        public Bounds core;

        public int Count => positions != null ? positions.Length : 0;
    }
}
