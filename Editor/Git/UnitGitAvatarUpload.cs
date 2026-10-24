using System;
using System.Linq;
using Orbiters.Toolkit.Editor;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// A VRChat avatar upload release (recorded by the VRChat SDK integration) read back from its fields: the avatar, the
    /// build and the upload times.
    /// </summary>
    internal sealed class UnitGitAvatarUpload
    {
        internal const string Tool = "VRChat SDK";
        internal const string Type = "avatar upload";
        /// <summary>The changelog every upload gets; the card leaves it out.</summary>
        internal const string DefaultChangelog = "Avatar uploaded with the VRChat SDK.";

        internal const string AvatarIdKey = "Avatar ID";
        internal const string BlueprintIdKey = "Blueprint ID";
        internal const string PlatformKey = "Platform";
        internal const string BuildTargetKey = "Build Target";
        internal const string SceneKey = "Scene";
        internal const string AvatarPathKey = "Avatar Path";
        internal const string UnityKey = "Unity";
        internal const string AvatarsSdkKey = "VRChat Avatars SDK";
        internal const string BaseSdkKey = "VRChat Base SDK";
        internal const string UploadStartedKey = "Upload Started";
        internal const string UploadSucceededKey = "Upload Succeeded";
        internal const string BundleKey = "Bundle";

        /// <summary>The fields the release card shows in its own places; any other field is listed as it is.</summary>
        internal static readonly string[] KnownKeys =
        {
            AvatarIdKey, BlueprintIdKey, PlatformKey, BuildTargetKey, SceneKey, AvatarPathKey, UnityKey, AvatarsSdkKey, BaseSdkKey,
            UploadStartedKey, UploadSucceededKey, BundleKey
        };

        public UnitGitReleaseEntry Release;
        public string AvatarId = string.Empty;
        public string BlueprintId = string.Empty;
        public string Platform = string.Empty;
        public string BuildTarget = string.Empty;
        public string Scene = string.Empty;
        public string AvatarPath = string.Empty;
        public string Unity = string.Empty;
        public string AvatarsSdk = string.Empty;
        public string BaseSdk = string.Empty;
        public string Bundle = string.Empty;
        public DateTime? StartedUtc;
        public DateTime? SucceededUtc;

        /// <summary>How long the upload took, from the SDK's start to its success.</summary>
        public TimeSpan? Duration => StartedUtc.HasValue && SucceededUtc.HasValue && SucceededUtc >= StartedUtc
            ? SucceededUtc.Value - StartedUtc.Value
            : (TimeSpan?)null;

        /// <summary>The avatar's page on vrchat.com; null without a valid avatar ID.</summary>
        public string PageUrl => VrcLinks.AvatarPage(AvatarId);

        /// <summary>Whether this upload was for PC (Windows).</summary>
        public bool IsPc => IsPcPlatform(Platform, BuildTarget);

        /// <summary>Whether this upload was for Android (Quest).</summary>
        public bool IsAndroid => IsAndroidPlatform(Platform, BuildTarget);

        /// <summary>The upload read from <paramref name="release"/>; null when it is not a VRChat avatar upload.</summary>
        internal static UnitGitAvatarUpload From(UnitGitReleaseEntry release)
        {
            if (!IsAvatarUpload(release)) return null;
            var upload = new UnitGitAvatarUpload
            {
                Release = release,
                AvatarId = Field(release, AvatarIdKey),
                BlueprintId = Field(release, BlueprintIdKey),
                Platform = Field(release, PlatformKey),
                BuildTarget = Field(release, BuildTargetKey),
                Scene = Field(release, SceneKey),
                AvatarPath = Field(release, AvatarPathKey),
                Unity = Field(release, UnityKey),
                AvatarsSdk = Field(release, AvatarsSdkKey),
                BaseSdk = Field(release, BaseSdkKey),
                Bundle = Field(release, BundleKey)
            };
            if (string.IsNullOrEmpty(upload.Platform)) upload.Platform = release.scope?.Trim() ?? string.Empty;
            if (UnitGitTime.TryParseUtc(Field(release, UploadStartedKey), out DateTime started)) upload.StartedUtc = started;
            if (UnitGitTime.TryParseUtc(Field(release, UploadSucceededKey), out DateTime succeeded)) upload.SucceededUtc = succeeded;
            return upload;
        }

        internal static bool IsAvatarUpload(UnitGitReleaseEntry release) =>
            release != null &&
            (string.Equals(release.tool?.Trim(), Tool, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(release.type?.Trim(), Type, StringComparison.OrdinalIgnoreCase) ||
             VrcLinks.IsAvatarId(Field(release, AvatarIdKey)));

        internal static string Field(UnitGitReleaseEntry release, string key)
        {
            var field = release?.fields?.FirstOrDefault(f => f != null && string.Equals(f.key?.Trim(), key, StringComparison.OrdinalIgnoreCase));
            return field?.value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// The platforms the avatar has been uploaded for up to this upload (VRChat keeps one upload per platform), so the
        /// card shows the badges VRChat showed then.
        /// </summary>
        internal void PlatformsSoFar(UnitGitReleaseFile releases, out bool pc, out bool android)
        {
            pc = IsPc;
            android = IsAndroid;
            if (releases?.releases == null || string.IsNullOrEmpty(AvatarId)) return;
            UnitGitTime.TryParseUtc(Release?.date, out DateTime until);
            foreach (UnitGitReleaseEntry other in releases.releases)
            {
                var upload = From(other);
                if (upload == null || !string.Equals(upload.AvatarId, AvatarId, StringComparison.OrdinalIgnoreCase)) continue;
                if (until != default && UnitGitTime.TryParseUtc(other.date, out DateTime when) && when > until) continue;
                pc |= upload.IsPc;
                android |= upload.IsAndroid;
            }
        }

        private static bool IsPcPlatform(string platform, string buildTarget) =>
            Contains(platform, "pc") || Contains(platform, "windows") || Contains(buildTarget, "StandaloneWindows");

        private static bool IsAndroidPlatform(string platform, string buildTarget) =>
            Contains(platform, "android") || Contains(platform, "quest") || Contains(buildTarget, "Android");

        private static bool Contains(string text, string part) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
