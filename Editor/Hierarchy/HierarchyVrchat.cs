using System;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>What the Mixer Hierarchy shows of a VRChat avatar beyond its name.</summary>
    internal sealed class VrchatAvatarFacts
    {
        /// <summary>The avatar's blueprint ID (its upload on VRChat), or null before the first upload.</summary>
        public string BlueprintId;
        /// <summary>The avatar's name on VRChat, or null while unknown.</summary>
        public string UploadedName;
        /// <summary>Why there is no name: not uploaded yet, signed out of the VRChat SDK, not found...</summary>
        public string Note;
        /// <summary>"Public" or "Private", or null while unknown.</summary>
        public string Visibility;
        /// <summary>The VRChat SDK's performance rank of the avatar as it is in the scene ("Good"), null while estimating.</summary>
        public string Rank;
        public Texture RankIcon;
        /// <summary>The platform the rank is for ("Windows", "Android").</summary>
        public string RankPlatform;

        public string Link => string.IsNullOrEmpty(BlueprintId) ? null : "https://vrchat.com/home/avatar/" + BlueprintId;
    }

    /// <summary>
    /// The VRChat part of Mixer (Orbiters.Mixer.Editor.VRChat, built with the VRChat SDK) describes avatars for the
    /// Hierarchy: their upload on VRChat and their performance rank. Answers that take time (VRChat's API, the rank)
    /// come later and raise <see cref="Changed"/>.
    /// </summary>
    internal static class HierarchyVrchat
    {
        public static Func<GameObject, VrchatAvatarFacts> Describe;
        public static event Action Changed;

        public static void NotifyChanged() => Changed?.Invoke();
    }
}
