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
    /// "Replace Unity's Hierarchy" (on by default, off in the Orbiters settings): the Mixer Hierarchy takes the place
    /// of Unity's Hierarchy window, as the Logger does with the Console. Once the layout is loaded, every Hierarchy
    /// docked somewhere gets a Mixer Hierarchy tab of its own at its place (selected only if it was) and is closed, so
    /// two Hierarchies stay two; a Hierarchy opened later (Window › General › Hierarchy, Ctrl+4, a layout change) is
    /// swapped the same way, half a second later, except one opened from the Mixer Hierarchy's "Open Unity's
    /// Hierarchy". Turning the setting off puts Unity's Hierarchy back in each Mixer Hierarchy's place. Docking goes
    /// through Unity's internal <c>DockArea</c>; when a Unity version changes it the Mixer Hierarchy opens as a window
    /// of its own, Unity's stays, and one warning says so.
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyReplacement
    {
        internal const string PrefKey = "Orbiters.Mixer.ReplaceHierarchy";
        internal const bool DefaultOn = true;
        private const string LoadedKey = "Orbiters.Mixer.HierarchyReplacement.Loaded";
        private const string PendingKey = "Orbiters.Mixer.HierarchyReplacement.StartupPending";
        private const string KnownKey = "Orbiters.Mixer.HierarchyReplacement.Known";
        // Unity Hierarchies the user opened on purpose: kept for the whole editor session (script reloads included).
        private const string KeptKey = "Orbiters.Mixer.HierarchyReplacement.Kept";
        private const string WarnedKey = "Orbiters.Mixer.HierarchyReplacement.Warned";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type HierarchyType;
        private static readonly Type DockAreaType;
        private static readonly FieldInfo ParentField;
        private static readonly FieldInfo PanesField;
        private static readonly PropertyInfo SelectedProperty;
        private static readonly PropertyInfo ActualViewProperty;
        private static readonly MethodInfo AddTabMethod;
        private static readonly PropertyInfo MainWindowProperty;

        private static bool enabled;
        private static bool pending;
        private static int readyTicks;
        private static double nextScan;

        static HierarchyReplacement()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                HierarchyType = assembly.GetType("UnityEditor.SceneHierarchyWindow");
                DockAreaType = assembly.GetType("UnityEditor.DockArea");
                var hostType = assembly.GetType("UnityEditor.HostView");
                var containerType = assembly.GetType("UnityEditor.ContainerWindow");
                ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
                PanesField = DockAreaType?.GetField("m_Panes", Instance);
                SelectedProperty = DockAreaType?.GetProperty("selected", Instance);
                ActualViewProperty = hostType?.GetProperty("actualView", Instance);
                AddTabMethod = DockAreaType?.GetMethod("AddTab", Instance, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
                MainWindowProperty = containerType?.GetProperty("mainWindow", Static);
            }
            catch (Exception) { }
            enabled = EditorPrefs.GetBool(PrefKey, DefaultOn);
            if (HierarchyType == null || Application.isBatchMode) return;
            // Only windows the docking failed on since this reload are left alone.
            SessionState.EraseString(KnownKey);
            if (!SessionState.GetBool(LoadedKey, false))
            {
                SessionState.SetBool(LoadedKey, true);
                SessionState.SetBool(PendingKey, true);
            }
            pending = SessionState.GetBool(PendingKey, false);
            EditorApplication.update += Update;
        }

        internal static bool Enabled => enabled;
        internal static bool CanDock => ParentField != null && DockAreaType != null && PanesField != null && SelectedProperty != null && ActualViewProperty != null && AddTabMethod != null;

        internal static void SetEnabled(bool on)
        {
            bool was = enabled;
            enabled = on;
            EditorPrefs.SetBool(PrefKey, on);
            if (on) { SessionState.EraseString(KnownKey); SessionState.EraseString(KeptKey); ReplaceOpenHierarchies(); }
            else if (was) RestoreUnityHierarchies();
        }

        /// <summary>Opens Unity's own Hierarchy beside the Mixer one and leaves it there for this editor session.</summary>
        internal static void OpenUnityHierarchy()
        {
            EditorApplication.ExecuteMenuItem("Window/General/Hierarchy");
            foreach (var hierarchy in OpenHierarchies()) Keep(hierarchy);
        }

        // The setting turned off: Unity's Hierarchy comes back where each docked Mixer Hierarchy is, and that one closes.
        private static void RestoreUnityHierarchies()
        {
            if (!CanDock || HierarchyType == null) return;
            foreach (var mine in Resources.FindObjectsOfTypeAll<MixerHierarchyWindow>())
            {
                if (mine == null || IsHidden(mine)) continue;
                var area = DockAreaOf(mine);
                if (area == null) continue;
                try
                {
                    var unity = (EditorWindow)ScriptableObject.CreateInstance(HierarchyType);
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
                    Debug.LogWarning("[Mixer] Couldn't put Unity's Hierarchy back in the Mixer Hierarchy's place (" + cause.Message + "): open it from Window › General › Hierarchy.");
                }
            }
        }

        internal static void ReplaceOpenHierarchies()
        {
            if (!enabled) return;
            foreach (var hierarchy in OpenHierarchies()) Replace(hierarchy, false);
        }

        private static void Update()
        {
            if (pending)
            {
                if (!LayoutLoaded()) return;
                if (++readyTicks < 2) return;
                pending = false;
                SessionState.SetBool(PendingKey, false);
                if (enabled)
                {
                    var focused = EditorWindow.focusedWindow;
                    foreach (var hierarchy in OpenHierarchies()) Replace(hierarchy, hierarchy == focused);
                }
                return;
            }
            // A Hierarchy opened during the session (Ctrl+4, the Window menu, a layout change): swapped once it exists.
            // One that could not be docked (Fallback) is left alone.
            if (!enabled || EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.5;
            foreach (var hierarchy in OpenHierarchies())
            {
                if (IsKnown(hierarchy) || IsKept(hierarchy)) continue;
                Replace(hierarchy, hierarchy == EditorWindow.focusedWindow);
                break;
            }
        }

        internal static bool Replace(EditorWindow hierarchy, bool focus)
        {
            if (hierarchy == null) return false;
            MixerHierarchyWindow mine = null;
            try
            {
                if (!CanDock) throw new NotSupportedException("this Unity version has no DockArea.AddTab");
                var area = DockAreaOf(hierarchy);
                if (area != null)
                {
                    // Each Hierarchy gets the Mixer Hierarchy of its own dock area: another area never loses its tab (and
                    // its split never collapses) because a second Hierarchy was swapped.
                    mine = MineIn(area);
                    bool selected = ReferenceEquals(ActualViewProperty.GetValue(area), hierarchy);
                    if (mine == null)
                    {
                        mine = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
                        int index = IndexOf(area, hierarchy);
                        int shown = (int)SelectedProperty.GetValue(area);
                        AddTabMethod.Invoke(area, new object[] { index, mine, true });
                        if (!selected) SelectedProperty.SetValue(area, shown >= index ? shown + 1 : shown);
                    }
                    else if (selected) SelectedProperty.SetValue(area, IndexOf(area, mine));
                }
                else
                {
                    var rect = hierarchy.position;
                    mine = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
                    mine.Show();
                    mine.position = rect;
                }
                hierarchy.Close();
                if (focus && mine != null) mine.Focus();
                return true;
            }
            catch (Exception exception)
            {
                Fallback(hierarchy, mine, focus, exception);
                return false;
            }
        }

        private static void Fallback(EditorWindow hierarchy, MixerHierarchyWindow mine, bool focus, Exception exception)
        {
            if (hierarchy != null) Remember(hierarchy);
            try { if (mine == null) EditorWindow.GetWindow<MixerHierarchyWindow>(false, null, focus); } catch (Exception) { }
            if (SessionState.GetBool(WarnedKey, false)) return;
            SessionState.SetBool(WarnedKey, true);
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Debug.LogWarning("[Mixer] Couldn't put the Mixer Hierarchy in the Hierarchy's place (" + cause.Message + "), so Unity's stays open. Turn off \"Replace Unity's Hierarchy\" in the Mixer Hierarchy's menu to stop trying.");
        }

        // The Mixer Hierarchy docked in that area, if there is one.
        private static MixerHierarchyWindow MineIn(object area)
        {
            if (!(PanesField.GetValue(area) is IList panes)) return null;
            foreach (var pane in panes)
                if (pane is MixerHierarchyWindow window && window != null && !IsHidden(window)) return window;
            return null;
        }

        private static List<EditorWindow> OpenHierarchies()
        {
            var result = new List<EditorWindow>();
            if (HierarchyType == null) return result;
            foreach (var window in Resources.FindObjectsOfTypeAll(HierarchyType))
                if (window is EditorWindow h && h != null && !IsHidden(h)) result.Add(h);
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

        private static bool LayoutLoaded() => MainWindowProperty == null ? Resources.FindObjectsOfTypeAll<EditorWindow>().Length > 0 : MainWindowProperty.GetValue(null) != null;

        private static bool IsKnown(Object window) => Array.IndexOf(Known(), window.GetInstanceID().ToString(CultureInfo.InvariantCulture)) >= 0;

        private static void Remember(Object window)
        {
            if (IsKnown(window)) return;
            string known = SessionState.GetString(KnownKey, string.Empty);
            string id = window.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            SessionState.SetString(KnownKey, known.Length == 0 ? id : known + "," + id);
        }

        private static string[] Known() => SessionState.GetString(KnownKey, string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        private static bool IsKept(Object window) =>
            Array.IndexOf(SessionState.GetString(KeptKey, string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), window.GetInstanceID().ToString(CultureInfo.InvariantCulture)) >= 0;

        private static void Keep(Object window)
        {
            if (window == null || IsKept(window)) return;
            string kept = SessionState.GetString(KeptKey, string.Empty);
            string id = window.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            SessionState.SetString(KeptKey, kept.Length == 0 ? id : kept + "," + id);
        }
    }
}
