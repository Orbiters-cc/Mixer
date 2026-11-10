using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The visit banner's state (the commit being visited, a Back under way, the banner's elements): what used to be fields of
    /// <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitVisitState
    {
        internal VisualElement visitBanner;
        internal Label visitTitle;
        internal Label visitDetail;
        internal Button visitNewBranchButton;
        internal Button visitBackButton;
        // Shown before Git is done: the commit a visit checks out, the branch "Back" checks out.
        internal UnitGitCommit pendingVisit;
        internal string pendingReturnBranch;
        // When the last visit or return finished (UTC ticks): Git state read after it replaces the pending state.
        internal long visitActionDone;
        // The visit ends through Back, which says itself what became of the shelf.
        internal bool visitReturning;
    }
}
