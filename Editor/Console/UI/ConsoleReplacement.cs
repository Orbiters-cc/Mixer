using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Logger.Editor
{
    /// <summary>What to do with a Console window so that the Mixer console takes its place.</summary>
    internal enum ConsoleSwap
    {
        /// <summary>Open a Mixer console as a tab at the Console's place in its dock area, then close the Console.</summary>
        AddTab,
        /// <summary>Move the open Mixer console (hidden behind another tab) to the Console's place, then close the Console.</summary>
        MoveTab,
        /// <summary>Bring the open Mixer console's tab to the front where it is, then close the Console.</summary>
        ShowLogger,
        /// <summary>Close the Console; the open Mixer console stays as it is.</summary>
        CloseOnly,
        /// <summary>Open a Mixer console window over the Console's rectangle (the Console is not in a dock area), then close it.</summary>
        OpenFloating,
    }

    /// <summary>Where a Console window is.</summary>
    internal struct ConsoleSpot
    {
        /// <summary>In a dock area (docked in the main window or a floating window's tabs).</summary>
        public bool Docked;
        /// <summary>The tab shown in its dock area (always true for a window that is not docked).</summary>
        public bool Selected;
        /// <summary>The only tab of a floating window of its own (as Window › General › Console opens it).</summary>
        public bool Alone;
    }

    /// <summary>Where the open Mixer console is, if any.</summary>
    internal struct LoggerSpot
    {
        public bool Open;
        public bool Docked;
        /// <summary>The tab shown in its dock area.</summary>
        public bool Visible;
        /// <summary>In the Console's dock area.</summary>
        public bool SameArea;
    }

    /// <summary>
    /// "Replace console" (on by default, per user): the Mixer console takes the place of Unity's Console window. At the start
    /// of the editor, once the layout is loaded, a Console docked somewhere gets a Mixer console tab at its place (selected
    /// only if the Console was) and is closed; a Mixer console already open stays where it is, or moves there when it was
    /// hidden behind another tab and the Console was the one shown. Later in the session, a Console opened by
    /// Window › General › Console, Ctrl+Shift+C or a click on the status bar is swapped for the Mixer console the same way and
    /// the Mixer console gets the focus. Docking goes through Unity's internal <c>DockArea</c>; when a Unity version changes it
    /// the Mixer console opens as a window of its own, the Console stays, and one warning says so.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleReplacement
    {
        internal const string PrefKey = "Orbiters.Logger.ReplaceConsole";
        internal const bool DefaultOn = true;

        private const string LoadedKey = "Orbiters.Logger.ConsoleReplacement.Loaded";
        private const string PendingKey = "Orbiters.Logger.ConsoleReplacement.StartupPending";
        private const string KnownKey = "Orbiters.Logger.ConsoleReplacement.Known";
        private const string WarnedKey = "Orbiters.Logger.ConsoleReplacement.Warned";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type ConsoleType;
        private static readonly Type DockAreaType;
        private static readonly FieldInfo ParentField;
        private static readonly FieldInfo CurrentConsoleField;
        private static readonly FieldInfo PanesField;
        private static readonly PropertyInfo SelectedProperty;
        private static readonly PropertyInfo ActualViewProperty;
        private static readonly MethodInfo AddTabMethod;
        private static readonly MethodInfo RemoveTabMethod;
        private static readonly PropertyInfo ViewWindowProperty;
        private static readonly PropertyInfo RootViewProperty;
        private static readonly PropertyInfo AllChildrenProperty;
        private static readonly MethodInfo IsMainWindowMethod;
        private static readonly PropertyInfo MainWindowProperty;

        private static bool enabled;
        private static bool pending;
        private static EditorWindow lastSeen;
        private static int readyTicks;

        static ConsoleReplacement()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                ConsoleType = assembly.GetType("UnityEditor.ConsoleWindow");
                DockAreaType = assembly.GetType("UnityEditor.DockArea");
                var viewType = assembly.GetType("UnityEditor.View");
                var hostType = assembly.GetType("UnityEditor.HostView");
                var containerType = assembly.GetType("UnityEditor.ContainerWindow");
                ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
                CurrentConsoleField = ConsoleType?.GetField("ms_ConsoleWindow", Static);
                PanesField = DockAreaType?.GetField("m_Panes", Instance);
                SelectedProperty = DockAreaType?.GetProperty("selected", Instance);
                ActualViewProperty = hostType?.GetProperty("actualView", Instance);
                AddTabMethod = DockAreaType?.GetMethod("AddTab", Instance, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
                RemoveTabMethod = DockAreaType?.GetMethod("RemoveTab", Instance, null, new[] { typeof(EditorWindow), typeof(bool), typeof(bool) }, null);
                ViewWindowProperty = viewType?.GetProperty("window", Instance);
                RootViewProperty = containerType?.GetProperty("rootView", Instance);
                AllChildrenProperty = viewType?.GetProperty("allChildren", Instance);
                IsMainWindowMethod = containerType?.GetMethod("IsMainWindow", Instance, null, Type.EmptyTypes, null);
                MainWindowProperty = containerType?.GetProperty("mainWindow", Static);
            }
            catch (Exception)
            {
                // Each missing member turns its part off: CanDock and CanWatch say which.
            }

            enabled = EditorPrefs.GetBool(PrefKey, DefaultOn);
            if (ConsoleType == null || Application.isBatchMode)
            {
                return;
            }

            if (!SessionState.GetBool(LoadedKey, false))
            {
                SessionState.SetBool(LoadedKey, true);
                // At the start of the editor scripts load before the layout, so no window exists yet. Loaded later in a
                // session (the package was just installed), the Consoles already open are left alone until next time.
                bool startup = !AnyEditorWindow();
                SessionState.SetBool(PendingKey, startup);
                if (!startup)
                {
                    foreach (var console in OpenConsoles())
                    {
                        Remember(console);
                    }
                }
            }

            pending = SessionState.GetBool(PendingKey, false);
            EditorApplication.update += Update;
        }

        /// <summary>The user's choice (on unless turned off).</summary>
        internal static bool Enabled => enabled;

        /// <summary>This Unity has the internals that dock the Mixer console where the Console is.</summary>
        internal static bool CanDock =>
            ParentField != null && DockAreaType != null && PanesField != null && SelectedProperty != null && ActualViewProperty != null &&
            AddTabMethod != null && RemoveTabMethod != null;

        /// <summary>This Unity tells which Console window Window › General › Console opened.</summary>
        internal static bool CanWatch => CurrentConsoleField != null;

        /// <summary>Turns the replacement on or off (the Consoles open now stay until <see cref="ReplaceOpenConsoles"/>).</summary>
        internal static void SetEnabled(bool on)
        {
            enabled = on;
            EditorPrefs.SetBool(PrefKey, on);
            if (on)
            {
                // Consoles kept so far (open while it was off) are the Mixer console's to replace again.
                SessionState.EraseString(KnownKey);
            }
        }

        /// <summary>Puts the Mixer console in the place of every Console open now (when turned on from the top bar).</summary>
        internal static void ReplaceOpenConsoles()
        {
            if (!enabled)
            {
                return;
            }

            foreach (var console in OpenConsoles())
            {
                Replace(console, false);
            }
        }

        /// <summary>
        /// Where the Mixer console goes for a Console at <paramref name="console"/>, with the Mixer console at <paramref name="logger"/>.
        /// </summary>
        internal static ConsoleSwap Decide(ConsoleSpot console, LoggerSpot logger)
        {
            if (!logger.Open)
            {
                return console.Docked ? ConsoleSwap.AddTab : ConsoleSwap.OpenFloating;
            }

            if (logger.SameArea)
            {
                return console.Selected ? ConsoleSwap.ShowLogger : ConsoleSwap.CloseOnly;
            }

            if (!console.Selected || logger.Visible)
            {
                return ConsoleSwap.CloseOnly;
            }

            // A Console alone in a window of its own (Window › General › Console) doesn't pull the Mixer console out of the
            // user's dock: the Mixer console comes to the front where it is.
            return console.Docked && !console.Alone && logger.Docked ? ConsoleSwap.MoveTab : ConsoleSwap.ShowLogger;
        }

        private static void Update()
        {
            if (pending)
            {
                if (!LayoutLoaded())
                {
                    return;
                }

                // One more editor tick so every window of the layout has run its OnEnable.
                if (++readyTicks < 2)
                {
                    return;
                }

                pending = false;
                SessionState.SetBool(PendingKey, false);
                if (enabled)
                {
                    var focused = EditorWindow.focusedWindow;
                    foreach (var console in OpenConsoles())
                    {
                        Replace(console, console == focused);
                    }

                    OpenIfLost();
                }

                return;
            }

            if (CurrentConsoleField == null)
            {
                return;
            }

            var current = CurrentConsoleField.GetValue(null) as EditorWindow;
            if (current == null || ReferenceEquals(current, lastSeen))
            {
                return;
            }

            lastSeen = current;
            if (IsHidden(current) || IsKnown(current))
            {
                return;
            }

            if (!enabled)
            {
                Remember(current);
                return;
            }

            // Opened during the session: the user asked for the console, so the Mixer console gets the focus.
            Replace(current, true);
        }

        /// <summary>
        /// The Mixer console for a tool's "see the console" link (<see cref="ConsoleLinks"/>): the open one comes to the
        /// front where it is, else one opens beside the Project window; it takes the focus and shows the newest error.
        /// </summary>
        internal static void ShowLatestError()
        {
            try
            {
                var logger = FindLogger();
                if (logger == null)
                {
                    logger = ScriptableObject.CreateInstance<LoggerWindow>();
                    Orbiters.Mixer.Editor.MixerWindowPlacement.OpenBeside(logger, "Orbiters.Mixer.Editor.MixerProjectWindow", "UnityEditor.ProjectBrowser", "UnityEditor.GameView");
                }
                else if (CanDock && DockAreaOf(logger) is { } area)
                {
                    SelectedProperty.SetValue(area, IndexOf(area, logger));
                }

                logger.Focus();
                logger.ShowLatestError();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Mixer console] Couldn't show the Mixer console (" + exception.Message + "): open it from Window › General › Console.");
            }
        }

        // A layout from another project that has neither console (its Mixer console could not be put back, see
        // ForeignLayoutRepair): the Mixer console opens beside the Project window, where Unity's default layout has it.
        private static void OpenIfLost()
        {
            if (!Orbiters.Mixer.Editor.ForeignLayoutRepair.StartedFromAnotherProjectsLayout || OpenConsoles().Count > 0 || FindLogger() != null)
            {
                return;
            }

            try
            {
                Orbiters.Mixer.Editor.MixerWindowPlacement.OpenBeside(ScriptableObject.CreateInstance<LoggerWindow>(),
                    "Orbiters.Mixer.Editor.MixerProjectWindow", "UnityEditor.ProjectBrowser", "UnityEditor.GameView", "UnityEditor.SceneView");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Mixer console] Couldn't open the Mixer console (" + exception.Message + "): open it from Window › General › Console.");
            }
        }

        /// <summary>Puts the Mixer console in <paramref name="console"/>'s place and closes it. False when it fell back.</summary>
        internal static bool Replace(EditorWindow console, bool focus) => Replace(console, focus, FindLogger());

        /// <summary>Same, with <paramref name="logger"/> as the open Mixer console (null: none open).</summary>
        internal static bool Replace(EditorWindow console, bool focus, LoggerWindow logger)
        {
            if (console == null)
            {
                return false;
            }

            try
            {
                if (!CanDock)
                {
                    throw new NotSupportedException("this Unity version has no DockArea.AddTab");
                }

                var area = DockAreaOf(console);
                var loggerArea = logger != null ? DockAreaOf(logger) : null;
                var consoleSpot = new ConsoleSpot
                {
                    Docked = area != null,
                    Selected = area == null || ReferenceEquals(ActualView(area), console),
                    Alone = area != null && IsAlone(area),
                };
                var loggerSpot = new LoggerSpot
                {
                    Open = logger != null,
                    Docked = loggerArea != null,
                    Visible = logger != null && (loggerArea == null || ReferenceEquals(ActualView(loggerArea), logger)),
                    SameArea = area != null && ReferenceEquals(area, loggerArea),
                };

                switch (Decide(consoleSpot, loggerSpot))
                {
                    case ConsoleSwap.AddTab:
                    {
                        logger = ScriptableObject.CreateInstance<LoggerWindow>();
                        int index = IndexOf(area, console);
                        int selected = (int)SelectedProperty.GetValue(area);
                        AddTabMethod.Invoke(area, new object[] { index, logger, true });
                        // AddTab shows the new tab: keep the tab that was shown when it wasn't the Console.
                        if (!consoleSpot.Selected)
                        {
                            SelectedProperty.SetValue(area, selected >= index ? selected + 1 : selected);
                        }

                        break;
                    }
                    case ConsoleSwap.MoveTab:
                        RemoveTabMethod.Invoke(loggerArea, new object[] { logger, true, true });
                        AddTabMethod.Invoke(area, new object[] { IndexOf(area, console), logger, true });
                        break;
                    case ConsoleSwap.ShowLogger:
                        if (loggerArea != null)
                        {
                            SelectedProperty.SetValue(loggerArea, IndexOf(loggerArea, logger));
                        }

                        break;
                    case ConsoleSwap.OpenFloating:
                    {
                        var rect = console.position;
                        logger = ScriptableObject.CreateInstance<LoggerWindow>();
                        logger.Show();
                        logger.position = rect;
                        break;
                    }
                }

                console.Close();
                if (focus && logger != null)
                {
                    logger.Focus();
                }

                return true;
            }
            catch (Exception exception)
            {
                Fallback(console, logger, focus, exception);
                return false;
            }
        }

        // The Console stays; the Mixer console opens as Unity places a new window, and one warning per session says why.
        private static void Fallback(EditorWindow console, LoggerWindow logger, bool focus, Exception exception)
        {
            if (console != null)
            {
                Remember(console);
            }

            try
            {
                if (logger == null)
                {
                    EditorWindow.GetWindow<LoggerWindow>(false, null, focus);
                }
            }
            catch (Exception)
            {
                // The warning below is all that can be done.
            }

            if (SessionState.GetBool(WarnedKey, false))
            {
                return;
            }

            SessionState.SetBool(WarnedKey, true);
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Debug.LogWarning("[Mixer console] Couldn't put the Mixer console in the Console's place (" + cause.Message + "), so the Console stays open. " +
                             "Turn off \"Replace console\" in the Mixer console's top bar to stop trying.");
        }

        // ---- Windows ----------------------------------------------------------------------------------------------

        /// <summary>The open Mixer console to use: one that is shown first, hidden capture copies left out.</summary>
        private static LoggerWindow FindLogger()
        {
            LoggerWindow first = null;
            foreach (var logger in Resources.FindObjectsOfTypeAll<LoggerWindow>())
            {
                if (logger == null || IsHidden(logger))
                {
                    continue;
                }

                var area = DockAreaOf(logger);
                if (area == null || ReferenceEquals(ActualView(area), logger))
                {
                    return logger;
                }

                first ??= logger;
            }

            return first;
        }

        private static List<EditorWindow> OpenConsoles()
        {
            var consoles = new List<EditorWindow>();
            if (ConsoleType == null)
            {
                return consoles;
            }

            foreach (var window in Resources.FindObjectsOfTypeAll(ConsoleType))
            {
                if (window is EditorWindow console && console != null && !IsHidden(console))
                {
                    consoles.Add(console);
                }
            }

            return consoles;
        }

        // Copies that tools open outside every display to take screenshots (HideAndDontSave) are not the user's windows.
        private static bool IsHidden(Object window) => (window.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave;

        private static object DockAreaOf(EditorWindow window)
        {
            var parent = ParentField?.GetValue(window);
            return parent != null && DockAreaType.IsInstanceOfType(parent) ? parent : null;
        }

        private static object ActualView(object area) => ActualViewProperty.GetValue(area);

        private static int IndexOf(object area, EditorWindow window)
        {
            int index = PanesField.GetValue(area) is IList panes ? panes.IndexOf(window) : -1;
            if (index < 0)
            {
                throw new InvalidOperationException("the window is not in its dock area's tabs");
            }

            return index;
        }

        // The only tab of the only dock area of a window that isn't the main one.
        private static bool IsAlone(object area)
        {
            if (!(PanesField.GetValue(area) is IList panes) || panes.Count != 1 || ViewWindowProperty == null || RootViewProperty == null ||
                AllChildrenProperty == null || IsMainWindowMethod == null)
            {
                return false;
            }

            var container = ViewWindowProperty.GetValue(area);
            if (container == null || (bool)IsMainWindowMethod.Invoke(container, null))
            {
                return false;
            }

            int areas = 0;
            if (RootViewProperty.GetValue(container) is { } root && AllChildrenProperty.GetValue(root) is Array views)
            {
                foreach (var view in views)
                {
                    if (DockAreaType.IsInstanceOfType(view))
                    {
                        areas++;
                    }
                }
            }

            return areas == 1;
        }

        private static bool AnyEditorWindow() => Resources.FindObjectsOfTypeAll<EditorWindow>().Length > 0;

        private static bool LayoutLoaded() => MainWindowProperty == null ? AnyEditorWindow() : MainWindowProperty.GetValue(null) != null;

        // Consoles the user keeps: open when the Mixer console arrived mid-session, opened with the replacement off, or where
        // docking failed. Instance IDs of windows survive script reloads.
        private static bool IsKnown(Object console) => Array.IndexOf(Known(), console.GetInstanceID().ToString(CultureInfo.InvariantCulture)) >= 0;

        private static void Remember(Object console)
        {
            if (!IsKnown(console))
            {
                string known = SessionState.GetString(KnownKey, string.Empty);
                string id = console.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                SessionState.SetString(KnownKey, known.Length == 0 ? id : known + "," + id);
            }
        }

        private static string[] Known() => SessionState.GetString(KnownKey, string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
