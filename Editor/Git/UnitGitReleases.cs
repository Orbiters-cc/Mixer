using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// A single key/value pair of tool-specific release data, displayed generically by Mixer Git.
    /// </summary>
    [Serializable]
    public sealed class UnitGitReleaseField
    {
        public string key = string.Empty;
        public string value = string.Empty;

        public UnitGitReleaseField()
        {
        }

        public UnitGitReleaseField(string key, string value)
        {
            this.key = key ?? string.Empty;
            this.value = value ?? string.Empty;
        }
    }

    /// <summary>
    /// A release checkpoint recorded in the repository. Intentionally tool-agnostic so any
    /// publisher (MCB versions, VRChat avatar uploads via the VRCSDK, ...) can record releases
    /// that Mixer Git keeps displaying even after the publishing tool is removed.
    /// </summary>
    [Serializable]
    public sealed class UnitGitReleaseEntry
    {
        public string id = string.Empty;
        public string tool = string.Empty;       // e.g. "MCB", "VRChat SDK"
        public string type = string.Empty;       // e.g. "mcb-version", "vrchat-avatar-upload"
        public string name = string.Empty;       // asset / product name
        public string version = string.Empty;    // e.g. "1.3.1"
        public string title = string.Empty;      // release title
        public string changelog = string.Empty;
        public string scope = string.Empty;      // e.g. "PUBLIC", "BETA"
        public string date = string.Empty;       // ISO-8601 (UTC)
        public string author = string.Empty;
        public string thumbnailPath = string.Empty;
        public List<UnitGitReleaseField> fields = new List<UnitGitReleaseField>();
    }

    [Serializable]
    public sealed class UnitGitReleaseFile
    {
        public int formatVersion = 1;
        public List<UnitGitReleaseEntry> releases = new List<UnitGitReleaseEntry>();
        public List<string> hiddenReleaseIds = new List<string>();
    }

    public sealed class UnitGitReleaseResult
    {
        public bool Success;
        public bool NoChanges;
        public string Message = string.Empty;
        public string CommitHash = string.Empty;
        public string ReleaseId = string.Empty;
    }

    /// <summary>
    /// Public entry point other tools use to record release checkpoints in the Git history.
    /// The release data lives in <c>.unitgit-releases.json</c> at the project root (committed with
    /// the release), and the commit is associated with its entry through a
    /// <c>UnitGit-Release: &lt;id&gt;</c> commit-message trailer.
    /// </summary>
    public static class UnitGitReleases
    {
        // One change to the catalog at a time in this editor: each reads it, adds to it and commits it, and a second one in
        // between would write over the first one's entry or roll the file back under it. A second one is refused, not queued,
        // so the editor never waits behind a long commit.
        private static readonly object CatalogGate = new object();
        private const string CatalogBusyMessage = "Another release is being recorded in " + ReleasesFileName + ". Try again when it is done.";

        public const int ApiVersion = 2;
        public const string CommitFilesCapability = "commit-files";
        public const string ScopedReleaseCheckpointCapability = "scoped-release-checkpoint";
        public const string FullProjectReleaseCheckpointCapability = "full-project-release-checkpoint";
        public const string ReleasesFileName = ".unitgit-releases.json";
        public const string TrailerKey = "UnitGit-Release";

        private const string IgnoredReleaseFileMessage = ReleasesFileName + " is ignored by Git, so the release metadata could not be committed " +
            "and no release checkpoint was created. Remove the ignore rule for it (\"git check-ignore -v " + ReleasesFileName +
            "\" shows which one), then try again.";

        /// <summary>
        /// Raised after an external tool successfully created a commit through this API, so open
        /// Mixer Git windows can refresh their history immediately.
        /// </summary>
        public static event Action ChangedExternally;

        // Captured on the main thread at load: handlers (Mixer Git windows, My Avatar's summary) always run there, also when
        // a window action wrote the catalog from a worker thread.
        private static SynchronizationContext mainThread;

        [UnityEditor.InitializeOnLoadMethod]
        private static void CaptureMainThread() => mainThread = SynchronizationContext.Current;

        private static void RaiseChangedExternally()
        {
            if (mainThread != null && !UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
            {
                mainThread.Post(_ => RaiseChangedExternally(), null);
                return;
            }

            try
            {
                ChangedExternally?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Mixer Git] ChangedExternally handler failed: " + ex.Message);
            }
        }

        public static string GetReleasesFilePath(string projectRoot)
        {
            return Path.Combine(projectRoot, ReleasesFileName);
        }

        public static UnitGitReleaseFile Load(string projectRoot)
        {
            if (TryLoad(projectRoot, out UnitGitReleaseFile file, out string error))
            {
                return file;
            }

            Debug.LogWarning("[Mixer Git] " + error);
            return new UnitGitReleaseFile();
        }

        /// <summary>
        /// Fails for a catalog that exists but cannot be read, so writers never replace it with a new one.
        /// A missing or empty file is an empty catalog.
        /// </summary>
        internal static bool TryLoad(string projectRoot, out UnitGitReleaseFile file, out string error)
        {
            file = new UnitGitReleaseFile();
            error = string.Empty;
            try
            {
                string path = GetReleasesFilePath(projectRoot);
                if (!File.Exists(path))
                {
                    return true;
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return true;
                }

                file = JsonUtility.FromJson<UnitGitReleaseFile>(json) ?? new UnitGitReleaseFile();
                file.releases = file.releases ?? new List<UnitGitReleaseEntry>();
                file.hiddenReleaseIds = file.hiddenReleaseIds ?? new List<string>();
                return true;
            }
            catch (Exception ex)
            {
                file = new UnitGitReleaseFile();
                error = "Could not read " + ReleasesFileName + ": " + ex.Message;
                return false;
            }
        }

        private static UnitGitReleaseResult FailUnreadableCatalog(string error)
        {
            return Fail(error + "\nUnit Git left it unchanged so no release history is lost. Fix the file " +
                        "(for example resolve merge conflict markers) or restore it from Git, then try again.");
        }

        public static UnitGitReleaseEntry FindById(UnitGitReleaseFile file, string id)
        {
            if (file == null || file.releases == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            string normalized = id.Trim();
            return file.releases.FirstOrDefault(entry =>
                entry != null &&
                string.Equals((entry.id ?? string.Empty).Trim(), normalized, StringComparison.Ordinal));
        }

        internal static bool IsHidden(UnitGitReleaseFile file, string id)
        {
            if (file == null || file.hiddenReleaseIds == null || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            string normalized = id.Trim();
            return file.hiddenReleaseIds.Any(hiddenId =>
                string.Equals((hiddenId ?? string.Empty).Trim(), normalized, StringComparison.Ordinal));
        }

        internal static UnitGitReleaseResult HideRelease(string projectRoot, string id)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return Fail("Project root is required.");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return Fail("Release id is required.");
            }

            if (!Monitor.TryEnter(CatalogGate))
            {
                return Fail(CatalogBusyMessage);
            }

            try
            {
                if (!TryLoad(projectRoot, out UnitGitReleaseFile file, out string error))
                {
                    return FailUnreadableCatalog(error);
                }

                string normalized = id.Trim();
                if (!file.hiddenReleaseIds.Any(hiddenId =>
                        string.Equals((hiddenId ?? string.Empty).Trim(), normalized, StringComparison.Ordinal)))
                {
                    file.hiddenReleaseIds.Add(normalized);
                }

                WriteCatalog(projectRoot, file);
                RaiseChangedExternally();
                return new UnitGitReleaseResult
                {
                    Success = true,
                    Message = "Release row hidden.",
                    ReleaseId = normalized
                };
            }
            catch (Exception ex)
            {
                return Fail("Could not hide release row: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(CatalogGate);
            }
        }

        public static string[] GetCapabilities()
        {
            return new[]
            {
                CommitFilesCapability,
                ScopedReleaseCheckpointCapability,
                FullProjectReleaseCheckpointCapability
            };
        }

        /// <summary>
        /// Appends the release entry to the releases file and commits only the release file plus
        /// the explicit project-relative paths supplied by the publisher.
        /// </summary>
        public static UnitGitReleaseResult PublishRelease(UnitGitReleaseEntry entry, string commitTitle, params string[] projectRelativePaths)
        {
            return PublishRelease(entry, commitTitle, new UnitGitService(), projectRelativePaths);
        }

        internal static UnitGitReleaseResult PublishRelease(UnitGitReleaseEntry entry, string commitTitle, UnitGitService service, params string[] projectRelativePaths)
        {
            if (entry == null)
            {
                return Fail("Release entry is required.");
            }

            if (service == null)
            {
                return Fail("Mixer Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            if (string.IsNullOrWhiteSpace(entry.id))
            {
                entry.id = GenerateReleaseId(entry);
            }

            if (string.IsNullOrWhiteSpace(entry.date))
            {
                entry.date = DateTime.UtcNow.ToString("o");
            }

            string title = string.IsNullOrWhiteSpace(commitTitle)
                ? BuildDefaultCommitTitle(entry)
                : commitTitle.Trim();
            return RecordRelease(service, entry, () => CommitFiles(
                service,
                title,
                TrailerKey + ": " + entry.id,
                BuildReleaseCommitPaths(projectRelativePaths),
                requireReleaseFile: true));
        }

        /// <summary>
        /// Appends the release entry and commits every pending project change. Used by upload
        /// workflows where the built content can touch scenes, assets, settings, and package files.
        /// </summary>
        public static UnitGitReleaseResult PublishReleaseAll(UnitGitReleaseEntry entry, string commitTitle)
        {
            return PublishReleaseAll(entry, commitTitle, new UnitGitService(), true);
        }

        internal static UnitGitReleaseResult PublishReleaseAll(UnitGitReleaseEntry entry, string commitTitle, UnitGitService service, bool notifyChangedExternally)
        {
            if (entry == null)
            {
                return Fail("Release entry is required.");
            }

            if (service == null)
            {
                return Fail("Mixer Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            if (string.IsNullOrWhiteSpace(entry.id))
            {
                entry.id = GenerateReleaseId(entry);
            }

            if (string.IsNullOrWhiteSpace(entry.date))
            {
                entry.date = DateTime.UtcNow.ToString("o");
            }

            string title = string.IsNullOrWhiteSpace(commitTitle)
                ? BuildDefaultCommitTitle(entry)
                : commitTitle.Trim();
            return RecordRelease(service, entry, () => StageAllAndCommit(
                service,
                title,
                TrailerKey + ": " + entry.id,
                notifyChangedExternally,
                true));
        }

        /// <summary>
        /// Adds the entry to the catalog and records it with <paramref name="commit"/>. When the commit fails the catalog goes
        /// back as it was, unless something else changed it meanwhile: that change is kept. The index is never rolled back:
        /// a failed checkpoint never touched it (it stages in a private copy).
        /// </summary>
        private static UnitGitReleaseResult RecordRelease(UnitGitService service, UnitGitReleaseEntry entry, Func<UnitGitReleaseResult> commit)
        {
            if (!Monitor.TryEnter(CatalogGate))
            {
                return Fail(CatalogBusyMessage);
            }

            try
            {
                // Read inside the gate: the catalog as the last release left it.
                if (!TryLoad(service.ProjectRoot, out UnitGitReleaseFile file, out string loadError))
                {
                    return FailUnreadableCatalog(loadError);
                }

                string path = GetReleasesFilePath(service.ProjectRoot);
                byte[] before;
                byte[] written;
                try
                {
                    before = File.Exists(path) ? File.ReadAllBytes(path) : null;
                }
                catch (Exception ex)
                {
                    return Fail("Could not read " + ReleasesFileName + ": " + ex.Message);
                }

                try
                {
                    file.releases.Add(entry);
                    written = WriteCatalog(service.ProjectRoot, file);
                }
                catch (Exception ex)
                {
                    RestoreCatalog(service.ProjectRoot, before, null, out _);
                    return Fail("Could not write " + ReleasesFileName + ": " + ex.Message);
                }

                UnitGitReleaseResult result = commit();
                if (!result.Success)
                {
                    if (!RestoreCatalog(service.ProjectRoot, before, written, out string rollbackMessage))
                    {
                        result.Message = result.Message + "\n" + rollbackMessage;
                    }

                    return result;
                }

                result.ReleaseId = entry.id;
                return result;
            }
            finally
            {
                Monitor.Exit(CatalogGate);
            }
        }

        private static byte[] WriteCatalog(string projectRoot, UnitGitReleaseFile file)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(file, true));
            File.WriteAllBytes(GetReleasesFilePath(projectRoot), bytes);
            return bytes;
        }

        /// <summary>
        /// Puts the catalog back to <paramref name="before"/> (null: no file), but only while it still holds exactly what this
        /// release wrote (<paramref name="written"/>; null after a write that failed part way).
        /// </summary>
        private static bool RestoreCatalog(string projectRoot, byte[] before, byte[] written, out string message)
        {
            message = string.Empty;
            string path = GetReleasesFilePath(projectRoot);
            try
            {
                if (written != null && !(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(written)))
                {
                    message = ReleasesFileName + " was changed by something else meanwhile, so Mixer Git left it as it is. " +
                              "Check that it does not list this release, which was not committed.";
                    return false;
                }

                if (before != null)
                {
                    File.WriteAllBytes(path, before);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex)
            {
                message = "Rollback failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Stages every pending change and commits it. Used by external tools (and connector
        /// tests) that want a plain commit without recording a release entry.
        /// </summary>
        public static UnitGitReleaseResult CommitAll(string commitTitle, string trailingParagraph = null)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            var service = new UnitGitService();
            return CommitAll(commitTitle, trailingParagraph, service, true);
        }

        internal static UnitGitReleaseResult CommitAll(string commitTitle, string trailingParagraph, UnitGitService service, bool notifyChangedExternally)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            if (service == null)
            {
                return Fail("Mixer Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            return StageAllAndCommit(service, commitTitle.Trim(), trailingParagraph, notifyChangedExternally);
        }

        /// <summary>
        /// Commits only the given project-relative paths (staging them first), leaving any other
        /// pending change untouched. Used by connector tests.
        /// </summary>
        public static UnitGitReleaseResult CommitFiles(string commitTitle, string trailingParagraph, params string[] projectRelativePaths)
        {
            return CommitFiles(new UnitGitService(), commitTitle, trailingParagraph, projectRelativePaths);
        }

        /// <summary>Explicit project root for integrations running Git off the Unity main thread.</summary>
        public static async System.Threading.Tasks.Task<UnitGitReleaseResult> CommitProjectFilesAsync(string projectRoot, string commitTitle, params string[] projectRelativePaths)
        {
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string path in projectRelativePaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
                    !Path.GetFullPath(Path.Combine(root, path)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Fail("Checkpoint paths must stay inside the Unity project.");
            }
            var result = await System.Threading.Tasks.Task.Run(() => CommitFiles(new UnitGitService(root), commitTitle, null, projectRelativePaths, false, true));
            if (result.Success) RaiseChangedExternally();
            return result;
        }

        private static UnitGitReleaseResult CommitFiles(UnitGitService service, string commitTitle, string trailingParagraph, string[] projectRelativePaths,
            bool notify = true, bool requireAll = false, bool requireReleaseFile = false)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            string[] paths = (projectRelativePaths ?? Array.Empty<string>())
                .Select(NormalizeProjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return Fail("At least one file path is required.");
            }

            if (service == null)
            {
                return Fail("Mixer Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            try
            {
                using var index = new UnitGitScopedIndex(service);
                var included = index.FilterIgnored(paths);
                if (requireAll && included.Length != paths.Length)
                    return Fail("Some checkpoint files are ignored by Git: " + string.Join(", ", paths.Except(included).Take(3)) + ". Update the project's ignore rules before saving.");
                if (requireReleaseFile && Array.IndexOf(included, ReleasesFileName) < 0)
                    return Fail(IgnoredReleaseFileMessage);
                paths = included;
                if (paths.Length == 0) return Fail("All requested checkpoint files are ignored by Git.");
                var addArgs = new List<string> { "add", "--" };
                addArgs.AddRange(paths);
                GitCommandResult stage = index.Run(addArgs.ToArray());

                if (!stage.Success)
                {
                    return Fail("Staging files failed: " + stage.Message);
                }

                var diffArgs = new List<string> { "diff", "--cached", "--quiet", "--exit-code", "--" };
                diffArgs.AddRange(paths);
                GitCommandResult difference = index.Run(diffArgs.ToArray());
                if (difference.Success)
                    return new UnitGitReleaseResult { Success = true, NoChanges = true, Message = "No new changes to checkpoint." };
                if (difference.TimedOut || difference.ExitCode != 1)
                    return Fail("Checking checkpoint changes failed: " + difference.Message);

                var commitArgs = new List<string> { "commit", "-m", commitTitle.Trim() };
                if (!string.IsNullOrWhiteSpace(trailingParagraph))
                {
                    commitArgs.Add("-m");
                    commitArgs.Add(trailingParagraph.Trim());
                }

                commitArgs.Add("--");
                commitArgs.AddRange(paths);
                GitCommandResult commit = index.Run(commitArgs.ToArray());
                if (!commit.Success)
                {
                    return Fail("Commit failed: " + commit.Message);
                }
                var result = new UnitGitReleaseResult
                {
                    Success = true,
                    Message = commit.Message
                };

                // HEAD has advanced. An index-install failure must not roll back the
                // release file or report the already-created checkpoint as missing.
                try { index.Complete(); }
                catch (IOException ex)
                {
                    result.Message += "\n" + ex.Message;
                    Debug.LogWarning("[Mixer Git] " + ex.Message);
                }

                GitCommandResult head = service.RunGit(10000, "rev-parse", "HEAD");
                if (head.Success)
                {
                    result.CommitHash = head.StandardOutput.Trim();
                }

                if (notify) RaiseChangedExternally();
                return result;
            }
            catch (Exception ex)
            {
                return Fail("Checkpoint failed: " + ex.Message);
            }
        }

        internal static void NotifyChangedExternally()
        {
            RaiseChangedExternally();
        }

        private static string[] BuildReleaseCommitPaths(IEnumerable<string> projectRelativePaths)
        {
            var paths = new List<string> { ReleasesFileName };
            foreach (string path in projectRelativePaths ?? Enumerable.Empty<string>())
            {
                string normalized = NormalizeProjectPath(path);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(normalized);
            }

            return paths.ToArray();
        }

        private static string NormalizeProjectPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/');
        }

        private static UnitGitReleaseResult CheckPreconditions(UnitGitService service)
        {
            if (!UnitGitService.IsUnityProject(service.ProjectRoot))
            {
                return Fail("No Unity project markers were found at " + service.ProjectRoot + ".");
            }

            if (!UnitGitService.HasRepository(service.ProjectRoot))
            {
                return Fail("No Git repository exists at the Unity project root. Initialize one with Mixer Git first.");
            }

            if (!service.IsGitAvailable())
            {
                return Fail("Git was not found on PATH.");
            }

            // A commit made on a detached HEAD (a visited commit) belongs to no branch and leaves the history on the way back.
            if (service.IsHeadDetached())
            {
                string detachedBy = UnitGitConflicts.DetachedBy(service.ProjectRoot);
                return Fail(detachedBy.Length > 0
                    ? "No branch is checked out (Git is " + detachedBy.ToLowerInvariant() + "). Finish that first, then record the checkpoint."
                    : "No branch is checked out (Mixer Git is visiting a commit). Go back to a branch, or make one, before recording a checkpoint.");
            }

            return null;
        }

        private static UnitGitReleaseResult StageAllAndCommit(UnitGitService service, string title, string trailingParagraph,
            bool notifyChangedExternally = true, bool requireReleaseFile = false)
        {
            try
            {
                using var index = new UnitGitScopedIndex(service);
                // "add -A" silently skips ignored files; a release commit without its metadata is not a release.
                if (requireReleaseFile && index.FilterIgnored(new[] { ReleasesFileName }).Length == 0)
                    return Fail(IgnoredReleaseFileMessage);
                GitCommandResult stage = index.Run("add", "-A");
                if (!stage.Success) return Fail("Staging changes failed: " + stage.Message);

                GitCommandResult commit = index.Commit(title, trailingParagraph);
                if (!commit.Success) return Fail("Commit failed: " + commit.Message);

                var result = new UnitGitReleaseResult { Success = true, Message = commit.Message };
                // The commit exists even if installing the completed index fails.
                // Keep the release file and the private index available for recovery.
                try { index.Complete(); }
                catch (IOException ex)
                {
                    result.Message += "\n" + ex.Message;
                    Debug.LogWarning("[Mixer Git] " + ex.Message);
                }
                GitCommandResult head = service.RunGit(10000, "rev-parse", "HEAD");
                if (head.Success) result.CommitHash = head.StandardOutput.Trim();
                if (notifyChangedExternally) RaiseChangedExternally();
                return result;
            }
            catch (Exception ex)
            {
                return Fail("Checkpoint failed: " + ex.Message);
            }
        }

        internal static string BuildDefaultCommitTitle(UnitGitReleaseEntry entry)
        {
            string tool = string.IsNullOrWhiteSpace(entry.tool) ? "Release" : entry.tool.Trim();
            string version = string.IsNullOrWhiteSpace(entry.version) ? string.Empty : " v" + entry.version.Trim();
            string title = string.IsNullOrWhiteSpace(entry.title) ? string.Empty : " - " + entry.title.Trim();
            return tool + " :" + version + title;
        }

        internal static string GenerateReleaseId(UnitGitReleaseEntry entry)
        {
            string tool = Sanitize(entry != null ? entry.tool : null, "release");
            string version = Sanitize(entry != null ? entry.version : null, "0");
            return tool + "-" + version + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        private static string Sanitize(string value, string fallback)
        {
            string sanitized = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9._-]+", "-").Trim('-').ToLowerInvariant();
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        private static UnitGitReleaseResult Fail(string message)
        {
            return new UnitGitReleaseResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
