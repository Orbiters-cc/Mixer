using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.VRChat
{
    /// <summary>
    /// Tells Mixer's blendshape list where VRCFury's BlendShape Links name a shape: on a linked skin, the base shape it
    /// follows; on the base skin, the linked skins' shapes it drives. The links are worked out as VRCFury documents them:
    /// the listed pairs (one name alone stands for both sides), then with "include all" every base shape not excluded
    /// whose name the linked skin has; names match exactly, or without "exact match" ignoring case and spaces when only
    /// one name fits. The base skin is the one on the avatar's object named as the link's base object (the shallowest).
    /// </summary>
    [InitializeOnLoad]
    internal static class VrcFuryBlendshapeUses
    {
        private const string Group = "VRCFury BlendShape Link";

        static VrcFuryBlendshapeUses() => MixerLink.BlendshapeUses += Uses;

        private sealed class Link
        {
            public Component Component;
            public SkinnedMeshRenderer Base;
            public List<SkinnedMeshRenderer> Skins;
            public bool IncludeAll, Exact;
            public HashSet<string> Excludes;
            public List<(string onBase, string onLinked)> Includes;
        }

        private static IEnumerable<MixerLink.BlendshapeUse> Uses(SkinnedMeshRenderer renderer, string shape)
        {
            var uses = new List<MixerLink.BlendshapeUse>();
            if (renderer == null || renderer.sharedMesh == null || string.IsNullOrEmpty(shape) || !VrcFury.Installed) return uses;
            var descriptor = renderer.GetComponentInParent<VRC.SDKBase.VRC_AvatarDescriptor>(true);
            var avatar = descriptor != null ? descriptor.gameObject : renderer.transform.root.gameObject;
            foreach (var (component, feature, kind) in VrcFury.Features(avatar))
            {
                if (kind != "BlendShapeLink") continue;
                var link = Read(avatar, component, feature);
                if (link.Base == null) continue;
                string where = "On " + component.gameObject.name;
                if (renderer == link.Base)
                {
                    var driven = new List<string>();
                    foreach (var skin in link.Skins)
                    {
                        if (skin == link.Base || skin.sharedMesh == null) continue;
                        foreach (var pair in Pairs(link, skin)) if (pair.from == shape) driven.Add(skin.name + " › " + pair.to);
                    }
                    if (driven.Count > 0)
                        uses.Add(new MixerLink.BlendshapeUse
                        {
                            Group = Group,
                            Title = "Drives " + (driven.Count == 1 ? driven[0] : driven.Count + " linked shapes"),
                            Detail = (driven.Count > 1 ? string.Join(", ", driven.Take(3)) + (driven.Count > 3 ? "…" : "") + " · " : "") + where,
                            Target = component,
                        });
                }
                if (link.Skins.Contains(renderer) && renderer != link.Base)
                {
                    foreach (var pair in Pairs(link, renderer))
                        if (pair.to == shape)
                            uses.Add(new MixerLink.BlendshapeUse { Group = Group, Title = "Follows " + pair.from + " of " + link.Base.name, Detail = where, Target = component });
                }
            }
            return uses;
        }

        private static Link Read(GameObject avatar, Component component, object feature)
        {
            string baseName = VrcFury.Field(feature, "baseObj") as string;
            return new Link
            {
                Component = component,
                Base = string.IsNullOrEmpty(baseName) ? null : avatar.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name == baseName)
                    .Select(t => t.GetComponent<SkinnedMeshRenderer>())
                    .Where(r => r != null)
                    // As VRCFury picks among same-named skins: the shortest path from the avatar (in characters).
                    .OrderBy(r => UnityEditor.AnimationUtility.CalculateTransformPath(r.transform, avatar.transform).Length)
                    .FirstOrDefault(),
                Skins = Items(feature, "linkSkins").Select(i => VrcFury.Field(i, "renderer") as SkinnedMeshRenderer).Where(r => r != null).ToList(),
                IncludeAll = VrcFury.Field(feature, "includeAll") is bool all && all,
                Exact = VrcFury.Field(feature, "exactMatch") is bool exact && exact,
                Excludes = new HashSet<string>(Items(feature, "excludes").Select(i => VrcFury.Field(i, "name") as string).Where(n => n != null), StringComparer.Ordinal),
                Includes = Items(feature, "includes").Select(i => (VrcFury.Field(i, "nameOnBase") as string, VrcFury.Field(i, "nameOnLinked") as string)).ToList(),
            };
        }

        // The base shape -> linked shape pairs of one linked skin.
        private static List<(string from, string to)> Pairs(Link link, SkinnedMeshRenderer skin)
        {
            var baseNames = Names(link.Base.sharedMesh);
            var skinNames = Names(skin.sharedMesh);
            var pairs = new List<(string, string)>();
            var linked = new HashSet<string>(StringComparer.Ordinal);
            // VRCFury keeps its links as a set: an include given twice links once.
            var made = new HashSet<(string, string)>();
            void Add(string from, string to, bool again)
            {
                from = Find(baseNames, from, link.Exact);
                if (from == null || (!again && linked.Contains(from))) return;
                to = Find(skinNames, to, link.Exact);
                if (to == null || !made.Add((from, to))) return;
                pairs.Add((from, to));
                linked.Add(from);
            }
            foreach (var (onBase, onLinked) in link.Includes)
            {
                bool noBase = string.IsNullOrWhiteSpace(onBase), noLinked = string.IsNullOrWhiteSpace(onLinked);
                if (noBase && noLinked) continue;
                Add(noBase ? onLinked : onBase, noLinked ? onBase : onLinked, true);
            }
            if (link.IncludeAll)
                foreach (string name in baseNames)
                    if (!link.Excludes.Contains(name)) Add(name, name, false);
            return pairs;
        }

        // The name of names that matches: the same one, else (loose matching) the only one equal ignoring case and spaces.
        private static string Find(List<string> names, string name, bool exact)
        {
            if (name == null) return null;
            if (names.Contains(name)) return name;
            if (exact) return null;
            string key = Squash(name), found = null;
            foreach (string candidate in names)
            {
                if (Squash(candidate) != key) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        private static string Squash(string name) => new string(name.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static IEnumerable<object> Items(object feature, string field) =>
            VrcFury.Field(feature, field) is IEnumerable list ? list.Cast<object>().Where(i => i != null) : Enumerable.Empty<object>();

        private static List<string> Names(Mesh mesh)
        {
            var names = new List<string>(mesh != null ? mesh.blendShapeCount : 0);
            if (mesh != null) for (int i = 0; i < mesh.blendShapeCount; i++) names.Add(mesh.GetBlendShapeName(i));
            return names;
        }
    }
}
