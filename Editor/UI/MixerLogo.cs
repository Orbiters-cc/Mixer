using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's logo: an M whose legs fold in and meet at a point, one shape from the logo's SVG (a 240 x 242 box), so it
    /// fills in one colour at any size and takes the tint of where it sits. The window tab, which only takes a texture,
    /// gets it rasterized at the screen's pixel density in the grey of Unity's own tab icons.
    /// </summary>
    internal static class MixerLogo
    {
        /// <summary>The logo's box (the SVG's view box).</summary>
        public static readonly Vector2 Size = new Vector2(240f, 242f);

        // The SVG's path, y down as in the SVG and in UI Toolkit.
        private static readonly Vector2[] Shape =
        {
            new Vector2(71.5f, 241.5f), new Vector2(0f, 241.5f), new Vector2(92f, 149.5f), new Vector2(50.5f, 112.5f),
            new Vector2(50.5f, 152f), new Vector2(0f, 202.5f), new Vector2(0f, 0f), new Vector2(120f, 101.5f),
            new Vector2(239.5f, 0.5f), new Vector2(239.5f, 202.5f), new Vector2(189f, 152f), new Vector2(189f, 112.5f),
            new Vector2(147.5f, 149.5f), new Vector2(239.5f, 241.5f), new Vector2(168.5f, 241.5f), new Vector2(120f, 193f),
        };

        private static readonly Color DarkSkinTab = new Color(0.78f, 0.78f, 0.78f);
        private static readonly Color LightSkinTab = new Color(0.27f, 0.27f, 0.27f);
        private const float TabPoints = 16f;
        private static readonly Dictionary<int, Texture2D> TabIcons = new Dictionary<int, Texture2D>();

        static MixerLogo() => AssemblyReloadEvents.beforeAssemblyReload += ReleaseTabIcons;

        private static void ReleaseTabIcons()
        {
            foreach (var icon in TabIcons.Values) if (icon != null) Object.DestroyImmediate(icon);
            TabIcons.Clear();
        }

        /// <summary>Fills the logo in <paramref name="color"/>, as large as fits <paramref name="box"/>, centred.</summary>
        public static void Fill(Painter2D painter, Rect box, Color color)
        {
            float scale = Mathf.Min(box.width / Size.x, box.height / Size.y);
            if (scale <= 0f) return;
            var origin = box.center - Size * (scale * 0.5f);
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(origin + Shape[0] * scale);
            for (int i = 1; i < Shape.Length; i++) painter.LineTo(origin + Shape[i] * scale);
            painter.ClosePath();
            painter.Fill(FillRule.NonZero);
        }

        /// <summary>An element that draws the logo in a colour (the brand mark, the About window).</summary>
        public sealed class Element : VisualElement
        {
            public Color Color = new Color(0.235f, 0.949f, 0.604f);

            public Element()
            {
                pickingMode = PickingMode.Ignore;
                generateVisualContent += ctx => Fill(ctx.painter2D, contentRect, Color);
            }
        }

        /// <summary>The window tab's icon: 16 points at the current pixel density, in the skin's tab icon grey.</summary>
        public static Texture2D TabIcon()
        {
            bool proSkin = EditorGUIUtility.isProSkin;
            int pixels = Mathf.Max(16, Mathf.RoundToInt(TabPoints * EditorGUIUtility.pixelsPerPoint));
            int key = pixels * 2 + (proSkin ? 1 : 0);
            if (TabIcons.TryGetValue(key, out var icon) && icon != null) return icon;
            icon = Rasterize(pixels, pixels / TabPoints, proSkin ? DarkSkinTab : LightSkinTab);
            icon.name = "Mixer tab icon";
            TabIcons[key] = icon;
            return icon;
        }

        internal static Texture2D Rasterize(int pixels, float margin, Color color)
        {
            float scale = (pixels - 2f * margin) / Mathf.Max(Size.x, Size.y);
            var origin = new Vector2((pixels - Size.x * scale) * 0.5f, (pixels - Size.y * scale) * 0.5f);
            var coverage = Cover(pixels, origin, scale, 8);
            var texture = new Texture2D(pixels, pixels, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var colors = new Color32[pixels * pixels];
            for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    float alpha = Mathf.Clamp01(coverage[y * pixels + x]) * color.a;
                    colors[(pixels - 1 - y) * pixels + x] = new Color(color.r, color.g, color.b, alpha);
                }
            texture.SetPixels32(colors);
            texture.Apply(false, false);
            return texture;
        }

        // Scanline coverage of the shape (concave: each sub-row fills between pairs of edge crossings, even-odd), each
        // covered span adding its fraction to its pixels.
        private static float[] Cover(int pixels, Vector2 origin, float scale, int subRows)
        {
            var coverage = new float[pixels * pixels];
            var pts = new Vector2[Shape.Length];
            for (int i = 0; i < Shape.Length; i++) pts[i] = origin + Shape[i] * scale;
            var crossings = new List<float>();
            for (int row = 0; row < pixels * subRows; row++)
            {
                float y = (row + 0.5f) / subRows;
                crossings.Clear();
                for (int i = 0; i < pts.Length; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Length];
                    if ((a.y <= y) == (b.y <= y)) continue;
                    crossings.Add(a.x + (b.x - a.x) * (y - a.y) / (b.y - a.y));
                }
                crossings.Sort();
                int line = row / subRows * pixels;
                for (int c = 0; c + 1 < crossings.Count; c += 2)
                {
                    float xMin = Mathf.Clamp(crossings[c], 0f, pixels), xMax = Mathf.Clamp(crossings[c + 1], 0f, pixels);
                    for (int x = (int)xMin; x < pixels && x < xMax; x++)
                    {
                        float covered = Mathf.Min(xMax, x + 1f) - Mathf.Max(xMin, x);
                        if (covered > 0f) coverage[line + x] += covered / subRows;
                    }
                }
            }
            return coverage;
        }
    }
}
