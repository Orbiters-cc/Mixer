using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// Gaussian splats of a reconstructed world (Orbiters - N4N0's Reconstruct), in Unity's world: imported from a
    /// <c>.splats</c> file (the session's <c>splats.ply</c>) by Mixer's importer and drawn by <see cref="SplatRenderer"/>.
    /// </summary>
    public sealed class SplatCloud : ScriptableObject
    {
        /// <summary>
        /// The most splats Mixer reads and draws: the GPU sort runs 256 splats per thread group over a power-of-two count,
        /// and a dispatch takes at most 65,535 groups in a row.
        /// </summary>
        public const int MaxCount = 1 << 23;

        /// <summary>Centres, metres.</summary>
        public Vector3[] positions = new Vector3[0];

        /// <summary>Orientations of the Gaussians' axes.</summary>
        public Quaternion[] rotations = new Quaternion[0];

        /// <summary>Standard deviations along the rotated axes, metres.</summary>
        public Vector3[] scales = new Vector3[0];

        /// <summary>Colour as captured (encoded sRGB) and opacity.</summary>
        public Color32[] colors = new Color32[0];

        /// <summary>What the training keyframes saw of each splat (Orbiters - N4N0's <c>seen_*</c> PLY properties): the
        /// largest blending weight it reached (negative where unknown), the keyframes that saw it, the compass sectors
        /// they looked from (bits, by the direction from the splat towards the camera, 0 = +Z, clockwise towards +X) and
        /// their finest sampling rate (pixels per metre). Empty where the PLY does not say.</summary>
        public Vector4[] seen = new Vector4[0];

        /// <summary>Whether each splat is on the trainer's sky shell (the PLY's <c>sky</c>). Empty where the PLY does not
        /// say.</summary>
        public bool[] sky = new bool[0];

        /// <summary>How far each splat lies beyond what the capture's depth frames reached, metres (the PLY's
        /// <c>seen_beyond</c>): the far field <see cref="SplatRenderer.farField"/> fogs or crops. Empty where the PLY does
        /// not say.</summary>
        public float[] beyond = new float[0];

        /// <summary>The sky's colour at the horizon (encoded sRGB), from <see cref="skyMap"/>'s band around it, else from
        /// the sky shell's splats; what fog and haze turn to. Meaningful only with <see cref="hasHorizon"/>.</summary>
        public Color horizon;
        public bool hasHorizon;

        /// <summary>The sky trained with the splats (Orbiters - N4N0's <c>sky.png</c>), drawn behind them: an
        /// equirectangular map, longitude <c>atan2(x, z)</c> across from −180°, elevation from +90° at the top, its
        /// encoded sRGB values kept as linear data. Null where the reconstruction keeps its sky as splats on a shell.</summary>
        public Texture2D skyMap;

        /// <summary>Where most of the splats are (the 2nd to 98th percentile per axis, so not the far sky shell): what
        /// framing looks at.</summary>
        public Bounds core;

        /// <summary>Changes with the splats (the importer sets it): a reimport in place, even with the same count, is uploaded again.</summary>
        public string contentHash = "";

        public int Count => positions != null ? positions.Length : 0;
    }
}
