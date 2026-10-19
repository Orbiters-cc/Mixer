using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The brushes as the Mixer window and the Scene view overlays show them: the weight brushes with their pictures and
    /// help (the sculpt brushes carry theirs), and the radius on its slider.
    /// </summary>
    internal static class MixerBrushTiles
    {
        public static readonly (WeightBrushKind Kind, BrushGlyph Glyph, string Tip)[] WeightBrushes =
        {
            (WeightBrushKind.Draw, BrushGlyph.WeightDraw, "Paint the weight toward the brush value; Ctrl toward 0."),
            (WeightBrushKind.Blur, BrushGlyph.WeightBlur, "Average the weight with the neighbours'."),
            (WeightBrushKind.Replace, BrushGlyph.WeightReplace, "Set the weight to the brush value outright; Ctrl to 0."),
        };

        public static BrushGlyph GlyphOf(WeightBrushKind kind)
        {
            foreach (var brush in WeightBrushes) if (brush.Kind == kind) return brush.Glyph;
            return BrushGlyph.WeightDraw;
        }

        /// <summary>The falloffs' names, in the order of <see cref="Falloff"/> (Constant is Blender's Flat).</summary>
        public static readonly string[] FalloffNames = { "Smooth", "Sphere", "Linear", "Sharp", "Flat" };

        public static string NameOf(Falloff falloff) => FalloffNames[(int)falloff];

        // Radius on a log scale from 1 mm to 2 m.
        public static float RadiusToSlider(float radius) => Mathf.InverseLerp(Mathf.Log10(0.001f), Mathf.Log10(2f), Mathf.Log10(Mathf.Clamp(radius, 0.001f, 2f)));
        public static float SliderToRadius(float value) => Mathf.Pow(10f, Mathf.Lerp(Mathf.Log10(0.001f), Mathf.Log10(2f), value));
    }
}
