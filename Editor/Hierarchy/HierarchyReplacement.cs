using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// "Replace Unity's Hierarchy" (on by default, off in the Orbiters settings): the Mixer Hierarchy takes the place of
    /// Unity's Hierarchy window wherever one is docked (<see cref="DockedWindowReplacement"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyReplacement
    {
        internal const string PrefKey = "Orbiters.Mixer.ReplaceHierarchy";
        internal const bool DefaultOn = true;

        private static readonly DockedWindowReplacement Replacement = new DockedWindowReplacement(
            "UnityEditor.SceneHierarchyWindow", typeof(MixerHierarchyWindow), PrefKey, DefaultOn,
            "Orbiters.Mixer.HierarchyReplacement", "Window/General/Hierarchy", "Hierarchy", "Mixer Hierarchy");

        internal static bool Enabled => Replacement.Enabled;
        internal static bool CanDock => DockedWindowReplacement.CanDock;
        internal static void SetEnabled(bool on) => Replacement.SetEnabled(on);
        /// <summary>Opens Unity's own Hierarchy beside the Mixer one and leaves it there for this editor session.</summary>
        internal static void OpenUnityHierarchy() => Replacement.OpenUnityWindow();
        internal static void ReplaceOpenHierarchies() => Replacement.ReplaceOpen();
        internal static bool Replace(EditorWindow hierarchy, bool focus) => Replacement.Replace(hierarchy, focus);
    }
}
