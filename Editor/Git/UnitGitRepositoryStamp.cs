using System;
using System.Globalization;
using System.IO;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The time and size of one of Git's own files (its config, the stash's reflog): a slice read from such a file is read
    /// again only when its stamp moved. Never the reason to skip status or the refs, which every refresh reads.
    /// </summary>
    internal static class UnitGitRepositoryStamp
    {
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
