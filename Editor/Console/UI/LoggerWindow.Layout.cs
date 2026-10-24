using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LoggerWindow
    {
        private const float MinListSize = 110f;
        private const float MinDetailsHeight = 120f;
        private const float MinDetailsWidth = 320f;

        [SerializeField] private float detailsWidth = 460f;
        [SerializeField] private bool detailsCollapsed;
        [SerializeField] private bool detailsSized;

        private VisualElement body;
        private VisualElement splitHandle;
        private VisualElement searchRow;
        private VisualElement topBar;
        private bool sideBySide;
        private bool dragging;
        private float dragStart;
        private float dragStartSize;

        /// <summary>
        /// The list and the details pane with a handle between them. Wide, short windows (the Console's usual dock at the
        /// bottom of the editor) put details on the right; others put them below. Double-click the handle to fold them.
        /// </summary>
        private VisualElement BuildBody()
        {
            body = LoggerUi.Box("lg-body");
            body.Add(BuildListPane());
            splitHandle = LoggerUi.Box("lg-split-handle");
            splitHandle.Add(LoggerUi.Box("lg-split-handle__line", PickingMode.Ignore));
            splitHandle.tooltip = "Drag to resize the details. Double-click to fold them.";
            splitHandle.RegisterCallback<PointerDownEvent>(OnHandleDown);
            splitHandle.RegisterCallback<PointerMoveEvent>(OnHandleMove);
            splitHandle.RegisterCallback<PointerUpEvent>(OnHandleUp);
            body.Add(splitHandle);
            body.Add(BuildDetailsPane());
            body.RegisterCallback<GeometryChangedEvent>(_ => ApplyLayout());
            return body;
        }

        private void ApplyLayout()
        {
            if (body == null)
            {
                return;
            }

            float width = body.layout.width;
            float height = body.layout.height;
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 0 || height <= 0)
            {
                return;
            }

            // Decided from the window, which the panes can't change, with some slack so a resize near the limit (or a
            // transient layout pass, such as when a menu opens) doesn't flip the panes back and forth.
            var window = rootVisualElement.layout;
            if (!float.IsNaN(window.width) && window.width > 0 && window.height > 0)
            {
                float ratio = window.width / window.height;
                bool side = sideBySide ? window.width >= 860f && ratio > 1.9f : window.width >= 900f && ratio > 2.1f;
                sideBySide = side;
            }

            body.EnableInClassList("lg-body--side", sideBySide);

            detailsPane.EnableInClassList("lg-details--collapsed", detailsCollapsed);
            if (sideBySide)
            {
                float max = Math.Max(MinDetailsWidth, width - MinListSize * 3f);
                float size = detailsCollapsed ? 0f : Mathf.Clamp(detailsWidth, MinDetailsWidth, max);
                detailsPane.style.width = size;
                detailsPane.style.height = StyleKeyword.Auto;
            }
            else
            {
                float max = Math.Max(MinDetailsHeight, height - MinListSize);
                // The default leaves the list most of a short window; a size the user dragged is kept as is.
                float preferred = detailsSized ? detailsPaneHeight : Math.Min(detailsPaneHeight, height * 0.45f);
                float size = detailsCollapsed ? 0f : Mathf.Clamp(preferred, MinDetailsHeight, max);
                detailsPane.style.height = size;
                detailsPane.style.width = StyleKeyword.Auto;
            }

            ApplyWidthClasses(rootVisualElement.layout.width);
        }

        private void OnHandleDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
            {
                return;
            }

            if (evt.clickCount >= 2)
            {
                detailsCollapsed = !detailsCollapsed;
                ApplyLayout();
                evt.StopPropagation();
                return;
            }

            dragging = true;
            detailsCollapsed = false;
            dragStart = sideBySide ? evt.position.x : evt.position.y;
            dragStartSize = sideBySide ? detailsPane.layout.width : detailsPane.layout.height;
            splitHandle.CapturePointer(evt.pointerId);
            splitHandle.AddToClassList("lg-split-handle--active");
            evt.StopPropagation();
        }

        private void OnHandleMove(PointerMoveEvent evt)
        {
            if (!dragging)
            {
                return;
            }

            float delta = (sideBySide ? evt.position.x : evt.position.y) - dragStart;
            float size = dragStartSize - delta;
            if (sideBySide)
            {
                detailsWidth = Mathf.Clamp(size, MinDetailsWidth, Math.Max(MinDetailsWidth, body.layout.width - MinListSize * 3f));
            }
            else
            {
                detailsPaneHeight = Mathf.Clamp(size, MinDetailsHeight, Math.Max(MinDetailsHeight, body.layout.height - MinListSize));
                detailsSized = true;
            }

            ApplyLayout();
        }

        private void OnHandleUp(PointerUpEvent evt)
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            splitHandle.ReleasePointer(evt.pointerId);
            splitHandle.RemoveFromClassList("lg-split-handle--active");
        }

        /// <summary>Narrower windows drop labels first, then move the search under the toolbar.</summary>
        private void ApplyWidthClasses(float width)
        {
            if (float.IsNaN(width) || width <= 0)
            {
                return;
            }

            rootVisualElement.EnableInClassList("lg-root--m", width < 1180f);
            rootVisualElement.EnableInClassList("lg-root--s", width < 1000f);
            bool tiny = width < 780f;
            rootVisualElement.EnableInClassList("lg-root--xs", tiny);
            rootVisualElement.EnableInClassList("lg-root--short", rootVisualElement.layout.height < 520f);
            if (searchBox == null || searchRow == null)
            {
                return;
            }

            // The search keeps its own row when the toolbar can't hold it.
            if (tiny && searchBox.parent != searchRow)
            {
                searchRow.Add(searchBox);
                searchRow.style.display = DisplayStyle.Flex;
            }
            else if (!tiny && searchBox.parent != topBar)
            {
                topBar.Insert(topBar.IndexOf(sourcesButton), searchBox);
                searchRow.style.display = DisplayStyle.None;
            }
        }
    }
}
