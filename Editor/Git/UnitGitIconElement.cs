using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal enum UnitGitIconKind
    {
        ChevronCollapsed,
        ChevronExpanded,
        CreateBranch,
        Update,
        Delete,
        Fetch,
        GitHub,
        PreviousDifference,
        NextDifference,
        Search,
        Refresh,
        Commit,
        Push,
        Pull,
        Branch,
        Settings,
        Rollback,
        Shelve,
        Diff,
        Folder,
        ExpandAll,
        CollapseAll,
        Copy,
        Tag,
        Star,
        Close,
        Console,
        Backup,
        Conflict,
        Merge,
        CherryPick,
        More,
        Check,
        Dash,
        Log,
        Changes,
        Filter,
        User,
        Calendar,
        Remote,
        ApplyLeft,
        CollapseUnchanged,
        FoldUp,
        FoldDown,
        Undo,
        Storage,
        OpenExternal
    }

    /// <summary>
    /// A line icon drawn with the vector API in a 24 x 24 box, crisp at any size. Its colour comes from the USS custom
    /// property <c>--icon-color</c> (and <c>--icon-width</c> for the stroke), so hover and selected states restyle it like text.
    /// </summary>
    internal sealed class UnitGitIconElement : VisualElement
    {
        private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--icon-color");
        private static readonly CustomStyleProperty<float> WidthProperty = new CustomStyleProperty<float>("--icon-width");
        private UnitGitIconKind kind;
        private Color color = new Color(0.84f, 0.84f, 0.86f);
        private float strokeWidth = 1.8f;

        public UnitGitIconElement(UnitGitIconKind kind)
        {
            this.kind = kind;
            AddToClassList("unitgit-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                bool dirty = false;
                if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; dirty = true; }
                if (customStyle.TryGetValue(WidthProperty, out float width) && !Mathf.Approximately(width, strokeWidth)) { strokeWidth = width; dirty = true; }
                if (dirty) MarkDirtyRepaint();
            });
        }

        public UnitGitIconKind Kind
        {
            get => kind;
            set { if (kind == value) return; kind = value; MarkDirtyRepaint(); }
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            float s = Mathf.Min(rect.width, rect.height) / 24f;
            if (s <= 0f) return;
            var o = new Vector2(rect.x + (rect.width - 24f * s) * 0.5f, rect.y + (rect.height - 24f * s) * 0.5f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
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
                for (int i = 1; i < points.Length; i++) p.LineTo(points[i]);
                p.Stroke();
            }
            void Closed(params Vector2[] points)
            {
                p.BeginPath();
                p.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++) p.LineTo(points[i]);
                p.ClosePath();
                p.Stroke();
            }
            void Filled(params Vector2[] points)
            {
                p.BeginPath();
                p.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++) p.LineTo(points[i]);
                p.ClosePath();
                p.Fill();
            }
            void Circle(float x, float y, float r) { p.BeginPath(); p.Arc(P(x, y), r * s, 0f, 360f); p.Stroke(); }
            void Dot(float x, float y, float r) { p.BeginPath(); p.Arc(P(x, y), r * s, 0f, 360f); p.Fill(); }
            void Arc(float x, float y, float r, float from, float to) { p.BeginPath(); p.Arc(P(x, y), r * s, from, to); p.Stroke(); }
            void RoundRect(float x, float y, float w, float h, float r)
            {
                p.BeginPath();
                p.MoveTo(P(x + r, y));
                p.LineTo(P(x + w - r, y));
                p.ArcTo(P(x + w, y), P(x + w, y + r), r * s);
                p.LineTo(P(x + w, y + h - r));
                p.ArcTo(P(x + w, y + h), P(x + w - r, y + h), r * s);
                p.LineTo(P(x + r, y + h));
                p.ArcTo(P(x, y + h), P(x, y + h - r), r * s);
                p.LineTo(P(x, y + r));
                p.ArcTo(P(x, y), P(x + r, y), r * s);
                p.ClosePath();
                p.Stroke();
            }

            switch (kind)
            {
                case UnitGitIconKind.ChevronCollapsed:
                    Line(P(9.5f, 6.5f), P(15f, 12f), P(9.5f, 17.5f));
                    break;
                case UnitGitIconKind.ChevronExpanded:
                    Line(P(6.5f, 9.5f), P(12f, 15f), P(17.5f, 9.5f));
                    break;
                case UnitGitIconKind.Branch:
                    Circle(7f, 5.5f, 2.2f); Circle(7f, 18.5f, 2.2f); Circle(17f, 7.5f, 2.2f);
                    Line(P(7f, 7.7f), P(7f, 16.3f));
                    p.BeginPath(); p.MoveTo(P(17f, 9.7f)); p.BezierCurveTo(P(17f, 14f), P(7f, 12f), P(7f, 16.3f)); p.Stroke();
                    break;
                case UnitGitIconKind.CreateBranch:
                    Circle(6f, 5.5f, 2.2f); Circle(6f, 18.5f, 2.2f);
                    Line(P(6f, 7.7f), P(6f, 16.3f));
                    Line(P(17f, 6f), P(17f, 14f)); Line(P(13f, 10f), P(21f, 10f));
                    break;
                case UnitGitIconKind.Merge:
                    Circle(7f, 5.5f, 2.2f); Circle(7f, 18.5f, 2.2f); Circle(17f, 18.5f, 2.2f);
                    Line(P(7f, 7.7f), P(7f, 16.3f));
                    p.BeginPath(); p.MoveTo(P(7f, 8.5f)); p.BezierCurveTo(P(7f, 13f), P(17f, 11f), P(17f, 16.3f)); p.Stroke();
                    break;
                case UnitGitIconKind.Update:
                case UnitGitIconKind.Pull:
                    Line(P(12f, 4f), P(12f, 15f)); Line(P(7.5f, 10.5f), P(12f, 15f), P(16.5f, 10.5f));
                    Line(P(5f, 19.5f), P(19f, 19.5f));
                    break;
                case UnitGitIconKind.Push:
                    Line(P(12f, 15f), P(12f, 4f)); Line(P(7.5f, 8.5f), P(12f, 4f), P(16.5f, 8.5f));
                    Line(P(5f, 19.5f), P(19f, 19.5f));
                    break;
                case UnitGitIconKind.Fetch:
                    p.BeginPath(); p.MoveTo(P(7.5f, 17.5f)); p.BezierCurveTo(P(3.5f, 17.5f), P(3f, 11.5f), P(7.2f, 11f));
                    p.BezierCurveTo(P(7.5f, 6f), P(15f, 5f), P(16.5f, 9.5f)); p.BezierCurveTo(P(21f, 9.5f), P(21.5f, 17.5f), P(16.5f, 17.5f)); p.Stroke();
                    Line(P(12f, 11f), P(12f, 20f)); Line(P(9.5f, 17.5f), P(12f, 20f), P(14.5f, 17.5f));
                    break;
                case UnitGitIconKind.Remote:
                case UnitGitIconKind.GitHub:
                    p.BeginPath(); p.MoveTo(P(7.5f, 18f)); p.BezierCurveTo(P(3.5f, 18f), P(3f, 12f), P(7.2f, 11.5f));
                    p.BezierCurveTo(P(7.5f, 6.5f), P(15f, 5.5f), P(16.5f, 10f)); p.BezierCurveTo(P(21f, 10f), P(21.5f, 18f), P(16.5f, 18f));
                    p.ClosePath(); p.Stroke();
                    break;
                case UnitGitIconKind.Delete:
                    Line(P(4.5f, 6.5f), P(19.5f, 6.5f)); Line(P(9.5f, 6.5f), P(10f, 4f), P(14f, 4f), P(14.5f, 6.5f));
                    Line(P(6.5f, 6.5f), P(7.5f, 20f), P(16.5f, 20f), P(17.5f, 6.5f));
                    Line(P(10.2f, 10f), P(10.4f, 16.5f)); Line(P(13.8f, 10f), P(13.6f, 16.5f));
                    break;
                case UnitGitIconKind.PreviousDifference:
                    Line(P(12f, 19f), P(12f, 5f)); Line(P(6.5f, 10.5f), P(12f, 5f), P(17.5f, 10.5f));
                    break;
                case UnitGitIconKind.NextDifference:
                    Line(P(12f, 5f), P(12f, 19f)); Line(P(6.5f, 13.5f), P(12f, 19f), P(17.5f, 13.5f));
                    break;
                case UnitGitIconKind.Search:
                    Circle(10.5f, 10.5f, 5.5f); Line(P(14.7f, 14.7f), P(19.5f, 19.5f));
                    break;
                case UnitGitIconKind.Filter:
                    Line(P(4.5f, 6f), P(19.5f, 6f), P(14f, 12.5f), P(14f, 18.5f), P(10f, 20f), P(10f, 12.5f), P(4.5f, 6f));
                    break;
                case UnitGitIconKind.Refresh:
                    Arc(12f, 12f, 7f, -60f, 250f);
                    Filled(P(15.4f, 2.4f), P(19.8f, 6.3f), P(14.2f, 8.1f));
                    break;
                case UnitGitIconKind.Rollback:
                    p.BeginPath(); p.MoveTo(P(8f, 9f)); p.LineTo(P(15f, 9f)); p.BezierCurveTo(P(22f, 9f), P(22f, 19.5f), P(15f, 19.5f)); p.LineTo(P(10f, 19.5f)); p.Stroke();
                    Line(P(11.5f, 5f), P(7.5f, 9f), P(11.5f, 13f));
                    break;
                case UnitGitIconKind.Commit:
                case UnitGitIconKind.Check:
                    Line(P(5f, 12.5f), P(10f, 17.5f), P(19f, 7f));
                    break;
                case UnitGitIconKind.Dash:
                    Line(P(6f, 12f), P(18f, 12f));
                    break;
                case UnitGitIconKind.Shelve:
                    Closed(P(4f, 10f), P(4f, 19.5f), P(20f, 19.5f), P(20f, 10f));
                    Line(P(4f, 10f), P(7f, 5f), P(17f, 5f), P(20f, 10f));
                    Line(P(9.5f, 13.5f), P(14.5f, 13.5f));
                    break;
                case UnitGitIconKind.Diff:
                    RoundRect(3.5f, 5f, 17f, 14f, 2.5f); Line(P(12f, 5f), P(12f, 19f));
                    Line(P(6.5f, 9.5f), P(9f, 9.5f)); Line(P(15f, 9.5f), P(17.5f, 9.5f)); Line(P(15f, 13.5f), P(17.5f, 13.5f));
                    break;
                case UnitGitIconKind.Folder:
                    Closed(P(3.5f, 7f), P(3.5f, 18.5f), P(20.5f, 18.5f), P(20.5f, 9f), P(11.5f, 9f), P(9.5f, 6.5f), P(4f, 6.5f));
                    break;
                // JetBrains' pair: flat chevrons leaving (expand) or reaching (collapse) a middle line.
                case UnitGitIconKind.ExpandAll:
                    Line(P(6.5f, 7.5f), P(12f, 3.5f), P(17.5f, 7.5f)); Line(P(6.5f, 16.5f), P(12f, 20.5f), P(17.5f, 16.5f));
                    Line(P(5f, 12f), P(19f, 12f));
                    break;
                case UnitGitIconKind.CollapseAll:
                    Line(P(6.5f, 3.5f), P(12f, 7.5f), P(17.5f, 3.5f)); Line(P(6.5f, 20.5f), P(12f, 16.5f), P(17.5f, 20.5f));
                    Line(P(5f, 12f), P(19f, 12f));
                    break;
                case UnitGitIconKind.ApplyLeft:
                    Line(P(5.5f, 6.5f), P(11f, 12f), P(5.5f, 17.5f)); Line(P(12.5f, 6.5f), P(18f, 12f), P(12.5f, 17.5f));
                    break;
                case UnitGitIconKind.CollapseUnchanged:
                    Line(P(8f, 3.5f), P(12f, 7.5f), P(16f, 3.5f)); Line(P(8f, 20.5f), P(12f, 16.5f), P(16f, 20.5f));
                    Line(P(4f, 12f), P(6.5f, 12f)); Line(P(10.75f, 12f), P(13.25f, 12f)); Line(P(17.5f, 12f), P(20f, 12f));
                    break;
                case UnitGitIconKind.FoldUp:
                    Line(P(5f, 4.5f), P(19f, 4.5f)); Line(P(7f, 14f), P(12f, 9f), P(17f, 14f)); Line(P(12f, 9f), P(12f, 20f));
                    break;
                case UnitGitIconKind.FoldDown:
                    Line(P(5f, 19.5f), P(19f, 19.5f)); Line(P(7f, 10f), P(12f, 15f), P(17f, 10f)); Line(P(12f, 15f), P(12f, 4f));
                    break;
                case UnitGitIconKind.Undo:
                    p.BeginPath(); p.MoveTo(P(8f, 9f)); p.LineTo(P(15f, 9f)); p.BezierCurveTo(P(22f, 9f), P(22f, 19.5f), P(15f, 19.5f)); p.LineTo(P(10f, 19.5f)); p.Stroke();
                    Line(P(11.5f, 5f), P(7.5f, 9f), P(11.5f, 13f));
                    break;
                case UnitGitIconKind.Copy:
                    RoundRect(8.5f, 8.5f, 11f, 11f, 2f);
                    Line(P(15.5f, 5.5f), P(15.5f, 5f), P(6.5f, 4.5f)); Line(P(4.5f, 6.5f), P(4.5f, 15.5f), P(5.5f, 15.5f));
                    break;
                case UnitGitIconKind.Tag:
                    Closed(P(4f, 4f), P(12f, 4f), P(20f, 12f), P(12f, 20f), P(4f, 12f));
                    Dot(8.5f, 8.5f, 1.4f);
                    break;
                case UnitGitIconKind.Star:
                    {
                        var points = new Vector2[10];
                        for (int i = 0; i < 10; i++)
                        {
                            float angle = (-90f + i * 36f) * Mathf.Deg2Rad;
                            float r = i % 2 == 0 ? 8.5f : 3.8f;
                            points[i] = P(12f + Mathf.Cos(angle) * r, 12.5f + Mathf.Sin(angle) * r);
                        }
                        Filled(points);
                    }
                    break;
                case UnitGitIconKind.Close:
                    Line(P(6.5f, 6.5f), P(17.5f, 17.5f)); Line(P(17.5f, 6.5f), P(6.5f, 17.5f));
                    break;
                case UnitGitIconKind.Console:
                    RoundRect(3.5f, 4.5f, 17f, 15f, 2.5f); Line(P(7.5f, 9.5f), P(10.5f, 12f), P(7.5f, 14.5f)); Line(P(12.5f, 15f), P(16.5f, 15f));
                    break;
                case UnitGitIconKind.Backup:
                    RoundRect(4f, 4f, 16f, 5f, 1.5f); Closed(P(5f, 9f), P(5f, 19.5f), P(19f, 19.5f), P(19f, 9f)); Line(P(10f, 13f), P(14f, 13f));
                    break;
                case UnitGitIconKind.Conflict:
                    Closed(P(12f, 4f), P(21f, 19.5f), P(3f, 19.5f)); Line(P(12f, 9.5f), P(12f, 14f)); Dot(12f, 16.8f, 1.2f);
                    break;
                case UnitGitIconKind.CherryPick:
                    Circle(8f, 16.5f, 3.5f); Circle(16.5f, 16.5f, 3.5f);
                    p.BeginPath(); p.MoveTo(P(8f, 13f)); p.BezierCurveTo(P(9f, 8f), P(12f, 5f), P(15f, 4f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(16.5f, 13f)); p.BezierCurveTo(P(16f, 9f), P(15.5f, 6f), P(15f, 4f)); p.Stroke();
                    break;
                case UnitGitIconKind.More:
                    Dot(6f, 12f, 1.6f); Dot(12f, 12f, 1.6f); Dot(18f, 12f, 1.6f);
                    break;
                case UnitGitIconKind.Settings:
                    Circle(12f, 12f, 3f);
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * 45f * Mathf.Deg2Rad;
                        var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        Line(P(12f + direction.x * 6f, 12f + direction.y * 6f), P(12f + direction.x * 8.5f, 12f + direction.y * 8.5f));
                    }
                    Circle(12f, 12f, 6f);
                    break;
                case UnitGitIconKind.Log:
                    Circle(12f, 12f, 8f); Line(P(12f, 7.5f), P(12f, 12f), P(15.5f, 14f));
                    break;
                case UnitGitIconKind.Changes:
                    Closed(P(6f, 3.5f), P(14f, 3.5f), P(18.5f, 8f), P(18.5f, 20.5f), P(6f, 20.5f));
                    Line(P(9f, 12.5f), P(15.5f, 12.5f)); Line(P(12.25f, 9.25f), P(12.25f, 15.75f));
                    break;
                case UnitGitIconKind.User:
                    Circle(12f, 8.5f, 3.8f);
                    p.BeginPath(); p.MoveTo(P(4.5f, 20f)); p.BezierCurveTo(P(5.5f, 14f), P(18.5f, 14f), P(19.5f, 20f)); p.Stroke();
                    break;
                case UnitGitIconKind.Calendar:
                    RoundRect(4f, 5.5f, 16f, 14.5f, 2.5f); Line(P(4f, 10f), P(20f, 10f)); Line(P(8.5f, 3.5f), P(8.5f, 7f)); Line(P(15.5f, 3.5f), P(15.5f, 7f));
                    break;
                // A database: three stacked discs.
                case UnitGitIconKind.Storage:
                    p.BeginPath(); p.MoveTo(P(5f, 6f)); p.BezierCurveTo(P(5f, 2.7f), P(19f, 2.7f), P(19f, 6f)); p.BezierCurveTo(P(19f, 9.3f), P(5f, 9.3f), P(5f, 6f)); p.Stroke();
                    Line(P(5f, 6f), P(5f, 18f)); Line(P(19f, 6f), P(19f, 18f));
                    p.BeginPath(); p.MoveTo(P(5f, 12f)); p.BezierCurveTo(P(5f, 15.3f), P(19f, 15.3f), P(19f, 12f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(5f, 18f)); p.BezierCurveTo(P(5f, 21.3f), P(19f, 21.3f), P(19f, 18f)); p.Stroke();
                    break;
                // A box with an arrow leaving it by its top right corner: opens in the browser.
                case UnitGitIconKind.OpenExternal:
                    Line(P(11f, 5.5f), P(6.5f, 5.5f), P(5f, 7f), P(5f, 17.5f), P(6.5f, 19f), P(17f, 19f), P(18.5f, 17.5f), P(18.5f, 13f));
                    Line(P(11f, 13f), P(19.5f, 4.5f)); Line(P(14f, 4.5f), P(19.5f, 4.5f), P(19.5f, 10f));
                    break;
            }
        }
    }
}
