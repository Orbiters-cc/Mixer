using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The one question Mixer asks before edits are lost for good (a Revert, closing a scene that holds edited meshes):
    /// asked once for everything the action drops, never when nothing would be lost, and Unity's "do not show again"
    /// box turns it off on this machine. Every Revert of the window, the Scene view, the inspector and the menus goes
    /// through <see cref="Revert"/>.
    /// </summary>
    internal static class MixerConfirm
    {
        internal const string OptOutKey = "Orbiters.Mixer.Confirm.DropEdits";

        /// <summary>Asks (title, message, ok button) and says whether to go on. Tests replace it; null asks nothing.</summary>
        internal static Func<string, string, string, bool> Ask = (title, message, ok) =>
            EditorUtility.DisplayDialog(title, message, ok, "Cancel", DialogOptOutDecisionType.ForThisMachine, OptOutKey);

        /// <summary>The edited meshes among <paramref name="targets"/> whose edits would be lost.</summary>
        internal static List<EditTarget> WithEdits(IEnumerable<EditTarget> targets) =>
            targets.Where(t => t != null && t.Alive && MixerSession.Targets.Contains(t) && t.Document.Changed).Distinct().ToList();

        /// <summary>
        /// True when <paramref name="targets"/> can lose their edits: none has any, or the user agreed. <paramref name="what"/>
        /// names the action ("Revert", "Unload Scene"), <paramref name="consequence"/> what else happens.
        /// </summary>
        internal static bool DropEdits(IEnumerable<EditTarget> targets, string what, string consequence)
        {
            var edited = WithEdits(targets);
            if (edited.Count == 0 || Ask == null) return true;
            string names = edited.Count == 1 ? edited[0].Name : edited.Count + " meshes (" + string.Join(", ", edited.Take(4).Select(t => t.Name)) + (edited.Count > 4 ? "…" : "") + ")";
            string message = "The Mixer edits of " + names + " have not been committed. " + consequence + " This cannot be undone.";
            return Ask(what + (edited.Count == 1 ? " " + edited[0].Name : ""), message, what);
        }

        /// <summary>
        /// Reverts <paramref name="targets"/> (the original meshes back, every edit dropped) after asking once when any
        /// holds edits. Returns how many were reverted (0 when the user cancelled).
        /// </summary>
        internal static int Revert(IEnumerable<EditTarget> targets)
        {
            var open = targets.Where(t => t != null && MixerSession.Targets.Contains(t)).Distinct().ToList();
            if (open.Count == 0) return 0;
            bool plural = open.Count > 1;
            if (!DropEdits(open, "Revert", plural ? "Their original meshes come back." : "Its original mesh comes back.")) return 0;
            foreach (var t in open) MixerSession.Revert(t);
            return open.Count;
        }

        internal static int Revert(EditTarget target) => target == null ? 0 : Revert(new[] { target });
    }
}
