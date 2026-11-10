using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // The richer rows: a picture of the meshes an object holds, a switch for whether it is active, a ball that turns a
    // light, the main bones of an avatar's armature, My Avatar's drop field on the avatar root and its ReFit question on
    // an item it added, and Update where a newer version waits (a gallery item, the avatar's custom base).
    internal sealed partial class MixerHierarchyWindow
    {
        private bool thumbnailsPending;
        private double lastThumbnailPoll;
        // The picture key of each row (by instance ID), worked out again only when something below the object changed
        // (HierarchyRow.PictureRevision); the picture of a key no longer used is freed.
        private sealed class Picture
        {
            public string Key;
            public bool OnDisk;
            public int Revision;
            public long Used;
        }
        private readonly Dictionary<int, Picture> pictures = new Dictionary<int, Picture>();
        private long pictureClock;
        // Rows holding more renderers than this (a world's folders) show their icon: a picture renders them all.
        private const int PictureRenderers = 128;
        // Pictures kept by this window at most (about 147 KB each): the least recently shown go first, never one on screen.
        internal const int PicturesKept = 160;

        // ---- Making the parts (once per pooled row) ---------------------------------------------------------------

        private VisualElement MakeActiveBox(VisualElement row)
        {
            var box = MixerUi.Box("mxh-row__active");
            box.tooltip = "Active (the object and its children)";
            var check = new VectorIcon(IconGlyph.Check); check.AddToClassList("mxh-row__active-check"); box.Add(check);
            // The box flips on the press; the object follows in the same event, one Undo step.
            box.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                evt.StopPropagation();
                var go = RowOf(row)?.Object;
                if (go == null) return;
                bool on = !go.activeSelf;
                box.EnableInClassList("mxh-row__active--on", on);
                Undo.RecordObject(go, (on ? "Activate " : "Deactivate ") + go.name);
                go.SetActive(on);
                EditorUtility.SetDirty(go);
                MarkEdited();
            });
            return box;
        }

        private VisualElement MakeLightSphere(VisualElement row)
        {
            Light Light() => RowOf(row)?.Light;
            OrbitSphere sphere = null;
            sphere = new OrbitSphere(89f, (yaw, tilt) =>
            {
                var light = Light();
                if (light == null) return;
                var t = light.transform;
                Undo.RecordObject(t, "Turn " + light.name);
                t.rotation = Quaternion.Euler(tilt, yaw, t.eulerAngles.z);
                EditorUtility.SetDirty(t);
                var detail = row.Q<Label>(className: "mxh-row__detail");
                if (detail != null) detail.text = LightDetail(light);
            }, () =>
            {
                var light = Light();
                if (light == null) return;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Turn " + light.name);
            }, () =>
            {
                // Double-click: Unity's own new directional light faces this way.
                var light = Light();
                if (light == null) return;
                Undo.RecordObject(light.transform, "Reset " + light.name + "'s turn");
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                sphere.SetValueWithoutNotify(-30f, 50f);
                var detail = row.Q<Label>(className: "mxh-row__detail");
                if (detail != null) detail.text = LightDetail(light);
            });
            sphere.AddToClassList("mxh-row__sphere");
            sphere.tooltip = "Drag to turn the light (sideways around, up and down to tilt); double-click resets it";
            return sphere;
        }

        private VisualElement MakeSecondLine(VisualElement row)
        {
            var line = MixerUi.Box("mxh-row__line2", PickingMode.Ignore);
            line.Add(MixerUi.Text("", "mxh-row__detail"));
            var extras = MixerUi.Box("mxh-row__extras", PickingMode.Ignore);
            line.Add(extras);

            // The armature's main bones, one click each (moved into the first line when they fit there).
            var bones = MixerUi.Box("mxh-bones", PickingMode.Ignore);
            Bone(bones, row, "Chest", "chest", b => b.Chest);
            Bone(bones, row, "Head", "head", b => b.Head);
            Bone(bones, row, "L hand", "left hand", b => b.LeftHand);
            Bone(bones, row, "R hand", "right hand", b => b.RightHand);
            Bone(bones, row, "L foot", "left foot", b => b.LeftFoot);
            Bone(bones, row, "R foot", "right foot", b => b.RightFoot);
            extras.Add(bones);

            // An item My Avatar added that seems not to fit: ReFit, as its fit card offers.
            var refit = MixerUi.Pill("ReFit ?", () =>
            {
                var r = RowOf(row);
                if (r?.RefitTool != null && r.Object != null) r.RefitTool.Refit(r.Object);
            }, "accent", null, "It may not fit this avatar: ReFit it to the body, as My Avatar's fit card offers");
            refit.AddToClassList("mxh-refit");
            refit.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation(), TrickleDown.TrickleDown);
            extras.Add(refit);

            // A newer version of an item a tool added (a gallery asset): Update, as its line in My Avatar offers.
            extras.Add(MakeUpdatePill(row, "mxh-row__update"));
            return line;
        }

        // Update: the tool's own update starts at the press (with its questions and progress), the pill saying so at once;
        // then the row follows what the tool reports. The tool is asked again at the press: an update done elsewhere since
        // the rows were made (or one already under way) starts nothing, and the pill goes with the next rebuild.
        private Button MakeUpdatePill(VisualElement row, string className)
        {
            Button pill = null;
            pill = MixerUi.Pill("Update", () =>
            {
                var r = RowOf(row);
                var update = r?.Object != null ? AvatarObjectTools.UpdateOf(r.Object) : null;
                if (r != null) r.Update = update;
                if (update?.Start == null || update.Running)
                {
                    // Nothing newer any more: the pill goes now, the row with the next rebuild.
                    if (update == null) MixerUi.Show(pill, false);
                    model.ToolsChanged(); MarkChanged();
                    return;
                }
                MixerUi.SetPillText(pill, "Updating…");
                pill.SetEnabled(false);
                update.Start();
            }, "accent");
            pill.AddToClassList("mxh-update");
            pill.AddToClassList(className);
            pill.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation(), TrickleDown.TrickleDown);
            return pill;
        }

        // On a VRChat avatar's row the update is its custom base's (MCB), never the items' below it (each has its own line):
        // the pill says "Update base".
        private static void BindUpdate(Button pill, AvatarObjectUpdate update, bool customBase = false)
        {
            if (pill == null) return;
            MixerUi.Show(pill, update != null);
            if (update == null) return;
            MixerUi.SetPillText(pill, update.Running ? "Updating…" : update.Failed ? "Retry update" : customBase ? "Update base" : "Update");
            pill.SetEnabled(!update.Running);
            string name = string.IsNullOrEmpty(update.Name) ? customBase ? "the custom base" : "it" : update.Name;
            string what = customBase && !name.StartsWith("the custom base", System.StringComparison.Ordinal) ? "the custom base " + name : name;
            pill.tooltip = update.Running ? (string.IsNullOrEmpty(update.Status) ? "Updating " + what + " to " + update.To : update.Status)
                : update.Failed ? "The update of " + what + " to " + update.To + " failed" + (string.IsNullOrEmpty(update.Status) ? "" : ": " + update.Status) + ". Click to try again."
                : "Update " + what + " from " + update.From + " to " + update.To;
        }

        // A VRChat avatar's second and third lines: its name on VRChat and its custom base; its performance rank, whether
        // it is public, and its link.
        private VisualElement MakeAvatarLines(VisualElement row)
        {
            var lines = MixerUi.Box("mxh-avatar", PickingMode.Ignore);
            var names = MixerUi.Box("mxh-avatar__line", PickingMode.Ignore);
            var uploaded = MixerUi.Text("", "mxh-avatar__name");
            uploaded.pickingMode = PickingMode.Position;
            names.Add(uploaded);
            var customBase = MixerUi.Text("", "mxh-avatar__base");
            customBase.pickingMode = PickingMode.Position;
            names.Add(customBase);
            // A newer version of the custom base (MCB): Update beside its name.
            names.Add(MakeUpdatePill(row, "mxh-avatar__update"));
            lines.Add(names);
            var facts = MixerUi.Box("mxh-avatar__line", PickingMode.Ignore);
            var rank = MixerUi.Box("mxh-avatar__fact mxh-avatar__rank");
            var rankIcon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            rankIcon.AddToClassList("mxh-avatar__rank-icon");
            rank.Add(rankIcon);
            rank.Add(MixerUi.Text("", "mxh-avatar__fact-text"));
            facts.Add(rank);
            var visibility = MixerUi.Box("mxh-avatar__fact mxh-avatar__visibility");
            var visibilityIcon = new VectorIcon(IconGlyph.People) { pickingMode = PickingMode.Ignore };
            visibilityIcon.AddToClassList("mxh-avatar__fact-icon");
            visibility.Add(visibilityIcon);
            visibility.Add(MixerUi.Text("", "mxh-avatar__fact-text"));
            facts.Add(visibility);
            var link = MixerUi.Icon(IconGlyph.Link, "Copy the avatar's link on VRChat", () =>
            {
                var r = RowOf(row);
                string url = r?.Object != null ? HierarchyVrchat.Describe?.Invoke(r.Object)?.Link : null;
                if (url == null) return;
                EditorGUIUtility.systemCopyBuffer = url;
                toasts.Show("Copied " + url);
            }, "mxh-avatar__link");
            link.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            facts.Add(link);
            lines.Add(facts);
            return lines;
        }

        private void BindAvatarLines(RowView view, HierarchyRow row)
        {
            var facts = HierarchyVrchat.Describe?.Invoke(row.Object);
            var uploaded = view.Uploaded;
            // Its assembly compiles only with the VRChat Avatars SDK 3.7 or later, and loads only when every script it
            // depends on compiled: say which of the two it is.
            if (facts == null) uploaded.text = EditorUtility.scriptCompilationFailed
                ? "Mixer's VRChat part is not loaded: scripts have compile errors (see the Console)"
                : "Mixer's VRChat part is not loaded (VRChat SDK 3.7 or later)";
            else if (facts.UploadedName != null) uploaded.text = facts.UploadedName;
            else uploaded.text = facts.Note ?? string.Empty;
            uploaded.EnableInClassList("mxh-avatar__name--note", facts?.UploadedName == null);
            uploaded.tooltip = facts?.UploadedName != null ? "Its name on VRChat (" + facts.BlueprintId + ")" : facts?.BlueprintId;
            var info = CustomBaseOf(row);
            var customBase = view.CustomBase;
            MixerUi.Show(customBase, info != null && !string.IsNullOrEmpty(info.Name));
            if (info != null)
            {
                customBase.text = info.Name;
                customBase.tooltip = "Custom base" + (string.IsNullOrEmpty(info.BaseName) ? "" : " of " + info.BaseName) + (info.Source == "MCB" ? " (My Custom Base)" : "");
            }
            BindUpdate(view.AvatarUpdate, row.Update, customBase: true);
            var rank = view.Rank;
            MixerUi.Show(rank, facts != null);
            if (facts != null)
            {
                view.RankIcon.image = facts.RankIcon;
                MixerUi.Show(view.RankIcon, facts.RankIcon != null);
                view.RankText.text = facts.Rank ?? "Ranking\u2026";
                rank.tooltip = "Its performance rank on " + facts.RankPlatform + " as it is in the scene, worked out by the VRChat SDK (the build target's platform)";
            }
            var visibility = view.Visibility;
            MixerUi.Show(visibility, facts?.Visibility != null);
            if (facts?.Visibility != null)
            {
                bool shared = facts.Visibility == "Public";
                view.VisibilityIcon.Glyph = shared ? IconGlyph.People : IconGlyph.Lock;
                view.VisibilityText.text = facts.Visibility;
                visibility.tooltip = shared ? "Public on VRChat: anyone can find and wear it" : "Private on VRChat: only its owner can wear it";
            }
            MixerUi.Show(view.Link, facts?.Link != null);
        }

        // A custom base's description per avatar root, kept until a custom base is applied or reset (CustomBases.Changed)
        // or the avatar's row is read again: a provider reads the whole body for it, and avatar rows bind on every click.
        private readonly Dictionary<int, CustomBaseInfo> customBases = new Dictionary<int, CustomBaseInfo>();
        private readonly Dictionary<int, int> customBaseRevisions = new Dictionary<int, int>();

        private CustomBaseInfo CustomBaseOf(HierarchyRow row)
        {
            // Inputs: the avatar root (its instance ID) and everything below it, which PictureRevision counts.
            if (customBases.TryGetValue(row.InstanceId, out var known) && customBaseRevisions.TryGetValue(row.InstanceId, out int at) && at == row.PictureRevision)
                return known;
            var info = CustomBases.Describe(row.Object.transform);
            customBases[row.InstanceId] = info;
            customBaseRevisions[row.InstanceId] = row.PictureRevision;
            return info;
        }


        // My Avatar's drop field on the avatar root: what is dropped on it goes to the avatar as on My Avatar's own.
        private VisualElement MakeDropField(VisualElement row)
        {
            var drop = MixerUi.Box("mxh-drop");
            var dropIcon = new VectorIcon(IconGlyph.Download); dropIcon.AddToClassList("mxh-drop__icon"); drop.Add(dropIcon);
            drop.Add(MixerUi.Text("Drop anything", "mxh-drop__text"));
            drop.tooltip = "Drop clothing, accessories (packages, prefabs, models) or textures here: My Avatar puts them on this avatar";
            // Dashed like My Avatar's own drop field.
            drop.generateVisualContent += DrawDashes;
            void Over(bool over) { drop.EnableInClassList("mxh-drop--over", over); drop.MarkDirtyRepaint(); }
            drop.RegisterCallback<DragEnterEvent>(evt => { if (DroppedPaths().Length > 0 && RowOf(row)?.DropTool != null) Over(true); });
            drop.RegisterCallback<DragLeaveEvent>(_ => Over(false));
            drop.RegisterCallback<DragExitedEvent>(_ => Over(false));
            drop.RegisterCallback<PointerEnterEvent>(_ => drop.MarkDirtyRepaint());
            drop.RegisterCallback<PointerLeaveEvent>(_ => drop.MarkDirtyRepaint());
            drop.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                // Scene objects dragged to reparent them pass through to the hierarchy.
                if (DroppedPaths().Length == 0 || RowOf(row)?.DropTool == null) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                MixerUi.Show(dragLine, false);
                evt.StopPropagation();
            });
            drop.RegisterCallback<DragPerformEvent>(evt =>
            {
                var r = RowOf(row);
                var paths = DroppedPaths();
                if (paths.Length == 0 || r?.DropTool == null || r.Object == null) return;
                DragAndDrop.AcceptDrag();
                Over(false);
                MixerUi.Show(dragLine, false);
                EndDrag();
                evt.StopPropagation();
                drop.AddToClassList("mxh-drop--busy");
                drop.Q<Label>().text = "Adding…";
                r.DropTool.Drop(r.Object, paths);
            });
            drop.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            return drop;
        }

        private void Bone(VisualElement parent, VisualElement row, string label, string what, System.Func<QuickBones, Transform> bone)
        {
            var button = new Button { text = label, tooltip = "Select the " + what + " bone" };
            button.AddToClassList("mxh-bone");
            MixerUi.Press(button, () =>
            {
                var target = RowOf(row)?.Bones;
                var t = target != null ? bone(target) : null;
                if (t == null) return;
                BoneSubSelection.Clear();
                Selection.activeGameObject = t.gameObject;
                anchorId = t.gameObject.GetInstanceID();
            });
            button.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation(), TrickleDown.TrickleDown);
            button.userData = bone;
            parent.Add(button);
        }

        // ---- Binding ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The parts of one pooled row, found once when it is made (a bind runs for every row on screen at each refresh),
        /// with the badges and component icons it may show made once too and only shown or hidden at a bind.
        /// </summary>
        private sealed class RowView
        {
            public const int ComponentIcons = 4;
            public MixerIndent Indent;
            public VisualElement Chevron, Dot, Badges, Components, Pick, Eye, Active, Line, Line2, Extras, Drop, Bones, Refit, Switches, Avatar, VrchatMark;
            public Image Icon, Thumbnail;
            public VectorIcon Glyph, PickIcon, EyeIcon;
            public Label Name, Detail, DropText;
            public TextField Rename;
            public OrbitSphere Sphere;
            public IMGUIContainer Decorations;
            public Button Update, AvatarUpdate;
            public Label Uploaded, CustomBase, RankText, VisibilityText;
            public VisualElement Rank, Visibility, Link;
            public Image RankIcon;
            public VectorIcon VisibilityIcon;
            // The badges in the order they show: VRChat's logo, prefab, missing script, painting or posing.
            public VisualElement Logo;
            public Label PrefabBadge, MissingBadge, PaintingBadge, PosingBadge;
            public readonly Image[] ComponentIcon = new Image[ComponentIcons];
            public Label More;
            public int ComponentsShown;
            // What the row was last bound to, in which refresh: a second bind of the same pass is skipped.
            public int Generation = -1, Index = -1;
            public HierarchyRow Row;

            public static RowView Of(VisualElement row)
            {
                var view = new RowView
                {
                    Indent = row.Q<MixerIndent>(className: "mxh-row__indent"),
                    Chevron = row.Q(className: "mxh-row__chevron"),
                    Dot = row.Q(className: "mxh-row__dot"),
                    Icon = row.Q<Image>(className: "mxh-row__icon"),
                    Glyph = row.Q<VectorIcon>(className: "mxh-row__glyph"),
                    Thumbnail = row.Q<Image>(className: "mxh-row__thumbnail"),
                    VrchatMark = row.Q(className: "mxh-row__vrchat-mark"),
                    Name = row.Q<Label>(className: "mxh-row__name"),
                    Rename = row.Q<TextField>(className: "mxh-row__rename"),
                    Badges = row.Q(className: "mxh-row__badges"),
                    Components = row.Q(className: "mxh-row__components"),
                    Pick = row.Q(className: "mxh-row__pick"),
                    Eye = row.Q(className: "mxh-row__eye"),
                    Active = row.Q(className: "mxh-row__active"),
                    Sphere = row.Q<OrbitSphere>(className: "mxh-row__sphere"),
                    Line = row.Q(className: "mxh-row__line"),
                    Line2 = row.Q(className: "mxh-row__line2"),
                    Extras = row.Q(className: "mxh-row__extras"),
                    Detail = row.Q<Label>(className: "mxh-row__detail"),
                    Drop = row.Q(className: "mxh-drop"),
                    Bones = row.Q(className: "mxh-bones"),
                    Refit = row.Q(className: "mxh-refit"),
                    Switches = row.Q(className: "mxh-row__switches"),
                    Avatar = row.Q(className: "mxh-avatar"),
                    Decorations = row.Q<IMGUIContainer>(className: "mxh-row__decorations"),
                    Update = row.Q<Button>(className: "mxh-row__update"),
                };
                view.PickIcon = view.Pick.Q<VectorIcon>();
                view.EyeIcon = view.Eye.Q<VectorIcon>();
                view.DropText = view.Drop.Q<Label>();
                var lines = view.Avatar;
                view.Uploaded = lines.Q<Label>(className: "mxh-avatar__name");
                view.CustomBase = lines.Q<Label>(className: "mxh-avatar__base");
                view.AvatarUpdate = lines.Q<Button>(className: "mxh-avatar__update");
                view.Rank = lines.Q(className: "mxh-avatar__rank");
                view.RankIcon = view.Rank.Q<Image>();
                view.RankText = view.Rank.Q<Label>();
                view.Visibility = lines.Q(className: "mxh-avatar__visibility");
                view.VisibilityIcon = view.Visibility.Q<VectorIcon>();
                view.VisibilityText = view.Visibility.Q<Label>();
                view.Link = lines.Q(className: "mxh-avatar__link");
                // Made once, hidden until a bind shows them (hidden elements take no room: the line looks as before).
                view.Logo = VRChatLogo.Create();
                if (view.Logo != null)
                {
                    view.Logo.tooltip = "A VRChat avatar (it has an avatar descriptor)";
                    view.Logo.pickingMode = PickingMode.Position;
                    view.Logo.AddToClassList("mxh-row__vrchat");
                    view.Badges.Add(view.Logo);
                }
                view.PrefabBadge = (Label)Badge("prefab", "A prefab instance", "mxh-badge--prefab");
                view.MissingBadge = (Label)Badge("missing script", "A component's script is missing", "mxh-badge--missing");
                view.PaintingBadge = (Label)Badge("painting", "The bone Weight Paint paints and poses; the mesh stays selected", "mxh-badge--mixer");
                view.PosingBadge = (Label)Badge("posing", "Posed in Weight Paint; the painted mesh is not skinned to it", "mxh-badge--mixer");
                foreach (var badge in new VisualElement[] { view.PrefabBadge, view.MissingBadge, view.PaintingBadge, view.PosingBadge }) view.Badges.Add(badge);
                foreach (var badge in view.Badges.Children()) MixerUi.Show(badge, false);
                for (int i = 0; i < ComponentIcons; i++)
                {
                    var image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Position };
                    image.AddToClassList("mxh-row__component");
                    MixerUi.Show(image, false);
                    view.Components.Add(image);
                    view.ComponentIcon[i] = image;
                }
                view.More = MixerUi.Text("+", "mxh-row__more");
                MixerUi.Show(view.More, false);
                view.Components.Add(view.More);
                row.userData = view;
                return view;
            }
        }

        private static RowView ViewOf(VisualElement element) => element.userData as RowView;

        private void BindRich(RowView view, VisualElement element, HierarchyRow row, bool tall)
        {
            var active = view.Active;
            var icon = view.Icon;
            var glyph = view.Glyph;
            var thumbnail = view.Thumbnail;
            var sphere = view.Sphere;
            var line = view.Line;
            var line2 = view.Line2;
            var extras = view.Extras;
            var detail = view.Detail;
            var drop = view.Drop;
            var bones = view.Bones;
            var refit = view.Refit;
            bool isObject = !row.IsScene && row.Object != null;
            MixerUi.Show(active, isObject);
            if (isObject) active.EnableInClassList("mxh-row__active--on", row.Object.activeSelf);
            string pictureKey = tall && row.HasMeshes && row.Light == null ? ThumbnailKey(row) : null;
            // A picture that could not be made (nothing shown below, an error) leaves the row its icon.
            bool picture = pictureKey != null && !ModelThumbnails.HasFailed(pictureKey, transparent: true);
            bool vector = isObject && (row.Bones != null || row.IsBone);
            element.EnableInClassList("mxh-row--picture", picture);
            MixerUi.Show(thumbnail, picture);
            var vrchatMark = view.VrchatMark;
            if (vrchatMark != null) MixerUi.Show(vrchatMark, picture && row.VrchatAvatar);
            MixerUi.Show(glyph, !picture && vector);
            MixerUi.Show(icon, !picture && !vector);
            if (vector) glyph.Glyph = row.Bones != null ? IconGlyph.Armature : IconGlyph.Bones;
            bool avatar = tall && row.VrchatAvatar;
            element.EnableInClassList("mxh-row--avatar", avatar);
            MixerUi.Show(line2, tall && !avatar);
            MixerUi.Show(view.Avatar, avatar);
            bool light = isObject && row.Light != null && !Compact;
            MixerUi.Show(sphere, light);
            bool dropShown = isObject && row.DropTool != null && !Compact;
            MixerUi.Show(drop, dropShown);
            // The bones sit in the first line when the window is wide enough, else on the second.
            bool bonesShown = isObject && row.Bones != null && !Compact;
            MixerUi.Show(bones, bonesShown);
            if (bonesShown)
            {
                var into = tall ? extras : line;
                if (bones.parent != into)
                {
                    if (into == line) line.Insert(line.IndexOf(view.Badges) + 1, bones);
                    else extras.Insert(0, bones);
                }
                foreach (var button in bones.Children())
                    if (button.userData is System.Func<QuickBones, Transform> bone) button.SetEnabled(bone(row.Bones) != null);
            }
            MixerUi.Show(refit, tall && row.RefitTool != null);
            BindUpdate(view.Update, tall && !avatar ? row.Update : null);
            // The visibility and picking switches go to the row's right end beside a light's ball or a drop field.
            var switches = view.Switches;
            var switchesInto = light || dropShown ? element : line;
            if (switches.parent != switchesInto) switchesInto.Add(switches);
            else if (switchesInto == element && element.IndexOf(switches) != element.childCount - 1) switches.BringToFront();
            element.EnableInClassList("mxh-row--switches-right", switchesInto == element);
            if (light)
            {
                var e = row.Light.transform.eulerAngles;
                sphere.SetValueWithoutNotify(Mathf.DeltaAngle(0f, e.y), Mathf.DeltaAngle(0f, e.x));
            }
            if (dropShown)
            {
                bool busy = row.DropTool.Busy(row.Object);
                drop.EnableInClassList("mxh-drop--busy", busy);
                view.DropText.text = busy ? "Adding…" : "Drop anything";
                drop.MarkDirtyRepaint();
            }
            if (!tall) return;
            if (avatar) BindAvatarLines(view, row);

            if (picture)
            {
                var texture = ModelThumbnails.Get(row.Object, pictureKey, transparent: true, keepOnDisk: PictureOnDisk(row));
                thumbnail.image = texture;
                thumbnail.EnableInClassList("mxh-row__thumbnail--waiting", texture == null);
                if (texture == null) thumbnailsPending = true;
            }
            detail.text = row.Light != null ? LightDetail(row.Light) : Detail(row);
            // Bones share the line with the detail at most half and half; lone pills keep their width beside the whole detail.
            detail.EnableInClassList("mxh-row__detail--short", bonesShown);
            extras.EnableInClassList("mxh-row__extras--pills", !bonesShown);
        }

        /// <summary>The lines a row takes: three for a VRChat avatar, two for <see cref="Tall"/> rows, else one.</summary>
        private int Lines(HierarchyRow row) => !Tall(row) ? 1 : row.VrchatAvatar ? 3 : 2;

        /// <summary>Two lines for rows with more than a name: meshes (with their picture), a light, My Avatar's drop
        /// field or ReFit question, an update, or an armature whose bone buttons do not fit beside its name. One in
        /// compact rows.</summary>
        private bool Tall(HierarchyRow row)
        {
            if (row == null || row.IsScene || row.Object == null || Compact) return false;
            if (row.IsBone) return false;
            return row.HasMeshes || row.Light != null || row.DropTool != null || row.RefitTool != null || row.Update != null || (row.Bones != null && !BonesFitInline(row));
        }

        // Name, badges and six bone buttons on one line: the indent plus about 430 points.
        private bool BonesFitInline(HierarchyRow row)
        {
            float width = list != null ? list.layout.width : float.NaN;
            return !float.IsNaN(width) && width >= Mathf.Max(0, row.Depth - 1) * 14f + 470f;
        }

        // The armature rows change height as the window crosses the width where their buttons fit beside the name (each
        // row at its own width: its indent counts).
        private float laidOutWidth = -1f;
        private void OnWidthChanged()
        {
            float width = list.layout.width;
            if (Mathf.Abs(width - laidOutWidth) < 0.5f) return;
            float before = laidOutWidth;
            laidOutWidth = width;
            bool Fits(HierarchyRow r, float at) => at >= Mathf.Max(0, r.Depth - 1) * 14f + 470f;
            if (model.Rows.Any(r => r.Bones != null && (before < 0f || Fits(r, before) != Fits(r, width)))) RefreshRows();
        }

        // Unity's icon of the object, with Mixer's own for what Unity draws as a plain cube: a light, a folder (open or
        // folded) for an empty object that groups others. Bones and armatures have vector icons (BindRich).
        private static Texture IconOf(HierarchyRow row)
        {
            if (row.Object == null) return null;
            if (row.Light != null && !row.PrefabRoot) return AssetPreview.GetMiniTypeThumbnail(typeof(Light));
            if (row.IsFolder) return EditorGUIUtility.IconContent(row.Expanded ? "FolderOpened Icon" : "Folder Icon").image;
            return PrefabUtility.GetIconForGameObject(row.Object);
        }

        // Pictures are made one per frame: rows waiting for one look again a few times a second.
        private void PollThumbnails()
        {
            if (!thumbnailsPending || list == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastThumbnailPoll < 0.15) return;
            lastThumbnailPoll = now;
            thumbnailsPending = false;
            bool failed = false;
            foreach (var element in list.Rows)
            {
                if (element.resolvedStyle.display == DisplayStyle.None || !element.ClassListContains("mxh-row--picture")) continue;
                var row = RowOf(element);
                var thumbnail = ViewOf(element)?.Thumbnail;
                if (row?.Object == null || !row.HasMeshes || thumbnail == null || thumbnail.image != null) continue;
                string key = ThumbnailKey(row);
                if (key == null) continue;
                var texture = ModelThumbnails.Get(row.Object, key, transparent: true, keepOnDisk: PictureOnDisk(row));
                if (texture == null)
                {
                    // Failed for good: the row shows its icon instead.
                    if (ModelThumbnails.HasFailed(key, transparent: true)) failed = true;
                    else thumbnailsPending = true;
                    continue;
                }
                thumbnail.image = texture;
                thumbnail.RemoveFromClassList("mxh-row__thumbnail--waiting");
            }
            if (failed) RefreshRows();
        }

        // What the picture shows: the meshes and materials below the object and whether they show, so a commit (a new
        // mesh), a material swap or a switched-off part makes a new one. Null for a row with too many renderers to
        // picture. Worked out again only when the row's PictureRevision moved (a change at or below the object).
        //
        // Key inputs, for every renderer below the object holding a mesh: the mesh, whether the renderer is enabled and
        // its object active, and each material slot's material; plus the row's object. When every mesh and material is an
        // asset and the object is in a saved scene, they are named by GUID, local file id and the asset's dependency hash
        // (its content), and the object by its GlobalObjectId: the key holds across editor starts and the picture is kept
        // on disk. Otherwise (a working mesh, a baked or generated one, an unsaved scene) by instance IDs, for the session.
        private string ThumbnailKey(HierarchyRow row)
        {
            if (pictures.TryGetValue(row.InstanceId, out var known) && known.Revision == row.PictureRevision)
            {
                known.Used = ++pictureClock;
                return known.Key;
            }
            var renderers = row.Object.GetComponentsInChildren<Renderer>(true);
            string made = null;
            bool onDisk = false;
            if (renderers.Length <= PictureRenderers)
            {
                made = SessionKey(row, renderers);
                string lasting = LastingKey(row, renderers);
                if (lasting != null) { made = lasting; onDisk = true; }
            }
            if (known != null && known.Key != made) ModelThumbnails.Release(known.Key, transparent: true);
            if (made == null) { pictures.Remove(row.InstanceId); return null; }
            if (known == null) pictures[row.InstanceId] = known = new Picture();
            known.Key = made;
            known.OnDisk = onDisk;
            known.Revision = row.PictureRevision;
            known.Used = ++pictureClock;
            return made;
        }

        private bool PictureOnDisk(HierarchyRow row) => pictures.TryGetValue(row.InstanceId, out var known) && known.OnDisk;

        private static string SessionKey(HierarchyRow row, Renderer[] renderers)
        {
            var key = new StringBuilder("mixer-hierarchy|").Append(row.InstanceId);
            foreach (var renderer in renderers)
            {
                var mesh = MeshOf(renderer);
                if (mesh == null) continue;
                key.Append('|').Append(mesh.GetInstanceID()).Append(renderer.enabled && renderer.gameObject.activeInHierarchy ? '+' : '-');
                foreach (var material in renderer.sharedMaterials) key.Append(',').Append(material != null ? material.GetInstanceID() : 0);
            }
            return key.ToString();
        }

        // The same inputs named so they hold across editor starts, or null when one of them cannot be.
        private static string LastingKey(HierarchyRow row, Renderer[] renderers)
        {
            var scene = row.Object.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || EditorApplication.isPlayingOrWillChangePlaymode) return null;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(row.Object);
            if (id.identifierType == 0) return null;
            var key = new StringBuilder("mixer-hierarchy-asset|3|").Append(id.ToString());
            var hashes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            bool Append(Object asset)
            {
                if (asset == null) { key.Append(",-"); return true; }
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long local) || string.IsNullOrEmpty(guid)) return false;
                if (!hashes.TryGetValue(guid, out string hash))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) return false;
                    hashes[guid] = hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                }
                key.Append(',').Append(guid).Append(':').Append(local).Append(':').Append(hash);
                return true;
            }
            foreach (var renderer in renderers)
            {
                var mesh = MeshOf(renderer);
                if (mesh == null) continue;
                key.Append('|');
                if (!Append(mesh)) return null;
                key.Append(renderer.enabled && renderer.gameObject.activeInHierarchy ? '+' : '-');
                foreach (var material in renderer.sharedMaterials) if (!Append(material)) return null;
            }
            // What the picture is rendered from beyond the assets: where each part is, the bones' pose, the shapes' weights.
            key.Append("|state:").Append(RenderedState(row.Object.transform, renderers).ToString("x16"));
            return key.ToString();
        }

        /// <summary>
        /// A hash of the scene state ModelThumbnails renders a row's renderers with: each renderer's matrix relative to the
        /// row's object, a skinned mesh's bones relative to it (its pose, which BakeMesh reads) and its blendshape weights.
        /// Values are rounded (1e-4) so float noise does not change the key. Fixed order: renderers, then bones and shapes
        /// in index order.
        /// </summary>
        internal static ulong RenderedState(Transform root, Renderer[] renderers)
        {
            ulong hash = 14695981039346656037UL;
            void Add(float value)
            {
                long q = float.IsNaN(value) || float.IsInfinity(value) ? long.MinValue : (long)System.Math.Round(value * 10000.0);
                for (int b = 0; b < 8; b++) { hash ^= (byte)(q >> (b * 8)); hash *= 1099511628211UL; }
            }
            void AddMatrix(Matrix4x4 m) { for (int i = 0; i < 16; i++) Add(m[i]); }
            var toRoot = root.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                if (renderer == null || MeshOf(renderer) == null) continue;
                AddMatrix(toRoot * renderer.transform.localToWorldMatrix);
                if (!(renderer is SkinnedMeshRenderer skinned)) continue;
                foreach (var bone in skinned.bones) { if (bone != null) AddMatrix(toRoot * bone.localToWorldMatrix); else Add(float.NaN); }
                int shapes = skinned.sharedMesh.blendShapeCount;
                for (int i = 0; i < shapes; i++) Add(skinned.GetBlendShapeWeight(i));
            }
            return hash;
        }

        private static Mesh MeshOf(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;

        // Pictures of rows gone for good are freed.
        private void OnRowForgotten(int instanceId)
        {
            customBases.Remove(instanceId);
            customBaseRevisions.Remove(instanceId);
            if (!pictures.TryGetValue(instanceId, out var known)) return;
            pictures.Remove(instanceId);
            ModelThumbnails.Release(known.Key, transparent: true);
        }

        // Past PicturesKept, the pictures shown longest ago are freed (never one on screen): scrolling a world's thousands
        // of rows keeps a bounded number of textures.
        // Called after rebuilds and from the window's tick (scrolling binds rows and makes pictures without a rebuild).
        internal void ReleaseUnusedPictures()
        {
            if (pictures.Count <= PicturesKept || list == null) return;
            var shown = new HashSet<int>();
            // The list shows and hides its rows by their inline display (read now, not after the next layout).
            foreach (var element in list.Rows)
                if (element.style.display.value != DisplayStyle.None && ViewOf(element)?.Row is HierarchyRow row) shown.Add(row.InstanceId);
            var oldest = new List<KeyValuePair<int, Picture>>(pictures);
            oldest.Sort((a, b) => a.Value.Used.CompareTo(b.Value.Used));
            foreach (var pair in oldest)
            {
                if (pictures.Count <= PicturesKept) break;
                if (shown.Contains(pair.Key)) continue;
                pictures.Remove(pair.Key);
                ModelThumbnails.Release(pair.Value.Key, transparent: true);
            }
        }

        /// <summary>How many pictures this window holds (tests).</summary>
        internal int PicturesHeld => pictures.Count;

        /// <summary>Holds a picture key for a row as a bind would (tests): the eviction's input without rendering.</summary>
        internal void HoldPicture(int instanceId, string key) => pictures[instanceId] = new Picture { Key = key, Used = ++pictureClock, Revision = int.MinValue };

        /// <summary>Whether this window holds a picture for the row (tests).</summary>
        internal bool HoldsPicture(int instanceId) => pictures.ContainsKey(instanceId);

        private void ReleasePictures()
        {
            foreach (var known in pictures.Values) ModelThumbnails.Release(known.Key, transparent: true);
            pictures.Clear();
        }


        private static string Detail(HierarchyRow row)
        {
            var go = row.Object;
            if (go == null) return string.Empty;
            if (go.TryGetComponent<SkinnedMeshRenderer>(out var skinned) && skinned.sharedMesh != null)
            {
                var mesh = skinned.sharedMesh;
                return "Skinned mesh · " + MixerUi.Count(mesh.vertexCount) + " vertices" + (mesh.blendShapeCount > 0 ? " · " + MixerUi.Count(mesh.blendShapeCount) + " shapes" : "");
            }
            if (go.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                return "Mesh · " + MixerUi.Count(filter.sharedMesh.vertexCount) + " vertices";
            if (!row.HasMeshes) return string.Empty;
            int meshes = go.GetComponentsInChildren<Renderer>(true).Count(r => r is SkinnedMeshRenderer s ? s.sharedMesh != null : r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var f) && f.sharedMesh != null);
            return meshes + (meshes == 1 ? " mesh below" : " meshes below");
        }

        private static string LightDetail(Light light)
        {
            var e = light.transform.eulerAngles;
            return light.type + " light · " + Mathf.RoundToInt(Mathf.Repeat(e.y, 360f)) + "° around · " + Mathf.RoundToInt(Mathf.DeltaAngle(0f, e.x)) + "° down";
        }

        // A rounded outline in dashes (USS borders are solid): brighter under the pointer, Mixer's green under a drop.
        private static void DrawDashes(MeshGenerationContext context)
        {
            var element = context.visualElement;
            float w = element.layout.width, h = element.layout.height;
            if (float.IsNaN(w) || w < 8f || h < 8f) return;
            const float radius = 6f, dash = 4f, gap = 3f, inset = 0.5f;
            var painter = context.painter2D;
            bool over = element.ClassListContains("mxh-drop--over");
            painter.strokeColor = over ? new Color(0f, 0.855f, 0.427f, 1f) : new Color(1f, 1f, 1f, element.ClassListContains("mxh-drop--busy") ? 0.3f : 0.2f);
            painter.lineWidth = 1f;
            float x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;
            void Dashes(Vector2 from, Vector2 to)
            {
                float length = (to - from).magnitude;
                if (length <= 0f) return;
                var step = (to - from) / length;
                for (float t = 0f; t < length; t += dash + gap)
                {
                    painter.BeginPath();
                    painter.MoveTo(from + step * t);
                    painter.LineTo(from + step * Mathf.Min(t + dash, length));
                    painter.Stroke();
                }
            }
            Dashes(new Vector2(x0 + radius, y0), new Vector2(x1 - radius, y0));
            Dashes(new Vector2(x1, y0 + radius), new Vector2(x1, y1 - radius));
            Dashes(new Vector2(x1 - radius, y1), new Vector2(x0 + radius, y1));
            Dashes(new Vector2(x0, y1 - radius), new Vector2(x0, y0 + radius));
            void Corner(Vector2 center, float from) { painter.BeginPath(); painter.Arc(center, radius, Angle.Degrees(from), Angle.Degrees(from + 90f)); painter.Stroke(); }
            Corner(new Vector2(x1 - radius, y0 + radius), 270f);
            Corner(new Vector2(x1 - radius, y1 - radius), 0f);
            Corner(new Vector2(x0 + radius, y1 - radius), 90f);
            Corner(new Vector2(x0 + radius, y0 + radius), 180f);
        }

        // What a drop holds as files: project assets and files from outside Unity (scene objects are reparented instead).
        private static string[] DroppedPaths()
        {
            var paths = new List<string>();
            if (DragAndDrop.paths != null) paths.AddRange(DragAndDrop.paths.Where(p => !string.IsNullOrEmpty(p)));
            if (DragAndDrop.objectReferences != null)
                foreach (var o in DragAndDrop.objectReferences)
                {
                    string path = o != null ? AssetDatabase.GetAssetPath(o) : null;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            return paths.Distinct().ToArray();
        }
    }
}
