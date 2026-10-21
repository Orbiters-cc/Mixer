using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // What Unity and other tools send to Unity's Hierarchy, answered here (UnityWindowHooks): a ping opens the way to the
    // object and flashes its row; Find References In Scene's ref: search shows in the search field (leaving it clears the
    // Scene views Unity greyed); other tools' row decorations (EditorApplication.hierarchyWindowItemOnGUI: icons, toggles)
    // draw on each row's name line, as in Unity's Hierarchy.
    internal sealed partial class MixerHierarchyWindow
    {
        // Unity's Hierarchy rows are 16 points high: decorations are drawn for one, in the middle of the name's line.
        private const float DecorationHeight = 16f;

        [NonSerialized] private int pingId;
        private double pingUntil;
        // The ref: search Unity gave this window, while the field still shows it.
        [NonSerialized] private string unitySearch;
        [NonSerialized] private bool decorated;

        /// <summary>The object whose row flashes after a ping (tests).</summary>
        internal int PingedId => pingId;
        /// <summary>What the search field holds (tests).</summary>
        internal string Search => search;

        private void HookUnity()
        {
            UnityWindowHooks.HierarchyPinged += OnUnityPing;
            UnityWindowHooks.ReferenceSearchChanged += OnReferenceSearch;
            UnityWindowHooks.HierarchyRepaint += RepaintDecorations;
        }

        private void UnhookUnity()
        {
            UnityWindowHooks.HierarchyPinged -= OnUnityPing;
            UnityWindowHooks.ReferenceSearchChanged -= OnReferenceSearch;
            UnityWindowHooks.HierarchyRepaint -= RepaintDecorations;
        }

        // ---- Pings -------------------------------------------------------------------------------------------------------

        // Unity's ping (true) or frame (Prefab Mode opening on its root) of a scene object.
        private void OnUnityPing(GameObject go, bool ping)
        {
            if (go == null || !UnityWindowHooks.Reaches(this)) return;
            RevealObject(go);
            if (!ping) return;
            pingId = go.GetInstanceID();
            pingUntil = EditorApplication.timeSinceStartup + 1.6;
            RefreshPing();
        }

        // Only the rows' flash changes.
        private void RefreshPing()
        {
            if (!built) return;
            foreach (var element in list.Rows)
            {
                if (element.resolvedStyle.display == DisplayStyle.None) continue;
                var row = RowOf(element);
                element.EnableInClassList("mxh-row--ping", pingId != 0 && row != null && !row.IsScene && row.InstanceId == pingId);
            }
        }

        private void TickHooks()
        {
            if (pingId != 0 && EditorApplication.timeSinceStartup > pingUntil) { pingId = 0; RefreshPing(); }
            // A tool registered (or removed) its row decorations since the rows were bound.
            bool decorations = EditorApplication.hierarchyWindowItemOnGUI != null;
            if (decorated == decorations) return;
            decorated = decorations;
            list.RefreshRows();
        }

        // ---- Find References In Scene ------------------------------------------------------------------------------------

        private void OnReferenceSearch(string query)
        {
            if (!built || !UnityWindowHooks.Reaches(this)) return;
            if (string.IsNullOrEmpty(query))
            {
                // Unity let its search go (a ping clears it): the field does too, unless something else was typed since.
                if (unitySearch != null && search == unitySearch) ShowSearch(string.Empty);
                unitySearch = null;
                return;
            }
            unitySearch = query;
            ShowSearch(query);
        }

        private void ShowSearch(string text)
        {
            // Unity's search is plain words: the regular expression switch goes off for it (not for later searches).
            if (searchOptions != null && searchOptions.Regex) searchOptions.Set(false, searchOptions.MatchCase, remember: false);
            searchField.SetValueWithoutNotify(text);
            search = text;
            SyncFilter();
            RebuildNow();
        }

        // The field no longer shows the search Unity gave it: the Scene views it greyed show everything again.
        private void LeaveUnitySearch()
        {
            if (unitySearch == null || search == unitySearch) return;
            unitySearch = null;
            UnityWindowHooks.ClearReferenceSearch();
        }

        // ---- Other tools' row decorations ----------------------------------------------------------------------------------

        // Drawn under the name's line, so the row's own controls stay on top; a click a decoration does not use goes on to
        // the row (selection, drag, context menu).
        private IMGUIContainer MakeDecorations(VisualElement row, VisualElement components, VisualElement switches)
        {
            var container = new IMGUIContainer { focusable = false };
            container.AddToClassList("mxh-row__decorations");
            container.onGUIHandler = () => DrawDecorations(row, container, components, switches);
            return container;
        }

        // Unity gives decorations the row's whole width, and tools draw at its right end: here that end is where the row's
        // component icons and switches start.
        private void DrawDecorations(VisualElement element, IMGUIContainer container, VisualElement components, VisualElement switches)
        {
            var callback = EditorApplication.hierarchyWindowItemOnGUI;
            var row = callback != null ? RowOf(element) : null;
            if (row == null) return;
            float right = container.layout.width;
            if (components.childCount > 0 && components.resolvedStyle.display != DisplayStyle.None) right = Mathf.Min(right, components.layout.xMin);
            if (switches.layout.width > 0 && switches.resolvedStyle.display != DisplayStyle.None) right = Mathf.Min(right, switches.layout.xMin);
            float top = Mathf.Max(0f, (container.layout.height - DecorationHeight) / 2f);
            callback(row.InstanceId, new Rect(0f, top, Mathf.Max(0f, right), DecorationHeight));
        }

        private void BindDecorations(VisualElement element)
        {
            decorated = EditorApplication.hierarchyWindowItemOnGUI != null;
            var container = element.Q<IMGUIContainer>(className: "mxh-row__decorations");
            if (container == null) return;
            MixerUi.Show(container, decorated);
            if (decorated) container.MarkDirtyRepaint();
        }

        // EditorApplication.RepaintHierarchyWindow: only the decorations draw again.
        private void RepaintDecorations()
        {
            if (!built || !decorated) return;
            foreach (var element in list.Rows)
                if (element.resolvedStyle.display != DisplayStyle.None) element.Q<IMGUIContainer>(className: "mxh-row__decorations")?.MarkDirtyRepaint();
        }
    }
}
