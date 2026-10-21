using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What Unity and other tools send to Unity's Hierarchy and Project windows, answered by Mixer's, which replace them
    /// (<see cref="DockedWindowReplacement"/>):
    /// <list type="bullet">
    /// <item><c>EditorGUIUtility.PingObject</c> (an Inspector field's click, the Console, any tool's "show in Project"):
    /// Unity pings every Hierarchy in its list. A Hierarchy made and destroyed at once stays in that list as a listener:
    /// nothing draws it, no <c>GetWindow</c> finds it (so Window › General › Hierarchy still opens a real one), and a ping
    /// only writes the object's ID into it, read here every editor update and shown by the Mixer Hierarchy (a scene
    /// object) or the Mixer Project (an asset).</item>
    /// <item>The listener is also Unity's "last clicked" Hierarchy while there is no other: Unity's scene visibility
    /// shortcuts (H, L) and Create Empty Parent read that one without checking it exists.</item>
    /// <item>Find References In Scene gives every Scene view a <c>ref:</c> search: it shows in the Mixer Hierarchy's search
    /// too, and clearing it there clears the Scene views.</item>
    /// <item><c>EditorApplication.RepaintHierarchyWindow</c> (a tool's row decorations changed) repaints the Mixer
    /// Hierarchy's rows.</item>
    /// </list>
    /// All of it is Unity's internals, by reflection: a Unity version without them leaves Mixer's windows as they were (only
    /// Mixer's own pings reach them, through <see cref="MixerPing"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityWindowHooks
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // The listener's window GUID: Unity saves a Hierarchy's folds under it when one goes, so one file, always the same.
        private const string ListenerGuid = "6d1f0c5b9a3e4f7e8b2c4a1d0e9f8a7b";

        private static readonly Type HierarchyWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        private static readonly FieldInfo WindowsField, LastInteractedField, SceneHierarchyField, WindowGuidField;
        private static readonly FieldInfo TreeViewStateField, HierarchySearchField;
        private static readonly Func<object, int> FrameRequest;
        private static readonly Func<object, bool> FrameRequestPing;
        private static readonly Action<object, int> SetFrameRequest;
        private static readonly Func<object, string> WindowSearch;
        private static readonly MethodInfo SetSearchFilterMethod;
        private static readonly bool HooksReady;

        private static EditorWindow listener;
        private static object listenerHierarchy;
        private static bool failed;
        private static double nextKeep;
        private static string sceneViewSearch = string.Empty;

        /// <summary>A ping or a frame for a scene object (true: a ping) or an asset (pings only).</summary>
        internal static event Action<GameObject, bool> HierarchyPinged;
        internal static event Action<string> ProjectPinged;
        /// <summary>Find References In Scene (a <c>ref:</c> search) started, or ended without the Mixer Hierarchy (a ping clears it).</summary>
        internal static event Action<string> ReferenceSearchChanged;
        /// <summary>A tool asked Unity's Hierarchy to repaint (its row decorations changed).</summary>
        internal static event Action HierarchyRepaint;

        /// <summary>Tests: only these windows hear what Unity sends, the user's own are left as they are (null: all).</summary>
        internal static ICollection<EditorWindow> OnlyTo;
        internal static bool Reaches(EditorWindow window) => OnlyTo == null || OnlyTo.Contains(window);

        static UnityWindowHooks()
        {
            try
            {
                WindowsField = HierarchyWindowType?.GetField("s_SceneHierarchyWindows", Static);
                LastInteractedField = HierarchyWindowType?.GetField("s_LastInteractedHierarchy", Static);
                SceneHierarchyField = HierarchyWindowType?.GetField("m_SceneHierarchy", Instance);
                WindowGuidField = HierarchyWindowType?.GetField("m_WindowGUID", Instance);
                var hierarchyType = SceneHierarchyField?.FieldType;
                TreeViewStateField = hierarchyType?.GetField("m_TreeViewState", Instance);
                HierarchySearchField = hierarchyType?.GetField("m_SearchFilter", Instance);
                var frameId = hierarchyType?.GetField("m_FrameRequestID", Instance);
                var framePing = hierarchyType?.GetField("m_FrameRequestPing", Instance);
                if (WindowsField != null && LastInteractedField != null && WindowGuidField != null && TreeViewStateField?.FieldType == typeof(TreeViewState)
                    && HierarchySearchField?.FieldType == typeof(string) && frameId?.FieldType == typeof(int) && framePing?.FieldType == typeof(bool))
                {
                    // Read every editor update: compiled, so nothing is boxed.
                    FrameRequest = Getter<int>(frameId);
                    FrameRequestPing = Getter<bool>(framePing);
                    SetFrameRequest = Setter<int>(frameId);
                    HooksReady = true;
                }
            }
            catch (Exception) { HooksReady = false; }
            try
            {
                var windowSearch = typeof(SearchableEditorWindow).GetField("m_SearchFilter", Instance);
                SetSearchFilterMethod = typeof(SearchableEditorWindow).GetMethod("SetSearchFilter", Instance, null,
                    new[] { typeof(string), typeof(SearchableEditorWindow.SearchMode), typeof(bool), typeof(bool) }, null);
                if (windowSearch?.FieldType == typeof(string) && SetSearchFilterMethod != null) WindowSearch = Getter<string>(windowSearch);
            }
            catch (Exception) { WindowSearch = null; }
            try
            {
                var refresh = typeof(EditorApplication).GetField("refreshHierarchy", Static);
                if (refresh?.FieldType == typeof(EditorApplication.CallbackFunction))
                    refresh.SetValue(null, (EditorApplication.CallbackFunction)refresh.GetValue(null) + OnRefreshHierarchy);
            }
            catch (Exception) { }
            if (Application.isBatchMode) return;
            EditorApplication.update += Update;
        }

        /// <summary>Whether Unity's pings reach the Mixer windows through the listener (else <see cref="MixerPing"/> tells them).</summary>
        internal static bool ListensToPings => HooksReady && !failed && listener is object && WindowsField.GetValue(null) is IList list && list.Contains(listener);

        private static Func<object, T> Getter<T>(FieldInfo field)
        {
            var target = Expression.Parameter(typeof(object), "target");
            return Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(target, field.DeclaringType), field), target).Compile();
        }

        private static Action<object, T> Setter<T>(FieldInfo field)
        {
            var target = Expression.Parameter(typeof(object), "target");
            var value = Expression.Parameter(typeof(T), "value");
            return Expression.Lambda<Action<object, T>>(Expression.Assign(Expression.Field(Expression.Convert(target, field.DeclaringType), field), value), target, value).Compile();
        }

        private static void OnRefreshHierarchy() => HierarchyRepaint?.Invoke();

        private static void Update()
        {
            Poll();
            double now = EditorApplication.timeSinceStartup;
            if (now < nextKeep) return;
            nextKeep = now + 0.5;
            Keep();
        }

        /// <summary>What Unity sent since the last update, given to the Mixer windows (tests call it instead of waiting).</summary>
        internal static void Poll()
        {
            if (listenerHierarchy != null)
            {
                int id = FrameRequest(listenerHierarchy);
                if (id != 0)
                {
                    bool ping = FrameRequestPing(listenerHierarchy);
                    SetFrameRequest(listenerHierarchy, 0);
                    Route(id, ping);
                }
            }
            PollSceneViewSearch();
        }

        // ---- The listener ----------------------------------------------------------------------------------------------

        /// <summary>Makes the listener when there is none (after a script reload Unity's list is a new one), keeps it last in
        /// Unity's list (Unity takes the first Hierarchy when it wants any) and Unity's last clicked Hierarchy while there is no
        /// other.</summary>
        internal static void Keep()
        {
            if (!HooksReady || failed) return;
            if (!(WindowsField.GetValue(null) is IList list)) return;
            if (listener is null || !list.Contains(listener)) Make(list);
            if (listener is null) return;
            if (list.Count > 1 && !ReferenceEquals(list[list.Count - 1], listener))
            {
                list.Remove(listener);
                list.Add(listener);
            }
            var last = LastInteractedField.GetValue(null) as EditorWindow;
            // A real Hierarchy (alive) stays Unity's; a closed one, or none, gives way to the listener.
            if (last == null && !ReferenceEquals(last, listener)) LastInteractedField.SetValue(null, listener);
        }

        private static void Make(IList list)
        {
            var lastBefore = LastInteractedField.GetValue(null) as EditorWindow;
            EditorWindow window = null;
            try
            {
                window = (EditorWindow)ScriptableObject.CreateInstance(HierarchyWindowType);
                window.hideFlags = HideFlags.HideAndDontSave;
                var hierarchy = SceneHierarchyField.GetValue(window);
                // State of its own, for what Unity reads from a Hierarchy: the folds it saves when one goes, the scenes picked
                // in it (visibility shortcuts). An empty search, so a ping's "clear the search" changes nothing in it.
                TreeViewStateField.SetValue(hierarchy, new TreeViewState());
                HierarchySearchField.SetValue(hierarchy, string.Empty);
                WindowGuidField.SetValue(window, ListenerGuid);
                Object.DestroyImmediate(window);
                list.Add(window);
                listener = window;
                listenerHierarchy = hierarchy;
            }
            catch (Exception exception)
            {
                failed = true;
                listener = null;
                listenerHierarchy = null;
                if (window is object)
                {
                    list.Remove(window);
                    if (window != null) Object.DestroyImmediate(window);
                }
                var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                Debug.LogWarning("[Mixer] Unity's pings cannot reach the Mixer Hierarchy and Project in this Unity version (" + cause.Message + "); Mixer's own still do.");
            }
            LastInteractedField.SetValue(null, lastBefore != null ? lastBefore : listener);
        }

        // A ping (or Unity framing an object, a prefab's root when Prefab Mode opens) for the Mixer windows.
        private static void Route(int instanceId, bool ping)
        {
            var target = EditorUtility.InstanceIDToObject(instanceId);
            if (target == null) return;
            var go = target as GameObject;
            if (go == null && target is Component component) go = component.gameObject;
            if (go != null && go.scene.IsValid())
            {
                HierarchyPinged?.Invoke(go, ping);
                return;
            }
            if (!ping) return;
            string path = AssetDatabase.GetAssetPath(target);
            if (!string.IsNullOrEmpty(path)) ProjectPinged?.Invoke(path);
        }

        // ---- Find References In Scene --------------------------------------------------------------------------------------

        internal static bool IsReferenceSearch(string text) => text != null && text.TrimStart().StartsWith("ref:", StringComparison.OrdinalIgnoreCase);

        // Unity gives every Scene view (and every Hierarchy) the search; the first Scene view says what it is now.
        private static void PollSceneViewSearch()
        {
            if (WindowSearch == null) return;
            var view = SceneView.lastActiveSceneView;
            if (view == null && SceneView.sceneViews.Count > 0) view = SceneView.sceneViews[0] as SceneView;
            if (view == null) return;
            string current = WindowSearch(view) ?? string.Empty;
            if (ReferenceEquals(current, sceneViewSearch) || current == sceneViewSearch) return;
            bool was = IsReferenceSearch(sceneViewSearch);
            sceneViewSearch = current;
            if (IsReferenceSearch(current)) ReferenceSearchChanged?.Invoke(current);
            else if (was) ReferenceSearchChanged?.Invoke(string.Empty);
        }

        /// <summary>The Mixer Hierarchy's search left a <c>ref:</c> search Unity gave it: the Scene views it greys show
        /// everything again.</summary>
        internal static void ClearReferenceSearch()
        {
            if (WindowSearch == null) return;
            foreach (var view in SceneView.sceneViews)
            {
                if (!(view is SceneView sceneView) || !IsReferenceSearch(WindowSearch(sceneView))) continue;
                try { SetSearchFilterMethod.Invoke(sceneView, new object[] { string.Empty, SearchableEditorWindow.SearchMode.All, false, false }); }
                catch (Exception) { }
            }
            sceneViewSearch = string.Empty;
        }

        /// <summary>The search the Scene views hold now (tests).</summary>
        internal static string SceneViewSearch(SceneView view) => WindowSearch != null && view != null ? WindowSearch(view) : null;
    }
}
