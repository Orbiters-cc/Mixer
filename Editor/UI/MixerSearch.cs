using System;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Match case ("Aa") and regular expression (".*") switches at the end of a Mixer search field, as in the Logger's,
    /// before any other buttons the field holds (the Hierarchy's and the Project's Filter). The switches are kept per field
    /// in EditorPrefs. An invalid expression turns the field red, the reason in its tooltip, and searches nothing out; so
    /// does one found too slow while searching (<see cref="ShowState"/> after a search).
    /// </summary>
    internal sealed class MixerSearch
    {
        private const string PrefPrefix = "Orbiters.Mixer.Search.";
        private readonly TextField field;
        private readonly Button caseToggle, regexToggle;
        private readonly string prefKey, baseTooltip;
        private SearchPattern pattern = SearchPattern.Empty;

        /// <param name="key">The field's name in EditorPrefs ("Hierarchy", "Shapes"...).</param>
        /// <param name="changed">Called when a switch changes (the text's own changes are the field's).</param>
        /// <param name="after">Buttons the field holds after the switches.</param>
        public MixerSearch(TextField field, string key, Action changed, params VisualElement[] after)
        {
            this.field = field;
            prefKey = PrefPrefix + key;
            baseTooltip = field.tooltip;
            field.AddToClassList("mx-search");
            var options = MixerUi.Box("mx-search__options");
            caseToggle = Toggle("Aa", "Match case", "MatchCase", changed);
            regexToggle = Toggle(".*", "Regular expression (.NET syntax)", "Regex", changed);
            options.Add(caseToggle);
            options.Add(regexToggle);
            foreach (var element in after) if (element != null) options.Add(element);
            field.Add(options);
            // The text stops before the buttons, whatever their width (the Filter's count comes and goes).
            options.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var input = field.Q(TextField.textInputUssName);
                if (input != null && !float.IsNaN(evt.newRect.width)) input.style.paddingRight = evt.newRect.width + 8f;
            });
        }

        public bool MatchCase => caseToggle.ClassListContains("mx-search__toggle--on");
        public bool Regex => regexToggle.ClassListContains("mx-search__toggle--on");

        /// <summary>The field's search as it is now (built again only when the text or a switch changed).</summary>
        public SearchPattern Pattern => For(field.value);

        /// <summary>The search for <paramref name="text"/> with the field's switches; the field shows when it is invalid.</summary>
        public SearchPattern For(string text)
        {
            if (!pattern.SameAs(text, Regex, MatchCase))
            {
                pattern = new SearchPattern(text, Regex, MatchCase);
                ShowState();
            }
            return pattern;
        }

        /// <summary>Shows on the field whether its expression is invalid or was found too slow (after a search ran).</summary>
        public void ShowState()
        {
            bool wrong = pattern.Error != null || pattern.TooSlow;
            field.EnableInClassList("mx-search--error", wrong);
            field.tooltip = pattern.Error != null ? "Invalid regular expression: " + pattern.Error : pattern.TooSlow ? SearchPattern.TooSlowMessage : baseTooltip;
        }

        /// <summary>Sets the switches (a saved search, a label added from a menu), without calling back.</summary>
        public void Set(bool regex, bool matchCase)
        {
            regexToggle.EnableInClassList("mx-search__toggle--on", regex);
            caseToggle.EnableInClassList("mx-search__toggle--on", matchCase);
            EditorPrefs.SetBool(prefKey + ".Regex", regex);
            EditorPrefs.SetBool(prefKey + ".MatchCase", matchCase);
            For(field.value);
        }

        private Button Toggle(string text, string tooltip, string pref, Action changed)
        {
            var button = new Button { text = text, tooltip = tooltip };
            button.AddToClassList("mx-search__toggle");
            button.EnableInClassList("mx-search__toggle--on", EditorPrefs.GetBool(prefKey + "." + pref, false));
            MixerUi.Press(button, () =>
            {
                button.ToggleInClassList("mx-search__toggle--on");
                EditorPrefs.SetBool(prefKey + "." + pref, button.ClassListContains("mx-search__toggle--on"));
                For(field.value);
                changed?.Invoke();
            });
            // A press on a switch keeps the focus (and the caret) in the field's text.
            button.focusable = false;
            return button;
        }
    }
}
