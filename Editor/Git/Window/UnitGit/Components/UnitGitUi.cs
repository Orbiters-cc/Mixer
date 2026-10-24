using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Mixer Git's building blocks, in the style of My Avatar and MCB: buttons that react on press (scale and highlight at
    /// once, act on press with Unity's click as the fallback), icon buttons, chips, a spinner and a three-state check box.
    /// </summary>
    internal static class UnitGitUi
    {
        internal const string PressedClass = "ug-pressed";

        /// <summary>Shows the press at once and runs <paramref name="action"/> on press (click as the fallback).</summary>
        internal static void Press(Button button, Action action)
        {
            button.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) button.AddToClassList(PressedClass); }, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList(PressedClass), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList(PressedClass));
            button.RegisterCallback<PointerCaptureOutEvent>(_ => button.RemoveFromClassList(PressedClass));
            if (action != null) ButtonInteraction.RegisterImmediateClick(button, action);
        }

        internal static Button Icon(UnitGitIconKind kind, string tooltip, Action action, string extraClass = null)
        {
            var button = new Button { tooltip = tooltip };
            button.AddToClassList("ug-icon-button");
            if (!string.IsNullOrEmpty(extraClass)) button.AddToClassList(extraClass);
            button.Add(new UnitGitIconElement(kind));
            Press(button, action);
            return button;
        }

        /// <summary>A rounded button: <c>primary</c> (green), <c>ghost</c> (quiet), <c>danger</c> or the default grey.</summary>
        internal static Button Pill(string text, Action action, string variant = null, UnitGitIconKind? icon = null, string tooltip = null)
        {
            var button = new Button { tooltip = tooltip };
            button.AddToClassList("ug-button");
            if (!string.IsNullOrEmpty(variant)) button.AddToClassList("ug-button--" + variant);
            if (icon.HasValue)
            {
                button.AddToClassList("ug-button--with-icon");
                button.Add(new UnitGitIconElement(icon.Value));
            }
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("ug-button__label");
            button.Add(label);
            Press(button, action);
            return button;
        }

        internal static void SetText(Button pill, string text)
        {
            var label = pill.Q<Label>(className: "ug-button__label");
            if (label != null) label.text = text;
        }

        internal static Label Chip(string text, string variant = null, string tooltip = null)
        {
            var chip = new Label(text) { tooltip = tooltip };
            chip.AddToClassList("ug-chip");
            if (!string.IsNullOrEmpty(variant)) chip.AddToClassList("ug-chip--" + variant);
            return chip;
        }

        internal static Label Text(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        internal static VisualElement Spacer()
        {
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList("ug-spacer");
            return spacer;
        }

        internal static VisualElement Separator()
        {
            var separator = new VisualElement { pickingMode = PickingMode.Ignore };
            separator.AddToClassList("ug-separator");
            return separator;
        }

        /// <summary>A search field with its magnifier, placeholder and a clear button once something is typed.</summary>
        internal static TextField Search(string value, string placeholder, Action<string> changed, string extraClass = null)
        {
            var field = new TextField { value = value ?? string.Empty };
            field.AddToClassList("ug-search");
            if (!string.IsNullOrEmpty(extraClass)) field.AddToClassList(extraClass);
            var input = field.Q(className: "unity-text-field__input") ?? field.Q(className: "unity-base-text-field__input");
            var icon = new UnitGitIconElement(UnitGitIconKind.Search);
            icon.AddToClassList("ug-search__icon");
            field.Add(icon);
            var hint = new Label(placeholder) { pickingMode = PickingMode.Ignore };
            hint.AddToClassList("ug-search__placeholder");
            field.Add(hint);
            var clear = Icon(UnitGitIconKind.Close, "Clear", () => field.value = string.Empty, "ug-search__clear");
            field.Add(clear);
            void Sync()
            {
                bool empty = string.IsNullOrEmpty(field.value);
                hint.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
                clear.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
            }
            field.RegisterValueChangedCallback(evt => { Sync(); changed?.Invoke(evt.newValue); });
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape || string.IsNullOrEmpty(field.value)) return;
                field.value = string.Empty;
                evt.StopPropagation();
            });
            Sync();
            return field;
        }

        internal static VisualElement Spinner()
        {
            var spinner = new VisualElement { pickingMode = PickingMode.Ignore };
            spinner.AddToClassList("ug-spinner");
            var arc = new VisualElement { pickingMode = PickingMode.Ignore };
            arc.AddToClassList("ug-spinner__arc");
            spinner.Add(arc);
            spinner.schedule.Execute(() =>
            {
                float angle = (float)(EditorApplication.timeSinceStartup * 400d % 360d);
                arc.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
            }).Every(16);
            return spinner;
        }

        /// <summary>A little count on a tab or header, hidden at zero.</summary>
        internal static Label Badge(int count, string variant = null)
        {
            var badge = new Label(count > 999 ? "999+" : count.ToString()) { pickingMode = PickingMode.Ignore };
            badge.AddToClassList("ug-badge");
            if (!string.IsNullOrEmpty(variant)) badge.AddToClassList("ug-badge--" + variant);
            badge.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            return badge;
        }

        /// <summary>Plays a class-driven entrance: the element starts with <paramref name="from"/>, which is removed a frame later.</summary>
        internal static void Enter(VisualElement element, string from = "ug-enter", long delay = 16)
        {
            if (element == null) return;
            element.AddToClassList(from);
            element.schedule.Execute(() => element.RemoveFromClassList(from)).StartingIn(delay);
        }
    }

    internal enum UnitGitCheckState { Off, On, Mixed }

    /// <summary>A drawn check box with a third, mixed state (a folder with some files included), toggled on press.</summary>
    internal sealed class UnitGitCheck : VisualElement
    {
        private readonly UnitGitIconElement mark = new UnitGitIconElement(UnitGitIconKind.Check);
        private UnitGitCheckState state;
        public event Action<bool> Toggled;

        public UnitGitCheck(UnitGitCheckState state)
        {
            AddToClassList("ug-check");
            mark.AddToClassList("ug-check__mark");
            Add(mark);
            State = state;
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                evt.StopPropagation();
                bool on = this.state != UnitGitCheckState.On;
                State = on ? UnitGitCheckState.On : UnitGitCheckState.Off;
                Toggled?.Invoke(on);
            });
            // Rows select on mouse down: the box keeps its press to itself.
            RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
        }

        public UnitGitCheckState State
        {
            get => state;
            set
            {
                state = value;
                EnableInClassList("ug-check--on", value == UnitGitCheckState.On);
                EnableInClassList("ug-check--mixed", value == UnitGitCheckState.Mixed);
                mark.Kind = value == UnitGitCheckState.Mixed ? UnitGitIconKind.Dash : UnitGitIconKind.Check;
            }
        }
    }

    /// <summary>
    /// Tabs in one rounded track with a highlight that slides to the chosen tab. Switching moves the highlight on press;
    /// the content follows.
    /// </summary>
    internal sealed class UnitGitTabs : VisualElement
    {
        private readonly VisualElement indicator = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly Dictionary<string, Button> tabs = new Dictionary<string, Button>();
        private string selected;
        private bool placed;

        public UnitGitTabs()
        {
            AddToClassList("ug-tabs");
            indicator.AddToClassList("ug-tabs__indicator");
            Add(indicator);
            RegisterCallback<GeometryChangedEvent>(_ => Place(animate: placed));
        }

        public Button Add(string key, string text, UnitGitIconKind icon, Action select, VisualElement trailing = null)
        {
            var tab = new Button { name = key };
            tab.AddToClassList("ug-tab");
            tab.Add(new UnitGitIconElement(icon));
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("ug-tab__label");
            tab.Add(label);
            if (trailing != null) tab.Add(trailing);
            UnitGitUi.Press(tab, () =>
            {
                Select(key);
                select?.Invoke();
            });
            tab.RegisterCallback<GeometryChangedEvent>(_ => { if (key == selected) Place(animate: placed); });
            tabs[key] = tab;
            Add(tab);
            return tab;
        }

        public void Select(string key)
        {
            selected = key;
            foreach (var pair in tabs) pair.Value.EnableInClassList("ug-tab--on", pair.Key == key);
            Place(animate: placed);
        }

        private void Place(bool animate)
        {
            if (selected == null || !tabs.TryGetValue(selected, out var tab)) { indicator.style.opacity = 0; return; }
            var box = tab.layout;
            if (float.IsNaN(box.width) || box.width <= 0) return;
            indicator.EnableInClassList("ug-tabs__indicator--instant", !animate);
            indicator.style.opacity = 1;
            indicator.style.left = box.x;
            indicator.style.top = box.y;
            indicator.style.width = box.width;
            indicator.style.height = box.height;
            placed = true;
        }
    }

    /// <summary>Short confirmations and errors, sliding in at the bottom right and fading away on their own.</summary>
    internal sealed class UnitGitToasts : VisualElement
    {
        public UnitGitToasts()
        {
            AddToClassList("ug-toasts");
            pickingMode = PickingMode.Ignore;
        }

        public void Show(string text, bool error = false, string actionText = null, Action action = null)
        {
            var toast = new VisualElement();
            toast.AddToClassList("ug-toast");
            if (error) toast.AddToClassList("ug-toast--error");
            var icon = new UnitGitIconElement(error ? UnitGitIconKind.Conflict : UnitGitIconKind.Check);
            icon.AddToClassList("ug-toast__icon");
            toast.Add(icon);
            var label = new Label(text);
            label.AddToClassList("ug-toast__text");
            toast.Add(label);
            if (actionText != null && action != null)
                toast.Add(UnitGitUi.Pill(actionText, () => { action(); Dismiss(toast); }, "ghost"));
            toast.Add(UnitGitUi.Icon(UnitGitIconKind.Close, "Dismiss", () => Dismiss(toast), "ug-toast__close"));
            while (childCount >= 3) RemoveAt(0);
            Add(toast);
            UnitGitUi.Enter(toast, "ug-toast--enter", 20);
            toast.schedule.Execute(() => Dismiss(toast)).StartingIn(error ? 9000 : 3800);
        }

        private static void Dismiss(VisualElement toast)
        {
            if (toast.parent == null || toast.ClassListContains("ug-toast--leave")) return;
            toast.AddToClassList("ug-toast--leave");
            toast.schedule.Execute(() => toast.RemoveFromHierarchy()).StartingIn(260);
        }
    }
}
