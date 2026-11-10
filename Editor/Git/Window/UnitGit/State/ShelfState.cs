using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Shelf tab's state (the shelves, the one and the file selected, its diff pane): what used to be fields of
    /// <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitShelfState
    {
        internal string selectedShelf = string.Empty;
        internal readonly Dictionary<string, List<UnitGitWindow.ShelfFile>> shelfFiles = new Dictionary<string, List<UnitGitWindow.ShelfFile>>(StringComparer.Ordinal);
        internal readonly HashSet<string> expandedShelves = new HashSet<string>(StringComparer.Ordinal);
        internal string selectedShelfFile = string.Empty;
        internal VisualElement shelfListRoot;
        internal VisualElement shelfDiffRoot;
        internal UnitGitDiffViewer shelfDiffViewer;
        internal int shelfDiffRequest;
    }
}
