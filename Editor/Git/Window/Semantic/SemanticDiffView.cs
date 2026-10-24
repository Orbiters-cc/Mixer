using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit.Editor.Semantic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// What changed in a scene or prefab, the way it shows in Unity: one card per GameObject (or prefab instance), its
    /// components inside, and each changed value as before → after. Reading and comparing run in the background; names
    /// of scripts and assets are resolved when a card opens.
    /// </summary>
    internal sealed class SemanticDiffView : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-semantic.uss";
        private const int PropertiesShown = 40;

        // Results and open cards survive the window rebuilding itself, so nothing reloads or re-animates on a refresh.
        private static readonly Dictionary<string, SemanticChangeSet> Results = new Dictionary<string, SemanticChangeSet>();
        private static readonly Dictionary<string, Task<SemanticChangeSet>> Pending = new Dictionary<string, Task<SemanticChangeSet>>();
        private static readonly Dictionary<string, HashSet<long>> Open = new Dictionary<string, HashSet<long>>();
        private static readonly HashSet<SemanticChangeSet> Animated = new HashSet<SemanticChangeSet>();

        private readonly VisualElement header, list;
        private readonly ScrollView scroll;
        private MaterialCompareView materials;
        private readonly Label summary;
        private readonly TextField filter;
        private string key = string.Empty;
        private SemanticChangeSet set;
        private UnitySemanticNames names;
        private string beforeTitle = "Before", afterTitle = "After";

        public SemanticDiffView()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null) styleSheets.Add(sheet);
            AddToClassList("ugs");

            header = new VisualElement();
            header.AddToClassList("ugs-header");
            Add(header);
            summary = new Label();
            summary.AddToClassList("ugs-summary");
            header.Add(summary);
            var holder = new VisualElement();
            holder.AddToClassList("ugs-filter-holder");
            header.Add(holder);
            filter = new TextField { tooltip = "Filter by object, component or property" };
            filter.AddToClassList("ugs-filter");
            holder.Add(filter);
            var placeholder = new Label("Filter…") { pickingMode = PickingMode.Ignore };
            placeholder.AddToClassList("ugs-placeholder");
            holder.Add(placeholder);
            filter.RegisterValueChangedCallback(evt =>
            {
                placeholder.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                Render(false);
            });
            var expand = Chip("Expand all", "Open every card.", () => SetAll(true));
            var collapse = Chip("Collapse", "Close every card.", () => SetAll(false));
            header.Add(expand);
            header.Add(collapse);

            scroll = new ScrollView();
            scroll.AddToClassList("ugs-scroll");
            Add(scroll);
            list = new VisualElement();
            list.AddToClassList("ugs-list");
            scroll.Add(list);
            ShowLoading();
        }

        /// <summary>Compares the two texts in the background. <paramref name="cacheKey"/> names these exact versions.</summary>
        public void Load(string cacheKey, Func<string> readBefore, Func<string> readAfter, string beforeLabel, string afterLabel, string assetPath = null)
        {
            materials?.RemoveFromHierarchy();
            materials = null;
            if (string.Equals(System.IO.Path.GetExtension(assetPath), ".mat", StringComparison.OrdinalIgnoreCase))
            {
                // Both consumers share each Git read, including when they start on different worker threads.
                var before = new Lazy<string>(readBefore);
                var after = new Lazy<string>(readAfter);
                readBefore = () => before.Value;
                readAfter = () => after.Value;
                materials = new MaterialCompareView(assetPath, readBefore, readAfter, beforeLabel, afterLabel);
                scroll.Insert(0, materials);
            }
            key = cacheKey ?? string.Empty;
            beforeTitle = beforeLabel;
            afterTitle = afterLabel;
            lock (Results)
            {
                if (Results.TryGetValue(key, out var cached))
                {
                    Show(cached);
                    return;
                }
            }
            ShowLoading();
            string requested = key;
            Task<SemanticChangeSet> task;
            lock (Results)
            {
                // A view rebuilt while its file is still being compared picks up the same work.
                if (!Pending.TryGetValue(requested, out task))
                {
                    task = Task.Run(() => UnitySemanticDiff.Compare(readBefore() ?? string.Empty, readAfter() ?? string.Empty));
                    Pending[requested] = task;
                }
            }
            bool handled = false;
            schedule.Execute(() =>
            {
                if (handled || !task.IsCompleted) return;
                handled = true;
                lock (Results) Pending.Remove(requested);
                if (requested != key) return;
                if (task.IsFaulted)
                {
                    ShowMessage("Could not compare this file", task.Exception?.GetBaseException().Message ?? "Unknown error.", true);
                    return;
                }
                lock (Results)
                {
                    if (Results.Count > 24) Results.Clear();
                    Results[requested] = task.Result;
                }
                Show(task.Result);
            }).Every(40).Until(() => handled);
        }

        private void Show(SemanticChangeSet result)
        {
            set = result;
            names = new UnitySemanticNames();
            Render(!Animated.Contains(result));
            Animated.Add(result);
        }

        private void ShowLoading()
        {
            set = null;
            summary.text = "Reading changes…";
            list.Clear();
            // Placeholder cards that shimmer while the file is read.
            for (int i = 0; i < 4; i++)
            {
                var skeleton = new VisualElement();
                skeleton.AddToClassList("ugs-skeleton");
                skeleton.style.width = new Length(92 - i * 13, LengthUnit.Percent);
                list.Add(skeleton);
                int index = i;
                skeleton.schedule.Execute(() => skeleton.ToggleInClassList("ugs-skeleton--dim")).Every(520 + index * 40);
            }
        }

        private void ShowMessage(string title, string body, bool warning)
        {
            list.Clear();
            var card = new VisualElement();
            card.AddToClassList("ugs-message");
            card.EnableInClassList("ugs-message--warning", warning);
            var t = new Label(title);
            t.AddToClassList("ugs-message__title");
            card.Add(t);
            var b = new Label(body);
            b.AddToClassList("ugs-message__body");
            card.Add(b);
            list.Add(card);
        }

        private void Render(bool animate)
        {
            if (set == null) return;
            list.Clear();
            summary.text = Summary();
            if (set.Objects.Count == 0)
            {
                ShowMessage("Nothing that matters changed", "Unity rewrote bookkeeping fields only (serialization versions, hints, object order). The Text view shows the raw lines.", false);
                return;
            }
            string query = (filter.value ?? string.Empty).Trim();
            if (!Open.TryGetValue(key, out var open))
            {
                open = new HashSet<long>();
                // Small change sets open by themselves: there is nothing to scan through.
                if (set.Objects.Count <= 3) foreach (var item in set.Objects) open.Add(item.FileId);
                Open[key] = open;
            }
            int shown = 0;
            foreach (var item in set.Objects)
            {
                if (query.Length > 0 && !Matches(item, query)) continue;
                var card = Card(item, open, query);
                list.Add(card);
                if (animate && shown < 14)
                {
                    card.AddToClassList("ugs-card--enter");
                    int delay = 30 + shown * 35;
                    card.schedule.Execute(() => card.RemoveFromClassList("ugs-card--enter")).StartingIn(delay);
                }
                shown++;
            }
            if (shown == 0) ShowMessage("No match", "No changed object, component or property matches \"" + query + "\".", false);
        }

        private string Summary()
        {
            var parts = new List<string>();
            if (set.Modified > 0) parts.Add(set.Modified + " changed");
            if (set.Added > 0) parts.Add(set.Added + " added");
            if (set.Removed > 0) parts.Add(set.Removed + " removed");
            return parts.Count == 0 ? "No changes" : string.Join(" · ", parts) + "   " + beforeTitle + " → " + afterTitle;
        }

        private bool Matches(SemanticObject item, string query)
        {
            bool Has(string text) => text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has(item.Path) || Has(DisplayName(item))) return true;
            foreach (var component in item.Components)
            {
                if (Has(names.Component(component).label)) return true;
                if (component.Properties.Any(p => Has(p.Label) || Has(p.Before) || Has(p.After))) return true;
            }
            return item.Overrides.Any(o => Has(o.Label) || Has(o.Before) || Has(o.After));
        }

        private string DisplayName(SemanticObject item)
        {
            string name = item.Name;
            if ((string.IsNullOrEmpty(name) || name == "?") && !string.IsNullOrEmpty(item.SourceGuid)) name = names.Inside(item.SourceGuid, item.SourceFileId);
            if ((string.IsNullOrEmpty(name) || name == "?") && item.PrefabInstance) name = names.Asset(item.PrefabGuid, 0);
            return string.IsNullOrEmpty(name) || name == "?" ? "Unnamed object" : name;
        }

        // ---- Cards ---------------------------------------------------------------------------------------------------

        private VisualElement Card(SemanticObject item, HashSet<long> open, string query)
        {
            var card = new VisualElement();
            card.AddToClassList("ugs-card");
            card.AddToClassList("ugs-card--" + item.Kind.ToString().ToLowerInvariant());

            var head = new VisualElement();
            head.AddToClassList("ugs-card__head");
            card.Add(head);
            var chevron = new Label("›");
            chevron.AddToClassList("ugs-chevron");
            head.Add(chevron);
            var dot = new VisualElement();
            dot.AddToClassList("ugs-dot");
            dot.AddToClassList("ugs-dot--" + item.Kind.ToString().ToLowerInvariant());
            head.Add(dot);
            var texts = new VisualElement();
            texts.AddToClassList("ugs-card__texts");
            head.Add(texts);
            var title = new Label(DisplayName(item));
            title.AddToClassList("ugs-card__title");
            texts.Add(title);
            string parent = ParentPath(item.Path);
            if (parent.Length > 0)
            {
                var path = new Label(parent);
                path.AddToClassList("ugs-card__path");
                texts.Add(path);
            }
            if (item.PrefabInstance) head.Add(Pill("Prefab", "ugs-pill--prefab", "An instance of " + names.Asset(item.PrefabGuid, 0) + "."));
            foreach (var family in item.Components.Select(c => names.Component(c).family).Where(f => f != ComponentFamily.Unity && f != ComponentFamily.Script).Distinct())
                head.Add(FamilyPill(family));
            if (item.Kind != SemanticChangeKind.Modified) head.Add(Pill(item.Kind == SemanticChangeKind.Added ? "Added" : "Removed", "ugs-pill--" + item.Kind.ToString().ToLowerInvariant(), null));
            var count = new Label(item.ChangeCount.ToString());
            count.AddToClassList("ugs-count");
            count.tooltip = item.ChangeCount + " change" + (item.ChangeCount == 1 ? "" : "s");
            head.Add(count);

            var body = new VisualElement();
            body.AddToClassList("ugs-card__body");
            card.Add(body);
            bool expanded = open.Contains(item.FileId) || query.Length > 0;
            void Apply(bool value)
            {
                card.EnableInClassList("ugs-card--open", value);
                body.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
                if (value && body.childCount == 0) FillBody(body, item);
            }
            Apply(expanded);
            // Opens on press, like the rest of Mixer Git's controls.
            head.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                bool now = !card.ClassListContains("ugs-card--open");
                if (now) open.Add(item.FileId); else open.Remove(item.FileId);
                Apply(now);
                if (now)
                {
                    body.AddToClassList("ugs-card__body--enter");
                    body.schedule.Execute(() => body.RemoveFromClassList("ugs-card__body--enter")).StartingIn(16);
                }
            });
            return card;
        }

        private void FillBody(VisualElement body, SemanticObject item)
        {
            if (item.Kind != SemanticChangeKind.Modified)
            {
                var note = new Label(item.Kind == SemanticChangeKind.Added ? "New object with these components:" : "Removed with these components:");
                note.AddToClassList("ugs-note");
                body.Add(note);
            }
            foreach (var component in item.Components) body.Add(ComponentSection(component, item));
            if (item.Overrides.Count > 0) body.Add(OverridesSection(item));
        }

        private VisualElement ComponentSection(SemanticComponent component, SemanticObject owner)
        {
            var section = new VisualElement();
            section.AddToClassList("ugs-component");
            var head = new VisualElement();
            head.AddToClassList("ugs-component__head");
            section.Add(head);
            var (label, family) = names.Component(component);
            var title = new Label(label);
            title.AddToClassList("ugs-component__title");
            head.Add(title);
            // "VRCFury" needs no VRCFury pill; "Contact Receiver" does get its VRChat one.
            if (family != ComponentFamily.Unity && family != ComponentFamily.Script && label != FamilyPill(family).text) head.Add(FamilyPill(family));
            if (component.Kind != SemanticChangeKind.Modified && owner.Kind == SemanticChangeKind.Modified)
                head.Add(Pill(component.Kind == SemanticChangeKind.Added ? "Added" : "Removed", "ugs-pill--" + component.Kind.ToString().ToLowerInvariant(), null));
            var file = component.Kind == SemanticChangeKind.Removed ? set.Before : set.After;
            AddRows(section, component.Properties.Select(p => (p.Label, p.Path, p.Before, p.After, p.Kind)).ToList(), file);
            return section;
        }

        private VisualElement OverridesSection(SemanticObject item)
        {
            var section = new VisualElement();
            section.AddToClassList("ugs-component");
            var head = new VisualElement();
            head.AddToClassList("ugs-component__head");
            section.Add(head);
            var title = new Label("Prefab overrides");
            title.AddToClassList("ugs-component__title");
            title.tooltip = "Values this instance changes on its prefab (" + names.Asset(item.PrefabGuid, 0) + ").";
            head.Add(title);
            var rows = new List<(string, string, string, string, SemanticChangeKind)>();
            foreach (var group in item.Overrides.GroupBy(o => names.Inside(o.TargetGuid, o.TargetFileId) ?? "Object in prefab"))
                foreach (var o in group)
                    rows.Add((group.Key + " › " + o.Label, o.PropertyPath, o.Before, o.After, o.Kind));
            AddRows(section, rows, set.After);
            return section;
        }

        private void AddRows(VisualElement section, List<(string label, string path, string before, string after, SemanticChangeKind kind)> rows, UnityYamlFile file)
        {
            if (rows.Count == 0) return;
            int limit = Math.Min(rows.Count, PropertiesShown);
            var holder = new VisualElement();
            holder.AddToClassList("ugs-rows");
            section.Add(holder);
            void AddRange(int from, int to)
            {
                for (int i = from; i < to; i++) holder.Add(Row(rows[i], file));
            }
            AddRange(0, limit);
            if (rows.Count > limit)
            {
                Button more = null;
                more = Chip("Show " + (rows.Count - limit) + " more", "Show every changed value of this component.", () =>
                {
                    more.RemoveFromHierarchy();
                    AddRange(limit, rows.Count);
                });
                more.AddToClassList("ugs-more");
                section.Add(more);
            }
        }

        private VisualElement Row((string label, string path, string before, string after, SemanticChangeKind kind) row, UnityYamlFile file)
        {
            var element = new VisualElement();
            element.AddToClassList("ugs-row");
            var label = new Label(row.label);
            label.AddToClassList("ugs-row__label");
            label.tooltip = row.path;
            element.Add(label);
            var values = new VisualElement();
            values.AddToClassList("ugs-row__values");
            element.Add(values);
            string before = names.Value(row.before, row.path, set.Before);
            string after = names.Value(row.after, row.path, file);
            if (row.kind != SemanticChangeKind.Added) values.Add(Value(before, "ugs-value--before"));
            if (row.kind == SemanticChangeKind.Modified)
            {
                var arrow = new Label("→");
                arrow.AddToClassList("ugs-arrow");
                values.Add(arrow);
            }
            if (row.kind != SemanticChangeKind.Removed) values.Add(Value(after, "ugs-value--after"));
            element.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Copy property path", _ => EditorGUIUtility.systemCopyBuffer = row.path);
                if (row.before != null) evt.menu.AppendAction("Copy old value", _ => EditorGUIUtility.systemCopyBuffer = row.before);
                if (row.after != null) evt.menu.AppendAction("Copy new value", _ => EditorGUIUtility.systemCopyBuffer = row.after);
            }));
            return element;
        }

        private static Label Value(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList("ugs-value");
            label.AddToClassList(className);
            label.tooltip = text;
            return label;
        }

        private static Label Pill(string text, string className, string tooltip)
        {
            var pill = new Label(text) { tooltip = tooltip };
            pill.AddToClassList("ugs-pill");
            pill.AddToClassList(className);
            return pill;
        }

        private static Label FamilyPill(ComponentFamily family)
        {
            switch (family)
            {
                case ComponentFamily.VRChat: return Pill("VRChat", "ugs-pill--vrchat", "A VRChat SDK component.");
                case ComponentFamily.VRCFury: return Pill("VRCFury", "ugs-pill--vrcfury", "A VRCFury component.");
                case ComponentFamily.ModularAvatar: return Pill("MA", "ugs-pill--ma", "A Modular Avatar component.");
                case ComponentFamily.Orbiters: return Pill("Orbiters", "ugs-pill--orbiters", "An Orbiters component.");
                default: return Pill("Script", "ugs-pill--script", null);
            }
        }

        private static Button Chip(string text, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            button.AddToClassList("ugs-chip");
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("ugs-chip--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ugs-chip--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ugs-chip--pressed"));
            return button;
        }

        private void SetAll(bool value)
        {
            if (set == null) return;
            if (!Open.TryGetValue(key, out var open)) Open[key] = open = new HashSet<long>();
            open.Clear();
            if (value) foreach (var item in set.Objects) open.Add(item.FileId);
            Render(false);
        }

        private static string ParentPath(string path)
        {
            int slash = (path ?? string.Empty).LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash).Replace("/", " › ") : string.Empty;
        }
    }
}
