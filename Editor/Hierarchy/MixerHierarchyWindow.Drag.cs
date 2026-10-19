using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // Dragging rows to reparent and reorder, dropping prefabs from the Project, and the context menu.
    internal sealed partial class MixerHierarchyWindow
    {
        private enum DropZone { None, Above, Onto, Below }

        private int dragFrom = -1;
        private Vector2 dragStart;
        private bool dragging;
        private int dropRow = -1;
        private DropZone dropZone;

        private void BeginDrag(int index, PointerDownEvent evt)
        {
            dragFrom = index;
            dragStart = evt.position;
            dragging = false;
        }

        private void OnViewportPointerDown(PointerDownEvent evt)
        {
            if (evt.button == 0 && list.ItemAt(list.WorldToLocal(evt.position)) < 0) { Selection.activeObject = null; list.RefreshRows(); root.Focus(); }
        }

        private void OnViewportPointerMove(PointerMoveEvent evt)
        {
            if (dragFrom < 0) return;
            if (!dragging)
            {
                if (((Vector2)evt.position - dragStart).magnitude < 5f) return;
                dragging = true;
                list.Viewport.CapturePointer(evt.pointerId);
            }
            UpdateDrop(evt.position);
        }

        private void OnViewportPointerUp(PointerUpEvent evt)
        {
            if (dragging && list.Viewport.HasPointerCapture(evt.pointerId)) list.Viewport.ReleasePointer(evt.pointerId);
            EndDrag(dragging && dropRow >= 0);
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
            if (row.IsScene) dropZone = DropZone.Onto;
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

        private void EndDrag(bool perform)
        {
            MixerUi.Show(dragLine, false);
            if (perform && dragging && dropRow >= 0)
            {
                var moving = Selection.gameObjects.Where(g => g != null && g.scene.IsValid()).ToList();
                var target = model[dropRow];
                if (moving.Count > 0 && target != null) Move(moving, target, dropZone);
            }
            dragFrom = -1; dragging = false; dropRow = -1; dropZone = DropZone.None;
        }

        private void Move(List<GameObject> moving, HierarchyRow target, DropZone zone)
        {
            // Only the topmost of the moved objects move; their descendants follow.
            var roots = moving.Where(g => !moving.Any(o => o != g && g.transform.IsChildOf(o.transform))).ToList();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Move " + (roots.Count == 1 ? roots[0].name : roots.Count + " objects"));
            if (target.IsScene)
            {
                if (!target.Scene.isLoaded) return;
                foreach (var go in roots) { Undo.SetTransformParent(go.transform, null, "Move to scene"); if (go.scene != target.Scene) Undo.MoveGameObjectToScene(go, target.Scene, "Move to scene"); }
                MarkDirty();
                return;
            }
            var anchor = target.Object;
            if (anchor == null) return;
            foreach (var go in roots) if (anchor.transform.IsChildOf(go.transform)) { toasts.Show("An object cannot go under its own child.", true); return; }
            if (zone == DropZone.Onto)
            {
                foreach (var go in roots)
                {
                    if (go.scene != anchor.scene) Undo.MoveGameObjectToScene(go, anchor.scene, "Move");
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
                    if (go.scene != anchor.scene) Undo.MoveGameObjectToScene(go, anchor.scene, "Move");
                    if (go.transform.parent == parent && go.transform.GetSiblingIndex() < index) index--;
                    Undo.SetTransformParent(go.transform, parent, "Move " + go.name);
                    go.transform.SetSiblingIndex(index++);
                }
            }
            MarkDirty();
        }

        // ---- Drops from the Project ---------------------------------------------------------------------------------

        private void OnExternalDragUpdated(DragUpdatedEvent evt)
        {
            if (!DragAndDrop.objectReferences.Any(o => o is GameObject)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            UpdateDrop(evt.mousePosition);
        }

        private void OnExternalDragPerform(DragPerformEvent evt)
        {
            var prefabs = DragAndDrop.objectReferences.OfType<GameObject>().Where(g => !g.scene.IsValid()).ToList();
            MixerUi.Show(dragLine, false);
            if (prefabs.Count == 0) return;
            DragAndDrop.AcceptDrag();
            var target = dropRow >= 0 ? model[dropRow] : null;
            var scene = target != null && target.IsScene ? target.Scene : target?.Object != null ? target.Object.scene : SceneManager.GetActiveScene();
            foreach (var prefab in prefabs)
            {
                var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
                if (instance == null) continue;
                Undo.RegisterCreatedObjectUndo(instance, "Drop " + prefab.name);
                if (target?.Object != null)
                {
                    if (dropZone == DropZone.Onto) instance.transform.SetParent(target.Object.transform, false);
                    else { instance.transform.SetParent(target.Object.transform.parent, false); instance.transform.SetSiblingIndex(target.Object.transform.GetSiblingIndex() + (dropZone == DropZone.Below ? 1 : 0)); }
                }
                Selection.activeGameObject = instance;
            }
            dropRow = -1;
            MarkDirty();
        }

        // ---- Context menu -------------------------------------------------------------------------------------------

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            int index = list.ItemAt(list.WorldToLocal(evt.mousePosition));
            var row = model[index];
            var menu = evt.menu;
            if (row != null && row.IsScene)
            {
                var scene = row.Scene;
                menu.AppendAction("Set Active Scene", _ => SceneManager.SetActiveScene(scene), scene.isLoaded && scene != SceneManager.GetActiveScene() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendAction("Save Scene", _ => EditorSceneManager.SaveScene(scene), scene.isDirty ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendAction("Unload Scene", _ => EditorSceneManager.CloseScene(scene, false), SceneManager.sceneCount > 1 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendAction("Remove Scene", _ => EditorSceneManager.CloseScene(scene, true), SceneManager.sceneCount > 1 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendSeparator();
                menu.AppendAction("Create Empty", _ => { var go = new GameObject("GameObject"); Undo.RegisterCreatedObjectUndo(go, "Create Empty"); SceneManager.MoveGameObjectToScene(go, scene); Selection.activeGameObject = go; renamingId = go.GetInstanceID(); MarkDirty(); });
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
            menu.AppendAction("Camera", _ => { var go = new GameObject("Camera"); go.AddComponent<Camera>(); Undo.RegisterCreatedObjectUndo(go, "Create Camera"); Selection.activeGameObject = go; MarkDirty(); });
            if (go0 == null) return;
            menu.AppendSeparator();
            menu.AppendAction("Rename", _ => BeginRename());
            menu.AppendAction("Duplicate", _ => Duplicate());
            menu.AppendAction("Delete", _ => DeleteSelection());
            menu.AppendAction("Copy", _ => Unsupported.CopyGameObjectsToPasteboard());
            menu.AppendAction("Paste", _ => { Unsupported.PasteGameObjectsFromPasteboard(); MarkDirty(); });
            menu.AppendAction(go0.activeSelf ? "Deactivate" : "Activate", _ => { foreach (var g in selected) { Undo.RecordObject(g, "Toggle active"); g.SetActive(!go0.activeSelf); } MarkDirty(); });
            menu.AppendSeparator();
            if (row.PrefabInstance)
            {
                menu.AppendAction("Prefab/Select Asset", _ => { var asset = PrefabUtility.GetCorrespondingObjectFromSource(go0); if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); } });
                menu.AppendAction("Prefab/Open Asset", _ => { var asset = PrefabUtility.GetCorrespondingObjectFromSource(go0); if (asset != null) AssetDatabase.OpenAsset(asset); });
                menu.AppendAction("Prefab/Unpack", _ => { foreach (var g in selected) if (PrefabUtility.IsAnyPrefabInstanceRoot(g)) PrefabUtility.UnpackPrefabInstance(g, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction); MarkDirty(); });
                menu.AppendAction("Prefab/Unpack Completely", _ => { foreach (var g in selected) if (PrefabUtility.IsAnyPrefabInstanceRoot(g)) PrefabUtility.UnpackPrefabInstance(g, PrefabUnpackMode.Completely, InteractionMode.UserAction); MarkDirty(); });
            }
            menu.AppendAction("Properties…", _ => EditorUtility.OpenPropertyEditor(go0));
            menu.AppendSeparator();
            bool excluded = row.Excluded;
            menu.AppendAction(excluded ? "Blender/Send to Blender" : "Blender/Keep out of Blender", _ => SetExcluded(selected, !excluded));
            menu.AppendAction("Blender/Open scene in Blender", _ => { string error = BlenderProjectLauncher.Open(); if (error != null) toasts.Show(error, true); });
            var renderers = selected.SelectMany(g => g.GetComponents<Renderer>()).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            if (renderers.Count > 0)
            {
                menu.AppendAction("Mixer/Sculpt", _ => { EditIn(renderers, 0); });
                menu.AppendAction("Mixer/Paint weights", _ => { EditIn(renderers, 1); });
                menu.AppendAction("Mixer/Render with Blender", _ => { MixerWindow.Open(); MixerSession.Say("Pick the Render mode in Mixer.", false); });
            }
        }

        private void EditIn(List<Renderer> renderers, int mode)
        {
            MixerWindow.Open();
            var window = GetWindow<MixerWindow>();
            foreach (var r in renderers) window.Edit(r);
            MixerSceneTool.Mode = mode == 1 ? MixerMode.Weights : MixerMode.Sculpt;
        }

        private void CreatePrimitive(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            Undo.RegisterCreatedObjectUndo(go, "Create " + type);
            var parent = Selection.activeGameObject;
            if (parent != null) go.transform.SetParent(parent.transform, false);
            Selection.activeGameObject = go;
            MarkDirty();
        }

        private void CreateLight(LightType type)
        {
            var go = new GameObject(type + " Light");
            go.AddComponent<Light>().type = type;
            Undo.RegisterCreatedObjectUndo(go, "Create Light");
            Selection.activeGameObject = go;
            MarkDirty();
        }

        /// <summary>Keeps objects (and their children) out of the Blender project, or sends them again.</summary>
        internal static void SetExcluded(IEnumerable<GameObject> objects, bool excluded)
        {
            var scene = SceneManager.GetActiveScene();
            var settings = MixerLinkService.SettingsFor(scene, create: true);
            Undo.RecordObject(settings, excluded ? "Keep out of Blender" : "Send to Blender");
            foreach (var go in objects)
            {
                string id = SceneGraphExporter.IdOf(go);
                if (excluded) { if (!settings.excludedIds.Contains(id)) settings.excludedIds.Add(id); }
                else settings.excludedIds.Remove(id);
            }
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            MixerLinkService.SendGraph(force: true);
            MixerLinkService.NoteSettingsChanged();
        }
    }
}
