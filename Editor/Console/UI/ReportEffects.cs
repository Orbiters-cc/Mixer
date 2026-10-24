using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// The report window's mark: a glyph in a soft tile, two moons orbiting it on a tilted ring. They drift while the
    /// window waits, race while a report is made, and the tile turns into a check when it is done.
    /// </summary>
    internal sealed class OrbitMark : VisualElement
    {
        private static readonly Color Green = new Color(0f, 0.855f, 0.427f);
        private static readonly Color Violet = new Color(0.706f, 0.612f, 1f);
        private readonly LoggerIcon icon;
        private float speed = 1f;
        private float targetSpeed = 1f;
        private float phase;
        private double last;

        public OrbitMark(LoggerGlyph glyph)
        {
            AddToClassList("rp-orbit");
            pickingMode = PickingMode.Ignore;
            icon = new LoggerIcon(glyph);
            Add(icon);
            generateVisualContent += Draw;
            schedule.Execute(Animate).Every(16);
        }

        /// <summary>1 drifts, 4 races.</summary>
        public float Speed
        {
            set => targetSpeed = value;
        }

        public LoggerGlyph Glyph
        {
            set
            {
                if (icon.Glyph == value)
                {
                    return;
                }

                // A quick shrink, the new glyph, back to size.
                icon.style.scale = new Scale(new Vector3(0.4f, 0.4f, 1f));
                icon.style.opacity = 0f;
                schedule.Execute(() =>
                {
                    icon.Glyph = value;
                    icon.style.scale = new Scale(Vector3.one);
                    icon.style.opacity = 1f;
                }).StartingIn(140);
            }
        }

        private void Animate()
        {
            if (panel == null || resolvedStyle.display == DisplayStyle.None)
            {
                return;
            }

            // Unity in the background: nothing to see, nothing drawn. Drifting, 20 frames a second are plenty; racing
            // (a report being made), every frame.
            if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive)
            {
                last = 0;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            bool drifting = targetSpeed <= 1.5f && Mathf.Abs(speed - targetSpeed) < 0.05f;
            if (drifting && last > 0 && now - last < 0.05d)
            {
                return;
            }

            float dt = last <= 0 ? 0.016f : Mathf.Min(0.1f, (float)(now - last));
            last = now;
            speed = Mathf.Lerp(speed, targetSpeed, 1f - Mathf.Exp(-dt * 3f));
            phase += dt * speed;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0f)
            {
                return;
            }

            var p = context.painter2D;
            var center = rect.center;
            float size = Mathf.Min(rect.width, rect.height);
            float tile = size * 0.64f;

            // The tile, with a faint halo breathing behind it.
            float breath = 0.5f + 0.5f * Mathf.Sin(phase * 1.7f);
            Rounded(p, center, tile + 6f + breath * 4f, (tile + 6f) * 0.32f, new Color(Green.r, Green.g, Green.b, 0.05f + 0.04f * breath));
            Rounded(p, center, tile, tile * 0.3f, new Color(Green.r, Green.g, Green.b, 0.15f));

            // The ring: a tilted ellipse, the part behind the tile drawn first and fainter.
            float rx = size * 0.5f - 2f;
            float ry = size * 0.2f;
            const float tilt = -0.42f;
            Vector2 OnRing(float angle)
            {
                var local = new Vector2(Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry);
                return center + new Vector2(local.x * Mathf.Cos(tilt) - local.y * Mathf.Sin(tilt), local.x * Mathf.Sin(tilt) + local.y * Mathf.Cos(tilt));
            }

            p.lineWidth = 1.2f;
            p.strokeColor = new Color(1f, 1f, 1f, 0.12f);
            p.BeginPath();
            for (int i = 0; i <= 48; i++)
            {
                var point = OnRing(i / 48f * Mathf.PI * 2f);
                if (i == 0)
                {
                    p.MoveTo(point);
                }
                else
                {
                    p.LineTo(point);
                }
            }

            p.Stroke();
            Moon(p, OnRing(phase * 1.6f), 3.6f, Green);
            Moon(p, OnRing(phase * 1.6f + Mathf.PI * 0.85f), 2.6f, Violet);
        }

        private static void Moon(Painter2D p, Vector2 at, float radius, Color color)
        {
            p.fillColor = new Color(color.r, color.g, color.b, 0.22f);
            p.BeginPath();
            p.Arc(at, radius * 2.1f, 0f, 360f);
            p.Fill();
            p.fillColor = color;
            p.BeginPath();
            p.Arc(at, radius, 0f, 360f);
            p.Fill();
        }

        private static void Rounded(Painter2D p, Vector2 center, float size, float radius, Color color)
        {
            float h = size * 0.5f;
            float x = center.x - h;
            float y = center.y - h;
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(new Vector2(x + radius, y));
            p.LineTo(new Vector2(x + size - radius, y));
            p.ArcTo(new Vector2(x + size, y), new Vector2(x + size, y + radius), radius);
            p.LineTo(new Vector2(x + size, y + size - radius));
            p.ArcTo(new Vector2(x + size, y + size), new Vector2(x + size - radius, y + size), radius);
            p.LineTo(new Vector2(x + radius, y + size));
            p.ArcTo(new Vector2(x, y + size), new Vector2(x, y + size - radius), radius);
            p.LineTo(new Vector2(x, y + radius));
            p.ArcTo(new Vector2(x, y), new Vector2(x + radius, y), radius);
            p.ClosePath();
            p.Fill();
        }
    }

    /// <summary>A circle that draws itself, then a check inside it: the report is done.</summary>
    internal sealed class DoneMark : VisualElement
    {
        private static readonly Color Green = new Color(0f, 0.855f, 0.427f);
        private double started = -1d;
        private bool failed;

        public DoneMark()
        {
            AddToClassList("rp-done__mark");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            schedule.Execute(() =>
            {
                if (started >= 0d && EditorApplication.timeSinceStartup - started < 1.2d)
                {
                    MarkDirtyRepaint();
                }
            }).Every(16);
        }

        public void Play(bool failure)
        {
            failed = failure;
            started = EditorApplication.timeSinceStartup;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            if (started < 0d)
            {
                return;
            }

            var rect = contentRect;
            var center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 4f;
            float t = (float)(EditorApplication.timeSinceStartup - started);
            float ring = Ease(Mathf.Clamp01(t / 0.5f));
            float mark = Ease(Mathf.Clamp01((t - 0.35f) / 0.35f));
            var color = failed ? new Color(1f, 0.42f, 0.42f) : Green;
            var p = context.painter2D;

            // A soft disc grows under the ring.
            p.fillColor = new Color(color.r, color.g, color.b, 0.14f * ring);
            p.BeginPath();
            p.Arc(center, radius * (0.6f + 0.4f * ring), 0f, 360f);
            p.Fill();

            p.lineWidth = 4f;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            p.strokeColor = color;
            if (ring > 0.001f)
            {
                p.BeginPath();
                p.Arc(center, radius, -90f, -90f + 360f * ring);
                p.Stroke();
            }

            if (mark <= 0.001f)
            {
                return;
            }

            float s = radius / 24f;
            var points = failed
                ? new[] { center + new Vector2(-9f, -9f) * s, center + new Vector2(9f, 9f) * s }
                : new[] { center + new Vector2(-10f, 1f) * s, center + new Vector2(-3f, 8f) * s, center + new Vector2(11f, -7f) * s };
            p.lineWidth = 5f;
            p.BeginPath();
            p.MoveTo(points[0]);
            float total = 0f;
            for (int i = 1; i < points.Length; i++)
            {
                total += Vector2.Distance(points[i - 1], points[i]);
            }

            float left = total * mark;
            for (int i = 1; i < points.Length && left > 0f; i++)
            {
                float length = Vector2.Distance(points[i - 1], points[i]);
                p.LineTo(Vector2.Lerp(points[i - 1], points[i], Mathf.Clamp01(left / length)));
                left -= length;
            }

            p.Stroke();
            if (failed && mark > 0.5f)
            {
                float second = Mathf.Clamp01((mark - 0.5f) * 2f);
                var a = center + new Vector2(9f, -9f) * s;
                var b = center + new Vector2(-9f, 9f) * s;
                p.BeginPath();
                p.MoveTo(a);
                p.LineTo(Vector2.Lerp(a, b, second));
                p.Stroke();
            }
        }

        private static float Ease(float x) => 1f - Mathf.Pow(1f - x, 3f);
    }

    /// <summary>A burst of colour from a point: confetti thrown up, falling and fading in a second and a half.</summary>
    internal sealed class Burst : VisualElement
    {
        private static readonly Color[] Colors =
        {
            new Color(0f, 0.855f, 0.427f), new Color(0.235f, 0.949f, 0.604f), new Color(0.706f, 0.612f, 1f),
            new Color(0.961f, 0.761f, 0.42f), new Color(0.47f, 0.7f, 1f), new Color(1f, 0.58f, 0.4f), Color.white
        };

        private readonly List<Piece> pieces = new List<Piece>();
        private double started = -1d;
        private Vector2 origin;

        private struct Piece
        {
            public Vector2 Velocity;
            public float Size;
            public float Spin;
            public float Turn;
            public Color Color;
            public bool Round;
        }

        public Burst()
        {
            AddToClassList("rp-burst");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            schedule.Execute(() =>
            {
                if (started >= 0d && EditorApplication.timeSinceStartup - started < 1.7d)
                {
                    MarkDirtyRepaint();
                }
            }).Every(16);
        }

        /// <summary>Throws the confetti from <paramref name="at"/>, in this element's coordinates.</summary>
        public void Play(Vector2 at, int count = 64)
        {
            origin = at;
            pieces.Clear();
            var random = new System.Random();
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(random.NextDouble() * Math.PI * 2d);
                float force = 140f + (float)random.NextDouble() * 260f;
                pieces.Add(new Piece
                {
                    Velocity = new Vector2(Mathf.Cos(angle) * force, Mathf.Sin(angle) * force * 0.8f - 160f),
                    Size = 3f + (float)random.NextDouble() * 4f,
                    Spin = (float)random.NextDouble() * 6.28f,
                    Turn = ((float)random.NextDouble() - 0.5f) * 14f,
                    Color = Colors[random.Next(Colors.Length)],
                    Round = random.NextDouble() < 0.35d
                });
            }

            started = EditorApplication.timeSinceStartup;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            if (started < 0d)
            {
                return;
            }

            float t = (float)(EditorApplication.timeSinceStartup - started);
            if (t > 1.6f)
            {
                return;
            }

            var p = context.painter2D;
            foreach (var piece in pieces)
            {
                // Air slows them, gravity pulls them down.
                float drag = (1f - Mathf.Exp(-2.4f * t)) / 2.4f;
                var at = origin + piece.Velocity * drag + new Vector2(0f, 260f * t * t);
                float alpha = Mathf.Clamp01(1.6f - t) * Mathf.Clamp01(t * 12f);
                p.fillColor = new Color(piece.Color.r, piece.Color.g, piece.Color.b, alpha);
                if (piece.Round)
                {
                    p.BeginPath();
                    p.Arc(at, piece.Size * 0.5f, 0f, 360f);
                    p.Fill();
                    continue;
                }

                float angle = piece.Spin + piece.Turn * t;
                var along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * piece.Size * 0.7f;
                var across = new Vector2(-along.y, along.x) * 0.45f;
                p.BeginPath();
                p.MoveTo(at - along - across);
                p.LineTo(at + along - across);
                p.LineTo(at + along + across);
                p.LineTo(at - along + across);
                p.ClosePath();
                p.Fill();
            }
        }
    }

    /// <summary>Numbers that count up to their value instead of appearing.</summary>
    internal static class CountUp
    {
        public static void Play(Label label, long to, Func<long, string> format, long durationMs = 520)
        {
            double started = EditorApplication.timeSinceStartup;
            label.text = format(0);
            IVisualElementScheduledItem item = null;
            item = label.schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - started) * 1000d / durationMs));
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                label.text = format((long)Math.Round(to * eased));
                if (t >= 1f)
                {
                    item?.Pause();
                }
            }).Every(16);
        }
    }
}
