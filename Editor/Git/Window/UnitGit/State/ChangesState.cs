using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Local Changes tab's state (the change list, what is selected and being included, the diff pane): what used to
    /// be fields of <see cref="UnitGitWindow"/> shared by every partial file. The commit message and its options stay
    /// serialized fields of the window, so a reload keeps them. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitChangesState
    {
        internal string selectedChangePath = string.Empty;
        internal VisualElement localChangesListRoot;
        internal VisualElement localDiffPaneRoot;
        internal string diffSearch = string.Empty;
        internal int diffSearchMatchIndex;
        internal TextField diffSearchField;
        internal bool excludePackageFolderFromInitialCommit = true;
        internal readonly UnitGitLatestRequest<UnitGitDiff> diffRead = new UnitGitLatestRequest<UnitGitDiff>();
        internal UnitGitDiff loadedDiff;
        internal string requestedDiffPath;
        // What the text diff asked last was read from (see TextDiffKey); null: no text diff asked for this file yet.
        internal string requestedDiffKey;
        internal UnitGitDiffViewer diffViewer;
        internal Label diffCountLabel;
        // A newly selected file opens at its first change; a refresh of the same file keeps the scroll.
        internal string jumpedToFirstChange;
        internal readonly HashSet<string> selectedChangePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> collapsedChangeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Boxes ticked or cleared but not yet written by Git: shown at once, reconciled by the next refresh.
        internal readonly Dictionary<string, bool> pendingInclude = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<(List<string> paths, bool include)> includeQueue = new List<(List<string> paths, bool include)>();
        internal bool includeRunning;
        internal int includeGeneration;
        internal int includeRunningCount;
        internal bool includeRunningAdds;
        // Commit (true: and push) clicked while Git was still including files: it runs as soon as they are in.
        internal bool? commitWhenIncluded;
        internal double includeStarted;
        internal string includePaths;
        internal Label commitProgressLabel;
        internal string changeFilter = string.Empty;
        internal string changeAnchorPath = string.Empty;
        internal List<UnitGitWindow.ChangeRow> changeRows = new List<UnitGitWindow.ChangeRow>();
        internal ListView changesListView;
        internal VisualElement changesNoticesRoot;
        internal string changesNoticesKey;
        internal Label commitSummaryLabel;
        internal Button commitButton;
        internal Button commitPushButton;
        internal UnitGitCheck amendCheck;
        internal Button addButton;
    }
}
