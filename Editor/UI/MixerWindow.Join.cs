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
        private VisualElement joinList;
        private Label joinCaption, joinSummary;
        private ToggleSwitch mergeMaterialsSwitch, hideSourcesSwitch;
        private Button joinButton;
        private bool joinMergeMaterials = true, joinHideSources = true;

        private void BuildJoinPage(VisualElement page)
        {
            var card = MixerUi.Card("JOIN MESHES", out var body);
            page.Add(card);
            joinCaption = MixerUi.Text("", "mx-caption");
            body.Add(joinCaption);
            joinList = MixerUi.Box("mx-join");
            body.Add(joinList);
            joinSummary = MixerUi.Text("", "mx-caption");
            body.Add(joinSummary);
            mergeMaterialsSwitch = new ToggleSwitch(joinMergeMaterials, on => { joinMergeMaterials = on; chromeDirty = true; });
            body.Add(MixerUi.Setting("Merge same materials", "Submeshes that share a material become one slot", mergeMaterialsSwitch));
            hideSourcesSwitch = new ToggleSwitch(joinHideSources, on => joinHideSources = on);
            body.Add(MixerUi.Setting("Hide the sources", "The joined objects are deactivated (Undo brings them back)", hideSourcesSwitch));
            var actions = MixerUi.Row();
            actions.Add(MixerUi.Spacer());
            joinButton = MixerUi.Pill("Join", JoinSelection, "primary", IconGlyph.Join, "Join the selected meshes into one skinned mesh, saved as an asset");
            actions.Add(joinButton);
            actions.style.marginTop = 8;
            body.Add(actions);
        }

        private static List<Renderer> JoinCandidates() =>
            Selection.gameObjects.SelectMany(g => g.GetComponentsInChildren<Renderer>(true)).Where(r => (r is SkinnedMeshRenderer s && s.sharedMesh != null) || (r is MeshRenderer && r.GetComponent<MeshFilter>()?.sharedMesh != null))
                .Distinct().OrderBy(r => r.transform.GetSiblingIndex()).ToList();

        private void RefreshJoinPage()
        {
            var candidates = JoinCandidates();
            joinList.Clear();
            int vertices = 0;
            var materials = new HashSet<Material>();
            var shapes = new HashSet<string>();
            foreach (var r in candidates)
            {
                var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh;
                vertices += mesh.vertexCount;
                foreach (var m in r.sharedMaterials) if (m != null) materials.Add(m);
                for (int i = 0; i < mesh.blendShapeCount; i++) shapes.Add(mesh.GetBlendShapeName(i));
                var row = MixerUi.Box("mx-setting");
                var thumbnail = new Image { scaleMode = ScaleMode.ScaleToFit };
                thumbnail.AddToClassList("mx-join__thumb");
                thumbnail.image = AssetPreview.GetMiniThumbnail(mesh);
                row.Add(thumbnail);
                var texts = MixerUi.Box("mx-setting__texts");
                texts.Add(MixerUi.Text(r.name + (r == candidates[0] ? "  · target space" : ""), "mx-setting__title"));
                texts.Add(MixerUi.Text(MixerUi.Count(mesh.vertexCount) + " vertices · " + mesh.subMeshCount + (mesh.subMeshCount == 1 ? " material" : " materials") + (mesh.blendShapeCount > 0 ? " · " + mesh.blendShapeCount + " shapes" : "") + (r is SkinnedMeshRenderer ? " · skinned" : " · static"), "mx-setting__detail"));
                row.Add(texts);
                joinList.Add(row);
            }
            joinCaption.text = candidates.Count == 0 ? "Select two or more objects with meshes in the Hierarchy. The first one's space and armature become the result's."
                : candidates.Count == 1 ? "One mesh selected; select at least two." : candidates.Count + " meshes selected.";
            joinSummary.text = candidates.Count >= 2 ? "Result: " + MixerUi.Count(vertices) + " vertices, " + (joinMergeMaterials ? materials.Count : candidates.Sum(r => r.sharedMaterials.Length)) + " material slots" + (shapes.Count > 0 ? ", " + shapes.Count + " blendshapes" : "") + "." : "";
            joinButton.SetEnabled(candidates.Count >= 2);
            mergeMaterialsSwitch.SetValueWithoutNotify(joinMergeMaterials);
            hideSourcesSwitch.SetValueWithoutNotify(joinHideSources);
        }

        private void JoinSelection()
        {
            var candidates = JoinCandidates();
            if (candidates.Count < 2) { toasts.Show("Select at least two meshes.", true); return; }
            try
            {
                var result = MeshJoin.Join(candidates, new MeshJoin.Options { MergeSameMaterials = joinMergeMaterials, HideSources = joinHideSources });
                toasts.Show("Joined into " + result.Renderer.name + ": " + MixerUi.Count(result.Vertices) + " vertices, " + result.Bones + " bones, " + result.Slots + " slots" + (result.Shapes > 0 ? ", " + result.Shapes + " shapes" : "") + ".");
                WakeGlow();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                toasts.Show("Join failed: " + e.Message, true);
            }
        }
    }
}
