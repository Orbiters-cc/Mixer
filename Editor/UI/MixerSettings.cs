using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's card in the Orbiters settings window: whether the Mixer Hierarchy and the Mixer Project replace Unity's
    /// Hierarchy and Project windows, whether the Blender link listens, and Mixer's accent colour.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerSettings
    {
        static MixerSettings() => OrbitersSettingsWindow.RegisterSection("Mixer", Build);

        private static VisualElement Build()
        {
            var box = new VisualElement();
            box.Add(Feature("Replace Unity's Hierarchy",
                "The Mixer Hierarchy takes each Hierarchy window's place, like the Mixer console does with the Console: search by type and component, kind filters, prefab and missing-script badges, the Blender state of each object. Off, Unity's Hierarchy comes back in its place; the Mixer Hierarchy stays in the Window menu, and its menu opens Unity's beside it.",
                HierarchyReplacement.Enabled, on => HierarchyReplacement.SetEnabled(on), first: true));
            box.Add(Feature("Replace Unity's Project window",
                "The Mixer Project takes each Project window's place: folders that unfold in place, sorting by name or by the date things were added or changed, a \"new\" chip on what came in the last day, the assets used lately, a search inside animators, animations and VRChat components, and where each asset is used. A click on a folder no longer changes the selection. Off, Unity's Project window comes back in its place.",
                ProjectReplacement.Enabled, on => ProjectReplacement.SetEnabled(on), first: false));
            box.Add(Feature("Blender link",
                "Mixer listens for the Mixer extension of Blender and streams the scene both ways. Off, nothing listens and Open scene in Blender turns it on again.",
                MixerLinkService.Enabled, on => MixerLinkService.Enabled = on, first: false));
            box.Add(AccentChoice());
            return box;
        }

        // Mixer's accent colour: a swatch per choice, the chosen one ringed. The ring moves on press, then every Mixer
        // window takes the colour.
        private static VisualElement AccentChoice()
        {
            var row = new VisualElement();
            row.AddToClassList("orb-settings__feature");
            var texts = new VisualElement();
            texts.AddToClassList("orb-settings__feature-texts");
            row.Add(texts);
            var label = new Label("Accent colour");
            label.AddToClassList("orb-settings__feature-label");
            texts.Add(label);
            var detail = new Label("The colour of buttons, switches, selections and highlights in every Mixer window: Mixer, the Mixer Hierarchy and Project, the Mixer console and Mixer Git. Colours that tell a state (added, done, OK) stay green.");
            detail.AddToClassList("orb-settings__feature-description");
            texts.Add(detail);
            var swatches = new VisualElement();
            swatches.style.flexDirection = FlexDirection.Row;
            swatches.style.flexWrap = Wrap.Wrap;
            swatches.style.marginTop = 10;
            texts.Add(swatches);

            var all = new System.Collections.Generic.List<(MixerAccent.Choice choice, VisualElement swatch)>();
            void Ring(MixerAccent.Choice chosen)
            {
                foreach (var (choice, swatch) in all)
                {
                    var ring = choice == chosen ? new UnityEngine.Color(1f, 1f, 1f, 0.9f) : new UnityEngine.Color(0f, 0f, 0f, 0f);
                    swatch.style.borderTopColor = ring; swatch.style.borderRightColor = ring;
                    swatch.style.borderBottomColor = ring; swatch.style.borderLeftColor = ring;
                }
            }

            foreach (var choice in MixerAccent.Choices)
            {
                var swatch = new VisualElement { tooltip = choice.Name };
                swatch.style.width = 22; swatch.style.height = 22;
                swatch.style.marginRight = 8; swatch.style.marginBottom = 4;
                swatch.style.borderTopLeftRadius = 11; swatch.style.borderTopRightRadius = 11;
                swatch.style.borderBottomLeftRadius = 11; swatch.style.borderBottomRightRadius = 11;
                swatch.style.borderTopWidth = 2; swatch.style.borderRightWidth = 2;
                swatch.style.borderBottomWidth = 2; swatch.style.borderLeftWidth = 2;
                swatch.style.backgroundColor = choice.Color;
                var chosen = choice;
                swatch.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    evt.StopPropagation();
                    Ring(chosen);
                    swatch.schedule.Execute(() => MixerAccent.Set(chosen.Id));
                });
                all.Add((choice, swatch));
                swatches.Add(swatch);
            }

            Ring(MixerAccent.Current);
            return row;
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
