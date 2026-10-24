using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Orbiters.Logger.Editor
{
    /// <summary>A moment HEAD moved: a commit, a checkout, a pull… (one line of Git's reflog).</summary>
    internal readonly struct HeadMove
    {
        public readonly string Hash;
        public readonly DateTime TimeUtc;
        public readonly string Action;
        public readonly string Subject;

        public HeadMove(string hash, DateTime timeUtc, string action, string subject)
        {
            Hash = hash;
            TimeUtc = timeUtc;
            Action = action;
            Subject = subject;
        }

        public string ShortHash => Hash.Length > 7 ? Hash.Substring(0, 7) : Hash;

        public bool IsCommit => Action.StartsWith("commit", StringComparison.Ordinal);
    }

    /// <summary>
    /// The project's Git history as HEAD went through it, read from <c>.git/logs/HEAD</c> (no Git process): which
    /// commit the project was at any moment of the last months, and what that commit was. Re-read when it changes.
    /// </summary>
    internal static class GitHistory
    {
        private static readonly object gate = new object();
        private static List<HeadMove> moves = new List<HeadMove>();
        private static DateTime readAt;
        private static DateTime readWrite;
        private static string root;

        public static string ReflogPath
        {
            get
            {
                if (root == null)
                {
                    try
                    {
                        root = Path.GetFullPath(".");
                    }
                    catch (Exception)
                    {
                        root = string.Empty;
                    }
                }

                return Path.Combine(root, ".git", "logs", "HEAD");
            }
        }

        public static bool Available
        {
            get
            {
                Refresh();
                lock (gate)
                {
                    return moves.Count > 0;
                }
            }
        }

        /// <summary>Where HEAD was at <paramref name="utc"/>, or false before the history kept begins.</summary>
        public static bool At(DateTime utc, out HeadMove move, out int commitsSince)
        {
            Refresh();
            lock (gate)
            {
                move = default;
                commitsSince = 0;
                int lo = 0;
                int hi = moves.Count - 1;
                int found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (moves[mid].TimeUtc <= utc)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                if (found < 0)
                {
                    return false;
                }

                move = Describe(found);
                for (int i = found + 1; i < moves.Count; i++)
                {
                    if (moves[i].IsCommit)
                    {
                        commitsSince++;
                    }
                }

                return true;
            }
        }

        // A move that isn't a commit (a checkout, a reset) keeps the subject of the commit it went to when the
        // history has it.
        private static HeadMove Describe(int index)
        {
            var move = moves[index];
            if (move.IsCommit)
            {
                return move;
            }

            for (int i = index; i >= 0; i--)
            {
                if (moves[i].IsCommit && moves[i].Hash == move.Hash)
                {
                    return new HeadMove(move.Hash, move.TimeUtc, move.Action, moves[i].Subject);
                }
            }

            return move;
        }

        private static void Refresh()
        {
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                if ((now - readAt).TotalSeconds < 2d)
                {
                    return;
                }

                readAt = now;
            }

            try
            {
                string path = ReflogPath;
                if (!File.Exists(path))
                {
                    lock (gate)
                    {
                        moves = new List<HeadMove>();
                    }

                    return;
                }

                var write = File.GetLastWriteTimeUtc(path);
                lock (gate)
                {
                    if (write == readWrite)
                    {
                        return;
                    }
                }

                var parsed = Parse(File.ReadAllLines(path));
                lock (gate)
                {
                    moves = parsed;
                    readWrite = write;
                }
            }
            catch (Exception)
            {
                // A reflog that can't be read: no commit to show.
            }
        }

        // "<old> <new> Name <email> 1696680000 +0200\tcommit: subject"
        internal static List<HeadMove> Parse(IEnumerable<string> lines)
        {
            var list = new List<HeadMove>();
            foreach (string line in lines)
            {
                int tab = line.IndexOf('\t');
                string head = tab >= 0 ? line.Substring(0, tab) : line;
                string message = tab >= 0 ? line.Substring(tab + 1).Trim() : string.Empty;
                string[] words = head.Split(' ');
                if (words.Length < 4 || words[1].Length < 7)
                {
                    continue;
                }

                // The time is the second to last word, before the time zone.
                if (!long.TryParse(words[words.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
                {
                    continue;
                }

                string action = message;
                string subject = string.Empty;
                int colon = message.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    action = message.Substring(0, colon);
                    subject = message.Substring(colon + 2);
                }

                list.Add(new HeadMove(words[1], DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, action, subject));
            }

            // The reflog is written in order: kept as is (moves of the same second stay in their order).
            return list;
        }
    }
}
