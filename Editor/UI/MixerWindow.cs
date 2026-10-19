using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The Mixer window: a top bar with the modes and the Blender link, a hero for the mesh being edited, the active
    /// mode's page (brushes, layers, settings), the edited meshes and the footer with Commit and Revert. Built in
    /// UI Toolkit from mixer.uss; partial files hold the pages.
    /// </summary>
    internal sealed partial class MixerWindow : EditorWindow, IHasCustomMenu
    {
        private const string StyleSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        private const string ModePref = "Orbiters.Mixer.Window.Mode";
        private static readonly string[] ModeNames = { "Sculpt", "Weights", "Armature", "Join", "Render" };
        private static readonly IconGlyph[] ModeIcons = { IconGlyph.Sculpt, IconGlyph.Weights, IconGlyph.Armature, IconGlyph.Join, IconGlyph.Render };

        [SerializeField] private int modeIndex;

        private VisualElement root, pages, targetsBox, heroBox, toolbarCenter;
        private SegmentedControl modes;
        private Button linkPill, undoButton, redoButton, commitButton, revertButton;
        private Label footerText, footerRight;
        private MixerToasts toasts;
        private OrbitersGlowSurfaceElement glow;
        private readonly VisualElement[] pageRoots = new VisualElement[ModeNames.Length];
        private bool built;
        private bool chromeDirty = true;
        private int seenRevision = -1;
        private bool visible = true;

        [MenuItem(MixerInfo.MenuPath + " %&m", priority = 2000)]
        public static void Open()
        {
            var window = GetWindow<MixerWindow>();
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(MixerInfo.DisplayName, MixerLogo.TabIcon(), "Orbiters Mixer");
            minSize = new Vector2(560f, 360f);
            MixerSession.Changed += MarkChrome;
            MixerSession.Notice += OnNotice;
            MixerSceneTool.Changed += MarkChrome;
            MixerHistory.Changed += MarkChrome;
            Selection.selectionChanged += MarkChrome;
            Undo.undoRedoPerformed += MarkChrome;
        }

        private void OnDisable()
        {
            MixerSession.Changed -= MarkChrome;
            MixerSession.Notice -= OnNotice;
            MixerSceneTool.Changed -= MarkChrome;
            MixerHistory.Changed -= MarkChrome;
            Selection.selectionChanged -= MarkChrome;
            Undo.undoRedoPerformed -= MarkChrome;
        }

        private void OnBecameVisible() { visible = true; MarkChrome(); }
        private void OnBecameInvisible() => visible = false;

        private void MarkChrome() => chromeDirty = true;

        private void OnNotice(string text, bool error) => toasts?.Show(text, error);

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open Mixer Hierarchy"), false, MixerHierarchyWindow.Open);
            menu.AddItem(new GUIContent("Settings…"), false, OrbitersSettingsWindow.Open);
            menu.AddItem(new GUIContent("About Mixer…"), false, MixerAboutWindow.Open);
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null) root.styleSheets.Add(sheet);
            root.focusable = true;

            BuildTopBar();
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("mx-body");
            root.Add(scroll);
            heroBox = MixerUi.Box("mx-hero-box");
            scroll.Add(heroBox);
            pages = MixerUi.Box("mx-pages");
            scroll.Add(pages);
            for (int i = 0; i < ModeNames.Length; i++)
            {
                var page = MixerUi.Box("mx-page");
                pageRoots[i] = page;
                pages.Add(page);
            }
            BuildSculptPage(pageRoots[0]);
            BuildWeightsPage(pageRoots[1]);
            BuildArmaturePage(pageRoots[2]);
            BuildJoinPage(pageRoots[3]);
            BuildRenderPage(pageRoots[4]);
            targetsBox = MixerUi.Box("mx-targets");
            scroll.Add(targetsBox);
            BuildFooter();
            toasts = new MixerToasts();
            root.Add(toasts);
            root.RegisterCallback<GeometryChangedEvent>(_ => ApplyWidthClasses());
            root.schedule.Execute(Tick).Every(60);
            built = true;
            // A window follows the tool's mode when one is on (another window may have set it); otherwise its own last page.
            int initial = MixerSceneTool.Mode == MixerMode.Weights ? 1 : MixerSceneTool.Mode == MixerMode.Armature ? 2 : MixerSceneTool.Mode == MixerMode.Sculpt ? 0 : Mathf.Clamp(modeIndex, 0, ModeNames.Length - 1);
            SelectMode(initial, animate: false, applyToTool: false);
            chromeDirty = true;
        }

        private void BuildTopBar()
        {
            var bar = MixerUi.Box("mx-topbar");
            root.Add(bar);
            var brand = MixerUi.Box("mx-brand");
            var mark = MixerUi.Box("mx-brand__mark");
            var logo = new MixerLogo.Element();
            logo.AddToClassList("mx-brand__logo");
            mark.Add(logo);
            brand.Add(mark);
            brand.Add(MixerUi.Text(MixerInfo.DisplayName, "mx-brand__name"));
            bar.Add(brand);

            var options = new List<SegmentedControl.Option>();
            for (int i = 0; i < ModeNames.Length; i++) options.Add(new SegmentedControl.Option(ModeNames[i], ModeIcons[i]));
            modes = new SegmentedControl(options, index => SelectMode(index, animate: true, applyToTool: true));
            modes.AddToClassList("mx-modes");
            toolbarCenter = MixerUi.Box("mx-topbar__center");
            toolbarCenter.Add(modes);
            bar.Add(toolbarCenter);
            bar.Add(MixerUi.Spacer());

            linkPill = MixerUi.Pill("Not linked", ShowLinkPopup, "ghost", IconGlyph.Blender, "Blender link");
            linkPill.AddToClassList("mx-link-pill");
            bar.Add(linkPill);
            undoButton = MixerUi.Icon(IconGlyph.Undo, "Undo (Ctrl+Z)", MixerHistory.PerformUndo);
            redoButton = MixerUi.Icon(IconGlyph.Redo, "Redo (Ctrl+Y)", MixerHistory.PerformRedo);
            bar.Add(undoButton);
            bar.Add(redoButton);
            bar.Add(MixerUi.Icon(IconGlyph.Hierarchy, "Mixer Hierarchy", MixerHierarchyWindow.Open));
            bar.Add(MixerUi.Icon(IconGlyph.Settings, "Settings", ShowSettingsPopup));
        }

        private void BuildFooter()
        {
            var footer = MixerUi.Box("mx-footer");
            root.Add(footer);
            footerText = MixerUi.Text("", "mx-footer__text");
            footer.Add(footerText);
            footer.Add(MixerUi.Spacer());
            footerRight = MixerUi.Text("", "mx-footer__text mx-footer__text--right");
            footer.Add(footerRight);
            revertButton = MixerUi.Pill("Revert", RevertAll, "ghost", null, "Put the original meshes back and drop every edit");
            revertButton.AddToClassList("mx-footer__button");
            commitButton = MixerUi.Pill("Commit", CommitAll, "primary", IconGlyph.Check, "Save the edited meshes as assets and show them on the renderers");
            commitButton.AddToClassList("mx-footer__button");
            footer.Add(revertButton);
            footer.Add(commitButton);
        }

        private void SelectMode(int index, bool animate, bool applyToTool)
        {
            int previous = modeIndex;
            modeIndex = index;
            EditorPrefs.SetInt(ModePref, index);
            modes.SetIndex(index);
            for (int i = 0; i < pageRoots.Length; i++)
            {
                var page = pageRoots[i];
                bool on = i == index;
                if (on)
                {
                    MixerUi.Show(page, true);
                    if (animate && previous != index) MixerUi.Enter(page, previous < index ? "mx-page--from-right" : "mx-page--from-left", 20);
                }
                else MixerUi.Show(page, false);
            }
            if (applyToTool) MixerSceneTool.Mode = index == 0 ? MixerMode.Sculpt : index == 1 ? MixerMode.Weights : index == 2 ? MixerMode.Armature : MixerMode.None;
            chromeDirty = true;
        }

        private void ApplyWidthClasses()
        {
            float width = root.resolvedStyle.width;
            root.EnableInClassList("mx-root--m", width < 1180f);
            root.EnableInClassList("mx-root--s", width < 980f);
            root.EnableInClassList("mx-root--xs", width < 760f);
            root.EnableInClassList("mx-root--short", root.resolvedStyle.height < 520f);
        }

        private void Tick()
        {
            if (!built || !visible) return;
            int revision = 0;
            foreach (var t in MixerSession.Targets) revision = unchecked(revision * 31 + t.Document.Revision);
            if (revision != seenRevision) { seenRevision = revision; chromeDirty = true; }
            if (!chromeDirty) return;
            chromeDirty = false;
            RefreshChrome();
        }

        private void RefreshChrome()
        {
            // The tool's mode changed elsewhere (the Scene toolbar, another window): show its page.
            int toolPage = MixerSceneTool.Mode == MixerMode.Sculpt ? 0 : MixerSceneTool.Mode == MixerMode.Weights ? 1 : MixerSceneTool.Mode == MixerMode.Armature ? 2 : -1;
            if (toolPage >= 0 && toolPage != modeIndex) SelectMode(toolPage, animate: true, applyToTool: false);
            var target = CurrentTarget();
            RefreshHero(target);
            RefreshTargets();
            RefreshSculptPage(target);
            RefreshWeightsPage(target);
            RefreshArmaturePage(target);
            RefreshJoinPage();
            RefreshRenderPage();
            undoButton.SetEnabled(MixerHistory.CanUndo);
            undoButton.tooltip = MixerHistory.CanUndo ? "Undo " + MixerHistory.UndoLabel + " (Ctrl+Z)" : "Nothing to undo";
            redoButton.SetEnabled(MixerHistory.CanRedo);
            redoButton.tooltip = MixerHistory.CanRedo ? "Redo " + MixerHistory.RedoLabel + " (Ctrl+Y)" : "Nothing to redo";
            bool any = MixerSession.Active;
            bool changed = MixerSession.Targets.Any(t => t.Document.Changed);
            commitButton.SetEnabled(changed);
            revertButton.SetEnabled(any);
            commitButton.tooltip = changed ? "Save the edited meshes as assets and show them on the renderers" : "Nothing changed yet";
            int count = MixerSession.Targets.Count;
            footerText.text = !any ? "Select a mesh and press Edit to start." : count == 1 ? "Editing " + MixerSession.Targets[0].Name + (changed ? " · unsaved edits" : "") : "Editing " + count + " meshes" + (changed ? " · unsaved edits" : "");
            footerRight.text = MixerHistory.Count > 0 ? MixerHistory.Count + " steps · " + MixerUi.Bytes(MixerHistory.Bytes) : "";
            RefreshLinkPill();
        }

        /// <summary>The target the pages show: the hovered one, else the selected renderer's, else the first.</summary>
        internal static EditTarget CurrentTarget()
        {
            if (!MixerSession.Active) return null;
            var hovered = MixerSceneTool.HoverTarget;
            if (hovered != null && hovered.Alive && MixerSession.Targets.Contains(hovered)) return hovered;
            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                foreach (var t in MixerSession.Targets)
                    if (t.Alive && (t.Renderer.gameObject == selected || t.Renderer.transform.IsChildOf(selected.transform))) return t;
            }
            return MixerSession.First;
        }

        /// <summary>Renderers of the selection that are not edited yet, with why they cannot be when they cannot.</summary>
        internal static List<(Renderer renderer, string reason)> EditableSelection()
        {
            var result = new List<(Renderer, string)>();
            foreach (var go in Selection.gameObjects)
            {
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    if (MixerSession.TargetOf(renderer) != null) continue;
                    if (renderer.name.StartsWith(MixerInfo.HelperPrefix, StringComparison.Ordinal) || renderer.name.StartsWith("__XRayGizmos_", StringComparison.Ordinal)) continue;
                    MixerSession.CanEdit(renderer, out string reason);
                    if (!result.Any(r => r.Item1 == renderer)) result.Add((renderer, reason));
                }
            }
            return result;
        }

        private void CommitAll()
        {
            foreach (var t in MixerSession.Targets.ToList())
            {
                try { MixerSession.Commit(t); }
                catch (Exception e) { Debug.LogException(e); toasts.Show("Commit of " + t.Name + " failed: " + e.Message, true); }
            }
        }

        private void RevertAll()
        {
            foreach (var t in MixerSession.Targets.ToList()) MixerSession.Revert(t);
            toasts.Show("Original meshes are back.");
        }

        internal void Edit(Renderer renderer)
        {
            try
            {
                var target = MixerSession.Edit(renderer);
                if (MixerSceneTool.Mode == MixerMode.None) SelectMode(modeIndex <= 2 ? modeIndex : 0, true, applyToTool: true);
                toasts.Show("Editing " + target.Name + ": sculpt it in the Scene view.");
                WakeGlow();
            }
            catch (Exception e)
            {
                toasts.Show(e.Message, true);
            }
        }

        internal void WakeGlow() => glow?.WakeForSeconds(2.5f);

        private void ShowLinkPopup() => UnityEditor.PopupWindow.Show(linkPill.worldBound, new MixerLinkPopup());
        private void ShowSettingsPopup() => OrbitersSettingsWindow.Open();
    }
}
