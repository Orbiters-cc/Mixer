using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// The VRChat SDK's "Check Console for more details" and "Show Console" buttons (its Builder's error notifications)
    /// open Unity's Console through Window › General › Console, which the Mixer console replaces a moment later: the
    /// Console flashed and closed, and the error the button meant was not shown. With "Replace console" on, a press on
    /// such a button in the SDK's panel opens the Mixer console instead (where it is, focused) with the newest error
    /// selected; the SDK's own handler never runs. The SDK is found by its window type's name: no dependency on it.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleLinks
    {
        // The windows whose console buttons are taken over (VRChat SDK's control panel, worlds and avatars alike).
        private static readonly string[] PanelTypeNames = { "VRCSdkControlPanel" };
        private static readonly Regex ConsoleText = new Regex(@"\b(?:check|show|open)\s+(?:the\s+)?(?:unity\s+)?console\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<VisualElement> Hooked = new HashSet<VisualElement>();
        private static List<Type> panelTypes;
        private static double nextScan;

        static ConsoleLinks()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.update += Scan;
        }

        /// <summary>Whether <paramref name="element"/> is (in) a button that opens the Console.</summary>
        internal static bool IsConsoleLink(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e is Button button)
                {
                    return button.name == "open-unity-console" || !string.IsNullOrEmpty(button.text) && ConsoleText.IsMatch(button.text);
                }
            }

            return false;
        }

        // Once a second: the SDK panels open now get the hook (once each).
        private static void Scan()
        {
            if (EditorApplication.timeSinceStartup < nextScan)
            {
                return;
            }

            nextScan = EditorApplication.timeSinceStartup + 1d;
            var types = PanelTypes();
            if (types.Count == 0)
            {
                // No VRChat SDK in this project (a script reload looks again).
                EditorApplication.update -= Scan;
                return;
            }

            Hooked.RemoveWhere(root => root.panel == null);
            if (!ConsoleReplacement.Enabled)
            {
                return;
            }

            foreach (var type in types)
            {
                foreach (var found in Resources.FindObjectsOfTypeAll(type))
                {
                    if (found is EditorWindow window && window != null)
                    {
                        Hook(window.rootVisualElement);
                    }
                }
            }
        }

        private static void Hook(VisualElement root)
        {
            if (root == null || root.panel == null || !Hooked.Add(root))
            {
                return;
            }

            // Before the button sees the press: it never starts its click.
            root.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            root.RegisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
        }

        private static void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button == 0 && Take(evt.target as VisualElement))
            {
                evt.PreventDefault();
                evt.StopImmediatePropagation();
            }
        }

        private static void OnMouseDown(MouseDownEvent evt)
        {
            if (evt.button == 0 && ConsoleReplacement.Enabled && IsConsoleLink(evt.target as VisualElement))
            {
                evt.PreventDefault();
                evt.StopImmediatePropagation();
            }
        }

        private static bool Take(VisualElement target)
        {
            if (!ConsoleReplacement.Enabled || !IsConsoleLink(target))
            {
                return false;
            }

            // Out of the panel's event: windows open and take the focus on the next editor tick.
            EditorApplication.update += ShowOnce;
            return true;
        }

        private static void ShowOnce()
        {
            EditorApplication.update -= ShowOnce;
            ConsoleReplacement.ShowLatestError();
        }

        private static List<Type> PanelTypes()
        {
            if (panelTypes != null)
            {
                return panelTypes;
            }

            panelTypes = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!name.StartsWith("VRC.", StringComparison.Ordinal) && !name.StartsWith("VRCSDK", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string typeName in PanelTypeNames)
                {
                    var type = assembly.GetType(typeName, false);
                    if (type != null && typeof(EditorWindow).IsAssignableFrom(type))
                    {
                        panelTypes.Add(type);
                    }
                }
            }

            return panelTypes;
        }
    }
}
