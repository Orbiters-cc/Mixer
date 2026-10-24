# Mixer

Blender inside the Unity editor, by Orbiters: sculpt meshes and create blendshapes, paint weights and manage vertex
groups, edit armatures, join meshes and render with Blender's engine, with a live two-way link to the real Blender,
modern Hierarchy and Project windows, the Mixer console (a log hub in the Console's place) and Mixer Git (Git for VRChat
projects). Nothing re-imports while you edit: edits show live on a working copy of the mesh and are saved as a native
mesh asset when you commit.

## 0.2.0 — 2026-10-24

- The Logger became the **Mixer console** and Unit Git **Mixer Git**, both part of Mixer now (from Logger 0.2.7 and
  Unit Git 0.2.10; their history stays in their own repositories): **Tools › Orbiters › Mixer console** (Ctrl+Alt+L) and
  **Tools › Orbiters › Mixer Git**; their tabs read Console and Git, with Mixer's logo, and their top bars start with
  their own controls. The
  console stays an assembly without references (`Orbiters.Mixer.Console.Editor`), so it keeps working while other
  packages fail to compile; its parts for the Toolkit, Mixer Git and the VRChat SDK are assemblies of their own. Their
  code keeps its names (namespaces, editor preferences, `Library/Orbiters/Logger/`, the `UnitGit-Release` trailer), so
  settings, the message ledger, a commit visit under way and release checkpoints carry over. Mixer Git keeps backups in
  Documents/Mixer Git Backups/<project> by default. Mixer's logo is an assembly of its own
  (`Orbiters.Mixer.Brand.Editor`), which the console draws too, on its tab and in the source chips of Mixer, the
  Mixer console and Mixer Git.
- MCB records its release checkpoints and ReFit files through Mixer Git, and My Avatar its Save checkpoints and
  Versioning card: both look for Mixer 0.2 instead of Unit Git.
- Mixer Git writes its headings into `.gitignore` only with a rule under them that the file lacks: a project that has
  the rules under Unit Git's heading gets nothing more.
- The console's details open on the explanation of the selected log (the bulb card), above its message and stack trace.
- Mixer Git compares model versions in `Assets/__Mixer Git Compare`.
- Mixer tells Orbiters Toolkit the mesh each working copy stands for (`WorkingMeshes.OriginalOf`), so the base
  fingerprint and My Avatar's base detection see the original model while a session edits it.
- The console's search field shows its Ctrl+F hint only when it is wide enough (beside a full top bar the hint covered
  the Aa switch), and a narrow placeholder ends with an ellipsis.
- In a short console window (under 520 points tall) the graph's commit and release labels sit in a row of their own
  under it, where they were cut off at its top, and the graph keeps its height while the timings lane shows.
- Requires Orbiters Toolkit 0.4.3 (`WorkingMeshes`).

## 0.1.1 — 2026-10-23

- `MixerLink.OriginalOf(mesh)`: the mesh a working copy stands for while a session edits it, for tools that read a
  renderer's model file (MCB 1.12.13 finds the avatar's custom base through it while Mixer edits the body).
- The Hierarchy finds the armature and bones of a model placed whole without a humanoid Avatar (a generic rig, as
  Ultirex-ULTRALowPoly10 is imported): its armature row has the armature icon and the quick bones its names give, its
  bones the bone icon, where they showed as folders. Without hips found by name, the bone nearest the root sets the
  armature.
- The Project window keeps Recent in a side panel at the right once it is wide enough (its panes 760 points, beside
  the usage panel when that is open): the open folder stays in view, a click shows the asset in its folder, a double
  click opens it, and it drags like any asset. Narrower, Recent is the tree's line as before. Recent leaves out
  deleted assets (their GUIDs still gave their old paths).

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
  optional PPv2) into a raw capture with ground truth under Documents/Orbiters/Raw captures for Orbiters - N4N0, then
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
    sessions on this computer's own drives. Splats blend as Orbiters - N4N0 trained them, on their colours as captured
    (encoded sRGB) and over the scene encoded the same way, so half-transparent ones are no longer brighter than in
    Orbiters - N4N0 and sparse far parts no longer look hazier: Mixer's render of a reconstruction now matches the
    trainer's at about 40 dB, where it differed at 27.5 dB.
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
- Capture simulator and capture kit on contracts 0.5.0: depth frames have larger tiles towards the edges, where
  chromatic aberration smears small cells, and the simulator's checks follow the layout's own tiles and samples (where
  nothing was drawn, the true depth is "nothing there"). Post-processing presets now run in the simulator's hidden
  scenes (they rendered like None before), and a Bloom preset (3 from a threshold of 1, as bright worlds set it) sits
  beside Heavy. The capture kit draws only on the wearer's own client: its FX controller gains a layer that sets the
  kit's `_OrbitersLocal` from VRChat's `IsLocal`, so other players' copies of the avatar leave their pictures and
  streams alone. Install the kit again (that adds the layer to the shared controller) and upload the avatar: a kit
  installed before draws nothing with this release.
- Splats come with the sky Orbiters - N4N0 trained with them: its reconstructions keep the far sky as a map
  (`reconstruction/sky.png`) instead of a shell of discs, and an import copies it beside the cloud
  (`<session>.splatsky`, a PNG the texture importer leaves alone). The Splat Renderer draws it behind everything the
  camera drew, in place of the scene's own sky, looked up by direction as Orbiters - N4N0 does, and fog and haze turn
  towards its colours at the horizon. Without it (a reconstruction made before, or of a world that never showed the sky)
  the splats draw over the scene as before.
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

## Mixer console

### Get started

The Mixer console comes with Mixer: open **Tools › Orbiters › Mixer console** (Ctrl+Alt+L). From the next start of Unity
it takes the Console's place in your layout, and Window › General › Console (Ctrl+Shift+C) opens it (**Replace console**
in its top bar turns that off). It shows everything logged since Unity started, including what was logged before it was
installed. It was the Logger: its code keeps that name (namespace `Orbiters.Logger.Editor`, preferences
`Orbiters.Logger.*`, `Library/Orbiters/Logger/`), so what the Logger kept carries over.

### The window

- **Levels**: the three pills show errors, warnings and logs that match the other filters. Click one to show or hide
  it; Alt+click to show only it. The error count flashes when new errors arrive.
- **Search** (Ctrl+F): text anywhere in the message, case-insensitive. **Aa** matches case, **.\*** reads a .NET
  regular expression (an invalid one turns the field red with the reason, and the last results stay), the stack
  button searches stack traces too. Enter applies at once, Escape clears.
- **Sources**: show or hide where logs come from, or show only one ("Only" on hover). Counts follow the search and
  levels.
- **Replace console** (on by default): the Mixer console stands in for Unity's Console, at start-up and whenever the
  Console is opened. Off, Unity's Console opens as usual.
- **List / Groups**: every log in time order, or one row per text with its count and activity.
- **Timeline**: hover reads the time and counts; click scrolls the list there; drag selects a time range (shown in
  a chip with ✕); double-click or Escape shows the whole session again.
- **Rows** show the time (to the millisecond; to the second for logs read from Unity's console), the source, the
  message and, under it, the method and file that logged it (behind logger wrappers such as `MCBLogger.Log`). A
  bulb means the Mixer console knows this message; a target means it is about an object in the scene.
- **New logs** follow the end of the list while you are there. Scrolled up, a "N new" pill brings you back.
- **Details** (bottom pane): the full message, the stack trace with links to each file and line (logging frames
  such as `UnityEngine.Debug:Log` folded away), the explanation, and how often the message happens: a chart over
  the session, the count, first and last time, peak per second and average interval. Select several logs to see a
  summary and copy them together.

#### Keyboard

| Keys | Action |
| --- | --- |
| ↑ ↓, Page Up/Down, Home/End | Move; with Shift, select a range |
| Ctrl+click, Shift+click | Add to the selection, select a range |
| Ctrl+A | Select every shown log |
| Ctrl+C / Ctrl+Shift+C | Copy the text / the text with stack traces |
| Enter, double-click | Open the source file at the line |
| F3 / Shift+F3 | Next / previous error |
| Delete | Hide messages like the selected ones |
| Ctrl+F | Search |
| Ctrl+L | Clear |
| Escape | Clear the selection, then the time range |

Right-click a log for the same actions plus "Show only / Hide" its source and "Search for this text". Hidden
messages are listed (and shown again) from the chip in the list header.

#### Settings

The sliders button opens the settings:

- **Clear on Play, Clear on Build, Clear on Recompile, Error Pause** are Unity's console settings: changing them
  here changes them in the Console too.
- **Group similar messages**: "Loaded 12 assets in 3.4 ms" and "Loaded 7 assets in 1.2 ms" share a group.
- **Compact rows**: one line per log. **Monospace font** for messages.
- **Memory**: how many logs to keep (250K to 6M, 3M by default); the oldest fifth goes when the limit is reached.
- **Unity's console**: open it, or read it again from scratch.
- **Explanations from Orbiters**: keep the explanations up to date from Orbiters between releases (on by default).
- **Timings**: measure script reloads, entering Play Mode, and avatar builds and uploads (all on by default), and
  script reloads by package (off by default: it leaks editor memory at every reload).
- **Beta**: the undo history timeline.

**Clear** (Ctrl+L) empties the Mixer console and Unity's console. Compile errors that still stand stay, as in Unity.
Clearing Unity's console from its own window doesn't clear the Mixer console.

### Timings

Each script reload, entry into Play Mode and avatar build or upload becomes a log under the **Timings** source:
"Script reload took 6.6 s", "Entered Play Mode in 9.1 s", "Built and uploaded Rexouium in 2 min 13 s". Its row shows the
time as a stacked bar, one colour per package (the engine's part in grey at the end), scaled to the slowest of its
kind so rows compare at a glance. The details show the total, how it compares with the usual time, each package's
share (click one for its parts: which startup method, which build step) and the same kind of timing over the session
as stacked columns: when a package suddenly costs more, it shows. The timeline has a lane with one mark per timing,
stacked by package; click a mark to show its log.

- **Script reloads**: Unity writes a "Domain Reload Profiling" tree to Editor.log after each reload. With its
  `EnableDomainReloadTimings` diagnostic switch on (the Mixer console turns it on only while “Script reloads by package” is on,
  and back off when you turn it off; in Unity 2022.3 the switch leaks temporary memory at every reload) the tree lists every type and method that runs on load, every before/after-reload callback
  and every window restored; the Mixer console reads it back on a worker thread and gives each entry to the package its code
  belongs to. Unity's console notice about the switch is hidden while it is the only one on.
- **Avatar builds and uploads** (VRChat SDK, through the `Orbiters.Mixer.Console.Editor.VRChat` assembly): from
  Build & Publish or Build & Test to the end of the upload. A probe sits in front of each group of build steps in the
  SDK's list, so each tool's step (VRCFury, MCB, My Avatar, ReFit, Orbiters Toolkit, the SDK's own…) is timed without
  touching it, then the asset bundle and the upload. The asset bundle is split too: Unity times every callback it runs,
  so each asset postprocessor that ran during it (TextMesh Pro's, Poiyomi's…) gets its time and package; the lines
  Unity times itself in Editor.log give the script compilation for the build, the asset imports for the build target
  and the bundle's size; the rest is shader compilation, writing and compressing the bundle.
- **Entering Play Mode**: leaving Edit Mode, the script reload by package, the avatar build steps VRCFury runs on each
  avatar of the scene (merged across avatars), then the scene load and first frames.

### Since which commit

For a message that keeps coming back, the details show **Appearing since**: the commit the project was at when it was
first seen, its subject, when that was and how many commits ago. First and last appearances are kept per message in
`Library/Orbiters/Logger/ledger.bin` (across editor sessions and clears, up to 100,000 messages); the commit comes
from Git's own history of HEAD (`.git/logs/HEAD`), so no Git process runs. Click the hash to copy it.

### Get help (project report)

**Get help** in the top bar (or the lifebuoy on a log's details, which starts the note with that log, or **Tools ›
Orbiters › Get Help**) opens a window that packs what a helper needs into one zip. Each part is a card; a press turns it
on or off, and the choice is remembered:

- **AI diagnosis** (Orbiters members, through Orbiters Toolkit; off until turned on, and connecting never turns it on):
  Orbiters passes the report to a third-party AI provider (which one: orbiters.cc/legal/ai) with `POST
  logger/diagnosis`. It sends the note, the project name, Unity version, platform, system, render pipeline and package
  list, and only from the parts that are on: the 40 most important distinct messages (compile errors first, then the
  most frequent errors, warnings and the latest logs) with the top of their stack traces (Logs), the Git branch, the
  number of changed files and the last 15 commit subjects (Git history), and the scene names, object counts and up to
  40 top-level object names (Scene description). Never files. The answer is a headline, a summary and up to five likely
  causes with steps, shown as plain text and escaped in the Markdown files. It runs while the scene is exported; when
  it fails, **Ask again** in the result (after connecting if needed) writes the answer into the file.
- **Logs**: `logs/logs.txt` (every log, oldest first), `logs/messages.txt` (every distinct message with its count,
  first and last time and stack trace) and the last 32 MB of Unity's `Editor.log` and `Editor-prev.log` (the previous
  session, which may be another project). Unity's logs hold the Windows user name, computer name, Unity machine id,
  license serial and project paths.
- **Git history**: branch, last commit, remotes and Git LFS files, the last 300 commits with their author's name and
  email and the files they touched, uncommitted changes, the reflog and stashes; with Mixer Git, its release records
  (avatar and blueprint ids) and the newest 64 MB of its release pictures. A repository without commits yet is fine.
- **Scene description**: every object of the open scenes with its components (inactive objects, prefab instances and
  missing scripts marked) and the files the scenes use as a folder tree with sizes (packages from the registry
  included).
- **Scene export**: the saved open scenes with their dependencies as a `.unitypackage` (unsaved changes are not in it;
  the card says so, and how big it will be). It holds those files as they are, purchased avatars, shaders and tools
  included: the window says to send it only to people you trust, never in a public channel.

**Anonymize** (under Privacy, off by default, remembered) also masks the Windows user name (with its 8.3 short and
address spellings), computer name, machine and session ids, license serials, emails, Git authors (commits are listed
without them), remote addresses, avatar and blueprint ids and paths outside the project, in every text of the report
and in the AI request; the project folder becomes `%PROJECT%`, other folders `[path]`. Release pictures and the scene
export can't be anonymized, so they are left out (the cards say so). It hides clues too: the window warns that the
problem may be harder to find.

Always masked, anonymized or not, before any text is cut: Orbiters, Git hosting and API tokens, `Authorization`
headers (Bearer, Basic, token), secrets in addresses (`access_token=`, `key=`, `apiKey=`…), JSON and settings values
named like secrets (`"token"`, `"password"`…), cookies, Discord webhooks, JSON web tokens, passwords in addresses
(JSON-escaped ones too) and store license keys; the user folder becomes `%USERPROFILE%` in every spelling. Pictures
and the scene export are copied as they are.

The project's Unity version, platform, system, package list and manifests are always included, and `README.md` at the
top of the zip sums it all up: the note, the project, the diagnosis, the messages that matter most, what was left out,
what each file holds and what was masked. The zip is written as `….zip.partial` beside its final name and renamed once
complete, after checking there is room for it: a cancel or a script reload (which stops the report; the window says so
after it) leaves nothing behind. Git runs read-only (no optional locks, no password prompts, stopped with what it
started at its timeout) from its installed location, never from the project folder. **Copy a message to send** puts a
short summary on the clipboard, escaped so nothing in it becomes a link or a mention.

### Undo history timeline (beta)

Off by default: turn on **Undo history timeline** in the settings. A strip under the activity chart shows Unity's
undo steps (Edit › Undo History) like a video's progress bar: one tick per step (selections smaller), the part already
done filled, the playhead where the project is. Hover a step for a snapshot of the Scene view right after it, its
name and when it happened (its moment is also marked on the activity chart). Click or drag to go there: the Mixer console
undoes or redoes one step at a time until the project is as it was right after that step. Scroll over the strip for
one step back or forward. A step's snapshot is taken once the change is done: nothing recorded for 0.4 s and no drag
going on. It is read from the Scene view window's own framebuffer when it is the visible tab (no extra render), or
rendered by its camera when another tab hides it, scaled down on the GPU and kept as a small JPEG (the last 600).

### How it works

- **Capture.** `Application.logMessageReceivedThreaded` queues each log with its time from any thread; the main
  thread files them a few milliseconds per editor update. Identical texts are stored once, so a message repeated a
  million times costs one string and 16 bytes per occurrence.
- **Unity's console.** At the start of an editor session the Mixer console reads what Unity's console already holds
  (`UnityEditor.LogEntries`, through compiled delegates): the first two lines and timestamp of each row, the full
  entry once per distinct message. Up to 20,000 rows are read at once; more are read a slice per editor update with
  progress in the toolbar, and appear when done. A step behind the live capture, each new console row is matched to
  its log: that adds the object a log is about and the file of engine errors, and any row the Mixer console never received
  live is added, so nothing Unity shows is missing.
- **Compiler messages** come from the compilation pipeline. A compilation that reports a message again replaces the
  earlier one; one that no longer reports it (or a script reload, which only happens once everything compiles) hides
  it, as in Unity's console.
- **Script reloads.** Before a reload the session is written to `Library/Orbiters/Logger/session.bin` (columns as
  raw arrays: a million logs in tens of milliseconds) and read back after it, with what Unity logged in between. The
  file belongs to the editor process; it is deleted when Unity quits.
- **Filtering** is one pass over the logs that fills the rows, group counts, per-level and per-source counts, the
  timeline and the sparklines. Search runs once per distinct text, not per log. Passes over large sessions run on a
  worker thread while the last results stay on screen; new logs are added to the current results as they arrive.
- **The list** is virtual and scrolls by row index, so a hundred million rows scroll like a hundred (Unity's
  ListView loses pixels past a few million rows).
- **No dependency.** The Mixer console only uses Unity's editor API, so it keeps working while other packages fail to
  compile. Its building blocks follow Orbiters Toolkit's look and its press-first buttons.

### Mixer Git

An assembly of its own (`Orbiters.Mixer.Console.Editor.Git`) puts the project's history on the timeline: Mixer Git
releases from its release catalog, and commits from `git log` (the last 30 days, read on a worker thread). Only what
happened inside the logged time shows. It refreshes when Mixer Git records something and every minute while a Mixer
console window is open. Every Git command Mixer Git runs is a log of the Mixer Git source too.

Other tools can add their own markers:

```csharp
TimelineMarkers.Set("mytool", new[] { new TimelineMarker(DateTime.UtcNow, TimelineMarkerKind.Note, "Avatar uploaded") });
```

### Explanations for your tool

```csharp
using Orbiters.Logger.Editor;

[UnityEditor.InitializeOnLoad]
static class MyToolExplanations
{
    static MyToolExplanations()
    {
        LogExplanations.Register(new LogExplanation("mytool.no-avatar", "[MyTool] No avatar", "No avatar selected")
        {
            Source = "My Tool",
            Severity = LogExplanationSeverity.Warning,
            Summary = "My Tool needs an avatar with a VRC Avatar Descriptor.",
            Fixes = new[] { "Select the avatar root in the Hierarchy.", "Click Refresh in My Tool." },
            StackNeedle = null,     // text the stack trace must contain, for messages shared with other tools
            Pattern = null          // a regular expression; named groups fill {name} placeholders
        });
    }
}
```

Registered explanations are tried before the built-in ones. Reference the `Orbiters.Mixer.Console.Editor` assembly
through a version define on `orbiters.mixer`, so your tool still compiles without Mixer.

#### The explanation database

The Mixer console's own explanations are in `Editor/Console/Knowledge/explanations.json` (`{ "explanations": [ … ] }`), one object per
entry: `id`, `needle` (text the message must contain, checked first), optional `pattern` (.NET regex with named
groups for `{name}` placeholders), `stackNeedle`, `levels` (`error`, `warning`, `log`), `priority` (catch-alls use
negative values), `severity` (`harmless`, `info`, `warning`, `problem`, `blocking`), `source`, `title`, `summary`,
`fixes`, `link`, `linkLabel`, and `updated` (yyyy-MM-dd, when the entry's text last changed). Entries tied to a stack
trace are tried first, then by priority, then in file order.

The same list lives on the Orbiters server (`GET /logger/explanations`, with `If-None-Match`), where it is updated
between releases: the Mixer console asks for it every six hours, keeps the last answer per user in
`%LOCALAPPDATA%/Orbiters/Logger/`, and server entries replace local ones with the same id, unless the local one's
`updated` is later (a server entry without it is older than a dated local one). With Orbiters Toolkit
installed (the `Orbiters.Mixer.Console.Editor.Toolkit` assembly), it asks the server Orbiters tools use, as the
signed-in member.

## Mixer Git

**Tools › Orbiters › Mixer Git**: Git for VRChat Unity projects. It was Unit Git: its code keeps that name (namespace
`Orbiters.UnitGit.Editor`, preferences `Orbiters.UnitGit.*`, the `UnitGit-Release` trailer of release checkpoints), so
settings, a commit visit under way and release checkpoints carry over. MCB records its release checkpoints and ReFit
files through it, and My Avatar its Save checkpoints and Versioning card. **Tools › Orbiters › Mixer Git › Health
Checks › All Deterministic** runs its package checks (`Orbiters.UnitGit.Editor.UnitGitEditorHealthChecks.RunAllBatchmode`
in batch mode).

### Features

The window follows JetBrains IDEs' Git tool window, in the style of the other Orbiters tools.

- **Top bar**: the tabs (Changes with its count, Log, Shelf, Conflicts when needed, Backups, Console), what is running,
  the size of the local Git data, the **branch widget** (current branch, commits to push and pull; click for every branch with Checkout, New branch
  from, Merge into current, Update and Delete) and Update (pull, fast-forward only), Push, Fetch, Refresh and Settings.
  Shortcuts: **Ctrl+K** commit, **Ctrl+Shift+K** push, **Ctrl+T** update, **F5** refresh. Results show as short
  notifications; failures stay longer and lead to the Console.
- **Changes**: every change with a box saying whether it goes into the next commit (Git's staging, per file or folder;
  a folder's box shows when only some of its files are in), grouped as changes and unversioned files, in folders
  (single-child chains shown as one row) or flat, with JetBrains' status colours and letters and a filter. Multi-select
  with Ctrl/Shift, arrows, Space to include or exclude, Delete to roll back; the context menu opens, reveals and copies
  paths. **Rollback** returns tracked files to their last commit and moves new files to the trash (assets) after a
  confirmation. Under the list: the message (Ctrl+Enter commits), **Amend** (edits the last commit and its message) and
  **Commit** / **Commit and Push**, which can be clicked while ticked files are still being added (the commit follows).
- **Diff**: side by side, as in JetBrains IDEs: line numbers at each side's edge, added/removed/changed lines tinted and
  the changed part of a changed line highlighted, a middle column joining the two versions of each change, and markers
  beside each side showing where every change is in the whole file (drag them to scroll, click to jump). Unchanged
  stretches fold away with 4 lines kept around each change (the band shows them all, its arrows 20 more lines; the
  toolbar switches folding off). A dropdown ignores whitespace as JetBrains does (trim, all, all and empty lines). It
  opens at the first change; the arrows (F7 / Shift+F7) move between changes, then on to the next file, or between
  matches while searching (Ctrl+F). In Changes, **»** puts one change of the file on disk back as its last commit had it
  (the notification can undo it). Scenes, prefabs and models also offer the Scene view and 3D (beta, below).
- **Log**: branches on the left (HEAD, local, remote in folders; a click shows that branch's history, a double click
  checks it out, a right click has the rest), the commit graph with every lane, branch and tag labels, avatar
  initials, dates and hashes, search (text or hash) and Branch, User and Date filters; older commits load as you
  scroll. Columns give way to the subject when the log is narrow. The selected commit's files (status colours, tree;
  a double click opens its diff in a window) sit above its message, author, date, hash (copy) and parents (a click
  selects them). A commit's menu: New branch from here, Visit this commit (look at an older version, then back to
  the latest), Cherry-Pick, Revert, Rename, Reset current branch to here (Soft, Mixed, Keep, Hard), Squash selected
  commits, copy hash or subject.
- **Shelf**: changes set aside (Git stashes) with their files and diff; **Shelve changes** asks for a name,
  **Unshelve** brings them back and removes the shelf, **Apply** keeps it, **Delete** asks first.
- **Console**: every Git command and its output, newest at the bottom, filterable and copyable.
- initialize a missing project-root Git repository with a VRChat Unity `.gitignore`
- keep MCB's downloaded version patches (`Assets/MCB/assets/*/versions/**/*.bin`) out of Git, also in existing
  repositories; when some were committed earlier, Changes offers **Stop tracking** (they stay on disk)
- connect a GitHub or GitLab remote with their command line tools (Settings or the Log's branches)

Only destructive operations ask first: deleting a branch or a shelf, rolling files back, resetting, squashing or
rewriting commits. Commit, pull (fast-forward only), push (never forced), checkout and merge run at once; Git refuses
anything that would lose local changes.

#### Backups

The **Backups** tab saves the whole history (every commit of every branch, as one `git bundle` file) to a folder of
your choice, by default `Documents/Mixer Git Backups/<project>`. Put it on another drive or in a synced folder
(OneDrive, Google Drive, Dropbox) to survive a broken disk or a deleted project. Each backup is read back in full to
check it; **Check** does it again later, **Restore…** recreates the project in a new, empty folder (this project is not
touched), and older backups beyond **Keep the last** are deleted. **Back up automatically once a day** makes a backup in
the background when Unity opens the project and the history changed since the last one. Uncommitted changes and
ignored files (Library, downloads) are not part of a backup; Git LFS files are kept as pointers.

#### Scene view and 3D (beta)

For scenes, prefabs and other Unity YAML files, the diff pane of Local Changes opens on **Scene view**: one card per
GameObject (or prefab instance) with its components and each changed value as before → after, rotations in degrees and
references by name. VRChat, VRCFury and Modular Avatar components carry their badge. **Text** shows the raw lines.
Unity's bookkeeping fields (hints, serialization versions, object order) are left out.

Models (`.fbx`, `.obj`) and prefabs also have a **3D** view: both versions side by side under one camera (drag to turn,
right-drag to move, scroll to zoom, double-click to reset), each coloured by what changed: moved vertices from yellow to
red, whole parts moved, other materials, other blendshapes or bones, added parts. A table lists every mesh with its
numbers (vertices moved and how far, vertex counts, blendshapes added, removed or reshaped). Models are read straight
from the file with Orbiters Toolkit's mesh comparison, so nothing is imported; prefab versions are imported into `Assets/__UnitGit Compare` (listed in the
repository's local exclude file) and deleted afterwards. In the Log, **Compare** on a scene, prefab or model of a
commit opens the same views for what that commit changed.

#### Conflicts (beta)

When a merge, a shelf or another Git operation leaves conflicting files, a **Conflicts** tab appears with their count.
For each file:

- scenes and prefabs go through Unity's own merge first (`UnityYAMLMerge`, never with an external fallback tool): it
  merges object by object and lists the values it could not decide, with both sides;
- text is split into parts, each chosen as **Mine**, **Theirs** or **Both**, labelled with the object it belongs to;
- **Keep mine** / **Take theirs** keep a whole side (models, images and other binary files only offer this, with the
  3D view to compare them);
- a file deleted on one side is kept or deleted.

The banner shows the progress, then **Finish merge** records the result with Git's prepared message; **Cancel merge**
goes back to before it started.

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

Gaussian splats that Orbiters - N4N0 reconstructed from a VRChat session show in the Scene and Game views, where the
world was captured. In Orbiters - N4N0, **Send to Mixer** in a reconstructed session's detail sends them to the Unity
editor running Mixer; in Unity, **Tools > Orbiters > Mixer Splats…** picks a session's
`reconstruction/splats.ply`. Either way the PLY is copied to `Assets/Orbiters/Splats/<session>.splats` (imported by
Mixer as a splat cloud), with the sky map trained with it (`sky.png`) beside it as `<session>.splatsky`, and an
EditorOnly **Splat Renderer** object (left out of builds) is added to the open scene, selected and framed.

The renderer sorts the splats back to front for each camera on the GPU (a bitonic sort in a compute shader) and draws
them before the transparent pass, behind your scene's geometry where it is in front (built-in render pipeline, compute
shaders). They blend as Orbiters - N4N0's trainer blended them while it fitted them, on encoded sRGB colours: the
splats accumulate in a temporary target that shares the camera's depth buffer, then go over a copy of the camera's
image in encoded sRGB and back in linear light, once per camera. A cloud's sky map is drawn first, wherever the
camera drew nothing nearer than the far plane. Move the object to offset them. Each editor keeps an inbox for Orbiters - N4N0 under
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
