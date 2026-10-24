using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// The Mixer console's building blocks, in the style of Mixer Git and My Avatar: buttons that answer on press (they scale and
    /// act at once, Unity's click stays as the fallback), icon buttons, pills, chips, a spinner, toasts and a segmented
    /// control with a sliding highlight. The Mixer console has no package dependency so it keeps working when other packages
    /// don't compile; these mirror Orbiters Toolkit's <c>ButtonInteraction</c> for that reason.
    /// </summary>
    internal static class LoggerUi
    {
        internal const string PressedClass = "lg-pressed";
        private const string PressableClass = "lg-pressable";

        /// <summary>Shows the press at once and runs <paramref name="action"/> on press; a click still works (keyboard, assistive input).</summary>
        internal static void Press(VisualElement element, Action action)
        {
            bool handledByPress = false;
            long lastPress = 0;
            element.AddToClassList(PressableClass);
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !element.enabledInHierarchy || OnInnerButton(element, evt.target as VisualElement))
                {
                    return;
                }

                element.AddToClassList(PressedClass);
                if (IsRepeatPress(ref lastPress))
                {
                    return;
                }

                handledByPress = true;
                element.schedule.Execute(() => handledByPress = false).StartingIn(800);
                // Keep Unity's Clickable from capturing the pointer: a menu or popup opened here takes the pointer-up,
                // the capture would stay, and every later click would land on this button again.
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
                    if (handledByPress)
                    {
                        handledByPress = false;
                        return;
                    }

                    action?.Invoke();
                };
            }

            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (element is Button)
                {
                    return;
                }

                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Space)
                {
                    action?.Invoke();
                    evt.StopPropagation();
                }
            });
        }

        /// <summary>
        /// True for a pointer-down arriving within 25 ms of the last one <paramref name="lastPress"/> recorded: the same
        /// press delivered again, which must not act twice. Otherwise records it and returns false.
        /// </summary>
        internal static bool IsRepeatPress(ref long lastPress)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - lastPress < TimeSpan.TicksPerMillisecond * 25)
            {
                return true;
            }

            lastPress = now;
            return false;
        }

        // A button inside a pressable one (a source row's "Only") answers its own press: the outer handler runs first,
        // in the trickle-down phase, and stopping the event there would leave the inner one nothing.
        private static bool OnInnerButton(VisualElement element, VisualElement target)
        {
            for (var current = target; current != null && current != element; current = current.parent)
            {
                if (current is Button || current.ClassListContains(PressableClass))
                {
                    return true;
                }
            }

            return false;
        }

        internal static Button Icon(LoggerGlyph glyph, string tooltip, Action action, string extraClass = null)
        {
            var button = new Button { tooltip = tooltip };
            button.AddToClassList("lg-icon-button");
            if (!string.IsNullOrEmpty(extraClass))
            {
                button.AddToClassList(extraClass);
            }

            button.Add(new LoggerIcon(glyph));
            Press(button, action);
            return button;
        }

        /// <summary>A rounded button: <c>primary</c> (green), <c>ghost</c> (quiet), <c>danger</c> or the default grey.</summary>
        internal static Button Pill(string text, Action action, string variant = null, LoggerGlyph? glyph = null, string tooltip = null)
        {
            var button = new Button { tooltip = tooltip };
            button.AddToClassList("lg-button");
            if (!string.IsNullOrEmpty(variant))
            {
                button.AddToClassList("lg-button--" + variant);
            }

            if (glyph.HasValue)
            {
                button.AddToClassList("lg-button--with-icon");
                button.Add(new LoggerIcon(glyph.Value));
            }

            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("lg-button__label");
            button.Add(label);
            Press(button, action);
            return button;
        }

        internal static Label Text(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        /// <summary>A label for text from elsewhere (an AI answer, a log): tags in it show as written, never as rich text.</summary>
        internal static Label Plain(string text, string className)
        {
            var label = Text(text, className);
            label.enableRichText = false;
            return label;
        }

        internal static VisualElement Box(string className, PickingMode picking = PickingMode.Position)
        {
            var element = new VisualElement { pickingMode = picking };
            element.AddToClassList(className);
            return element;
        }

        internal static VisualElement Spacer()
        {
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList("lg-spacer");
            return spacer;
        }

        internal static VisualElement Separator()
        {
            var separator = new VisualElement { pickingMode = PickingMode.Ignore };
            separator.AddToClassList("lg-separator");
            return separator;
        }

        internal static VisualElement Spinner()
        {
            var spinner = new VisualElement { pickingMode = PickingMode.Ignore };
            spinner.AddToClassList("lg-spinner");
            var arc = new VisualElement { pickingMode = PickingMode.Ignore };
            arc.AddToClassList("lg-spinner__arc");
            spinner.Add(arc);
            spinner.schedule.Execute(() =>
            {
                if (spinner.resolvedStyle.display == DisplayStyle.None || spinner.panel == null)
                {
                    return;
                }

                float angle = (float)(EditorApplication.timeSinceStartup * 420d % 360d);
                arc.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
            }).Every(16);
            return spinner;
        }

        /// <summary>Plays a class-driven entrance: the element starts with <paramref name="from"/>, removed a frame later.</summary>
        internal static void Enter(VisualElement element, string from, long delay = 16)
        {
            if (element == null)
            {
                return;
            }

            element.AddToClassList(from);
            element.schedule.Execute(() => element.RemoveFromClassList(from)).StartingIn(delay);
        }

        internal static void Show(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // ---- Formatting -------------------------------------------------------------------------------------------

        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        /// <summary>1234 → "1,234".</summary>
        internal static string Count(long value) => value.ToString("N0", Culture);

        /// <summary>1234 → "1.2K", 1834438 → "1.8M": for tight places (pills, badges).</summary>
        internal static string Compact(long value)
        {
            if (value < 10_000)
            {
                return value.ToString("N0", Culture);
            }

            if (value < 1_000_000)
            {
                return (value / 1000d).ToString(value < 100_000 ? "0.#" : "0", Culture) + "K";
            }

            return (value / 1_000_000d).ToString(value < 100_000_000 ? "0.#" : "0", Culture) + "M";
        }

        internal static DateTime Local(long utcTicks) => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();

        internal static string Time(long utcTicks, bool milliseconds)
        {
            if (utcTicks <= 0)
            {
                return "--:--:--";
            }

            return Local(utcTicks).ToString(milliseconds ? "HH:mm:ss.fff" : "HH:mm:ss", Culture);
        }

        /// <summary>A date and time, the date only when it isn't today.</summary>
        internal static string Moment(long utcTicks)
        {
            var local = Local(utcTicks);
            return local.Date == DateTime.Today ? local.ToString("HH:mm:ss", Culture) : local.ToString("MMM d, HH:mm:ss", Culture);
        }

        internal static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 1)
            {
                return Math.Max(0, (int)span.TotalMilliseconds) + " ms";
            }

            if (span.TotalMinutes < 1)
            {
                return span.TotalSeconds.ToString(span.TotalSeconds < 10 ? "0.#" : "0", Culture) + " s";
            }

            if (span.TotalHours < 1)
            {
                return (int)span.TotalMinutes + " min " + (span.Seconds > 0 ? span.Seconds + " s" : string.Empty);
            }

            if (span.TotalDays < 1)
            {
                return (int)span.TotalHours + " h " + (span.Minutes > 0 ? span.Minutes + " min" : string.Empty);
            }

            return (int)span.TotalDays + " d " + span.Hours + " h";
        }

        internal static string Ago(long utcTicks)
        {
            var span = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - utcTicks));
            if (span.TotalSeconds < 2)
            {
                return "just now";
            }

            return Duration(span).Trim() + " ago";
        }

        internal static string Plural(long count, string singular, string plural = null) =>
            Count(count) + " " + (count == 1 ? singular : plural ?? singular + "s");

        internal static string ColorHex(Color color) => "#" + ColorUtility.ToHtmlStringRGB(color);
    }

    /// <summary>
    /// A chart's hover read-out, drawn above everything else of the window: it lives in the window's root (after every
    /// pane), not in the chart, so rows or cards laid out after the chart can't cover it.
    /// </summary>
    internal sealed class FloatingTip
    {
        private readonly VisualElement owner;
        private readonly Label label;

        public FloatingTip(VisualElement owner)
        {
            this.owner = owner;
            label = LoggerUi.Text(string.Empty, "lg-chart-tip");
            label.AddToClassList("lg-floating-tip");
            label.pickingMode = PickingMode.Ignore;
            label.enableRichText = true;
            label.style.display = DisplayStyle.None;
            label.RegisterCallback<GeometryChangedEvent>(OnLaidOut);
            owner.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                label.style.display = DisplayStyle.None;
                label.RemoveFromHierarchy();
            });
        }

        /// <summary>
        /// Shows <paramref name="text"/> (rich text) at a point of the owner, in its local coordinates: to the right of
        /// it, or to its left in the right part of the window.
        /// </summary>
        public void Show(string text, float localX, float localY)
        {
            var host = Host();
            if (host == null)
            {
                return;
            }

            if (label.parent != host)
            {
                host.Add(label);
            }
            else if (host.IndexOf(label) != host.childCount - 1)
            {
                label.BringToFront();
            }

            label.text = text;
            label.style.display = DisplayStyle.Flex;
            anchor = host.WorldToLocal(owner.LocalToWorld(new Vector2(localX, localY)));
            label.style.top = anchor.y;
            Place(host, label.layout.width);
        }

        public void Hide() => label.style.display = DisplayStyle.None;

        private Vector2 anchor;

        // Right of the point when it fits in the window, else left of it; checked again once the text is laid out.
        private void Place(VisualElement host, float tipWidth)
        {
            float width = float.IsNaN(host.layout.width) ? 0f : host.layout.width;
            float measured = float.IsNaN(tipWidth) || tipWidth <= 0f ? 220f : tipWidth;
            bool left = anchor.x + 10f + measured > width - 4f && anchor.x - 10f - measured >= 4f;
            label.style.left = left ? StyleKeyword.Auto : new StyleLength(Mathf.Clamp(anchor.x + 10f, 4f, Mathf.Max(4f, width - measured - 4f)));
            label.style.right = left ? new StyleLength(Mathf.Max(4f, width - anchor.x + 10f)) : StyleKeyword.Auto;
        }

        private void OnLaidOut(GeometryChangedEvent evt)
        {
            var host = label.parent;
            if (host != null && label.resolvedStyle.display == DisplayStyle.Flex && Mathf.Abs(evt.oldRect.width - evt.newRect.width) > 0.5f)
            {
                Place(host, evt.newRect.width);
            }
        }

        private VisualElement Host()
        {
            for (var element = owner; element != null; element = element.parent)
            {
                if (element.ClassListContains("lg-root"))
                {
                    return element;
                }
            }

            return owner.panel?.visualTree;
        }
    }

    /// <summary>Two or three options in one rounded track; a highlight slides to the chosen one on press.</summary>
    internal sealed class SegmentedControl : VisualElement
    {
        private readonly VisualElement indicator = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly List<Button> options = new List<Button>();
        private readonly Action<int> changed;
        private int selected = -1;
        private bool placed;

        public SegmentedControl(Action<int> changed)
        {
            this.changed = changed;
            AddToClassList("lg-segmented");
            indicator.AddToClassList("lg-segmented__indicator");
            Add(indicator);
            RegisterCallback<GeometryChangedEvent>(_ => Place(placed));
        }

        public int Selected => selected;

        public Button AddOption(string text, LoggerGlyph glyph, string tooltip)
        {
            int index = options.Count;
            var button = new Button { tooltip = tooltip };
            button.AddToClassList("lg-segmented__option");
            button.Add(new LoggerIcon(glyph));
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("lg-segmented__label");
            button.Add(label);
            LoggerUi.Press(button, () =>
            {
                if (selected == index)
                {
                    return;
                }

                Select(index);
                changed?.Invoke(index);
            });
            button.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (index == selected)
                {
                    Place(placed);
                }
            });
            options.Add(button);
            Add(button);
            return button;
        }

        public void Select(int index)
        {
            selected = index;
            for (int i = 0; i < options.Count; i++)
            {
                options[i].EnableInClassList("lg-segmented__option--on", i == index);
            }

            Place(placed);
        }

        private void Place(bool animate)
        {
            if (selected < 0 || selected >= options.Count)
            {
                indicator.style.opacity = 0;
                return;
            }

            var box = options[selected].layout;
            if (float.IsNaN(box.width) || box.width <= 0)
            {
                return;
            }

            indicator.EnableInClassList("lg-segmented__indicator--instant", !animate);
            indicator.style.opacity = 1;
            indicator.style.left = box.x;
            indicator.style.top = box.y;
            indicator.style.width = box.width;
            indicator.style.height = box.height;
            placed = true;
        }
    }

    /// <summary>Short confirmations and errors that slide in at the bottom right and fade away on their own.</summary>
    internal sealed class Toasts : VisualElement
    {
        public Toasts()
        {
            AddToClassList("lg-toasts");
            pickingMode = PickingMode.Ignore;
        }

        public void Show(string text, bool error = false)
        {
            // The same toast again while it still shows (a click repeated on what failed): it stays longer, no copy stacks.
            if (childCount > 0 && this[childCount - 1] is VisualElement newest && newest.userData is ShownToast shown &&
                shown.Text == text && shown.Error == error && !newest.ClassListContains("lg-toast--leave"))
            {
                shown.Dismissal.ExecuteLater(error ? 7000 : 2600);
                return;
            }

            var toast = new VisualElement();
            toast.AddToClassList("lg-toast");
            if (error)
            {
                toast.AddToClassList("lg-toast--error");
            }

            var icon = new LoggerIcon(error ? LoggerGlyph.Warning : LoggerGlyph.Check);
            icon.AddToClassList("lg-toast__icon");
            toast.Add(icon);
            toast.Add(LoggerUi.Text(text, "lg-toast__text"));
            toast.Add(LoggerUi.Icon(LoggerGlyph.Close, "Dismiss", () => Dismiss(toast), "lg-toast__close"));
            while (childCount >= 3)
            {
                RemoveAt(0);
            }

            Add(toast);
            LoggerUi.Enter(toast, "lg-toast--enter", 20);
            var dismissal = toast.schedule.Execute(() => Dismiss(toast)).StartingIn(error ? 7000 : 2600);
            toast.userData = new ShownToast { Text = text, Error = error, Dismissal = dismissal };
        }

        private sealed class ShownToast
        {
            public string Text;
            public bool Error;
            public IVisualElementScheduledItem Dismissal;
        }

        private static void Dismiss(VisualElement toast)
        {
            if (toast.parent == null || toast.ClassListContains("lg-toast--leave"))
            {
                return;
            }

            toast.AddToClassList("lg-toast--leave");
            toast.schedule.Execute(() => toast.RemoveFromHierarchy()).StartingIn(240);
        }
    }

    /// <summary>
    /// The search field: a magnifier, a placeholder, toggles for match case, regular expression and stack traces, and a
    /// clear button. An invalid regular expression turns the field red with the reason in its tooltip.
    /// </summary>
    internal sealed class SearchBox : VisualElement
    {
        private readonly TextField field;
        private readonly Label placeholder;
        private readonly Button clear;
        private readonly Button caseToggle;
        private readonly Button regexToggle;
        private readonly Button stackToggle;

        public event Action<string> TextChanged;
        public event Action OptionsChanged;
        public event Action Submitted;
        public event Action Escaped;
        public event Action DownPressed;

        public SearchBox(string value, bool matchCase, bool regex, bool stack)
        {
            AddToClassList("lg-search");
            var icon = new LoggerIcon(LoggerGlyph.Search);
            icon.AddToClassList("lg-search__icon");
            field = new TextField { value = value ?? string.Empty };
            field.AddToClassList("lg-search__field");
            Add(field);
            Add(icon);
            placeholder = new Label("Search logs") { pickingMode = PickingMode.Ignore };
            placeholder.AddToClassList("lg-search__placeholder");
            var hint = new Label("Ctrl+F") { pickingMode = PickingMode.Ignore };
            hint.AddToClassList("lg-search__hint");
            placeholder.Add(hint);
            Add(placeholder);
            // The shortcut only where the placeholder and it fit before the switches (a wide top bar leaves the field narrow).
            RegisterCallback<GeometryChangedEvent>(evt => hint.style.display = evt.newRect.width >= 236f ? DisplayStyle.Flex : DisplayStyle.None);

            var options = LoggerUi.Box("lg-search__options");
            caseToggle = Toggle("Aa", "Match case", matchCase);
            regexToggle = Toggle(".*", "Regular expression (.NET syntax)", regex);
            stackToggle = new Button { tooltip = "Search stack traces too" };
            stackToggle.AddToClassList("lg-search__toggle");
            stackToggle.Add(new LoggerIcon(LoggerGlyph.Stack));
            stackToggle.EnableInClassList("lg-search__toggle--on", stack);
            LoggerUi.Press(stackToggle, () =>
            {
                stackToggle.ToggleInClassList("lg-search__toggle--on");
                OptionsChanged?.Invoke();
            });
            clear = LoggerUi.Icon(LoggerGlyph.Close, "Clear the search (Esc)", () =>
            {
                field.value = string.Empty;
                field.Q(className: "unity-text-field__input")?.Focus();
            }, "lg-search__clear");
            options.Add(clear);
            options.Add(caseToggle);
            options.Add(regexToggle);
            options.Add(stackToggle);
            Add(options);

            field.RegisterValueChangedCallback(evt =>
            {
                Sync();
                TextChanged?.Invoke(evt.newValue ?? string.Empty);
            });
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Submitted?.Invoke();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    if (!string.IsNullOrEmpty(field.value))
                    {
                        field.value = string.Empty;
                    }
                    else
                    {
                        Escaped?.Invoke();
                    }

                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.DownArrow)
                {
                    DownPressed?.Invoke();
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            Sync();
        }

        public string Value
        {
            get => field.value ?? string.Empty;
            set => field.SetValueWithoutNotify(value ?? string.Empty);
        }

        public bool MatchCase => caseToggle.ClassListContains("lg-search__toggle--on");
        public bool Regex => regexToggle.ClassListContains("lg-search__toggle--on");
        public bool Stack => stackToggle.ClassListContains("lg-search__toggle--on");

        public void FocusField()
        {
            var input = field.Q(className: "unity-text-field__input") ?? field.Q(className: "unity-base-text-field__input");
            (input ?? field).Focus();
            field.SelectAll();
        }

        public bool HasFocus => field.panel != null && field.panel.focusController?.focusedElement is VisualElement focused && (focused == field || field.Contains(focused));

        public void SetError(string error)
        {
            EnableInClassList("lg-search--error", !string.IsNullOrEmpty(error));
            field.tooltip = string.IsNullOrEmpty(error) ? null : "Invalid regular expression: " + error;
        }

        public void Sync()
        {
            bool empty = string.IsNullOrEmpty(field.value);
            placeholder.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            clear.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
            EnableInClassList("lg-search--active", !empty);
        }

        private Button Toggle(string text, string tooltip, bool on)
        {
            var button = new Button { text = text, tooltip = tooltip };
            button.AddToClassList("lg-search__toggle");
            button.AddToClassList("lg-search__toggle--text");
            button.EnableInClassList("lg-search__toggle--on", on);
            LoggerUi.Press(button, () =>
            {
                button.ToggleInClassList("lg-search__toggle--on");
                OptionsChanged?.Invoke();
            });
            return button;
        }
    }
}
