using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Window layouts made in another project. Orbiters packages ship without <c>.meta</c> files, so each project gives
    /// their scripts GUIDs of its own; a layout names a window's script by GUID. A new project (or one whose UserSettings
    /// were deleted) opens with the layout last used in any project, and a package update re-imported with new GUIDs
    /// leaves the project's own layout pointing at the old ones: either way Unity drops the Mixer windows it cannot find
    /// (the Mixer Hierarchy, Project and console, where Unity's own were), and their place is gone. Before the layout is
    /// loaded at the start of the editor, the layout Unity is about to load is read: each window of an Orbiters script
    /// this project doesn't know is matched, by its type's name and serialized fields, with the window type of this
    /// project, and the layout with those GUIDs put right is written as the project's own (UserSettings/Layouts), which
    /// Unity then loads: the windows come back where they were. Nothing changes when every script is known.
    /// </summary>
    [InitializeOnLoad]
    public static class ForeignLayoutRepair
    {
        private const string CheckedKey = "Orbiters.Mixer.LayoutRepair.Checked";
        private const string ForeignKey = "Orbiters.Mixer.LayoutRepair.Foreign";
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>A window type a layout's script GUID can be put right to: its name, script GUID and serialized fields.</summary>
        public sealed class WindowScript
        {
            public string TypeName;
            public string Guid;
            public HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal);
        }

        static ForeignLayoutRepair()
        {
            if (Application.isBatchMode || MixerWindowPlacement.LayoutLoaded()) return;
            try
            {
                if (!SessionState.GetBool(CheckedKey, false))
                {
                    SessionState.SetBool(CheckedKey, true);
                    string own = ProjectLayoutPath();
                    SessionState.SetBool(ForeignKey, own == null || !File.Exists(own));
                }
                Repair();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mixer] Couldn't check the window layout this project opens with (" + e.Message + "): Mixer windows it can't find stay closed.");
            }
        }

        /// <summary>
        /// This editor session started from a layout that was not this project's own (none was saved here yet): windows
        /// it lacks were never closed in this project.
        /// </summary>
        public static bool StartedFromAnotherProjectsLayout => SessionState.GetBool(ForeignKey, false);

        // Before the layout is loaded (this domain load runs before Unity loads it at the start of the editor).
        private static void Repair()
        {
            string layout = LayoutToLoad();
            if (layout == null || !File.Exists(layout)) return;
            string text = File.ReadAllText(layout);
            List<WindowScript> scripts = null;
            string repaired = Remap(text, IsKnown, () => scripts ?? (scripts = OrbitersWindowScripts()), out var remapped);
            if (remapped.Count == 0) return;
            string own = ProjectLayoutPath();
            if (own == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(own));
            string temp = own + ".mixer.tmp";
            File.WriteAllText(temp, repaired);
            if (File.Exists(own)) File.Delete(own);
            File.Move(temp, own);
            Debug.Log("[Mixer] The window layout this project opens with was made in another project: " + string.Join(", ", remapped.Distinct()) +
                      " " + (remapped.Count == 1 ? "is" : "are") + " put back where " + (remapped.Count == 1 ? "it was" : "they were") + ".");
        }

        private static bool IsKnown(string guid)
        {
            try
            {
                return !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static readonly Regex DocumentStart = new Regex(@"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ScriptLine = new Regex(@"^  m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: (\d+)\}[ \t\r]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex NameLine = new Regex(@"^  m_Name: ?(.*?)[ \t\r]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ActualViewLine = new Regex(@"^  m_ActualView: \{fileID: (-?\d+)\}", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex KeyLine = new Regex(@"^  ([A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// The layout with the script GUID of each window this project doesn't know (<paramref name="isKnown"/>) changed to
        /// the one <paramref name="scripts"/> has for that window: the type a dock area names for it when it is the area's
        /// shown tab and its fields fit, else the only type whose fields hold every field the window has. Windows that fit
        /// none, or more than one, stay as they are. <paramref name="remapped"/> lists the types put right.
        /// </summary>
        public static string Remap(string layout, Func<string, bool> isKnown, Func<IList<WindowScript>> scripts, out List<string> remapped)
        {
            remapped = new List<string>();
            if (string.IsNullOrEmpty(layout)) return layout;
            var starts = DocumentStart.Matches(layout).Cast<Match>().ToList();
            if (starts.Count == 0) return layout;
            var documents = new List<(string id, int start, int end)>();
            for (int i = 0; i < starts.Count; i++)
                documents.Add((starts[i].Groups[1].Value, starts[i].Index, i + 1 < starts.Count ? starts[i + 1].Index : layout.Length));

            // What each dock area says its shown tab is (it is named after the tab's type).
            var shownAs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, start, end) in documents)
            {
                string body = layout.Substring(start, end - start);
                var view = ActualViewLine.Match(body);
                var name = NameLine.Match(body);
                if (view.Success && name.Success && name.Groups[1].Value.Length > 0) shownAs[view.Groups[1].Value] = name.Groups[1].Value;
            }

            var edits = new List<(int at, int length, string text)>();
            IList<WindowScript> known = null;
            foreach (var (id, start, end) in documents)
            {
                string body = layout.Substring(start, end - start);
                var script = ScriptLine.Match(body);
                if (!script.Success || script.Groups[3].Value != "3" || script.Groups[1].Value != "11500000") continue;
                string guid = script.Groups[2].Value;
                // The types are looked up only once a script is unknown (most starts have none).
                if (isKnown(guid)) continue;
                known = known ?? scripts() ?? new List<WindowScript>();
                if (known.Any(s => s.Guid == guid)) continue;
                var fields = new HashSet<string>(KeyLine.Matches(body).Cast<Match>().Select(m => m.Groups[1].Value).Where(k => !k.StartsWith("m_", StringComparison.Ordinal) && k != "references"), StringComparer.Ordinal);
                bool Fits(WindowScript s) => fields.All(s.Fields.Contains);
                WindowScript match = null;
                if (shownAs.TryGetValue(id, out string typeName))
                {
                    var named = known.Where(s => s.TypeName == typeName).ToList();
                    if (named.Count == 1 && Fits(named[0])) match = named[0];
                }
                if (match == null && fields.Count > 0)
                {
                    var fitting = known.Where(Fits).ToList();
                    if (fitting.Count == 1) match = fitting[0];
                }
                if (match == null) continue;
                var guidGroup = script.Groups[2];
                edits.Add((start + guidGroup.Index, guidGroup.Length, match.Guid));
                remapped.Add(match.TypeName);
            }

            if (edits.Count == 0) return layout;
            var result = new StringBuilder(layout);
            foreach (var (at, length, text) in edits.OrderByDescending(e => e.at))
            {
                result.Remove(at, length);
                result.Insert(at, text);
            }
            return result.ToString();
        }

        /// <summary>The fields Unity can write for a window type: its own and its base types', up to EditorWindow.</summary>
        public static HashSet<string> FieldsOf(Type type)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            for (var t = type; t != null && t != typeof(EditorWindow) && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    fields.Add(field.Name);
            return fields;
        }

        // The window types of the Orbiters packages, each with its script's GUID in this project (read from its .meta).
        private static List<WindowScript> OrbitersWindowScripts()
        {
            var result = new List<WindowScript>();
            var sources = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                if (assembly.name.StartsWith("Orbiters.", StringComparison.Ordinal)) sources[assembly.name] = assembly.sourceFiles;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = loaded.GetName().Name;
                if (!sources.TryGetValue(name, out var files)) continue;
                Type[] types;
                try { types = loaded.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var type in types)
                {
                    if (type.IsAbstract || type.DeclaringType != null || !typeof(EditorWindow).IsAssignableFrom(type)) continue;
                    string file = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), type.Name, StringComparison.Ordinal));
                    string guid = file != null ? GuidOf(file) : null;
                    if (guid != null) result.Add(new WindowScript { TypeName = type.Name, Guid = guid, Fields = FieldsOf(type) });
                }
            }
            return result;
        }

        private static string GuidOf(string file)
        {
            try
            {
                string meta = file + ".meta";
                if (!File.Exists(meta)) return null;
                foreach (string line in File.ReadLines(meta))
                    if (line.StartsWith("guid: ", StringComparison.Ordinal)) return line.Substring(6).Trim();
            }
            catch (Exception) { }
            return null;
        }

        // The project's own layout (UserSettings/Layouts/<mode>-<version>.dwlt).
        private static string ProjectLayoutPath()
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
                if (type?.GetProperty("ProjectLayoutPath", Static)?.GetValue(null) is string path && path.Length > 0) return Path.GetFullPath(path);
            }
            catch (Exception) { }
            string major = Application.unityVersion.Split('.')[0];
            return Path.GetFullPath(Path.Combine("UserSettings", "Layouts", "default-" + major + ".dwlt"));
        }

        // The layout Unity loads at the start: the first of its own list of last layouts (the project's, else the one last
        // used in any project).
        private static string LayoutToLoad()
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
                var last = type?.GetMethod("GetLastLayout", Static, null, Type.EmptyTypes, null);
                if (last?.Invoke(null, null) is IEnumerable paths)
                    foreach (var path in paths)
                        if (path is string file && File.Exists(file)) return file;
            }
            catch (Exception) { }
            string own = ProjectLayoutPath();
            return own != null && File.Exists(own) ? own : null;
        }
    }

    /// <summary>Puts a Mixer window where a window of a kind it belongs with is docked, through Unity's internal DockArea.</summary>
    public static class MixerWindowPlacement
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        private static readonly FieldInfo ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
        private static readonly FieldInfo PanesField = DockAreaType?.GetField("m_Panes", Instance);
        private static readonly PropertyInfo SelectedProperty = DockAreaType?.GetProperty("selected", Instance);
        private static readonly MethodInfo AddTabMethod = DockAreaType?.GetMethod("AddTab", Instance, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
        private static readonly Type ContainerType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");

        /// <summary>The editor's main window is up (the layout is loaded).</summary>
        public static bool LayoutLoaded()
        {
            try
            {
                if (ContainerType?.GetProperty("mainWindow", Static)?.GetValue(null) != null) return true;
                var showMode = ContainerType?.GetProperty("showMode", Instance);
                if (ContainerType?.GetProperty("windows", Static)?.GetValue(null) is Array windows && showMode != null)
                    foreach (var window in windows)
                        if (window != null && showMode.GetValue(window)?.ToString() == "MainWindow") return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Copies tools open outside every display to take screenshots: not the user's windows.</summary>
        public static bool IsHidden(Object window) => (window.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave;

        /// <summary>Whether a window of the type named <paramref name="typeName"/> (full name) is open, hidden copies left out.</summary>
        public static bool AnyOpen(string typeName)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window != null && !IsHidden(window) && window.GetType().FullName == typeName) return true;
            return false;
        }

        /// <summary>
        /// Opens <paramref name="window"/> as the last tab beside the first docked window of <paramref name="besideTypeNames"/>
        /// (full type names, in order of preference), without changing the tab that area shows; as a window of its own when
        /// none is docked. True when it was docked.
        /// </summary>
        public static bool OpenBeside(EditorWindow window, params string[] besideTypeNames)
        {
            if (window == null) return false;
            try
            {
                if (DockAreaType != null && ParentField != null && PanesField != null && SelectedProperty != null && AddTabMethod != null)
                {
                    var open = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null && w != window && !IsHidden(w)).ToList();
                    foreach (string typeName in besideTypeNames)
                    {
                        foreach (var other in open.Where(w => w.GetType().FullName == typeName))
                        {
                            var area = ParentField.GetValue(other);
                            if (area == null || !DockAreaType.IsInstanceOfType(area) || !(PanesField.GetValue(area) is IList panes)) continue;
                            int shown = (int)SelectedProperty.GetValue(area);
                            AddTabMethod.Invoke(area, new object[] { panes.Count, window, true });
                            SelectedProperty.SetValue(area, shown);
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            window.Show();
            return false;
        }
    }
}
