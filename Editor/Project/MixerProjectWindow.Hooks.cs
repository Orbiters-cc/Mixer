using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // What Unity sends to its Project window, answered here: pings (UnityWindowHooks), and the naming Unity's create items
    // hand over (UnityCreateFlow), shown as a line being renamed where the asset goes; Enter makes it under the name typed,
    // Escape makes nothing, as in Unity's window. Unity's Import New Asset brings the file into the folder asked for.
    internal sealed partial class MixerProjectWindow
    {
        // The line of an asset being named before it exists.
        internal const string PendingPath = "\u0001new";
        private const string ImportFolderPref = "Orbiters.Mixer.Project.ImportFolder";

        // Not kept across a script reload: Unity's naming does not survive one either.
        [NonSerialized] private UnityCreateFlow.Pending pending;

        /// <summary>The naming Unity handed over, until it ends (tests).</summary>
        internal UnityCreateFlow.Pending PendingCreate => pending;
        /// <summary>The line flashing after a ping (tests).</summary>
        internal string PingedPath => pingPath;

        private void HookUnity() => UnityWindowHooks.ProjectPinged += OnUnityPing;

        private void OnUnityPing(string path)
        {
            if (UnityWindowHooks.Reaches(this)) Ping(path);
        }

        private void UnhookUnity()
        {
            UnityWindowHooks.ProjectPinged -= OnUnityPing;
            // The window goes (closed, or a script reload) while an asset is being named: nothing is made, as Unity's naming
            // is lost with its window.
            if (pending == null) return;
            var gone = pending;
            pending = null;
            renamingPath = null;
            UnityCreateFlow.Cancel(gone);
        }

        // ---- Unity's create items ------------------------------------------------------------------------------------------

        // The asset goes where it was asked for (Unity puts a bare name beside the selected asset), under a name free there,
        // on screen and being renamed.
        private void BeginPending(UnityCreateFlow.Pending made, string target)
        {
            made.Folder = target;
            string free = AssetDatabase.GenerateUniqueAssetPath(target + "/" + made.Name + made.Extension);
            made.Name = made.IsFolder ? Path.GetFileName(free) : Path.GetFileNameWithoutExtension(free);
            pending = made;
            if (Searching || showRecent || (target != folder && (GridMode || !target.StartsWith(folder + "/", StringComparison.Ordinal)))) Navigate(target);
            for (string p = target; p != null && p != folder; p = ParentOf(p)) contentOpenSet.Add(p);
            renamingPath = PendingPath;
            MarkAllDirty();
            RebuildNow();
            int index = IndexOf(PendingPath);
            if (index >= 0) RevealIndex(index);
            UpdateFooter();
        }

        // First in its folder: at the top of the open folder, or just under the unfolded folder it goes in.
        private void InsertPending()
        {
            if (pending == null || Searching || showRecent) return;
            int at = -1, depth = 0;
            if (pending.Folder == folder) at = 0;
            else
            {
                int parent = rows.FindIndex(r => !r.IsHeading && r.Path == pending.Folder);
                if (parent >= 0 && rows[parent].Expanded) { at = parent + 1; depth = rows[parent].Depth + 1; }
            }
            if (at < 0) return;
            rows.Insert(at, new ProjectItem
            {
                Path = PendingPath, Name = pending.Name, Extension = pending.Extension, IsFolder = pending.IsFolder, Empty = pending.IsFolder,
                Depth = depth, Tag = pending,
            });
        }

        private static bool IsPending(ProjectItem item) => item != null && item.Tag is UnityCreateFlow.Pending;

        /// <summary>Ends the naming as its line's rename field does: Enter (and a click elsewhere) makes the asset under the
        /// name typed, Escape makes nothing.</summary>
        internal void EndPending(string name, bool make)
        {
            var made = pending;
            pending = null;
            if (renamingPath == PendingPath) renamingPath = null;
            if (made == null) return;
            string path = make ? UnityCreateFlow.Finish(made, name) : null;
            if (!make) UnityCreateFlow.Cancel(made);
            MarkAllDirty();
            if (path != null) Created(path, selectInUnity: !AssetDatabase.IsValidFolder(path), rename: false);
            else RebuildNow();
            root?.Focus();
        }

        private void CommitPending(TextField field, bool apply)
        {
            // A pooled field that no longer shows the line holds no name for it: the name Unity gave stands.
            bool ours = field.userData is string bound && bound == PendingPath;
            field.userData = null;
            EndPending(ours ? field.value : pending?.Name, apply);
        }

        // Its line no longer shows (its folder was deleted or moved meanwhile): the naming ends as a click elsewhere ends it,
        // or makes nothing when its folder is gone.
        private void TickPending()
        {
            if (pending == null || dirty || IndexOf(PendingPath) >= 0) return;
            if (AssetDatabase.IsValidFolder(pending.Folder)) EndPending(pending.Name, true);
            else EndPending(null, false);
        }

        // ---- Import New Asset ------------------------------------------------------------------------------------------------

        /// <summary>Unity's Import New Asset, into <paramref name="target"/> (Unity's own imports into its Project window's
        /// folder, or Assets).</summary>
        internal void ImportNewAsset(string target)
        {
            if (!CanCreateIn(target)) return;
            string file = EditorUtility.OpenFilePanel("Import New Asset", EditorPrefs.GetString(ImportFolderPref, string.Empty), string.Empty);
            if (string.IsNullOrEmpty(file)) return;
            EditorPrefs.SetString(ImportFolderPref, Path.GetDirectoryName(file));
            ImportInto(target, new List<string> { file });
        }
    }
}
