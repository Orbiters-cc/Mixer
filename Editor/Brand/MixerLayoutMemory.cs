using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The last window layout in which the Mixer Hierarchy, Project and console each had a place of their own, kept for
    /// every project of this computer. A project opened for the first time starts from the layout last used in any
    /// project; when that layout has lost them (it was saved by a project where they could not load, e.g. scripts that
    /// did not compile yet), there is no place left to put them back to, and opening them beside some other window piles
    /// all three into one dock. Instead the remembered layout is loaded (its script GUIDs put right for this project,
    /// <see cref="ForeignLayoutRepair"/>), so they come back where they were; with none remembered yet, Unity's default
    /// layout is loaded and each of its Hierarchy, Project and Console is replaced in place. Saved when the editor
    /// quits or reloads scripts, only while all three are open and not all in one dock.
    /// </summary>
    [InitializeOnLoad]
    public static class MixerLayoutMemory
    {
        /// <summary>The Mixer windows that take the place of Unity's Hierarchy, Project and Console.</summary>
        public static readonly string[] MixerWindowTypes =
        {
            "Orbiters.Mixer.Editor.MixerHierarchyWindow", "Orbiters.Mixer.Editor.MixerProjectWindow", "Orbiters.Logger.Editor.LoggerWindow",
        };

        private const string RestoredKey = "Orbiters.Mixer.LayoutMemory.Restored";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
        private static readonly Type DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        private static double checkStackedAt;
        private static bool restoreFailed;

        static MixerLayoutMemory()
        {
            if (Application.isBatchMode) return;
            EditorApplication.quitting += Remember;
            AssemblyReloadEvents.beforeAssemblyReload += Remember;
            checkStackedAt = EditorApplication.timeSinceStartup + 2;
            EditorApplication.update += CheckStacked;
        }

        /// <summary>Where the layout is kept: Unity's preferences folder, shared by every project of this Unity major version.</summary>
        public static string SnapshotPath =>
            Path.Combine(InternalEditorUtility.unityPreferencesFolder, "Orbiters", "Mixer last layout " + Application.unityVersion.Split('.')[0] + ".dwlt");

        /// <summary>
        /// All three Mixer windows are open, each docked, and not all in the same dock: a layout worth putting back.
        /// <paramref name="windows"/> are the open windows' full type names and dock areas (null: not docked).
        /// </summary>
        public static bool IsWorthRemembering(IEnumerable<(string Type, object Area)> windows)
        {
            var mine = windows.Where(w => MixerWindowTypes.Contains(w.Type)).ToList();
            if (MixerWindowTypes.Any(t => !mine.Any(w => w.Type == t && w.Area != null))) return false;
            return !IsStacked(mine);
        }

        /// <summary>All three Mixer windows are open and every one of them sits in the same dock.</summary>
        public static bool IsStacked(IEnumerable<(string Type, object Area)> windows)
        {
            var mine = windows.Where(w => MixerWindowTypes.Contains(w.Type)).ToList();
            if (MixerWindowTypes.Any(t => !mine.Any(w => w.Type == t))) return false;
            var first = mine[0].Area;
            return first != null && mine.All(w => ReferenceEquals(w.Area, first));
        }

        /// <summary>
        /// Puts the Mixer windows back where they were (the remembered layout, else Unity's default one with them in its
        /// windows' places), once per editor session. True when that is done or under way: the caller then opens nothing
        /// itself.
        /// </summary>
        public static bool Restore()
        {
            if (SessionState.GetBool(RestoredKey, false)) return !restoreFailed;
            SessionState.SetBool(RestoredKey, true);
            string source = File.Exists(SnapshotPath) ? SnapshotPath : FactoryDefaultLayout();
            if (source == null) { restoreFailed = true; return false; }
            bool remembered = source == SnapshotPath;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    string text = File.ReadAllText(source);
                    if (remembered) text = ForeignLayoutRepair.RemapForThisProject(text, out _);
                    string temp = Path.GetFullPath(Path.Combine("Temp", "Orbiters Mixer layout.dwlt"));
                    Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    File.WriteAllText(temp, text);
                    EditorUtility.LoadWindowLayout(temp);
                    Debug.Log(remembered
                        ? "[Mixer] The window layout this project opened with had lost the Hierarchy, Project and Console: your last layout with them is back (" + SnapshotPath + ")."
                        : "[Mixer] The window layout this project opened with had lost the Hierarchy, Project and Console: Unity's default layout is back, with the Mixer windows in their places.");
                }
                catch (Exception e)
                {
                    restoreFailed = true;
                    Debug.LogWarning("[Mixer] Couldn't put the window layout back (" + e.Message + "): use Window › Layouts to choose one.");
                }
            };
            return true;
        }

        // Unity's own default layout, as shipped with the editor.
        private static string FactoryDefaultLayout()
        {
            string path = Path.Combine(EditorApplication.applicationContentsPath, "Resources", "Layouts", "Default.wlt");
            return File.Exists(path) ? path : null;
        }

        private static List<(string Type, object Area)> OpenWindows()
        {
            var result = new List<(string, object)>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || MixerWindowPlacement.IsHidden(window)) continue;
                var parent = ParentField?.GetValue(window);
                result.Add((window.GetType().FullName, parent != null && DockAreaType != null && DockAreaType.IsInstanceOfType(parent) ? parent : null));
            }
            return result;
        }

        // The windows were opened beside each other by an older Mixer, or before this one could restore anything: in a
        // session started from another project's layout, all three in one dock is that pile, not a choice.
        private static void CheckStacked()
        {
            if (EditorApplication.timeSinceStartup < checkStackedAt || !MixerWindowPlacement.LayoutLoaded()) return;
            EditorApplication.update -= CheckStacked;
            try
            {
                if (ForeignLayoutRepair.StartedFromAnotherProjectsLayout && IsStacked(OpenWindows())) Restore();
            }
            catch (Exception) { }
        }

        private static void Remember()
        {
            try
            {
                if (!MixerWindowPlacement.LayoutLoaded() || !IsWorthRemembering(OpenWindows())) return;
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
                var save = type?.GetMethods(Static).FirstOrDefault(m => m.Name == "SaveWindowLayout" &&
                    m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string) &&
                    m.GetParameters().Skip(1).All(p => p.HasDefaultValue));
                if (save == null) return;
                string path = SnapshotPath, temp = path + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var args = new object[save.GetParameters().Length];
                args[0] = temp;
                for (int i = 1; i < args.Length; i++) args[i] = save.GetParameters()[i].DefaultValue;
                save.Invoke(null, args);
                if (!File.Exists(temp)) return;
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception)
            {
                // Remembering is best effort: the last remembered layout stays.
            }
        }
    }
}
