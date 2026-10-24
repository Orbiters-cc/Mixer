using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Mixer's accent colour: green unless another is chosen in Orbiters settings › Mixer, for every Mixer window (Mixer,
    /// the Mixer Hierarchy and Project, the Mixer console, Mixer Git, captures and performances), its overlays and panels.
    /// Their styles read it from the variables of accent.uss (<c>--mixer-accent</c>, its shades and tints), which
    /// <see cref="Apply"/> sets on each root; what they draw themselves reads <see cref="Color"/>, <see cref="Text"/> and
    /// <see cref="With"/>. Status colours (added, done, OK) stay green whatever the accent.
    /// </summary>
    public static class MixerAccent
    {
        public sealed class Choice
        {
            public readonly string Id;
            public readonly string Name;
            /// <summary>Fills: buttons, switches, progress.</summary>
            public readonly Color Color;
            /// <summary>Text and icons in the accent on Mixer's dark surfaces.</summary>
            public readonly Color Text;

            internal Choice(string id, string name, string color, string text)
            {
                Id = id;
                Name = name;
                ColorUtility.TryParseHtmlString(color, out Color);
                ColorUtility.TryParseHtmlString(text, out Text);
            }
        }

        // The same choices as accent.uss (written by the accent script with their shades and tints).
        public static readonly IReadOnlyList<Choice> Choices = new[]
        {
            new Choice("green", "Green", "#00da6d", "#3cf29a"),
            new Choice("cyan", "Cyan", "#00c2d9", "#3ddcf0"),
            new Choice("blue", "Blue", "#3b8bff", "#6aaaff"),
            new Choice("violet", "Violet", "#8f6bff", "#ab93ff"),
            new Choice("pink", "Pink", "#ff5fa2", "#ff86bb"),
            new Choice("orange", "Orange", "#ff8a33", "#ffa25c"),
            new Choice("yellow", "Yellow", "#f5c400", "#ffd84a"),
            new Choice("silver", "Silver", "#d8d8d8", "#f0f0f0"),
        };

        private const string Pref = "Orbiters.Mixer.Accent";
        private const string SheetPath = "Packages/orbiters.mixer/Editor/Brand/accent.uss";
        private const string RootClass = "mixer-accent";
        private static readonly List<WeakReference<VisualElement>> Roots = new List<WeakReference<VisualElement>>();
        private static StyleSheet sheet;
        private static Choice current;

        /// <summary>After the accent changed: the roots given to <see cref="Apply"/> show it already.</summary>
        public static event Action Changed;

        public static Choice Current => current ?? (current = Find(EditorPrefs.GetString(Pref, Choices[0].Id)));
        public static Color Color => Current.Color;
        public static Color Text => Current.Text;

        /// <summary>The accent with <paramref name="alpha"/>, for tints drawn in code.</summary>
        public static Color With(float alpha)
        {
            var color = Color;
            color.a = alpha;
            return color;
        }

        /// <summary>Gives <paramref name="root"/> and everything below it the accent, now and when it changes.</summary>
        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            if (sheet == null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(SheetPath);
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            if (!root.ClassListContains(RootClass))
            {
                root.AddToClassList(RootClass);
                Roots.RemoveAll(r => !r.TryGetTarget(out _));
                Roots.Add(new WeakReference<VisualElement>(root));
            }

            Mark(root);
        }

        public static void Set(string id)
        {
            var choice = Find(id);
            if (choice == Current) return;
            current = choice;
            EditorPrefs.SetString(Pref, choice.Id);
            Roots.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (var reference in Roots)
            {
                if (!reference.TryGetTarget(out var root)) continue;
                Mark(root);
                // What draws itself (charts, graphs) reads the colour as it repaints.
                root.Query<VisualElement>().ForEach(element => element.MarkDirtyRepaint());
            }

            Changed?.Invoke();
            InternalEditorUtility.RepaintAllViews();
        }

        private static void Mark(VisualElement root)
        {
            foreach (var choice in Choices) root.EnableInClassList(RootClass + "--" + choice.Id, choice == Current && choice != Choices[0]);
        }

        private static Choice Find(string id)
        {
            foreach (var choice in Choices)
                if (choice.Id == id) return choice;
            return Choices[0];
        }
    }
}
