using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Unity rich text in log messages (<c>&lt;color=red&gt;</c>, <c>&lt;b&gt;</c>…). Only known tag names count, so
    /// generic types such as <c>List&lt;int&gt;</c> stay text.
    /// </summary>
    internal static class RichText
    {
        private static readonly Regex Tags = new Regex(
            @"</?(?:b|i|u|s|color|size|material|quad|mark|sub|sup|noparse|nobr|font|font-weight|align|alpha|cspace|indent|line-height|line-indent|link|lowercase|uppercase|smallcaps|margin|mspace|pos|rotate|space|sprite|style|voffset|width|br|strikethrough|underline|gradient)(?:=[^<>\r\n]*)?\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The text without tags, or null when it has none (the common case costs one scan).</summary>
        public static string StripOrNull(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            {
                return null;
            }

            string stripped = Tags.Replace(text, string.Empty);
            return stripped.Length == text.Length ? null : stripped;
        }

        public static string Strip(string text) => StripOrNull(text) ?? text ?? string.Empty;

        /// <summary>Text shown as is: tags in it are not interpreted.</summary>
        public static string Literal(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = Printable(text);
            if (text.IndexOf('<') < 0)
            {
                return text;
            }

            return "<noparse>" + text.Replace("</noparse>", "</no​parse>") + "</noparse>";
        }

        /// <summary>
        /// <paramref name="text"/> shown as is with <paramref name="ranges"/> (start, length; sorted, not overlapping)
        /// highlighted.
        /// </summary>
        public static string Highlight(string text, List<(int start, int length)> ranges, string markColor)
        {
            if (ranges == null || ranges.Count == 0)
            {
                return Literal(text);
            }

            text = Printable(text);

            var builder = new StringBuilder(text.Length + ranges.Count * 48);
            int position = 0;
            foreach (var (start, length) in ranges)
            {
                if (start < position || length <= 0 || start + length > text.Length)
                {
                    continue;
                }

                if (start > position)
                {
                    builder.Append(Literal(text.Substring(position, start - position)));
                }

                builder.Append("<mark=").Append(markColor).Append('>');
                builder.Append(Literal(text.Substring(start, length)));
                builder.Append("</mark>");
                position = start + length;
            }

            if (position < text.Length)
            {
                builder.Append(Literal(text.Substring(position)));
            }

            return builder.ToString();
        }

        /// <summary>The first line of a text, at most <paramref name="maxLength"/> characters (layout cost grows with length).</summary>
        public static string FirstLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = Printable(text);

            int end = text.IndexOf('\n');
            if (end < 0)
            {
                end = text.Length;
            }

            if (end > 0 && text[end - 1] == '\r')
            {
                end--;
            }

            if (end > maxLength)
            {
                return text.Substring(0, maxLength) + "…";
            }

            return end == text.Length ? text : text.Substring(0, end);
        }

        /// <summary>
        /// <paramref name="text"/> with its control characters (git's field separators, escape codes) shown as "·": the
        /// editor font has no glyph for them and Unity warns at every redraw. Same length, so match ranges still fit.
        /// </summary>
        public static string Printable(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            int first = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (IsHidden(text[i]))
                {
                    first = i;
                    break;
                }
            }

            if (first < 0)
            {
                return text;
            }

            var chars = text.ToCharArray();
            for (int i = first; i < chars.Length; i++)
            {
                if (IsHidden(chars[i]))
                {
                    chars[i] = '·';
                }
            }

            return new string(chars);
        }

        private static bool IsHidden(char c) => (c < ' ' && c != '\n' && c != '\r' && c != '\t') || c == '\u007f';

        /// <summary>The second line of a text, if any (for multi-line messages).</summary>
        public static string SecondLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = Printable(text);

            int first = text.IndexOf('\n');
            if (first < 0 || first + 1 >= text.Length)
            {
                return string.Empty;
            }

            return FirstLine(text.Substring(first + 1, Math.Min(text.Length - first - 1, maxLength + 2)), maxLength);
        }
    }
}
