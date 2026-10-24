using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>How much disk the local Git data takes: the repository's .git folder, not the working files.</summary>
    internal sealed class UnitGitStorageSize
    {
        public string GitDirectory = string.Empty;
        public long Total;
        /// <summary>Git's object database: every version of every committed file (history).</summary>
        public long Objects;
        /// <summary>Git LFS's local copies of large files.</summary>
        public long Lfs;
        /// <summary>The rest: index, refs, logs, hooks…</summary>
        public long Other;
        public DateTime MeasuredUtc;
    }

    /// <summary>
    /// Measures the .git folder on a background thread. One measurement runs at a time per repository; asking while one
    /// runs queues a single follow-up (so a commit that lands mid-walk is counted), and the last result stays cached.
    /// </summary>
    internal static class UnitGitStorage
    {
        private sealed class Entry
        {
            public UnitGitStorageSize Last;
            public Task<UnitGitStorageSize> Running;
            public Task<UnitGitStorageSize> Queued;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last measurement of <paramref name="projectRoot"/>'s Git data, if any.</summary>
        internal static UnitGitStorageSize Cached(string projectRoot)
        {
            lock (Gate) return Entries.TryGetValue(Key(projectRoot), out Entry entry) ? entry.Last : null;
        }

        /// <summary>Measures in the background; the task completes on a worker thread.</summary>
        internal static Task<UnitGitStorageSize> MeasureAsync(string projectRoot)
        {
            string key = Key(projectRoot);
            lock (Gate)
            {
                if (!Entries.TryGetValue(key, out Entry entry)) Entries[key] = entry = new Entry();
                if (entry.Queued != null) return entry.Queued;
                if (entry.Running != null && !entry.Running.IsCompleted)
                    return entry.Queued = entry.Running.ContinueWith(_ => Run(projectRoot, entry, true), TaskScheduler.Default);
                return entry.Running = Task.Run(() => Run(projectRoot, entry, false));
            }
        }

        private static UnitGitStorageSize Run(string projectRoot, Entry entry, bool queued)
        {
            if (queued)
            {
                lock (Gate)
                {
                    entry.Running = entry.Queued;
                    entry.Queued = null;
                }
            }
            var size = Measure(ResolveGitDirectory(projectRoot));
            lock (Gate) entry.Last = size;
            return size;
        }

        /// <summary>
        /// The folder holding the repository's data: the project's .git folder, or for a .git file (a worktree or a
        /// submodule) the folder it points to, and that folder's shared common directory when it has one.
        /// </summary>
        internal static string ResolveGitDirectory(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return string.Empty;
            string dotGit = Path.Combine(projectRoot, ".git");
            if (Directory.Exists(dotGit)) return Path.GetFullPath(dotGit);
            if (!File.Exists(dotGit)) return string.Empty;

            string pointer = string.Empty;
            try
            {
                foreach (string line in File.ReadAllLines(dotGit))
                {
                    if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) continue;
                    pointer = line.Substring("gitdir:".Length).Trim();
                    break;
                }
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
            if (pointer.Length == 0) return string.Empty;

            string gitDir = Path.GetFullPath(Path.IsPathRooted(pointer) ? pointer : Path.Combine(projectRoot, pointer));
            string commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                try
                {
                    string common = File.ReadAllText(commonFile).Trim();
                    if (common.Length > 0)
                    {
                        string commonDir = Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(gitDir, common));
                        if (Directory.Exists(commonDir)) return commonDir;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Directory.Exists(gitDir) ? gitDir : string.Empty;
        }

        /// <summary>
        /// Adds up every file under <paramref name="gitDirectory"/>, split into objects, LFS and the rest. Links (symbolic
        /// links, junctions) are not followed and unreadable folders are skipped.
        /// </summary>
        internal static UnitGitStorageSize Measure(string gitDirectory)
        {
            var size = new UnitGitStorageSize { GitDirectory = gitDirectory ?? string.Empty, MeasuredUtc = DateTime.UtcNow };
            if (string.IsNullOrWhiteSpace(gitDirectory) || !Directory.Exists(gitDirectory)) return size;

            var root = new DirectoryInfo(gitDirectory);
            size.Other += FilesSize(root);
            foreach (DirectoryInfo child in Children(root))
            {
                long bytes = TreeSize(child);
                if (string.Equals(child.Name, "objects", StringComparison.OrdinalIgnoreCase)) size.Objects += bytes;
                else if (string.Equals(child.Name, "lfs", StringComparison.OrdinalIgnoreCase)) size.Lfs += bytes;
                else size.Other += bytes;
            }
            size.Total = size.Objects + size.Lfs + size.Other;
            return size;
        }

        /// <summary>"Git 1.2 GB" for the top bar.</summary>
        internal static string Label(UnitGitStorageSize size) => size == null ? "Git …" : "Git " + UnitGitService.FormatBytes(size.Total);

        internal static string Tooltip(UnitGitStorageSize size)
        {
            const string What = "Disk space of this project's local Git data (the .git folder): its whole history, Git LFS's copies " +
                                "and Git's bookkeeping. Your working files are not counted.";
            if (size == null) return What + "\nMeasuring…";
            return What + "\n\nHistory (objects): " + UnitGitService.FormatBytes(size.Objects) +
                   "\nGit LFS: " + UnitGitService.FormatBytes(size.Lfs) +
                   "\nOther: " + UnitGitService.FormatBytes(size.Other) +
                   "\n\nMeasured at " + size.MeasuredUtc.ToLocalTime().ToString("HH:mm:ss") + ". Click to measure again.";
        }

        private static long TreeSize(DirectoryInfo directory)
        {
            long total = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                DirectoryInfo current = pending.Pop();
                total += FilesSize(current);
                foreach (DirectoryInfo child in Children(current)) pending.Push(child);
            }
            return total;
        }

        private static long FilesSize(DirectoryInfo directory)
        {
            long total = 0;
            try
            {
                foreach (FileInfo file in directory.EnumerateFiles())
                {
                    try { total += file.Length; }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return total;
        }

        private static IEnumerable<DirectoryInfo> Children(DirectoryInfo directory)
        {
            var children = new List<DirectoryInfo>();
            try
            {
                foreach (DirectoryInfo child in directory.EnumerateDirectories())
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0) children.Add(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return children;
        }

        private static string Key(string projectRoot) =>
            string.IsNullOrWhiteSpace(projectRoot) ? string.Empty : Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
    }
}
