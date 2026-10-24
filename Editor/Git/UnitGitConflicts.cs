using System;
using Orbiters.Toolkit.Editor.Processes;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>What Git is in the middle of, when files conflict.</summary>
    internal enum UnitGitOperation { None, Merge, CherryPick, Revert, Rebase }

    internal enum ConflictChoice { None, Mine, Theirs, Both }

    /// <summary>One part of a conflicted text file: shared text, or a place where both sides changed the same lines.</summary>
    internal sealed class ConflictBlock
    {
        public bool IsConflict;
        public string Common = string.Empty;
        public string Mine = string.Empty;
        public string Base = string.Empty;
        public string Theirs = string.Empty;
        // For Unity files: the object and component the lines belong to ("Hips (Transform)").
        public string Context = string.Empty;
        public ConflictChoice Choice;
    }

    /// <summary>A property Unity's merge could not decide: both sides set it differently.</summary>
    internal sealed class UnityMergeConflict
    {
        public string Object = string.Empty;
        public string Property = string.Empty;
        public string Mine = string.Empty;
        public string Theirs = string.Empty;
    }

    internal sealed class UnityMergeResult
    {
        public bool Available;
        public bool Clean;
        public string Output;
        public readonly List<UnityMergeConflict> Conflicts = new List<UnityMergeConflict>();
        public string Message = string.Empty;
    }

    /// <summary>
    /// Resolving conflicted files: Unity's own merge for scenes and prefabs first (it merges by object, not by line), then
    /// choices part by part, or a whole side. Mixer Git never starts an external merge tool.
    /// </summary>
    internal static class UnitGitConflicts
    {
        public const string MineLabel = "Mine", BaseLabel = "Base", TheirsLabel = "Theirs";
        private const int UnityMergeTimeoutMilliseconds = 120000;
        // Git's own test for binary content: a zero byte in the first 8000.
        private const int BinarySniffBytes = 8000;

        public static bool IsConflict(UnitGitStatusEntry entry)
        {
            string code = (entry.IndexStatus.ToString() + entry.WorkTreeStatus).ToUpperInvariant();
            return code == "UU" || code == "AA" || code == "DD" || code == "AU" || code == "UA" || code == "DU" || code == "UD";
        }

        /// <summary>A short description of the conflict: "Changed on both sides", "Deleted by you"…</summary>
        public static string Describe(UnitGitStatusEntry entry)
        {
            switch ((entry.IndexStatus.ToString() + entry.WorkTreeStatus).ToUpperInvariant())
            {
                case "AA": return "Added on both sides";
                case "DD": return "Deleted on both sides";
                case "AU": return "Added by you";
                case "UA": return "Added by them";
                case "DU": return "Deleted by you";
                case "UD": return "Deleted by them";
                default: return "Changed on both sides";
            }
        }

        public static UnitGitOperation Operation(string root, out string title)
        {
            title = string.Empty;
            // Git says where these files are: in a linked worktree ".git" is a file, and they live elsewhere.
            string mergeHead = UnitGitPaths.GitPath(root, "MERGE_HEAD");
            if (mergeHead == null) return UnitGitOperation.None;
            if (File.Exists(mergeHead))
            {
                string message = FirstLine(UnitGitPaths.GitPath(root, "MERGE_MSG"));
                title = message.Length > 0 ? message : "Merge";
                return UnitGitOperation.Merge;
            }
            if (File.Exists(UnitGitPaths.GitPath(root, "CHERRY_PICK_HEAD"))) { title = "Copying a commit"; return UnitGitOperation.CherryPick; }
            if (File.Exists(UnitGitPaths.GitPath(root, "REVERT_HEAD"))) { title = "Reverting a commit"; return UnitGitOperation.Revert; }
            if (Directory.Exists(UnitGitPaths.GitPath(root, "rebase-merge")) || Directory.Exists(UnitGitPaths.GitPath(root, "rebase-apply"))) { title = "Rebasing"; return UnitGitOperation.Rebase; }
            return UnitGitOperation.None;
        }

        /// <summary>A "git bisect" under way: it checks out commits on its own, and "git bisect reset" ends it.</summary>
        public static bool IsBisecting(string root)
        {
            string log = UnitGitPaths.GitPath(root, "BISECT_LOG");
            return log != null && File.Exists(log);
        }

        /// <summary>
        /// What Git itself does on a commit without a branch: "Rebasing", "Bisecting", or empty (a visited commit, another
        /// tool's checkout). Those two belong to Git: going "Back" to a branch would leave them half done.
        /// </summary>
        public static string DetachedBy(string root)
        {
            return Operation(root, out _) == UnitGitOperation.Rebase ? "Rebasing" : IsBisecting(root) ? "Bisecting" : string.Empty;
        }

        private static string FirstLine(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() ?? string.Empty : string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The versions Git holds of a conflicted file: 1 base, 2 mine, 3 theirs. Empty when the file is not in conflict.
        /// Throws when Git cannot list them: a failed read is never taken for a side that deleted the file.
        /// </summary>
        public static HashSet<int> Stages(UnitGitService git, string path)
        {
            var listed = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "-c", "core.quotePath=true", "--literal-pathspecs", "ls-files", "--unmerged", "--", path);
            if (!listed.Success) throw new IOException("Git could not list the versions of " + path + ": " + listed.Message);
            var stages = new HashSet<int>();
            foreach (string line in UnitGitService.SplitLines(listed.StandardOutput))
            {
                // "<mode> <object> <stage>\t<path>"
                int tab = line.IndexOf('\t');
                if (tab < 0 || UnitGitService.UnquotePath(line.Substring(tab + 1)) != path) continue;
                string[] fields = line.Substring(0, tab).Split(' ');
                if (fields.Length == 3 && int.TryParse(fields[2], out int stage) && stage >= 1 && stage <= 3) stages.Add(stage);
            }
            return stages;
        }

        /// <summary>
        /// One version of a conflicted file (1 base, 2 mine, 3 theirs), read byte for byte. Unity text assets use LF, so the
        /// sides are compared and written with LF. Null when the version is binary: it is never read as text. The version
        /// must exist (see <see cref="Stages"/>); a failed read throws.
        /// </summary>
        public static string Stage(UnitGitService git, string path, int stage)
        {
            string file = Path.Combine(Path.GetTempPath(), "unitgit-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!git.WriteBlob(":" + stage + ":" + path, file))
                    throw new IOException("Git could not read " + (stage == 2 ? "your" : stage == 3 ? "their" : "the common") + " version of " + path + ".");
                using (var stream = File.OpenRead(file))
                {
                    var head = new byte[(int)Math.Min(stream.Length, BinarySniffBytes)];
                    int read = 0;
                    for (int n; read < head.Length && (n = stream.Read(head, read, head.Length - read)) > 0;) read += n;
                    if (Array.IndexOf(head, (byte)0, 0, read) >= 0) return null;
                }
                return Normalize(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            }
            finally
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The file on disk as it is now (a hash of its bytes, or "missing"), to tell later whether it changed.</summary>
        public static string FileState(string fullPath)
        {
            if (!File.Exists(fullPath)) return "missing";
            using (var hash = SHA256.Create())
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Convert.ToBase64String(hash.ComputeHash(stream));
        }

        // ---- Unity's merge ---------------------------------------------------------------------------------------------

        public static string UnityMergeTool()
        {
            string tools = Path.Combine(EditorApplication.applicationContentsPath, "Tools");
            string exe = Path.Combine(tools, "UnityYAMLMerge.exe");
            if (File.Exists(exe)) return exe;
            string plain = Path.Combine(tools, "UnityYAMLMerge");
            return File.Exists(plain) ? plain : null;
        }

        /// <summary>
        /// Runs Unity's merge on the three versions in a scratch folder (never on the project file). "--fallback none" keeps
        /// it from opening another merge program when it cannot decide.
        /// </summary>
        public static UnityMergeResult UnityMerge(string tool, string mine, string baseText, string theirs, string extension,
            int timeoutMilliseconds = UnityMergeTimeoutMilliseconds)
        {
            var result = new UnityMergeResult { Available = tool != null };
            if (tool == null)
            {
                result.Message = "Unity's merge tool was not found next to this editor.";
                return result;
            }
            string folder = Path.Combine(Path.GetTempPath(), "unitgit-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string basePath = Path.Combine(folder, "base" + extension), minePath = Path.Combine(folder, "mine" + extension);
                string theirsPath = Path.Combine(folder, "theirs" + extension), output = Path.Combine(folder, "merged" + extension);
                File.WriteAllText(basePath, baseText ?? string.Empty);
                File.WriteAllText(minePath, mine ?? string.Empty);
                File.WriteAllText(theirsPath, theirs ?? string.Empty);
                var start = new ProcessStartInfo(tool, "merge -h -p --force --fallback none " + Quote(basePath) + " " + Quote(theirsPath) + " " + Quote(minePath) + " " + Quote(output))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };
                var process = EditorProcessRunner.Run(start, timeoutMilliseconds);
                if (process.TimedOut || process.Cancelled)
                {
                    result.Message = process.Cancelled ? "Unity's merge was interrupted by editor reload or shutdown."
                        : "Unity's merge did not finish in " + timeoutMilliseconds / 1000 + " seconds.";
                    return result;
                }
                ParseConflicts(process.StandardOutput, result);
                if (process.Success && File.Exists(output))
                {
                    result.Output = File.ReadAllText(output);
                    result.Clean = true;
                }
                else if (result.Conflicts.Count == 0)
                {
                    string said = (process.StandardOutput + process.StandardError).Trim();
                    result.Message = said.Length > 0 ? said : "Unity's merge stopped without a result (exit code " + process.ExitCode + ").";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)
            {
                result.Message = ex.Message;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return result;
        }

        // "Left  558243963.GameObject.m_Name change to String Right" (theirs) and "Right ..." (mine) for one property.
        internal static void ParseConflicts(string output, UnityMergeResult result)
        {
            var sides = new Dictionary<string, UnityMergeConflict>();
            foreach (Match match in Regex.Matches(output ?? string.Empty, @"^(Left|Right)\s+(\S+?)\s+(?:change to|changed to)\s?(.*)$", RegexOptions.Multiline))
            {
                string key = match.Groups[2].Value;
                if (!sides.TryGetValue(key, out var conflict))
                {
                    int dot = key.IndexOf('.');
                    int second = dot >= 0 ? key.IndexOf('.', dot + 1) : -1;
                    conflict = new UnityMergeConflict
                    {
                        Object = dot > 0 ? key.Substring(0, second > 0 ? second : dot) : key,
                        Property = second > 0 ? key.Substring(second + 1) : key
                    };
                    sides[key] = conflict;
                    result.Conflicts.Add(conflict);
                }
                if (match.Groups[1].Value == "Left") conflict.Theirs = match.Groups[3].Value.TrimEnd('\r');
                else conflict.Mine = match.Groups[3].Value.TrimEnd('\r');
            }
        }

        // ---- Parts ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The file as shared parts and conflicting parts, from Git's line merge of the three versions. Throws when the merge
        /// failed or timed out: its parts are never guessed from what it left behind.
        /// </summary>
        public static List<ConflictBlock> Blocks(UnitGitService git, string mine, string baseText, string theirs)
        {
            mine = Normalize(mine);
            baseText = Normalize(baseText);
            theirs = Normalize(theirs);
            string folder = Path.Combine(Path.GetTempPath(), "unitgit-blocks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string m = Path.Combine(folder, "mine"), b = Path.Combine(folder, "base"), t = Path.Combine(folder, "theirs");
                File.WriteAllText(m, mine);
                File.WriteAllText(b, baseText);
                File.WriteAllText(t, theirs);
                // Git writes the merge over "mine" byte for byte; its standard output would come back line by line, which
                // loses whether the file ends with a line break. The exit status is the number of conflicts, at most 127.
                var merged = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge-file", "--diff3", "-L", MineLabel, "-L", BaseLabel, "-L", TheirsLabel, m, b, t);
                if (merged.TimedOut || merged.ExitCode < 0 || merged.ExitCode > 127)
                    throw new IOException("Git could not merge the versions: " + merged.Message);
                var blocks = Parse(Encoding.UTF8.GetString(File.ReadAllBytes(m)));
                // A run that never happened also ends in 1: the count Git reports must be the parts it wrote.
                int conflicts = blocks.Count(block => block.IsConflict);
                if (conflicts != merged.ExitCode && !(merged.ExitCode == 127 && conflicts > 127))
                    throw new IOException("Git's merge reported " + merged.ExitCode + " conflicting part" + (merged.ExitCode == 1 ? "" : "s") + " but wrote " + conflicts +
                                          ". " + (merged.Message.Length > 0 ? merged.Message : "The file may already hold conflict markers: keep a whole side instead."));
                KeepEnding(blocks, mine, baseText, theirs);
                return blocks;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // Git ends each side of a conflict with a line break, even at the end of a file that had none. A side that ends the
        // file gets its own ending back, so choosing it writes the end of the file as that side had it.
        private static void KeepEnding(List<ConflictBlock> blocks, string mine, string baseText, string theirs)
        {
            var last = blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
            if (last == null || !last.IsConflict) return;
            last.Mine = Ending(last.Mine, mine);
            last.Base = Ending(last.Base, baseText);
            last.Theirs = Ending(last.Theirs, theirs);
        }

        private static string Ending(string part, string whole)
        {
            return part.EndsWith("\n", StringComparison.Ordinal) && !whole.EndsWith("\n", StringComparison.Ordinal) ? part.Substring(0, part.Length - 1) : part;
        }

        internal static List<ConflictBlock> Parse(string merged)
        {
            var blocks = new List<ConflictBlock>();
            var common = new StringBuilder();
            var lines = Normalize(merged).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("<<<<<<< ", StringComparison.Ordinal))
                {
                    common.Append(line);
                    if (i < lines.Length - 1) common.Append('\n');
                    continue;
                }
                if (common.Length > 0) { blocks.Add(new ConflictBlock { Common = common.ToString() }); common.Clear(); }
                var block = new ConflictBlock { IsConflict = true };
                var mine = new StringBuilder();
                var baseText = new StringBuilder();
                var theirs = new StringBuilder();
                var target = mine;
                for (i++; i < lines.Length; i++)
                {
                    line = lines[i];
                    if (line.StartsWith("||||||| ", StringComparison.Ordinal)) { target = baseText; continue; }
                    if (line == "=======") { target = theirs; continue; }
                    if (line.StartsWith(">>>>>>> ", StringComparison.Ordinal)) break;
                    target.Append(line).Append('\n');
                }
                block.Mine = mine.ToString();
                block.Base = baseText.ToString();
                block.Theirs = theirs.ToString();
                blocks.Add(block);
            }
            if (common.Length > 0) blocks.Add(new ConflictBlock { Common = common.ToString() });
            AddUnityContext(blocks);
            return blocks;
        }

        // Each conflicting part of a Unity file: the object and component it sits in, from the text before it.
        private static void AddUnityContext(List<ConflictBlock> blocks)
        {
            string type = string.Empty, name = string.Empty;
            var header = new Regex(@"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline);
            foreach (var block in blocks)
            {
                if (!block.IsConflict)
                {
                    foreach (string line in block.Common.Split('\n'))
                    {
                        if (header.IsMatch(line)) { type = string.Empty; name = string.Empty; continue; }
                        if (type.Length == 0 && line.Length > 1 && line[0] != ' ' && line.EndsWith(":", StringComparison.Ordinal)) { type = line.TrimEnd(':'); continue; }
                        if (line.StartsWith("  m_Name: ", StringComparison.Ordinal)) name = line.Substring(10).Trim();
                    }
                    continue;
                }
                // The name itself may be what conflicts: then the object goes by both names.
                string own = name;
                if (own.Length == 0)
                {
                    string mine = NameIn(block.Mine), theirs = NameIn(block.Theirs);
                    own = mine == theirs || theirs.Length == 0 ? mine : mine.Length == 0 ? theirs : mine + " / " + theirs;
                }
                if (type.Length > 0) block.Context = own.Length > 0 ? own + " (" + Semantic.UnitySemanticNames.Words(type) + ")" : Semantic.UnitySemanticNames.Words(type);
            }
        }

        private static string NameIn(string text)
        {
            foreach (string line in (text ?? string.Empty).Split('\n'))
                if (line.StartsWith("  m_Name: ", StringComparison.Ordinal)) return line.Substring(10).Trim();
            return string.Empty;
        }

        /// <summary>
        /// The file with every conflicting part replaced by its choice, ending as the merge does (a file without a final line
        /// break keeps it that way). Null while a part has no choice.
        /// </summary>
        public static string Compose(IList<ConflictBlock> blocks)
        {
            var builder = new StringBuilder();
            foreach (var block in blocks)
            {
                if (!block.IsConflict) { builder.Append(block.Common); continue; }
                switch (block.Choice)
                {
                    case ConflictChoice.Mine: builder.Append(block.Mine); break;
                    case ConflictChoice.Theirs: builder.Append(block.Theirs); break;
                    case ConflictChoice.Both:
                        builder.Append(block.Mine);
                        // Mine may end the file without a line break: theirs still starts on a line of its own.
                        if (block.Mine.Length > 0 && !block.Mine.EndsWith("\n", StringComparison.Ordinal)) builder.Append('\n');
                        builder.Append(block.Theirs);
                        break;
                    default: return null;
                }
            }
            return builder.ToString();
        }

        // ---- Resolving --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Writes the resolved text over the project file and marks it resolved. Refused when the file on disk is no longer
        /// <paramref name="expected"/>, its <see cref="FileState"/> when the parts were read: a choice made on an older read
        /// never overwrites edits made since.
        /// </summary>
        public static GitCommandResult Resolve(UnitGitService git, string path, string text, string expected)
        {
            if (text == null) return Failure("Choose every part before saving " + path + ".");
            string full = Path.Combine(git.ProjectRoot, path);
            if (FileState(full) != expected)
                return Failure(path + " changed on disk after Mixer Git read it, so nothing was saved. Look at it again, then choose.");
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, text);
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "add", "--", path);
        }

        /// <summary>
        /// Keeps one side for the whole file, including "the file is deleted" when that side has no file. Which sides exist
        /// comes from Git's list of versions, and the kept version is written by Git byte for byte, never read as text. Any
        /// failed read stops here: it never becomes a deletion, and the project file is replaced only by a complete version.
        /// </summary>
        public static GitCommandResult TakeSide(UnitGitService git, string path, bool mine)
        {
            int stage = mine ? 2 : 3;
            HashSet<int> stages;
            try
            {
                stages = Stages(git, path);
            }
            catch (IOException ex)
            {
                return Failure(ex.Message);
            }
            if (stages.Count == 0) return Failure(path + " is not in conflict any more. Refresh, then look at it again.");
            if (!stages.Contains(stage))
                return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "rm", "--quiet", "--", path);

            // Git writes the version to a temporary file next to the project (with the filters a checkout applies) and names it.
            var written = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "checkout-index", "--temp", "--stage=" + stage, "--", path);
            if (!written.Success) return written;
            string name = UnitGitService.SplitLines(written.StandardOutput).FirstOrDefault()?.Split('\t')[0] ?? string.Empty;
            string temporary = Path.Combine(git.ProjectRoot, name);
            if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || !File.Exists(temporary))
                return Failure("Git did not say where it wrote the version of " + path + ": " + written.Message);
            string full = Path.Combine(git.ProjectRoot, path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                if (File.Exists(full)) File.Replace(temporary, full, null);
                else File.Move(temporary, full);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return Failure("Could not write " + path + ": " + ex.Message);
            }
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "add", "--", path);
        }

        /// <summary>Records the resolved merge (or continues the copy/revert/rebase) with Git's prepared message.</summary>
        public static GitCommandResult Finish(UnitGitService git, UnitGitOperation operation)
        {
            switch (operation)
            {
                case UnitGitOperation.Merge: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "commit", "--no-edit");
                case UnitGitOperation.CherryPick: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "cherry-pick", "--continue");
                case UnitGitOperation.Revert: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "revert", "--continue");
                case UnitGitOperation.Rebase: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "rebase", "--continue");
                default: return new GitCommandResult { StandardOutput = "All conflicts are resolved." };
            }
        }

        public static GitCommandResult Abort(UnitGitService git, UnitGitOperation operation)
        {
            switch (operation)
            {
                case UnitGitOperation.Merge: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge", "--abort");
                case UnitGitOperation.CherryPick: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "cherry-pick", "--abort");
                case UnitGitOperation.Revert: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "revert", "--abort");
                case UnitGitOperation.Rebase: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "rebase", "--abort");
                default: return new GitCommandResult { ExitCode = 1, StandardError = "Nothing to cancel." };
            }
        }

        /// <summary>Merges a branch into the current one. Conflicts are a normal outcome here, not a failure.</summary>
        public static GitCommandResult MergeBranch(UnitGitService git, string branch)
        {
            var result = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge", "--no-edit", "--no-ff", branch);
            if (!result.Success && (result.StandardOutput + result.StandardError).IndexOf("CONFLICT", StringComparison.Ordinal) >= 0)
                return new GitCommandResult { StandardOutput = "The merge has conflicts: resolve them in the Conflicts tab." };
            return result;
        }

        private static string Normalize(string text) => (text ?? string.Empty).Replace("\r\n", "\n");

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        private static GitCommandResult Failure(string message) => new GitCommandResult { ExitCode = 1, StandardError = message };
    }
}
