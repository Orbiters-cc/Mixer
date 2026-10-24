using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// A list of fixed-height rows that only creates the rows on screen and scrolls by row index, kept as a double.
    /// Unity's ListView positions rows in pixels with floats, which loses whole pixels past a few million rows; here
    /// a hundred million rows scroll as smoothly as a hundred. Scrolling one row rebinds one row: each item keeps the
    /// pooled row <c>item % pool</c>.
    /// </summary>
    internal sealed class VirtualList : VisualElement
    {
        private readonly VisualElement viewport;
        private readonly Scroller scroller;
        private readonly List<VisualElement> pool = new List<VisualElement>();
        private readonly List<int> bound = new List<int>();
        private readonly Func<VisualElement> makeRow;
        private readonly Action<VisualElement, int> bindRow;
        private int itemCount;
        private float rowHeight;
        private double first;
        private bool updatingScroller;

        public VirtualList(float rowHeight, Func<VisualElement> makeRow, Action<VisualElement, int> bindRow)
        {
            this.rowHeight = rowHeight;
            this.makeRow = makeRow;
            this.bindRow = bindRow;
            AddToClassList("lg-vlist");
            focusable = true;
            tabIndex = 0;

            viewport = new VisualElement();
            viewport.AddToClassList("lg-vlist__viewport");
            Add(viewport);

            scroller = new Scroller(0f, 1f, value =>
            {
                if (!updatingScroller)
                {
                    SetFirst(value, fromScroller: true);
                }
            }, SliderDirection.Vertical);
            scroller.AddToClassList("lg-vlist__scroller");
            Add(scroller);

            RegisterCallback<WheelEvent>(OnWheel);
            viewport.RegisterCallback<GeometryChangedEvent>(_ => Layout());
        }

        /// <summary>The first visible row (fraction = how much of it is scrolled off).</summary>
        public double First => first;
        public int ItemCount => itemCount;
        public float RowHeight => rowHeight;
        public float ViewportHeight => float.IsNaN(viewport.layout.height) ? 0f : viewport.layout.height;
        public double VisibleRows => rowHeight > 0 ? ViewportHeight / rowHeight : 0d;
        public double MaxFirst => Math.Max(0d, itemCount - VisibleRows);
        public bool AtEnd => first >= MaxFirst - 0.01d;
        public VisualElement Viewport => viewport;

        /// <summary>Raised after the list scrolled (by the user or <see cref="SetFirst"/>).</summary>
        public event Action Scrolled;

        public void SetItemCount(int count, bool keepAtEnd)
        {
            bool wasAtEnd = AtEnd;
            itemCount = Math.Max(0, count);
            if (keepAtEnd && wasAtEnd)
            {
                first = MaxFirst;
            }

            first = Clamp(first);
            Layout();
        }

        public void SetRowHeight(float height)
        {
            if (Mathf.Approximately(height, rowHeight))
            {
                return;
            }

            double anchor = first;
            rowHeight = height;
            foreach (var row in pool)
            {
                row.style.height = rowHeight;
            }

            first = Clamp(anchor);
            Layout();
        }

        public void SetFirst(double value, bool fromScroller = false)
        {
            value = Clamp(value);
            if (Math.Abs(value - first) < 0.0001d)
            {
                return;
            }

            first = value;
            Layout(updateScroller: !fromScroller);
            Scrolled?.Invoke();
        }

        public void ScrollToEnd() => SetFirst(MaxFirst);

        /// <summary>Scrolls just enough for <paramref name="index"/> to be fully visible.</summary>
        public void Reveal(int index)
        {
            if (index < 0 || index >= itemCount)
            {
                return;
            }

            double visible = VisibleRows;
            if (index < first)
            {
                SetFirst(index);
            }
            else if (index + 1 > first + visible)
            {
                SetFirst(index + 1 - visible);
            }
        }

        /// <summary>Scrolls so <paramref name="index"/> is at about a third from the top.</summary>
        public void Center(int index)
        {
            if (index >= 0 && index < itemCount)
            {
                SetFirst(index - VisibleRows / 3d);
            }
        }

        /// <summary>The item under a point in the list's coordinates, or -1.</summary>
        public int ItemAt(Vector2 localPosition)
        {
            var point = this.ChangeCoordinatesTo(viewport, localPosition);
            if (point.y < 0 || point.y > ViewportHeight || rowHeight <= 0)
            {
                return -1;
            }

            int index = (int)Math.Floor(first + point.y / rowHeight);
            return index >= 0 && index < itemCount ? index : -1;
        }

        /// <summary>The item a pooled row shows, or -1.</summary>
        public int ItemOf(VisualElement row)
        {
            int slot = pool.IndexOf(row);
            return slot >= 0 ? bound[slot] : -1;
        }

        /// <summary>Binds every visible row again (selection or content changed).</summary>
        public void RefreshRows()
        {
            for (int i = 0; i < bound.Count; i++)
            {
                bound[i] = -1;
            }

            Layout();
        }

        private double Clamp(double value)
        {
            if (double.IsNaN(value))
            {
                return 0d;
            }

            return Math.Max(0d, Math.Min(value, MaxFirst));
        }

        private void OnWheel(WheelEvent evt)
        {
            if (itemCount == 0)
            {
                return;
            }

            // A wheel notch is 3 rows; touchpads send fractions, scrolled as they come.
            SetFirst(first + evt.delta.y);
            evt.StopPropagation();
        }

        private void Layout(bool updateScroller = true)
        {
            float height = ViewportHeight;
            if (height <= 0 || rowHeight <= 0)
            {
                return;
            }

            int needed = Math.Min(itemCount, (int)Math.Ceiling(height / rowHeight) + 1);
            while (pool.Count < needed)
            {
                var row = makeRow();
                row.AddToClassList("lg-vlist__row");
                row.style.position = Position.Absolute;
                row.style.left = 0;
                row.style.right = 0;
                row.style.height = rowHeight;
                viewport.Add(row);
                pool.Add(row);
                bound.Add(-1);
            }

            int start = (int)Math.Floor(first);
            float offset = (float)((first - start) * rowHeight);
            int poolSize = pool.Count;
            for (int k = 0; k < poolSize; k++)
            {
                int item = start + k;
                if (k >= needed || item >= itemCount)
                {
                    continue;
                }

                int slot = item % poolSize;
                var row = pool[slot];
                if (bound[slot] != item)
                {
                    bound[slot] = item;
                    bindRow(row, item);
                }

                row.style.top = k * rowHeight - offset;
                row.style.display = DisplayStyle.Flex;
            }

            // Slots not used this frame.
            for (int slot = 0; slot < poolSize; slot++)
            {
                int item = bound[slot];
                if (item < start || item >= start + needed || item >= itemCount)
                {
                    pool[slot].style.display = DisplayStyle.None;
                    bound[slot] = -1;
                }
            }

            if (updateScroller)
            {
                updatingScroller = true;
                float max = (float)MaxFirst;
                scroller.highValue = Math.Max(0.0001f, max);
                scroller.lowValue = 0f;
                scroller.value = (float)first;
                scroller.Adjust(itemCount > 0 ? Mathf.Clamp01((float)(VisibleRows / itemCount)) : 1f);
                scroller.slider.pageSize = (float)VisibleRows;
                updatingScroller = false;
            }

            scroller.style.display = itemCount > VisibleRows ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
