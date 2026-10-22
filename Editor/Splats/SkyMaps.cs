using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// The sky Orbiters desktop trained with a reconstruction's splats (its <c>reconstruction/sky.png</c>, contracts
    /// Docs/session-bundle.md): an equirectangular 8-bit sRGB map behind them, longitude <c>atan2(x, z)</c> across from
    /// −180°, elevation from +90° at the top row, in Unity's world. A cloud keeps it beside its <c>.splats</c> as a
    /// <c>.splatsky</c> file, which <see cref="SplatImporter"/> reads into the cloud.
    /// </summary>
    public static class SkyMaps
    {
        /// <summary>The sky map in a reconstruction's folder (Rust <c>orbiters_core::reconstruction::SKY_FILE</c>).</summary>
        public const string FileName = "sky.png";

        /// <summary>Its copy beside a cloud's <c>.splats</c>: the PNG under Mixer's own extension, so Unity's texture
        /// importer never sees it.</summary>
        public const string Extension = ".splatsky";

        /// <summary>The <c>.splatsky</c> beside the <c>.splats</c> at <paramref name="splats"/>.</summary>
        public static string Beside(string splats) => System.IO.Path.ChangeExtension(splats, Extension);

        /// <summary>
        /// The map as a texture, or null with <paramref name="problem"/> saying why: its encoded sRGB values kept as they
        /// are (linear data), so the renderer filters them and turns them into light as Orbiters desktop composites
        /// them; around in longitude, clamped at the poles, bilinear, without mipmaps.
        /// </summary>
        public static Texture2D Load(byte[] png, out string problem)
        {
            problem = null;
            var decoded = new Texture2D(2, 2, TextureFormat.RGB24, false, true);
            try
            {
                if (png == null || !decoded.LoadImage(png))
                {
                    problem = "it is not a PNG";
                    return null;
                }
                int w = decoded.width, h = decoded.height;
                if (w < 4 || w % 4 != 0 || w != 2 * h)
                {
                    problem = "a sky map is twice as wide as high, not " + w + " × " + h;
                    return null;
                }
                var map = new Texture2D(w, h, TextureFormat.RGB24, false, true)
                {
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0,
                };
                map.SetPixels32(decoded.GetPixels32());
                map.Apply(false, false);
                return map;
            }
            finally
            {
                Object.DestroyImmediate(decoded);
            }
        }

        /// <summary>The sky's colour at the horizon (encoded sRGB), as Orbiters desktop's viewer reads it from a map
        /// (<c>SkyMap::horizon</c>): the mean of the texel rows from 2° below the horizon to 12° above.</summary>
        public static Color Horizon(Texture2D map)
        {
            int w = map.width, h = map.height;
            Color32[] pixels = map.GetPixels32();
            double r = 0, g = 0, b = 0;
            long n = 0;
            for (int y = 0; y < h; y++)
            {
                // A texture's rows run from the bottom: the map's last row, −90°, comes first.
                float elevation = -90f + (y + 0.5f) / h * 180f;
                if (elevation < -2f || elevation > 12f)
                    continue;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = pixels[y * w + x];
                    r += c.r;
                    g += c.g;
                    b += c.b;
                }
                n += w;
            }
            if (n == 0)
                return Color.black;
            return new Color((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0), 1f);
        }
    }
}
