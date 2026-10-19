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
    /// "Replace Hierarchy" (on by default, per user): the Mixer Hierarchy takes the place of Unity's Hierarchy
    /// window, as the Logger does with the Console. At the start of the editor, once the layout is loaded, a
    /// Hierarchy docked somewhere gets a Mixer Hierarchy tab at its place (selected only if it was) and is closed;
    /// later in the session a Hierarchy opened by Window › General › Hierarchy or Ctrl+4 is swapped the same way.
    /// Docking goes through Unity's internal <c>DockArea</c>; when a Unity version changes it the Mixer Hierarchy
    /// opens as a window of its own, Unity's stays, and one warning says so.
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyReplacement
    {
        internal const string PrefKey = "Orbiters.Mixer.ReplaceHierarchy";
        internal const bool DefaultOn = true;
        private const string LoadedKey = "Orbiters.Mixer.HierarchyReplacement.Loaded";
        private const string PendingKey = "Orbiters.Mixer.HierarchyReplacement.StartupPending";
        private const string KnownKey = "Orbiters.Mixer.HierarchyReplacement.Known";
        private const string WarnedKey = "Orbiters.Mixer.HierarchyReplacement.Warned";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type HierarchyType;
        private static readonly Type DockAreaType;
        private static readonly FieldInfo ParentField;
        private static readonly PropertyInfo LastInteractedProperty;
        private static readonly FieldInfo PanesField;
        private static readonly PropertyInfo SelectedProperty;
        private static readonly PropertyInfo ActualViewProperty;
        private static readonly MethodInfo AddTabMethod;
        private static readonly MethodInfo RemoveTabMethod;
        private static readonly PropertyInfo MainWindowProperty;

        private static bool enabled;
        private static bool pending;
        private static EditorWindow lastSeen;
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
                LastInteractedProperty = HierarchyType?.GetProperty("lastInteractedHierarchyWindow", Static);
                PanesField = DockAreaType?.GetField("m_Panes", Instance);
                SelectedProperty = DockAreaType?.GetProperty("selected", Instance);
                ActualViewProperty = hostType?.GetProperty("actualView", Instance);
                AddTabMethod = DockAreaType?.GetMethod("AddTab", Instance, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
                RemoveTabMethod = DockAreaType?.GetMethod("RemoveTab", Instance, null, new[] { typeof(EditorWindow), typeof(bool), typeof(bool) }, null);
                MainWindowProperty = containerType?.GetProperty("mainWindow", Static);
            }
            catch (Exception) { }
            enabled = EditorPrefs.GetBool(PrefKey, DefaultOn);
            if (HierarchyType == null || Application.isBatchMode) return;
            if (!SessionState.GetBool(LoadedKey, false))
            {
                SessionState.SetBool(LoadedKey, true);
                bool startup = Resources.FindObjectsOfTypeAll<EditorWindow>().Length == 0;
                SessionState.SetBool(PendingKey, startup);
                if (!startup) foreach (var hierarchy in OpenHierarchies()) Remember(hierarchy);
            }
            pending = SessionState.GetBool(PendingKey, false);
            EditorApplication.update += Update;
        }

        internal static bool Enabled => enabled;
        internal static bool CanDock => ParentField != null && DockAreaType != null && PanesField != null && SelectedProperty != null && ActualViewProperty != null && AddTabMethod != null && RemoveTabMethod != null;

        internal static void SetEnabled(bool on)
        {
            enabled = on;
            EditorPrefs.SetBool(PrefKey, on);
            if (on) SessionState.EraseString(KnownKey);
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
            // A Hierarchy opened during the session (Ctrl+4, the Window menu): swapped once it exists.
            if (EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.5;
            EditorWindow current = null;
            try { current = LastInteractedProperty?.GetValue(null) as EditorWindow; } catch (Exception) { }
            if (current == null)
            {
                foreach (var h in OpenHierarchies()) { if (!IsKnown(h)) { current = h; break; } }
            }
            if (current == null || ReferenceEquals(current, lastSeen)) return;
            lastSeen = current;
            if (IsHidden(current) || IsKnown(current)) return;
            if (!enabled) { Remember(current); return; }
            Replace(current, true);
        }

        internal static bool Replace(EditorWindow hierarchy, bool focus)
        {
            if (hierarchy == null) return false;
            var mine = FindMine();
            try
            {
                if (!CanDock) throw new NotSupportedException("this Unity version has no DockArea.AddTab");
                var area = DockAreaOf(hierarchy);
                var myArea = mine != null ? DockAreaOf(mine) : null;
                bool selected = area == null || ReferenceEquals(ActualViewProperty.GetValue(area), hierarchy);
                if (mine != null && area != null && ReferenceEquals(area, myArea))
                {
                    if (selected) SelectedProperty.SetValue(area, IndexOf(area, mine));
                }
                else if (mine != null)
                {
                    if (selected && myArea != null && area != null)
                    {
                        RemoveTabMethod.Invoke(myArea, new object[] { mine, true, true });
                        AddTabMethod.Invoke(area, new object[] { IndexOf(area, hierarchy), mine, true });
                    }
                    else if (selected && myArea != null) SelectedProperty.SetValue(myArea, IndexOf(myArea, mine));
                }
                else if (area != null)
                {
                    mine = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
                    int index = IndexOf(area, hierarchy);
                    int shown = (int)SelectedProperty.GetValue(area);
                    AddTabMethod.Invoke(area, new object[] { index, mine, true });
                    if (!selected) SelectedProperty.SetValue(area, shown >= index ? shown + 1 : shown);
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

        private static MixerHierarchyWindow FindMine()
        {
            MixerHierarchyWindow first = null;
            foreach (var window in Resources.FindObjectsOfTypeAll<MixerHierarchyWindow>())
            {
                if (window == null || IsHidden(window)) continue;
                var area = DockAreaOf(window);
                if (area == null || ReferenceEquals(ActualViewProperty.GetValue(area), window)) return window;
                first ??= window;
            }
            return first;
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
    }
}
