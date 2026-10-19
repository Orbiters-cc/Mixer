# Mixer

Blender inside the Unity editor, by Orbiters: sculpt meshes and create blendshapes, paint weights and manage vertex
groups, edit armatures, join meshes and render with Blender's engine, with a live two-way link to the real Blender and
a modern Hierarchy window. Nothing re-imports while you edit: edits show live on a working copy of the mesh and are
saved as a native mesh asset when you commit.

## 0.1.0 — 2026-10-19

- First release: sculpting with seven brushes (Grab, Smooth, Inflate, Flatten, Clay, Pinch, Crease), falloffs,
  symmetry, blendshape layers (sculpt the base mesh or any shape, new or existing), Ctrl+Z and Ctrl+Y through Unity's
  own Undo for every stroke, autosave of edits in progress, commit to a native mesh asset and revert.
- A Mask brush (Ctrl unmasks; Clear and Invert): masked vertices stay still under the other brushes.
- Shapes: rename, duplicate, mirror one side onto the other (right-click a shape chip), delete; all undoable.
- Weight painting: Draw, Blur and Replace brushes on a bone (picked in the list or in the Scene view) or on a vertex
  group, with auto-normalize, a brush value, symmetry onto the mirror bone, the Toolkit's weight overlay coloured live,
  and the weight under the cursor. Vertex groups are kept beside the committed mesh (`<mesh>.mixer.json`).
- The Blender link: Mixer listens for the Mixer extension of Blender (installed by Mixer when you open a scene in
  Blender) and streams the whole scene (objects, lights, cameras, meshes with skin, shape keys and vertex groups,
  armatures from the bones); what you edit, sculpt or weight paint in Blender lands in Unity live and becomes one
  undo step when you leave the edit mode; what you sculpt or paint in Unity shows in Blender. Each mesh has one
  editing side at a time. Objects can be kept out of Blender from the Mixer Hierarchy.
- The Mixer Hierarchy window replaces Unity's Hierarchy (switch it off in its menu): search with t: and c:, kind
  filters, keyboard and multi-selection, rename, drag to reparent and reorder, prefab drops, visibility and picking
  switches, prefab and missing-script badges, Mixer's edits and the Blender state per object, and Blender actions in
  the context menu.
- Requires Orbiters Toolkit 0.4.0, which now holds the XRay gizmos (bones, weight paint, mesh edges, avatar budget).

## Get started

1. Open **Tools › Orbiters › Mixer** (Ctrl+Alt+M).
2. Select an avatar or a mesh in the Hierarchy and press **Edit** in Mixer's header. The renderer now shows a working
   copy; the original mesh is never changed.
3. Pick a brush and drag on the mesh in the Scene view. `[` and `]` change the radius, Shift+wheel the strength, Ctrl
   inverts a brush, Shift smooths, F toggles symmetry and Esc leaves the brush.
4. Ctrl+Z undoes a stroke, Ctrl+Y redoes it, like any other edit in Unity.
5. **Commit** saves the result as `Assets/Orbiters/Mixer/Meshes/<avatar>/<mesh>.asset` and shows it on the renderer, in
   one Undo step. **Revert** puts the original back.

## Blendshape layers

The **Layer** card picks what strokes change: the base mesh, or a blendshape. Type a name and press **Create** to make
a new shape and sculpt it; pick an existing shape to edit its deltas. While a shape is edited the renderer shows it at
100 %. On commit, new shapes are appended to the mesh and edited ones rebuilt, with their normals following the sculpt.

## Blender

Press the Blender pill in Mixer's header and **Open scene in Blender**: Blender starts on the scene's project
(`Assets/Orbiters/Mixer/Blender/<scene>/blend~/<scene>.blend`, a folder Unity never imports), installs or updates the
Mixer extension and connects to the editor through the endpoint file in `%LOCALAPPDATA%/Orbiters/Mixer`. The scene
arrives as a collection `Unity: <scene>`; skinned meshes get an armature built from their bones.

- Enter Edit, Sculpt or Weight Paint mode on a Unity object in Blender: Unity shows the changes as you make them and
  records one undo step when you leave the mode. Shape keys edited in Blender become the matching blendshape layer.
- Sculpt or paint in Unity: Blender's mesh follows. A mesh is edited on one side at a time; the other side sees it as
  taken.
- Right-click an object in the Mixer Hierarchy, Blender, **Keep out of Blender** to leave it (and its children) out
  of the project; **Send to Blender** puts it back. The exclusions are saved per scene under `Assets/Orbiters/Mixer/Link`.
- Topology changes (vertices added or removed in Blender) are not applied by the link: export an FBX for those.
- Development: EditorPrefs `Orbiters.Mixer.BlenderExtensionSource` (a folder with `blender_manifest.toml`) or
  `Orbiters.Mixer.BlenderExtensionZip` make Mixer install the extension from there instead of the release.

## Mixer Hierarchy

Tools, Orbiters, Mixer Hierarchy, or in place of Unity's Hierarchy. Ctrl+F searches (`t:Light`, `c:Rigidbody` by
type), the chips filter by kind, arrows move, F2 renames, Delete deletes, Ctrl+D duplicates, Alt+click opens or closes
a branch with everything under it, double-click frames the object. Drag rows to reparent or reorder, drop prefabs from
the Project. The eye and lock at the right switch the Scene view visibility and picking. The dot before each object
is its Blender state: green in the Blender project, grey kept out, violet being edited in Blender, hollow when Blender
is not linked.

## How it works

- Edits live in a document of plain arrays; a working `Mesh` (never saved) shows them on the renderer, uploaded once
  a frame. Brushes aim at the world positions through the skinning, so a posed avatar is sculpted where it is seen.
- Ctrl+Z: every stroke registers a four-byte change on a hidden anchor object with Unity's Undo, and keeps the moved
  vertices' positions before and after in Mixer's own history (capped in memory, 512 MB by default). Undoing the anchor
  replays the history.
- Edits in progress are autosaved under `Library/Orbiters/Mixer/autosave` a few seconds after each change and restored
  after a script reload.
- A guard puts the original mesh back on any renderer left on a working mesh no session shows (Undo steps recorded
  while editing, scene saves, reloads).
- The link is a Toolkit `LiveLinkServer` (frames "MIXR" v1 over TCP on 127.0.0.1). Meshes travel welded by position
  (Unity's split vertices become face corners with their UV and normal) so Blender edits a clean mesh; positions come
  back per welded vertex and land on every Unity vertex sharing it. Coordinates travel in Unity's space; the extension
  converts (Blender = (-x, -z, y) of Unity, the mirror Unity's FBX importer applies).

## Agents

The tests in `Tests/Editor` run by reflection: call the `[Test]` methods of `Orbiters.Mixer.Editor.Tests`. Windows are
checked with Orbiters Toolkit's `orbiters_editor_window` MCP tool (`window_type: "Orbiters.Mixer.Editor.MixerWindow"`).
