using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitBackup
    {
        public string Path = string.Empty;
        public DateTime Created;
        public long Bytes;
        // Set once checked: null until then.
        public bool? Healthy;
        public int Branches;
        public string Problem = string.Empty;

        public string FileName
        {
            get { return System.IO.Path.GetFileName(Path); }
        }
    }

    /// <summary>
    /// Backups of a project's whole Git history: one <c>git bundle</c> file per backup, with every branch, tag and other
    /// ref, the checked-out commit and every shelf, kept in a folder the user picks (another drive or a synced folder).
    /// Mixer Git never uploads anything; a backup is the copy that survives a broken disk or a deleted project.
    /// Uncommitted changes that are not shelved and ignored files are not part of it.
    /// </summary>
    internal static class UnitGitBackups
    {
        public const string Extension = ".bundle";
        /// <summary>
        /// Where a backup keeps the shelves: Git holds only the newest in refs/stash (the others are in its log, which a
        /// bundle does not carry), so each one is a ref of its own, 000 the newest.
        /// </summary>
        internal const string ShelfRefs = "refs/shelves/";
        private const string AutomaticSessionKey = "Orbiters.UnitGit.Backups.AutomaticChecked";
        private static readonly Dictionary<string, UnitGitBackup> Checked = new Dictionary<string, UnitGitBackup>(StringComparer.OrdinalIgnoreCase);
        // What follows a project's own prefix in its backup names: the time it was made, and a number when two share it.
        private static readonly Regex Stamp = new Regex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_\d+)?\.bundle$");

        public static string DefaultFolder(string projectRoot)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(documents, "Mixer Git Backups", ProjectName(projectRoot));
        }

        public static string Folder(string projectRoot)
        {
            string chosen = UnitGitSettings.BackupFolder;
            return string.IsNullOrWhiteSpace(chosen) ? DefaultFolder(projectRoot) : chosen;
        }

        public static string ProjectName(string projectRoot)
        {
            string name = System.IO.Path.GetFileName(System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/'));
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Project" : name;
        }

        /// <summary>
        /// How this project's backups are named: "Avatar_3f9a12c4_", its folder name and a mark of its full path. Projects
        /// can share a backup folder, and even a name; each one only ever counts, and deletes, its own backups.
        /// </summary>
        internal static string OwnPrefix(string projectRoot)
        {
            string full = System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/').ToUpperInvariant();
            using (var hash = SHA256.Create())
            {
                byte[] mark = hash.ComputeHash(Encoding.UTF8.GetBytes(full));
                return ProjectName(projectRoot) + "_" + BitConverter.ToString(mark, 0, 4).Replace("-", string.Empty).ToLowerInvariant() + "_";
            }
        }

        /// <summary>True for a backup this project made, by its name; anything else in the folder is never touched.</summary>
        internal static bool IsOwn(string path, string projectRoot)
        {
            string name = System.IO.Path.GetFileName(path), prefix = OwnPrefix(projectRoot);
            return name.StartsWith(prefix, StringComparison.Ordinal) && Stamp.IsMatch(name.Substring(prefix.Length));
        }

        /// <summary>True when the folder is inside the project: a backup there is lost with the project.</summary>
        public static bool IsInsideProject(string folder, string projectRoot)
        {
            string full = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            string root = System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The backups in the folder, newest first, with what an earlier check found.</summary>
        public static List<UnitGitBackup> List(string folder)
        {
            var backups = new List<UnitGitBackup>();
            if (!Directory.Exists(folder)) return backups;
            foreach (string path in Directory.GetFiles(folder, "*" + Extension))
            {
                var info = new FileInfo(path);
                var backup = new UnitGitBackup { Path = info.FullName, Created = info.LastWriteTime, Bytes = info.Length };
                lock (Checked)
                {
                    if (Checked.TryGetValue(info.FullName, out var known) && known.Bytes == info.Length && known.Created == info.LastWriteTime)
                    {
                        backup.Healthy = known.Healthy;
                        backup.Branches = known.Branches;
                        backup.Problem = known.Problem;
                    }
                }
                backups.Add(backup);
            }
            return backups.OrderByDescending(b => b.Created).ToList();
        }

        /// <summary>
        /// Writes a new backup (to a temporary name, checked, then renamed) and removes this project's oldest beyond
        /// <paramref name="keep"/>. Other files in the folder, other projects' backups included, are never removed.
        /// </summary>
        public static GitCommandResult Create(UnitGitService git, string folder, int keep)
        {
            Directory.CreateDirectory(folder);
            string name = OwnPrefix(git.ProjectRoot) + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string final = System.IO.Path.Combine(folder, name + Extension);
            for (int n = 2; File.Exists(final); n++) final = System.IO.Path.Combine(folder, name + "_" + n + Extension);
            string temporary = final + ".partial";
            var result = Bundle(git, temporary);
            if (!result.Success)
            {
                TryDelete(temporary);
                return result;
            }
            var check = Check(git, temporary);
            if (check.Healthy != true)
            {
                TryDelete(temporary);
                return Failure("The backup could not be read back: " + check.Problem);
            }
            File.Move(temporary, final);
            check.Path = System.IO.Path.GetFullPath(final);
            Remember(final, check);
            if (keep > 0)
                foreach (var old in Own(List(folder), git.ProjectRoot).Where(b => !string.Equals(b.Path, check.Path, StringComparison.OrdinalIgnoreCase)).Skip(keep - 1))
                    Delete(old.Path);
            return new GitCommandResult { StandardOutput = "Backed up " + check.Branches + " branch" + (check.Branches == 1 ? "" : "es") + " to " + System.IO.Path.GetFileName(final) + "." };
        }

        /// <summary>
        /// What a backup holds, as "&lt;commit&gt; &lt;name&gt;" lines: every ref but refs/stash, HEAD, and each shelf under
        /// <see cref="ShelfRefs"/>. Null when Git could not say.
        /// </summary>
        internal static List<string> RefsToBackUp(UnitGitService git)
        {
            var refs = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "for-each-ref", "--format=%(objectname) %(refname)");
            var shelves = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "stash", "list", "--format=%H");
            if (!refs.Success || !shelves.Success) return null;
            var lines = UnitGitService.SplitLines(refs.StandardOutput).Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.EndsWith(" refs/stash", StringComparison.Ordinal) && line.IndexOf(" " + ShelfRefs, StringComparison.Ordinal) < 0)
                .ToList();
            var head = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "HEAD");
            if (head.Success && head.StandardOutput.Trim().Length > 0) lines.Add(head.StandardOutput.Trim() + " HEAD");
            int n = 0;
            foreach (string shelf in UnitGitService.SplitLines(shelves.StandardOutput).Select(line => line.Trim()).Where(line => line.Length > 0))
                lines.Add(shelf + " " + ShelfRefs + (n++).ToString("D3", CultureInfo.InvariantCulture));
            return lines;
        }

        // The bundle is made in a scratch repository that borrows the project's objects (Git's alternates) and has exactly
        // the refs to back up: the project's own repository is never written to.
        private static GitCommandResult Bundle(UnitGitService git, string file)
        {
            var refs = RefsToBackUp(git);
            if (refs == null) return Failure("Git could not list the project's branches and shelves.");
            var common = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--git-common-dir");
            if (!common.Success) return common;
            string objects = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(git.ProjectRoot, common.StandardOutput.Trim())), "objects");
            string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mixer-git-backup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                var scratch = new UnitGitService(staging);
                var init = scratch.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "init", "--bare", "--quiet");
                if (!init.Success) return init;
                File.WriteAllText(System.IO.Path.Combine(staging, "objects", "info", "alternates"), objects.Replace('\\', '/') + "\n");
                var packed = new StringBuilder();
                string head = null;
                foreach (string line in refs)
                {
                    int space = line.IndexOf(' ');
                    if (space <= 0) continue;
                    if (line.Substring(space + 1) == "HEAD") head = line.Substring(0, space);
                    else packed.Append(line).Append('\n');
                }
                File.WriteAllText(System.IO.Path.Combine(staging, "packed-refs"), packed.ToString());
                if (head != null) File.WriteAllText(System.IO.Path.Combine(staging, "HEAD"), head + "\n");
                return scratch.RunGit(UnitGitService.LongTimeoutMilliseconds, "bundle", "create", file, "--all");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Failure("The backup could not be prepared: " + ex.Message);
            }
            finally
            {
                DeleteFolder(staging);
            }
        }

        // This project's backups among everything in the folder, in the same order.
        private static IEnumerable<UnitGitBackup> Own(IEnumerable<UnitGitBackup> backups, string projectRoot)
        {
            return backups.Where(backup => IsOwn(backup.Path, projectRoot));
        }

        /// <summary>
        /// Reads the whole backup back into a temporary copy: Git checks every object on the way, which "git bundle verify"
        /// alone does not (it reads the header), so a truncated or corrupted file is caught here.
        /// </summary>
        public static UnitGitBackup Check(UnitGitService git, string path)
        {
            var info = new FileInfo(path);
            var backup = new UnitGitBackup { Path = info.FullName, Created = info.LastWriteTime, Bytes = info.Exists ? info.Length : 0 };
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unitgit-check-" + Guid.NewGuid().ToString("N"));
            GitCommandResult verify;
            try
            {
                verify = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "clone", "--bare", "--quiet", path, scratch);
            }
            finally
            {
                DeleteFolder(scratch);
            }
            var heads = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", path);
            backup.Healthy = verify.Success && heads.Success;
            backup.Branches = heads.Success
                ? heads.StandardOutput.Split('\n').Count(line => line.Contains(" refs/heads/"))
                : 0;
            backup.Problem = backup.Healthy == true ? string.Empty : (verify.Success ? heads.Message : verify.Message);
            if (info.Exists) Remember(info.FullName, backup);
            return backup;
        }

        /// <summary>
        /// Recreates the project from a backup in a new, empty folder: its files at the commit that was checked out when the
        /// backup was made, checked before success is reported. Unity rebuilds the Library on first open.
        /// </summary>
        public static GitCommandResult RestoreCopy(UnitGitService git, string bundle, string destination)
        {
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                return Failure("Choose an empty folder: " + destination + " already has files.");
            var heads = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", bundle);
            if (!heads.Success) return heads;
            var refs = UnitGitService.SplitLines(heads.StandardOutput).Select(line => line.Trim().Split(' ')).Where(parts => parts.Length == 2).ToList();
            string commit = refs.Where(parts => parts[1] == "HEAD").Select(parts => parts[0]).FirstOrDefault();
            if (commit == null) return Failure("This backup does not record which commit was checked out, so its files cannot be restored.");
            // A bundle does not record the branch HEAD was on. A branch that is alone at that commit is the one; with none
            // there HEAD was detached, and with several the copy stays on the commit itself rather than guess.
            var branches = refs.Where(parts => parts[0] == commit && parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                .Select(parts => parts[1].Substring("refs/heads/".Length)).ToList();

            // Every branch and tag becomes a local one, with nothing pointing back at the backup file; HEAD brings the
            // checked-out commit even when no branch holds it.
            Directory.CreateDirectory(destination);
            var copy = new UnitGitService(destination);
            var result = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "init", "--quiet");
            if (result.Success)
                result = copy.RunGit(UnitGitService.LongTimeoutMilliseconds, "fetch", "--quiet", "--update-head-ok", bundle, "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*",
                    "+" + ShelfRefs + "*:" + ShelfRefs + "*", "HEAD");
            if (result.Success)
                result = branches.Count == 1
                    ? copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "symbolic-ref", "HEAD", "refs/heads/" + branches[0])
                    : copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "update-ref", "--no-deref", "HEAD", commit);
            if (result.Success)
                result = copy.RunGit(UnitGitService.LongTimeoutMilliseconds, "reset", "--hard", "--quiet");
            if (!result.Success) return result;
            // The shelves come back as shelves, oldest first so the newest is on top again.
            var shelves = refs.Where(parts => parts[1].StartsWith(ShelfRefs, StringComparison.Ordinal)).OrderByDescending(parts => parts[1], StringComparer.Ordinal).ToList();
            foreach (var shelf in shelves)
            {
                var subject = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "log", "-1", "--format=%s", shelf[0]);
                var stored = subject.Success
                    ? copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "stash", "store", "-m", subject.StandardOutput.Trim(), shelf[0])
                    : subject;
                if (!stored.Success) return Failure("The copy in " + destination + " has the project, but its shelf " + shelf[1].Substring(ShelfRefs.Length) + " could not be restored: " + stored.Message);
            }
            if (shelves.Count > 0)
            {
                var drop = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "for-each-ref", "--format=delete %(refname)", ShelfRefs);
                foreach (string line in UnitGitService.SplitLines(drop.StandardOutput).Where(l => l.StartsWith("delete ", StringComparison.Ordinal)))
                    copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "update-ref", "-d", line.Substring("delete ".Length).Trim());
            }
            string shortCommit = commit.Substring(0, Math.Min(7, commit.Length));
            var restored = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "HEAD^{commit}");
            if (!restored.Success || restored.StandardOutput.Trim() != commit)
                return Failure("The copy in " + destination + " is not at the backed-up commit " + shortCommit + ": do not use it.");

            string shelved = shelves.Count == 0 ? string.Empty : shelves.Count == 1 ? " Its shelf is back too." : " Its " + shelves.Count + " shelves are back too.";
            string where = branches.Count == 1 ? "on branch " + branches[0]
                : branches.Count == 0 ? "at commit " + shortCommit + " with no branch checked out, as when the backup was made"
                : "at commit " + shortCommit + " with no branch checked out: " + string.Join(", ", branches) + " point there, check out the one you were on";
            return new GitCommandResult { StandardOutput = "Restored the project to " + destination + ", " + where + "." + shelved + " Open it with Unity Hub (Add project from disk)." };
        }

        public static bool Delete(string path)
        {
            lock (Checked) Checked.Remove(path);
            return TryDelete(path);
        }

        /// <summary>
        /// Once per editor session: backs up in the background when automatic backups are on, the last one is a day old and
        /// the history changed since.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ScheduleAutomatic()
        {
            if (SessionState.GetBool(AutomaticSessionKey, false)) return;
            SessionState.SetBool(AutomaticSessionKey, true);
            if (!UnitGitSettings.AutomaticBackups) return;
            string root = UnitGitService.GetUnityProjectRoot();
            int keep = UnitGitSettings.BackupsToKeep;
            string folder = Folder(root);
            EditorApplication.delayCall += () => Task.Run(() =>
            {
                try
                {
                    var git = new UnitGitService(root);
                    if (!git.IsGitAvailable() || !UnitGitService.HasRepository(root) || !IsDue(git, folder)) return;
                    var result = Create(git, folder, keep);
                    if (!result.Success) UnityEngine.Debug.LogWarning("[Mixer Git] Automatic backup failed: " + result.Message);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    UnityEngine.Debug.LogWarning("[Mixer Git] Automatic backup failed: " + ex.Message);
                }
            });
        }

        internal static bool IsDue(UnitGitService git, string folder)
        {
            var last = Own(List(folder), git.ProjectRoot).FirstOrDefault();
            if (last == null) return HasHistory(git);
            if ((DateTime.Now - last.Created).TotalHours < 24) return false;
            // Everything a backup holds counts: a new commit on a detached HEAD, a new shelf, a remote's ref.
            var current = RefsToBackUp(git);
            var saved = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", last.Path);
            if (current == null || !saved.Success) return true;
            return !SameLines(string.Join("\n", current), saved.StandardOutput.Split('\n'));
        }

        private static bool HasHistory(UnitGitService git)
        {
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "HEAD").Success;
        }

        private static bool SameLines(string a, IEnumerable<string> b)
        {
            var left = new HashSet<string>(a.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
            var right = new HashSet<string>(b.Select(line => line.Trim()).Where(line => line.Length > 0));
            return left.SetEquals(right);
        }

        private static void Remember(string path, UnitGitBackup backup)
        {
            var info = new FileInfo(path);
            backup.Created = info.LastWriteTime;
            backup.Bytes = info.Length;
            lock (Checked) Checked[info.FullName] = backup;
        }

        private static void DeleteFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult { ExitCode = 1, StandardError = message };
        }
    }
}
