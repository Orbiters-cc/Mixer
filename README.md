# Mixer

Blender inside the Unity editor, by Orbiters: sculpt meshes and create blendshapes, paint weights and manage vertex
groups, edit armatures, join meshes and render with Blender's engine, with a live two-way link to the real Blender and
a modern Hierarchy window. Nothing re-imports while you edit: edits show live on a working copy of the mesh and are
saved as a native mesh asset when you commit.

## 0.1.0 — 2026-10-19

- First release: sculpting with eight brushes (Grab, Smooth, Inflate, Bump, Flatten, Clay, Pinch, Crease), falloffs,
  symmetry, blendshape layers (sculpt the base mesh or any shape, new or existing), Ctrl+Z and Ctrl+Y through Unity's
  own Undo for every stroke, autosave of edits in progress, commit to a native mesh asset and revert.
- A Mask brush (Ctrl unmasks; Clear and Invert): masked vertices stay still under the other brushes.
- Shapes: rename, duplicate, mirror one side onto the other (right-click a shape chip), delete; all undoable.
- Weight painting: Draw, Blur and Replace brushes on a bone (picked in the list or in the Scene view) or on a vertex
  group, with auto-normalize, a brush value, symmetry onto the mirror bone, the Toolkit's weight overlay coloured live,
  and the weight under the cursor. Weight Paint shows a bone's weights as soon as it is on: the bone picked last, else
  the one weighing most on the mesh. Vertex groups are kept beside the committed mesh (`<mesh>.mixer.json`).
- The Blender link: Mixer listens for the Mixer extension of Blender (installed by Mixer when you open a scene in
  Blender) and streams the whole scene (objects, lights, cameras, meshes with skin, shape keys and vertex groups,
  armatures from the bones); what you edit, sculpt or weight paint in Blender lands in Unity live and becomes one
  undo step when you leave the edit mode; what you sculpt or paint in Unity shows in Blender. Each mesh has one
  editing side at a time. Objects can be kept out of Blender from the Mixer Hierarchy.
- The Mixer Hierarchy window replaces Unity's Hierarchy (switch it off in its menu): search with t: and c:, kind
  filters, keyboard and multi-selection, rename, drag to reparent and reorder, prefab drops, visibility and picking
  switches, prefab and missing-script badges, the Blender state per object, and Blender actions in
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
- Mixer in the Scene view, like Blender, made of Unity's own toolbar controls: a **Mixer** header beside Unity's Tools
  with the mode dropdown (Object Mode, Sculpt Mode, Weight Paint, Edit Armature; entering a mode opens the selected mesh
  in Mixer), the active brush, its settings as Blender's slider fields (Radius, Strength and Weight: press, drag or use
  the wheel), the falloff with its curve, X symmetry, Normalize, Commit / Revert and the Mixer window; and a **Mixer
  brushes** toolbar under Unity's Tools with a large picture of each brush (a cut through the surface showing what the
  brush does to it, after Blender's tool icons), shown while sculpting or weight painting. Nothing needs the Mixer
  window any more.
- The Skinned Mesh Renderer inspector gains Blender's panels: **Shape keys** (the blendshapes with their values; New,
  Duplicate, Mirror, Rename by double-click, Delete, Sculpt a shape; Commit and Revert) and **Vertex groups** (the bones
  and Mixer's groups with Paint, which enters Weight paint on that channel).
- Mixer's card in the Orbiters settings: Replace Unity's Hierarchy and Replace Unity's Project window (both on by
  default, also when Mixer arrives in a running editor) and the Blender link.
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
- Capture simulator (**Tools > Orbiters > Mixer Capture**): flies a camera through the open scene (a recorded Scene view
  flight, waypoints or an orbit), renders it as VRChat's Stream camera sees the capture avatar (strip, depth frames,
  optional PPv2) into a raw capture with ground truth under Documents/Orbiters/Raw captures for Orbiters desktop, then
  decodes a sample back and checks it bit for bit. Formats come from the OrbitersContracts submodule in `Contracts/`.
- Weight Paint sub-selects bones, as Blender does: a click on a bone in the Scene view's armature overlay, in the
  Mixer Hierarchy or in the bone list makes it the painted bone and the one the weight overlay shows, at once, while
  Unity's selection and the inspectors stay on the painted mesh. The bone is highlighted in the overlay and in the
  hierarchy (with a "painting" badge) and gets a handle in the Scene view to pose it and try the weights: Unity's move
  handle with the Move tool, rotation rings with the others (without the free-rotation ball, so a stroke can still
  start over the joint), along the toolbar's pivot rotation and with Undo; Unity's own handle of the mesh hides
  meanwhile. A click on empty space, a vertex group, another selection or leaving the mode drops it. Shift and Ctrl
  clicks still select in Unity; in the hierarchy a double click or F frames the bone. A bone of the rig the mesh is not
  skinned to can be posed, not painted.
- Review fixes (2026-10-20), data safety: nothing done just before a script reload, Play, closing a scene or quitting is
  lost (edits are saved at once), and after a crash or restart they come back by themselves. Play shows the committed
  meshes; the edits come back when it stops. Ctrl+Z after a Commit or a Revert never leaves a mesh missing; an edited
  object deleted and brought back with Undo keeps its edits; undo past a session's start sets it aside, with its edits
  and history, until the mesh is edited again (Redo resumes it where the redo gives back the working mesh); a Ctrl+D copy of an edited object keeps the edits in a session of its own. Scene saves and uploads made during
  rest-pose editing get the original, undeformed avatar. Undoing a bone move or an added bone gives the bind poses back
  too, also after a reload; Revert puts moved bones back and removes added leaf bones; rig mates are committed together.
  Commits keep blendshape weights by name and a renamed shape's in-betweens, recompute tangents where the mesh moved,
  give a Mixer mesh used elsewhere a file of its own, and save nothing when every edit was undone. A commit of a skinned
  mesh Mixer saved before really updates it in place (it kept the old shape: Orbiters Toolkit 0.4.1). A deleted shape's name
  can be used again. Normals stay hard at hard edges and on double-sided cloth, and the display shows the normals the
  commit writes. Join keeps in-between frames and every UV channel, turns mirrored parts the right way, and joins meshes
  being edited as a commit would. Renders can be cancelled, survive a reload, and old render files are cleaned up.
- Review fixes, the window and the Scene view: Weight Paint's painted bone belongs to its mesh and follows to other
  meshes by the bone itself; a stroke on a mesh without that bone is refused instead of painting another one. Every
  Revert runs on the click and asks once when uncommitted edits would be lost (Unity's "don't show again" box turns it
  off). Edit Armature always edits the rest pose. The Scene view's Commit and Revert act on the focused or selected mesh
  only; selecting an edited mesh makes it the one the window and overlays act on (the last mesh hovered no longer
  counts). Brushes do nothing in Prefab Mode. Join takes the active object's space and keeps Unity's order. Mixer's own
  Undo and Redo act only on Mixer's steps. Flat frames throughout (no 1 px borders); brush settings are no longer
  written to EditorPrefs on every slider move; the preview badge rests while hidden.
- Review fixes, the Mixer Hierarchy: rows drag into Inspector fields and other windows; eye and lock act on the branch
  (Alt: the object alone), as in Unity; folds and filters show at once; Backspace alone no longer deletes; Copy, Cut,
  Paste, Load Scene, Set as Default Parent and Unity's GameObject menu (ReFit, My Avatar, VRCFury...) added; Unload and
  Remove Scene ask about Mixer edits and unsaved changes; DontDestroyOnLoad objects in Play mode; prefab-instance
  children, cross-scene moves and Prefab Mode handled; exclusions kept per scene; folds kept across editor starts; two
  Unity Hierarchies become two Mixer Hierarchies, and Unity's Hierarchy comes back when the setting is turned off.
  Inspector: Edit Bounds handle, the shape list no longer rebuilds on every click, Escape cancels a rename, shape
  weights outside 0-100, prefab assets refused, readable in the light skin. `mixer_tool`: start refuses assets, stroke
  checks the brush, radius, strength and the Blender claim, a root target picks its edited mesh.
- Review fixes, the Blender link (protocol 3, Mixer extension 0.2.0, installed by Open scene in Blender): Unity and
  Blender check each other's protocol and say which side to update instead of reconnecting in a loop; the extension
  asks Unity to prove it holds the link's token before sending anything. A mesh has one editing side at a time: Blender
  while one of its objects is in an edit mode, Unity while a Mixer stroke runs on it, so a Mixer session no longer locks
  Blender out and Blender's own edit is no longer taken back. Nothing from Blender is applied to a mesh it does not
  hold, or to a session of another topology. Committing or reverting a mesh Blender edits ends that edit and sends
  Unity's mesh again; Commit refuses while Blender edits. Meshes larger than a frame travel in chunks (up to 384 MB a
  mesh), one at a time, and a mesh Unity cannot send is reported. A Blender edit is one undo step with every layer it
  touched, deletes the shapes and groups deleted in Blender, and never brings back shapes Unity deleted or renamed
  meanwhile. Nodes whose target FBX, materials, bones, light or camera changed are resent; renderers kept out of
  Blender are listed as missing by Export for MCB (MCB refuses such an import instead of losing them). A `.blend` never
  opened from Mixer links only when you press Connect. `MixerLink.Commit` returns null when nothing changed.
- Sculpting stays smooth on a whole avatar body (90k vertices, 500 shapes) with a large brush: a step writes its moves
  straight into the shown positions and the world picture through flat per-vertex arrays (no sets or dictionaries per
  vertex), normals and the upload follow once a frame (the normals of a large stroke over several cores), inverse
  skinning is worked out only for the vertices under the brush, the undo record keeps each welded ring once, and the
  welds, mirror map and BVH a first stroke needs are built on worker threads as the session opens. The Mixer window,
  the preview badge and the Skinned Mesh Renderer inspector's panels wait for the stroke's end instead of rebuilding
  during it.
- The Mixer Hierarchy shows more on each line: objects holding a mesh, or with one below, take two lines with a picture
  of their meshes (no background) and what they are (vertices, shapes, meshes below); every object has a box that
  switches it on and off (one Undo step); a light's row has a light icon and, on its right, a ball that turns it (drag
  sideways to turn it around, up and down to tilt it, double-click for Unity's default); a VRChat avatar's row takes
  three lines: its picture with VRChat's logo on it, its name on VRChat (asked to VRChat through the SDK's session) and
  its custom base (MCB's or another tool's, through the Toolkit's `CustomBases`), then the VRChat SDK's performance rank
  of the avatar as it is in the scene for the build target, whether it is public, and a button that copies its link;
  with My Avatar on it, a "Drop anything" field on its right hands what is dropped to My Avatar (its component icons
  make room for it). On light and avatar rows the visibility and picking switches sit at the right end, after the ball
  or the drop field. Faint lines under each open object's fold arrow show the nesting (in the Mixer Project's trees
  too). Its armature's row has an armature icon and selects the chest, head,
  hands or feet in one click, beside its name when the window is wide enough, else on a second line; bones show a bone
  icon and empty objects that only group others a folder (open while unfolded); an accessory My Avatar added that may
  not fit offers "ReFit ?". A newer version waiting shows an Update pill on its line: a gallery item My Avatar added
  (updated in the background as from My Avatar's list, the pill saying "Updating…" meanwhile), and the avatar's custom
  base beside its name (MCB's recommended version, applied by MCB's Inspector with its confirmation and progress); the
  tools answer through the Toolkit's `AvatarObjectTools.UpdateOf`. The kind filters moved into a Filter menu in the search field; the Blender dots are off by
  default (window menu). Compact rows keep one line. The window no longer throws after a script reload when the
  selection changes before it rebuilt its rows.
- A stroke starts at once: pressing allocates nothing (each target keeps a stroke scratch of marks, moves and lists
  between strokes, and the undo record marks vertices with the stroke's serial), so no garbage collection (about half a
  second on a large project) lands on the press. While the pointer only moves, one edited mesh's pose is checked per
  refresh, in turn, without reading its bones and bind poses again; the autosave snapshots every 8 s at most; the Skinned
  Mesh Renderer inspector's shape list reads its names once per mesh and touches only rows whose weight changed.
- Strokes show as they are made: the working mesh's positions are uploaded with its renderers told, so a skinned
  renderer skins them again at once (it kept showing what it skinned last and only caught up when the mesh's bounds
  grew, so a stroke inside the body showed late and in jumps).
- Brushes keep working while the pointer is held still (every one but Grab, in Sculpt and Weight Paint): the brush
  applies again about twenty times a second where the pointer's ray meets the surface as it is now, until the release.
- No more hitches every few frames of a stroke: the working mesh's bounds are worked out from the shown positions
  once the stroke ends (Mesh.RecalculateBounds every tenth upload took ~27 ms on a body with 500 shapes), and while a
  Mixer mode edits an avatar's meshes, work that can wait on them waits (Orbiters Toolkit's
  `AvatarMeshEdits.LiveEditing`: My Avatar's optimization estimate took ~30 ms a frame, and up to 2.6 s at once).
  Profiler markers name Mixer's work in Unity's Profiler (`Mixer.Flush`, `Mixer.Stroke.Step`, `Mixer.MoveWelded`...).
- Opt-in stroke timings (EditorPrefs `Orbiters.Mixer.Diagnostics.StrokeTimings`): one line per stroke in
  `Library/Orbiters/Mixer/stroke-timings.log` (press to first move, to first upload and to the first frame the skinned
  renderer shows it, read back from the GPU; uploads against changes seen on the GPU and the longest wait for one;
  frames; garbage collections).
- The Mixer Project window replaces Unity's Project window (switch it off in its menu or the Orbiters settings):
  folders on the left with a **Recent** list at the top (the assets selected or opened lately, anywhere in the editor,
  by day); on the right the open folder as a tree whose folders unfold in place (their arrow; Alt: everything below),
  **Unfold all** and **Fold all**, and a double click that opens a folder. Sort by name, date added or date modified;
  a "new" chip on what was added in the last 24 hours. As in Unity's window, the status bar's slider turns the
  list into an icon grid with a picture of each asset, at the size it is set to (all the way left: the list). A click on a folder never changes Unity's
  selection, so the Inspector keeps what it shows; a click on a file selects it as Unity's does. The top bar creates a
  folder, a material, an animation or an animator controller in the highlighted folder (else the open one), renamed in
  place; the context menu adds Unity's own Create items, Rename, Duplicate, Delete, Copy Path, Show in Explorer and
  Reimport; drag assets onto folders to move them, files from Explorer to import them, scene objects to make prefabs.
- The search finds names (Unity's `t:` filters work) and what assets hold: animator controllers' parameters, layers,
  states and blend trees, animations' paths, properties, blendshapes and events, VRChat's expression parameters and
  menus, and the fields of components in prefabs and scenes (VRChat's, VRCFury's, any tool's). The contents are read
  on a worker thread and kept in `Library/Orbiters/Mixer/content-index.bin`, brought up to date after imports.
- A target button on each file shows where it is used: in the open scenes (the objects referencing it, directly or
  through a material or a controller) and in the project, animator controllers first with the layers and states that
  play an animation. One place is selected at once; several open a side panel listing them, a click selecting one, a
  double click framing it in the Scene view or opening it.
- Each shape of the Skinned Mesh Renderer inspector's **Shape keys** has a target button listing where it is used: the
  animations animating it on that renderer, the animator controllers playing them (on this avatar or not), and what
  other tools register through `MixerLink.BlendshapeUses`: MCB's corrective shapes on a custom base and VRCFury's
  BlendShape Links (the base shape a linked skin follows, the linked shapes a base shape drives). A click shows the
  place in the Mixer Hierarchy or Project without changing the selection; a double click selects it.
- Every Mixer search field (the Hierarchy, the Project, the Skinned Mesh Renderer inspector's shapes and groups, the
  Mixer window's bone lists) has the Logger's switches: match case (Aa) and regular expression (.*), kept per field. An
  invalid expression turns the field red, the reason in its tooltip. In the Project window an expression is matched
  against file names and what assets hold.
- The Mixer Project window gets what Unity's Project window had: **Favourites** in the tree (Unity's all materials,
  models and prefabs, plus folders and assets added from the context menu or dragged there, and searches saved with the
  star in the search field; right-click to move or remove one); a **+** button with everything Unity and the project's
  tools can create; files holding sub-assets (a model's meshes, materials, clips and avatar) unfold to them, to select or
  drag one; the search's scope (everywhere, Assets, packages or the open folder, in the path bar) and asset labels
  (Filter › Labels); the selected asset's path in the status bar; a lock in the tab that stops following the selection;
  Unity's Assets menu on the context menu (Import, Export Package, Find References, Select Dependencies, Properties,
  other tools' items); Copy and Paste of assets; typing the start of a name to jump to it.
- The Mixer Hierarchy no longer shows a "mixer" badge on meshes being edited.
- Review fixes (2026-10-20):
  - Mixer Project: "+ › C# Script" (or Assembly Definition) takes the name you give it, class included, so a second one
    no longer breaks compilation. The create menu moves only what its item made, never files saved elsewhere meanwhile.
    Japanese and other non-ASCII names are found inside assets, so their blendshapes no longer read as used by nothing.
    A .unitypackage dropped from Explorer is imported; project files dragged from Explorer move instead of being copied.
    A folder pasted onto itself goes beside it. Delete, Duplicate and Copy act only on what the open folder or search
    shows. The Edit menu's Delete, Duplicate, Select All and Find work in the window. Scene objects dropped as prefabs
    ask for an original prefab or a variant, as Unity does. A regular expression that is too slow stops at once and
    turns the field red.
  - Mixer Hierarchy: a row's picture never destroys the mesh Mixer is editing; pictures are kept for the session only
    and freed when they change. Shift+arrows grow a range. Reordering under the same parent is undoable. Double-clicks on
    a row's switches and light ball stay theirs. Cut no longer deletes: the objects stay, dimmed, until Paste moves them
    beside the selected object (or under the clicked one with Paste as Child), so every reference to them still holds;
    Escape or a new Copy forgets the cut. VRChat ranks update only after changes to that avatar; lookups that
    failed are retried later and later, and an avatar whose blueprint ID is another account's upload says so.
  - Sculpt and paint: a stroke whose release is lost ends instead of sculpting on its own. A held brush works only
    while the pointer stays still (slow drags follow the brush spacing) and aims from the camera as it is now. Undo
    during a held stroke puts that stroke back first. My Avatar's estimate waits only during strokes and shortly after.
  - Data safety: Ctrl+Z after committing rest-pose edits shows the original on its own rest pose; a Revert can no
    longer be partly undone into a deformed avatar; a duplicated shape starts at 0; undone edits are no longer brought
    back by the crash autosave.
  - Blender: the link waits while Unity is in Play mode (Blender keeps the edit-mode scene and links again when Play
    ends; Render and Open scene in Blender ask you to stop Play first). Each scene's .blend is under its scene's GUID, so
    two scenes with the same name no longer share a file. Shapes and groups you change in Mixer while Blender holds the
    mesh reach Blender when it lets go; a shape added just as Blender takes the mesh is no longer deleted by Blender's
    release, and a deleted shape is no longer brought back by Blender's live edits. Renders without a linked Blender
    work for unsaved scenes and say when the installed extension is too old. The `mixer_tool` commit fails when Blender
    holds the mesh or nothing was committed.
  - Splats: **Import Splats…** is now **Tools > Orbiters > Mixer Splats…**. Imports work in a new project; PLYs Mixer
    cannot read are refused before they are copied, broken splats are skipped, and clouds over 8,388,608 splats are
    refused. Splats no longer draw in Prefab Mode, inspector previews or other tools' renders. Send to Mixer only takes
    sessions on this computer's own drives.
  - Performance bakes: the last pose holds through gaps instead of jumping to the origin, and without foot trackers the
    feet follow the body when you walk. An old T-pose calibration no longer overrides VRChat's own head and hand poses.
    Short button presses, Random parameter drivers and the sil viseme behave as in VRChat. Bakes of sessions still
    recording, or where the avatar changed, say what is missing. Long audio tracks are streamed, so hour-long sessions
    keep their sound.
- Review fixes, second round (2026-10-21):
  - Unity's own features now reach the Mixer Hierarchy and Project that replace its windows. A ping (an object field's
    click, the Console, Find References, any tool's "show") opens the way to the object or asset and flashes its line.
    Find References In Scene shows its search in the Mixer Hierarchy, and clearing it there brings the Scene view back.
    Other tools' row icons (`EditorApplication.hierarchyWindowItemOnGUI`, such as Easy Quest Switch) draw on Mixer
    Hierarchy rows and get their clicks. Unity's create items in the Mixer Project's + and Create menus make their asset
    in the folder and name it in place, as in Unity's window (Escape makes nothing). Import New Asset in its context menu
    brings the file into the folder you clicked. Unity's scene visibility shortcuts (H, L) and Create Empty Parent no
    longer throw while the Hierarchy is replaced. Unity's main menu Assets > Create and Import New Asset, used outside
    the Mixer Project, still put their asset in Assets.
  - Blender link: Blender's work is no longer thrown away. Meshes Unity sends again (after a commit, a revert, a resend)
    are patched into Blender's copy, so UVs, seams, normals, colours and keys and groups made in Blender stay; a copy that
    must be rebuilt for another topology is kept hidden first. Edits made in Blender under a claim Unity refused or ended
    stay in Blender, marked in its Mixer panel, until you send them or take Unity's mesh (Blender's then kept in a copy).
    Ctrl+Z and Ctrl+Y in Unity reach Blender; an undo of a mesh Blender is editing ends Blender's claim and keeps its
    edits there. Claims are numbered, so a late answer no longer cancels a newer claim. Shift+D copies in Blender are
    no longer the Unity object. A link dropped by a script reload or a long render links again on its own; only another
    Blender taking it waits for Connect. Needs the Mixer extension of this release.
  - Data safety: a script reload after the mesh was reimported, even with as many vertices, starts from the new import
    instead of mixing old and new. A mesh left by a session that could not come back after a reload gets its original
    again, so scenes never save it missing. Ctrl+Z after committing a mesh Mixer saved before gives that asset what it
    held, and Ctrl+Y the commit again; a Mixer mesh that a prefab, a closed scene or a waiting session uses gets a file of its own. A bone posed in
    Weight Paint goes back when Weight Paint ends, and scene saves and uploads never get the pose. Painting a vertex's
    last bone away with Normalize off leaves it on that bone instead of the rig's first bone. Undoing a bone move made
    before Play keeps the skin. A session set aside by an undo comes back with its edits after a reload when the mesh is
    edited again. An undo step of a session committed, reverted or ended since (Play ends sessions) says it changes
    nothing in Mixer.
  - Performance bakes play the avatar as an upload builds it: face tracking prefabs, toggles and merged animators from
    VRCFury or Modular Avatar move the face and toggles, under the parameter names VRChat recorded. The clip still
    animates the avatar in the scene; what only the build has is left out, and a note says how much. When the build
    fails, the bake uses the avatar as the scene has it and says what failed. The Bake button says "Baking…" while the
    avatar is built, and the result shows which animators played.
- A Bump brush (Blender's Draw): the surface under the brush rises together along its mean normal, so a bump keeps
  its shape over creases; Ctrl presses a dent.
- Sculpting keeps the mesh's own normals: where a brush moved the surface they turn as its faces turned, instead of
  being computed again from the faces. Smooth (and every brush) left dents and blotches in the shading wherever it
  touched authored or smoothed normals, even where the surface hardly moved; committed base edits keep them the same way.
- The shape key panel's slider of the shape being edited works: it shows the shape at that weight (a new shape too, and
  a session-only shape that is not edited becomes the edited one); the next stroke shows it whole again. Its columns
  line up whatever a shape's name (long names are cut with an ellipsis, the full name is in the tooltip) and the
  slider knobs sit on their tracks.
- Requires Orbiters Toolkit 0.4.1, which holds the avatar gizmos (bones, weight paint, mesh edges, avatar budget) and
  lets Mixer take the armature overlay's bone clicks.

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
100 %; its slider in the inspector's Shape keys panel tries other weights until the next stroke. On commit, new shapes are appended to the mesh and edited ones rebuilt, with their normals following the sculpt.

## Blender

Press the Blender pill in Mixer's header and **Open scene in Blender**: Blender starts on the scene's project
(`Assets/Orbiters/Mixer/Blender/<scene GUID>/blend~/<scene>.blend`, a folder Unity never imports), installs or updates the
Mixer extension and connects to the editor through the endpoint file in `%LOCALAPPDATA%/Orbiters/Mixer`. The scene
arrives as a collection `Unity: <scene>`; skinned meshes get an armature built from their bones.

- Enter Edit, Sculpt or Weight Paint mode on a Unity object in Blender: Unity shows the changes as you make them and
  records one undo step when you leave the mode. Shape keys edited in Blender become the matching blendshape layer.
- Sculpt or paint in Unity: Blender's mesh follows. A mesh is edited on one side at a time; the other side sees it as
  taken.
- Right-click an object in the Mixer Hierarchy, Blender, **Keep out of Blender** to leave it (and its children) out
  of the project; **Send to Blender** puts it back. The exclusions are saved per scene under `Assets/Orbiters/Mixer/Link`.
- Topology changes (vertices added or removed in Blender) are not applied by the link: export an FBX for those.
- Unity's meshes are patched into Blender's copy: what the link does not carry (UVs, seams, normals, colours, keys and
  groups made in Blender) stays there. What Unity does not take (a claim refused or ended by a commit, revert or undo)
  stays in Blender too, marked in its Mixer panel, until **Send to Unity** or **Reload from Unity** (which keeps
  Blender's copy in the hidden `Mixer: kept copies` collection first).
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
type), the Filter menu in the search field filters by kind, arrows move, F2 renames, Delete deletes, Ctrl+D duplicates,
Alt+click opens or closes a branch with everything under it, double-click frames the object. Drag rows to reparent or
reorder, drop prefabs from the Project. The eye and lock at the right switch the Scene view visibility and picking.
With Blender dots on (window menu), the dot before each object is its Blender state: green in the Blender project,
grey kept out, violet being edited in Blender, hollow when Blender is not linked.

Pings from Unity and other tools (an object field's click, the Console) open the way to the object and flash its row.
Find References In Scene filters the rows; clear the search to show everything again in the Scene view too. Other
tools' row icons show at the end of each row's name.

## Mixer Project

Tools, Orbiters, Mixer Project, or in place of Unity's Project window. Folders on the left (Recent first, then Assets
and the packages by name); the open folder on the right, its folders unfolding in place with their arrow (Alt+click:
everything below) or Unfold all and Fold all in the path bar; double-click opens a folder or a file. The slider at the
right of the status bar sets the icons' size: all the way left is the list, further right an icon grid (arrows move
through the tiles). Arrows move, Right
and Left unfold and fold, Enter opens, F2 renames, Delete deletes (to the Recycle Bin, after asking), Ctrl+D duplicates,
Backspace goes up, Ctrl+F searches. A click on a folder lights it up here only: Unity's selection and the Inspector stay
as they were. The four buttons at the top create a folder, a material, an animation or an animator controller in the
highlighted folder, or the open one. The target button on a file lists where it is used.

Unity's create items in the Mixer Project's + and Create (right-click) menus make their asset in the folder and name it
in place, as Unity's window does. Unity's main menu **Assets > Create** and **Import New Asset** still put their asset in
Assets: Unity asks a Project window of its own for the folder, so use the Mixer Project's menus (its right-click menu has
Import New Asset too). Unity's pings (a click on an object field, the Console, Find References) show the asset here.

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

## Splats

Gaussian splats that Orbiters desktop reconstructed from a VRChat session show in the Scene and Game views, where the
world was captured. In Orbiters desktop, **Send to Mixer** in a reconstructed session's detail sends them to the Unity
editor running Mixer; in Unity, **Tools > Orbiters > Mixer Splats…** picks a session's
`reconstruction/splats.ply`. Either way the PLY is copied to `Assets/Orbiters/Splats/<session>.splats` (imported by
Mixer as a splat cloud), and an EditorOnly **Splat Renderer** object (left out of builds) is added to the open scene,
selected and framed.

The renderer sorts the splats back to front for each camera on the GPU (a bitonic sort in a compute shader) and draws
them before the transparent pass, behind your scene's geometry where it is in front (built-in render pipeline, compute
shaders). Move the object to offset them. Each editor keeps an inbox for Orbiters desktop under
`%LOCALAPPDATA%/Orbiters/Mixer/inbox/<process id>/` (an `endpoint.json` naming the project, and the requests).

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
The capture simulator's tests (`Tests/Editor/Capture`, `Orbiters.Mixer.Capture.Editor.Tests`) render a preview scene of
their own and write into the temp folder; its window is `Orbiters.Mixer.Editor.Capture.CaptureWindow`.
