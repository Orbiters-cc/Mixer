using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's logo: a blender jar (its lid, a flaring body, the base) with two blades crossing inside, drawn from
    /// polygons so it fills in one colour at any size and takes the tint of where it sits. The window tab, which only
    /// takes a texture, gets it rasterized at the screen's pixel density in the grey of Unity's own tab icons.
    /// </summary>
    internal static class MixerLogo
    {
        /// <summary>The logo's box (its design grid).</summary>
        public static readonly Vector2 Size = new Vector2(24f, 24f);

        // Closed polygons in the 24 x 24 grid, filled with non-zero winding.
        private static readonly Vector2[][] Shapes =
        {
            // lid
            new[] { new Vector2(6.5f, 2f), new Vector2(17.5f, 2f), new Vector2(17.5f, 4.6f), new Vector2(6.5f, 4.6f) },
            // jar body: wider at the top, narrower at the bottom, with a hole for the blades region (drawn as outline polygon)
            new[] { new Vector2(5f, 5.8f), new Vector2(19f, 5.8f), new Vector2(17.2f, 17.2f), new Vector2(6.8f, 17.2f) },
            // base
            new[] { new Vector2(4.5f, 18.4f), new Vector2(19.5f, 18.4f), new Vector2(19.5f, 22f), new Vector2(4.5f, 22f) },
        };

        // The blades, cut out of the body: drawn in the background colour over it.
        private static readonly Vector2[][] Cuts =
        {
            new[] { new Vector2(8.2f, 8.6f), new Vector2(9.6f, 7.6f), new Vector2(15.8f, 15.2f), new Vector2(14.4f, 16.2f) },
            new[] { new Vector2(15.8f, 7.6f), new Vector2(14.4f, 8.6f), new Vector2(8.2f, 16.2f), new Vector2(9.6f, 15.2f) },
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

        /// <summary>Fills the logo in <paramref name="color"/>, as large as fits <paramref name="box"/>, centred; the blades in <paramref name="cut"/>.</summary>
        public static void Fill(Painter2D painter, Rect box, Color color, Color cut)
        {
            float scale = Mathf.Min(box.width / Size.x, box.height / Size.y);
            if (scale <= 0f) return;
            var origin = box.center - Size * (scale * 0.5f);
            painter.fillColor = color;
            foreach (var shape in Shapes) Polygon(painter, shape, origin, scale);
            painter.fillColor = cut;
            foreach (var shape in Cuts) Polygon(painter, shape, origin, scale);
        }

        private static void Polygon(Painter2D painter, Vector2[] points, Vector2 origin, float scale)
        {
            painter.BeginPath();
            painter.MoveTo(origin + points[0] * scale);
            for (int i = 1; i < points.Length; i++) painter.LineTo(origin + points[i] * scale);
            painter.ClosePath();
            painter.Fill();
        }

        /// <summary>An element that draws the logo in a colour (the brand mark, the About window).</summary>
        public sealed class Element : VisualElement
        {
            public Color Color = new Color(0.235f, 0.949f, 0.604f);
            public Color Cut = new Color(0f, 0.855f, 0.427f, 0.14f);

            public Element()
            {
                pickingMode = PickingMode.Ignore;
                generateVisualContent += ctx => Fill(ctx.painter2D, contentRect, Color, Cut);
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
            float scale = (pixels - 2f * margin) / Size.y;
            var origin = new Vector2((pixels - Size.x * scale) * 0.5f, margin);
            const int subRows = 8;
            var coverage = new float[pixels * pixels];
            Cover(coverage, pixels, Shapes, origin, scale, subRows, 1f);
            Cover(coverage, pixels, Cuts, origin, scale, subRows, -1f);
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

        // Scanline coverage of convex polygons: each sub-row adds the covered span's fraction to its pixels.
        private static void Cover(float[] coverage, int pixels, Vector2[][] shapes, Vector2 origin, float scale, int subRows, float sign)
        {
            foreach (var shape in shapes)
            {
                var pts = new Vector2[shape.Length];
                for (int i = 0; i < shape.Length; i++) pts[i] = origin + shape[i] * scale;
                for (int row = 0; row < pixels * subRows; row++)
                {
                    float y = (row + 0.5f) / subRows;
                    float xMin = float.MaxValue, xMax = float.MinValue;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        var a = pts[i]; var b = pts[(i + 1) % pts.Length];
                        if ((a.y <= y) == (b.y <= y)) continue;
                        float x = a.x + (b.x - a.x) * (y - a.y) / (b.y - a.y);
                        xMin = Mathf.Min(xMin, x); xMax = Mathf.Max(xMax, x);
                    }
                    if (xMin > xMax) continue;
                    int line = row / subRows * pixels;
                    xMin = Mathf.Clamp(xMin, 0f, pixels); xMax = Mathf.Clamp(xMax, 0f, pixels);
                    for (int x = (int)xMin; x < pixels && x < xMax; x++)
                    {
                        float covered = Mathf.Min(xMax, x + 1f) - Mathf.Max(xMin, x);
                        if (covered > 0f) coverage[line + x] += sign * covered / subRows;
                    }
                }
            }
        }
    }
}
