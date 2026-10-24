using UnityEngine;
using UnityEngine.UIElements;
using Orbiters.Mixer.Editor;

namespace Orbiters.Logger.Editor
{
    internal enum LoggerGlyph
    {
        Error,
        Warning,
        Info,
        Search,
        Stack,
        Trash,
        Sliders,
        Copy,
        CopyStack,
        Open,
        Target,
        EyeOff,
        Eye,
        Filter,
        Groups,
        List,
        Bulb,
        Chart,
        Play,
        Stop,
        Compile,
        Reload,
        Build,
        ArrowDown,
        Close,
        Chevron,
        Check,
        Clock,
        External,
        Thread,
        Pause,
        Stopwatch,
        Commit,
        Upload,
        Undo,
        Redo,
        Cloud,
        Lifebuoy,
        Sparkle,
        Branch,
        Hierarchy,
        Package,
        Folder,
        Shield,
        Logo
    }

    /// <summary>
    /// A line icon drawn with the vector API in a 24 x 24 box, crisp at any size. Its colour and stroke come from the USS
    /// custom properties <c>--icon-color</c> and <c>--icon-width</c>, so hover and selected states restyle it like text.
    /// </summary>
    internal sealed class LoggerIcon : VisualElement
    {
        private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--icon-color");
        private static readonly CustomStyleProperty<float> WidthProperty = new CustomStyleProperty<float>("--icon-width");
        private LoggerGlyph glyph;
        private Color color = new Color(0.8f, 0.8f, 0.8f);
        private float strokeWidth = 1.9f;

        public LoggerIcon(LoggerGlyph glyph)
        {
            this.glyph = glyph;
            AddToClassList("lg-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                bool dirty = false;
                if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color)
                {
                    color = resolved;
                    dirty = true;
                }

                if (customStyle.TryGetValue(WidthProperty, out float width) && !Mathf.Approximately(width, strokeWidth))
                {
                    strokeWidth = width;
                    dirty = true;
                }

                if (dirty)
                {
                    MarkDirtyRepaint();
                }
            });
        }

        public LoggerGlyph Glyph
        {
            get => glyph;
            set
            {
                if (glyph == value)
                {
                    return;
                }

                glyph = value;
                MarkDirtyRepaint();
            }
        }

        public static LoggerGlyph ForLevel(LogLevel level) =>
            level == LogLevel.Error ? LoggerGlyph.Error : level == LogLevel.Warning ? LoggerGlyph.Warning : LoggerGlyph.Info;

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float s = Mathf.Min(rect.width, rect.height) / 24f;
            if (s <= 0f)
            {
                return;
            }

            var origin = new Vector2(rect.x + (rect.width - 24f * s) * 0.5f, rect.y + (rect.height - 24f * s) * 0.5f);
            Vector2 P(float x, float y) => origin + new Vector2(x, y) * s;
            var p = context.painter2D;
            p.strokeColor = color;
            p.fillColor = color;
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;
            p.lineWidth = strokeWidth * s;

            void Line(params Vector2[] points)
            {
                p.BeginPath();
                p.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++)
                {
                    p.LineTo(points[i]);
                }

                p.Stroke();
            }

            void Closed(params Vector2[] points)
            {
                p.BeginPath();
                p.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++)
                {
                    p.LineTo(points[i]);
                }

                p.ClosePath();
                p.Stroke();
            }

            void Filled(params Vector2[] points)
            {
                p.BeginPath();
                p.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++)
                {
                    p.LineTo(points[i]);
                }

                p.ClosePath();
                p.Fill();
            }

            void Circle(float x, float y, float r, bool fill = false)
            {
                p.BeginPath();
                p.Arc(P(x, y), r * s, 0f, 360f);
                if (fill)
                {
                    p.Fill();
                }
                else
                {
                    p.Stroke();
                }
            }

            void Dot(float x, float y, float r) => Circle(x, y, r, true);

            void Rounded(float x, float y, float w, float h, float r, bool fill = false)
            {
                float rr = r * s;
                p.BeginPath();
                p.MoveTo(P(x + r, y));
                p.LineTo(P(x + w - r, y));
                p.Arc(P(x + w - r, y + r), rr, 270f, 360f);
                p.LineTo(P(x + w, y + h - r));
                p.Arc(P(x + w - r, y + h - r), rr, 0f, 90f);
                p.LineTo(P(x + r, y + h));
                p.Arc(P(x + r, y + h - r), rr, 90f, 180f);
                p.LineTo(P(x, y + r));
                p.Arc(P(x + r, y + r), rr, 180f, 270f);
                p.ClosePath();
                if (fill)
                {
                    p.Fill();
                }
                else
                {
                    p.Stroke();
                }
            }

            switch (glyph)
            {
                case LoggerGlyph.Logo:
                    // Filled, not stroked: Mixer's logo, one unit inside the 24 box like the line glyphs.
                    MixerLogo.Fill(p, new Rect(P(1f, 1f), new Vector2(22f, 22f) * s), color);
                    break;
                case LoggerGlyph.Error:
                    // An octagon with an exclamation mark: distinct from the warning triangle and info circle.
                    Closed(P(8.2f, 2.8f), P(15.8f, 2.8f), P(21.2f, 8.2f), P(21.2f, 15.8f), P(15.8f, 21.2f), P(8.2f, 21.2f), P(2.8f, 15.8f), P(2.8f, 8.2f));
                    Line(P(12f, 7.4f), P(12f, 13f));
                    Dot(12f, 16.6f, 1.25f);
                    break;
                case LoggerGlyph.Warning:
                    Closed(P(12f, 3f), P(21.6f, 20f), P(2.4f, 20f));
                    Line(P(12f, 9f), P(12f, 13.6f));
                    Dot(12f, 16.8f, 1.2f);
                    break;
                case LoggerGlyph.Info:
                    Circle(12f, 12f, 9.2f);
                    Line(P(12f, 11f), P(12f, 16.6f));
                    Dot(12f, 7.6f, 1.25f);
                    break;
                case LoggerGlyph.Search:
                    Circle(10.5f, 10.5f, 6.2f);
                    Line(P(15.2f, 15.2f), P(20.5f, 20.5f));
                    break;
                case LoggerGlyph.Stack:
                    Line(P(4f, 6f), P(20f, 6f));
                    Line(P(7f, 12f), P(20f, 12f));
                    Line(P(10f, 18f), P(20f, 18f));
                    break;
                case LoggerGlyph.Trash:
                    Line(P(4f, 6.5f), P(20f, 6.5f));
                    Line(P(9.5f, 6.5f), P(9.5f, 4f), P(14.5f, 4f), P(14.5f, 6.5f));
                    Line(P(6f, 6.5f), P(7f, 20f), P(17f, 20f), P(18f, 6.5f));
                    Line(P(10.2f, 10.5f), P(10.4f, 16.5f));
                    Line(P(13.8f, 10.5f), P(13.6f, 16.5f));
                    break;
                case LoggerGlyph.Sliders:
                    Line(P(4f, 7f), P(20f, 7f));
                    Line(P(4f, 17f), P(20f, 17f));
                    Dot(9f, 7f, 2.6f);
                    Dot(15f, 17f, 2.6f);
                    break;
                case LoggerGlyph.Copy:
                    Rounded(8.5f, 8.5f, 11.5f, 11.5f, 2.2f);
                    // The sheet behind shows only above and left of the front one.
                    Line(P(4.5f, 15.5f), P(4.5f, 6.3f), P(6.3f, 4.5f), P(15.5f, 4.5f));
                    break;
                case LoggerGlyph.CopyStack:
                    Rounded(4f, 4f, 11f, 11f, 2.2f);
                    Line(P(9f, 19.5f), P(20f, 19.5f));
                    Line(P(12f, 15.5f), P(20f, 15.5f));
                    Line(P(18f, 11.5f), P(20f, 11.5f));
                    break;
                case LoggerGlyph.Open:
                    Line(P(9f, 6f), P(4.5f, 12f), P(9f, 18f));
                    Line(P(15f, 6f), P(19.5f, 12f), P(15f, 18f));
                    break;
                case LoggerGlyph.Target:
                    Circle(12f, 12f, 7f);
                    Dot(12f, 12f, 2.2f);
                    Line(P(12f, 2.5f), P(12f, 5f));
                    Line(P(12f, 19f), P(12f, 21.5f));
                    Line(P(2.5f, 12f), P(5f, 12f));
                    Line(P(19f, 12f), P(21.5f, 12f));
                    break;
                case LoggerGlyph.EyeOff:
                case LoggerGlyph.Eye:
                    p.BeginPath();
                    p.MoveTo(P(2.5f, 12f));
                    p.BezierCurveTo(P(6f, 5.5f), P(18f, 5.5f), P(21.5f, 12f));
                    p.BezierCurveTo(P(18f, 18.5f), P(6f, 18.5f), P(2.5f, 12f));
                    p.ClosePath();
                    p.Stroke();
                    Circle(12f, 12f, 2.8f);
                    if (glyph == LoggerGlyph.EyeOff)
                    {
                        Line(P(4f, 4f), P(20f, 20f));
                    }

                    break;
                case LoggerGlyph.Filter:
                    Closed(P(3.5f, 5f), P(20.5f, 5f), P(14f, 12.5f), P(14f, 19f), P(10f, 17f), P(10f, 12.5f));
                    break;
                case LoggerGlyph.Groups:
                    Rounded(3.5f, 4f, 17f, 6f, 1.8f);
                    Rounded(3.5f, 14f, 17f, 6f, 1.8f);
                    break;
                case LoggerGlyph.List:
                    Line(P(4f, 6f), P(20f, 6f));
                    Line(P(4f, 10f), P(20f, 10f));
                    Line(P(4f, 14f), P(20f, 14f));
                    Line(P(4f, 18f), P(20f, 18f));
                    break;
                case LoggerGlyph.Bulb:
                    p.BeginPath();
                    p.MoveTo(P(9f, 16.5f));
                    p.BezierCurveTo(P(9f, 13.5f), P(5.5f, 12.5f), P(5.5f, 9f));
                    p.BezierCurveTo(P(5.5f, 5.2f), P(8.5f, 2.8f), P(12f, 2.8f));
                    p.BezierCurveTo(P(15.5f, 2.8f), P(18.5f, 5.2f), P(18.5f, 9f));
                    p.BezierCurveTo(P(18.5f, 12.5f), P(15f, 13.5f), P(15f, 16.5f));
                    p.ClosePath();
                    p.Stroke();
                    Line(P(9.5f, 19.5f), P(14.5f, 19.5f));
                    Line(P(10.5f, 22f), P(13.5f, 22f));
                    break;
                case LoggerGlyph.Chart:
                    Line(P(5f, 20f), P(5f, 13f));
                    Line(P(10f, 20f), P(10f, 6f));
                    Line(P(15f, 20f), P(15f, 10f));
                    Line(P(20f, 20f), P(20f, 4f));
                    break;
                case LoggerGlyph.Play:
                    Filled(P(7.5f, 4.5f), P(19.5f, 12f), P(7.5f, 19.5f));
                    break;
                case LoggerGlyph.Stop:
                    Rounded(6f, 6f, 12f, 12f, 2f, true);
                    break;
                case LoggerGlyph.Pause:
                    Rounded(6f, 5f, 4f, 14f, 1.2f, true);
                    Rounded(14f, 5f, 4f, 14f, 1.2f, true);
                    break;
                case LoggerGlyph.Compile:
                    // Braces: code.
                    Line(P(9f, 4f), P(7f, 4f), P(6f, 5.5f), P(6f, 10f), P(3.5f, 12f), P(6f, 14f), P(6f, 18.5f), P(7f, 20f), P(9f, 20f));
                    Line(P(15f, 4f), P(17f, 4f), P(18f, 5.5f), P(18f, 10f), P(20.5f, 12f), P(18f, 14f), P(18f, 18.5f), P(17f, 20f), P(15f, 20f));
                    break;
                case LoggerGlyph.Reload:
                    p.BeginPath();
                    p.Arc(P(12f, 12f), 7.5f * s, 40f, 320f);
                    p.Stroke();
                    Filled(P(20.5f, 5f), P(20.5f, 11f), P(14.5f, 9.5f));
                    break;
                case LoggerGlyph.Build:
                    Closed(P(12f, 3f), P(20f, 7.5f), P(20f, 16.5f), P(12f, 21f), P(4f, 16.5f), P(4f, 7.5f));
                    Line(P(4f, 7.5f), P(12f, 12f), P(20f, 7.5f));
                    Line(P(12f, 12f), P(12f, 21f));
                    break;
                case LoggerGlyph.ArrowDown:
                    Line(P(12f, 4.5f), P(12f, 19f));
                    Line(P(6f, 13.5f), P(12f, 19.5f), P(18f, 13.5f));
                    break;
                case LoggerGlyph.Close:
                    Line(P(6.5f, 6.5f), P(17.5f, 17.5f));
                    Line(P(17.5f, 6.5f), P(6.5f, 17.5f));
                    break;
                case LoggerGlyph.Chevron:
                    Line(P(7f, 9.5f), P(12f, 14.5f), P(17f, 9.5f));
                    break;
                case LoggerGlyph.Check:
                    Line(P(5f, 12.5f), P(10f, 17.5f), P(19f, 7f));
                    break;
                case LoggerGlyph.Clock:
                    Circle(12f, 12f, 8.5f);
                    Line(P(12f, 7f), P(12f, 12f), P(15.5f, 14.5f));
                    break;
                case LoggerGlyph.External:
                    Line(P(10f, 5f), P(5f, 5f), P(5f, 19f), P(19f, 19f), P(19f, 14f));
                    Line(P(12f, 12f), P(19.5f, 4.5f));
                    Line(P(14f, 4.5f), P(19.5f, 4.5f), P(19.5f, 10f));
                    break;
                case LoggerGlyph.Stopwatch:
                    Circle(12f, 13.5f, 7.5f);
                    Line(P(12f, 13.5f), P(12f, 9.5f));
                    Line(P(10f, 3f), P(14f, 3f));
                    Line(P(12f, 3f), P(12f, 6f));
                    Line(P(18.2f, 6.6f), P(19.6f, 5.2f));
                    break;
                case LoggerGlyph.Commit:
                    Circle(12f, 12f, 4f);
                    Line(P(2.5f, 12f), P(8f, 12f));
                    Line(P(16f, 12f), P(21.5f, 12f));
                    break;
                case LoggerGlyph.Upload:
                    Line(P(12f, 15.5f), P(12f, 4f));
                    Line(P(7f, 9f), P(12f, 4f), P(17f, 9f));
                    Line(P(4.5f, 15f), P(4.5f, 19.5f), P(19.5f, 19.5f), P(19.5f, 15f));
                    break;
                case LoggerGlyph.Undo:
                case LoggerGlyph.Redo:
                {
                    bool undo = glyph == LoggerGlyph.Undo;
                    float m = undo ? 1f : -1f;
                    float cx = 12f;
                    Vector2 Q(float x, float y) => P(cx + (x - cx) * m, y);
                    p.BeginPath();
                    p.MoveTo(Q(5f, 10f));
                    p.LineTo(Q(14.5f, 10f));
                    p.BezierCurveTo(Q(18.5f, 10f), Q(20.5f, 12.5f), Q(20.5f, 15f));
                    p.BezierCurveTo(Q(20.5f, 17.5f), Q(18.5f, 20f), Q(14.5f, 20f));
                    p.LineTo(Q(9f, 20f));
                    p.Stroke();
                    Line(Q(9f, 6f), Q(5f, 10f), Q(9f, 14f));
                    break;
                }
                case LoggerGlyph.Cloud:
                    p.BeginPath();
                    p.MoveTo(P(7f, 18.5f));
                    p.BezierCurveTo(P(3.5f, 18.5f), P(2.5f, 13.5f), P(6.5f, 12.5f));
                    p.BezierCurveTo(P(6.5f, 7f), P(13.5f, 5f), P(15.5f, 9.5f));
                    p.BezierCurveTo(P(19.5f, 8.5f), P(22.5f, 13f), P(19.5f, 16f));
                    p.BezierCurveTo(P(19f, 17.8f), P(18f, 18.5f), P(16.5f, 18.5f));
                    p.ClosePath();
                    p.Stroke();
                    break;
                case LoggerGlyph.Lifebuoy:
                    Circle(12f, 12f, 9f);
                    Circle(12f, 12f, 4f);
                    Line(P(5.6f, 5.6f), P(9.2f, 9.2f));
                    Line(P(18.4f, 5.6f), P(14.8f, 9.2f));
                    Line(P(5.6f, 18.4f), P(9.2f, 14.8f));
                    Line(P(18.4f, 18.4f), P(14.8f, 14.8f));
                    break;
                case LoggerGlyph.Sparkle:
                    // A four-point star with a small one beside it: what the AI brings.
                    p.BeginPath();
                    p.MoveTo(P(10.5f, 3.5f));
                    p.BezierCurveTo(P(11.2f, 8.6f), P(12.4f, 9.8f), P(17.5f, 10.5f));
                    p.BezierCurveTo(P(12.4f, 11.2f), P(11.2f, 12.4f), P(10.5f, 17.5f));
                    p.BezierCurveTo(P(9.8f, 12.4f), P(8.6f, 11.2f), P(3.5f, 10.5f));
                    p.BezierCurveTo(P(8.6f, 9.8f), P(9.8f, 8.6f), P(10.5f, 3.5f));
                    p.ClosePath();
                    p.Stroke();
                    Filled(P(18f, 14.5f), P(18.7f, 17.3f), P(21.5f, 18f), P(18.7f, 18.7f), P(18f, 21.5f), P(17.3f, 18.7f), P(14.5f, 18f), P(17.3f, 17.3f));
                    break;
                case LoggerGlyph.Branch:
                    Circle(7f, 5f, 2.3f);
                    Circle(7f, 19f, 2.3f);
                    Circle(17f, 7f, 2.3f);
                    Line(P(7f, 7.3f), P(7f, 16.7f));
                    p.BeginPath();
                    p.MoveTo(P(17f, 9.3f));
                    p.BezierCurveTo(P(17f, 13.5f), P(8f, 12.5f), P(7.4f, 16.6f));
                    p.Stroke();
                    break;
                case LoggerGlyph.Hierarchy:
                    Rounded(3f, 3f, 8f, 5.5f, 1.6f);
                    Rounded(12f, 9.5f, 9f, 5f, 1.6f);
                    Rounded(12f, 16f, 9f, 5f, 1.6f);
                    Line(P(7f, 8.5f), P(7f, 18.5f), P(12f, 18.5f));
                    Line(P(7f, 12f), P(12f, 12f));
                    break;
                case LoggerGlyph.Package:
                    Rounded(3.5f, 7f, 17f, 13.5f, 2f);
                    Line(P(3.5f, 7f), P(6f, 3.5f), P(18f, 3.5f), P(20.5f, 7f));
                    Line(P(12f, 3.5f), P(12f, 11f));
                    Line(P(9f, 15.5f), P(15f, 15.5f));
                    break;
                case LoggerGlyph.Folder:
                    Closed(P(3f, 6f), P(3f, 19f), P(21f, 19f), P(21f, 8.5f), P(12f, 8.5f), P(10f, 5f), P(4f, 5f));
                    break;
                case LoggerGlyph.Shield:
                    p.BeginPath();
                    p.MoveTo(P(12f, 2.8f));
                    p.LineTo(P(19.5f, 5.6f));
                    p.BezierCurveTo(P(19.5f, 13f), P(17f, 18.5f), P(12f, 21.2f));
                    p.BezierCurveTo(P(7f, 18.5f), P(4.5f, 13f), P(4.5f, 5.6f));
                    p.ClosePath();
                    p.Stroke();
                    Line(P(8.8f, 12f), P(11.2f, 14.4f), P(15.4f, 9.6f));
                    break;
                case LoggerGlyph.Thread:
                    Line(P(4f, 7f), P(13f, 7f));
                    Line(P(4f, 17f), P(13f, 17f));
                    p.BeginPath();
                    p.MoveTo(P(13f, 7f));
                    p.BezierCurveTo(P(19f, 7f), P(19f, 17f), P(13f, 17f));
                    p.Stroke();
                    Dot(20f, 12f, 1.6f);
                    break;
            }
        }
    }
}
