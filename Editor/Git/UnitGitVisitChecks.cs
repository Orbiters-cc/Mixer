using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Processes;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>A package Mixer Git runs on (Mixer, the Toolkit) and its package.json in Git now and at a commit to visit.</summary>
    internal sealed class UnitGitToolPackage
    {
        public string Id = string.Empty; // "orbiters.mixer"
        public string Name = string.Empty; // "Mixer"
        public string PackageJson = string.Empty; // relative to the project: "Packages/orbiters.mixer/package.json"
        public string Running = string.Empty; // the version Unity runs now
        public string AtHead; // package.json as committed at HEAD; null when Git has none (an ignored or outside package)
        public string AtTarget; // package.json at the commit to visit; null when it has none
    }

    /// <summary>The three ways out of the "changed while visiting" dialog. Shelving is the default (Enter), Escape cancels.</summary>
    internal enum UnitGitVisitChangesChoice { Shelve, Cancel, Discard }

    /// <summary>What Visit This Commit and Back check before Git rewrites the project, and how they say it.</summary>
    internal static class UnitGitVisitChecks
    {
        internal const string ShelveButton = "Shelve Them";
        internal const string CancelButton = "Cancel";
        internal const string DiscardButton = "Discard Changes";

        [Serializable]
        internal sealed class PackageManifest
        {
            public string version = string.Empty;
        }

        /// <summary>The answer of <c>DisplayDialogComplex(…, ShelveButton, CancelButton, DiscardButton)</c>.</summary>
        internal static UnitGitVisitChangesChoice ChangesChoice(int dialogResult)
        {
            switch (dialogResult)
            {
                case 0: return UnitGitVisitChangesChoice.Shelve;
                case 2: return UnitGitVisitChangesChoice.Discard;
                default: return UnitGitVisitChangesChoice.Cancel;
            }
        }

        /// <summary>
        /// The paths of <paramref name="incoming"/> that something Git does not track already holds: a file Git listed itself,
        /// or a file on disk inside a folder Git listed whole ("Library/").
        /// </summary>
        internal static List<string> InTheWay(IEnumerable<string> incoming, IEnumerable<string> listed, Func<string, bool> exists)
        {
            // Windows and macOS file systems ignore case: "assets/a.meta" is in the way of "Assets/a.meta".
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in listed)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                if (entry.EndsWith("/", StringComparison.Ordinal)) folders.Add(entry);
                else files.Add(entry);
            }

            var found = new List<string>();
            if (files.Count == 0 && folders.Count == 0) return found;
            foreach (string path in incoming)
            {
                if (files.Contains(path))
                {
                    found.Add(path);
                    continue;
                }

                for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
                {
                    if (!folders.Contains(path.Substring(0, slash + 1))) continue;
                    if (exists(path)) found.Add(path);
                    break;
                }
            }

            return found;
        }

        /// <summary>The "version" of a package.json, or empty.</summary>
        internal static string PackageVersion(string packageJson)
        {
            if (string.IsNullOrWhiteSpace(packageJson)) return string.Empty;
            try
            {
                return (JsonUtility.FromJson<PackageManifest>(packageJson)?.version ?? string.Empty).Trim();
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        /// <summary>Mixer (with Mixer Git) and the Toolkit it runs on, where they sit inside the project (main thread: Package Manager).</summary>
        internal static List<UnitGitToolPackage> ToolPackages(string projectRoot)
        {
            var tools = new List<UnitGitToolPackage>();
            AddToolPackage(tools, projectRoot, typeof(UnitGitVisitChecks).Assembly);
            AddToolPackage(tools, projectRoot, typeof(EditorProcessRunner).Assembly);
            return tools;
        }

        private static void AddToolPackage(List<UnitGitToolPackage> tools, string projectRoot, System.Reflection.Assembly assembly)
        {
            UnityEditor.PackageManager.PackageInfo info;
            try
            {
                info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
            }
            catch (Exception)
            {
                return;
            }

            if (info == null || string.IsNullOrWhiteSpace(info.resolvedPath)) return;
            // A package outside the project folder is outside its Git too: no checkout touches it.
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string folder = Path.GetFullPath(info.resolvedPath);
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            tools.Add(new UnitGitToolPackage
            {
                Id = info.name ?? string.Empty,
                Name = string.IsNullOrWhiteSpace(info.displayName) ? info.name ?? string.Empty : info.displayName,
                PackageJson = folder.Substring(root.Length).Replace('\\', '/').TrimEnd('/') + "/package.json",
                Running = info.version ?? string.Empty
            });
        }

        /// <summary>
        /// The warning before visiting a commit that holds another Mixer or Toolkit (or none), or null when the visit
        /// keeps the ones running now. <paramref name="branch"/> and <paramref name="shelf"/> say the way back without Mixer Git.
        /// </summary>
        internal static string ToolChangeText(IEnumerable<UnitGitToolPackage> tools, string visited, string branch, bool shelf)
        {
            var found = new List<string>();
            bool mixerGone = false, mixerOther = false;
            foreach (UnitGitToolPackage tool in tools)
            {
                // Not in Git now nor then (ignored, in Library or outside the project): the visit leaves it as it is.
                if (tool.AtHead == null && tool.AtTarget == null) continue;
                string running = !string.IsNullOrWhiteSpace(tool.Running) ? tool.Running.Trim() : PackageVersion(tool.AtHead);
                string then = tool.AtTarget != null ? PackageVersion(tool.AtTarget) : null;
                if (then != null && then.Length > 0 && then == running) continue;
                bool mixer = tool.Id == "orbiters.mixer";
                if (then == null)
                {
                    found.Add("no " + tool.Name);
                    mixerGone |= mixer;
                }
                else
                {
                    found.Add(tool.Name + " " + (then.Length > 0 ? then : "of another version") + (running.Length > 0 ? " (" + running + " now)" : string.Empty));
                    mixerOther |= mixer;
                }
            }

            if (found.Count == 0) return null;
            string text = "At " + visited + " the project has " + string.Join(" and ", found) + ". Unity runs that code while you visit";
            if (mixerGone) text += ": Mixer goes away, and this window with it.";
            else if (mixerOther) text += ": another Mixer may have no Mixer Git or no Back button, and not bring back the changes it shelved.";
            else text += ".";
            text += " A mix of package versions may not compile, and Unity may then offer Safe Mode.\n\n" +
                    "To come back without Mixer Git, run \"git checkout " + (string.IsNullOrWhiteSpace(branch) ? "<branch>" : branch) + "\" in a terminal" +
                    (shelf ? ", then \"git stash pop\" for the changes shelved before visiting." : ".");
            return text;
        }

        /// <summary>The warning before a visit overwrites ignored files with the commit's copy (Back then deletes them).</summary>
        internal static string IgnoredInTheWayText(IList<string> ignored, string visited)
        {
            return Plural(ignored.Count, "ignored file") + (ignored.Count == 1 ? " is" : " are") + " where " + visited + " has a file of its own. " +
                   "The visit overwrites " + (ignored.Count == 1 ? "it" : "them") + " with " + visited + "'s copy and going back deletes " +
                   (ignored.Count == 1 ? "it" : "them") + ": Git never shelves ignored files.\n\n" + ListPaths(ignored, 8, "\n") +
                   "\n\nCopy " + (ignored.Count == 1 ? "it" : "them") + " out of the project first to keep " + (ignored.Count == 1 ? "it." : "them.");
        }

        /// <summary>
        /// The "changed while visiting" dialog: files edited, and new files where <paramref name="target"/> (the branch Back
        /// goes to, the next commit to visit) has its own, which Git would not check out over.
        /// </summary>
        internal static string ChangedWhileVisitingText(int tracked, int inTheWay, string visited, string target)
        {
            var found = new List<string>();
            if (tracked > 0) found.Add(Plural(tracked, "file") + " changed");
            if (inTheWay > 0) found.Add(Plural(inTheWay, "new file") + (inTheWay == 1 ? " was" : " were") + " made where " + target + " has its own");
            return "While you were visiting " + visited + ", " + string.Join(" and ", found) + ". Unity re-saves and makes some files when it opens an older version.\n\n" +
                   ShelveButton + " keeps them on the shelf. " + DiscardButton + " puts edited files back as they were committed" +
                   (inTheWay > 0 ? ", and replaces the new ones with those of " + target + "." : ".") + " Other new files stay either way.";
        }

        /// <summary>
        /// Before leaving a visited commit (<paramref name="leaving"/>: "going back to ‘main’", "visiting abc1234"): commits
        /// made on it belong to no branch, and leave the Log.
        /// </summary>
        internal static string CommitsLeftBehindText(int count, string leaving)
        {
            string them = count == 1 ? "it" : "them";
            return Plural(count, "commit") + " made here " + (count == 1 ? "belongs" : "belong") + " to no branch. After " + leaving + " the Log no longer shows " +
                   them + ", and only Git's reflog keeps " + them + " for a while.\n\n" +
                   "New Branch Here… keeps " + them + " on a branch of " + (count == 1 ? "its" : "their") + " own.";
        }

        /// <summary>The first <paramref name="shown"/> paths, then "and N more".</summary>
        internal static string ListPaths(IList<string> paths, int shown, string separator)
        {
            if (paths == null || paths.Count == 0) return string.Empty;
            string listed = string.Join(separator, paths.Take(shown));
            int more = paths.Count - Math.Min(shown, paths.Count);
            return more > 0 ? listed + (separator == "\n" ? "\n" : " ") + "and " + more + " more" : listed;
        }

        private static string Plural(int count, string noun) => count + " " + noun + (count == 1 ? string.Empty : "s");
    }
}
