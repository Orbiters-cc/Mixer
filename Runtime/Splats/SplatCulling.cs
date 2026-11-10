using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// The projection pass's culling (SplatProjection.hlsl's <c>SplatOnScreen</c>), the same arithmetic in C# so its
    /// claim can be checked on the CPU: a quad it leaves out has no pixel on screen and no depth the GPU would keep.
    /// </summary>
    internal static class SplatCulling
    {
        /// <summary>
        /// Whether a quad centred at <paramref name="clip"/> (the camera's own projection, OpenGL-style) with corners at
        /// ±<paramref name="axis1"/> ± <paramref name="axis2"/> pixels can cover a pixel of a
        /// <paramref name="screen"/>-sized target: every corner shares the centre's z and w, so the GPU clips the whole
        /// quad when the centre's depth is outside ±w; otherwise the circle through the corners is tested against the
        /// screen's rectangle.
        /// </summary>
        public static bool OnScreen(Vector4 clip, Vector2 axis1, Vector2 axis2, Vector2 screen)
        {
            if (!(clip.w > 0f) || clip.z < -clip.w || clip.z > clip.w)
                return false;
            var centre = new Vector2((clip.x / clip.w * 0.5f + 0.5f) * screen.x, (clip.y / clip.w * 0.5f + 0.5f) * screen.y);
            float reach = Mathf.Sqrt(Vector2.Dot(axis1, axis1) + Vector2.Dot(axis2, axis2));
            return centre.x + reach >= 0f && centre.x - reach <= screen.x && centre.y + reach >= 0f && centre.y - reach <= screen.y;
        }
    }
}
