# Mixer

Blender inside the Unity editor, by Orbiters: sculpt meshes and create blendshapes, paint weights and manage vertex
groups, edit armatures, join meshes and render with Blender's engine, with a live two-way link to the real Blender and
a modern Hierarchy window. Nothing re-imports while you edit: edits show live on a working copy of the mesh and are
saved as a native mesh asset when you commit.

## 0.1.0 — 2026-10-19

- First release: sculpting with seven brushes (Grab, Smooth, Inflate, Flatten, Clay, Pinch, Crease), falloffs,
  symmetry, blendshape layers (sculpt the base mesh or any shape, new or existing), Ctrl+Z and Ctrl+Y through Unity's
  own Undo for every stroke, autosave of edits in progress, commit to a native mesh asset and revert.
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

## Agents

The tests in `Tests/Editor` run by reflection: call the `[Test]` methods of `Orbiters.Mixer.Editor.Tests`. Windows are
checked with Orbiters Toolkit's `orbiters_editor_window` MCP tool (`window_type: "Orbiters.Mixer.Editor.MixerWindow"`).
