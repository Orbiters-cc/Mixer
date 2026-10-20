using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal sealed partial class MixerWindow
    {
        private Label heroEyebrow, heroTitle, heroSubtitle;
        private MixerPreviewBadge heroBadge;
        private VisualElement heroActions, heroEmpty, heroFrame;
        private MixerRing historyRing;

        private void BuildHero()
        {
            heroBox.Clear();
            heroFrame = MixerUi.Box("mx-hero");
            heroBox.Add(heroFrame);
            glow = new OrbitersGlowSurfaceElement(new Color(0.11f, 0.118f, 0.137f, 1f), -40f, 160f);
            glow.AddToClassList("mx-hero__glow");
            glow.pickingMode = PickingMode.Ignore;
            heroFrame.Add(glow);
            var top = MixerUi.Box("mx-hero__top");
            heroFrame.Add(top);
            // The badge renders only while the window is on screen (a docked tab behind another one is not).
            heroBadge = new MixerPreviewBadge { Shown = () => visible };
            heroBadge.AddToClassList("mx-hero__badge");
            top.Add(heroBadge);
            var texts = MixerUi.Box("mx-hero__texts");
            top.Add(texts);
            heroEyebrow = MixerUi.Text("MIXER", "mx-hero__eyebrow");
            heroTitle = MixerUi.Text("", "mx-hero__title");
            heroSubtitle = MixerUi.Text("", "mx-hero__subtitle");
            texts.Add(heroEyebrow);
            texts.Add(heroTitle);
            texts.Add(heroSubtitle);
            heroActions = MixerUi.Row("mx-hero__actions");
            texts.Add(heroActions);
            historyRing = new MixerRing();
            historyRing.AddToClassList("mx-hero__ring");
            top.Add(historyRing);
            heroEmpty = MixerUi.Box("mx-hero__empty");
            heroFrame.Add(heroEmpty);
        }

        private void RefreshHero(EditTarget target)
        {
            if (heroFrame == null) BuildHero();
            string mode = ModeNames[Mathf.Clamp(modeIndex, 0, ModeNames.Length - 1)].ToUpperInvariant();
            heroActions.Clear();
            heroEmpty.Clear();
            if (target == null)
            {
                heroEyebrow.text = "MIXER · " + mode;
                heroTitle.text = "Nothing is being edited";
                var editable = EditableSelection();
                heroSubtitle.text = editable.Count == 0
                    ? "Select an avatar or a mesh in the Hierarchy, then press Edit here. Edits show live and nothing is written until you commit."
                    : editable.Count == 1 ? "Edit " + editable[0].renderer.name + " to start sculpting it in the Scene view." : editable.Count + " meshes in the selection can be edited.";
                foreach (var (renderer, reason) in editable.Take(6))
                {
                    var r = renderer;
                    var pill = MixerUi.Pill("Edit " + renderer.name, () => Edit(r), "primary", IconGlyph.Sculpt, reason);
                    pill.SetEnabled(reason == null);
                    heroActions.Add(pill);
                }
                if (editable.Count > 1)
                    heroActions.Add(MixerUi.Pill("Edit all", () => { foreach (var (r, reason) in editable) if (reason == null) Edit(r); }, null, IconGlyph.Layers, "Edit every mesh of the selection"));
                heroBadge.Set(null);
                historyRing.Set(0f, "0", "steps");
                return;
            }
            var doc = target.Document;
            heroEyebrow.text = "MIXER · " + mode + (doc.Layer != null ? " · SHAPE" : "");
            heroTitle.text = target.Name;
            int shapes = doc.ShapeOrder.Count(n => doc.HasShape(n));
            string layer = doc.Layer != null ? "shape " + doc.Layer : "base mesh";
            heroSubtitle.text = MixerUi.Count(doc.VertexCount) + " vertices · " + MixerUi.Count(doc.Triangles.Length / 3) + " triangles · " + shapes + (shapes == 1 ? " shape" : " shapes") + " · editing the " + layer
                                + (doc.Changed ? " · " + MixerUi.Count(doc.MovedBase.Count + doc.ShapeEdits.Values.Count(s => s.Edited) * 0) + " base vertices moved" : "");
            heroBadge.Set(target);
            var more = EditableSelection();
            foreach (var (renderer, reason) in more.Take(3))
            {
                var r = renderer;
                var pill = MixerUi.Pill("Edit " + renderer.name, () => Edit(r), "ghost", IconGlyph.Plus, reason);
                pill.SetEnabled(reason == null);
                heroActions.Add(pill);
            }
            heroActions.Add(MixerUi.Pill("Frame", () => { Selection.activeGameObject = target.Renderer.gameObject; SceneView.FrameLastActiveSceneView(); }, null, IconGlyph.Camera, "Select the mesh and frame it in the Scene view"));
            float fraction = MixerHistory.Cap > 0 ? Mathf.Clamp01(MixerHistory.Bytes / (float)MixerHistory.Cap) : 0f;
            historyRing.Set(fraction, MixerHistory.Count.ToString(), MixerHistory.Count == 1 ? "step" : "steps");
        }

        private VisualElement targetsCard;

        private void RefreshTargets()
        {
            targetsBox.Clear();
            if (!MixerSession.Active) return;
            targetsCard = MixerUi.Card("EDITED MESHES", out var body);
            targetsBox.Add(targetsCard);
            bool first = true;
            foreach (var t in MixerSession.Targets)
            {
                if (!t.Alive) continue;
                var target = t;
                var doc = t.Document;
                var row = MixerUi.Box("mx-setting");
                if (first) row.AddToClassList("mx-setting--first");
                first = false;
                var dot = MixerUi.Box("mx-dot");
                dot.AddToClassList(doc.Changed ? "mx-dot--amber" : "mx-dot--green");
                dot.tooltip = doc.Changed ? "Unsaved edits" : "No change yet";
                row.Add(dot);
                var texts = MixerUi.Box("mx-setting__texts");
                texts.Add(MixerUi.Text(t.Name, "mx-setting__title"));
                string detail = MixerUi.Count(doc.VertexCount) + " vertices";
                if (doc.MovedBase.Count > 0) detail += " · " + MixerUi.Count(doc.MovedBase.Count) + " moved";
                var editedShapes = doc.ShapeEdits.Values.Where(s => s.Edited || s.IsNew).Select(s => s.Name).ToList();
                if (editedShapes.Count > 0) detail += " · shapes: " + string.Join(", ", editedShapes.Take(4)) + (editedShapes.Count > 4 ? "…" : "");
                texts.Add(MixerUi.Text(detail, "mx-setting__detail"));
                row.Add(texts);
                var actions = MixerUi.Row("mx-setting__control");
                actions.Add(MixerUi.Pill("Select", () => { Selection.activeGameObject = target.Renderer.gameObject; }, "ghost", null, "Select the mesh"));
                actions.Add(MixerUi.Pill("Revert", () => { if (MixerConfirm.Revert(target) > 0) toasts.Show("The original mesh of " + target.Name + " is back."); }, "ghost", null, "Put the original mesh back (asks first when edits would be lost)", destructive: true));
                var commit = MixerUi.Pill("Commit", () =>
                {
                    try { MixerSession.Commit(target); }
                    catch (System.Exception e) { Debug.LogException(e); toasts.Show("Commit failed: " + e.Message, true); }
                }, "accent", IconGlyph.Check, "Save this mesh as an asset and show it");
                commit.SetEnabled(doc.Changed);
                actions.Add(commit);
                row.Add(actions);
                body.Add(row);
            }
        }
    }

    /// <summary>A ring around a number: how much of the history memory is used.</summary>
    internal sealed class MixerRing : VisualElement
    {
        private float fraction;
        private readonly Label value, under;

        public MixerRing()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList("mx-ring__texts");
            value = new Label("0"); value.AddToClassList("mx-ring__value");
            under = new Label("steps"); under.AddToClassList("mx-ring__under");
            texts.Add(value); texts.Add(under);
            Add(texts);
        }

        public void Set(float fraction, string text, string underText)
        {
            this.fraction = Mathf.Clamp01(fraction);
            value.text = text;
            under.text = underText;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            float radius = Mathf.Min(r.width, r.height) * 0.5f - 4f;
            if (radius <= 0f) return;
            var center = r.center;
            var p = ctx.painter2D;
            p.lineWidth = 5f;
            p.lineCap = LineCap.Round;
            p.strokeColor = new Color(1f, 1f, 1f, 0.07f);
            p.BeginPath(); p.Arc(center, radius, 0f, 360f); p.Stroke();
            if (fraction <= 0f) return;
            p.strokeColor = fraction > 0.85f ? new Color(0.96f, 0.72f, 0.31f) : new Color(0f, 0.855f, 0.427f);
            p.BeginPath(); p.Arc(center, radius, -90f, -90f + 360f * fraction); p.Stroke();
        }
    }
}
