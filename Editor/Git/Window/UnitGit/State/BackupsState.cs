using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Backups tab's state (the backup highlighted): what used to be fields of
    /// <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitBackupsState
    {
        // The backup just made or checked: its row flashes once so the eye finds it.
        internal string highlightedBackup;
    }
}
