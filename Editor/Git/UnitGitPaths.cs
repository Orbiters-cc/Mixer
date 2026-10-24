using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Where Git keeps its own files for a project ("index", "MERGE_HEAD", "info/exclude"). In a linked worktree ".git" is a
    /// file pointing elsewhere, so the paths are asked of Git ("rev-parse --git-path") and remembered per project.
    /// </summary>
    internal static class UnitGitPaths
    {
        // Asked together the first time: one Git call answers them all.
        private static readonly string[] Usual =
        {
            "index", "MERGE_HEAD", "MERGE_MSG", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG", "logs/HEAD", "info/exclude"
        };
        private static readonly Dictionary<string, Dictionary<string, string>> Known = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>The full path Git uses for <paramref name="name"/> in the project's repository. Null when Git cannot tell.</summary>
        public static string GitPath(string projectRoot, string name)
        {
            string root = Path.GetFullPath(projectRoot);
            lock (Known)
            {
                if (Known.TryGetValue(root, out var paths) && paths.TryGetValue(name, out string path)) return path;
            }

            string[] names = Array.IndexOf(Usual, name) >= 0 ? Usual : new[] { name };
            var arguments = new List<string> { "rev-parse" };
            foreach (string each in names)
            {
                arguments.Add("--git-path");
                arguments.Add(each);
            }

            // Relative to the project (".git/index"), or absolute in a linked worktree.
            var result = new UnitGitService(root).RunGit(UnitGitService.DefaultTimeoutMilliseconds, arguments.ToArray());
            var lines = UnitGitService.SplitLines(result.StandardOutput).Where(line => line.Length > 0).ToList();
            if (!result.Success || lines.Count != names.Length) return null;
            lock (Known)
            {
                if (!Known.TryGetValue(root, out var paths)) Known[root] = paths = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < names.Length; i++) paths[names[i]] = Path.GetFullPath(Path.Combine(root, lines[i]));
                return paths[name];
            }
        }
    }
}
