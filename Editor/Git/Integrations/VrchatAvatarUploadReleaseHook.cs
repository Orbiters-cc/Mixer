using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;

namespace Orbiters.UnitGit.Editor
{
    [InitializeOnLoad]
    internal static class VrchatAvatarUploadReleaseHook
    {
        private const string ToolName = UnitGitAvatarUpload.Tool;
        private const string ReleaseType = UnitGitAvatarUpload.Type;
        private const string ReleaseAssetsFolderName = ".unitgit-release-assets";
        private const string AvatarThumbnailAssetsFolderName = "vrchat-avatar-thumbnails";
        private const string BuilderThumbnailPathFieldName = "_newThumbnailImagePath";
        private static readonly ConcurrentQueue<Action> MainThreadActions = new ConcurrentQueue<Action>();
        private static readonly Queue<AvatarUploadContext> PendingContexts = new Queue<AvatarUploadContext>();

        private static IVRCSdkAvatarBuilderApi attachedBuilder;
        private static AvatarUploadContext currentContext;
        private static bool commitTaskRunning;

        static VrchatAvatarUploadReleaseHook()
        {
            VRCSdkControlPanel.OnSdkPanelEnable -= OnSdkPanelEnable;
            VRCSdkControlPanel.OnSdkPanelEnable += OnSdkPanelEnable;
            VRCSdkControlPanel.OnSdkPanelDisable -= OnSdkPanelDisable;
            VRCSdkControlPanel.OnSdkPanelDisable += OnSdkPanelDisable;
            AssemblyReloadEvents.beforeAssemblyReload -= DetachBuilder;
            AssemblyReloadEvents.beforeAssemblyReload += DetachBuilder;
            EditorApplication.update -= DrainMainThreadActions;
            EditorApplication.update += DrainMainThreadActions;
        }

        private static void OnSdkPanelEnable(object sender, EventArgs args)
        {
            EditorApplication.delayCall -= TryAttachBuilder;
            EditorApplication.delayCall += TryAttachBuilder;
        }

        private static void OnSdkPanelDisable(object sender, EventArgs args)
        {
            DetachBuilder();
        }

        private static void TryAttachBuilder()
        {
            if (VRCSdkControlPanel.window == null)
            {
                return;
            }

            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
            {
                return;
            }

            if (ReferenceEquals(attachedBuilder, builder))
            {
                return;
            }

            DetachBuilder();
            attachedBuilder = builder;
            attachedBuilder.OnSdkBuildStart += OnSdkBuildStart;
            attachedBuilder.OnSdkBuildSuccess += OnSdkBuildSuccess;
            attachedBuilder.OnSdkBuildError += OnSdkBuildError;
            attachedBuilder.OnSdkUploadStart += OnSdkUploadStart;
            attachedBuilder.OnSdkUploadSuccess += OnSdkUploadSuccess;
            attachedBuilder.OnSdkUploadFinish += OnSdkUploadFinish;
            attachedBuilder.OnSdkUploadError += OnSdkUploadError;
        }

        private static void DetachBuilder()
        {
            if (attachedBuilder == null)
            {
                return;
            }

            attachedBuilder.OnSdkBuildStart -= OnSdkBuildStart;
            attachedBuilder.OnSdkBuildSuccess -= OnSdkBuildSuccess;
            attachedBuilder.OnSdkBuildError -= OnSdkBuildError;
            attachedBuilder.OnSdkUploadStart -= OnSdkUploadStart;
            attachedBuilder.OnSdkUploadSuccess -= OnSdkUploadSuccess;
            attachedBuilder.OnSdkUploadFinish -= OnSdkUploadFinish;
            attachedBuilder.OnSdkUploadError -= OnSdkUploadError;
            attachedBuilder = null;
        }

        private static void OnSdkBuildStart(object sender, object target)
        {
            currentContext = CaptureContext(target as GameObject);
        }

        private static void OnSdkBuildSuccess(object sender, string bundlePath)
        {
            AvatarUploadContext context = EnsureContext();
            context.BundlePath = NormalizePath(bundlePath);
        }

        private static void OnSdkBuildError(object sender, string message)
        {
            currentContext = null;
        }

        private static void OnSdkUploadStart(object sender, EventArgs args)
        {
            AvatarUploadContext context = EnsureContext();
            context.UploadStartedUtc = DateTime.UtcNow;
            CaptureThumbnailSource(context, sender);
        }

        private static void OnSdkUploadSuccess(object sender, string avatarId)
        {
            AvatarUploadContext context = EnsureContext();
            context.AvatarId = string.IsNullOrWhiteSpace(avatarId) ? context.AvatarId : avatarId.Trim();
            if (string.IsNullOrWhiteSpace(context.BlueprintId))
            {
                context.BlueprintId = context.AvatarId;
            }

            context.UploadSucceededUtc = DateTime.UtcNow;
            context.UploadSucceeded = true;
        }

        private static void OnSdkUploadFinish(object sender, string message)
        {
            AvatarUploadContext context = currentContext;
            currentContext = null;
            if (context == null || !context.UploadSucceeded)
            {
                return;
            }

            QueueUploadCommit(context);
        }

        private static void OnSdkUploadError(object sender, string message)
        {
            currentContext = null;
        }

        private static AvatarUploadContext EnsureContext()
        {
            if (currentContext != null)
            {
                return currentContext;
            }

            currentContext = CaptureContext(SelectedAvatar());
            return currentContext;
        }

        // The SDK's getter throws once the avatar it shows was destroyed (a build copy or a test avatar it picked up).
        private static GameObject SelectedAvatar()
        {
            if (attachedBuilder == null)
            {
                return null;
            }

            try
            {
                return attachedBuilder.SelectedAvatar;
            }
            catch (MissingReferenceException)
            {
                return null;
            }
        }

        private static AvatarUploadContext CaptureContext(GameObject avatarObject)
        {
            var context = new AvatarUploadContext
            {
                AvatarName = avatarObject != null ? avatarObject.name : "Avatar",
                UnityVersion = Application.unityVersion,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                Platform = GetPlatformLabel(EditorUserBuildSettings.activeBuildTarget),
                SdkAvatarPackageVersion = GetPackageVersion(typeof(IVRCSdkAvatarBuilderApi).Assembly),
                SdkBasePackageVersion = GetPackageVersion(typeof(IVRCSdkBuilderApi).Assembly)
            };

            if (avatarObject == null)
            {
                return context;
            }

            Scene scene = avatarObject.scene;
            context.ScenePath = NormalizePath(scene.IsValid() ? scene.path : string.Empty);
            context.AssetPath = NormalizePath(AssetDatabase.GetAssetPath(avatarObject));
            context.PrefabAssetPath = NormalizePath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatarObject));
            context.HierarchyPath = GetHierarchyPath(avatarObject.transform);

            if (avatarObject.TryGetComponent<PipelineManager>(out var pipelineManager))
            {
                context.BlueprintId = pipelineManager.blueprintId ?? string.Empty;
            }

            return context;
        }

        private static void QueueUploadCommit(AvatarUploadContext context)
        {
            if (!UnitGitSettings.AvatarUploadCommitEnabled)
            {
                Debug.Log("[Mixer Git] VRChat avatar upload commit skipped by Mixer Git settings.");
                return;
            }

            string projectRoot = UnitGitService.GetUnityProjectRoot();
            if (UnitGitSettings.AvatarUploadReleaseRowEnabled)
            {
                context.ThumbnailPath = SaveThumbnailSnapshot(context, projectRoot);
            }

            if (!SaveProjectStateForUploadCommit(out string saveError))
            {
                Debug.LogError("[Mixer Git] Could not prepare VRChat avatar upload commit: " + saveError);
                return;
            }

            PendingContexts.Enqueue(context);
            TryStartNextCommit();
        }

        private static bool SaveProjectStateForUploadCommit(out string message)
        {
            message = string.Empty;
            try
            {
                AssetDatabase.SaveAssets();
                if (!EditorSceneManager.SaveOpenScenes())
                {
                    message = "Unity did not save the open scenes.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private static void TryStartNextCommit()
        {
            if (commitTaskRunning || PendingContexts.Count == 0)
            {
                return;
            }

            commitTaskRunning = true;
            AvatarUploadContext context = PendingContexts.Dequeue();
            AvatarUploadCommitJob job = BuildCommitJob(context);
            Debug.Log("[Mixer Git] Creating VRChat avatar upload commit for " + job.DisplayName + ".");

            Task.Run(() => ExecuteCommitJob(job))
                .ContinueWith(task => MainThreadActions.Enqueue(() => CompleteCommitJob(job, task)));
        }

        private static AvatarUploadCommitJob BuildCommitJob(AvatarUploadContext context)
        {
            bool insertReleaseRow = UnitGitSettings.AvatarUploadReleaseRowEnabled;
            string displayName = string.IsNullOrWhiteSpace(context.AvatarName) ? "Avatar" : context.AvatarName.Trim();
            string platform = string.IsNullOrWhiteSpace(context.Platform) ? context.BuildTarget : context.Platform;
            string commitTitle = "vrchat avatar upload: " + displayName;
            if (!string.IsNullOrWhiteSpace(platform))
            {
                commitTitle += " (" + platform + ")";
            }

            return new AvatarUploadCommitJob
            {
                ProjectRoot = UnitGitService.GetUnityProjectRoot(),
                DisplayName = displayName,
                CommitTitle = commitTitle,
                CommitBody = BuildCommitBody(context),
                InsertReleaseRow = insertReleaseRow,
                ReleaseEntry = insertReleaseRow ? BuildReleaseEntry(context, displayName, platform) : null
            };
        }

        private static UnitGitReleaseResult ExecuteCommitJob(AvatarUploadCommitJob job)
        {
            var service = new UnitGitService(job.ProjectRoot);
            return job.InsertReleaseRow
                ? UnitGitReleases.PublishReleaseAll(job.ReleaseEntry, job.CommitTitle, service, false)
                : UnitGitReleases.CommitAll(job.CommitTitle, job.CommitBody, service, false);
        }

        private static void CompleteCommitJob(AvatarUploadCommitJob job, Task<UnitGitReleaseResult> task)
        {
            commitTaskRunning = false;
            UnitGitReleaseResult result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new UnitGitReleaseResult
                {
                    Success = false,
                    Message = task.Exception != null ? task.Exception.GetBaseException().Message : "The avatar upload commit failed."
                };

            if (result != null && result.Success)
            {
                UnitGitReleases.NotifyChangedExternally();
                Debug.Log("[Mixer Git] Created VRChat avatar upload commit " + ShortHash(result.CommitHash) + " for " + job.DisplayName + ".");
            }
            else
            {
                string message = result != null && !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : "The avatar upload commit failed.";
                Debug.LogError("[Mixer Git] " + message);
            }

            TryStartNextCommit();
        }

        private static void DrainMainThreadActions()
        {
            while (MainThreadActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Mixer Git] VRChat upload integration failed: " + ex.Message);
                }
            }
        }

        private static UnitGitReleaseEntry BuildReleaseEntry(AvatarUploadContext context, string displayName, string platform)
        {
            var fields = new List<UnitGitReleaseField>();
            AddField(fields, UnitGitAvatarUpload.AvatarIdKey, context.AvatarId);
            AddField(fields, UnitGitAvatarUpload.BlueprintIdKey, context.BlueprintId);
            AddField(fields, UnitGitAvatarUpload.PlatformKey, platform);
            AddField(fields, UnitGitAvatarUpload.BuildTargetKey, context.BuildTarget);
            AddField(fields, UnitGitAvatarUpload.SceneKey, context.ScenePath);
            AddField(fields, UnitGitAvatarUpload.AvatarPathKey, FirstNonEmpty(context.PrefabAssetPath, context.AssetPath, context.HierarchyPath));
            AddField(fields, UnitGitAvatarUpload.UnityKey, context.UnityVersion);
            AddField(fields, UnitGitAvatarUpload.AvatarsSdkKey, context.SdkAvatarPackageVersion);
            AddField(fields, UnitGitAvatarUpload.BaseSdkKey, context.SdkBasePackageVersion);
            AddField(fields, UnitGitAvatarUpload.UploadStartedKey, FormatUtc(context.UploadStartedUtc));
            AddField(fields, UnitGitAvatarUpload.UploadSucceededKey, FormatUtc(context.UploadSucceededUtc));
            AddField(fields, UnitGitAvatarUpload.BundleKey, context.BundlePath);

            return new UnitGitReleaseEntry
            {
                id = "vrchat-avatar-upload-" + Sanitize(FirstNonEmpty(context.AvatarId, displayName), "avatar") + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                tool = ToolName,
                type = ReleaseType,
                name = displayName,
                title = string.IsNullOrWhiteSpace(platform) ? "Avatar upload" : platform + " upload",
                scope = platform,
                changelog = UnitGitAvatarUpload.DefaultChangelog,
                date = DateTime.UtcNow.ToString("o"),
                author = Environment.UserName,
                thumbnailPath = context.ThumbnailPath,
                fields = fields
            };
        }

        private static void CaptureThumbnailSource(AvatarUploadContext context, object builder)
        {
            if (context == null)
            {
                return;
            }

            context.ThumbnailSourcePath = FirstExistingFilePath(
                AvatarBuilderSessionState.AvatarThumbPath,
                GetBuilderThumbnailPath(builder));
        }

        private static string GetBuilderThumbnailPath(object builder)
        {
            if (builder == null)
            {
                return string.Empty;
            }

            try
            {
                FieldInfo field = builder.GetType().GetField(
                    BuilderThumbnailPathFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return field != null ? field.GetValue(builder) as string ?? string.Empty : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SaveThumbnailSnapshot(AvatarUploadContext context, string projectRoot)
        {
            if (context == null || string.IsNullOrWhiteSpace(projectRoot))
            {
                return string.Empty;
            }

            string sourcePath = FirstExistingFilePath(context.ThumbnailSourcePath);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return string.Empty;
            }

            try
            {
                string folderRelativePath = ReleaseAssetsFolderName + "/" + AvatarThumbnailAssetsFolderName;
                string folderFullPath = Path.Combine(projectRoot, ReleaseAssetsFolderName, AvatarThumbnailAssetsFolderName);
                Directory.CreateDirectory(folderFullPath);

                string extension = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
                {
                    extension = ".png";
                }

                string avatarKey = Sanitize(FirstNonEmpty(context.AvatarId, context.BlueprintId, context.AvatarName), "avatar");
                DateTime timestamp = context.UploadSucceededUtc == default ? DateTime.UtcNow : context.UploadSucceededUtc;
                string fileName = avatarKey + "-" + timestamp.ToString("yyyyMMddHHmmss") + extension.ToLowerInvariant();
                string destinationPath = Path.Combine(folderFullPath, fileName);
                int suffix = 2;
                while (File.Exists(destinationPath))
                {
                    fileName = avatarKey + "-" + timestamp.ToString("yyyyMMddHHmmss") + "-" + suffix + extension.ToLowerInvariant();
                    destinationPath = Path.Combine(folderFullPath, fileName);
                    suffix++;
                }

                File.Copy(sourcePath, destinationPath);
                return NormalizePath(folderRelativePath + "/" + fileName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Mixer Git] Could not save VRChat avatar upload thumbnail: " + ex.Message);
                return string.Empty;
            }
        }

        private static string BuildCommitBody(AvatarUploadContext context)
        {
            var lines = new List<string>();
            AddLine(lines, "Avatar ID", context.AvatarId);
            AddLine(lines, "Blueprint ID", context.BlueprintId);
            AddLine(lines, "Platform", context.Platform);
            AddLine(lines, "Build Target", context.BuildTarget);
            AddLine(lines, "Scene", context.ScenePath);
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private static void AddField(List<UnitGitReleaseField> fields, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(new UnitGitReleaseField(key, value.Trim()));
            }
        }

        private static void AddLine(List<string> lines, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(key + ": " + value.Trim());
            }
        }

        private static string GetPackageVersion(System.Reflection.Assembly assembly)
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
                return packageInfo != null ? packageInfo.version : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var names = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private static string GetPlatformLabel(BuildTarget buildTarget)
        {
            switch (buildTarget)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "PC";
                case BuildTarget.Android:
                    return "Android/Quest";
                case BuildTarget.iOS:
                    return "iOS";
                default:
                    return buildTarget.ToString();
            }
        }

        private static string FormatUtc(DateTime value)
        {
            return value == default ? string.Empty : value.ToString("o");
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }

        private static string FirstExistingFilePath(params string[] paths)
        {
            if (paths == null)
            {
                return string.Empty;
            }

            string projectRoot = UnitGitService.GetUnityProjectRoot();
            foreach (string path in paths)
            {
                string normalized = NormalizePath(path);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (File.Exists(normalized))
                {
                    return normalized;
                }

                if (!string.IsNullOrWhiteSpace(projectRoot) && !Path.IsPathRooted(normalized))
                {
                    string projectPath = Path.Combine(projectRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(projectPath))
                    {
                        return NormalizePath(projectPath);
                    }
                }
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values == null
                ? string.Empty
                : values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string ShortHash(string hash)
        {
            return string.IsNullOrWhiteSpace(hash)
                ? "unknown"
                : hash.Trim().Substring(0, Math.Min(8, hash.Trim().Length));
        }

        private static string Sanitize(string value, string fallback)
        {
            string sanitized = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9._-]+", "-").Trim('-').ToLowerInvariant();
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        private sealed class AvatarUploadContext
        {
            public string AvatarName = string.Empty;
            public string AvatarId = string.Empty;
            public string BlueprintId = string.Empty;
            public string Platform = string.Empty;
            public string BuildTarget = string.Empty;
            public string ScenePath = string.Empty;
            public string AssetPath = string.Empty;
            public string PrefabAssetPath = string.Empty;
            public string HierarchyPath = string.Empty;
            public string UnityVersion = string.Empty;
            public string SdkAvatarPackageVersion = string.Empty;
            public string SdkBasePackageVersion = string.Empty;
            public string BundlePath = string.Empty;
            public string ThumbnailSourcePath = string.Empty;
            public string ThumbnailPath = string.Empty;
            public DateTime UploadStartedUtc;
            public DateTime UploadSucceededUtc;
            public bool UploadSucceeded;
        }

        private sealed class AvatarUploadCommitJob
        {
            public string ProjectRoot = string.Empty;
            public string DisplayName = string.Empty;
            public string CommitTitle = string.Empty;
            public string CommitBody = string.Empty;
            public bool InsertReleaseRow;
            public UnitGitReleaseEntry ReleaseEntry;
        }
    }
}
