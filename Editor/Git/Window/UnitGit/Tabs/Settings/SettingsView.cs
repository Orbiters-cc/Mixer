using System;
using Orbiters.Toolkit.Editor;
using Orbiters.UnitGit;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // Mixer Git's options as cards of switches, in My Avatar's style: each switch flips on press and saves at once.
    internal sealed partial class UnitGitWindow
    {
        private VisualElement BuildSettingsBody()
        {
            var root = new ScrollView();
            root.AddToClassList("unitgit-settings-root");

            var header = new VisualElement();
            header.AddToClassList("ug-settings-header");
            header.Add(UnitGitUi.Text("Settings", "ug-settings-header__title"));
            header.Add(UnitGitUi.Text("Saved for this project.", "ug-settings-header__text"));
            root.Add(header);

            var repository = BuildSettingsCard("Repository", UnitGitIconKind.Remote);
            repository.Add(BuildSettingsRow("Remote", snapshot.Remotes.Count > 0 ? string.Join(", ", snapshot.Remotes) : "Not connected: Push needs one.",
                UnitGitUi.Pill(snapshot.Remotes.Count > 0 ? "GitHub / GitLab…" : "Connect…", OpenRemoteSetup, snapshot.Remotes.Count > 0 ? null : "primary", UnitGitIconKind.Remote)));
            repository.Add(BuildSettingsRow("Project folder", snapshot.ProjectRoot, null));
            root.Add(repository);

#if UNITGIT_VRCHAT_AVATARS
            var uploads = BuildSettingsCard("VRChat avatar uploads", UnitGitIconKind.Tag);
            ToggleSwitch releaseSwitch = null;
            Label releaseDetail = null;
            uploads.Add(BuildSettingsRow("Commit after each upload",
                "Every avatar upload records the project as it was uploaded.",
                new ToggleSwitch(UnitGitSettings.AvatarUploadCommitEnabled, value =>
                {
                    UnitGitSettings.AvatarUploadCommitEnabled = value;
                    releaseSwitch?.SetEnabled(value);
                    if (releaseDetail != null) releaseDetail.text = ReleaseRowDetail();
                })));
            releaseSwitch = new ToggleSwitch(UnitGitSettings.AvatarUploadReleaseRowEnabled, value => UnitGitSettings.AvatarUploadReleaseRowEnabled = value);
            releaseSwitch.SetEnabled(UnitGitSettings.AvatarUploadCommitEnabled);
            var releaseRow = BuildSettingsRow("Show uploads in the log", ReleaseRowDetail(), releaseSwitch);
            releaseDetail = releaseRow.Q<Label>(className: "ug-settings-row__detail");
            uploads.Add(releaseRow);
            root.Add(uploads);
#endif
            return root;
        }

#if UNITGIT_VRCHAT_AVATARS
        private static string ReleaseRowDetail() => UnitGitSettings.AvatarUploadCommitEnabled
            ? "A row names the upload (avatar, version) above its commit."
            : "Needs “Commit after each upload”.";
#endif

        private static VisualElement BuildSettingsCard(string title, UnitGitIconKind icon)
        {
            var card = new VisualElement();
            card.AddToClassList("ug-settings-card");
            var head = new VisualElement();
            head.AddToClassList("ug-settings-card__head");
            var glyph = new UnitGitIconElement(icon);
            head.Add(glyph);
            head.Add(UnitGitUi.Text(title, "ug-settings-card__title"));
            card.Add(head);
            return card;
        }

        private static VisualElement BuildSettingsRow(string title, string detail, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("ug-settings-row");
            var texts = new VisualElement();
            texts.AddToClassList("ug-settings-row__texts");
            texts.Add(UnitGitUi.Text(title, "ug-settings-row__title"));
            if (!string.IsNullOrEmpty(detail)) texts.Add(UnitGitUi.Text(detail, "ug-settings-row__detail"));
            row.Add(texts);
            if (control != null) row.Add(control);
            return row;
        }
    }
}
