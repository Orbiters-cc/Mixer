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
- Armature mode: rest-pose editing (move a bone with Unity's handles or the Mixer Hierarchy and the bind poses
  follow so the skin stays where it is, in the same undo step), a list of the bones with their weighted vertex counts,
  Reset to bind pose, Add bone under the selected bone, and the Toolkit's mirror posing. Blender's armature edits
  rebuild the bones on the way back.
- Join: the selected meshes (skinned or static, several objects) become one skinned mesh in the first one's space,
  bones united, blendshapes united by name, same materials in one slot, saved as a mesh asset on a new object; the
  sources are hidden; one Undo step.
- Render with Blender: Eevee or Cycles, size, samples, Scene view or a scene camera, transparent background. With
  Blender linked it renders the scene it holds; otherwise a background Blender takes the scene over the link, renders
  it and quits. The PNG is shown in Mixer and saved on request (never imported on its own).
- A VRChat build hook strips Mixer's helper objects and shows the original meshes on the build copy, so an upload made
  during an edit carries nothing of Mixer.
- An MCP tool `mixer_tool` for agents (see Agents).
- `MixerLink`, a public facade for the other Orbiters tools: the link's state, Open scene in Blender, a renderer's Mixer state, Commit, Revert, Edit, keep out of Blender. MCB's creator mode uses it.
- A tool can name the project file each renderer's mesh stands for (`MixerLink.TargetFbxResolver`): Blender gets it on the node as `targetFbx`, and the Mixer extension's **Export for MCB** writes one FBX per target with the manifest MCB imports. Status frames from Blender reach tools through `MixerLink.BlenderStatus`.
- Mixer in the Scene view, like Blender: a **Mixer** toolbar overlay with the mode dropdown (Object Mode, Sculpt Mode,
  Weight Paint, Edit Armature; entering a mode opens the selected mesh in Mixer) and Commit / Revert, and a **Mixer
  brushes** overlay with the brushes and brush settings of the mode, shown while sculpting or weight painting. Nothing
  needs the Mixer window any more.
- The Skinned Mesh Renderer inspector gains Blender's panels: **Shape keys** (the blendshapes with their values; New,
  Duplicate, Mirror, Rename by double-click, Delete, Sculpt a shape; Commit and Revert) and **Vertex groups** (the bones
  and Mixer's groups with Paint, which enters Weight paint on that channel).
- Mixer's card in the Orbiters settings: Replace Unity's Hierarchy (on by default, also when Mixer arrives in a running
  editor) and the Blender link.
- Fixed: Ctrl+Z after a sculpt stroke left the normals of the previous state; the Mixer Hierarchy's fold arrows, eye and
  lock did not react to clicks.
- Review fixes: commits never overwrite another mesh asset of the same name; a picked shape loads at once; unedited
  shapes keep their frames, edited ones their in-betweens, with seamless normal deltas; mirror bones from the humanoid
  map or L/R tokens (no painting the other side on an unmatched side bone); rest-pose editing carries every mesh of the
  rig; bind poses and the mask in the autosave; Prefab Mode refused and prefab writes keep the original mesh; the edited
  shape's own weight kept for saves, uploads and reloads.
- Blender link protocol 2, for an Export for MCB that keeps the mesh whole. The link welds vertices only where Unity
  split them for a UV seam or a hard edge: same place, normals less than 120° apart, same skin, same motion in every
  shape frame. So double-sided cloth, closed lips and seams that open in a shape stay apart, and no face is left out
  (a 89k-vertex body used to lose 4,368). Meshes also carry UV channels 1-7, vertex colours, in-between shape frames
  and bind poses. Blender rests the armature in the bind pose with Unity's bone orientations, and poses it where the
  scene has it. The Blender extension has to be the matching version (Open scene in Blender reinstalls it).
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
- What Blender gets is the whole mesh:
  - every face;
  - UV channels 1-7 (`UVMap1`…);
  - vertex colours (`Color`);
  - in-between shape frames, as keys named `<shape> @<weight>`;
  - an armature resting in the bind pose and posed like the scene.

  **Export for MCB** folds the in-between keys back into frames of their shape in the FBX. Its manifest only warns
  about what FBX cannot hold (UV components past two).
- Development: EditorPrefs `Orbiters.Mixer.BlenderExtensionSource` (a folder with `blender_manifest.toml`) or
  `Orbiters.Mixer.BlenderExtensionZip` make Mixer install the extension from there instead of the release.

## Mixer Hierarchy

Tools, Orbiters, Mixer Hierarchy, or in place of Unity's Hierarchy. Ctrl+F searches (`t:Light`, `c:Rigidbody` by
type), the chips filter by kind, arrows move, F2 renames, Delete deletes, Ctrl+D duplicates, Alt+click opens or closes
a branch with everything under it, double-click frames the object. Drag rows to reparent or reorder, drop prefabs from
the Project. The eye and lock at the right switch the Scene view visibility and picking. The dot before each object
is its Blender state: green in the Blender project, grey kept out, violet being edited in Blender, hollow when Blender
is not linked.

## Armature

The **Armature** mode lists the target's bones (the weighted vertices of each) and turns on rest-pose editing: move or
rotate a bone with Unity's own handles, the Mixer Hierarchy or the Inspector, and the skin stays where it is because
the bind poses are recomputed in the same undo step. Switch rest-pose editing off to pose the mesh as usual. **Reset
to bind pose** puts the bones back where the mesh was skinned, **Add bone** adds a leaf bone under the selected one
(then paint its weights in the Weights mode), **Mirror** moves left and right bones together. Commit writes the new
bind poses into the mesh asset.

## Join

Select two or more objects with meshes in the Hierarchy and press **Join** in the **Join** mode: the result is one
skinned mesh in the first object's space on a new object next to it. Skinned meshes bring their bones (united by
transform); a static mesh rides its own transform as a bone, so joining a hat onto a head keeps it where it is.
Blendshapes with the same name become one shape; submeshes that share a material share one slot (switch off **Merge
same materials** to keep them apart). The sources are hidden, not deleted; Ctrl+Z brings everything back.

## Render

The **Render** mode renders the scene with Blender: engine (Eevee, Cycles), size, samples, camera (the Scene view's
eye or any camera of the scene) and a transparent background. When Blender is linked it renders the scene it holds and
sends the image back; otherwise Mixer starts a Blender without a window, which connects to the link, receives the scene
and renders it (the Mixer extension must have been installed once with **Open scene in Blender**). The PNG lands under
`Library/Orbiters/Mixer/render`, is shown in Mixer, and **Save as…** or **Save to project** keep it.

## Agents

The `mixer_tool` MCP tool (with MCP for Unity installed) drives Mixer without the desktop: `status` (sessions, mode,
brush, history, link), `start {target, mode}` (edit a renderer: instance ID, Mixer node id or hierarchy path
`Root/Child`, `Scene:Root/Child`), `stroke {brush, from, to, radius, strength, steps, invert}` (a world-space stroke
recorded as one undo step), `undo`, `redo`, `commit`, `revert`, `link_status`, `hierarchy {search, limit}` (the
objects with Mixer's flags) and `exclude {target, excluded}` (keep an object out of the Blender project). The
Toolkit's `orbiters_editor_window` tool captures the Mixer windows.

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
