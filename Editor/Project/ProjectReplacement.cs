using UnityEditor;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// "Replace Unity's Project window" (on by default, off in the Orbiters settings): the Mixer Project takes the place
    /// of Unity's Project window wherever one is docked (<see cref="DockedWindowReplacement"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class ProjectReplacement
    {
        internal const string PrefKey = "Orbiters.Mixer.ReplaceProject";
        internal const bool DefaultOn = true;

        private static readonly DockedWindowReplacement Replacement = new DockedWindowReplacement(
            "UnityEditor.ProjectBrowser", typeof(MixerProjectWindow), PrefKey, DefaultOn,
            "Orbiters.Mixer.ProjectReplacement", "Window/General/Project", "Project window", "Mixer Project",
            "Orbiters.Logger.Editor.LoggerWindow", "UnityEditor.ConsoleWindow", "UnityEditor.GameView", "UnityEditor.SceneView");

        internal static bool Enabled => Replacement.Enabled;
        internal static void SetEnabled(bool on) => Replacement.SetEnabled(on);
        /// <summary>Opens Unity's own Project window beside the Mixer one and leaves it there for this editor session.</summary>
        internal static void OpenUnityProject() => Replacement.OpenUnityWindow();
    }
}
