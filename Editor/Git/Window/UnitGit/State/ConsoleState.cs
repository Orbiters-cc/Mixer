using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Console tab's state (the lines of Git's output kept, those waiting for the editor thread, the list and its filter): what used to be fields of
    /// <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitConsoleState
    {
        internal readonly List<string> consoleLines = new List<string>();
        internal readonly ConcurrentQueue<string> pendingConsoleLines = new ConcurrentQueue<string>();
        internal readonly List<string> consoleView = new List<string>();
        internal ListView consoleList;
        internal string consoleFilter = string.Empty;
    }
}
