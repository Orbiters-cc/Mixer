using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Unity's create items run from the Mixer Project (its "+" and Create menus) create in the folder asked for and are
    /// named in place, as in Unity's Project window. Those items (Assets › Create, CreateAssetMenu, ProjectWindowUtil's
    /// create functions) ask Unity's last clicked ProjectBrowser for its folder and hand it the new asset to name. While one
    /// runs from here, a ProjectBrowser nobody sees stands in: it shows the folder asked for, and the naming it is handed
    /// comes to the Mixer Project (<see cref="Pending"/>), which ends it with Unity's own end-of-naming step. For that
    /// moment it is a tab of the Mixer Project's dock area (taken out before anything draws), so Unity code that focuses
    /// "the Project window" (<c>EditorUtility.FocusProjectWindow</c>) finds a real one.
    /// <para>The stand-in lives only for the menu item: Unity's main menu Assets › Create used anywhere else still makes
    /// its asset in Assets, unnamed. A stand-in kept for good would make <c>FocusProjectWindow</c> (NDMF's error window,
    /// TextMesh Pro) throw, since it sends an event to the window's parent, and Window › General › Project would show it.</para>
    /// </summary>
    internal static class UnityCreateFlow
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type BrowserType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");
        private static readonly Type SearchFilterType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SearchFilter");
        private static readonly Type DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        private static readonly FieldInfo LastInteracted, SearchFilterField, LastProjectPath, ListAreaState, ParentField, PanesField;
        private static readonly PropertyInfo Folders;
        private static readonly MethodInfo RemoveTab;
        // CreateAssetUtility and ObjectListAreaState: what a naming in Unity's window holds.
        private static readonly FieldInfo CreateUtility, RenameOverlay, NewAssetIndex, EndActionField, InstanceIdField, PathField, IconField, ResourceFileField;
        private static readonly MethodInfo UtilityClear, OverlayClear;

        static UnityCreateFlow()
        {
            try
            {
                LastInteracted = BrowserType?.GetField("s_LastInteractedProjectBrowser", Static);
                SearchFilterField = BrowserType?.GetField("m_SearchFilter", Instance);
                LastProjectPath = BrowserType?.GetField("m_LastProjectPath", Instance);
                ListAreaState = BrowserType?.GetField("m_ListAreaState", Instance);
                Folders = SearchFilterType?.GetProperty("folders", Instance);
                ParentField = typeof(EditorWindow).GetField("m_Parent", Instance);
                PanesField = DockAreaType?.GetField("m_Panes", Instance);
                RemoveTab = DockAreaType?.GetMethod("RemoveTab", Instance, null, new[] { typeof(EditorWindow), typeof(bool), typeof(bool) }, null);
                var stateType = ListAreaState?.FieldType;
                CreateUtility = stateType?.GetField("m_CreateAssetUtility", Instance);
                RenameOverlay = stateType?.GetField("m_RenameOverlay", Instance);
                NewAssetIndex = stateType?.GetField("m_NewAssetIndexInList", Instance);
                var utilityType = CreateUtility?.FieldType;
                EndActionField = utilityType?.GetField("m_EndAction", Instance);
                InstanceIdField = utilityType?.GetField("m_InstanceID", Instance);
                PathField = utilityType?.GetField("m_Path", Instance);
                IconField = utilityType?.GetField("m_Icon", Instance);
                ResourceFileField = utilityType?.GetField("m_ResourceFile", Instance);
                UtilityClear = utilityType?.GetMethod("Clear", Instance, null, Type.EmptyTypes, null);
                OverlayClear = RenameOverlay?.FieldType.GetMethod("Clear", Instance, null, Type.EmptyTypes, null);
                Available = LastInteracted != null && SearchFilterField?.FieldType == SearchFilterType && Folders != null && LastProjectPath?.FieldType == typeof(string)
                            && ListAreaState != null && CreateUtility != null && RenameOverlay != null && NewAssetIndex?.FieldType == typeof(int)
                            && EndActionField?.FieldType == typeof(EndNameEditAction) && InstanceIdField?.FieldType == typeof(int) && PathField?.FieldType == typeof(string)
                            && IconField != null && ResourceFileField?.FieldType == typeof(string) && UtilityClear != null && OverlayClear != null;
            }
            catch (Exception)
            {
                Available = false;
            }
        }

        /// <summary>Whether this Unity version has what the stand-in needs (else Unity's items create where they used to).</summary>
        internal static bool Available { get; }

        /// <summary>A naming Unity handed the stand-in: the asset is made (or not) when the Mixer Project's rename ends.</summary>
        internal sealed class Pending
        {
            public EndNameEditAction Action;
            public int InstanceId;
            public string Folder, Name, Extension, ResourceFile;
            public Texture2D Icon;
            public bool IsFolder;
        }

        /// <summary>
        /// Runs <paramref name="run"/> (a create item) with the stand-in showing <paramref name="folder"/> beside
        /// <paramref name="host"/>; returns the naming Unity handed it, or null (nothing was handed: the item made its asset
        /// itself, or asked where to save).
        /// </summary>
        internal static Pending Run(EditorWindow host, string folder, Action run)
        {
            if (!Available) { run(); return null; }
            var before = LastInteracted.GetValue(null) as EditorWindow;
            EditorWindow standIn = null;
            object area = null;
            try
            {
                standIn = (EditorWindow)ScriptableObject.CreateInstance(BrowserType);
                standIn.hideFlags = HideFlags.HideAndDontSave;
                // Its folder, and this project as the one it was set up for (else its first set-up would forget the folder).
                LastProjectPath.SetValue(standIn, Directory.GetCurrentDirectory());
                var filter = Activator.CreateInstance(SearchFilterType, true);
                Folders.SetValue(filter, new[] { folder });
                SearchFilterField.SetValue(standIn, filter);
                LastInteracted.SetValue(null, standIn);
                area = Dock(host, standIn);
                run();
                return Take(standIn);
            }
            finally
            {
                if (area != null) Undock(area, standIn);
                if (standIn != null) Object.DestroyImmediate(standIn);
                // A Project window of Unity's opened on purpose beside the Mixer one stays Unity's last clicked.
                if (before != null) LastInteracted.SetValue(null, before);
            }
        }

        // A tab of the host's dock area, not selected (nothing changes on screen unless Unity focuses it, and it is taken out
        // before anything draws). Only while the host has the focus (its menu was just used): Unity focusing the stand-in
        // then leaves the focus in the same place.
        private static object Dock(EditorWindow host, EditorWindow standIn)
        {
            if (host == null || EditorWindow.focusedWindow != host || ParentField == null || PanesField == null || RemoveTab == null) return null;
            var area = ParentField.GetValue(host);
            if (area == null || !DockAreaType.IsInstanceOfType(area) || !(PanesField.GetValue(area) is IList panes)) return null;
            panes.Add(standIn);
            ParentField.SetValue(standIn, area);
            return area;
        }

        // Unity's own removal: the tab shown before comes back if Unity focused the stand-in.
        private static void Undock(object area, EditorWindow standIn)
        {
            try
            {
                if (PanesField.GetValue(area) is IList panes && panes.Contains(standIn)) RemoveTab.Invoke(area, new object[] { standIn, false, false });
            }
            finally { ParentField.SetValue(standIn, null); }
        }

        // The naming the stand-in was handed, taken out of it unfinished (finishing it there would make the asset now, or
        // throw it away).
        private static Pending Take(EditorWindow standIn)
        {
            var state = ListAreaState.GetValue(standIn);
            var utility = state != null ? CreateUtility.GetValue(state) : null;
            string path = utility != null ? PathField.GetValue(utility) as string : null;
            if (string.IsNullOrEmpty(path)) return null;
            var action = EndActionField.GetValue(utility) as EndNameEditAction;
            var pending = new Pending
            {
                Action = action,
                InstanceId = (int)InstanceIdField.GetValue(utility),
                Icon = IconField.GetValue(utility) as Texture2D,
                ResourceFile = ResourceFileField.GetValue(utility) as string,
                Folder = (Path.GetDirectoryName(path) ?? "Assets").Replace('\\', '/'),
                IsFolder = action != null && action.GetType().Name.StartsWith("DoCreateFolder", StringComparison.Ordinal),
            };
            pending.Name = pending.IsFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            pending.Extension = pending.IsFolder ? string.Empty : Path.GetExtension(path);
            UtilityClear.Invoke(utility, null);
            OverlayClear.Invoke(RenameOverlay.GetValue(state), null);
            NewAssetIndex.SetValue(state, -1);
            return pending;
        }

        /// <summary>The path the asset gets in <paramref name="folder"/> under <paramref name="name"/> (Unity's file name rules,
        /// its extension kept, made unique).</summary>
        internal static string PathFor(Pending pending, string folder, string name)
        {
            name = InternalEditorUtility.RemoveInvalidCharsFromFileName((name ?? string.Empty).Trim(), true);
            if (name.Length == 0) name = pending.Name;
            string path = folder + "/" + name;
            if (!string.IsNullOrEmpty(pending.Extension) && !path.EndsWith(pending.Extension, StringComparison.OrdinalIgnoreCase)) path += pending.Extension;
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        /// <summary>Makes the asset under the name given, with Unity's own end of naming; returns its path, or null when
        /// nothing was made (its action failed or is gone: a script reload while naming).</summary>
        internal static string Finish(Pending pending, string name)
        {
            var action = pending?.Action;
            if (action == null) return null;
            string path = PathFor(pending, pending.Folder, name);
            try { action.Action(pending.InstanceId, path, pending.ResourceFile); }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { if (action != null) action.CleanUp(); }
            if (AssetDatabase.IsValidFolder(path) || AssetDatabase.LoadMainAssetAtPath(path) != null) return path;
            // An action of a tool's own may save elsewhere: what it selected, then.
            string selected = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            return string.IsNullOrEmpty(selected) ? null : selected;
        }

        /// <summary>Gives the naming up, as Escape in Unity's window: nothing is made.</summary>
        internal static void Cancel(Pending pending)
        {
            var action = pending?.Action;
            if (action == null) return;
            try { action.Cancelled(pending.InstanceId, pending.Folder + "/" + pending.Name + pending.Extension, pending.ResourceFile); }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { if (action != null) action.CleanUp(); }
        }
    }
}
