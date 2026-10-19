using System;
using System.Globalization;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>Mixer's small UI vocabulary over UI Toolkit: presses that answer at once, pills, icons, cards, toasts.</summary>
    internal static class MixerUi
    {
        internal const string PressedClass = "mx-pressed";
        private const string PressableClass = "mx-pressable";
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        /// <summary>Shows the press at once and runs <paramref name="action"/> on press; a click still works (keyboard, assistive input).</summary>
        internal static void Press(VisualElement element, Action action)
        {
            bool handledByPress = false;
            long lastPress = 0;
            element.AddToClassList(PressableClass);
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !element.enabledInHierarchy || OnInnerButton(element, evt.target as VisualElement)) return;
                element.AddToClassList(PressedClass);
                long now = DateTime.UtcNow.Ticks;
                if (now - lastPress < TimeSpan.TicksPerMillisecond * 25) return;
                lastPress = now;
                handledByPress = true;
                element.schedule.Execute(() => handledByPress = false).StartingIn(800);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                action?.Invoke();
            }, TrickleDown.TrickleDown);
            element.RegisterCallback<PointerUpEvent>(_ => element.RemoveFromClassList(PressedClass));
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList(PressedClass));
            element.RegisterCallback<PointerCaptureOutEvent>(_ => element.RemoveFromClassList(PressedClass));
            if (element is Button button)
            {
                button.clicked += () =>
                {
                    if (handledByPress) { handledByPress = false; return; }
                    action?.Invoke();
                };
            }
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (element is Button) return;
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Space) { action?.Invoke(); evt.StopPropagation(); }
            });
        }

        private static bool OnInnerButton(VisualElement element, VisualElement target)
        {
            for (var current = target; current != null && current != element; current = current.parent)
                if (current is Button || current.ClassListContains(PressableClass)) return true;
            return false;
        }

        internal static Button Icon(IconGlyph glyph, string tooltip, Action action, string extraClass = null) => Icon(glyph, tooltip, action, null, extraClass);

        /// <summary>
        /// An icon button. <paramref name="onPointerDown"/> sees the press with its modifiers before the press handling
        /// (which stops the event for later handlers): for buttons whose action depends on Alt or Shift.
        /// </summary>
        internal static Button Icon(IconGlyph glyph, string tooltip, Action action, Action<PointerDownEvent> onPointerDown, string extraClass = null)
        {
            var button = new Button { tooltip = tooltip, text = string.Empty };
            button.AddToClassList("mx-icon-button");
            if (extraClass != null) button.AddToClassList(extraClass);
            var icon = new VectorIcon(glyph) { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mx-icon");
            button.Add(icon);
            if (onPointerDown != null) button.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) onPointerDown(evt); }, TrickleDown.TrickleDown);
            Press(button, action);
            return button;
        }

        /// <summary>A rounded button; variants: primary, ghost, danger, accent.</summary>
        internal static Button Pill(string text, Action action, string variant = null, IconGlyph? glyph = null, string tooltip = null)
        {
            var button = new Button { text = string.Empty, tooltip = tooltip };
            button.AddToClassList("mx-button");
            if (!string.IsNullOrEmpty(variant)) button.AddToClassList("mx-button--" + variant);
            if (glyph.HasValue)
            {
                var icon = new VectorIcon(glyph.Value) { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("mx-icon");
                button.Add(icon);
                button.AddToClassList("mx-button--with-icon");
            }
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("mx-button__label");
            button.Add(label);
            Press(button, action);
            return button;
        }

        internal static void SetPillText(Button pill, string text)
        {
            var label = pill.Q<Label>(className: "mx-button__label");
            if (label != null) label.text = text;
        }

        internal static Label Text(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            if (!string.IsNullOrEmpty(className)) foreach (var c in className.Split(' ')) label.AddToClassList(c);
            return label;
        }

        internal static VisualElement Box(string className, PickingMode picking = PickingMode.Position)
        {
            var box = new VisualElement { pickingMode = picking };
            if (!string.IsNullOrEmpty(className)) foreach (var c in className.Split(' ')) box.AddToClassList(c);
            return box;
        }

        internal static VisualElement Row(string className = null)
        {
            var row = Box(className);
            row.AddToClassList("mx-row");
            return row;
        }

        internal static VisualElement Spacer()
        {
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            return spacer;
        }

        /// <summary>A card with a small uppercase title.</summary>
        internal static VisualElement Card(string title, out VisualElement body, string extraClass = null)
        {
            var card = Box("mx-card");
            if (extraClass != null) card.AddToClassList(extraClass);
            if (!string.IsNullOrEmpty(title)) card.Add(Text(title, "mx-card__title"));
            body = Box("mx-card__body");
            card.Add(body);
            return card;
        }

        /// <summary>A setting row: title and detail on the left, a control on the right.</summary>
        internal static VisualElement Setting(string title, string detail, VisualElement control, bool first = false)
        {
            var row = Box("mx-setting");
            if (first) row.AddToClassList("mx-setting--first");
            var texts = Box("mx-setting__texts");
            texts.Add(Text(title, "mx-setting__title"));
            if (!string.IsNullOrEmpty(detail)) texts.Add(Text(detail, "mx-setting__detail"));
            row.Add(texts);
            if (control != null) { control.AddToClassList("mx-setting__control"); row.Add(control); }
            return row;
        }

        /// <summary>A chip (small rounded label), optionally removable.</summary>
        internal static VisualElement Chip(string text, Action onRemove = null, bool accent = false, string tooltip = null)
        {
            var chip = Box("mx-chip");
            if (accent) chip.AddToClassList("mx-chip--accent");
            chip.tooltip = tooltip;
            chip.Add(Text(text, "mx-chip__text"));
            if (onRemove != null) chip.Add(Icon(IconGlyph.Close, "Remove", onRemove, "mx-chip__remove"));
            return chip;
        }

        /// <summary>Class-driven entrance: added with <paramref name="from"/>, which is removed a frame later so transitions run.</summary>
        internal static void Enter(VisualElement element, string from, long delay = 16)
        {
            element.AddToClassList(from);
            element.schedule.Execute(() => element.RemoveFromClassList(from)).StartingIn(delay);
        }

        internal static void Show(VisualElement element, bool visible) => element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        internal static string Count(long value) => value.ToString("N0", Culture);

        internal static string Metres(float metres) =>
            metres >= 1f ? metres.ToString("0.00", Culture) + " m" : metres >= 0.01f ? (metres * 100f).ToString("0.0", Culture) + " cm" : (metres * 1000f).ToString("0.0", Culture) + " mm";

        internal static string Percent(float fraction) => Mathf.RoundToInt(fraction * 100f) + " %";

        internal static string Bytes(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", Culture) + " GB" :
            bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0", Culture) + " MB" :
            bytes >= 1L << 10 ? (bytes / (double)(1L << 10)).ToString("0", Culture) + " KB" : bytes + " B";
    }

    /// <summary>Short confirmations that slide in at the bottom right and leave on their own.</summary>
    internal sealed class MixerToasts : VisualElement
    {
        public MixerToasts()
        {
            AddToClassList("mx-toasts");
            pickingMode = PickingMode.Ignore;
        }

        public void Show(string text, bool error = false)
        {
            if (childCount > 0 && this[childCount - 1] is VisualElement newest && newest.userData is Shown shown && shown.Text == text && shown.Error == error && !newest.ClassListContains("mx-toast--leave"))
            {
                shown.Dismissal.ExecuteLater(error ? 7000 : 2600);
                return;
            }
            var toast = new VisualElement();
            toast.AddToClassList("mx-toast");
            if (error) toast.AddToClassList("mx-toast--error");
            var icon = new VectorIcon(error ? IconGlyph.Close : IconGlyph.Check);
            icon.AddToClassList("mx-toast__icon");
            toast.Add(icon);
            toast.Add(MixerUi.Text(text, "mx-toast__text"));
            toast.Add(MixerUi.Icon(IconGlyph.Close, "Dismiss", () => Dismiss(toast), "mx-toast__close"));
            while (childCount >= 3) RemoveAt(0);
            Add(toast);
            MixerUi.Enter(toast, "mx-toast--enter", 20);
            var dismissal = toast.schedule.Execute(() => Dismiss(toast)).StartingIn(error ? 7000 : 2600);
            toast.userData = new Shown { Text = text, Error = error, Dismissal = dismissal };
        }

        private sealed class Shown { public string Text; public bool Error; public IVisualElementScheduledItem Dismissal; }

        private static void Dismiss(VisualElement toast)
        {
            if (toast.parent == null || toast.ClassListContains("mx-toast--leave")) return;
            toast.AddToClassList("mx-toast--leave");
            toast.schedule.Execute(() => toast.RemoveFromHierarchy()).StartingIn(240);
        }
    }
}
