using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Conflicts tab's state (the file selected, the files resolved, the merge work under way): what used to be fields of
    /// <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitConflictsState
    {
        internal string selectedConflict;
        // Files resolved since the operation began: they stay listed, ticked, until it is finished or cancelled.
        internal readonly List<string> resolvedConflicts = new List<string>();
        internal readonly Dictionary<string, Task<UnitGitWindow.ConflictWork>> conflictWork = new Dictionary<string, Task<UnitGitWindow.ConflictWork>>();
        internal float lastConflictRatio;
    }
}
