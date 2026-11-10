using System;
using System.Collections.Generic;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>Shorter path lists for Git: a whole folder as one pathspec where that says exactly the same thing.</summary>
    internal static class UnitGitPathspecs
    {
        /// <summary>
        /// <paramref name="send"/> with every folder all of whose changes are covered replaced by the folder itself.
        /// <paramref name="covered"/> are the files the user chose (a ticked folder, a group), <paramref name="send"/> the
        /// ones among them Git must act on, <paramref name="changed"/> every path Git reported changed (both paths of a
        /// rename). A folder is sent whole only when no change under it is left out, so a filter that hides part of a folder,
        /// or a file of it left unticked, keeps the file list. A folder stands for at least two files; the project root is never
        /// sent as a folder. Order follows <paramref name="send"/>.
        /// </summary>
        internal static List<string> Collapse(IList<string> send, IEnumerable<string> covered, IEnumerable<string> changed)
        {
            var result = new List<string>();
            if (send == null || send.Count == 0) return result;
            var sendSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in send) if (!string.IsNullOrEmpty(path)) sendSet.Add(path);
            var coveredSet = new HashSet<string>(sendSet, StringComparer.Ordinal);
            if (covered != null) foreach (string path in covered) if (!string.IsNullOrEmpty(path)) coveredSet.Add(path);
            var all = new HashSet<string>(coveredSet, StringComparer.Ordinal);
            if (changed != null) foreach (string path in changed) if (!string.IsNullOrEmpty(path)) all.Add(path);

            var total = new Dictionary<string, int>(StringComparer.Ordinal);
            var coveredCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var sendCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in all) CountFolders(path, total);
            foreach (string path in coveredSet) CountFolders(path, coveredCount);
            foreach (string path in sendSet) CountFolders(path, sendCount);

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in send)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string pathspec = path;
                // The outermost folder that is complete and stands for at least two files.
                for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
                {
                    string folder = path.Substring(0, slash);
                    if (sendCount.TryGetValue(folder, out int sending) && sending >= 2 &&
                        coveredCount.TryGetValue(folder, out int done) && total.TryGetValue(folder, out int changes) && done == changes)
                    {
                        pathspec = folder;
                        break;
                    }
                }

                if (emitted.Add(pathspec)) result.Add(pathspec);
            }

            return result;
        }

        private static void CountFolders(string path, Dictionary<string, int> counts)
        {
            for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                string folder = path.Substring(0, slash);
                counts[folder] = counts.TryGetValue(folder, out int count) ? count + 1 : 1;
            }
        }
    }
}
