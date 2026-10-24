using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>A run of changed lines: what F7 jumps to, what the markers show and what "»" puts back.</summary>
    internal sealed class UnitGitDiffBlock
    {
        public int First;
        public int End;
        // 1-based line of each side where the block starts; for a side without lines, where they would go.
        public int LeftStart;
        public int RightStart;
        public readonly List<string> Left = new List<string>();
        public readonly List<string> Right = new List<string>();
        // The right side is the file on disk: the block can be put back as the left side has it.
        public bool Editable;
    }

    /// <summary>
    /// Two versions of a text file side by side, as in JetBrains IDEs: line numbers at each side's edge, added, removed and
    /// changed lines tinted (the changed part of a changed line highlighted), a middle column joining the two versions of
    /// each change (with "»" to put a change of the file on disk back), change markers beside the scroll on both sides,
    /// unchanged stretches folded away with a few lines around each change (unfolded 20 lines or all at once), and jumps
    /// between changes and search matches. Rows and their characters are virtualized: a million lines stay fast.
    /// </summary>
    internal sealed class UnitGitDiffViewer : VisualElement
    {
        private const float RowHeight = 20f;
        private const float Gutter = 44f;
        private const float TextInset = 8f;
        private const float DividerWidth = 28f;
        private const int ContextLines = 4;
        private const int ExpandStep = 20;
        private const int MinimumFold = 3;
        internal const string CollapsePref = "Orbiters.UnitGit.Diff.CollapseUnchanged";
        internal const string WhitespacePref = "Orbiters.UnitGit.Diff.Whitespace";
        private static readonly Regex HunkHeader = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);
        private static Font font;
        private static float characterWidth;

        private sealed class Fold
        {
            public int Start;
            public int End;
            // Lines the diff does not contain (between two hunks of a big file): shown, never unfolded.
            public int Missing = -1;
        }

        private readonly Label leftTitle;
        private readonly Label rightTitle;
        private readonly ListView list;
        private readonly ScrollView scroll;
        private readonly Scroller horizontal;
        private readonly UnitGitDiffOverview leftOverview;
        private readonly UnitGitDiffOverview rightOverview;
        // Shown rows: a line index, or -(fold + 1).
        private readonly List<int> rows = new List<int>();
        private readonly List<Fold> folds = new List<Fold>();
        private readonly List<int> leftNumbers = new List<int>();
        private readonly List<int> rightNumbers = new List<int>();
        private readonly List<UnitGitDiffBlock> blocks = new List<UnitGitDiffBlock>();
        private readonly List<int> matches = new List<int>();
        private readonly HashSet<int> matchLines = new HashSet<int>();
        private readonly Dictionary<int, Vector2Int> changedSpans = new Dictionary<int, Vector2Int>();
        private int[] rowOfLine = Array.Empty<int>();
        private int[] blockOfLine = Array.Empty<int>();
        private UnitGitDiff diff = new UnitGitDiff();
        private string search = string.Empty;
        private int matchIndex;
        private int currentChange = -1;
        private float horizontalOffset;
        private bool collapse = EditorPrefs.GetBool(CollapsePref, true);

        public UnitGitDiffViewer()
        {
            AddToClassList("ug-diff");
            EnsureFont();
            var header = new VisualElement();
            header.AddToClassList("unitgit-diff-header");
            leftTitle = new Label();
            rightTitle = new Label();
            leftTitle.AddToClassList("unitgit-diff-header-cell");
            leftTitle.AddToClassList("ug-diff-header-cell--left");
            rightTitle.AddToClassList("unitgit-diff-header-cell");
            var middle = new VisualElement();
            middle.AddToClassList("ug-diff-header__middle");
            header.Add(leftTitle);
            header.Add(middle);
            header.Add(rightTitle);
            Add(header);

            var body = new VisualElement();
            body.AddToClassList("ug-diff__body");
            leftOverview = new UnitGitDiffOverview(this, true);
            body.Add(leftOverview);
            list = new ListView
            {
                itemsSource = rows,
                fixedItemHeight = RowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                horizontalScrollingEnabled = false,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            list.AddToClassList("ug-diff__list");
            list.style.flexGrow = 1;
            list.style.flexBasis = 0;
            list.style.minHeight = 0;
            list.style.minWidth = 0;
            scroll = list.Q<ScrollView>();
            scroll.name = "unitgit-diff-scroll";
            // The markers beside the text are the scroll bar (drag them, click to jump); the wheel scrolls as usual.
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScroller.valueChanged += _ => RepaintOverviews();
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                UpdateHorizontalRange();
                list.RefreshItems();
                RepaintOverviews();
            });
            list.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            body.Add(list);
            rightOverview = new UnitGitDiffOverview(this, false);
            body.Add(rightOverview);
            Add(body);

            horizontal = new Scroller(0f, 1f, value =>
            {
                horizontalOffset = value;
                list.RefreshItems();
            }, SliderDirection.Horizontal);
            horizontal.AddToClassList("ug-diff__hscroll");
            horizontal.style.display = DisplayStyle.None;
            Add(horizontal);
        }

        /// <summary>"»" was pressed on a change: put it back as the left side has it (the host writes the file).</summary>
        public event Action<UnitGitDiffBlock> RevertRequested;

        /// <summary>The host can write the file on disk: changes of its editable section get "»".</summary>
        public bool CanRevert { get; set; }

        public ListView List => list;
        public UnitGitDiff Diff => diff;
        public int DifferenceCount => blocks.Count;
        public int MatchCount => matches.Count;
        public int MatchIndex => matchIndex;
        public int CurrentChange => currentChange;
        public IReadOnlyList<int> Matches => matches;
        public Label LeftTitle => leftTitle;
        public Label RightTitle => rightTitle;
        internal IReadOnlyList<UnitGitDiffBlock> Blocks => blocks;
        internal int RowCount => rows.Count;

        /// <summary>What JetBrains calls "Ignore whitespaces", for every diff Mixer Git shows.</summary>
        internal static UnitGitWhitespace Whitespace
        {
            get => (UnitGitWhitespace)Mathf.Clamp(EditorPrefs.GetInt(WhitespacePref, 0), 0, 3);
            set => EditorPrefs.SetInt(WhitespacePref, (int)value);
        }

        /// <summary>Unchanged lines folded away, a few kept around each change.</summary>
        public bool Collapse
        {
            get => collapse;
            set
            {
                if (collapse == value) return;
                collapse = value;
                EditorPrefs.SetBool(CollapsePref, value);
                Refold();
            }
        }

        /// <summary>
        /// JetBrains' diff view options for a toolbar: fold unchanged lines, and what whitespace to ignore (the diff is read
        /// again with it, so <paramref name="whitespaceChanged"/> reloads it).
        /// </summary>
        internal static VisualElement BuildViewOptions(Func<UnitGitDiffViewer> viewer, Action whitespaceChanged)
        {
            var group = new VisualElement();
            group.AddToClassList("ug-diff-options");
            Button fold = null;
            fold = UnitGitUi.Icon(UnitGitIconKind.CollapseUnchanged, "Fold unchanged lines (a few stay around each change)", () =>
            {
                bool value = !EditorPrefs.GetBool(CollapsePref, true);
                EditorPrefs.SetBool(CollapsePref, value);
                fold.EnableInClassList("ug-icon-button--on", value);
                var current = viewer?.Invoke();
                if (current != null) current.Collapse = value;
            });
            fold.EnableInClassList("ug-icon-button--on", EditorPrefs.GetBool(CollapsePref, true));
            group.Add(fold);

            var whitespace = new Button { tooltip = "What the comparison ignores" };
            whitespace.AddToClassList("ug-dropdown");
            var label = new Label(WhitespaceLabel(Whitespace)) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("ug-dropdown__label");
            whitespace.Add(label);
            var chevron = new UnitGitIconElement(UnitGitIconKind.ChevronExpanded);
            chevron.AddToClassList("ug-dropdown__chevron");
            whitespace.Add(chevron);
            whitespace.EnableInClassList("ug-dropdown--on", Whitespace != UnitGitWhitespace.None);
            UnitGitUi.Press(whitespace, () =>
            {
                var menu = new GenericMenu();
                foreach (UnitGitWhitespace option in Enum.GetValues(typeof(UnitGitWhitespace)))
                {
                    var value = option;
                    menu.AddItem(new GUIContent(WhitespaceLabel(value)), Whitespace == value, () =>
                    {
                        if (Whitespace == value) return;
                        Whitespace = value;
                        label.text = WhitespaceLabel(value);
                        whitespace.EnableInClassList("ug-dropdown--on", value != UnitGitWhitespace.None);
                        whitespaceChanged?.Invoke();
                    });
                }
                menu.DropDown(whitespace.worldBound);
            });
            group.Add(whitespace);
            return group;
        }

        internal static string WhitespaceLabel(UnitGitWhitespace value)
        {
            switch (value)
            {
                case UnitGitWhitespace.Trim: return "Trim whitespaces";
                case UnitGitWhitespace.Ignore: return "Ignore whitespaces";
                case UnitGitWhitespace.IgnoreAndBlankLines: return "Ignore whitespaces and empty lines";
                default: return "Do not ignore";
            }
        }

        private static void EnsureFont()
        {
            if (font != null) return;
            font = Font.CreateDynamicFontFromOSFont(new[] { "JetBrains Mono", "Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono" }, 12);
            font.hideFlags = HideFlags.HideAndDontSave;
            characterWidth = Mathf.Max(1f, new GUIStyle { font = font, fontSize = 12 }.CalcSize(new GUIContent("M")).x);
        }

        /// <summary>Shows another diff (or the same one again), keeping the header labels and the search.</summary>
        public void SetDiff(UnitGitDiff next)
        {
            diff = next ?? new UnitGitDiff();
            leftTitle.text = diff.LeftTitle;
            rightTitle.text = diff.RightTitle;
            Index();
            currentChange = -1;
            Refold();
            UpdateHorizontalRange();
            Search(search, false);
        }

        // ---- Lines, numbers and changes ------------------------------------------------------------------------------

        private static bool IsHunkHeader(UnitGitDiffLine line) =>
            line.Kind == UnitGitDiffLineKind.Hunk && (line.Left ?? string.Empty).StartsWith("@@", StringComparison.Ordinal);

        private static bool IsChange(UnitGitDiffLineKind kind) =>
            kind == UnitGitDiffLineKind.Added || kind == UnitGitDiffLineKind.Removed || kind == UnitGitDiffLineKind.Changed;

        // Line numbers from the hunk headers, the changes (runs of changed lines) and what lies between hunks.
        private void Index()
        {
            leftNumbers.Clear();
            rightNumbers.Clear();
            blocks.Clear();
            changedSpans.Clear();
            int count = diff.Lines.Count;
            blockOfLine = new int[count];
            int left = 1, right = 1;
            bool inHunk = false;
            UnitGitDiffBlock block = null;
            for (int i = 0; i < count; i++)
            {
                var line = diff.Lines[i];
                blockOfLine[i] = -1;
                if (!IsChange(line.Kind)) block = null;
                switch (line.Kind)
                {
                    case UnitGitDiffLineKind.Hunk:
                        var match = HunkHeader.Match(line.Left ?? string.Empty);
                        if (match.Success)
                        {
                            left = int.Parse(match.Groups[1].Value);
                            right = int.Parse(match.Groups[2].Value);
                            // "+0,0" (and "-0,0"): the side is empty, its next line is the first.
                            left = Math.Max(1, left);
                            right = Math.Max(1, right);
                            inHunk = true;
                        }
                        else
                        {
                            // A section ("Staged changes …"): its own hunks number from the start again.
                            left = right = 1;
                            inHunk = false;
                        }
                        leftNumbers.Add(0);
                        rightNumbers.Add(0);
                        continue;
                    case UnitGitDiffLineKind.Context:
                        // A message outside any hunk ("Binary file changed") has no line numbers.
                        leftNumbers.Add(inHunk ? left++ : 0);
                        rightNumbers.Add(inHunk ? right++ : 0);
                        continue;
                }

                if (block == null)
                {
                    block = new UnitGitDiffBlock
                    {
                        First = i,
                        LeftStart = left,
                        RightStart = right,
                        Editable = diff.EditableFrom >= 0 && i >= diff.EditableFrom
                    };
                    blocks.Add(block);
                }
                block.End = i + 1;
                blockOfLine[i] = blocks.Count - 1;
                if (line.Kind != UnitGitDiffLineKind.Added)
                {
                    leftNumbers.Add(left++);
                    block.Left.Add(line.RawLeft ?? line.Left ?? string.Empty);
                }
                else leftNumbers.Add(0);
                if (line.Kind != UnitGitDiffLineKind.Removed)
                {
                    rightNumbers.Add(right++);
                    block.Right.Add(line.RawRight ?? line.Right ?? string.Empty);
                }
                else rightNumbers.Add(0);
            }
        }

        // ---- Folding -------------------------------------------------------------------------------------------------

        // Folds every unchanged stretch longer than the lines kept around changes (when folding is on), and marks the
        // lines a partial diff does not contain.
        private void Refold()
        {
            folds.Clear();
            int count = diff.Lines.Count;
            int nextLeft = 1, nextRight = 1;
            for (int i = 0; i < count; i++)
            {
                var line = diff.Lines[i];
                if (IsHunkHeader(line))
                {
                    var match = HunkHeader.Match(line.Left ?? string.Empty);
                    int missing = 0;
                    if (match.Success)
                        missing = Math.Max(0, Math.Max(int.Parse(match.Groups[1].Value) - nextLeft, int.Parse(match.Groups[2].Value) - nextRight));
                    folds.Add(new Fold { Start = i, End = i + 1, Missing = missing });
                    continue;
                }
                if (line.Kind == UnitGitDiffLineKind.Hunk)
                {
                    nextLeft = nextRight = 1;
                    continue;
                }
                if (leftNumbers[i] > 0) nextLeft = leftNumbers[i] + 1;
                if (rightNumbers[i] > 0) nextRight = rightNumbers[i] + 1;
            }

            if (collapse && blocks.Count > 0)
            {
                for (int i = 0; i < count;)
                {
                    if (diff.Lines[i].Kind != UnitGitDiffLineKind.Context)
                    {
                        i++;
                        continue;
                    }
                    int start = i;
                    while (i < count && diff.Lines[i].Kind == UnitGitDiffLineKind.Context) i++;
                    bool changeBefore = start > 0 && IsChange(diff.Lines[start - 1].Kind);
                    bool changeAfter = i < count && IsChange(diff.Lines[i].Kind);
                    if (!changeBefore && !changeAfter) continue;
                    int from = changeBefore ? start + ContextLines : start;
                    int to = changeAfter ? i - ContextLines : i;
                    if (to - from >= MinimumFold) folds.Add(new Fold { Start = from, End = to });
                }
            }

            folds.Sort((a, b) => a.Start.CompareTo(b.Start));
            BuildRows();
        }

        private void BuildRows()
        {
            int count = diff.Lines.Count;
            rows.Clear();
            if (rowOfLine.Length != count) rowOfLine = new int[count];
            int fold = 0;
            for (int i = 0; i < count;)
            {
                if (fold < folds.Count && folds[fold].Start == i)
                {
                    var current = folds[fold];
                    // A hunk header with nothing missing before it (the file's start) simply disappears.
                    if (current.Missing != 0)
                        rows.Add(-(fold + 1));
                    for (int hidden = current.Start; hidden < current.End; hidden++) rowOfLine[hidden] = -1;
                    i = current.End;
                    fold++;
                    continue;
                }
                rowOfLine[i] = rows.Count;
                rows.Add(i);
                i++;
            }
            if (!ReferenceEquals(list.itemsSource, rows)) list.itemsSource = rows;
            else list.Rebuild();
            RepaintOverviews();
        }

        private void Unfold(Fold fold, int top, int bottom)
        {
            // top: lines shown at the fold's start (above), bottom: at its end (just above the change below).
            if (top < 0 || bottom < 0 || fold.Start + top >= fold.End - bottom) folds.Remove(fold);
            else
            {
                fold.Start += top;
                fold.End -= bottom;
                if (fold.End - fold.Start < 1) folds.Remove(fold);
            }
            BuildRows();
        }

        // Unfolds what hides this line (a search match): the line with a few lines around it, the rest stays folded.
        private void Reveal(int line)
        {
            foreach (var fold in folds)
            {
                if (fold.Missing >= 0 || line < fold.Start || line >= fold.End) continue;
                int from = Math.Max(fold.Start, line - ContextLines), to = Math.Min(fold.End, line + ContextLines + 1);
                var after = new Fold { Start = to, End = fold.End };
                fold.End = from;
                if (fold.End - fold.Start < 1) folds.Remove(fold);
                if (after.End - after.Start >= 1) folds.Add(after);
                folds.Sort((a, b) => a.Start.CompareTo(b.Start));
                BuildRows();
                return;
            }
        }

        // ---- Rows --------------------------------------------------------------------------------------------------

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("unitgit-diff-line");
            row.Add(MakeSide(true));
            var divider = new VisualElement();
            divider.AddToClassList("ug-dl__divider");
            var revert = new Button { tooltip = "Revert this change: put the lines on the left back in the file" };
            revert.AddToClassList("ug-dl__revert");
            revert.Add(new UnitGitIconElement(UnitGitIconKind.ApplyLeft));
            UnitGitUi.Press(revert, () =>
            {
                if (revert.userData is UnitGitDiffBlock block) RevertRequested?.Invoke(block);
            });
            divider.Add(revert);
            row.Add(divider);
            row.Add(MakeSide(false));
            row.AddManipulator(new ContextualMenuManipulator(evt => BuildRowMenu(row, evt)));
            return row;
        }

        private static VisualElement MakeSide(bool left)
        {
            var side = new VisualElement();
            side.AddToClassList("unitgit-diff-cell");
            side.AddToClassList(left ? "ug-diff-cell--left" : "ug-diff-cell--right");
            var number = new Label();
            number.AddToClassList("ug-diff-number");
            number.style.width = Gutter;
            number.style.minWidth = Gutter;
            number.style.unityFont = font;
            number.style.fontSize = 11;
            side.Add(number);
            var area = new VisualElement();
            area.AddToClassList("ug-diff-text-area");
            var text = new Label { enableRichText = false };
            text.AddToClassList("ug-diff-text");
            text.style.position = Position.Absolute;
            text.style.unityFont = font;
            text.style.fontSize = 12;
            text.style.whiteSpace = WhiteSpace.NoWrap;
            area.Add(text);
            side.Add(area);
            return side;
        }

        // The band of a folded stretch or a section title, made the first time a row needs it.
        private VisualElement Band(VisualElement row)
        {
            if (row.childCount > 3) return row[3];
            var band = new VisualElement();
            band.AddToClassList("ug-dl__band");
            var buttons = new VisualElement();
            buttons.AddToClassList("ug-dl__band-buttons");
            var up = UnitGitUi.Icon(UnitGitIconKind.FoldUp, "Show " + ExpandStep + " more lines above the change below", () =>
            {
                if (band.userData is Fold fold) Unfold(fold, 0, ExpandStep);
            }, "ug-dl__unfold");
            var down = UnitGitUi.Icon(UnitGitIconKind.FoldDown, "Show " + ExpandStep + " more lines below the change above", () =>
            {
                if (band.userData is Fold fold) Unfold(fold, ExpandStep, 0);
            }, "ug-dl__unfold");
            buttons.Add(down);
            buttons.Add(up);
            band.Add(buttons);
            var label = new Label();
            label.AddToClassList("ug-dl__band-label");
            band.Add(label);
            // The band itself (not its arrows) shows the whole stretch, on press like every other button.
            band.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || (evt.target != band && evt.target != label)) return;
                if (band.userData is Fold fold && fold.Missing < 0)
                {
                    evt.StopPropagation();
                    Unfold(fold, -1, -1);
                }
            });
            row.Add(band);
            return band;
        }

        private void BindRow(VisualElement row, int index)
        {
            int value = index < rows.Count ? rows[index] : 0;
            row.ClearClassList();
            row.AddToClassList("unitgit-diff-line");
            bool banded = value < 0 || (value < diff.Lines.Count && diff.Lines[value].Kind == UnitGitDiffLineKind.Hunk);
            for (int i = 0; i < 3; i++) row[i].style.display = banded ? DisplayStyle.None : DisplayStyle.Flex;
            if (row.childCount > 3) row[3].style.display = banded ? DisplayStyle.Flex : DisplayStyle.None;
            if (banded)
            {
                BindBand(row, value);
                return;
            }

            var line = diff.Lines[value];
            row.userData = value;
            row.AddToClassList("unitgit-diff-line--" + line.Kind.ToString().ToLowerInvariant());
            row.EnableInClassList("unitgit-diff-line--search-match", matchLines.Contains(value));
            row.EnableInClassList("unitgit-diff-line--search-current", matches.Count > 0 && matches[matchIndex] == value);
            int blockIndex = value < blockOfLine.Length ? blockOfLine[value] : -1;
            bool first = blockIndex >= 0 && blocks[blockIndex].First == value;
            bool last = blockIndex >= 0 && blocks[blockIndex].End == value + 1;
            row.EnableInClassList("ug-diff-line--block-start", first);
            row.EnableInClassList("ug-diff-line--block-end", last);
            row.EnableInClassList("ug-diff-line--current-change", blockIndex >= 0 && blockIndex == currentChange);

            var revert = (Button)row[1][0];
            bool canRevert = first && CanRevert && blocks[blockIndex].Editable;
            revert.style.display = canRevert ? DisplayStyle.Flex : DisplayStyle.None;
            revert.userData = canRevert ? blocks[blockIndex] : null;

            Vector2Int span = line.Kind == UnitGitDiffLineKind.Changed ? ChangedSpan(value, line) : new Vector2Int(-1, -1);
            string leftText = line.Left ?? string.Empty, rightText = line.Right ?? string.Empty;
            BindSide(row[0], leftText, value < leftNumbers.Count ? leftNumbers[value] : 0, span.x, span.x >= 0 ? leftText.Length - span.y : -1);
            BindSide(row[2], rightText, value < rightNumbers.Count ? rightNumbers[value] : 0, span.x, span.x >= 0 ? rightText.Length - span.y : -1);
        }

        private void BindBand(VisualElement row, int value)
        {
            var band = Band(row);
            row.userData = null;
            var buttons = band[0];
            var label = (Label)band[1];
            if (value >= 0)
            {
                // A section of the diff ("Staged changes …").
                row.AddToClassList("unitgit-diff-line--section");
                band.userData = null;
                buttons.style.display = DisplayStyle.None;
                label.text = diff.Lines[value].Left;
                return;
            }

            var fold = folds[-value - 1];
            band.userData = fold;
            row.AddToClassList("unitgit-diff-line--fold");
            int hidden = fold.Missing >= 0 ? fold.Missing : fold.End - fold.Start;
            bool expandable = fold.Missing < 0;
            row.EnableInClassList("unitgit-diff-line--fold-static", !expandable);
            buttons.style.display = expandable ? DisplayStyle.Flex : DisplayStyle.None;
            // At the file's start only "up" makes sense (more of what leads to the first change), at its end only "down".
            bool atStart = fold.Start == 0 || diff.Lines[fold.Start - 1].Kind == UnitGitDiffLineKind.Hunk;
            bool atEnd = fold.End >= diff.Lines.Count || diff.Lines[fold.End].Kind == UnitGitDiffLineKind.Hunk;
            buttons[0].style.display = atStart || hidden <= ExpandStep ? DisplayStyle.None : DisplayStyle.Flex;
            buttons[1].style.display = atEnd || hidden <= ExpandStep ? DisplayStyle.None : DisplayStyle.Flex;
            label.text = hidden + (hidden == 1 ? " unchanged line" : " unchanged lines") + (expandable ? "  ·  click to show" : " not loaded (large file)");
            label.tooltip = expandable ? "Show the " + hidden + " hidden lines" : "Big files load with 80 lines around each change.";
        }

        // Common start and end of a changed line's two versions: what lies between is highlighted.
        private Vector2Int ChangedSpan(int index, UnitGitDiffLine line)
        {
            if (changedSpans.TryGetValue(index, out var span)) return span;
            string a = line.Left ?? string.Empty, b = line.Right ?? string.Empty;
            int limit = Math.Min(a.Length, b.Length);
            int prefix = 0;
            while (prefix < limit && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < limit - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
            span = new Vector2Int(prefix, suffix);
            changedSpans[index] = span;
            return span;
        }

        private float TextAreaWidth()
        {
            float width = scroll.contentViewport.layout.width;
            if (float.IsNaN(width) || width <= 0f) width = 1000f;
            return Mathf.Max(40f, (width - DividerWidth) * 0.5f - Gutter);
        }

        private void BindSide(VisualElement side, string text, int number, int markStart, int markEnd)
        {
            var numberLabel = (Label)side[0];
            numberLabel.text = number > 0 ? number.ToString() : string.Empty;

            // Only the visible characters are given to the label: Unity cannot mesh unlimited text.
            float width = TextAreaWidth();
            int start = Mathf.Clamp(Mathf.FloorToInt(horizontalOffset / characterWidth), 0, text.Length);
            int count = Math.Min(text.Length - start, Mathf.CeilToInt(width / characterWidth) + 2);
            var label = (Label)side[1][0];
            label.style.left = TextInset + start * characterWidth - horizontalOffset;
            if (count <= 0)
            {
                label.enableRichText = false;
                label.text = string.Empty;
                return;
            }
            string visible = text.Substring(start, count);
            int from = Mathf.Clamp(markStart - start, 0, count);
            int to = Mathf.Clamp(markEnd - start, 0, count);
            bool mark = markStart >= 0 && markEnd > markStart && to > from && visible.IndexOf("noparse", StringComparison.OrdinalIgnoreCase) < 0;
            label.enableRichText = mark;
            label.text = mark
                ? "<noparse>" + visible.Substring(0, from) + "</noparse><mark=#6aa7ff50><noparse>" + visible.Substring(from, to - from) +
                  "</noparse></mark><noparse>" + visible.Substring(to) + "</noparse>"
                : visible;
        }

        private void BuildRowMenu(VisualElement row, ContextualMenuPopulateEvent evt)
        {
            if (row.userData is int index && index >= 0 && index < diff.Lines.Count)
            {
                var line = diff.Lines[index];
                evt.menu.AppendAction("Copy left line", _ => EditorGUIUtility.systemCopyBuffer = line.RawLeft ?? line.Left);
                evt.menu.AppendAction("Copy right line", _ => EditorGUIUtility.systemCopyBuffer = line.RawRight ?? line.Right);
                int block = index < blockOfLine.Length ? blockOfLine[index] : -1;
                if (block >= 0)
                {
                    var change = blocks[block];
                    evt.menu.AppendAction("Copy the change (left)", _ => EditorGUIUtility.systemCopyBuffer = string.Join("\n", change.Left),
                        change.Left.Count > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                    if (CanRevert && change.Editable)
                    {
                        evt.menu.AppendSeparator();
                        evt.menu.AppendAction("Revert this change", _ => RevertRequested?.Invoke(change));
                    }
                }
                evt.menu.AppendSeparator();
            }
            evt.menu.AppendAction(collapse ? "Show unchanged lines" : "Fold unchanged lines", _ => Collapse = !collapse);
        }

        // ---- Scrolling ---------------------------------------------------------------------------------------------

        private void UpdateHorizontalRange()
        {
            float width = TextAreaWidth();
            float content = diff.MaxLineLength * characterWidth + TextInset * 2f;
            float max = Mathf.Max(0f, content - width);
            horizontal.style.display = max > 0f ? DisplayStyle.Flex : DisplayStyle.None;
            horizontal.lowValue = 0f;
            horizontal.highValue = Mathf.Max(1f, max);
            horizontal.Adjust(Mathf.Clamp01(width / Mathf.Max(width, content)));
            if (horizontalOffset > max)
            {
                horizontalOffset = max;
                horizontal.value = max;
            }
        }

        private void SetHorizontal(float value)
        {
            float max = horizontal.style.display == DisplayStyle.None ? 0f : horizontal.highValue;
            horizontalOffset = Mathf.Clamp(value, 0f, max);
            horizontal.value = horizontalOffset;
            list.RefreshItems();
        }

        // Shift + wheel, or a sideways wheel, scrolls both sides sideways.
        private void OnWheel(WheelEvent evt)
        {
            float sideways = Mathf.Abs(evt.delta.x) > Mathf.Abs(evt.delta.y) ? evt.delta.x : evt.shiftKey ? evt.delta.y : 0f;
            if (Mathf.Approximately(sideways, 0f)) return;
            SetHorizontal(horizontalOffset + sideways * characterWidth * 3f);
            evt.StopPropagation();
        }

        internal float ScrollRow => scroll.scrollOffset.y / RowHeight;

        internal float VisibleRows
        {
            get
            {
                float height = scroll.contentViewport.layout.height;
                return float.IsNaN(height) || height <= 0f ? 1f : height / RowHeight;
            }
        }

        internal void ScrollToRow(float row)
        {
            float max = Mathf.Max(0f, rows.Count - VisibleRows);
            scroll.scrollOffset = new Vector2(scroll.scrollOffset.x, Mathf.Clamp(row, 0f, max) * RowHeight);
            RepaintOverviews();
        }

        internal int RowOfLine(int line) => line >= 0 && line < rowOfLine.Length ? rowOfLine[line] : -1;

        private void RepaintOverviews()
        {
            leftOverview?.MarkDirtyRepaint();
            rightOverview?.MarkDirtyRepaint();
        }

        // ---- Search and navigation --------------------------------------------------------------------------------------

        public void Search(string term, bool scrollToMatch)
        {
            search = term ?? string.Empty;
            matches.Clear();
            matchLines.Clear();
            if (!string.IsNullOrWhiteSpace(search))
            {
                for (int i = 0; i < diff.Lines.Count; i++)
                {
                    var line = diff.Lines[i];
                    if (line.Kind == UnitGitDiffLineKind.Hunk) continue;
                    if ((line.Left ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (line.Right ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    matches.Add(i);
                    matchLines.Add(i);
                }
            }
            matchIndex = matches.Count == 0 ? 0 : Math.Min(matchIndex, matches.Count - 1);
            list.RefreshItems();
            RepaintOverviews();
            if (scrollToMatch && matches.Count > 0) RevealMatch();
        }

        public void MoveMatch(int step)
        {
            if (matches.Count == 0) return;
            matchIndex = (matchIndex + matches.Count + step) % matches.Count;
            list.RefreshItems();
            RevealMatch();
        }

        private void RevealMatch()
        {
            int line = matches[matchIndex];
            if (RowOfLine(line) < 0) Reveal(line);
            int row = Math.Max(0, RowOfLine(line));
            ScrollToRow(row - VisibleRows * 0.35f);
            var text = diff.Lines[line];
            int column = (text.Left ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (column < 0) column = (text.Right ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase);
            float x = Mathf.Max(0, column - 8) * characterWidth;
            // Sideways only when the match is out of sight.
            if (x < horizontalOffset || x + search.Length * characterWidth > horizontalOffset + TextAreaWidth() - TextInset * 2f)
                SetHorizontal(x);
            else list.RefreshItems();
        }

        /// <summary>Jumps to the next or previous change, like F7 in JetBrains IDEs; false at either end.</summary>
        public bool MoveChange(int step)
        {
            if (blocks.Count == 0) return false;
            int next = currentChange < 0 ? (step > 0 ? 0 : blocks.Count - 1) : currentChange + step;
            if (next < 0 || next >= blocks.Count) return false;
            ShowChange(next);
            return true;
        }

        /// <summary>Makes this change the current one and scrolls it a few lines below the top.</summary>
        internal void ShowChange(int index)
        {
            if (index < 0 || index >= blocks.Count) return;
            currentChange = index;
            int row = Math.Max(0, RowOfLine(blocks[index].First));
            ScrollToRow(row - 3);
            list.RefreshItems();
        }
    }

    /// <summary>
    /// The strip beside one side of a diff: a marker where each change is in the whole file (JetBrains' error stripe) and
    /// the visible part as a thumb. Dragging the thumb scrolls; a click elsewhere jumps there.
    /// </summary>
    internal sealed class UnitGitDiffOverview : VisualElement
    {
        private static readonly Color Changed = new Color32(106, 167, 255, 230);
        private static readonly Color Added = new Color32(67, 209, 127, 230);
        private static readonly Color Removed = new Color32(255, 123, 123, 220);
        private static readonly Color Thumb = new Color(1f, 1f, 1f, 0.13f);
        private static readonly Color ThumbActive = new Color(1f, 1f, 1f, 0.24f);
        private readonly UnitGitDiffViewer owner;
        private readonly bool left;
        private bool dragging;
        private float grab;

        public UnitGitDiffOverview(UnitGitDiffViewer owner, bool left)
        {
            this.owner = owner;
            this.left = left;
            AddToClassList("ug-diff-overview");
            AddToClassList(left ? "ug-diff-overview--left" : "ug-diff-overview--right");
            tooltip = "Changes in the whole file: click to jump, drag to scroll";
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => { dragging = false; MarkDirtyRepaint(); });
            RegisterCallback<WheelEvent>(evt =>
            {
                owner.ScrollToRow(owner.ScrollRow + evt.delta.y * 3f);
                evt.StopPropagation();
            });
        }

        private float Height => Mathf.Max(1f, contentRect.height);

        // Rows the strip stands for: a short diff keeps its rows' own height, so each marker sits beside its lines.
        private float Span => Mathf.Max(1f, Mathf.Max(owner.RowCount, owner.VisibleRows));

        private void ThumbRange(out float top, out float bottom)
        {
            float rows = Span;
            float visible = Mathf.Min(rows, owner.VisibleRows);
            top = owner.ScrollRow / rows * Height;
            bottom = Mathf.Min(Height, top + Mathf.Max(18f, visible / rows * Height));
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.height < 4f || owner.RowCount == 0) return;
            var p = context.painter2D;
            float rows = Span;
            if (owner.VisibleRows < rows)
            {
                ThumbRange(out float top, out float bottom);
                p.fillColor = dragging ? ThumbActive : Thumb;
                RoundRect(p, new Rect(1.5f, top, rect.width - 3f, bottom - top), 3f);
            }
            foreach (var block in owner.Blocks)
            {
                int first = owner.RowOfLine(block.First), last = owner.RowOfLine(block.End - 1);
                if (first < 0 || last < first) continue;
                bool both = block.Left.Count > 0 && block.Right.Count > 0;
                // Each side marks what it has; a change the side does not have is a thin tick where it would be.
                bool own = left ? block.Left.Count > 0 : block.Right.Count > 0;
                p.fillColor = both ? Changed : block.Right.Count > 0 ? Added : Removed;
                float y = first / rows * rect.height;
                float h = own ? Mathf.Max(2.5f, (last - first + 1) / rows * rect.height) : 2f;
                float inset = own ? 3f : 4.5f;
                RoundRect(p, new Rect(inset, Mathf.Min(y, rect.height - h), rect.width - inset * 2f, h), 1f);
            }
        }

        private static void RoundRect(Painter2D p, Rect r, float radius)
        {
            radius = Mathf.Min(radius, r.width * 0.5f, r.height * 0.5f);
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin + radius, r.yMin));
            p.LineTo(new Vector2(r.xMax - radius, r.yMin));
            p.ArcTo(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMin + radius), radius);
            p.LineTo(new Vector2(r.xMax, r.yMax - radius));
            p.ArcTo(new Vector2(r.xMax, r.yMax), new Vector2(r.xMax - radius, r.yMax), radius);
            p.LineTo(new Vector2(r.xMin + radius, r.yMax));
            p.ArcTo(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMax - radius), radius);
            p.LineTo(new Vector2(r.xMin, r.yMin + radius));
            p.ArcTo(new Vector2(r.xMin, r.yMin), new Vector2(r.xMin + radius, r.yMin), radius);
            p.ClosePath();
            p.Fill();
        }

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || owner.RowCount == 0) return;
            ThumbRange(out float top, out float bottom);
            float y = evt.localPosition.y;
            if (y < top || y > bottom)
            {
                // Elsewhere: the clicked place comes to the middle, and the thumb follows the pointer from there.
                owner.ScrollToRow(y / Height * Span - owner.VisibleRows * 0.5f);
                ThumbRange(out top, out bottom);
            }
            grab = y - top;
            dragging = true;
            this.CapturePointer(evt.pointerId);
            MarkDirtyRepaint();
            evt.StopPropagation();
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (!dragging || !this.HasPointerCapture(evt.pointerId)) return;
            owner.ScrollToRow((evt.localPosition.y - grab) / Height * Span);
            evt.StopPropagation();
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (!dragging) return;
            dragging = false;
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            MarkDirtyRepaint();
            evt.StopPropagation();
        }
    }
}
