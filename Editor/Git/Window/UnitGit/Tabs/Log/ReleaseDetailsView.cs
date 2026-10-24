using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The selected release. A VRChat avatar upload shows the avatar as VRChat's menu shows it (Orbiters Toolkit's card on
    // its stage, lit by its thumbnail), a link to its page and copyable IDs, then what was uploaded, how and when. Other
    // releases (MCB versions…) show their name, version, thumbnail and fields.
    internal sealed partial class UnitGitWindow
    {
        // The release thumbnail, kept while the same file is shown: details are rebuilt on every refresh.
        private string releaseThumbnailPath = string.Empty;
        private DateTime releaseThumbnailWrite;
        private Texture2D releaseThumbnail;
        // The entrance plays when another release is selected, not on each refresh of the same one.
        private string releaseShownId = string.Empty;

        private VisualElement BuildReleaseDetailsPanel(UnitGitReleaseEntry release)
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");
            panel.AddToClassList("unitgit-release-panel");

            string headerDetail = string.IsNullOrWhiteSpace(release.tool)
                ? "Release checkpoint"
                : "published with " + release.tool;
            panel.Add(BuildSectionHeader("Release", headerDetail));

            var scroll = new ScrollView();
            scroll.name = "unitgit-release-details-scroll";
            scroll.AddToClassList("unitgit-files-scroll");

            UnitGitAvatarUpload upload = UnitGitAvatarUpload.From(release);
            scroll.Add(upload != null ? BuildAvatarUploadCard(release, upload) : BuildGenericReleaseCard(release));

            bool defaultChangelog = upload != null && string.Equals(release.changelog?.Trim(), UnitGitAvatarUpload.DefaultChangelog, StringComparison.Ordinal);
            if (!defaultChangelog && (upload == null || !string.IsNullOrWhiteSpace(release.changelog)))
            {
                var changelogHeader = new Label("Changelog");
                changelogHeader.AddToClassList("unitgit-release-changelog-header");
                scroll.Add(changelogHeader);

                var changelog = new Label(string.IsNullOrWhiteSpace(release.changelog)
                    ? "No changelog was provided for this release."
                    : release.changelog);
                changelog.AddToClassList("unitgit-release-changelog");
                scroll.Add(changelog);
            }

            panel.Add(scroll);
            return panel;
        }

        // ---- VRChat avatar upload ------------------------------------------------------------------------------------

        private VisualElement BuildAvatarUploadCard(UnitGitReleaseEntry release, UnitGitAvatarUpload upload)
        {
            var root = new VisualElement();
            root.AddToClassList("ug-upload");
            string url = upload.PageUrl;
            UnitGitTime.TryParseUtc(release.date, out DateTime releasedUtc);

            // Headline: what happened and when.
            var headline = new VisualElement();
            headline.AddToClassList("ug-upload__headline");
            var dot = new VisualElement { pickingMode = PickingMode.Ignore };
            dot.AddToClassList("ug-upload__dot");
            headline.Add(dot);
            headline.Add(UnitGitUi.Text("Uploaded to VRChat", "ug-upload__headline-text"));
            headline.Add(UnitGitUi.Spacer());
            if (releasedUtc != default)
            {
                var when = UnitGitUi.Chip(UnitGitTime.Relative(releasedUtc), null, UnitGitTime.LocalDate(release.date));
                when.AddToClassList("ug-upload__when");
                headline.Add(when);
            }
            root.Add(headline);

            // The avatar as VRChat's menu shows it.
            var card = new VrcAvatarCard();
            card.AddToClassList("ug-upload__card");
            card.EmptyText = "No thumbnail was saved with this upload";
            Texture2D thumbnail = LoadReleaseThumbnail(release);
            card.Show(ReleaseDisplayName(release), thumbnail);
            card.SetAuthor(ReleaseAuthor(release));
            upload.PlatformsSoFar(snapshot != null ? snapshot.Releases : null, out bool pc, out bool android);
            card.SetPlatforms(pc, android);
            var stage = new VrcCardStage(card);
            stage.AddToClassList("ug-upload__stage");
            stage.SetAmbient(thumbnail);
            if (url != null)
            {
                card.AddToClassList("ug-upload__card--link");
                card.tooltip = "Open on VRChat\n" + url;
                PressElement(card, () => Application.OpenURL(url));
            }
            root.Add(stage);

            // Actions: the avatar's page and its ID.
            var actions = new VisualElement();
            actions.AddToClassList("ug-upload__actions");
            var open = UnitGitUi.Pill("Open on VRChat", () => Application.OpenURL(url), "primary", UnitGitIconKind.OpenExternal,
                url != null ? "Open this avatar's page on vrchat.com\n" + url : "This upload has no avatar ID.");
            open.AddToClassList("ug-upload__open");
            open.SetEnabled(url != null);
            actions.Add(open);
            if (!string.IsNullOrEmpty(upload.AvatarId))
            {
                var copy = UnitGitUi.Pill("Copy ID", () => CopyWithToast("copy avatar ID", upload.AvatarId), null, UnitGitIconKind.Copy,
                    "Copy the avatar ID\n" + upload.AvatarId);
                copy.AddToClassList("ug-upload__copy");
                actions.Add(copy);
            }
            root.Add(actions);

            // At a glance.
            var tiles = new VisualElement();
            tiles.AddToClassList("ug-upload__tiles");
            tiles.Add(Tile("Uploaded", releasedUtc != default ? releasedUtc.ToLocalTime().ToString("MMM d") : "—",
                releasedUtc != default ? releasedUtc.ToLocalTime().ToString("hh:mm tt") : string.Empty, UnitGitTime.LocalDate(release.date)));
            TimeSpan? duration = upload.Duration;
            tiles.Add(Tile("Took", duration.HasValue ? UnitGitTime.Duration(duration.Value) : "—", "SDK upload",
                duration.HasValue ? "From the SDK starting the upload to VRChat accepting it." : "The SDK did not report both times."));
            string platform = string.IsNullOrWhiteSpace(upload.Platform) ? "—" : upload.Platform;
            tiles.Add(Tile("Platform", platform, upload.IsPc ? "Windows" : upload.IsAndroid ? "Android" : upload.BuildTarget, upload.BuildTarget));
            root.Add(tiles);

            // Details.
            var avatar = Group(root, "Avatar");
            avatar.Add(IdRow("Avatar ID", upload.AvatarId));
            if (!string.IsNullOrEmpty(upload.BlueprintId) && !string.Equals(upload.BlueprintId, upload.AvatarId, StringComparison.OrdinalIgnoreCase))
                avatar.Add(IdRow("Blueprint ID", upload.BlueprintId));
            avatar.Add(AssetRow("Scene", upload.Scene));
            avatar.Add(AssetRow("Avatar", upload.AvatarPath));

            var upTimes = Group(root, "Upload");
            if (upload.StartedUtc.HasValue)
                upTimes.Add(KeyValue("Started", upload.StartedUtc.Value.ToLocalTime().ToString(UnitGitTime.DateTimeFormat)));
            if (upload.SucceededUtc.HasValue)
            {
                DateTime end = upload.SucceededUtc.Value.ToLocalTime();
                bool sameDay = upload.StartedUtc.HasValue && upload.StartedUtc.Value.ToLocalTime().Date == end.Date;
                upTimes.Add(KeyValue("Finished", end.ToString(sameDay ? UnitGitTime.TimeFormat : UnitGitTime.DateTimeFormat)));
            }
            if (duration.HasValue) upTimes.Add(KeyValue("Took", UnitGitTime.Duration(duration.Value)));
            if (!string.IsNullOrEmpty(upload.Bundle))
                upTimes.Add(KeyValue("Bundle", Path.GetFileName(upload.Bundle), upload.Bundle, true));

            var build = Group(root, "Build");
            build.Add(KeyValue("Build target", upload.BuildTarget));
            build.Add(KeyValue("Unity", upload.Unity));
            build.Add(KeyValue("Avatars SDK", upload.AvatarsSdk, "VRChat Avatars SDK " + upload.AvatarsSdk));
            build.Add(KeyValue("Base SDK", upload.BaseSdk, "VRChat Base SDK " + upload.BaseSdk));

            var more = release.fields?.Where(f => f != null && !string.IsNullOrWhiteSpace(f.key) && !string.IsNullOrWhiteSpace(f.value) &&
                                                  !UnitGitAvatarUpload.KnownKeys.Contains(f.key.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            if (more != null && more.Count > 0)
            {
                var extra = Group(root, "More");
                foreach (UnitGitReleaseField field in more) extra.Add(KeyValue(field.key, field.value));
            }

            var commit = Group(root, "Commit");
            commit.Add(KeyValue("Author", ReleaseAuthor(release)));
            if (selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.ShortHash))
                commit.Add(IdRow("Commit", selectedCommit.ShortHash, selectedCommit.FullHash));

            // Rows without a value go.
            foreach (var group in root.Query(className: "ug-upload__group").ToList())
            {
                foreach (var row in group.Children().Where(r => r.userData as string == "empty").ToList()) row.RemoveFromHierarchy();
                if (group.childCount == 0) { group.RemoveFromHierarchy(); }
            }
            foreach (var title in root.Query(className: "ug-upload__group-title").ToList())
            {
                int index = title.parent.IndexOf(title);
                if (index + 1 >= title.parent.childCount || !title.parent[index + 1].ClassListContains("ug-upload__group"))
                    title.RemoveFromHierarchy();
            }

            if (!string.Equals(releaseShownId, release.id, StringComparison.Ordinal))
            {
                releaseShownId = release.id ?? string.Empty;
                UnitGitUi.Enter(root, "ug-upload--enter", 30);
            }
            return root;
        }

        private static VisualElement Tile(string label, string value, string detail, string tooltip)
        {
            var tile = new VisualElement { tooltip = tooltip };
            tile.AddToClassList("ug-upload__tile");
            tile.Add(UnitGitUi.Text(label, "ug-upload__tile-label"));
            tile.Add(UnitGitUi.Text(value, "ug-upload__tile-value"));
            if (!string.IsNullOrWhiteSpace(detail)) tile.Add(UnitGitUi.Text(detail, "ug-upload__tile-detail"));
            return tile;
        }

        // A titled flat group of key/value rows; under 240 points the values go under their keys.
        private static VisualElement Group(VisualElement parent, string title)
        {
            parent.Add(UnitGitUi.Text(title, "ug-upload__group-title"));
            var group = new VisualElement();
            group.AddToClassList("ug-upload__group");
            group.RegisterCallback<GeometryChangedEvent>(evt => group.EnableInClassList("ug-kv-list--narrow", evt.newRect.width < 240f));
            parent.Add(group);
            return group;
        }

        // A key above or beside its value; the value wraps (or, for IDs and file names, ends in an ellipsis).
        private static VisualElement KeyValue(string key, string value, string tooltip = null, bool ellipsis = false)
        {
            var row = new VisualElement();
            row.AddToClassList("ug-kv");
            if (string.IsNullOrWhiteSpace(value)) { row.userData = "empty"; return row; }
            row.Add(UnitGitUi.Text(key, "ug-kv__key"));
            var text = UnitGitUi.Text(value.Trim(), "ug-kv__value");
            if (ellipsis) text.AddToClassList("ug-kv__value--ellipsis");
            text.tooltip = tooltip ?? (ellipsis ? value : null);
            row.Add(text);
            return row;
        }

        private VisualElement IdRow(string key, string id, string copied = null)
        {
            var row = KeyValue(key, id, copied ?? id, true);
            if (row.userData as string == "empty") return row;
            row.AddToClassList("ug-kv--id");
            string value = copied ?? id;
            row.Add(UnitGitUi.Icon(UnitGitIconKind.Copy, "Copy " + key.ToLowerInvariant().Replace(" id", " ID") + "\n" + value,
                () => CopyWithToast("copy " + key.ToLowerInvariant(), value), "ug-icon-button--small"));
            return row;
        }

        // A project path: a link that shows the asset in the Project window while it still exists.
        private static VisualElement AssetRow(string key, string path)
        {
            var row = KeyValue(key, path);
            if (row.userData as string == "empty" || !path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
                return row;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return row;
            var value = row.Q<Label>(className: "ug-kv__value");
            value.AddToClassList("ug-kv__value--link");
            value.tooltip = "Show in the Project window\n" + path;
            value.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                EditorGUIUtility.PingObject(asset);
                evt.StopPropagation();
            });
            return row;
        }

        private void CopyWithToast(string label, string text)
        {
            CopyText(label, text);
            ShowToast("Copied " + (text.Length > 24 ? text.Substring(0, 23) + "…" : text));
        }

        // Pressed at once (scale), acting on release inside, as Mixer Git's buttons do.
        private static void PressElement(VisualElement element, Action action)
        {
            bool down = false;
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                down = true;
                element.AddToClassList(UnitGitUi.PressedClass);
            });
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                element.RemoveFromClassList(UnitGitUi.PressedClass);
                if (!down || evt.button != 0) return;
                down = false;
                if (element.ContainsPoint(element.WorldToLocal(evt.position))) action();
            });
            element.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                down = false;
                element.RemoveFromClassList(UnitGitUi.PressedClass);
            });
        }

        // ---- Other releases ------------------------------------------------------------------------------------------

        private VisualElement BuildGenericReleaseCard(UnitGitReleaseEntry release)
        {
            var card = new VisualElement();
            card.AddToClassList("unitgit-release-card");
            Color branchColor = selectedCommit != null ? GetGraphColor(selectedCommit) : new Color(0f, 0.85f, 0.43f);

            var headline = new VisualElement();
            headline.AddToClassList("unitgit-release-headline");
            var name = new Label(ReleaseDisplayName(release));
            name.AddToClassList("unitgit-release-details-name");
            headline.Add(name);

            if (!string.IsNullOrWhiteSpace(release.version))
            {
                var pill = new Label(release.version);
                pill.AddToClassList("unitgit-release-pill");
                pill.style.color = branchColor;
                pill.style.backgroundColor = new Color(branchColor.r, branchColor.g, branchColor.b, 0.14f);
                headline.Add(pill);
            }

            if (!string.IsNullOrWhiteSpace(release.scope))
            {
                var scope = new Label(release.scope);
                scope.AddToClassList("unitgit-release-scope");
                headline.Add(scope);
            }

            card.Add(headline);

            if (!string.IsNullOrWhiteSpace(release.title))
            {
                var title = new Label(release.title);
                title.AddToClassList("unitgit-release-details-title");
                card.Add(title);
            }

            Texture2D thumbnail = LoadReleaseThumbnail(release);
            if (thumbnail != null)
            {
                var frame = new VisualElement();
                frame.AddToClassList("unitgit-release-thumbnail-frame");
                var image = new Image { image = thumbnail, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("unitgit-release-thumbnail");
                frame.Add(image);
                card.Add(frame);
            }

            var rows = new VisualElement();
            rows.AddToClassList("unitgit-release-meta");
            rows.RegisterCallback<GeometryChangedEvent>(evt => rows.EnableInClassList("ug-kv-list--narrow", evt.newRect.width < 240f));
            rows.Add(KeyValue("Released", UnitGitTime.LocalDate(release.date)));
            rows.Add(KeyValue("Author", ReleaseAuthor(release)));
            rows.Add(KeyValue("Type", release.type));
            if (release.fields != null)
            {
                foreach (UnitGitReleaseField field in release.fields)
                {
                    if (field != null && !string.IsNullOrWhiteSpace(field.key))
                        rows.Add(KeyValue(field.key, field.value));
                }
            }
            if (selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.ShortHash))
                rows.Add(IdRow("Commit", selectedCommit.ShortHash, selectedCommit.FullHash));
            foreach (var row in rows.Children().Where(r => r.userData as string == "empty").ToList()) row.RemoveFromHierarchy();
            card.Add(rows);
            return card;
        }

        // ---- Shared --------------------------------------------------------------------------------------------------

        private string ReleaseDisplayName(UnitGitReleaseEntry release) =>
            !string.IsNullOrWhiteSpace(release.name) ? release.name.Trim() : selectedCommit != null ? selectedCommit.Subject : "Release";

        private string ReleaseAuthor(UnitGitReleaseEntry release) =>
            !string.IsNullOrWhiteSpace(release.author) ? release.author.Trim()
            : selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.AuthorName) ? selectedCommit.AuthorName : string.Empty;

        // The release's thumbnail, read once per file version.
        private Texture2D LoadReleaseThumbnail(UnitGitReleaseEntry release)
        {
            string fullPath = ResolveReleaseProjectPath(release != null ? release.thumbnailPath : string.Empty);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;

            try
            {
                DateTime write = File.GetLastWriteTimeUtc(fullPath);
                if (releaseThumbnail != null && string.Equals(releaseThumbnailPath, fullPath, StringComparison.OrdinalIgnoreCase) && releaseThumbnailWrite == write)
                    return releaseThumbnail;

                ReleaseThumbnailCacheClear();
                var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                if (!texture.LoadImage(File.ReadAllBytes(fullPath)))
                {
                    DestroyImmediate(texture);
                    return null;
                }

                texture.name = Path.GetFileName(fullPath);
                releaseThumbnail = texture;
                releaseThumbnailPath = fullPath;
                releaseThumbnailWrite = write;
                return texture;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void ReleaseThumbnailCacheClear()
        {
            if (releaseThumbnail != null) DestroyImmediate(releaseThumbnail);
            releaseThumbnail = null;
            releaseThumbnailPath = string.Empty;
        }

        private string ResolveReleaseProjectPath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath) || Path.IsPathRooted(projectRelativePath))
            {
                return string.Empty;
            }

            string projectRoot = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ProjectRoot)
                ? snapshot.ProjectRoot
                : (gitService != null ? gitService.ProjectRoot : string.Empty);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return string.Empty;
            }

            string rootFullPath = Path.GetFullPath(projectRoot);
            string candidatePath = Path.GetFullPath(Path.Combine(
                rootFullPath,
                projectRelativePath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsPathInsideRoot(rootFullPath, candidatePath))
            {
                return string.Empty;
            }

            return candidatePath;
        }

        private static bool IsPathInsideRoot(string rootFullPath, string candidatePath)
        {
            string root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            string candidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private UnitGitReleaseEntry GetReleaseForCommit(UnitGitCommit commit)
        {
            if (commit == null || !commit.HasRelease)
            {
                return null;
            }

            UnitGitReleaseFile releases = snapshot != null ? snapshot.Releases : null;
            if (UnitGitReleases.IsHidden(releases, commit.ReleaseId))
            {
                return null;
            }

            UnitGitReleaseEntry release = UnitGitReleases.FindById(releases, commit.ReleaseId);
            if (release != null)
            {
                return release;
            }

            // The releases file no longer contains this entry; fall back to commit data so the
            // checkpoint stays visible in the history.
            return new UnitGitReleaseEntry
            {
                id = commit.ReleaseId.Trim(),
                name = commit.Subject,
                author = commit.AuthorName
            };
        }
    }
}
