using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The hero's badge: a live little picture of the mesh being edited, turned by dragging, refreshed while the
    /// document changes. Rendered with a PreviewRenderUtility on the working mesh (nothing in the scene moves); the
    /// override lighting is restored after every render, as Unity requires.
    /// </summary>
    internal sealed class MixerPreviewBadge : VisualElement
    {
        private EditTarget target;
        private PreviewRenderUtility preview;
        private readonly Image image;
        private readonly MixerLogo.Element logo;
        private Vector2 angles = new Vector2(20f, -30f);
        private int seenRevision = -1;
        private double lastRender;
        private bool dragging;
        private Vector2 dragStart;
        private Material material;
        private Mesh baked;

        public MixerPreviewBadge()
        {
            image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.AddToClassList("mx-badge__image");
            Add(image);
            logo = new MixerLogo.Element();
            logo.AddToClassList("mx-badge__logo");
            Add(logo);
            RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0 || target == null) return; dragging = true; dragStart = evt.position; this.CapturePointer(evt.pointerId); });
            RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!dragging) return;
                var delta = (Vector2)evt.position - dragStart;
                dragStart = evt.position;
                angles += new Vector2(delta.y * 0.6f, -delta.x * 0.8f);
                angles.x = Mathf.Clamp(angles.x, -80f, 80f);
                seenRevision = -1;
            });
            RegisterCallback<PointerUpEvent>(evt => { dragging = false; this.ReleasePointer(evt.pointerId); });
            RegisterCallback<DetachFromPanelEvent>(_ => Release());
            schedule.Execute(Tick).Every(80);
        }

        public void Set(EditTarget target)
        {
            if (this.target != target) { this.target = target; seenRevision = -1; }
            logo.style.display = target == null ? DisplayStyle.Flex : DisplayStyle.None;
            image.style.display = target == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (target == null) image.image = null;
        }

        private void Tick()
        {
            if (target == null || !target.Alive || panel == null) return;
            int revision = target.Document.Revision;
            if (revision == seenRevision) return;
            if (EditorApplication.timeSinceStartup - lastRender < 0.08) return;
            seenRevision = revision;
            lastRender = EditorApplication.timeSinceStartup;
            try { Render(); } catch (Exception e) { Debug.LogException(e); target = null; }
        }

        private void Render()
        {
            int size = Mathf.Max(64, Mathf.RoundToInt(78f * EditorGUIUtility.pixelsPerPoint));
            if (preview == null)
            {
                preview = new PreviewRenderUtility();
                preview.camera.fieldOfView = 28f;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 100f;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(0.11f, 0.118f, 0.137f, 0f);
                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                preview.lights[1].intensity = 0.6f;
                preview.ambientColor = new Color(0.25f, 0.27f, 0.3f);
            }
            if (material == null)
            {
                var shader = Shader.Find("Standard");
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material.SetColor("_Color", new Color(0.78f, 0.8f, 0.84f));
                material.SetFloat("_Glossiness", 0.35f);
            }
            // The mesh as the scene shows it: posed and in the renderer's orientation (a skinned mesh's own space is
            // often turned), so the badge matches what the user sculpts.
            Mesh mesh;
            Quaternion orientation;
            if (target.Skinned != null)
            {
                if (baked == null) baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                target.Skinned.BakeMesh(baked, true);
                mesh = baked;
                orientation = target.Skinned.transform.rotation;
            }
            else
            {
                mesh = target.Working;
                orientation = target.Renderer.transform.rotation;
            }
            var bounds = mesh.bounds;
            float extent = Mathf.Max(bounds.extents.magnitude, 0.01f);
            var rotation = Quaternion.Euler(angles.x, angles.y, 0f) * orientation;
            var rect = new Rect(0, 0, size, size);
            preview.BeginPreview(rect, GUIStyle.none);
            try
            {
                float distance = extent / Mathf.Tan(preview.camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
                preview.camera.transform.position = Vector3.back * distance;
                preview.camera.transform.rotation = Quaternion.identity;
                var matrix = Matrix4x4.TRS(rotation * -bounds.center, rotation, Vector3.one);
                for (int s = 0; s < mesh.subMeshCount; s++) preview.DrawMesh(mesh, matrix, material, s);
                preview.Render(true, false);
            }
            finally
            {
                // Mandatory in this editor: a Render() without it leaves the scene's lighting overridden.
                Unsupported.RestoreOverrideLightingSettings();
            }
            var texture = preview.EndPreview();
            image.image = texture;
        }

        private void Release()
        {
            preview?.Cleanup();
            preview = null;
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            material = null;
            if (baked != null) UnityEngine.Object.DestroyImmediate(baked);
            baked = null;
        }
    }
}
