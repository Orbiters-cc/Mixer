using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    // Dragging rows (to reparent and reorder here, or into another window: an Inspector field, the Project, the
    // Animator), dropping prefabs and scene objects from elsewhere, and the context menus.
    internal sealed partial class MixerHierarchyWindow
    {
        private enum DropZone { None, Above, Onto, Below }

        private int dragFrom = -1;
        private Vector2 dragStart;
        private int dropRow = -1;
        private DropZone dropZone;

        /// <summary>
        /// Asks to save a scene with changes before it is closed (Save, Don't Save or Cancel, as Unity does); false
        /// cancels. Tests replace it.
        /// </summary>
        internal static Func<Scene, bool> AskToSaveScene = scene => EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new[] { scene });

        private void BeginDrag(int index, PointerDownEvent evt)
        {
            dragFrom = index;
            dragStart = evt.position;
        }

        private void OnViewportPointerDown(PointerDownEvent evt)
        {
            if (evt.button == 0 && list.ItemAt(list.WorldToLocal(evt.position)) < 0) { Selection.activeObject = null; list.RefreshRows(); root.Focus(); }
        }

        private void OnViewportPointerMove(PointerMoveEvent evt)
        {
            if (dragFrom < 0) return;
            // A release outside the window never came back as a pointer up: a move without the button held is no drag.
            if ((evt.pressedButtons & 1) == 0) { EndDrag(); return; }
            if (((Vector2)evt.position - dragStart).magnitude < 5f) return;
            StartDrag();
        }

        private void OnViewportPointerUp(PointerUpEvent evt) => EndDrag();

        // Unity's own drag and drop, as its Hierarchy does: the rows land in Inspector fields (a PhysBone's root, a
        // constraint's source), the Project (a prefab), the Animator, Unity's Hierarchy and back here (a move).
        private void StartDrag()
        {
            var row = model[dragFrom];
            dragFrom = -1;
            var objects = Selection.gameObjects.Where(g => g != null && g.scene.IsValid()).Cast<Object>().ToArray();
            if (objects.Length == 0 && row?.Object != null) objects = new Object[] { row.Object };
            if (objects.Length == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objects;
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.StartDrag(objects.Length == 1 ? objects[0].name : "<Multiple>");
        }

        private void EndDrag()
        {
            dragFrom = -1;
            dropRow = -1;
            dropZone = DropZone.None;
            if (dragLine != null) MixerUi.Show(dragLine, false);
        }

        private void UpdateDrop(Vector2 worldPosition)
        {
            var local = list.WorldToLocal(worldPosition);
            int index = list.ItemAt(local);
            dropRow = -1; dropZone = DropZone.None;
            MixerUi.Show(dragLine, false);
            if (index < 0) return;
            var row = model[index];
            float rowTop = (float)((index - list.First) * list.RowHeight);
            float within = list.ChangeCoordinatesTo(list.Viewport, local).y - rowTop;
            float h = list.RowHeight;
            if (row.IsScene || IsStageRoot(row.Object)) dropZone = DropZone.Onto;
            else if (within < h * 0.25f) dropZone = DropZone.Above;
            else if (within > h * 0.75f) dropZone = DropZone.Below;
            else dropZone = DropZone.Onto;
            dropRow = index;
            MixerUi.Show(dragLine, true);
            dragLine.EnableInClassList("mxh-dragline--onto", dropZone == DropZone.Onto);
            float indent = Mathf.Max(0, row.Depth - 1) * 14f + 24f;
            dragLine.style.left = dropZone == DropZone.Onto ? 2f : indent;
            dragLine.style.top = dropZone == DropZone.Above ? rowTop : dropZone == DropZone.Below ? rowTop + h - 2f : rowTop;
            dragLine.style.height = dropZone == DropZone.Onto ? h : 2f;
        }

        private static List<GameObject> DraggedSceneObjects() =>
            DragAndDrop.objectReferences.OfType<GameObject>().Where(g => g != null && g.scene.IsValid()).ToList();

        private static List<GameObject> DraggedPrefabs() =>
            DragAndDrop.objectReferences.OfType<GameObject>().Where(g => g != null && !g.scene.IsValid() && EditorUtility.IsPersistent(g)).ToList();

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            var moving = DraggedSceneObjects();
            var prefabs = DraggedPrefabs();
            if (moving.Count == 0 && prefabs.Count == 0) return;
            UpdateDrop(evt.mousePosition);
            if (moving.Count > 0) DragAndDrop.visualMode = dropRow >= 0 ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected;
            else DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            var moving = DraggedSceneObjects();
            var prefabs = DraggedPrefabs();
            MixerUi.Show(dragLine, false);
            if (moving.Count == 0 && prefabs.Count == 0) return;
            DragAndDrop.AcceptDrag();
            var target = dropRow >= 0 ? model[dropRow] : null;
            if (moving.Count > 0) { if (target != null) Move(moving, target, dropZone); }
            else DropPrefabs(prefabs, target, dropZone);
            EndDrag();
            evt.StopPropagation();
        }

        private void Move(List<GameObject> moving, HierarchyRow target, DropZone zone)
        {
            // Only the topmost of the moved objects move; their descendants follow.
            var roots = moving.Where(g => !moving.Any(o => o != g && g.transform.IsChildOf(o.transform))).ToList();
            if (target.Object != null && roots.Contains(target.Object)) return; // released where it was
            if (roots.Any(IsStageRoot)) { toasts.Show("The prefab's root stays where it is.", true); return; }
            // Unity cannot restructure a prefab instance: what it holds moves once the prefab is opened or unpacked.
            var locked = roots.FirstOrDefault(IsLockedInPrefab);
            if (locked != null) { toasts.Show(locked.name + " is part of a prefab instance: open the prefab, or unpack the instance, to move it.", true); return; }
            if (target.IsDontDestroyOnLoad) { toasts.Show("Objects go to DontDestroyOnLoad from their scripts in Play mode.", true); return; }
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Move " + (roots.Count == 1 ? roots[0].name : roots.Count + " objects"));
            if (target.IsScene)
            {
                if (!target.Scene.isLoaded) return;
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && stage.scene == target.Scene && stage.prefabContentsRoot != null)
                {
                    // A prefab has one root: what is dropped on its row goes under it.
                    foreach (var go in roots) Undo.SetTransformParent(go.transform, stage.prefabContentsRoot.transform, "Move " + go.name);
                    model.SetExpanded(stage.prefabContentsRoot.GetInstanceID(), true);
                }
                else
                {
                    foreach (var go in roots) { ToScene(go, target.Scene); if (go.transform.parent != null) Undo.SetTransformParent(go.transform, null, "Move to scene"); }
                }
                RebuildNow();
                return;
            }
            var anchor = target.Object;
            if (anchor == null) return;
            foreach (var go in roots) if (anchor.transform.IsChildOf(go.transform)) { toasts.Show("An object cannot go under its own child.", true); return; }
            if (zone == DropZone.Onto || IsStageRoot(anchor))
            {
                foreach (var go in roots)
                {
                    ToScene(go, anchor.scene);
                    Undo.SetTransformParent(go.transform, anchor.transform, "Move " + go.name);
                }
                model.SetExpanded(anchor.GetInstanceID(), true);
            }
            else
            {
                var parent = anchor.transform.parent;
                int index = anchor.transform.GetSiblingIndex() + (zone == DropZone.Below ? 1 : 0);
                foreach (var go in roots)
                {
                    ToScene(go, anchor.scene);
                    if (go.transform.parent == parent && go.transform.GetSiblingIndex() < index) index--;
                    Undo.SetTransformParent(go.transform, parent, "Move " + go.name);
                    go.transform.SetSiblingIndex(index++);
                }
            }
            RebuildNow();
        }

        // Unity moves only scene roots to another scene: a child leaves its parent first.
        private static void ToScene(GameObject go, Scene scene)
        {
            if (go.scene == scene || !scene.IsValid() || !scene.isLoaded) return;
            if (go.transform.parent != null) Undo.SetTransformParent(go.transform, null, "Move " + go.name);
            Undo.MoveGameObjectToScene(go, scene, "Move " + go.name);
        }

        // A part of a prefab instance that is not its outermost root nor added to it.
        private static bool IsLockedInPrefab(GameObject go) =>
            PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go && !PrefabUtility.IsAddedGameObjectOverride(go);

        private void DropPrefabs(List<GameObject> prefabs, HierarchyRow target, DropZone zone)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Transform parent = null;
            int sibling = -1;
            Scene scene;
            if (target == null || target.IsScene)
            {
                scene = target != null ? target.Scene : stage != null ? stage.scene : SceneManager.GetActiveScene();
                if (target != null && (!scene.isLoaded || target.IsDontDestroyOnLoad)) return;
                if (stage != null && stage.scene == scene && stage.prefabContentsRoot != null) parent = stage.prefabContentsRoot.transform;
            }
            else
            {
                scene = target.Object.scene;
                if (zone == DropZone.Onto || IsStageRoot(target.Object)) parent = target.Object.transform;
                else { parent = target.Object.transform.parent; sibling = target.Object.transform.GetSiblingIndex() + (zone == DropZone.Below ? 1 : 0); }
            }
            var made = new List<Object>();
            foreach (var prefab in prefabs)
            {
                var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
                if (instance == null) continue;
                Undo.RegisterCreatedObjectUndo(instance, "Drop " + prefab.name);
                if (parent != null) instance.transform.SetParent(parent, false);
                if (sibling >= 0) instance.transform.SetSiblingIndex(sibling++);
                made.Add(instance);
            }
            if (made.Count > 0) Selection.objects = made.ToArray();
            dropRow = -1;
            RebuildNow();
        }

        // ---- Scenes -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Unload Scene (<paramref name="remove"/> false) or Remove Scene, as Unity's Hierarchy does them: never in Play
        /// mode nor for the last scene; uncommitted Mixer edits of meshes in it are asked about, then a scene with
        /// changes asks to be saved (Cancel keeps it open). An untitled scene can only be removed. True when it closed.
        /// </summary>
        internal static bool CloseScene(Scene scene, bool remove)
        {
            if (!scene.IsValid() || EditorApplication.isPlayingOrWillChangePlaymode || SceneManager.sceneCount <= 1 || IsPrefabStageScene(scene)) return false;
            if (!remove && (!scene.isLoaded || string.IsNullOrEmpty(scene.path))) return false;
            if (scene.isLoaded)
            {
                var edited = MixerSession.Targets.Where(t => t.Alive && t.Renderer.gameObject.scene == scene).ToList();
                if (!MixerConfirm.DropEdits(edited, remove ? "Remove Scene" : "Unload Scene", "They go with the scene.")) return false;
                if (scene.isDirty && AskToSaveScene != null && !AskToSaveScene(scene)) return false;
            }
            return EditorSceneManager.CloseScene(scene, remove);
        }

        // ---- Context menu -------------------------------------------------------------------------------------------

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            int index = list.ItemAt(list.WorldToLocal(evt.mousePosition));
            var row = model[index];
            var menu = evt.menu;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            if (row != null && row.IsScene)
            {
                var scene = row.Scene;
                if (row.IsDontDestroyOnLoad) return;
                if (IsPrefabStageScene(scene))
                {
                    menu.AppendAction("Create Empty", _ => CreateEmpty(false));
                    return;
                }
                var normal = DropdownMenuAction.Status.Normal;
                var disabled = DropdownMenuAction.Status.Disabled;
                menu.AppendAction("Set Active Scene", _ => SceneManager.SetActiveScene(scene), scene.isLoaded && scene != SceneManager.GetActiveScene() ? normal : disabled);
                menu.AppendAction("Save Scene", _ => EditorSceneManager.SaveScene(scene), scene.isLoaded && scene.isDirty && !playing ? normal : disabled);
                menu.AppendSeparator();
                if (!scene.isLoaded)
                    menu.AppendAction("Load Scene", _ => { EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Additive); RebuildNow(); }, !playing && !string.IsNullOrEmpty(scene.path) ? normal : disabled);
                else
                    menu.AppendAction("Unload Scene", _ => { CloseScene(scene, false); RebuildNow(); }, !playing && SceneManager.sceneCount > 1 && !string.IsNullOrEmpty(scene.path) ? normal : disabled);
                menu.AppendAction("Remove Scene", _ => { CloseScene(scene, true); RebuildNow(); }, !playing && SceneManager.sceneCount > 1 ? normal : disabled);
                menu.AppendSeparator();
                menu.AppendAction("Create Empty", _ =>
                {
                    var go = new GameObject("GameObject");
                    Undo.RegisterCreatedObjectUndo(go, "Create Empty");
                    Place(go, null, scene);
                    Selection.activeGameObject = go;
                    renamingId = go.GetInstanceID();
                    RebuildNow();
                }, scene.isLoaded ? normal : disabled);
                return;
            }
            var go0 = row?.Object;
            var selected = Selection.gameObjects.Where(g => g != null && g.scene.IsValid()).ToList();
            menu.AppendAction("Create Empty", _ => CreateEmpty(false));
            menu.AppendAction("Create Empty Child", _ => CreateEmpty(true), go0 != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("3D Object/Cube", _ => CreatePrimitive(PrimitiveType.Cube));
            menu.AppendAction("3D Object/Sphere", _ => CreatePrimitive(PrimitiveType.Sphere));
            menu.AppendAction("3D Object/Plane", _ => CreatePrimitive(PrimitiveType.Plane));
            menu.AppendAction("Light/Directional Light", _ => CreateLight(LightType.Directional));
            menu.AppendAction("Light/Point Light", _ => CreateLight(LightType.Point));
            menu.AppendAction("Camera", _ => { var go = new GameObject("Camera"); go.AddComponent<Camera>(); Undo.RegisterCreatedObjectUndo(go, "Create Camera"); Place(go, null); Selection.activeGameObject = go; RebuildNow(); });
            AppendGameObjectMenu(menu);
            if (go0 == null) return;
            menu.AppendSeparator();
            menu.AppendAction("Rename", _ => BeginRename());
            menu.AppendAction("Duplicate", _ => Duplicate());
            menu.AppendAction("Delete", _ => DeleteSelection());
            menu.AppendAction("Copy", _ => Copy());
            menu.AppendAction("Cut", _ => Cut());
            menu.AppendAction("Paste", _ => Paste());
            // One state for the whole selection, from the clicked row (as Unity does), not a flip per object.
            bool activate = !go0.activeSelf;
            menu.AppendAction(activate ? "Activate" : "Deactivate", _ => { foreach (var g in selected) { Undo.RecordObject(g, activate ? "Activate" : "Deactivate"); g.SetActive(activate); } RebuildNow(); });
            menu.AppendAction("Set as Default Parent", _ => EditorUtility.SetDefaultParentObject(go0), PrefabStageUtility.GetCurrentPrefabStage() == null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Clear Default Parent", _ => EditorUtility.ClearDefaultParentObject());
            menu.AppendSeparator();
            if (row.PrefabInstance)
            {
                menu.AppendAction("Prefab/Select Asset", _ => { var asset = PrefabUtility.GetCorrespondingObjectFromSource(go0); if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); } });
                menu.AppendAction("Prefab/Open Asset", _ => { var asset = PrefabUtility.GetCorrespondingObjectFromSource(go0); if (asset != null) AssetDatabase.OpenAsset(asset); });
                menu.AppendAction("Prefab/Unpack", _ => { foreach (var g in selected) if (PrefabUtility.IsAnyPrefabInstanceRoot(g)) PrefabUtility.UnpackPrefabInstance(g, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction); RebuildNow(); });
                menu.AppendAction("Prefab/Unpack Completely", _ => { foreach (var g in selected) if (PrefabUtility.IsAnyPrefabInstanceRoot(g)) PrefabUtility.UnpackPrefabInstance(g, PrefabUnpackMode.Completely, InteractionMode.UserAction); RebuildNow(); });
            }
            menu.AppendAction("Properties…", _ => EditorUtility.OpenPropertyEditor(go0));
            menu.AppendSeparator();
            bool excluded = row.Excluded;
            menu.AppendAction(excluded ? "Blender/Send to Blender" : "Blender/Keep out of Blender", _ => SetExcluded(selected, !excluded));
            menu.AppendAction("Blender/Open scene in Blender", _ => { string error = BlenderProjectLauncher.Open(); if (error != null) toasts.Show(error, true); });
            var renderers = selected.SelectMany(g => g.GetComponents<Renderer>()).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            if (renderers.Count > 0)
            {
                menu.AppendAction("Mixer/Sculpt", _ => { EditIn(renderers, MixerMode.Sculpt); });
                menu.AppendAction("Mixer/Paint weights", _ => { EditIn(renderers, MixerMode.Weights); });
                menu.AppendAction("Mixer/Render with Blender", _ => { MixerWindow.Open(); MixerSession.Say("Pick the Render mode in Mixer.", false); });
            }
        }

        // Unity's GameObject menu, with what other tools add to it (ReFit, My Avatar, VRCFury, Modular Avatar...): they
        // act on the selection, as from Unity's Hierarchy.
        private static void AppendGameObjectMenu(DropdownMenu menu)
        {
            string[] items;
            try { items = Unsupported.GetSubmenus("GameObject"); }
            catch (Exception) { return; }
            if (items == null || items.Length == 0) return;
            menu.AppendSeparator();
            foreach (string item in items)
            {
                if (string.IsNullOrEmpty(item) || !item.StartsWith("GameObject/", StringComparison.Ordinal)) continue;
                string path = item;
                menu.AppendAction(path, _ => EditorApplication.ExecuteMenuItem(path));
            }
        }

        private void EditIn(List<Renderer> renderers, MixerMode mode)
        {
            MixerWindow.Open();
            var window = GetWindow<MixerWindow>();
            foreach (var r in renderers) window.Edit(r);
            // The clicked mesh is the one painted or sculpted (its own bone indices), not the last one opened.
            var first = renderers.Select(MixerSession.TargetOf).FirstOrDefault(t => t != null && t.Alive);
            if (first != null) MixerModes.Enter(mode, first);
        }

        private void CreatePrimitive(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            Undo.RegisterCreatedObjectUndo(go, "Create " + type);
            var parent = Selection.activeGameObject;
            Place(go, parent != null ? parent.transform : null);
            Selection.activeGameObject = go;
            RebuildNow();
        }

        private void CreateLight(LightType type)
        {
            var go = new GameObject(type + " Light");
            go.AddComponent<Light>().type = type;
            Undo.RegisterCreatedObjectUndo(go, "Create Light");
            Place(go, null);
            Selection.activeGameObject = go;
            RebuildNow();
        }

        /// <summary>
        /// Keeps objects (and their children) out of the Blender project, or sends them again. Each object's own scene
        /// keeps the exclusion (where the link and <see cref="MixerLink.IsKeptOutOfBlender"/> read it).
        /// </summary>
        internal static void SetExcluded(IEnumerable<GameObject> objects, bool excluded)
        {
            bool any = false;
            foreach (var group in objects.Where(g => g != null && g.scene.IsValid()).GroupBy(g => g.scene))
            {
                var settings = MixerLinkService.SettingsFor(group.Key, create: excluded);
                if (settings == null) continue; // nothing was kept out of that scene: nothing to send back
                Undo.RecordObject(settings, excluded ? "Keep out of Blender" : "Send to Blender");
                foreach (var go in group)
                {
                    string id = SceneGraphExporter.IdOf(go);
                    if (excluded) { if (!settings.excludedIds.Contains(id)) settings.excludedIds.Add(id); }
                    else settings.excludedIds.Remove(id);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                any = true;
            }
            if (!any) return;
            MixerLinkService.SendGraph(force: true);
            MixerLinkService.NoteSettingsChanged();
        }
    }
}
