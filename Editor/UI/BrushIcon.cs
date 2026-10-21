using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal enum BrushGlyph { Grab, Smooth, Inflate, Bump, Flatten, Clay, Pinch, Crease, Mask, WeightDraw, WeightBlur, WeightReplace }

    /// <summary>
    /// A brush's picture, after Blender's tool icons: a cut through the surface with what the brush does to it (the
    /// surface before as a faint line, moves as arrows), in the icon colour (<c>--icon-color</c>). Weight brushes show a
    /// patch of skin in the weight colours of the Scene view overlay.
    /// </summary>
    internal sealed class BrushIcon : VisualElement
    {
        private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--icon-color");
        private Color color = new Color(0.77f, 0.77f, 0.77f);
        private BrushGlyph glyph;

        public BrushGlyph Glyph
        {
            get => glyph;
            set { if (glyph == value) return; glyph = value; MarkDirtyRepaint(); }
        }

        public BrushIcon(BrushGlyph glyph)
        {
            this.glyph = glyph;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; MarkDirtyRepaint(); }
            });
        }

        private void Draw(MeshGenerationContext context)
        {
            var pen = Pen.For(context.painter2D, contentRect, color);
            if (pen == null) return;
            switch (glyph)
            {
                case BrushGlyph.Grab:
                    pen.Ghost(p => { p.M(6, 19); p.L(19, 19); });
                    pen.Ground(p => { p.M(2, 19); p.L(6, 19); p.C(9, 19, 11, 9, 14, 8); p.C(16.5f, 7.5f, 16.5f, 19, 19, 19); p.L(22, 19); });
                    pen.Dot(14.4f, 8.1f, 1.6f);
                    pen.Arrow(15.6f, 6.6f, 20.5f, 2f);
                    break;
                case BrushGlyph.Smooth:
                    pen.Ghost(p => { p.M(2, 12.5f); p.L(4, 9); p.L(6, 13.5f); p.L(8, 9.5f); p.L(10, 13); p.L(12, 8.5f); p.L(14, 13.5f); p.L(16, 10); p.L(18, 14); p.L(20, 10.5f); p.L(22, 12.5f); });
                    pen.Ground(p => { p.M(2, 16); p.C(6, 13.5f, 9, 13.5f, 12, 16); p.C(15, 18.5f, 18, 18.5f, 22, 16); });
                    break;
                case BrushGlyph.Inflate:
                    pen.Ground(p => { p.M(2, 19); p.L(6.5f, 19); p.C(6.5f, 11.5f, 17.5f, 11.5f, 17.5f, 19); p.L(22, 19); });
                    pen.Arrow(12, 11, 12, 3.5f);
                    pen.Arrow(7.3f, 13.2f, 3.2f, 9.1f);
                    pen.Arrow(16.7f, 13.2f, 20.8f, 9.1f);
                    break;
                case BrushGlyph.Bump:
                    pen.Ghost(p => { p.M(5, 19); p.L(19, 19); });
                    pen.Ground(p => { p.M(2, 19); p.L(5, 19); p.C(8, 19, 9, 12.5f, 12, 12.5f); p.C(15, 12.5f, 16, 19, 19, 19); p.L(22, 19); });
                    pen.Arrow(12, 10.5f, 12, 3.5f);
                    pen.Arrow(7.5f, 13.5f, 7.5f, 8);
                    pen.Arrow(16.5f, 13.5f, 16.5f, 8);
                    break;
                case BrushGlyph.Flatten:
                    pen.Ghost(p => { p.M(3, 14); p.C(5, 14, 6, 7, 8.5f, 7); p.C(11, 7, 11.5f, 14, 13, 14); p.C(15, 14, 15.5f, 9.5f, 17.5f, 9.5f); p.C(19.5f, 9.5f, 20, 14, 21, 14); });
                    pen.Ground(p => { p.M(2, 14); p.L(22, 14); });
                    pen.Arrow(8.5f, 1.5f, 8.5f, 5.5f);
                    pen.Arrow(17.5f, 3.5f, 17.5f, 7.5f);
                    break;
                case BrushGlyph.Clay:
                    pen.Ground(p => { p.M(2, 19); p.L(22, 19); });
                    pen.Block(5, 15.2f, 14, 3.1f, 1.2f, 1f);
                    pen.Block(7.5f, 11.2f, 9, 3.1f, 1.2f, 1f);
                    pen.Arrow(12, 1.5f, 12, 8.2f);
                    break;
                case BrushGlyph.Pinch:
                    pen.Ground(p => { p.M(2, 19); p.L(8, 19); p.C(10, 19, 11, 10.5f, 12, 9.5f); p.C(13, 10.5f, 14, 19, 16, 19); p.L(22, 19); });
                    pen.Arrow(2.5f, 11.5f, 8.2f, 11.5f);
                    pen.Arrow(21.5f, 11.5f, 15.8f, 11.5f);
                    break;
                case BrushGlyph.Crease:
                    pen.Ghost(p => { p.M(8.5f, 12); p.L(15.5f, 12); });
                    pen.Ground(p => { p.M(2, 12); p.L(8.5f, 12); p.L(12, 19.5f); p.L(15.5f, 12); p.L(22, 12); });
                    pen.Arrow(12, 2, 12, 9.5f);
                    break;
                case BrushGlyph.Mask:
                    pen.Ground(p => { p.M(2, 14); p.L(22, 14); });
                    pen.Hatch(2.6f, 14.8f, 9.6f, 6.2f);
                    pen.Line(p => { p.M(12.5f, 14.5f); p.L(12.5f, 21); }, 1.2f, 0.9f);
                    pen.Dot(7.2f, 8.3f, 2.3f);
                    pen.Line(p => { p.M(8.9f, 6.6f); p.L(13, 2.5f); }, 2.2f, 1f);
                    break;
                case BrushGlyph.WeightDraw:
                    pen.Skin(x => Mathf.Clamp01(1.15f * Mathf.Exp(-Mathf.Pow((x - 12f) / 4.2f, 2f) * 3f)), hard: false);
                    pen.Line(p => { p.M(18.5f, 2); p.L(14.3f, 6.2f); }, 2.4f, 1f);
                    pen.Paint(13, 8.4f, 2.1f, MixerSceneTool.WeightColor(1f));
                    break;
                case BrushGlyph.WeightBlur:
                    pen.Skin(x => 0.75f * Mathf.Exp(-Mathf.Pow((x - 12f) / 7f, 2f)), hard: false);
                    pen.Drop(12, 2, 3.4f, 10.6f);
                    break;
                case BrushGlyph.WeightReplace:
                    pen.Skin(x => x >= 7.5f && x <= 16.5f ? 1f : 0f, hard: true);
                    pen.Arrow(12, 2, 12, 9.6f);
                    break;
            }
        }

        /// <summary>A falloff's curve, as Blender draws its falloff presets: the brush's strength from its edge to its center and back.</summary>
        internal sealed class FalloffCurve : VisualElement
        {
            private Color color = new Color(0.77f, 0.77f, 0.77f);
            private Falloff falloff;

            public Falloff Falloff
            {
                get => falloff;
                set { if (falloff == value) return; falloff = value; MarkDirtyRepaint(); }
            }

            public FalloffCurve()
            {
                pickingMode = PickingMode.Ignore;
                generateVisualContent += context =>
                {
                    var pen = Pen.For(context.painter2D, contentRect, color);
                    pen?.Ground(p =>
                    {
                        const int steps = 24;
                        for (int i = 0; i <= steps; i++)
                        {
                            float u = i / (float)steps * 2f - 1f;
                            float x = 2f + 20f * i / steps, y = 20f - 15f * Brush.Curve(falloff, Mathf.Abs(u));
                            if (i == 0) p.M(x, y); else p.L(x, y);
                        }
                    });
                };
                RegisterCallback<CustomStyleResolvedEvent>(_ =>
                {
                    if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; MarkDirtyRepaint(); }
                });
            }
        }

        // Drawing in a 24 x 24 box scaled to the element: paths are replayed for the fill and the outline.
        private sealed class Pen
        {
            private const float Bottom = 21f;
            private readonly Painter2D painter;
            private readonly Vector2 origin;
            private readonly float scale;
            private readonly Color color;

            private Pen(Painter2D painter, Vector2 origin, float scale, Color color)
            {
                this.painter = painter;
                this.origin = origin;
                this.scale = scale;
                this.color = color;
                painter.lineJoin = LineJoin.Round;
                painter.lineCap = LineCap.Round;
            }

            public static Pen For(Painter2D painter, Rect rect, Color color)
            {
                float scale = Mathf.Min(rect.width, rect.height) / 24f;
                if (scale <= 0f) return null;
                return new Pen(painter, new Vector2(rect.x + (rect.width - 24f * scale) * 0.5f, rect.y + (rect.height - 24f * scale) * 0.5f), scale, color);
            }

            private Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
            private Color Faded(float alpha) => new Color(color.r, color.g, color.b, color.a * alpha);

            public void M(float x, float y) => painter.MoveTo(P(x, y));
            public void L(float x, float y) => painter.LineTo(P(x, y));
            public void C(float x1, float y1, float x2, float y2, float x, float y) => painter.BezierCurveTo(P(x1, y1), P(x2, y2), P(x, y));

            /// <summary>The surface: its top edge from left to right, filled down to the bottom of the cut.</summary>
            public void Ground(Action<Pen> top)
            {
                painter.BeginPath();
                top(this);
                painter.LineTo(P(22, Bottom));
                painter.LineTo(P(2, Bottom));
                painter.ClosePath();
                painter.fillColor = Faded(0.36f);
                painter.Fill();
                Line(top, 1.7f, 1f);
            }

            /// <summary>The surface before the brush, faint.</summary>
            public void Ghost(Action<Pen> path)
            {
                painter.BeginPath();
                path(this);
                painter.strokeColor = Faded(0.5f);
                painter.lineWidth = 1.1f * scale;
                painter.Stroke();
            }

            public void Line(Action<Pen> path, float width, float alpha)
            {
                painter.BeginPath();
                path(this);
                painter.strokeColor = Faded(alpha);
                painter.lineWidth = width * scale;
                painter.Stroke();
            }

            public void Arrow(float x0, float y0, float x1, float y1)
            {
                var from = new Vector2(x0, y0);
                var to = new Vector2(x1, y1);
                var along = (to - from).normalized;
                var side = new Vector2(-along.y, along.x);
                const float head = 2.6f;
                var stem = to - along * 1.2f;
                Line(p => { p.M(from.x, from.y); p.L(stem.x, stem.y); }, 1.7f, 1f);
                var a = to - along * head + side * head * 0.8f;
                var b = to - along * head - side * head * 0.8f;
                painter.BeginPath();
                painter.MoveTo(P(to.x, to.y));
                painter.LineTo(P(a.x, a.y));
                painter.LineTo(P(b.x, b.y));
                painter.ClosePath();
                painter.fillColor = color;
                painter.Fill();
            }

            public void Dot(float x, float y, float radius) => Paint(x, y, radius, color);

            public void Paint(float x, float y, float radius, Color fill)
            {
                painter.BeginPath();
                painter.Arc(P(x, y), radius * scale, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.ClosePath();
                painter.fillColor = fill;
                painter.Fill();
            }

            /// <summary>A rounded strip of material laid on the surface.</summary>
            public void Block(float x, float y, float width, float height, float radius, float alpha)
            {
                painter.BeginPath();
                RoundRect(x, y, width, height, radius);
                painter.fillColor = Faded(alpha);
                painter.Fill();
            }

            private void RoundRect(float x, float y, float width, float height, float radius)
            {
                painter.MoveTo(P(x + radius, y));
                painter.LineTo(P(x + width - radius, y));
                painter.ArcTo(P(x + width, y), P(x + width, y + radius), radius * scale);
                painter.LineTo(P(x + width, y + height - radius));
                painter.ArcTo(P(x + width, y + height), P(x + width - radius, y + height), radius * scale);
                painter.LineTo(P(x + radius, y + height));
                painter.ArcTo(P(x, y + height), P(x, y + height - radius), radius * scale);
                painter.LineTo(P(x, y + radius));
                painter.ArcTo(P(x, y), P(x + radius, y), radius * scale);
                painter.ClosePath();
            }

            /// <summary>Diagonal hatching over a rectangle (the masked part of the surface).</summary>
            public void Hatch(float x, float y, float width, float height)
            {
                painter.BeginPath();
                for (float d = -height; d < width; d += 2.4f)
                {
                    float x0 = x + Mathf.Max(d, 0f), y0 = y + height - Mathf.Max(-d, 0f);
                    float run = Mathf.Min(width - Mathf.Max(d, 0f), height - Mathf.Max(-d, 0f));
                    painter.MoveTo(P(x0, y0));
                    painter.LineTo(P(x0 + run, y0 - run));
                }
                painter.strokeColor = color;
                painter.lineWidth = 1.1f * scale;
                painter.Stroke();
            }

            /// <summary>A patch of skin in the weight colours, the weight across it given by <paramref name="weightAt"/>.</summary>
            public void Skin(Func<float, float> weightAt, bool hard)
            {
                const float top = 13f, bottom = 21f;
                float step = hard ? 0.5f : 1f;
                for (float x = 2f; x < 22f - 1e-3f; x += step)
                {
                    float x1 = Mathf.Min(22f, x + step);
                    var fill = MixerSceneTool.WeightColor(weightAt((x + x1) * 0.5f));
                    painter.BeginPath();
                    painter.MoveTo(P(x, top));
                    painter.LineTo(P(x1 + 0.05f, top));
                    painter.LineTo(P(x1 + 0.05f, bottom));
                    painter.LineTo(P(x, bottom));
                    painter.ClosePath();
                    painter.fillColor = fill;
                    painter.Fill();
                }
                Line(p => { p.M(2, top); p.L(22, top); }, 1.4f, 0.9f);
            }

            /// <summary>A drop, for blurring.</summary>
            public void Drop(float x, float top, float halfWidth, float bottom)
            {
                float mid = bottom - halfWidth;
                painter.BeginPath();
                painter.MoveTo(P(x, top));
                painter.BezierCurveTo(P(x + halfWidth * 0.35f, top + (mid - top) * 0.45f), P(x + halfWidth, mid - halfWidth * 0.55f), P(x + halfWidth, mid));
                painter.BezierCurveTo(P(x + halfWidth, mid + halfWidth * 0.55f), P(x + halfWidth * 0.55f, bottom), P(x, bottom));
                painter.BezierCurveTo(P(x - halfWidth * 0.55f, bottom), P(x - halfWidth, mid + halfWidth * 0.55f), P(x - halfWidth, mid));
                painter.BezierCurveTo(P(x - halfWidth, mid - halfWidth * 0.55f), P(x - halfWidth * 0.35f, top + (mid - top) * 0.45f), P(x, top));
                painter.ClosePath();
                painter.fillColor = color;
                painter.Fill();
            }
        }
    }
}
