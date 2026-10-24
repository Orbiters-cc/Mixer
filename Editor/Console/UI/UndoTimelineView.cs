using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Beta: the undo history as a strip under the activity chart, like a video's progress bar. Each step is a tick
    /// (selections smaller), the part already done is filled, the playhead is where the project is. Hover shows the
    /// Scene view after a step; click or drag to go there (undo or redo, step by step); scroll for one step back or
    /// forward.
    /// </summary>
    internal sealed class UndoTimelineView : VisualElement
    {
        private const float CardWidth = 216f;

        private readonly VisualElement strip;
        private readonly QuadBatch batch = new QuadBatch();
        private readonly Label countLabel;
        private readonly VisualElement card;
        private readonly Image cardImage;
        private readonly Label cardEmpty;
        private readonly Label cardName;
        private readonly Label cardMeta;
        private readonly Label cardHint;
        private readonly VisualElement overlay;
        private Texture2D preview;
        private int previewId = -2;
        private int hover = int.MinValue;
        private bool scrubbing;
        private int pointerId = -1;
        private int seenVersion = -1;

        /// <summary>A step was hovered (its time, UTC ticks) or left (0): the activity chart marks the moment.</summary>
        public event Action<long> TimeHovered;

        public UndoTimelineView(VisualElement overlayRoot)
        {
            overlay = overlayRoot;
            AddToClassList("lg-undo");
            var icon = new LoggerIcon(LoggerGlyph.Undo);
            icon.AddToClassList("lg-undo__icon");
            icon.tooltip = "Undo history (beta)";
            Add(icon);

            strip = LoggerUi.Box("lg-undo__strip");
            strip.generateVisualContent += Draw;
            strip.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            strip.RegisterCallback<PointerDownEvent>(OnPointerDown);
            strip.RegisterCallback<PointerUpEvent>(OnPointerUp);
            strip.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!scrubbing)
                {
                    SetHover(int.MinValue);
                }
            });
            strip.RegisterCallback<WheelEvent>(evt =>
            {
                if (UndoHistory.Steps.Count == 0)
                {
                    return;
                }

                UndoHistory.Step(evt.delta.y > 0 ? 1 : -1);
                evt.StopPropagation();
            });
            Add(strip);

            countLabel = LoggerUi.Text(string.Empty, "lg-undo__count");
            Add(countLabel);

            card = LoggerUi.Box("lg-undo-card", PickingMode.Ignore);
            cardImage = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit };
            cardImage.AddToClassList("lg-undo-card__image");
            card.Add(cardImage);
            cardEmpty = LoggerUi.Text("No snapshot of this step", "lg-undo-card__empty");
            cardEmpty.pickingMode = PickingMode.Ignore;
            cardImage.Add(cardEmpty);
            cardName = LoggerUi.Text(string.Empty, "lg-undo-card__name");
            cardMeta = LoggerUi.Text(string.Empty, "lg-undo-card__meta");
            cardHint = LoggerUi.Text(string.Empty, "lg-undo-card__hint");
            card.Add(cardName);
            card.Add(cardMeta);
            card.Add(cardHint);
            card.style.display = DisplayStyle.None;
            RegisterCallback<AttachToPanelEvent>(_ => overlay.Add(card));
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                card.RemoveFromHierarchy();
                if (preview != null)
                {
                    UnityEngine.Object.DestroyImmediate(preview);
                    preview = null;
                }
            });
        }

        /// <summary>Redraws when the history moved. Cheap: call it every tick.</summary>
        public void Refresh()
        {
            if (UndoHistory.Version == seenVersion)
            {
                return;
            }

            seenVersion = UndoHistory.Version;
            var steps = UndoHistory.Steps;
            int undone = steps.Count - 1 - UndoHistory.Cursor;
            countLabel.text = steps.Count == 0
                ? "No steps yet"
                : LoggerUi.Plural(steps.Count, "step") + (undone > 0 ? "  ·  " + LoggerUi.Count(undone) + " undone" : string.Empty);
            if (hover != int.MinValue)
            {
                UpdateCard();
            }

            strip.MarkDirtyRepaint();
        }

        private float Width => float.IsNaN(strip.contentRect.width) ? 0f : strip.contentRect.width;

        // Positions -1 (before every step) to Count-1, evenly spread.
        private float XOf(int position)
        {
            int slots = UndoHistory.Steps.Count + 1;
            return (position + 1.5f) / slots * Width;
        }

        private int PositionAt(float x)
        {
            int count = UndoHistory.Steps.Count;
            if (Width <= 0f)
            {
                return int.MinValue;
            }

            int position = Mathf.FloorToInt(Mathf.Clamp01(x / Width) * (count + 1)) - 1;
            return Mathf.Clamp(position, -1, count - 1);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || UndoHistory.Steps.Count == 0)
            {
                return;
            }

            scrubbing = true;
            pointerId = evt.pointerId;
            strip.CapturePointer(pointerId);
            int position = PositionAt(evt.localPosition.x);
            SetHover(position);
            UndoHistory.GoTo(position);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            int position = PositionAt(evt.localPosition.x);
            SetHover(position);
            if (scrubbing && position != UndoHistory.Target)
            {
                UndoHistory.GoTo(position);
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!scrubbing)
            {
                return;
            }

            scrubbing = false;
            if (strip.HasPointerCapture(pointerId))
            {
                strip.ReleasePointer(pointerId);
            }
        }

        private void SetHover(int position)
        {
            if (position == hover)
            {
                return;
            }

            hover = position;
            UpdateCard();
            strip.MarkDirtyRepaint();
        }

        // The preview card above the strip: the Scene view after the step, its name, when, and what a click does.
        private void UpdateCard()
        {
            var steps = UndoHistory.Steps;
            if (hover == int.MinValue || steps.Count == 0)
            {
                card.style.display = DisplayStyle.None;
                TimeHovered?.Invoke(0L);
                return;
            }

            int cursor = UndoHistory.Cursor;
            var step = hover >= 0 && hover < steps.Count ? steps[hover] : null;
            cardName.text = step != null ? step.Name : "Before every step";
            cardMeta.text = step == null ? "The project before the first step kept" : step.Time > 0
                ? LoggerUi.Moment(step.Time) + "  ·  " + LoggerUi.Ago(step.Time)
                : "Before the Mixer console followed the history";
            cardHint.text = hover == cursor ? "Where the project is now" : hover < cursor
                ? "Click to go back here (undo " + LoggerUi.Plural(cursor - hover, "step") + ")"
                : "Click to come back here (redo " + LoggerUi.Plural(hover - cursor, "step") + ")";
            card.EnableInClassList("lg-undo-card--now", hover == cursor);

            byte[] jpg = step != null ? SceneThumbnails.Get(step.Thumbnail) : null;
            int id = step != null && jpg != null ? step.Thumbnail : -1;
            if (id != previewId)
            {
                previewId = id;
                if (jpg != null)
                {
                    if (preview == null)
                    {
                        preview = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                    }

                    preview.LoadImage(jpg);
                    cardImage.image = preview;
                }
                else
                {
                    cardImage.image = null;
                }
            }

            cardEmpty.style.display = jpg != null ? DisplayStyle.None : DisplayStyle.Flex;
            card.style.display = DisplayStyle.Flex;
            // Over the panes laid out after the strip (the list and the details), not under them.
            if (card.parent != null && card.parent.IndexOf(card) != card.parent.childCount - 1)
            {
                card.BringToFront();
            }

            // Centred on the step, under the strip (over the list), kept inside the window.
            var bounds = strip.worldBound;
            var origin = overlay.worldBound;
            float x = bounds.x - origin.x + XOf(hover) - CardWidth * 0.5f;
            x = Mathf.Clamp(x, 8f, Math.Max(8f, origin.width - CardWidth - 8f));
            card.style.left = x;
            card.style.top = bounds.yMax - origin.y + 8f;
            TimeHovered?.Invoke(step != null ? step.Time : 0L);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = strip.contentRect;
            var steps = UndoHistory.Steps;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            float height = rect.height;
            int count = steps.Count;
            int cursor = UndoHistory.Cursor;
            if (count == 0)
            {
                batch.Rect(0f, height * 0.5f, rect.width, 1f, new Color(1f, 1f, 1f, 0.08f));
                batch.Flush(context);
                return;
            }

            // What is done, like the played part of a video.
            float done = XOf(cursor);
            batch.Rect(0f, 0f, Math.Max(0f, done), height, new Color(0f, 0.85f, 0.43f, 0.09f));

            float slot = rect.width / (count + 1);
            float tick = slot >= 3f ? Math.Min(2f, slot - 1f) : Math.Max(0.6f, slot);
            var doneColor = new Color(0.24f, 0.95f, 0.6f, 0.9f);
            var redoColor = new Color(1f, 1f, 1f, 0.22f);
            for (int i = 0; i < count; i++)
            {
                float x = XOf(i);
                bool selection = steps[i].IsSelection;
                float h = selection ? height * 0.34f : height * 0.68f;
                var color = i <= cursor ? doneColor : redoColor;
                if (selection)
                {
                    color.a *= 0.55f;
                }

                if (i == hover)
                {
                    color = Color.white;
                    h = height * 0.82f;
                }

                batch.Rect(x - tick * 0.5f, (height - h) * 0.5f, tick, h, color);
            }

            if (hover != int.MinValue)
            {
                float x = XOf(hover);
                batch.Rect(x - 0.5f, 0f, 1f, height, new Color(1f, 1f, 1f, 0.28f));
            }

            if (UndoHistory.Traveling)
            {
                float target = XOf(UndoHistory.Target);
                batch.Rect(target - 1f, 0f, 2f, height, new Color(0f, 0.85f, 0.43f, 0.55f));
            }

            batch.Flush(context);

            // The playhead: where the project is.
            var painter = context.painter2D;
            float head = XOf(cursor);
            painter.fillColor = Color.white;
            painter.BeginPath();
            painter.MoveTo(new Vector2(head - 1f, 2f));
            painter.LineTo(new Vector2(head + 1f, 2f));
            painter.LineTo(new Vector2(head + 1f, height - 2f));
            painter.LineTo(new Vector2(head - 1f, height - 2f));
            painter.ClosePath();
            painter.Fill();
            painter.BeginPath();
            painter.Arc(new Vector2(head, 4f), 4f, 0f, 360f);
            painter.Fill();
        }
    }
}
