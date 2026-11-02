using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Text of any length in labels Unity can draw. A UI Toolkit element holds at most 65,535 vertices (four per glyph,
    /// more with the selection highlight) and text generation stops at 49,152 ("Generated text will be truncated…",
    /// "A VisualElement must not allocate more than 65535 vertices"), so a log of a few thousand lines (an FBX import's
    /// warnings joined into one message, a Burst error listing every folder it searched) is shown as several labels of at
    /// most <see cref="ChunkChars"/> characters each, cut at line ends where possible. The first
    /// <see cref="InitialChars"/> show at once; "Show all" adds the rest, up to <see cref="MaxChars"/> (Copy always has
    /// everything). Tooltips, drawn by IMGUI, go through <see cref="Tooltip"/>.
    /// </summary>
    internal static class LongText
    {
        /// <summary>The most characters one label gets: 16,000 vertices, a quarter of an element's budget.</summary>
        internal const int ChunkChars = 4000;

        /// <summary>What is shown before "Show all".</summary>
        internal const int InitialChars = 12_000;

        /// <summary>Where even "Show all" stops: past it a log is read by copying it.</summary>
        internal const int MaxChars = 400_000;

        /// <summary>The longest tooltip.</summary>
        internal const int TooltipChars = 1000;

        internal const string PartClass = "lg-longtext__part";
        internal const string MoreClass = "lg-longtext__more";

        /// <summary>
        /// <paramref name="text"/> in consecutive pieces of at most <paramref name="chunk"/> characters that join back into
        /// it exactly. A piece ends after a line break when one is in its second half, else after a space, else at the
        /// limit (never between the two halves of a surrogate pair).
        /// </summary>
        internal static List<string> Split(string text, int chunk = ChunkChars)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return parts;
            }

            chunk = Math.Max(2, chunk);
            int start = 0;
            while (start < text.Length)
            {
                int remaining = text.Length - start;
                if (remaining <= chunk)
                {
                    parts.Add(text.Substring(start));
                    break;
                }

                int limit = start + chunk;
                int floor = start + chunk / 2;
                int end = text.LastIndexOf('\n', limit - 1, limit - floor) + 1;
                if (end <= floor)
                {
                    end = text.LastIndexOf(' ', limit - 1, limit - floor) + 1;
                }

                if (end <= floor)
                {
                    end = limit;
                    if (char.IsHighSurrogate(text[end - 1]))
                    {
                        end--;
                    }
                }

                parts.Add(text.Substring(start, end - start));
                start = end;
            }

            return parts;
        }

        /// <summary>A tooltip of at most <see cref="TooltipChars"/> characters (null stays null).</summary>
        internal static string Tooltip(string text)
        {
            if (text == null || text.Length <= TooltipChars)
            {
                return text;
            }

            int end = TooltipChars;
            if (char.IsHighSurrogate(text[end - 1]))
            {
                end--;
            }

            return text.Substring(0, end) + "…";
        }

        /// <summary>
        /// <paramref name="text"/> shown as written (no rich text), selectable, in labels of the class
        /// <paramref name="className"/>: the first <see cref="InitialChars"/>, then "Show all".
        /// </summary>
        internal static VisualElement Build(string text, string className, Font font = null)
        {
            text = text ?? string.Empty;
            var box = LoggerUi.Box("lg-longtext");
            // Past MaxChars nothing is shown, so nothing is split either.
            var parts = Split(text.Length > MaxChars ? text.Substring(0, MaxChars) : text);
            int shown = 0;
            int index = 0;
            while (index < parts.Count && (index == 0 || shown + parts[index].Length <= InitialChars))
            {
                shown += parts[index].Length;
                box.Add(Part(parts, index++, className, font));
            }

            if (index < parts.Count)
            {
                var more = LoggerUi.Box(MoreClass);
                more.Add(LoggerUi.Text("Showing " + LoggerUi.Count(shown) + " of " + LoggerUi.Count(text.Length) + " characters.", "lg-details__note"));
                Action showAll = () =>
                {
                    more.RemoveFromHierarchy();
                    for (int next = index; next < parts.Count; next++)
                    {
                        box.Add(Part(parts, next, className, font));
                    }

                    if (text.Length > MaxChars)
                    {
                        box.Add(LoggerUi.Text("Shortened here: " + LoggerUi.Count(text.Length) + " characters in all. Copy it to get everything.", "lg-details__note"));
                    }
                };
                more.userData = showAll;
                more.Add(LoggerUi.Pill("Show all", showAll, "ghost"));
                box.Add(more);
            }

            return box;
        }

        /// <summary>What "Show all" does, for a text <see cref="Build"/> made; false when it shows everything already.</summary>
        internal static bool ShowAll(VisualElement box)
        {
            if (box?.Q(className: MoreClass)?.userData is Action showAll)
            {
                showAll();
                return true;
            }

            return false;
        }

        private static Label Part(List<string> parts, int index, string className, Font font)
        {
            // The line break between two pieces is the gap between their labels already; the text's own last line break
            // (a piece with none after it) stays, as one label would show it.
            string part = parts[index];
            int length = part.Length;
            if (index < parts.Count - 1 && length > 0 && part[length - 1] == '\n')
            {
                length--;
                if (length > 0 && part[length - 1] == '\r')
                {
                    length--;
                }
            }

            var label = new Label(RichText.Literal(length == part.Length ? part : part.Substring(0, length)));
            label.AddToClassList(PartClass);
            label.AddToClassList(className);
            label.selection.isSelectable = true;
            if (font != null)
            {
                label.style.unityFont = font;
            }

            return label;
        }
    }
}
