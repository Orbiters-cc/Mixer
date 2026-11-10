using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// What the search field asks for: a piece of text (case-insensitive unless asked), or a .NET regular expression,
    /// in messages and optionally their stack traces. Immutable, so worker threads can share it.
    /// </summary>
    internal sealed class LogQuery
    {
        public static readonly LogQuery Empty = new LogQuery(string.Empty, false, false, false);
        /// <summary>Texts a pass tests before the regular expression is worth compiling to IL.</summary>
        public const int CompileAbove = 4096;
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
        // Compiled expressions by pattern and options: typing back to an earlier pattern, or toggling an option, reuses one.
        private static readonly Dictionary<(string, RegexOptions), Regex> compiled = new Dictionary<(string, RegexOptions), Regex>();
        private Regex compiledRegex;
        private Regex foregroundRegex;
        private int timeouts;

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
                    Regex = new Regex(Text, options, MatchTimeout);
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

        /// <summary>Texts a pass could not test because the expression took too long on them (counted as no match).</summary>
        public int Timeouts => Volatile.Read(ref timeouts);

        /// <summary>
        /// Search results of <paramref name="previous"/> (per text id: 0 untested, 1 no match, 2 match) that still hold
        /// for this query, or null when none do. The same query keeps all of them. A plain text that contains the previous
        /// one (typing on) can only match texts that matched before: those are tested again, the others stay out. A plain
        /// text the previous one contains (deleting) still matches everything that matched: only the others are tested.
        /// </summary>
        public byte[] Reuse(LogQuery previous, byte[] known)
        {
            if (known == null || previous == null || previous.IsEmpty || IsEmpty)
            {
                return null;
            }

            if (SameAs(previous))
            {
                return Retest((byte[])known.Clone(), 0);
            }

            if (UseRegex || previous.UseRegex || MatchCase != previous.MatchCase || IncludeStack != previous.IncludeStack)
            {
                return null;
            }

            var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            byte retest;
            if (Text.IndexOf(previous.Text, comparison) >= 0)
            {
                retest = 2;
            }
            else if (previous.Text.IndexOf(Text, comparison) >= 0)
            {
                retest = 1;
            }
            else
            {
                return null;
            }

            return Retest((byte[])known.Clone(), retest);
        }

        /// <summary>Per text: 0 untested, 1 miss, 2 match, 3 timed out (a worker gave up: left out, said in the status),
        /// 4 timed out on the editor's thread (waits for a worker).</summary>
        internal const byte TimedOut = 3;
        internal const byte ForWorker = 4;

        // Results to test again become 0: those equal to `retest`, and every undecided one (the new pass may finish it).
        private static byte[] Retest(byte[] results, byte retest)
        {
            for (int i = 0; i < results.Length; i++)
            {
                byte value = results[i];
                if (value == ForWorker || retest != 0 && (value == retest || value == TimedOut))
                {
                    results[i] = 0;
                }
            }

            return results;
        }

        /// <summary>
        /// The expression to test many texts with: compiled to IL once (on the calling worker thread) when the pass is
        /// big enough to pay for it, else the interpreted one.
        /// </summary>
        public Regex RegexFor(int texts)
        {
            if (Regex == null || texts < CompileAbove)
            {
                return Regex;
            }

            var ready = Volatile.Read(ref compiledRegex);
            if (ready != null)
            {
                return ready;
            }

            var key = (Text, Regex.Options);
            lock (compiled)
            {
                if (!compiled.TryGetValue(key, out ready))
                {
                    try
                    {
                        ready = new Regex(Text, Regex.Options | RegexOptions.Compiled, MatchTimeout);
                    }
                    catch (Exception)
                    {
                        ready = Regex;
                    }

                    if (compiled.Count >= 16)
                    {
                        compiled.Clear();
                    }

                    compiled[key] = ready;
                }
            }

            Volatile.Write(ref compiledRegex, ready);
            return ready;
        }

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

            return Matches(text, Regex);
        }

        /// <summary>
        /// The expression for the caller's (editor's) thread: interpreted, with a short timeout, so one slow text costs a
        /// frame at most. Null for plain text.
        /// </summary>
        public Regex ForegroundRegex
        {
            get
            {
                if (Regex == null)
                {
                    return null;
                }

                var ready = Volatile.Read(ref foregroundRegex);
                if (ready == null)
                {
                    ready = new Regex(Text, Regex.Options, TimeSpan.FromMilliseconds(25));
                    Volatile.Write(ref foregroundRegex, ready);
                }

                return ready;
            }
        }

        /// <summary><see cref="Matches(string, Regex)"/>, saying whether the expression ran out of time (then it is no match).</summary>
        public bool TryMatch(string text, Regex regex, out bool timedOut)
        {
            timedOut = false;
            if (regex == null || string.IsNullOrEmpty(text))
            {
                return Matches(text, regex);
            }

            try
            {
                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref timeouts);
                timedOut = true;
                return false;
            }
        }

        /// <summary><see cref="Matches(string)"/> with the expression of <see cref="RegexFor"/>.</summary>
        public bool Matches(string text, Regex regex)
        {
            if (regex == null)
            {
                return Matches(text);
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref timeouts);
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
