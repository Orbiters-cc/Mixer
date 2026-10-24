using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitFileRow
    {
        public string Path;
        public string Name;
        public int Depth;
        public int FileCount;
        public bool IsFolder;
        public UnitGitStatusEntry Change;
    }

    internal sealed class UnitGitFileList
    {
        public readonly List<UnitGitFileRow> Rows = new List<UnitGitFileRow>();
        public readonly List<UnitGitFileRow> Folders = new List<UnitGitFileRow>();

        public void Add(UnitGitFileRow row)
        {
            Rows.Add(row);
            if (row.IsFolder)
                Folders.Add(row);
        }

        public List<UnitGitFileRow> GetVisibleRows(Func<UnitGitFileRow, bool> expanded)
        {
            var collapsed = new HashSet<UnitGitFileRow>(Folders.Where(folder => !expanded(folder)));
            if (collapsed.Count == 0)
                return Rows;
            var visible = new List<UnitGitFileRow>();
            int hiddenDepth = -1;
            foreach (var row in Rows)
            {
                if (hiddenDepth >= 0 && row.Depth > hiddenDepth)
                    continue;
                hiddenDepth = -1;
                visible.Add(row);
                if (collapsed.Contains(row))
                    hiddenDepth = row.Depth;
            }
            return visible;
        }

        public static UnitGitFileList FromChanges(IEnumerable<UnitGitStatusEntry> changes)
        {
            var result = new UnitGitFileList();
            var groups = changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(change => TopFolder(change.Path))
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                bool root = group.Key.Length == 0;
                if (!root)
                    result.Add(new UnitGitFileRow { Path = group.Key, Name = group.Key, IsFolder = true, FileCount = group.Count() });
                foreach (var change in group)
                    result.Add(new UnitGitFileRow { Path = change.Path, Change = change, Depth = root ? 0 : 1 });
            }
            return result;
        }

        private static string TopFolder(string path)
        {
            string normalized = path.Replace('\\', '/');
            int slash = normalized.IndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : string.Empty;
        }
    }
}
