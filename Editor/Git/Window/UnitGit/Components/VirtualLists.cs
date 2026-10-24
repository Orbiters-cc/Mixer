using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        internal static ListView BuildVirtualList<T>(IList<T> items, float height, Func<T, VisualElement> buildRow, string scrollName)
        {
            var list = new ListView
            {
                itemsSource = (IList)items,
                fixedItemHeight = height,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = () => new VisualElement(),
                bindItem = (element, index) =>
                {
                    element.Clear();
                    var row = buildRow(items[index]);
                    row.style.height = height;
                    row.style.minHeight = height;
                    row.style.marginTop = 0;
                    row.style.marginBottom = 0;
                    element.Add(row);
                }
            };
            list.style.flexGrow = 1;
            list.style.flexBasis = 0;
            list.style.minHeight = 0;
            var scroll = list.Q<ScrollView>();
            scroll.name = scrollName;
            return list;
        }
    }
}
