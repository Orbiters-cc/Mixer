using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>Colours of the sources in timing charts: every tool gets its own, even the Orbiters ones.</summary>
    internal static class TimingColors
    {
        public static readonly Color Unity = SourceCatalog.Hex("#5d6573");

        private static readonly Dictionary<string, Color> Known = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "Unity", Unity },
            { "Project", SourceCatalog.Hex("#d4d4d4") },
            { "VRChat SDK", SourceCatalog.Hex("#6aa7ff") },
            { "VRCFury", SourceCatalog.Hex("#b890ff") },
            { "Modular Avatar", SourceCatalog.Hex("#4fd1c5") },
            { "NDMF", SourceCatalog.Hex("#a3e635") },
            { "Poiyomi", SourceCatalog.Hex("#f78fb3") },
            { "MCP for Unity", SourceCatalog.Hex("#2ea3ff") },
            { "MCB", SourceCatalog.Hex("#3cf29a") },
            { "My Avatar", SourceCatalog.Hex("#38d6f5") },
            { "ReFit", SourceCatalog.Hex("#ff7ac0") },
            { "Mixer Git", SourceCatalog.Hex("#ff9b54") },
            { "Mixer", SourceCatalog.Hex("#ffd34e") },
            { "Mixer console", SourceCatalog.Hex("#9fb3c8") },
            { "Orbiters Toolkit", SourceCatalog.Hex("#2fd39b") },
            { "Touch World", SourceCatalog.Hex("#ff6b6b") },
            { "UnityPackageManager", SourceCatalog.Hex("#e879f9") },
            { "JetBrains Rider Editor", SourceCatalog.Hex("#f5a524") },
            { "Gesture Manager", SourceCatalog.Hex("#f0a35e") },
        };

        public static Color For(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return Unity;
            }

            return Known.TryGetValue(source, out var color) ? color : SourceCatalog.ColorFor(source.Trim().ToLowerInvariant());
        }

        public static Color Kind(TimingKind kind)
        {
            switch (kind)
            {
                case TimingKind.PlayMode:
                    return SourceCatalog.Hex("#3cf29a");
                case TimingKind.AvatarBuild:
                case TimingKind.AvatarUpload:
                    return SourceCatalog.Hex("#f0a35e");
                default:
                    return SourceCatalog.Hex("#b890ff");
            }
        }

        public static string Hex(Color color) => "#" + ColorUtility.ToHtmlStringRGB(color);

        /// <summary>A source chip for a timing share (not a log's source: it is not added to the store's sources).</summary>
        public static LogSource Source(string name) => new LogSource(-1, (name ?? string.Empty).Trim().ToLowerInvariant(), name ?? string.Empty, For(name));
    }

    /// <summary>
    /// One timing as a stacked bar: each source's share in its colour, the engine's at the end in grey, and the main
    /// sources named after it. <see cref="Scale"/> shortens the bar so rows can be compared with the slowest one.
    /// </summary>
    internal sealed class TimingStrip : VisualElement
    {
        private readonly Label legend;
        private TimingProfile profile;
        private float scale = 1f;
        private int hover = -1;
        private readonly List<(float from, float to, TimingShare share)> spans = new List<(float, float, TimingShare)>();
        private readonly bool interactive;
        private readonly FloatingTip tip;

        public TimingStrip(bool interactive, string className)
        {
            this.interactive = interactive;
            AddToClassList("lg-timing-strip");
            AddToClassList(className);
            pickingMode = interactive ? PickingMode.Position : PickingMode.Ignore;
            generateVisualContent += Draw;
            legend = LoggerUi.Text(string.Empty, "lg-timing-strip__legend");
            legend.pickingMode = PickingMode.Ignore;
            legend.enableRichText = true;
            Add(legend);
            if (interactive)
            {
                tip = new FloatingTip(this);
                RegisterCallback<PointerMoveEvent>(evt => SetHover(evt.localPosition.x));
                RegisterCallback<PointerLeaveEvent>(_ => SetHover(-1f));
            }

            RegisterCallback<GeometryChangedEvent>(_ => PlaceLegend());
        }

        /// <summary>Width of the bar as a share of the element (the rest holds the legend).</summary>
        public float BarFraction { get; set; } = 0.55f;

        public bool ShowLegend { get; set; } = true;

        public void Set(TimingProfile value, float barScale)
        {
            bool changed = !ReferenceEquals(value, profile) || Math.Abs(barScale - scale) > 0.001f;
            profile = value;
            scale = Mathf.Clamp(barScale, 0.04f, 1f);
            if (!changed)
            {
                return;
            }

            legend.text = ShowLegend ? Legend(value, 3) : string.Empty;
            legend.style.display = ShowLegend ? DisplayStyle.Flex : DisplayStyle.None;
            PlaceLegend();
            MarkDirtyRepaint();
        }

        /// <summary>"VRCFury 1.0 s · MCB 0.52 s · VRChat SDK 0.50 s": the largest shares, the engine left out.</summary>
        public static string Legend(TimingProfile profile, int count)
        {
            if (profile == null)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            int shown = 0;
            int others = 0;
            foreach (var share in profile.Shares)
            {
                if (share.Source == TimingProfile.UnitySource)
                {
                    continue;
                }

                if (shown >= count)
                {
                    others++;
                    continue;
                }

                if (shown > 0)
                {
                    text.Append("<color=#5f5f5f>  ·  </color>");
                }

                text.Append("<color=").Append(TimingColors.Hex(TimingColors.For(share.Source))).Append('>').Append(RichText.Literal(share.Source))
                    .Append("</color> <color=#a8a8a8>").Append(TimingProfile.FormatDuration(share.Milliseconds)).Append("</color>");
                shown++;
            }

            if (others > 0)
            {
                text.Append("<color=#6f6f6f>  +").Append(others).Append("</color>");
            }

            if (shown == 0)
            {
                text.Append("<color=#7c7c7c>Unity only").Append(profile.Kind == TimingKind.ScriptReload && !ReloadTimings.Detailed ? " (measuring by package is off)" : string.Empty).Append("</color>");
            }

            return text.ToString();
        }

        private float BarWidth => Math.Max(6f, (float.IsNaN(contentRect.width) ? 0f : contentRect.width) * (ShowLegend ? BarFraction : 1f) * scale);

        private void PlaceLegend()
        {
            if (!ShowLegend)
            {
                return;
            }

            legend.style.left = BarWidth + 8f;
        }

        private void SetHover(float x)
        {
            int index = -1;
            for (int i = 0; i < spans.Count && x >= 0f; i++)
            {
                if (x >= spans[i].from && x <= spans[i].to)
                {
                    index = i;
                    break;
                }
            }

            if (index == hover)
            {
                return;
            }

            hover = index;
            MarkDirtyRepaint();
            if (tip == null)
            {
                return;
            }

            if (index < 0)
            {
                tip.Hide();
                return;
            }

            var share = spans[index].share;
            double percent = profile.TotalMilliseconds > 0 ? share.Milliseconds / profile.TotalMilliseconds * 100d : 0d;
            var text = new StringBuilder();
            text.Append("<b><color=").Append(TimingColors.Hex(TimingColors.For(share.Source))).Append('>').Append(RichText.Literal(share.Source)).Append("</color></b>  ")
                .Append(TimingProfile.FormatDuration(share.Milliseconds)).Append("  <color=#8a8a8a>").Append(percent.ToString("0")).Append("%</color>");
            int lines = 0;
            foreach (var part in share.Parts)
            {
                if (lines++ >= 4)
                {
                    break;
                }

                text.Append("\n<color=#8a8a8a>").Append(RichText.Literal(part.Phase)).Append("</color>  ").Append(RichText.Literal(Shorten(part.Label, 48)))
                    .Append("  <color=#b9b9b9>").Append(TimingProfile.FormatDuration(part.Milliseconds)).Append("</color>");
            }

            // Under the bar, so the bar and its neighbours stay visible.
            float center = (spans[index].from + spans[index].to) * 0.5f;
            tip.Show(text.ToString(), center, contentRect.height + 6f);
        }

        private static string Shorten(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

        private void Draw(MeshGenerationContext context)
        {
            spans.Clear();
            var rect = contentRect;
            if (profile == null || rect.width <= 0f || rect.height <= 0f || profile.TotalMilliseconds <= 0d)
            {
                return;
            }

            float barHeight = Math.Min(rect.height, interactive ? 14f : 7f);
            float y = rect.y + (rect.height - barHeight) * 0.5f;
            float width = BarWidth;
            float radius = Math.Min(barHeight * 0.5f, 4f);
            var painter = context.painter2D;
            var background = new Color(1f, 1f, 1f, 0.05f);
            Segment(painter, rect.x, rect.x + width, y, barHeight, radius, true, true, background);

            // Packages first, the engine last: the bar reads "who took the time, then Unity".
            var ordered = new List<TimingShare>(profile.Shares.Count);
            TimingShare engine = null;
            foreach (var share in profile.Shares)
            {
                if (share.Source == TimingProfile.UnitySource)
                {
                    engine = share;
                }
                else
                {
                    ordered.Add(share);
                }
            }

            if (engine != null)
            {
                ordered.Add(engine);
            }

            float x = rect.x;
            double total = Math.Max(1d, profile.TotalMilliseconds);
            for (int i = 0; i < ordered.Count; i++)
            {
                var share = ordered[i];
                float w = (float)(share.Milliseconds / total * width);
                if (w < 0.6f)
                {
                    continue;
                }

                bool last = i == ordered.Count - 1 || x + w >= rect.x + width - 0.5f;
                var color = TimingColors.For(share.Source);
                float alpha = hover < 0 || hover == spans.Count ? 1f : 0.45f;
                Segment(painter, x, Math.Min(rect.x + width, x + w), y, barHeight, radius, x <= rect.x + 0.01f, last, new Color(color.r, color.g, color.b, alpha));
                spans.Add((x - rect.x, x - rect.x + w, share));
                // A hairline between segments keeps neighbours of similar colours apart.
                if (!last && w > 3f)
                {
                    Segment(painter, x + w - 1f, x + w, y, barHeight, 0f, false, false, new Color(0.11f, 0.11f, 0.11f, 0.85f));
                }

                x += w;
            }
        }

        private static void Segment(Painter2D painter, float x0, float x1, float y, float height, float radius, bool roundLeft, bool roundRight, Color color)
        {
            if (x1 - x0 <= 0f)
            {
                return;
            }

            float r = Math.Min(radius, Math.Min((x1 - x0) * 0.5f, height * 0.5f));
            float left = roundLeft ? r : 0f;
            float right = roundRight ? r : 0f;
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x0 + left, y));
            painter.LineTo(new Vector2(x1 - right, y));
            if (right > 0f)
            {
                painter.Arc(new Vector2(x1 - right, y + right), right, 270f, 360f);
                painter.LineTo(new Vector2(x1, y + height - right));
                painter.Arc(new Vector2(x1 - right, y + height - right), right, 0f, 90f);
            }
            else
            {
                painter.LineTo(new Vector2(x1, y));
                painter.LineTo(new Vector2(x1, y + height));
            }

            painter.LineTo(new Vector2(x0 + left, y + height));
            if (left > 0f)
            {
                painter.Arc(new Vector2(x0 + left, y + height - left), left, 90f, 180f);
                painter.LineTo(new Vector2(x0, y + left));
                painter.Arc(new Vector2(x0 + left, y + left), left, 180f, 270f);
            }
            else
            {
                painter.LineTo(new Vector2(x0, y + height));
                painter.LineTo(new Vector2(x0, y));
            }

            painter.ClosePath();
            painter.Fill();
        }
    }

    /// <summary>
    /// The last timings of one kind as stacked columns, the one shown highlighted: how reloads, Play Mode or uploads
    /// evolve, and which package changed. Hover reads one out, a click selects its log.
    /// </summary>
    internal sealed class TimingHistoryChart : VisualElement
    {
        private const int MaxColumns = 40;
        private readonly QuadBatch batch = new QuadBatch();
        private readonly FloatingTip tip;
        private readonly List<TimingEntry> entries = new List<TimingEntry>();
        private readonly List<string> palette = new List<string>();
        private int current = -1;
        private int hover = -1;
        private long lastPress;

        public event Action<int> MessageClicked;

        public TimingHistoryChart()
        {
            AddToClassList("lg-timing-history");
            generateVisualContent += Draw;
            tip = new FloatingTip(this);
            RegisterCallback<PointerMoveEvent>(evt => SetHover(ColumnAt(evt.localPosition.x)));
            RegisterCallback<PointerLeaveEvent>(_ => SetHover(-1));
            RegisterCallback<PointerDownEvent>(evt =>
            {
                int column = ColumnAt(evt.localPosition.x);
                if (evt.button == 0 && column >= 0 && column < entries.Count && column != current)
                {
                    evt.StopPropagation();
                    if (!LoggerUi.IsRepeatPress(ref lastPress))
                    {
                        MessageClicked?.Invoke(entries[column].Message);
                    }
                }
            });
        }

        /// <summary>The sources coloured in the chart, largest overall first (the others are grey).</summary>
        public IReadOnlyList<string> Palette => palette;

        public void Set(IReadOnlyList<TimingEntry> all, int message)
        {
            entries.Clear();
            current = -1;
            TimingKind? kind = null;
            foreach (var entry in all)
            {
                if (entry.Message == message)
                {
                    kind = entry.Profile.Kind;
                    break;
                }
            }

            if (kind == null)
            {
                MarkDirtyRepaint();
                return;
            }

            // The shown timing and the ones before it (and a few after, when it isn't the latest).
            var same = new List<TimingEntry>();
            int at = -1;
            foreach (var entry in all)
            {
                if (entry.Profile.Kind != kind.Value)
                {
                    continue;
                }

                if (entry.Message == message)
                {
                    at = same.Count;
                }

                same.Add(entry);
            }

            int end = Math.Min(same.Count, Math.Max(at + 1 + 6, MaxColumns));
            int start = Math.Max(0, end - MaxColumns);
            for (int i = start; i < end; i++)
            {
                entries.Add(same[i]);
                if (same[i].Message == message)
                {
                    current = entries.Count - 1;
                }
            }

            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                foreach (var share in entry.Profile.Shares)
                {
                    if (share.Source == TimingProfile.UnitySource)
                    {
                        continue;
                    }

                    totals.TryGetValue(share.Source, out double sum);
                    totals[share.Source] = sum + share.Milliseconds;
                }
            }

            var ranked = new List<KeyValuePair<string, double>>(totals);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            palette.Clear();
            for (int i = 0; i < ranked.Count && i < 7; i++)
            {
                palette.Add(ranked[i].Key);
            }

            MarkDirtyRepaint();
        }

        public int Count => entries.Count;

        private int ColumnAt(float x)
        {
            float width = contentRect.width;
            if (entries.Count == 0 || width <= 0f || x < 0f || x > width)
            {
                return -1;
            }

            return Mathf.Clamp((int)(x / width * entries.Count), 0, entries.Count - 1);
        }

        private void SetHover(int column)
        {
            if (column == hover)
            {
                return;
            }

            hover = column;
            MarkDirtyRepaint();
            if (column < 0)
            {
                tip.Hide();
                return;
            }

            var entry = entries[column];
            var text = new StringBuilder();
            text.Append("<b>").Append(TimingProfile.FormatDuration(entry.Profile.TotalMilliseconds)).Append("</b>  <color=#8a8a8a>")
                .Append(LoggerUi.Moment(entry.Time)).Append("</color>");
            if (!string.IsNullOrEmpty(entry.Profile.Subject))
            {
                text.Append("  ").Append(RichText.Literal(entry.Profile.Subject));
            }

            string legend = TimingStrip.Legend(entry.Profile, 3);
            if (legend.Length > 0)
            {
                text.Append('\n').Append(legend);
            }

            if (column != current)
            {
                text.Append("\n<color=#6f6f6f>Click to show it</color>");
            }

            float x = contentRect.width * (column + 0.5f) / entries.Count;
            tip.Show(text.ToString(), x, 4f);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (entries.Count == 0 || rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            double max = 1d;
            foreach (var entry in entries)
            {
                max = Math.Max(max, entry.Profile.TotalMilliseconds);
            }

            float slot = rect.width / Math.Max(entries.Count, 12);
            float columnWidth = Math.Max(2f, Math.Min(18f, slot - (slot > 6f ? 3f : 1f)));
            float plot = rect.height - 2f;
            batch.Rect(rect.x, rect.yMax - 1f, rect.width, 1f, ChartColors.Grid);
            batch.Rect(rect.x, rect.y + plot * 0.5f, rect.width, 1f, new Color(1f, 1f, 1f, 0.03f));
            float step = rect.width / entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                var profile = entries[i].Profile;
                float x = rect.x + i * step + (step - columnWidth) * 0.5f;
                if (i == hover || i == current)
                {
                    batch.Rect(rect.x + i * step, rect.y, step, rect.height, new Color(1f, 1f, 1f, i == current ? 0.07f : 0.045f));
                }

                float height = Math.Max(2f, (float)(profile.TotalMilliseconds / max) * plot);
                float alpha = i == current || i == hover ? 1f : 0.62f;
                float bottom = rect.y + plot;
                double rest = profile.TotalMilliseconds;
                foreach (string source in palette)
                {
                    double ms = 0d;
                    foreach (var share in profile.Shares)
                    {
                        if (string.Equals(share.Source, source, StringComparison.OrdinalIgnoreCase))
                        {
                            ms = share.Milliseconds;
                            break;
                        }
                    }

                    if (ms <= 0d)
                    {
                        continue;
                    }

                    rest -= ms;
                    float h = (float)(ms / profile.TotalMilliseconds) * height;
                    var color = TimingColors.For(source);
                    batch.Rect(x, bottom - h, columnWidth, h, new Color(color.r, color.g, color.b, alpha));
                    bottom -= h;
                }

                if (rest > 0d)
                {
                    float h = (float)(rest / profile.TotalMilliseconds) * height;
                    batch.Rect(x, bottom - h, columnWidth, h, new Color(TimingColors.Unity.r, TimingColors.Unity.g, TimingColors.Unity.b, alpha * 0.9f));
                }

                if (i == current)
                {
                    var accent = TimingColors.Kind(profile.Kind);
                    batch.Rect(x, rect.y + plot - height - 3f, columnWidth, 2f, accent);
                }
            }

            batch.Flush(context);
        }
    }

    /// <summary>A timing log of the store: when, which message, what it measured.</summary>
    internal readonly struct TimingEntry
    {
        public readonly int Message;
        public readonly long Time;
        public readonly TimingProfile Profile;

        public TimingEntry(int message, long time, TimingProfile profile)
        {
            Message = message;
            Time = time;
            Profile = profile;
        }
    }

    /// <summary>
    /// The timing logs of the store, parsed once: for the rows' bars (scaled to the slowest of their kind), the
    /// timeline's lane and the details' history. Messages are only added, so new ones are read as they come.
    /// </summary>
    internal sealed class TimingIndex
    {
        private readonly List<TimingEntry> entries = new List<TimingEntry>();
        private readonly Dictionary<int, TimingProfile> byMessage = new Dictionary<int, TimingProfile>();
        private readonly double[] slowest = new double[4];
        private LogStore store;
        private int epoch = -1;
        private int scanned;
        private int version = -1;

        public IReadOnlyList<TimingEntry> Entries => entries;

        public int Changes { get; private set; }

        public void Update(LogStore source)
        {
            if (source == null)
            {
                return;
            }

            if (!ReferenceEquals(source, store) || source.Epoch != epoch)
            {
                store = source;
                epoch = source.Epoch;
                entries.Clear();
                byMessage.Clear();
                Array.Clear(slowest, 0, slowest.Length);
                scanned = 0;
                version = -1;
                Changes++;
            }

            if (source.Version == version)
            {
                return;
            }

            version = source.Version;
            bool added = false;
            for (int i = scanned; i < source.MessageCount; i++)
            {
                ref var message = ref source.Message(i);
                if (message.Stack <= 0 || message.Count == 0)
                {
                    continue;
                }

                string stack = source.Texts[message.Stack];
                if (!TimingProfile.IsTimings(stack))
                {
                    continue;
                }

                var profile = TimingProfile.Parse(stack);
                if (profile == null)
                {
                    continue;
                }

                byMessage[i] = profile;
                entries.Add(new TimingEntry(i, source.TimeAt(Math.Max(0, message.Last)), profile));
                slowest[(int)profile.Kind] = Math.Max(slowest[(int)profile.Kind], profile.TotalMilliseconds);
                added = true;
            }

            scanned = source.MessageCount;
            if (added)
            {
                entries.Sort((a, b) => a.Time.CompareTo(b.Time));
                Changes++;
            }
        }

        public bool TryGet(int message, out TimingProfile profile) => byMessage.TryGetValue(message, out profile);

        public double Slowest(TimingKind kind) => Math.Max(1d, slowest[(int)kind]);
    }
}
