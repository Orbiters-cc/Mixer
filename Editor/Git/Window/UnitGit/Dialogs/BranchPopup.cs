using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The branch widget's popup, as in JetBrains IDEs: a search field, "New branch", then local and remote branches; a
    /// branch unfolds its actions in place (checkout, new branch from it, merge into the current one, update, delete). In
    /// place rather than as a menu: a menu takes the focus, and a popup without focus closes.
    /// </summary>
    internal sealed class UnitGitBranchPopup : PopupWindowContent
    {
        internal sealed class Actions
        {
            public Action NewBranch;
            public Action<UnitGitBranch> Checkout, NewBranchFrom, Merge, Update, Delete;
        }

        private readonly List<UnitGitBranch> branches;
        private readonly string current;
        private readonly Actions actions;
        private readonly Func<string, Color> colorOf;
        private VisualElement list;
        private string filter = string.Empty;
        private string expanded;

        public UnitGitBranchPopup(IEnumerable<UnitGitBranch> branches, string current, Actions actions, Func<string, Color> colorOf)
        {
            this.branches = branches.ToList();
            this.current = current ?? string.Empty;
            this.actions = actions;
            this.colorOf = colorOf;
        }

        public override Vector2 GetWindowSize() => new Vector2(320, Mathf.Clamp(118 + branches.Count * 28, 190, 440));

        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            root.AddToClassList("unitgit-root");
            root.AddToClassList("ug-popup");
            foreach (string path in new[] { "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-shell.uss", "Packages/orbiters.mixer/Editor/Git/Styles/unitgit.uss" })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }
            var search = UnitGitUi.Search(string.Empty, "Search branches", value => { filter = value ?? string.Empty; Fill(); }, "ug-popup__search");
            root.Add(search);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("ug-popup__scroll");
            list = scroll.contentContainer;
            root.Add(scroll);
            Fill();
            search.schedule.Execute(() => search.Q(className: "unity-text-field__input")?.Focus()).StartingIn(30);
            root.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) editorWindow.Close(); }, TrickleDown.TrickleDown);
        }

        private void Fill()
        {
            list.Clear();
            string term = filter.Trim();
            if (term.Length == 0)
                list.Add(Row(UnitGitIconKind.CreateBranch, "New branch…", null, null, () => Run(actions.NewBranch), "ug-popup__row--action"));
            AddSection("Local", branches.Where(b => !b.IsRemote), term);
            AddSection("Remote", branches.Where(b => b.IsRemote), term);
            if (list.childCount == 0)
                list.Add(UnitGitUi.Text("No branch matches “" + term + "”", "ug-popup__empty"));
        }

        private void AddSection(string title, IEnumerable<UnitGitBranch> section, string term)
        {
            var matching = section
                .Where(b => term.Length == 0 || b.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(b => b.IsCurrent)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matching.Count == 0) return;
            list.Add(UnitGitUi.Text(title, "ug-popup__header"));
            foreach (var branch in matching)
            {
                var item = branch;
                string detail = !string.IsNullOrWhiteSpace(branch.Upstream) && !branch.IsRemote ? branch.Upstream : branch.ShortHash;
                bool open = expanded == item.FullRef;
                var row = Row(branch.IsCurrent ? UnitGitIconKind.Star : UnitGitIconKind.Branch, branch.Name, detail,
                    colorOf?.Invoke(branch.Name), () =>
                    {
                        expanded = expanded == item.FullRef ? null : item.FullRef;
                        Fill();
                    }, branch.IsCurrent ? "ug-popup__row--current" : null);
                row.EnableInClassList("ug-popup__row--open", open);
                row.tooltip = branch.Name + (branch.IsCurrent ? " (current)" : string.Empty);
                var chevron = new UnitGitIconElement(open ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed);
                chevron.AddToClassList("ug-popup__chevron");
                row.Add(chevron);
                list.Add(row);
                if (open) AddBranchActions(item);
            }
        }

        private void AddBranchActions(UnitGitBranch branch)
        {
            string target = string.IsNullOrWhiteSpace(current) ? "the current branch" : "‘" + current + "’";
            if (!branch.IsCurrent) Sub(UnitGitIconKind.Check, "Checkout", () => actions.Checkout?.Invoke(branch));
            Sub(UnitGitIconKind.CreateBranch, "New branch from ‘" + branch.Name + "’…", () => actions.NewBranchFrom?.Invoke(branch));
            if (!branch.IsCurrent) Sub(UnitGitIconKind.Merge, "Merge into " + target, () => actions.Merge?.Invoke(branch));
            if (!branch.IsRemote) Sub(UnitGitIconKind.Pull, "Update (fast-forward from its upstream)", () => actions.Update?.Invoke(branch));
            if (!branch.IsCurrent) Sub(UnitGitIconKind.Delete, "Delete…", () => actions.Delete?.Invoke(branch), "ug-popup__row--danger");

            void Sub(UnitGitIconKind icon, string text, Action action, string extra = null)
            {
                var row = Row(icon, text, null, null, () => Run(action), "ug-popup__row--sub");
                if (extra != null) row.AddToClassList(extra);
                list.Add(row);
            }
        }

        private void Run(Action action)
        {
            if (editorWindow != null) editorWindow.Close();
            action?.Invoke();
        }

        private static Button Row(UnitGitIconKind icon, string text, string detail, Color? dot, Action action, string extraClass)
        {
            var row = new Button();
            row.AddToClassList("ug-popup__row");
            if (!string.IsNullOrEmpty(extraClass)) row.AddToClassList(extraClass);
            var glyph = new UnitGitIconElement(icon);
            if (dot.HasValue && icon == UnitGitIconKind.Branch) glyph.style.opacity = 0.9f;
            row.Add(glyph);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("ug-popup__name");
            row.Add(label);
            if (!string.IsNullOrEmpty(detail))
            {
                var hint = new Label(detail) { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ug-popup__detail");
                row.Add(hint);
            }
            if (action != null) UnitGitUi.Press(row, action);
            return row;
        }
    }
}
