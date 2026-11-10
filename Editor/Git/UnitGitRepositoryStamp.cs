using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// A cheap "did anything change in Git" test: the times and sizes of the files Git rewrites when the repository moves
    /// (the index on staging, HEAD and its reflog on commit and checkout, packed-refs, FETCH_HEAD on fetch, the stash's
    /// reflog, the config, the folders loose refs are created in). Equal stamps: nothing Git keeps changed, so only the
    /// working tree can have (which the editor reports itself). Taken without starting any process once Git's paths are known.
    /// </summary>
    internal static class UnitGitRepositoryStamp
    {
        internal static readonly string[] Names =
        {
            "index", "HEAD", "packed-refs", "logs/HEAD", "FETCH_HEAD", "ORIG_HEAD", "MERGE_HEAD", "logs/refs/stash", "config",
            "refs/heads", "refs/tags", "refs/remotes", "reftable"
        };

        /// <summary>The stamp of every file in <see cref="Names"/> (resolving Git's paths if needed: run off the main thread).</summary>
        internal static string Read(string projectRoot)
        {
            var stamp = new StringBuilder();
            foreach (string name in Names) stamp.Append(Of(projectRoot, name)).Append(';');
            return stamp.ToString();
        }

        /// <summary>The same as <see cref="Read"/> from Git's paths already known; false (no stamp) while they are not.</summary>
        internal static bool TryRead(string projectRoot, out string stamp)
        {
            var text = new StringBuilder();
            foreach (string name in Names)
            {
                if (!UnitGitPaths.TryGetKnown(projectRoot, name, out string path))
                {
                    stamp = string.Empty;
                    return false;
                }
                text.Append(OfPath(path)).Append(';');
            }

            stamp = text.ToString();
            return true;
        }

        /// <summary>One of Git's files or folders as "time:size" ("-" when it does not exist).</summary>
        internal static string Of(string projectRoot, string name)
        {
            return OfPath(UnitGitPaths.GitPath(projectRoot, name));
        }

        private static string OfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            try
            {
                var file = new FileInfo(path);
                if (file.Exists) return file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + file.Length.ToString(CultureInfo.InvariantCulture);
                var folder = new DirectoryInfo(path);
                // A folder's time moves when a file is created or deleted in it (a new branch, a deleted tag).
                return folder.Exists ? folder.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "-";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return "?";
            }
        }
    }
}
