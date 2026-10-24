using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    internal static class UnitGitSettings
    {
        private const string PrefPrefix = "Orbiters.UnitGit.Project.";
        private const string AvatarUploadCommitKey = "AvatarUpload.Commit";
        private const string AvatarUploadReleaseRowKey = "AvatarUpload.ReleaseRow";
        private const string VisitKey = "Visit";

        public static bool AvatarUploadCommitEnabled
        {
            get { return GetBool(AvatarUploadCommitKey, true); }
            set { SetBool(AvatarUploadCommitKey, value); }
        }

        public static bool AvatarUploadReleaseRowEnabled
        {
            get { return GetBool(AvatarUploadReleaseRowKey, true); }
            set { SetBool(AvatarUploadReleaseRowKey, value); }
        }

        /// <summary>Where backups go; empty means the default folder in Documents.</summary>
        public static string BackupFolder
        {
            get { return EditorPrefs.GetString(BuildProjectKey("Backups.Folder"), string.Empty); }
            set { EditorPrefs.SetString(BuildProjectKey("Backups.Folder"), value ?? string.Empty); }
        }

        public static bool AutomaticBackups
        {
            get { return GetBool("Backups.Automatic", false); }
            set { SetBool("Backups.Automatic", value); }
        }

        public static int BackupsToKeep
        {
            get { return Math.Max(1, EditorPrefs.GetInt(BuildProjectKey("Backups.Keep"), 10)); }
            set { EditorPrefs.SetInt(BuildProjectKey("Backups.Keep"), Math.Max(1, value)); }
        }

        /// <summary>The commit being visited and the way back, or null when no visit is under way.</summary>
        public static UnitGitVisit Visit
        {
            get
            {
                string json = EditorPrefs.GetString(BuildProjectKey(VisitKey), string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return null;
                try
                {
                    var visit = JsonUtility.FromJson<UnitGitVisit>(json);
                    return visit != null && !string.IsNullOrWhiteSpace(visit.commit) ? visit : null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            set
            {
                if (value == null) EditorPrefs.DeleteKey(BuildProjectKey(VisitKey));
                else EditorPrefs.SetString(BuildProjectKey(VisitKey), JsonUtility.ToJson(value));
            }
        }

        private static bool GetBool(string key, bool defaultValue)
        {
            return EditorPrefs.GetBool(BuildProjectKey(key), defaultValue);
        }

        private static void SetBool(string key, bool value)
        {
            EditorPrefs.SetBool(BuildProjectKey(key), value);
        }

        private static string BuildProjectKey(string key)
        {
            string projectRoot = string.Empty;
            try
            {
                projectRoot = Path.GetFullPath(UnitGitService.GetUnityProjectRoot())
                    .Replace('\\', '/')
                    .ToLowerInvariant();
            }
            catch
            {
                projectRoot = "unknown";
            }

            return PrefPrefix + StableHash(projectRoot) + "." + key;
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return hash.ToString("x16");
            }
        }
    }
}
