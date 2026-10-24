using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Puts one change of a file back as its last commit had it (the "»" of JetBrains' diff), in the file on disk only.
    /// The file must still have exactly the lines the diff showed there, or nothing is written. Line endings, the byte
    /// order mark and the final newline are kept as the file has them, and the bytes before and after are returned so
    /// the change can be undone.
    /// </summary>
    internal static class UnitGitBlockRevert
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <param name="rightStart">1-based line where the block's current lines start (where to insert when it has none).</param>
        /// <param name="current">The block's lines as the diff showed them in the file.</param>
        /// <param name="previous">The lines to put back.</param>
        /// <param name="previousEndsWithoutNewline">The previous version had no newline after its last line.</param>
        public static GitCommandResult Apply(string fullPath, int rightStart, IList<string> current, IList<string> previous,
            bool previousEndsWithoutNewline, out byte[] before, out byte[] after)
        {
            before = after = null;
            if (!File.Exists(fullPath)) return Failure("The file is not on disk anymore.");
            before = File.ReadAllBytes(fullPath);
            bool bom = before.Length >= 3 && before[0] == 0xEF && before[1] == 0xBB && before[2] == 0xBF;
            string text;
            try
            {
                text = StrictUtf8.GetString(before, bom ? 3 : 0, before.Length - (bom ? 3 : 0));
            }
            catch (ArgumentException)
            {
                return Failure("The file is not UTF-8 text: put it back with Rollback instead.");
            }

            var lines = Split(text, out string newline);
            int start = rightStart - 1;
            if (start < 0 || start + current.Count > lines.Count)
                return Failure("The file changed since this diff was read. Refresh and try again.");
            for (int i = 0; i < current.Count; i++)
                if (!string.Equals(lines[start + i].Text, current[i], StringComparison.Ordinal))
                    return Failure("The file changed since this diff was read. Refresh and try again.");

            bool reachesEnd = start + current.Count == lines.Count;
            string ending = current.Count > 0 && lines[start].Ending.Length > 0 ? lines[start].Ending : newline;
            var replacement = previous.Select(line => new Line { Text = line, Ending = ending }).ToList();
            lines.RemoveRange(start, current.Count);
            lines.InsertRange(start, replacement);
            // The last line ends the way the previous version's did once the block reaches the end of the file.
            if (reachesEnd && lines.Count > 0)
                lines[lines.Count - 1].Ending = previousEndsWithoutNewline ? string.Empty : ending;

            var builder = new StringBuilder(text.Length + 64);
            foreach (var line in lines) builder.Append(line.Text).Append(line.Ending);
            byte[] body = StrictUtf8.GetBytes(builder.ToString());
            after = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
            Write(fullPath, after);
            return new GitCommandResult { StandardOutput = "Reverted the change." };
        }

        /// <summary>Writes <paramref name="before"/> back, as long as the file is still exactly <paramref name="after"/>.</summary>
        public static GitCommandResult Undo(string fullPath, byte[] before, byte[] after)
        {
            if (before == null || after == null || !File.Exists(fullPath)) return Failure("Nothing to undo.");
            if (!File.ReadAllBytes(fullPath).SequenceEqual(after)) return Failure("The file changed since: nothing was undone.");
            Write(fullPath, before);
            return new GitCommandResult { StandardOutput = "Undone." };
        }

        // Next to the file, then swapped in: a failed write never leaves half a file.
        private static void Write(string fullPath, byte[] bytes)
        {
            string temp = fullPath + ".unitgit-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllBytes(temp, bytes);
            try
            {
                File.Replace(temp, fullPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temp, fullPath, true);
                File.Delete(temp);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private sealed class Line
        {
            public string Text;
            public string Ending;
        }

        // Lines with the ending each one has; the most common ending is the one new lines get.
        private static List<Line> Split(string text, out string newline)
        {
            var lines = new List<Line>();
            int crlf = 0, lf = 0, begin = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                bool cr = i > begin && text[i - 1] == '\r';
                if (cr) crlf++;
                else lf++;
                lines.Add(new Line { Text = text.Substring(begin, i - begin - (cr ? 1 : 0)), Ending = cr ? "\r\n" : "\n" });
                begin = i + 1;
            }
            if (begin < text.Length) lines.Add(new Line { Text = text.Substring(begin), Ending = string.Empty });
            newline = crlf > lf ? "\r\n" : "\n";
            return lines;
        }

        private static GitCommandResult Failure(string message) => new GitCommandResult { ExitCode = 1, StandardError = message };
    }
}
