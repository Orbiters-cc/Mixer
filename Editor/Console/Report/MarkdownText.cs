using System.Text.RegularExpressions;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Text from elsewhere (AI answers, log messages, package names, the note) made safe for the report's Markdown and
    /// the chat message: it reads as written, never as a link, a picture, HTML, a heading or a mention, whatever a log or
    /// the AI put in it.
    /// </summary>
    internal static class MarkdownText
    {
        // What Markdown (and Discord) read as formatting anywhere in a line: links and pictures need '[', HTML '<'. A
        // backslash only escapes punctuation, so one before a letter (C:\Users) stays as it is.
        private static readonly Regex Special = new Regex(@"\\(?=[!-/:-@\[-`{-~])|[`*_\[\]<>~|]", RegexOptions.Compiled);
        // Headings, lists, quotes and setext underlines at the start of a line.
        private static readonly Regex LineStart = new Regex(@"(?m)^([ \t]*)([#=+\-])", RegexOptions.Compiled);
        private static readonly Regex Mentions = new Regex(@"@(everyone|here|[&!]?\d{5,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = Special.Replace(text, "\\$0");
            return LineStart.Replace(text, "$1\\$2");
        }

        /// <summary>For a chat message (Discord): escaped, and @everyone, @here and &lt;@…&gt; mentions can't ping anyone.</summary>
        internal static string ForChat(string text) => Mentions.Replace(Escape(text), "@​$1");
    }
}
