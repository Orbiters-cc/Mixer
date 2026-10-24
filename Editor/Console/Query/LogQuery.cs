using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// What the search field asks for: a piece of text (case-insensitive unless asked), or a .NET regular expression,
    /// in messages and optionally their stack traces. Immutable, so worker threads can share it.
    /// </summary>
    internal sealed class LogQuery
    {
        public static readonly LogQuery Empty = new LogQuery(string.Empty, false, false, false);

        public LogQuery(string text, bool useRegex, bool matchCase, bool includeStack)
        {
            Text = text ?? string.Empty;
            UseRegex = useRegex;
            MatchCase = matchCase;
            IncludeStack = includeStack;
            if (UseRegex && Text.Length > 0)
            {
                try
                {
                    var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                    Regex = new Regex(Text, options, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException exception)
                {
                    Error = Tidy(exception.Message);
                }
            }
        }

        public string Text { get; }
        public bool UseRegex { get; }
        public bool MatchCase { get; }
        public bool IncludeStack { get; }
        public Regex Regex { get; }
        /// <summary>Why the regular expression is invalid, or null.</summary>
        public string Error { get; }

        public bool IsEmpty => Text.Length == 0 || Error != null;

        public bool SameAs(LogQuery other) =>
            other != null && Text == other.Text && UseRegex == other.UseRegex && MatchCase == other.MatchCase && IncludeStack == other.IncludeStack;

        public bool Matches(string text)
        {
            if (IsEmpty)
            {
                return true;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (Regex == null)
            {
                return text.IndexOf(Text, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;
            }

            try
            {
                return Regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>Where the query matches in <paramref name="text"/> (start, length), at most <paramref name="max"/> ranges.</summary>
        public void FindMatches(string text, List<(int start, int length)> ranges, int max = 32)
        {
            ranges.Clear();
            if (IsEmpty || string.IsNullOrEmpty(text))
            {
                return;
            }

            if (Regex == null)
            {
                var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int index = 0;
                while (ranges.Count < max && index < text.Length)
                {
                    int found = text.IndexOf(Text, index, comparison);
                    if (found < 0)
                    {
                        break;
                    }

                    ranges.Add((found, Text.Length));
                    index = found + Math.Max(1, Text.Length);
                }

                return;
            }

            try
            {
                for (var match = Regex.Match(text); match.Success && ranges.Count < max; match = match.NextMatch())
                {
                    if (match.Length > 0)
                    {
                        ranges.Add((match.Index, match.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                ranges.Clear();
            }
        }

        private static string Tidy(string message)
        {
            // "parsing \"(abc\" - Not enough )'s." → "Not enough )'s."
            int dash = message.LastIndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? message.Substring(dash + 3) : message;
        }
    }
}
