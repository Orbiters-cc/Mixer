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
    // an item it added.
    internal sealed partial class MixerHierarchyWindow
    {
        private bool thumbnailsPending;
        private double lastThumbnailPoll;
        // Picture keys of the rows, worked out once per rebuild.
        private readonly Dictionary<int, string> thumbnailKeys = new Dictionary<int, string>();

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
                MarkDirty();
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
            return line;
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

        private void BindAvatarLines(VisualElement element, HierarchyRow row)
        {
            var lines = element.Q(className: "mxh-avatar");
            var facts = HierarchyVrchat.Describe?.Invoke(row.Object);
            var uploaded = lines.Q<Label>(className: "mxh-avatar__name");
            if (facts == null) uploaded.text = "Mixer's VRChat part is not loaded (VRChat SDK 3.7 or later)";
            else if (facts.UploadedName != null) uploaded.text = facts.UploadedName;
            else uploaded.text = facts.Note ?? string.Empty;
            uploaded.EnableInClassList("mxh-avatar__name--note", facts?.UploadedName == null);
            uploaded.tooltip = facts?.UploadedName != null ? "Its name on VRChat (" + facts.BlueprintId + ")" : facts?.BlueprintId;
            var info = CustomBases.Describe(row.Object.transform);
            var customBase = lines.Q<Label>(className: "mxh-avatar__base");
            MixerUi.Show(customBase, info != null && !string.IsNullOrEmpty(info.Name));
            if (info != null)
            {
                customBase.text = info.Name;
                customBase.tooltip = "Custom base" + (string.IsNullOrEmpty(info.BaseName) ? "" : " of " + info.BaseName) + (info.Source == "MCB" ? " (My Custom Base)" : "");
            }
            var rank = lines.Q(className: "mxh-avatar__rank");
            MixerUi.Show(rank, facts != null);
            if (facts != null)
            {
                rank.Q<Image>().image = facts.RankIcon;
                MixerUi.Show(rank.Q<Image>(), facts.RankIcon != null);
                rank.Q<Label>().text = facts.Rank ?? "Ranking\u2026";
                rank.tooltip = "Its performance rank on " + facts.RankPlatform + " as it is in the scene, worked out by the VRChat SDK (the build target's platform)";
            }
            var visibility = lines.Q(className: "mxh-avatar__visibility");
            MixerUi.Show(visibility, facts?.Visibility != null);
            if (facts?.Visibility != null)
            {
                bool shared = facts.Visibility == "Public";
                visibility.Q<VectorIcon>().Glyph = shared ? IconGlyph.People : IconGlyph.Lock;
                visibility.Q<Label>().text = facts.Visibility;
                visibility.tooltip = shared ? "Public on VRChat: anyone can find and wear it" : "Private on VRChat: only its owner can wear it";
            }
            MixerUi.Show(lines.Q(className: "mxh-avatar__link"), facts?.Link != null);
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

        private void BindRich(VisualElement element, HierarchyRow row, bool tall)
        {
            var active = element.Q(className: "mxh-row__active");
            var icon = element.Q<Image>(className: "mxh-row__icon");
            var glyph = element.Q<VectorIcon>(className: "mxh-row__glyph");
            var thumbnail = element.Q<Image>(className: "mxh-row__thumbnail");
            var sphere = element.Q<OrbitSphere>(className: "mxh-row__sphere");
            var line = element.Q(className: "mxh-row__line");
            var line2 = element.Q(className: "mxh-row__line2");
            var extras = element.Q(className: "mxh-row__extras");
            var detail = element.Q<Label>(className: "mxh-row__detail");
            var drop = element.Q(className: "mxh-drop");
            var bones = element.Q(className: "mxh-bones");
            var refit = element.Q(className: "mxh-refit");
            bool isObject = !row.IsScene && row.Object != null;
            MixerUi.Show(active, isObject);
            if (isObject) active.EnableInClassList("mxh-row__active--on", row.Object.activeSelf);
            bool picture = tall && row.HasMeshes && row.Light == null;
            bool vector = isObject && (row.Bones != null || row.IsBone);
            element.EnableInClassList("mxh-row--picture", picture);
            MixerUi.Show(thumbnail, picture);
            var vrchatMark = element.Q(className: "mxh-row__vrchat-mark");
            if (vrchatMark != null) MixerUi.Show(vrchatMark, picture && row.VrchatAvatar);
            MixerUi.Show(glyph, !picture && vector);
            MixerUi.Show(icon, !picture && !vector);
            if (vector) glyph.Glyph = row.Bones != null ? IconGlyph.Armature : IconGlyph.Bones;
            bool avatar = tall && row.VrchatAvatar;
            element.EnableInClassList("mxh-row--avatar", avatar);
            MixerUi.Show(line2, tall && !avatar);
            MixerUi.Show(element.Q(className: "mxh-avatar"), avatar);
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
                    if (into == line) line.Insert(line.IndexOf(line.Q(className: "mxh-row__badges")) + 1, bones);
                    else extras.Insert(0, bones);
                }
                foreach (var button in bones.Children())
                    if (button.userData is System.Func<QuickBones, Transform> bone) button.SetEnabled(bone(row.Bones) != null);
            }
            MixerUi.Show(refit, tall && row.RefitTool != null);
            // The visibility and picking switches go to the row's right end beside a light's ball or a drop field.
            var switches = element.Q(className: "mxh-row__switches");
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
                drop.Q<Label>().text = busy ? "Adding…" : "Drop anything";
                drop.MarkDirtyRepaint();
            }
            if (!tall) return;
            if (avatar) BindAvatarLines(element, row);

            if (picture)
            {
                var texture = ModelThumbnails.Get(row.Object, ThumbnailKey(row), transparent: true);
                thumbnail.image = texture;
                thumbnail.EnableInClassList("mxh-row__thumbnail--waiting", texture == null);
                if (texture == null) thumbnailsPending = true;
            }
            detail.text = row.Light != null ? LightDetail(row.Light) : Detail(row);
            detail.EnableInClassList("mxh-row__detail--short", bonesShown || row.RefitTool != null);
        }

        /// <summary>The lines a row takes: three for a VRChat avatar, two for <see cref="Tall"/> rows, else one.</summary>
        private int Lines(HierarchyRow row) => !Tall(row) ? 1 : row.VrchatAvatar ? 3 : 2;

        /// <summary>Two lines for rows with more than a name: meshes (with their picture), a light, My Avatar's drop
        /// field or ReFit question, or an armature whose bone buttons do not fit beside its name. One in compact rows.</summary>
        private bool Tall(HierarchyRow row)
        {
            if (row == null || row.IsScene || row.Object == null || Compact) return false;
            if (row.IsBone) return false;
            return row.HasMeshes || row.Light != null || row.DropTool != null || row.RefitTool != null || (row.Bones != null && !BonesFitInline(row));
        }

        // Name, badges and six bone buttons on one line: the indent plus about 430 points.
        private bool BonesFitInline(HierarchyRow row)
        {
            float width = list != null ? list.layout.width : float.NaN;
            return !float.IsNaN(width) && width >= Mathf.Max(0, row.Depth - 1) * 14f + 470f;
        }

        // The armature rows change height as the window crosses the width where their buttons fit beside the name.
        private float laidOutWidth = -1f;
        private void OnWidthChanged()
        {
            float width = list.layout.width;
            if (Mathf.Abs(width - laidOutWidth) < 0.5f) return;
            bool before = laidOutWidth >= 0f && model.Rows.Any(r => r.Bones != null && laidOutWidth >= Mathf.Max(0, r.Depth - 1) * 14f + 470f);
            laidOutWidth = width;
            bool now = model.Rows.Any(r => r.Bones != null && BonesFitInline(r));
            if (before != now || model.Rows.Any(r => r.Bones != null)) list.RefreshRows();
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
            foreach (var element in list.Rows)
            {
                if (element.resolvedStyle.display == DisplayStyle.None || !element.ClassListContains("mxh-row--picture")) continue;
                var row = RowOf(element);
                var thumbnail = element.Q<Image>(className: "mxh-row__thumbnail");
                if (row?.Object == null || !row.HasMeshes || thumbnail == null || thumbnail.image != null) continue;
                var texture = ModelThumbnails.Get(row.Object, ThumbnailKey(row), transparent: true);
                if (texture == null) { thumbnailsPending = true; continue; }
                thumbnail.image = texture;
                thumbnail.RemoveFromClassList("mxh-row__thumbnail--waiting");
            }
        }

        // What the picture shows: the meshes and materials below the object and whether they show, so a commit (a new
        // mesh), a material swap or a switched-off part makes a new one. Kept until the next rebuild.
        private string ThumbnailKey(HierarchyRow row)
        {
            if (thumbnailKeys.TryGetValue(row.InstanceId, out var cached)) return cached;
            var key = new StringBuilder("mixer-hierarchy|").Append(row.InstanceId);
            foreach (var renderer in row.Object.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                if (mesh == null) continue;
                key.Append('|').Append(mesh.GetInstanceID()).Append(renderer.enabled && renderer.gameObject.activeInHierarchy ? '+' : '-');
                foreach (var material in renderer.sharedMaterials) key.Append(',').Append(material != null ? material.GetInstanceID() : 0);
            }
            return thumbnailKeys[row.InstanceId] = key.ToString();
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
