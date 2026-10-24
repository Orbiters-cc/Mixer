using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Reserves the real index and stages a scoped checkpoint in a private copy.
    /// A failed add/commit cannot leave partially staged user files behind.
    /// </summary>
    internal sealed class UnitGitScopedIndex : IDisposable
    {
        private readonly UnitGitService service;
        private readonly string indexPath;
        private readonly string lockPath;
        private readonly string temporaryPath;
        private FileStream reservation;
        private bool ownsLock;
        private bool preserveTemporaryIndex;
        private readonly Dictionary<string, string> environment;

        internal UnitGitScopedIndex(UnitGitService service)
        {
            this.service = service;
            var resolved = service.RunGit(10000, "rev-parse", "--git-path", "index");
            if (!resolved.Success) throw new IOException(resolved.Message);
            indexPath = Path.GetFullPath(Path.Combine(service.ProjectRoot, resolved.StandardOutput.Trim()));
            lockPath = indexPath + ".lock";
            temporaryPath = indexPath + ".unitgit-" + Guid.NewGuid().ToString("N");
            // Never delete another process's lock. The caller can retry the checkpoint.
            reservation = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsLock = true;
            environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporaryPath, ["GIT_LITERAL_PATHSPECS"] = "1" };
            try
            {
                if (File.Exists(indexPath)) File.Copy(indexPath, temporaryPath);
            }
            catch { Dispose(); throw; }
        }

        internal GitCommandResult Run(params string[] arguments) =>
            service.RunGitWithEnvironment(environment, UnitGitService.LongTimeoutMilliseconds, arguments);

        internal GitCommandResult Commit(string title, string trailingParagraph) =>
            service.RunCommitWithMessageFile(new[] { "commit" }, string.IsNullOrWhiteSpace(trailingParagraph)
                ? title : title.Trim() + "\n\n" + trailingParagraph.Trim(), environment);

        internal string[] FilterIgnored(string[] paths)
        {
            var args = new List<string> { "-c", "core.quotepath=false", "check-ignore", "--" };
            args.AddRange(paths);
            // check-ignore already takes literal filenames and rejects Git's
            // pathspec-magic mode, unlike add/commit.
            var ignoreEnvironment = new Dictionary<string, string>(environment) { ["GIT_LITERAL_PATHSPECS"] = "0" };
            var result = service.RunGitWithEnvironment(ignoreEnvironment,
                UnitGitService.LongTimeoutMilliseconds, args.ToArray());
            // check-ignore returns 1 when no paths are ignored. Tracked paths are retained.
            if (result.TimedOut || (result.ExitCode != 0 && result.ExitCode != 1))
                throw new IOException("Could not check ignored checkpoint files: " + result.Message);
            var ignored = new HashSet<string>(UnitGitService.SplitLines(result.StandardOutput)
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(UnitGitService.UnquotePath), StringComparer.Ordinal);
            return paths.Where(path => !ignored.Contains(path)).ToArray();
        }

        internal void Complete()
        {
            try
            {
                using (var source = File.OpenRead(temporaryPath)) source.CopyTo(reservation);
                reservation.Flush(true);
                reservation.Dispose();
                reservation = null;
                if (File.Exists(indexPath)) File.Replace(lockPath, indexPath, null);
                else File.Move(lockPath, indexPath);
                ownsLock = false;
            }
            catch (Exception ex)
            {
                preserveTemporaryIndex = true;
                throw new IOException("The checkpoint was committed, but the staging index could not be updated. " +
                    "The completed index is preserved at " + temporaryPath + ". " + ex.Message, ex);
            }
        }

        public void Dispose()
        {
            try { reservation?.Dispose(); }
            finally { reservation = null; }
            DeleteOwnedTemporaryFile(ownsLock ? lockPath : null);
            if (!preserveTemporaryIndex) DeleteOwnedTemporaryFile(temporaryPath);
            DeleteOwnedTemporaryFile(temporaryPath + ".lock");
        }

        private static void DeleteOwnedTemporaryFile(string path)
        {
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException ex) { UnityEngine.Debug.LogWarning("[Mixer Git] Could not clean up " + path + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { UnityEngine.Debug.LogWarning("[Mixer Git] Could not clean up " + path + ": " + ex.Message); }
        }
    }
}
