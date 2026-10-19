using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal sealed partial class MixerWindow
    {
        private readonly Dictionary<WeightBrushKind, Button> weightTiles = new Dictionary<WeightBrushKind, Button>();
        private Slider weightValueSlider, weightRadiusSlider, weightStrengthSlider;
        private Label weightValueLabel, weightRadiusValue, weightStrengthValue, boneCaption, groupCaption, hoverCaption;
        private ToggleSwitch normalizeSwitch;
        private TextField boneSearch, newGroupField;
        private ListView boneList;
        private VisualElement groupChips, boneCard;
        private List<int> shownBones = new List<int>();
        private EditTarget bonesOf;
        private int bonesRevision = -1;

        private void BuildWeightsPage(VisualElement page)
        {
            var bones = MixerUi.Card("BONE", out var boneBody);
            boneCard = bones;
            page.Add(bones);
            boneCaption = MixerUi.Text("", "mx-caption");
            boneBody.Add(boneCaption);
            boneSearch = new TextField();
            boneSearch.AddToClassList("mx-field");
            boneSearch.tooltip = "Search bones";
            boneSearch.RegisterValueChangedCallback(_ => RefreshBoneList(true));
            boneBody.Add(boneSearch);
            boneList = new ListView(shownBones, 22f, MakeBoneRow, BindBoneRow);
            boneList.AddToClassList("mx-list");
            boneList.AddToClassList("mx-bones");
            boneList.selectionType = SelectionType.Single;
            boneList.selectionChanged += items =>
            {
                if (items.FirstOrDefault() is int bone && bone >= 0) { MixerSceneTool.SetChannel(WeightChannel.OfBone(bone)); WakeGlow(); }
            };
            boneBody.Add(boneList);
            hoverCaption = MixerUi.Text("", "mx-caption");
            boneBody.Add(hoverCaption);

            var brushCard = MixerUi.Card("BRUSH", out var brushBody);
            page.Add(brushCard);
            var tiles = MixerUi.Box("mx-tiles");
            brushBody.Add(tiles);
            foreach (var (kind, glyph, tip) in new[]
            {
                (WeightBrushKind.Draw, IconGlyph.Sculpt, "Paint the weight toward the brush value; Ctrl toward 0."),
                (WeightBrushKind.Blur, IconGlyph.Broom, "Average the weight with the neighbours'."),
                (WeightBrushKind.Replace, IconGlyph.Layers, "Set the weight to the brush value outright; Ctrl to 0."),
            })
            {
                var k = kind;
                var tile = new Button { text = string.Empty, tooltip = tip };
                tile.AddToClassList("mx-tile");
                tile.AddToClassList("mx-tile--short");
                var icon = new VectorIcon(glyph) { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("mx-tile__icon");
                tile.Add(icon);
                tile.Add(MixerUi.Text(kind.ToString(), "mx-tile__name"));
                MixerUi.Press(tile, () => { MixerSceneTool.WeightKind = k; MixerSceneTool.Mode = MixerMode.Weights; });
                tiles.Add(tile);
                weightTiles[kind] = tile;
            }
            weightValueSlider = new Slider(0f, 1f) { value = MixerSceneTool.WeightValue };
            weightValueSlider.AddToClassList("mx-slider");
            weightValueLabel = MixerUi.Text(MixerSceneTool.WeightValue.ToString("0.00"), "mx-chip mx-chip__text");
            weightValueSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.WeightValue = evt.newValue; weightValueLabel.text = evt.newValue.ToString("0.00"); });
            brushBody.Add(MixerUi.Setting("Weight", "What Draw and Replace paint toward", Pair(weightValueSlider, weightValueLabel), first: true));
            weightRadiusSlider = new Slider(0f, 1f) { value = RadiusToSlider(MixerSceneTool.Radius) };
            weightRadiusSlider.AddToClassList("mx-slider");
            weightRadiusValue = MixerUi.Text(MixerUi.Metres(MixerSceneTool.Radius), "mx-chip mx-chip__text");
            weightRadiusSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Radius = SliderToRadius(evt.newValue); weightRadiusValue.text = MixerUi.Metres(MixerSceneTool.Radius); });
            brushBody.Add(MixerUi.Setting("Radius", "Shared with the sculpt brushes", Pair(weightRadiusSlider, weightRadiusValue)));
            weightStrengthSlider = new Slider(0f, 1f) { value = MixerSceneTool.Strength };
            weightStrengthSlider.AddToClassList("mx-slider");
            weightStrengthValue = MixerUi.Text(MixerUi.Percent(MixerSceneTool.Strength), "mx-chip mx-chip__text");
            weightStrengthSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Strength = evt.newValue; weightStrengthValue.text = MixerUi.Percent(evt.newValue); });
            brushBody.Add(MixerUi.Setting("Strength", "How fast a stroke gets there", Pair(weightStrengthSlider, weightStrengthValue)));
            normalizeSwitch = new ToggleSwitch(MixerSceneTool.AutoNormalize, on => MixerSceneTool.AutoNormalize = on);
            brushBody.Add(MixerUi.Setting("Auto normalize", "Keep each vertex's bone weights summing to 1: the other bones give way", normalizeSwitch));
            brushBody.Add(MixerUi.Text("Drag on the mesh in the Scene view; the overlay colours the painted bone from blue (0) to red (1). Click a bone in the Scene view or the list to paint it; with symmetry the mirror bone takes the same on the other side.", "mx-caption"));

            var groups = MixerUi.Card("VERTEX GROUPS", out var groupBody);
            page.Add(groups);
            groupCaption = MixerUi.Text("", "mx-caption");
            groupBody.Add(groupCaption);
            groupChips = MixerUi.Box("mx-chips");
            groupBody.Add(groupChips);
            var newRow = MixerUi.Row("mx-newshape");
            newGroupField = new TextField { value = string.Empty };
            newGroupField.AddToClassList("mx-field");
            newGroupField.tooltip = "New vertex group name";
            newGroupField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CreateGroup(); evt.StopPropagation(); } });
            newRow.Add(newGroupField);
            newRow.Add(MixerUi.Pill("Create", CreateGroup, "accent", IconGlyph.Plus, "Make a vertex group and paint it"));
            groupBody.Add(newRow);
        }

        private static VisualElement MakeBoneRow()
        {
            var row = MixerUi.Row("mx-bone");
            var dot = MixerUi.Box("mx-dot");
            dot.AddToClassList("mx-bone__dot");
            row.Add(dot);
            row.Add(MixerUi.Text("", "mx-bone__name"));
            row.Add(MixerUi.Spacer());
            row.Add(MixerUi.Text("", "mx-bone__count"));
            return row;
        }

        private void BindBoneRow(VisualElement row, int index)
        {
            var target = bonesOf;
            int bone = index < shownBones.Count ? shownBones[index] : -1;
            var name = row.Q<Label>(className: "mx-bone__name");
            var count = row.Q<Label>(className: "mx-bone__count");
            var dot = row.Q(className: "mx-bone__dot");
            if (target == null || target.Skinned == null || bone < 0 || bone >= target.Skinned.bones.Length) { name.text = ""; count.text = ""; return; }
            var t = target.Skinned.bones[bone];
            name.text = t != null ? t.name : "(missing bone " + bone + ")";
            bool active = MixerSceneTool.Channel.Bone == bone && !MixerSceneTool.Channel.IsGroup;
            row.EnableInClassList("mx-bone--on", active);
            dot.EnableInClassList("mx-dot--green", active);
            count.text = "";
        }

        private void CreateGroup()
        {
            var target = CurrentTarget();
            if (target == null) { toasts.Show("Edit a mesh first.", true); return; }
            string name = newGroupField.value.Trim();
            if (name.Length == 0) { toasts.Show("Give the group a name.", true); return; }
            var group = MixerSession.AddGroup(target, name);
            if (group == null) return;
            newGroupField.value = string.Empty;
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.SetChannel(WeightChannel.OfGroup(name));
            toasts.Show("Group " + name + ": paint it with the weight brushes.");
        }

        private void RefreshBoneList(bool force)
        {
            var target = CurrentTarget();
            int revision = target != null && target.Skinned != null ? target.Skinned.bones.Length * 31 + target.Document.VertexCount : 0;
            if (!force && target == bonesOf && revision == bonesRevision) { boneList.RefreshItems(); return; }
            bonesOf = target;
            bonesRevision = revision;
            shownBones = new List<int>();
            if (target != null && target.Skinned != null)
            {
                string q = boneSearch.value.Trim();
                var bones = target.Skinned.bones;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    if (q.Length > 0 && bones[i].name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shownBones.Add(i);
                }
            }
            boneList.itemsSource = shownBones;
            boneList.Rebuild();
            int selected = shownBones.IndexOf(MixerSceneTool.Channel.IsGroup ? -1 : MixerSceneTool.Channel.Bone);
            boneList.SetSelectionWithoutNotify(selected >= 0 ? new[] { selected } : new int[0]);
            if (selected >= 0) boneList.ScrollToItem(selected);
        }

        private void RefreshWeightsPage(EditTarget target)
        {
            foreach (var pair in weightTiles) pair.Value.EnableInClassList("mx-tile--on", MixerSceneTool.WeightKind == pair.Key && MixerSceneTool.Mode == MixerMode.Weights);
            if (Math.Abs(weightValueSlider.value - MixerSceneTool.WeightValue) > 1e-4f) weightValueSlider.SetValueWithoutNotify(MixerSceneTool.WeightValue);
            weightValueLabel.text = MixerSceneTool.WeightValue.ToString("0.00");
            if (Math.Abs(weightRadiusSlider.value - RadiusToSlider(MixerSceneTool.Radius)) > 1e-4f) weightRadiusSlider.SetValueWithoutNotify(RadiusToSlider(MixerSceneTool.Radius));
            weightRadiusValue.text = MixerUi.Metres(MixerSceneTool.Radius);
            if (Math.Abs(weightStrengthSlider.value - MixerSceneTool.Strength) > 1e-4f) weightStrengthSlider.SetValueWithoutNotify(MixerSceneTool.Strength);
            weightStrengthValue.text = MixerUi.Percent(MixerSceneTool.Strength);
            normalizeSwitch.SetValueWithoutNotify(MixerSceneTool.AutoNormalize);
            var channel = MixerSceneTool.Channel;
            if (target == null) boneCaption.text = "Edit a skinned mesh to paint its bone weights.";
            else if (target.Skinned == null || !target.Document.HasSkin) boneCaption.text = target.Name + " has no bones: paint vertex groups below.";
            else if (channel.IsGroup) boneCaption.text = "Painting the vertex group " + channel.Group + ". Pick a bone to paint its weights instead.";
            else if (channel.Bone >= 0) boneCaption.text = "Painting " + WeightStroke.BoneName(target, channel.Bone) + (MixerSceneTool.Symmetry ? ", mirrored onto " + WeightStroke.BoneName(target, WeightStroke.MirrorBone(target, channel.Bone)) : "") + ".";
            else boneCaption.text = "Pick a bone to paint: click it in the list, or in the Scene view.";
            hoverCaption.text = MixerSceneTool.HoverWeight >= 0f ? "Under the cursor: " + MixerSceneTool.HoverWeight.ToString("0.00") : "";
            RefreshBoneList(false);
            groupChips.Clear();
            if (target == null) { groupCaption.text = "Named sets of vertices with weights, as Blender's vertex groups; saved beside the committed mesh and sent to Blender."; return; }
            var doc = target.Document;
            groupCaption.text = doc.Groups.Count == 0 ? "No vertex group yet. Create one and paint it; groups are saved beside the committed mesh and sent to Blender." : doc.Groups.Count + (doc.Groups.Count == 1 ? " group" : " groups") + ". Click one to paint it; right-click to rename or remove it.";
            foreach (var g in doc.Groups)
            {
                var group = g;
                bool on = channel.IsGroup && channel.Group == g.Name;
                var chip = Chip(g.Name + " · " + MixerUi.Count(g.Weights.Count), on, () => { MixerSceneTool.FocusTarget = target; MixerSceneTool.SetChannel(WeightChannel.OfGroup(group.Name)); }, null);
                chip.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Rename…", _ => AskName("Rename " + group.Name, group.Name, value => MixerSession.RenameGroup(target, group.Name, value)));
                    evt.menu.AppendAction("Remove", _ => MixerSession.RemoveGroup(target, group.Name));
                }));
                groupChips.Add(chip);
            }
        }
    }
}
