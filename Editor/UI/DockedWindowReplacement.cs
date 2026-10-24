using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// One of Unity's windows replaced by a Mixer window (the Hierarchy, the Project), as the Mixer console does with the
    /// Console. Once the layout is loaded, every such Unity window docked somewhere gets a Mixer window of its own at its
    /// place (selected only if it was) and is closed, so two stay two; one opened later (the Window menu, a shortcut, a
    /// layout change) is swapped the same way half a second later, except one opened on purpose from the Mixer window's
    /// menu. Turning the setting off puts Unity's window back in each Mixer window's place. Docking goes through Unity's
    /// internal <c>DockArea</c>; when a Unity version changes it the Mixer window opens on its own, Unity's stays, and one
    /// warning says so.
    /// </summary>
    internal sealed class DockedWindowReplacement
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type DockAreaType;
        private static readonly FieldInfo ParentField;
        private static readonly FieldInfo PanesField;
        private static readonly PropertyInfo SelectedProperty;
        private static readonly PropertyInfo ActualViewProperty;
        private static readonly MethodInfo AddTabMethod;
        private static readonly PropertyInfo MainWindowProperty;
        private static readonly PropertyInfo WindowsProperty;
        private static readonly PropertyInfo ShowModeProperty;

        static DockedWindowReplacement()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                DockAreaType = assembly.GetType("UnityEditor.DockArea");
                var hostType = assembly.GetType("UnityEditor.HostView");
                var containerType = assembly.GetType("UnityEditor.ContainerWindow");
                ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
                PanesField = DockAreaType?.GetField("m_Panes", Instance);
                SelectedProperty = DockAreaType?.GetProperty("selected", Instance);
                ActualViewProperty = hostType?.GetProperty("actualView", Instance);
                AddTabMethod = DockAreaType?.GetMethod("AddTab", Instance, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
                MainWindowProperty = containerType?.GetProperty("mainWindow", Static);
                WindowsProperty = containerType?.GetProperty("windows", Static);
                ShowModeProperty = containerType?.GetProperty("showMode", Instance);
            }
            catch (Exception) { }
        }

        private readonly Type unityType;
        private readonly Type mixerType;
        private readonly string prefKey, loadedKey, pendingKey, knownKey, keptKey, warnedKey;
        private readonly string unityMenu, unityName, mixerName;
        private bool enabled;
        private bool pending;
        private int readyTicks;
        private double nextScan;

        /// <param name="unityTypeName">Unity's window type, e.g. <c>UnityEditor.SceneHierarchyWindow</c>.</param>
        /// <param name="mixerType">The Mixer window put in its place.</param>
        /// <param name="prefKey">The EditorPrefs switch.</param>
        /// <param name="sessionPrefix">The SessionState keys' prefix.</param>
        /// <param name="unityMenu">The menu item that opens Unity's window.</param>
        public DockedWindowReplacement(string unityTypeName, Type mixerType, string prefKey, bool defaultOn, string sessionPrefix, string unityMenu, string unityName, string mixerName)
        {
            unityType = typeof(EditorWindow).Assembly.GetType(unityTypeName);
            this.mixerType = mixerType;
            this.prefKey = prefKey;
            loadedKey = sessionPrefix + ".Loaded";
            pendingKey = sessionPrefix + ".StartupPending";
            knownKey = sessionPrefix + ".Known";
            // Unity windows the user opened on purpose: kept for the whole editor session (script reloads included).
            keptKey = sessionPrefix + ".Kept";
            warnedKey = sessionPrefix + ".Warned";
            this.unityMenu = unityMenu;
            this.unityName = unityName;
            this.mixerName = mixerName;
            enabled = EditorPrefs.GetBool(prefKey, defaultOn);
            if (unityType == null || Application.isBatchMode) return;
            // Only windows the docking failed on since this reload are left alone.
            SessionState.EraseString(knownKey);
            if (!SessionState.GetBool(loadedKey, false))
            {
                SessionState.SetBool(loadedKey, true);
                SessionState.SetBool(pendingKey, true);
            }
            pending = SessionState.GetBool(pendingKey, false);
            EditorApplication.update += Update;
        }

        public bool Enabled => enabled;
        public static bool CanDock => ParentField != null && DockAreaType != null && PanesField != null && SelectedProperty != null && ActualViewProperty != null && AddTabMethod != null;

        public void SetEnabled(bool on)
        {
            bool was = enabled;
            enabled = on;
            EditorPrefs.SetBool(prefKey, on);
            if (on) { SessionState.EraseString(knownKey); SessionState.EraseString(keptKey); ReplaceOpen(); }
            else if (was) RestoreUnityWindows();
        }

        /// <summary>Opens Unity's own window beside the Mixer one and leaves it there for this editor session.</summary>
        public void OpenUnityWindow()
        {
            EditorApplication.ExecuteMenuItem(unityMenu);
            foreach (var window in OpenUnityWindows()) Keep(window);
        }

        // The setting turned off: Unity's window comes back where each docked Mixer window is, and that one closes.
        private void RestoreUnityWindows()
        {
            if (!CanDock || unityType == null) return;
            foreach (var found in Resources.FindObjectsOfTypeAll(mixerType))
            {
                var mine = found as EditorWindow;
                if (mine == null || IsHidden(mine)) continue;
                var area = DockAreaOf(mine);
                if (area == null) continue;
                try
                {
                    var unity = (EditorWindow)ScriptableObject.CreateInstance(unityType);
                    int index = IndexOf(area, mine);
                    bool selected = ReferenceEquals(ActualViewProperty.GetValue(area), mine);
                    int shown = (int)SelectedProperty.GetValue(area);
                    AddTabMethod.Invoke(area, new object[] { index, unity, true });
                    if (!selected) SelectedProperty.SetValue(area, shown >= index ? shown + 1 : shown);
                    Keep(unity);
                    mine.Close();
                }
                catch (Exception exception)
                {
                    var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                    Debug.LogWarning("[Mixer] Couldn't put Unity's " + unityName + " back in the " + mixerName + "'s place (" + cause.Message + "): open it from " + unityMenu.Replace("/", " › ") + ".");
                }
            }
        }

        public void ReplaceOpen()
        {
            if (!enabled) return;
            foreach (var window in OpenUnityWindows()) Replace(window, false);
        }

        private void Update()
        {
            if (pending)
            {
                if (!LayoutLoaded()) return;
                if (++readyTicks < 2) return;
                pending = false;
                SessionState.SetBool(pendingKey, false);
                if (enabled)
                {
                    var focused = EditorWindow.focusedWindow;
                    foreach (var window in OpenUnityWindows()) Replace(window, window == focused);
                }
                return;
            }
            // One opened during the session: swapped once it exists. One that could not be docked (Fallback) stays.
            if (!enabled || EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.5;
            foreach (var window in OpenUnityWindows())
            {
                if (IsKnown(window) || IsKept(window)) continue;
                Replace(window, window == EditorWindow.focusedWindow);
                break;
            }
        }

        public bool Replace(EditorWindow unity, bool focus)
        {
            if (unity == null) return false;
            EditorWindow mine = null;
            try
            {
                if (!CanDock) throw new NotSupportedException("this Unity version has no DockArea.AddTab");
                var area = DockAreaOf(unity);
                if (area != null)
                {
                    // Each Unity window gets the Mixer window of its own dock area: another area never loses its tab (and
                    // its split never collapses) because a second one was swapped.
                    mine = MineIn(area);
                    bool selected = ReferenceEquals(ActualViewProperty.GetValue(area), unity);
                    if (mine == null)
                    {
                        mine = (EditorWindow)ScriptableObject.CreateInstance(mixerType);
                        int index = IndexOf(area, unity);
                        int shown = (int)SelectedProperty.GetValue(area);
                        AddTabMethod.Invoke(area, new object[] { index, mine, true });
                        if (!selected) SelectedProperty.SetValue(area, shown >= index ? shown + 1 : shown);
                    }
                    else if (selected) SelectedProperty.SetValue(area, IndexOf(area, mine));
                }
                else
                {
                    var rect = unity.position;
                    mine = (EditorWindow)ScriptableObject.CreateInstance(mixerType);
                    mine.Show();
                    mine.position = rect;
                }
                unity.Close();
                if (focus && mine != null) mine.Focus();
                return true;
            }
            catch (Exception exception)
            {
                Fallback(unity, mine, focus, exception);
                return false;
            }
        }

        private void Fallback(EditorWindow unity, EditorWindow mine, bool focus, Exception exception)
        {
            if (unity != null) Remember(unity);
            try { if (mine == null) EditorWindow.GetWindow(mixerType, false, null, focus); } catch (Exception) { }
            if (SessionState.GetBool(warnedKey, false)) return;
            SessionState.SetBool(warnedKey, true);
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Debug.LogWarning("[Mixer] Couldn't put the " + mixerName + " in the " + unityName + "'s place (" + cause.Message + "), so Unity's stays open. Turn off \"Replace Unity's " + unityName + "\" in the " + mixerName + "'s menu to stop trying.");
        }

        // The Mixer window docked in that area, if there is one.
        private EditorWindow MineIn(object area)
        {
            if (!(PanesField.GetValue(area) is IList panes)) return null;
            foreach (var pane in panes)
                if (pane is EditorWindow window && window != null && mixerType.IsInstanceOfType(window) && !IsHidden(window)) return window;
            return null;
        }

        private List<EditorWindow> OpenUnityWindows()
        {
            var result = new List<EditorWindow>();
            if (unityType == null) return result;
            foreach (var window in Resources.FindObjectsOfTypeAll(unityType))
                if (window is EditorWindow w && w != null && !IsHidden(w)) result.Add(w);
            return result;
        }

        private static bool IsHidden(Object window) => (window.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave;

        private static object DockAreaOf(EditorWindow window)
        {
            var parent = ParentField?.GetValue(window);
            return parent != null && DockAreaType.IsInstanceOfType(parent) ? parent : null;
        }

        private static int IndexOf(object area, EditorWindow window)
        {
            int index = PanesField.GetValue(area) is IList panes ? panes.IndexOf(window) : -1;
            if (index < 0) throw new InvalidOperationException("the window is not in its dock area's tabs");
            return index;
        }

        // The main window is up. ContainerWindow.mainWindow reads null after a script reload while the main window is open,
        // so the container windows' show modes are asked too.
        private static bool LayoutLoaded()
        {
            try
            {
                if (MainWindowProperty?.GetValue(null) != null) return true;
                if (WindowsProperty?.GetValue(null) is Array windows && ShowModeProperty != null)
                    foreach (var window in windows)
                        if (window != null && ShowModeProperty.GetValue(window)?.ToString() == "MainWindow") return true;
            }
            catch (Exception) { }
            return MainWindowProperty == null && Resources.FindObjectsOfTypeAll<EditorWindow>().Length > 0;
        }

        private bool IsKnown(Object window) => Array.IndexOf(Ids(knownKey), Id(window)) >= 0;
        private void Remember(Object window) => Add(knownKey, window);
        private bool IsKept(Object window) => Array.IndexOf(Ids(keptKey), Id(window)) >= 0;
        private void Keep(Object window) => Add(keptKey, window);

        private static string Id(Object window) => window.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        private static string[] Ids(string key) => SessionState.GetString(key, string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        private static void Add(string key, Object window)
        {
            if (window == null) return;
            string id = Id(window);
            var ids = Ids(key);
            if (Array.IndexOf(ids, id) >= 0) return;
            string all = SessionState.GetString(key, string.Empty);
            SessionState.SetString(key, all.Length == 0 ? id : all + "," + id);
        }
    }
}
