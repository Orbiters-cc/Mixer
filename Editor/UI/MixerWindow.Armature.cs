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
        private ToggleSwitch restPoseSwitch, mirrorSwitch;
        private Label armatureCaption, armatureBoneCaption;
        private TextField armatureSearch;
        private ListView armatureList;
        private List<int> armatureBones = new List<int>();
        private EditTarget armatureOf;
        private Button addBoneButton, resetPoseButton;

        private void BuildArmaturePage(VisualElement page)
        {
            var mode = MixerUi.Card("REST POSE", out var modeBody);
            page.Add(mode);
            armatureCaption = MixerUi.Text("", "mx-caption");
            modeBody.Add(armatureCaption);
            restPoseSwitch = new ToggleSwitch(ArmatureEdit.Active, on => { ArmatureEdit.Active = on; if (on) MixerSceneTool.Mode = MixerMode.Armature; WakeGlow(); });
            modeBody.Add(MixerUi.Setting("Edit rest pose", "Moving a bone changes where the skin rests instead of posing it; one Ctrl+Z takes the move and the bind poses back together", restPoseSwitch, first: true));
            mirrorSwitch = new ToggleSwitch(ArmatureEdit.Mirror, on => ArmatureEdit.Mirror = on);
            modeBody.Add(MixerUi.Setting("Mirror", "Left and right bones move together (the Toolkit's mirror posing)", mirrorSwitch));
            var actions = MixerUi.Row();
            resetPoseButton = MixerUi.Pill("Reset to bind pose", () => { var t = CurrentTarget(); int moved = ArmatureEdit.ResetToBindPose(t); toasts.Show(moved > 0 ? moved + " bones back in the bind pose." : "Already in the bind pose."); }, "ghost", IconGlyph.Refresh, "Put every bone where the mesh was skinned");
            actions.Add(resetPoseButton);
            addBoneButton = MixerUi.Pill("Add leaf bone", AddLeafBone, "accent", IconGlyph.Plus, "Add a bone under the picked bone; paint weights onto it in the Weights page");
            actions.Add(addBoneButton);
            modeBody.Add(MixerUi.Setting("Bones", "Pick a bone in the list, in the Scene view or in the Hierarchy", actions));

            var bones = MixerUi.Card("BONE", out var boneBody);
            page.Add(bones);
            armatureBoneCaption = MixerUi.Text("", "mx-caption");
            boneBody.Add(armatureBoneCaption);
            armatureSearch = new TextField();
            armatureSearch.AddToClassList("mx-field");
            armatureSearch.tooltip = "Search bones";
            armatureSearch.RegisterValueChangedCallback(_ => RefreshArmatureList(true));
            var searchRow = MixerUi.Row();
            searchRow.Add(armatureSearch);
            boneBody.Add(searchRow);
            armatureList = new ListView(armatureBones, 22f, MakeArmatureRow, BindArmatureRow);
            armatureList.AddToClassList("mx-list");
            armatureList.AddToClassList("mx-bones");
            armatureList.selectionType = SelectionType.Single;
            armatureList.selectionChanged += items =>
            {
                if (items.FirstOrDefault() is int bone && bone >= 0)
                {
                    ArmatureEdit.SelectedBone = bone;
                    var target = CurrentTarget();
                    if (target?.Skinned != null && bone < target.Skinned.bones.Length && target.Skinned.bones[bone] != null) Selection.activeTransform = target.Skinned.bones[bone];
                }
            };
            boneBody.Add(armatureList);
            boneBody.Add(MixerUi.Text("Picking a bone selects it, so Unity's move and rotate tools work on it in the Scene view. Removing bones, re-binding meshes to another armature and the humanoid avatar update come in a later version.", "mx-caption"));
        }

        private static VisualElement MakeArmatureRow()
        {
            var row = MixerUi.Row("mx-bone");
            var dot = MixerUi.Box("mx-dot"); dot.AddToClassList("mx-bone__dot"); row.Add(dot);
            row.Add(MixerUi.Text("", "mx-bone__name"));
            row.Add(MixerUi.Spacer());
            row.Add(MixerUi.Text("", "mx-bone__count"));
            return row;
        }

        private void BindArmatureRow(VisualElement row, int index)
        {
            var target = armatureOf;
            int bone = index < armatureBones.Count ? armatureBones[index] : -1;
            var name = row.Q<Label>(className: "mx-bone__name");
            var count = row.Q<Label>(className: "mx-bone__count");
            var dot = row.Q(className: "mx-bone__dot");
            if (target == null || target.Skinned == null || bone < 0 || bone >= target.Skinned.bones.Length) { name.text = ""; count.text = ""; return; }
            var t = target.Skinned.bones[bone];
            name.text = t != null ? t.name : "(missing bone " + bone + ")";
            bool active = ArmatureEdit.SelectedBone == bone;
            row.EnableInClassList("mx-bone--on", active);
            dot.EnableInClassList("mx-dot--green", active);
            count.text = t != null ? t.childCount + (t.childCount == 1 ? " child" : " children") : "";
        }

        private void AddLeafBone()
        {
            var target = CurrentTarget();
            if (target == null || target.Skinned == null) { toasts.Show("Edit a skinned mesh first.", true); return; }
            int bone = ArmatureEdit.SelectedBone;
            if (bone < 0 || bone >= target.Skinned.bones.Length) { toasts.Show("Pick the parent bone first.", true); return; }
            var made = ArmatureEdit.AddLeafBone(target, target.Skinned.bones[bone]);
            if (made != null) { toasts.Show("Added " + made.name + " under " + target.Skinned.bones[bone].name + "."); RefreshArmatureList(true); }
        }

        private void RefreshArmatureList(bool force)
        {
            var target = CurrentTarget();
            if (!force && target == armatureOf) { armatureList.RefreshItems(); return; }
            armatureOf = target;
            armatureBones = new List<int>();
            if (target != null && target.Skinned != null)
            {
                string q = armatureSearch.value.Trim();
                var bones = target.Skinned.bones;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    if (q.Length > 0 && bones[i].name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    armatureBones.Add(i);
                }
            }
            armatureList.itemsSource = armatureBones;
            armatureList.Rebuild();
            int selected = armatureBones.IndexOf(ArmatureEdit.SelectedBone);
            armatureList.SetSelectionWithoutNotify(selected >= 0 ? new[] { selected } : new int[0]);
        }

        private void RefreshArmaturePage(EditTarget target)
        {
            restPoseSwitch.SetValueWithoutNotify(ArmatureEdit.Active);
            mirrorSwitch.SetValueWithoutNotify(ArmatureEdit.Mirror);
            bool skinned = target != null && target.Skinned != null && target.Document.HasSkin;
            resetPoseButton.SetEnabled(skinned);
            addBoneButton.SetEnabled(skinned && ArmatureEdit.SelectedBone >= 0);
            armatureCaption.text = target == null ? "Edit a skinned mesh to work on its armature."
                : !skinned ? target.Name + " has no bones."
                : ArmatureEdit.Active ? "Rest pose editing is on: move bones with Unity's tools, the skin stays where it is and the bind poses follow."
                : "Switch rest pose editing on, then move bones with Unity's move and rotate tools; the mesh keeps its shape.";
            int bone = ArmatureEdit.SelectedBone;
            armatureBoneCaption.text = skinned && bone >= 0 && bone < target.Skinned.bones.Length && target.Skinned.bones[bone] != null
                ? "Picked: " + target.Skinned.bones[bone].name + (target.Document.BindPosesEdited ? " · bind poses changed" : "")
                : "No bone picked.";
            RefreshArmatureList(target != armatureOf);
        }
    }
}
