using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// One part of the report as a card: its icon, title and what it holds, live numbers below, a switch on the right.
    /// The whole card toggles on press; its colour lights the edge and the icon when it is on. A card can be unavailable
    /// (no Git repository, not signed in), its reason shown instead of the switch acting.
    /// </summary>
    internal sealed class ReportCard : VisualElement
    {
        private readonly VisualElement switchElement;
        private readonly VisualElement stats;
        private readonly Label body;
        private readonly Label note;
        private readonly Action<bool> toggled;
        private string statsKey;
        private bool on;
        private bool available = true;

        /// <summary>Room for something under the numbers (the sign-in buttons).</summary>
        public VisualElement Extra { get; }

        /// <summary>Links that stay whatever the card shows (where to read more).</summary>
        public VisualElement Links { get; }

        /// <summary>Adds a quiet link at the bottom of the card.</summary>
        public void AddLink(string text, string tooltip, Action action)
        {
            var link = LoggerUi.Pill(text, action, "ghost", LoggerGlyph.External, tooltip);
            link.AddToClassList("rp-card__link");
            Links.Add(link);
            LoggerUi.Show(Links, true);
        }

        public ReportCard(ReportPart part, LoggerGlyph glyph, string title, string tag, string description, Action<bool> toggled)
            : this(part.ToString().ToLowerInvariant(), glyph, title, tag, description, toggled)
        {
        }

        /// <param name="kind">Names the card's colours in the style sheet ("rp-card--anonymize").</param>
        public ReportCard(string kind, LoggerGlyph glyph, string title, string tag, string description, Action<bool> toggled)
        {
            this.toggled = toggled;
            AddToClassList("rp-card");
            AddToClassList("rp-card--" + kind);
            var head = LoggerUi.Box("rp-card__head");
            var tile = LoggerUi.Box("rp-card__tile", PickingMode.Ignore);
            tile.Add(new LoggerIcon(glyph));
            head.Add(tile);
            var titles = LoggerUi.Box("rp-card__titles", PickingMode.Ignore);
            titles.Add(LoggerUi.Text(title, "rp-card__title"));
            if (!string.IsNullOrEmpty(tag))
            {
                titles.Add(LoggerUi.Text(tag, "rp-card__tag"));
            }

            head.Add(titles);
            switchElement = LoggerUi.Box("lg-switch", PickingMode.Ignore);
            switchElement.AddToClassList("rp-card__switch");
            switchElement.Add(LoggerUi.Box("lg-switch__knob", PickingMode.Ignore));
            head.Add(switchElement);
            Add(head);

            body = LoggerUi.Text(description, "rp-card__body");
            body.pickingMode = PickingMode.Ignore;
            Add(body);
            stats = LoggerUi.Box("rp-card__stats", PickingMode.Ignore);
            Add(stats);
            note = LoggerUi.Text(string.Empty, "rp-card__note");
            note.pickingMode = PickingMode.Ignore;
            LoggerUi.Show(note, false);
            Add(note);
            Extra = LoggerUi.Box("rp-card__extra");
            LoggerUi.Show(Extra, false);
            Add(Extra);
            Links = LoggerUi.Box("rp-card__links");
            LoggerUi.Show(Links, false);
            Add(Links);
            focusable = true;
            LoggerUi.Press(this, () =>
            {
                if (available)
                {
                    toggled?.Invoke(!on);
                }
            });
            Loading();
        }

        public bool On => on;

        public void SetOn(bool value)
        {
            on = value;
            switchElement.EnableInClassList("lg-switch--on", value && available);
            EnableInClassList("rp-card--on", value && available);
            EnableInClassList("rp-card--off", !value || !available);
        }

        public void SetAvailable(bool value)
        {
            available = value;
            EnableInClassList("rp-card--disabled", !value);
            SetOn(on);
        }

        public void SetDescription(string text) => body.text = text;

        /// <summary>A line in the warning colour under the numbers, or none.</summary>
        public void SetNote(string text)
        {
            note.text = text ?? string.Empty;
            LoggerUi.Show(note, !string.IsNullOrEmpty(text));
        }

        /// <summary>Two soft bars breathing while the numbers are read.</summary>
        public void Loading()
        {
            statsKey = null;
            stats.Clear();
            for (int i = 0; i < 2; i++)
            {
                var bar = LoggerUi.Box("rp-shimmer", PickingMode.Ignore);
                bar.style.width = i == 0 ? 96 : 140;
                stats.Add(bar);
                bar.schedule.Execute(() => bar.ToggleInClassList("rp-shimmer--bright")).Every(600).StartingIn(i * 200);
            }
        }

        /// <summary>
        /// Shows the numbers. The same numbers again change nothing; new ones rise in one after another, counting up.
        /// </summary>
        public void SetStats(IReadOnlyList<ReportStat> values)
        {
            string key = string.Join("|", System.Linq.Enumerable.Select(values, v => v.Key));
            if (key == statsKey)
            {
                return;
            }

            bool first = statsKey == null;
            statsKey = key;
            stats.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                var chip = LoggerUi.Box("rp-stat", PickingMode.Ignore);
                if (!string.IsNullOrEmpty(value.Kind))
                {
                    chip.AddToClassList("rp-stat--" + value.Kind);
                }

                if (value.Glyph.HasValue)
                {
                    chip.Add(new LoggerIcon(value.Glyph.Value));
                }

                var label = LoggerUi.Text(value.Text, "rp-stat__text");
                chip.Add(label);
                stats.Add(chip);
                if (first)
                {
                    LoggerUi.Enter(chip, "rp-stat--enter", 30 + i * 70);
                    if (value.Number.HasValue && value.Number.Value > 0)
                    {
                        var format = value.Format;
                        CountUp.Play(label, value.Number.Value, n => format(n));
                    }
                }
            }
        }
    }

    /// <summary>One number on a card: "214 errors", with an icon and a colour.</summary>
    internal readonly struct ReportStat
    {
        public readonly string Text;
        public readonly LoggerGlyph? Glyph;
        public readonly string Kind;
        public readonly long? Number;
        public readonly Func<long, string> Format;

        private ReportStat(string text, LoggerGlyph? glyph, string kind, long? number, Func<long, string> format)
        {
            Text = text;
            Glyph = glyph;
            Kind = kind;
            Number = number;
            Format = format;
        }

        public string Key => Kind + ":" + Text;

        public static ReportStat Plain(string text, LoggerGlyph? glyph = null, string kind = null) => new ReportStat(text, glyph, kind, null, null);

        /// <summary>A count that counts up when it first shows.</summary>
        public static ReportStat Counted(long value, Func<long, string> format, LoggerGlyph? glyph = null, string kind = null) =>
            new ReportStat(format(value), glyph, kind, value, format);
    }
}
