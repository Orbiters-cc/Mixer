using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>Solid rectangles drawn in one mesh (cheaper than one element or one painter path per bar).</summary>
    internal sealed class QuadBatch
    {
        private readonly List<Vertex> vertices = new List<Vertex>(1024);
        private readonly List<ushort> indices = new List<ushort>(1536);

        public void Clear()
        {
            vertices.Clear();
            indices.Clear();
        }

        public void Rect(float x, float y, float width, float height, Color color)
        {
            if (width <= 0f || height <= 0f || color.a <= 0f || vertices.Count > 65000)
            {
                return;
            }

            Color32 tint = color;
            ushort start = (ushort)vertices.Count;
            vertices.Add(new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = tint });
            vertices.Add(new Vertex { position = new Vector3(x + width, y, Vertex.nearZ), tint = tint });
            vertices.Add(new Vertex { position = new Vector3(x + width, y + height, Vertex.nearZ), tint = tint });
            vertices.Add(new Vertex { position = new Vector3(x, y + height, Vertex.nearZ), tint = tint });
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
            indices.Add(start);
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)(start + 3));
        }

        public void Flush(MeshGenerationContext context)
        {
            if (vertices.Count == 0)
            {
                return;
            }

            var mesh = context.Allocate(vertices.Count, indices.Count);
            for (int i = 0; i < vertices.Count; i++)
            {
                mesh.SetNextVertex(vertices[i]);
            }

            for (int i = 0; i < indices.Count; i++)
            {
                mesh.SetNextIndex(indices[i]);
            }

            Clear();
        }
    }

    internal static class ChartColors
    {
        public static readonly Color Error = SourceCatalog.Hex("#ff6b6b");
        public static readonly Color Warning = SourceCatalog.Hex("#f5b74f");
        public static readonly Color Info = SourceCatalog.Hex("#7f93ad");
        public static readonly Color Accent = SourceCatalog.Hex("#00da6d");
        public static readonly Color Grid = new Color(1f, 1f, 1f, 0.05f);

        public static Color Level(LogLevel level) => level == LogLevel.Error ? Error : level == LogLevel.Warning ? Warning : Info;

        public static Color Event(SessionEventKind kind)
        {
            switch (kind)
            {
                case SessionEventKind.EnterPlayMode:
                    return SourceCatalog.Hex("#3cf29a");
                case SessionEventKind.ExitPlayMode:
                    return SourceCatalog.Hex("#9e9e9e");
                case SessionEventKind.Compile:
                    return SourceCatalog.Hex("#6aa7ff");
                case SessionEventKind.CompileFailed:
                    return SourceCatalog.Hex("#ff6b6b");
                case SessionEventKind.DomainReload:
                    return SourceCatalog.Hex("#b890ff");
                default:
                    return SourceCatalog.Hex("#f0a35e");
            }
        }

        /// <summary>Bar height for <paramref name="value"/>: logarithmic, so a burst of a million doesn't flatten everything else.</summary>
        public static float Scale(int value, int max) => value <= 0 || max <= 0 ? 0f : Mathf.Log(1f + value) / Mathf.Log(1f + max);
    }

    /// <summary>A row's small activity chart: the group's logs over the session in 24 bars.</summary>
    internal sealed class Sparkline : VisualElement
    {
        private readonly int[] values = new int[FilterState.SparkBuckets];
        private readonly QuadBatch batch = new QuadBatch();
        private Color color = ChartColors.Info;
        private bool empty = true;

        public Sparkline()
        {
            AddToClassList("lg-spark");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        public void Set(int[] source, int offset, Color barColor)
        {
            bool changed = barColor != color;
            bool nowEmpty = true;
            for (int i = 0; i < values.Length; i++)
            {
                int value = source != null && offset + i < source.Length ? source[offset + i] : 0;
                if (values[i] != value)
                {
                    values[i] = value;
                    changed = true;
                }

                nowEmpty &= value == 0;
            }

            color = barColor;
            if (changed || nowEmpty != empty)
            {
                empty = nowEmpty;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0 || rect.height <= 0)
            {
                return;
            }

            int max = 0;
            foreach (int value in values)
            {
                max = Math.Max(max, value);
            }

            float step = rect.width / values.Length;
            float barWidth = Math.Max(1f, step - 1f);
            var baseColor = new Color(color.r, color.g, color.b, 0.16f);
            for (int i = 0; i < values.Length; i++)
            {
                float x = rect.x + i * step;
                batch.Rect(x, rect.yMax - 1f, barWidth, 1f, baseColor);
                if (values[i] <= 0)
                {
                    continue;
                }

                float height = Math.Max(2f, ChartColors.Scale(values[i], max) * rect.height);
                float alpha = i == values.Length - 1 ? 1f : 0.55f + 0.45f * i / values.Length;
                batch.Rect(x, rect.yMax - height, barWidth, height, new Color(color.r, color.g, color.b, alpha));
            }

            batch.Flush(context);
        }
    }

    /// <summary>The details pane's chart: one group's logs over time, with its own hover read-out.</summary>
    internal sealed class FrequencyChart : VisualElement
    {
        private readonly QuadBatch batch = new QuadBatch();
        private readonly FloatingTip tip;
        private int[] values = new int[0];
        private long from;
        private long to;
        private Color color = ChartColors.Info;
        private int hover = -1;

        public FrequencyChart()
        {
            AddToClassList("lg-frequency");
            generateVisualContent += Draw;
            tip = new FloatingTip(this);
            RegisterCallback<PointerMoveEvent>(evt => SetHover(evt.localPosition.x));
            RegisterCallback<PointerLeaveEvent>(_ => SetHover(-1f));
        }

        public void Set(int[] buckets, long start, long end, Color barColor)
        {
            values = buckets ?? new int[0];
            from = start;
            to = end;
            color = barColor;
            MarkDirtyRepaint();
            if (hover >= 0)
            {
                UpdateTip();
            }
        }

        private void SetHover(float x)
        {
            int bucket = x < 0 || values.Length == 0 || contentRect.width <= 0 ? -1 : Mathf.Clamp((int)(x / contentRect.width * values.Length), 0, values.Length - 1);
            if (bucket == hover)
            {
                return;
            }

            hover = bucket;
            UpdateTip();
            MarkDirtyRepaint();
        }

        private void UpdateTip()
        {
            if (hover < 0 || hover >= values.Length)
            {
                tip.Hide();
                return;
            }

            long span = Math.Max(1L, to - from);
            long start = from + span * hover / values.Length;
            long end = from + span * (hover + 1) / values.Length;
            float x = contentRect.width * (hover + 0.5f) / values.Length;
            tip.Show(LoggerUi.Time(start, false) + " – " + LoggerUi.Time(end, false) + "   ×" + LoggerUi.Count(values[hover]), x, 4f);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0 || rect.height <= 0 || values.Length == 0)
            {
                return;
            }

            int max = 0;
            foreach (int value in values)
            {
                max = Math.Max(max, value);
            }

            float step = rect.width / values.Length;
            float barWidth = Math.Max(1f, step - (step > 4f ? 1.5f : step > 2f ? 1f : 0f));
            float plotHeight = rect.height - 2f;
            batch.Rect(rect.x, rect.yMax - 1f, rect.width, 1f, ChartColors.Grid);
            batch.Rect(rect.x, rect.y + plotHeight * 0.5f, rect.width, 1f, new Color(1f, 1f, 1f, 0.03f));
            for (int i = 0; i < values.Length; i++)
            {
                float x = rect.x + i * step;
                if (i == hover)
                {
                    batch.Rect(x, rect.y, Math.Max(step, 1f), rect.height, new Color(1f, 1f, 1f, 0.06f));
                }

                if (values[i] <= 0)
                {
                    continue;
                }

                float height = Math.Max(2f, ChartColors.Scale(values[i], max) * plotHeight);
                batch.Rect(x, rect.y + plotHeight - height, barWidth, height, new Color(color.r, color.g, color.b, i == hover ? 1f : 0.82f));
                batch.Rect(x, rect.y + plotHeight - height, barWidth, Math.Min(2f, height), new Color(1f, 1f, 1f, 0.18f));
            }

            batch.Flush(context);
        }
    }

    /// <summary>
    /// The activity strip under the toolbar: every visible log over the session, stacked by level, with Play Mode,
    /// compile and reload markers. Hover reads a moment out; a click jumps the list there; a drag keeps only that time
    /// range; a double-click clears it.
    /// </summary>
    internal sealed class TimelineView : VisualElement
    {
        private const float DragThreshold = 4f;
        private readonly VisualElement plot;
        private readonly QuadBatch batch = new QuadBatch();
        private readonly Label startLabel;
        private readonly Label middleLabel;
        private readonly Label endLabel;
        private readonly FloatingTip tip;
        private readonly Label emptyLabel;
        private FilterState state;
        private IReadOnlyList<SessionEvent> events = new SessionEvent[0];
        private TimelineMarker[] markers = new TimelineMarker[0];
        private IReadOnlyList<TimingEntry> timingEntries = new TimingEntry[0];
        private readonly VisualElement timingLane;
        private readonly QuadBatch laneBatch = new QuadBatch();
        private readonly List<(float x, int index)> laneMarks = new List<(float x, int index)>();
        private int laneHover = -1;
        private long highlight;
        private VisualElement markerLabels;
        private readonly VisualElement chipRow;
        private bool labelsBelow;
        private string labelsKey = string.Empty;
        private long rangeFrom;
        private long rangeTo;
        private int hover = -1;
        private bool pressed;
        private bool dragging;
        private float pressX;
        private float dragX;
        private int pointerId = -1;
        private long lastLanePress;

        public event Action<long, long> RangeSelected;
        public event Action<long> TimeClicked;
        /// <summary>A timing mark (reload, Play Mode, build, upload) was clicked: its log's message.</summary>
        public event Action<int> TimingClicked;

        public TimelineView()
        {
            AddToClassList("lg-timeline");
            plot = LoggerUi.Box("lg-timeline__plot");
            plot.generateVisualContent += Draw;
            markerLabels = LoggerUi.Box("lg-timeline__markers", PickingMode.Ignore);
            plot.Add(markerLabels);
            plot.RegisterCallback<GeometryChangedEvent>(_ => LayoutMarkerLabels());
            Add(plot);

            // A short window leaves the plot too low for the labels: they move to this row under it.
            chipRow = LoggerUi.Box("lg-timeline__chips", PickingMode.Ignore);
            Add(chipRow);

            // Reloads, entries into Play Mode, builds and uploads: one stacked mark each, coloured by package.
            timingLane = LoggerUi.Box("lg-timeline__lane");
            timingLane.generateVisualContent += DrawLane;
            timingLane.RegisterCallback<PointerMoveEvent>(evt => SetLaneHover(LaneMarkAt(evt.localPosition.x)));
            timingLane.RegisterCallback<PointerLeaveEvent>(_ => SetLaneHover(-1));
            timingLane.RegisterCallback<PointerDownEvent>(evt =>
            {
                int index = LaneMarkAt(evt.localPosition.x);
                if (evt.button == 0 && index >= 0 && index < timingEntries.Count)
                {
                    evt.StopPropagation();
                    if (!LoggerUi.IsRepeatPress(ref lastLanePress))
                    {
                        TimingClicked?.Invoke(timingEntries[index].Message);
                    }
                }
            });
            timingLane.style.display = DisplayStyle.None;
            Add(timingLane);

            var axis = LoggerUi.Box("lg-timeline__axis", PickingMode.Ignore);
            startLabel = LoggerUi.Text(string.Empty, "lg-timeline__label");
            middleLabel = LoggerUi.Text(string.Empty, "lg-timeline__label");
            middleLabel.AddToClassList("lg-timeline__label--middle");
            endLabel = LoggerUi.Text(string.Empty, "lg-timeline__label");
            endLabel.AddToClassList("lg-timeline__label--end");
            axis.Add(startLabel);
            axis.Add(middleLabel);
            axis.Add(endLabel);
            Add(axis);

            tip = new FloatingTip(this);

            emptyLabel = LoggerUi.Text("Activity over the session shows here", "lg-timeline__empty");
            emptyLabel.pickingMode = PickingMode.Ignore;
            Add(emptyLabel);

            plot.RegisterCallback<PointerDownEvent>(OnPointerDown);
            plot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            plot.RegisterCallback<PointerUpEvent>(OnPointerUp);
            plot.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!dragging)
                {
                    SetHover(-1);
                }
            });
        }

        /// <summary>Marks a moment on the chart (an undo step being hovered), or nothing with 0.</summary>
        public void SetHighlight(long utcTicks)
        {
            if (utcTicks == highlight)
            {
                return;
            }

            highlight = utcTicks;
            plot.MarkDirtyRepaint();
        }

        /// <summary>Width of the plot in pixels: decides how many buckets the filter computes.</summary>
        public float PlotWidth => float.IsNaN(plot.layout.width) ? 0f : plot.layout.width;

        public void Set(FilterState filter, IReadOnlyList<SessionEvent> sessionEvents, TimelineMarker[] projectMarkers,
            IReadOnlyList<TimingEntry> timingMarks, long from, long to)
        {
            state = filter;
            events = sessionEvents ?? new SessionEvent[0];
            markers = projectMarkers ?? new TimelineMarker[0];
            timingEntries = timingMarks ?? new TimingEntry[0];
            bool lane = false;
            if (state != null && state.HasTimeline)
            {
                foreach (var entry in timingEntries)
                {
                    if (entry.Time >= state.ChartFrom && entry.Time <= state.ChartTo)
                    {
                        lane = true;
                        break;
                    }
                }
            }

            timingLane.style.display = lane ? DisplayStyle.Flex : DisplayStyle.None;
            EnableInClassList("lg-timeline--lane", lane);
            timingLane.MarkDirtyRepaint();
            LayoutMarkerLabels();
            rangeFrom = from;
            rangeTo = to;
            bool hasData = state != null && state.HasTimeline && state.Covered > 0;
            emptyLabel.style.display = hasData ? DisplayStyle.None : DisplayStyle.Flex;
            if (state != null && state.HasTimeline)
            {
                long span = state.ChartTo - state.ChartFrom;
                startLabel.text = LoggerUi.Moment(state.ChartFrom);
                middleLabel.text = span > TimeSpan.TicksPerMinute * 2 ? LoggerUi.Time(state.ChartFrom + span / 2, false) : string.Empty;
                endLabel.text = LoggerUi.Time(state.ChartTo, false);
            }
            else
            {
                startLabel.text = middleLabel.text = endLabel.text = string.Empty;
            }

            if (hover >= 0)
            {
                UpdateTip();
            }

            plot.MarkDirtyRepaint();
        }

        private long TimeAt(float x)
        {
            if (state == null || PlotWidth <= 0)
            {
                return 0;
            }

            double t = Mathf.Clamp01(x / PlotWidth);
            return state.ChartFrom + (long)((state.ChartTo - state.ChartFrom) * t);
        }

        private float XOf(long time)
        {
            if (state == null)
            {
                return 0f;
            }

            double span = Math.Max(1L, state.ChartTo - state.ChartFrom);
            return (float)((time - state.ChartFrom) / span * PlotWidth);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || state == null)
            {
                return;
            }

            if (evt.clickCount >= 2)
            {
                pressed = false;
                RangeSelected?.Invoke(0, 0);
                return;
            }

            pressed = true;
            dragging = false;
            pressX = dragX = evt.localPosition.x;
            pointerId = evt.pointerId;
            plot.CapturePointer(pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            float x = evt.localPosition.x;
            if (pressed)
            {
                dragX = x;
                if (!dragging && Mathf.Abs(dragX - pressX) > DragThreshold)
                {
                    dragging = true;
                }

                if (dragging)
                {
                    plot.MarkDirtyRepaint();
                }
            }

            SetHover(BucketAt(x));
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!pressed)
            {
                return;
            }

            pressed = false;
            if (plot.HasPointerCapture(pointerId))
            {
                plot.ReleasePointer(pointerId);
            }

            if (dragging)
            {
                dragging = false;
                float a = Mathf.Min(pressX, dragX);
                float b = Mathf.Max(pressX, dragX);
                RangeSelected?.Invoke(TimeAt(a), TimeAt(b));
            }
            else
            {
                TimeClicked?.Invoke(TimeAt(evt.localPosition.x));
            }

            plot.MarkDirtyRepaint();
        }

        private int BucketAt(float x)
        {
            if (state == null || !state.HasTimeline || PlotWidth <= 0 || x < 0 || x > PlotWidth)
            {
                return -1;
            }

            return Mathf.Clamp((int)(x / PlotWidth * state.Buckets), 0, state.Buckets - 1);
        }

        private void SetHover(int bucket)
        {
            if (bucket == hover)
            {
                return;
            }

            hover = bucket;
            UpdateTip();
            plot.MarkDirtyRepaint();
        }

        private void UpdateTip()
        {
            if (hover < 0 || state == null || !state.HasTimeline)
            {
                tip.Hide();
                return;
            }

            int buckets = state.Buckets;
            long span = Math.Max(1L, state.ChartTo - state.ChartFrom);
            long start = state.ChartFrom + span * hover / buckets;
            long end = state.ChartFrom + span * (hover + 1) / buckets;
            int errors = state.Timeline[2 * buckets + hover];
            int warnings = state.Timeline[buckets + hover];
            int logs = state.Timeline[hover];
            var text = new System.Text.StringBuilder();
            text.Append("<b>").Append(LoggerUi.Time(start, false)).Append(" – ").Append(LoggerUi.Time(end, false)).Append("</b>");
            if (errors + warnings + logs == 0)
            {
                text.Append("   <color=#8a8a8a>nothing logged</color>");
            }
            else
            {
                if (errors > 0)
                {
                    text.Append("   <color=#ff8a8a>").Append(LoggerUi.Plural(errors, "error")).Append("</color>");
                }

                if (warnings > 0)
                {
                    text.Append("   <color=#f5c26b>").Append(LoggerUi.Plural(warnings, "warning")).Append("</color>");
                }

                if (logs > 0)
                {
                    text.Append("   <color=#b9c4d3>").Append(LoggerUi.Plural(logs, "log")).Append("</color>");
                }
            }

            foreach (var sessionEvent in events)
            {
                if (sessionEvent.Time >= start && sessionEvent.Time < end)
                {
                    text.Append("\n").Append(LoggerUi.Time(sessionEvent.Time, false)).Append("  ").Append(sessionEvent.Label);
                }
            }

            int listed = 0;
            int more = 0;
            foreach (var marker in markers)
            {
                long ticks = marker.TimeUtc.Ticks;
                if (ticks < start || ticks >= end)
                {
                    continue;
                }

                if (listed++ >= 4)
                {
                    more++;
                    continue;
                }

                text.Append('\n').Append(marker.Kind == TimelineMarkerKind.Release ? "<color=#ffd06a>◆ " : "<color=#9dbbff>● ")
                    .Append(LoggerUi.Time(ticks, false)).Append("</color>  ").Append(RichText.Literal(Shorten(marker.Title, 70)));
                if (!string.IsNullOrEmpty(marker.Detail))
                {
                    text.Append("  <color=#8a8a8a>").Append(RichText.Literal(Shorten(marker.Detail, 40))).Append("</color>");
                }
            }

            if (more > 0)
            {
                text.Append("\n<color=#8a8a8a>+").Append(more).Append(" more</color>");
            }

            float x = PlotWidth * (hover + 0.5f) / buckets;
            tip.Show(text.ToString(), x + plot.layout.x, 6f);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = plot.contentRect;
            if (rect.width <= 0 || rect.height <= 0)
            {
                return;
            }

            float height = rect.height;
            batch.Rect(0, height - 1f, rect.width, 1f, ChartColors.Grid);
            if (state != null && state.HasTimeline)
            {
                int buckets = state.Buckets;
                var timeline = state.Timeline;
                int max = 0;
                for (int b = 0; b < buckets; b++)
                {
                    max = Math.Max(max, timeline[b] + timeline[buckets + b] + timeline[2 * buckets + b]);
                }

                float step = rect.width / buckets;
                float barWidth = Math.Max(1f, step - (step >= 4f ? 1f : 0f));
                bool ranged = rangeTo > rangeFrom && rangeTo > 0;
                float rangeA = ranged ? XOf(rangeFrom) : 0f;
                float rangeB = ranged ? XOf(rangeTo) : 0f;
                for (int b = 0; b < buckets; b++)
                {
                    float x = b * step;
                    int logs = timeline[b];
                    int warnings = timeline[buckets + b];
                    int errors = timeline[2 * buckets + b];
                    int total = logs + warnings + errors;
                    if (b == hover)
                    {
                        batch.Rect(x, 0, Math.Max(step, 1f), height, new Color(1f, 1f, 1f, 0.07f));
                    }

                    if (total == 0)
                    {
                        continue;
                    }

                    bool inRange = !ranged || x + step >= rangeA && x <= rangeB;
                    float dim = inRange ? 1f : 0.35f;
                    float barHeight = Math.Max(2f, ChartColors.Scale(total, max) * (height - 3f));
                    // Stacked proportionally: errors at the bottom, where the eye goes first.
                    float y = height - 1f;
                    y = Segment(x, y, barWidth, barHeight * errors / total, ChartColors.Error, dim);
                    y = Segment(x, y, barWidth, barHeight * warnings / total, ChartColors.Warning, dim);
                    Segment(x, y, barWidth, barHeight * logs / total, ChartColors.Info, dim * 0.85f);
                }

                foreach (var sessionEvent in events)
                {
                    if (sessionEvent.Time < state.ChartFrom || sessionEvent.Time > state.ChartTo)
                    {
                        continue;
                    }

                    float x = XOf(sessionEvent.Time);
                    var color = ChartColors.Event(sessionEvent.Kind);
                    batch.Rect(x, 0, 1f, height, new Color(color.r, color.g, color.b, 0.35f));
                    batch.Rect(x - 2f, height - 3f, 5f, 3f, color);
                }

                // Project history (Mixer Git): commits as blue ticks, releases as gold pins.
                foreach (var marker in markers)
                {
                    long ticks = marker.TimeUtc.Ticks;
                    if (ticks < state.ChartFrom || ticks > state.ChartTo)
                    {
                        continue;
                    }

                    float x = XOf(ticks);
                    if (marker.Kind == TimelineMarkerKind.Release)
                    {
                        batch.Rect(x, 0, 1.5f, height, new Color(1f, 0.79f, 0.3f, 0.7f));
                        batch.Rect(x - 3.5f, 0, 8.5f, 8.5f, new Color(1f, 0.79f, 0.3f, 1f));
                    }
                    else
                    {
                        batch.Rect(x - 0.5f, 0, 2f, height, new Color(0.42f, 0.65f, 1f, 0.6f));
                        batch.Rect(x - 3f, 0, 7f, 7f, new Color(0.42f, 0.65f, 1f, 1f));
                    }
                }

                if (highlight > 0 && highlight >= state.ChartFrom && highlight <= state.ChartTo)
                {
                    float x = XOf(highlight);
                    batch.Rect(x - 1f, 0, 2f, height, new Color(1f, 1f, 1f, 0.75f));
                    batch.Rect(x - 4f, 0, 8f, 3f, Color.white);
                }

                if (ranged)
                {
                    batch.Rect(rangeA, 0, Math.Max(1f, rangeB - rangeA), height, new Color(0f, 0.85f, 0.43f, 0.10f));
                    batch.Rect(rangeA, 0, 1.5f, height, ChartColors.Accent);
                    batch.Rect(rangeB - 1.5f, 0, 1.5f, height, ChartColors.Accent);
                }

                if (dragging)
                {
                    float a = Mathf.Clamp(Mathf.Min(pressX, dragX), 0, rect.width);
                    float b = Mathf.Clamp(Mathf.Max(pressX, dragX), 0, rect.width);
                    batch.Rect(a, 0, Math.Max(1f, b - a), height, new Color(0f, 0.85f, 0.43f, 0.16f));
                    batch.Rect(a, 0, 1.5f, height, ChartColors.Accent);
                    batch.Rect(b - 1.5f, 0, 1.5f, height, ChartColors.Accent);
                }
            }

            batch.Flush(context);
        }

        // A small label next to each commit and release pin. Newest first wins the space; labels that would overlap a
        // shown one are left out (the pin and the hover read-out still show them).
        private void LayoutMarkerLabels()
        {
            if (markerLabels == null || state == null || !state.HasTimeline)
            {
                markerLabels?.Clear();
                labelsKey = string.Empty;
                ShowChipRow();
                return;
            }

            float width = PlotWidth;
            string key = width.ToString("0") + "|" + state.ChartFrom / TimeSpan.TicksPerSecond + "|" + state.ChartTo / (TimeSpan.TicksPerSecond * 5) + "|" + markers.Length;
            if (key == labelsKey)
            {
                return;
            }

            labelsKey = key;
            markerLabels.Clear();
            var taken = new List<(float from, float to)>();
            for (int i = markers.Length - 1; i >= 0; i--)
            {
                var marker = markers[i];
                long ticks = marker.TimeUtc.Ticks;
                if (ticks < state.ChartFrom || ticks > state.ChartTo)
                {
                    continue;
                }

                bool release = marker.Kind == TimelineMarkerKind.Release;
                string text = Shorten(marker.Title, release ? 30 : 22);
                float labelWidth = 14f + text.Length * 5.6f;
                float x = XOf(ticks);
                // Right of the pin, or left of it near the right edge.
                float left = x + labelWidth + 8f > width ? x - labelWidth - 6f : x + 6f;
                if (left < 0f)
                {
                    continue;
                }

                bool overlaps = false;
                foreach (var (from, to) in taken)
                {
                    if (left < to + 4f && left + labelWidth > from - 4f)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (overlaps)
                {
                    continue;
                }

                taken.Add((left, left + labelWidth));
                var label = LoggerUi.Text(text, release ? "lg-timeline__marker--release" : "lg-timeline__marker--commit");
                label.AddToClassList("lg-timeline__marker");
                label.pickingMode = PickingMode.Ignore;
                label.style.left = left;
                markerLabels.Add(label);
            }

            ShowChipRow();
        }

        /// <summary>Short windows: the commit and release labels sit in a row under the plot instead of on it.</summary>
        public void SetLabelsBelow(bool below)
        {
            if (below == labelsBelow)
            {
                return;
            }

            labelsBelow = below;
            (below ? chipRow : plot).Add(markerLabels);
            ShowChipRow();
        }

        // The row under the plot takes room only while it holds labels.
        private void ShowChipRow()
        {
            EnableInClassList("lg-timeline--chips", labelsBelow && markerLabels != null && markerLabels.childCount > 0);
        }

        private void DrawLane(MeshGenerationContext context)
        {
            laneMarks.Clear();
            var rect = timingLane.contentRect;
            if (state == null || !state.HasTimeline || rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            double slowest = 1d;
            foreach (var entry in timingEntries)
            {
                if (entry.Time >= state.ChartFrom && entry.Time <= state.ChartTo)
                {
                    slowest = Math.Max(slowest, entry.Profile.TotalMilliseconds);
                }
            }

            float height = rect.height;
            laneBatch.Rect(0f, height - 1f, rect.width, 1f, ChartColors.Grid);
            for (int i = 0; i < timingEntries.Count; i++)
            {
                var entry = timingEntries[i];
                if (entry.Time < state.ChartFrom || entry.Time > state.ChartTo)
                {
                    continue;
                }

                float x = XOf(entry.Time);
                laneMarks.Add((x, i));
                var profile = entry.Profile;
                bool hovered = i == laneHover;
                float width = hovered ? 6f : 4f;
                float column = Math.Max(3f, (float)(0.3d + 0.7d * profile.TotalMilliseconds / slowest) * (height - 3f));
                float bottom = height - 1f;
                float alpha = laneHover < 0 || hovered ? 1f : 0.5f;
                double total = Math.Max(1d, profile.TotalMilliseconds);
                // Packages from the bottom, the engine's part on top.
                TimingShare engine = null;
                foreach (var share in profile.Shares)
                {
                    if (share.Source == TimingProfile.UnitySource)
                    {
                        engine = share;
                        continue;
                    }

                    float h = (float)(share.Milliseconds / total) * column;
                    if (h < 0.4f)
                    {
                        continue;
                    }

                    var color = TimingColors.For(share.Source);
                    laneBatch.Rect(x - width * 0.5f, bottom - h, width, h, new Color(color.r, color.g, color.b, alpha));
                    bottom -= h;
                }

                if (engine != null)
                {
                    float h = (float)(engine.Milliseconds / total) * column;
                    laneBatch.Rect(x - width * 0.5f, bottom - h, width, h, new Color(TimingColors.Unity.r, TimingColors.Unity.g, TimingColors.Unity.b, alpha));
                    bottom -= h;
                }

                var kind = TimingColors.Kind(profile.Kind);
                laneBatch.Rect(x - width * 0.5f, bottom - 2f, width, 2f, new Color(kind.r, kind.g, kind.b, alpha));
            }

            laneBatch.Flush(context);
        }

        private int LaneMarkAt(float x)
        {
            int best = -1;
            float bestDistance = 7f;
            foreach (var (markX, index) in laneMarks)
            {
                float distance = Mathf.Abs(markX - x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }

            return best;
        }

        private void SetLaneHover(int index)
        {
            if (index == laneHover)
            {
                return;
            }

            laneHover = index;
            timingLane.MarkDirtyRepaint();
            if (index < 0 || index >= timingEntries.Count)
            {
                if (hover < 0)
                {
                    tip.Hide();
                }

                return;
            }

            var entry = timingEntries[index];
            var text = new System.Text.StringBuilder();
            text.Append("<b>").Append(RichText.Literal(entry.Profile.Message())).Append("</b>  <color=#8a8a8a>").Append(LoggerUi.Time(entry.Time, false)).Append("</color>");
            string legend = TimingStrip.Legend(entry.Profile, 4);
            if (legend.Length > 0)
            {
                text.Append('\n').Append(legend);
            }

            text.Append("\n<color=#6f6f6f>Click to show it</color>");
            tip.Show(text.ToString(), XOf(entry.Time) + plot.layout.x, 6f);
        }

        private static string Shorten(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

        private float Segment(float x, float bottom, float width, float height, Color color, float alpha)
        {
            if (height <= 0f)
            {
                return bottom;
            }

            height = Math.Max(1f, height);
            batch.Rect(x, bottom - height, width, height, new Color(color.r, color.g, color.b, color.a * alpha));
            return bottom - height;
        }
    }
}
