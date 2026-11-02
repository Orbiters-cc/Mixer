using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's renames, moves and joins keep the avatar's animations pointing at their objects: the paths are taken before
    /// the change (<see cref="Capture"/>), the Toolkit's <see cref="AnimationPathWatch"/> plans what would break after it
    /// (<see cref="After"/>), and a compact question asks whether to update them (<see cref="Ask"/>). An update joins the
    /// change's own Undo step when nothing was done since.
    /// </summary>
    internal static class AnimationRepath
    {
        internal sealed class Captured
        {
            public Transform Target, Avatar;
            public string Former;
        }

        /// <summary>Each object's avatar and path from it, before a change (the avatar's root itself binds nothing).</summary>
        internal static List<Captured> Capture(IEnumerable<GameObject> objects)
        {
            var captured = new List<Captured>();
            foreach (var go in objects ?? Enumerable.Empty<GameObject>())
            {
                if (go == null) continue;
                var avatar = AnimationPaths.AvatarRootOf?.Invoke(go.transform);
                if (avatar == null || avatar == go.transform) continue;
                captured.Add(new Captured { Target = go.transform, Avatar = avatar, Former = AnimationUtility.CalculateTransformPath(go.transform, avatar) });
            }
            return captured;
        }

        /// <summary>
        /// After the change: per avatar whose animations would break, its notice (the avatars whose animations still hold are
        /// taken as they are). Call it in the event that made the change, after its Undo records.
        /// </summary>
        internal static List<AnimationPathWatch.Notice> After(List<Captured> captured)
        {
            var notices = new List<AnimationPathWatch.Notice>();
            if (captured == null) return notices;
            foreach (var group in captured.Where(c => c.Avatar != null && c.Target != null).GroupBy(c => c.Avatar))
            {
                var moves = group.Select(c => (c.Target, c.Former)).ToList();
                AnimationPathWatch.Notice notice;
                try { notice = AnimationPathWatch.Check(group.Key.gameObject, moves, What(group.ToList())); }
                catch (Exception e) { Debug.LogException(e); continue; }
                if (notice != null) notices.Add(notice);
            }
            return notices;
        }

        private static string What(List<Captured> moved)
        {
            if (moved.Count != 1) return "Moving " + moved.Count + " objects";
            var c = moved[0];
            int slash = c.Former.LastIndexOf('/');
            string parent = slash < 0 ? "" : c.Former.Substring(0, slash);
            string parentNow = c.Target.parent == c.Avatar ? "" : c.Target.parent != null && c.Target.IsChildOf(c.Avatar) ? AnimationUtility.CalculateTransformPath(c.Target.parent, c.Avatar) : null;
            return parent == parentNow ? "Renaming “" + c.Former.Substring(slash + 1) + "”" : "Moving “" + c.Target.name + "”";
        }

        /// <summary>The question, e.g. "3 animations point at “Jacket”. Update them?".</summary>
        internal static string Question(AnimationPathWatch.Notice notice, string name = null)
        {
            int n = notice.Plan.FixableAssets;
            string what = n + (n == 1 ? " animation points at “" : " animations point at “") + (name ?? Named(notice)) + "”.";
            if (notice.Plan.Shared) what += " Some are used elsewhere too: Update changes copies for this avatar.";
            int locked = notice.Plan.LockedAssets.Count();
            if (locked > 0) what += " " + locked + (locked == 1 ? " is" : " are") + " in a package or read-only and stays as it is.";
            return what + (n == 1 ? " Update it?" : " Update them?");
        }

        private static string Named(AnimationPathWatch.Notice notice)
        {
            // "Renaming “Jacket”" names the object; else the objects the animations reach.
            int open = notice.What.IndexOf('“'), close = notice.What.LastIndexOf('”');
            if (open >= 0 && close > open) return notice.What.Substring(open + 1, close - open - 1);
            return notice.Objects.Count == 1 ? notice.Objects[0] : notice.Objects.Count + " objects";
        }

        /// <summary>Asks (Update / Leave) in <paramref name="toasts"/> and tells what the update did.</summary>
        internal static void Ask(MixerToasts toasts, AnimationPathWatch.Notice notice, string name = null)
        {
            if (toasts == null || notice == null) return;
            Asked.Add(notice);
            toasts.Ask(Question(notice, name), "Update", () =>
            {
                AnimationPaths.Applied applied;
                try { applied = AnimationPathWatch.Fix(notice); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    toasts.Show("Updating the animations failed: " + e.Message, true);
                    return;
                }
                toasts.Show(applied.Paths == 0 ? "Nothing left to update." :
                    "Updated " + applied.Paths + (applied.Paths == 1 ? " path" : " paths") + " in " + applied.Assets + (applied.Assets == 1 ? " animation" : " animations") +
                    (applied.Copies > 0 ? " (" + applied.Copies + (applied.Copies == 1 ? " copy" : " copies") + " for this avatar)" : "") + ".");
            }, "Leave", () => AnimationPathWatch.Leave(notice));
        }

        // Notices already asked (by any Mixer window): the watcher's later report of the same change isn't asked again.
        internal static readonly HashSet<AnimationPathWatch.Notice> Asked = new HashSet<AnimationPathWatch.Notice>();

        /// <summary>The watcher's notices no Mixer window asked about yet (a rename in Unity's Hierarchy).</summary>
        internal static IEnumerable<AnimationPathWatch.Notice> Unasked()
        {
            Asked.RemoveWhere(n => !AnimationPathWatch.Notices.Contains(n));
            return AnimationPathWatch.Notices.Where(n => !Asked.Contains(n)).ToList();
        }

        // ---- Joins ----

        /// <summary>The bindings that follow a mesh join: shapes, switches and visibility of each source go to the result.</summary>
        internal static List<Retarget> JoinRetargets(IEnumerable<Renderer> sources, Renderer joined) =>
            sources.Where(r => r != null && joined != null).Select(r => new Retarget
            {
                From = r.transform, To = joined.transform,
                Applies = b => b.type == typeof(GameObject) && b.propertyName == "m_IsActive" ||
                               typeof(Renderer).IsAssignableFrom(b.type) && (b.propertyName == "m_Enabled" || b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
            }).ToList();

        /// <summary>
        /// Sources shown and hidden by different animations (one joined mesh can follow only one of them): their names, or
        /// an empty list when they all follow the same ones (or none).
        /// </summary>
        internal static List<string> DifferentToggles(IReadOnlyList<Renderer> sources)
        {
            var first = sources.FirstOrDefault(r => r != null);
            var avatar = first != null ? AnimationPaths.AvatarRootOf?.Invoke(first.transform) : null;
            if (avatar == null) return new List<string>();
            var toggles = AnimationPaths.Toggles(avatar.gameObject, sources);
            var reference = toggles[first];
            var differ = toggles.Where(p => !p.Value.SetEquals(reference)).Select(p => p.Key.name).ToList();
            if (differ.Count == 0) return differ;
            differ.Insert(0, first.name);
            return differ.Distinct().ToList();
        }

        /// <summary>
        /// After a join: the plan pointing the sources' animations at the result, or null when none animate them.
        /// </summary>
        internal static RepathPlan PlanJoin(IReadOnlyList<Renderer> sources, Renderer joined)
        {
            var avatar = joined != null ? AnimationPaths.AvatarRootOf?.Invoke(joined.transform) : null;
            if (avatar == null) return null;
            var plan = AnimationPaths.Plan(avatar.gameObject, (PathSnapshot)null, JoinRetargets(sources, joined), broken: false);
            return plan.Fixable > 0 ? plan : null;
        }

        /// <summary>Asks whether to point the sources' animations at the joined mesh; the update joins the join's Undo step.</summary>
        internal static void AskJoin(MixerToasts toasts, IReadOnlyList<Renderer> sources, Renderer joined, UndoStep step)
        {
            var plan = PlanJoin(sources, joined);
            if (plan == null || toasts == null) return;
            int n = plan.FixableAssets;
            string text = n + (n == 1 ? " animation shows, hides or shapes" : " animations show, hide or shape") + " the joined meshes. Point " + (n == 1 ? "it" : "them") +
                          " at “" + joined.name + "”?" + (plan.Shared ? " Some are used elsewhere too: Update changes copies for this avatar." : "");
            toasts.Ask(text, "Update", () =>
            {
                if (joined == null) { toasts.Show("The joined mesh is gone.", true); return; }
                // Planned again: the scene may have changed meanwhile.
                var now = PlanJoin(sources, joined);
                AnimationPaths.Applied applied = null;
                if (now != null) step.Join(() => applied = AnimationPaths.Apply(now, false, "Point animations at " + joined.name));
                toasts.Show(applied == null || applied.Paths == 0 ? "Nothing left to update." : "Updated " + applied.Paths + (applied.Paths == 1 ? " path." : " paths."));
            }, "Leave", null);
        }
    }
}
