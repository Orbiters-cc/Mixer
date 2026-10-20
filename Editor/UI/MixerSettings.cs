using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's card in the Orbiters settings window: whether the Mixer Hierarchy replaces Unity's Hierarchy, and whether
    /// the Blender link listens.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerSettings
    {
        static MixerSettings() => OrbitersSettingsWindow.RegisterSection("Mixer", Build);

        private static VisualElement Build()
        {
            var box = new VisualElement();
            box.Add(Feature("Replace Unity's Hierarchy",
                "The Mixer Hierarchy takes each Hierarchy window's place, like the Logger does with the Console: search by type and component, kind filters, prefab and missing-script badges, the Blender state of each object. Off, Unity's Hierarchy comes back in its place; the Mixer Hierarchy stays in the Window menu, and its menu opens Unity's beside it.",
                HierarchyReplacement.Enabled, on => HierarchyReplacement.SetEnabled(on), first: true));
            box.Add(Feature("Blender link",
                "Mixer listens for the Mixer extension of Blender and streams the scene both ways. Off, nothing listens and Open scene in Blender turns it on again.",
                MixerLinkService.Enabled, on => MixerLinkService.Enabled = on, first: false));
            return box;
        }

        private static VisualElement Feature(string title, string description, bool value, System.Action<bool> changed, bool first)
        {
            var row = new VisualElement();
            row.AddToClassList("orb-settings__feature");
            if (first) { row.AddToClassList("orb-settings__feature--first"); row.style.paddingTop = 0; }
            var texts = new VisualElement();
            texts.AddToClassList("orb-settings__feature-texts");
            row.Add(texts);
            var label = new Label(title);
            label.AddToClassList("orb-settings__feature-label");
            texts.Add(label);
            var detail = new Label(description);
            detail.AddToClassList("orb-settings__feature-description");
            texts.Add(detail);
            var toggle = new ToggleSwitch(value, changed);
            toggle.AddToClassList("orb-settings__feature-toggle");
            row.Add(toggle);
            return row;
        }
    }
}
