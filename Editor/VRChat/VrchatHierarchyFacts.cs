using System;
using System.Collections.Generic;
using System.Net;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Mixer.Editor.VRChat
{
    /// <summary>
    /// Describes VRChat avatars for the Mixer Hierarchy: the upload a blueprint ID points to (its name and whether it is
    /// public, asked to VRChat's API through the SDK's own session, kept ten minutes) and the SDK's performance rank of
    /// the avatar as it is in the scene for the build target, worked out one avatar per editor frame after changes (at
    /// most every few seconds, never while a tool edits its meshes live).
    /// </summary>
    [InitializeOnLoad]
    internal static class VrchatHierarchyFacts
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SignedOutRetry = TimeSpan.FromSeconds(10);
        private const double RankEvery = 3.0;

        private sealed class Upload
        {
            public VRCAvatar? Avatar;
            public string Note;
            public DateTime Asked;
            public bool Pending;
        }

        private sealed class Rank
        {
            public PerformanceRating Rating;
            public bool Mobile;
            public int Changes;
            public double When;
        }

        private static readonly Dictionary<string, Upload> uploads = new Dictionary<string, Upload>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Rank> ranks = new Dictionary<int, Rank>();
        private static readonly List<int> toRank = new List<int>();
        private static int changes;
        // An upload asked while the SDK was signed out: the rows are told once it has signed in (again after each reload).
        private static bool waitingForSignIn;
        private static double nextSignInCheck;

        static VrchatHierarchyFacts()
        {
            HierarchyVrchat.Describe = Describe;
            EditorApplication.hierarchyChanged += Changed;
            Undo.undoRedoPerformed += Changed;
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream _) => Changed();
            EditorApplication.update += RankNext;
        }

        private static void Changed() => changes++;

        private static VrchatAvatarFacts Describe(GameObject root)
        {
            var facts = new VrchatAvatarFacts();
            if (root == null) return facts;
            var pipeline = root.GetComponent<PipelineManager>();
            facts.BlueprintId = pipeline != null && !string.IsNullOrWhiteSpace(pipeline.blueprintId) ? pipeline.blueprintId.Trim() : null;
            if (facts.BlueprintId == null) facts.Note = "Not uploaded yet";
            else
            {
                var upload = UploadOf(facts.BlueprintId);
                if (upload.Avatar.HasValue)
                {
                    facts.UploadedName = upload.Avatar.Value.Name;
                    facts.Visibility = string.Equals(upload.Avatar.Value.ReleaseStatus, "public", StringComparison.OrdinalIgnoreCase) ? "Public" : "Private";
                }
                else facts.Note = upload.Note;
            }
            bool mobile = Mobile;
            facts.RankPlatform = mobile ? "Android" : "Windows";
            int id = root.GetInstanceID();
            if (ranks.TryGetValue(id, out var rank) && rank.Mobile == mobile)
            {
                facts.Rank = RankName(rank.Rating);
                facts.RankIcon = RankIcon(rank.Rating);
            }
            if ((rank == null || rank.Changes != changes || rank.Mobile != mobile) && !toRank.Contains(id)) toRank.Add(id);
            return facts;
        }

        private static bool Mobile => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

        // ---- The upload on VRChat -------------------------------------------------------------------------------------

        private static Upload UploadOf(string blueprintId)
        {
            var now = DateTime.UtcNow;
            if (uploads.TryGetValue(blueprintId, out var upload) && (upload.Pending || now - upload.Asked < (upload.Avatar.HasValue ? Fresh : SignedOutRetry))) return upload;
            if (upload == null) uploads[blueprintId] = upload = new Upload();
            upload.Asked = now;
            if (!APIUser.IsLoggedIn)
            {
                upload.Note = "Signed out of the VRChat SDK";
                waitingForSignIn = true;
                return upload;
            }
            upload.Pending = true;
            if (!upload.Avatar.HasValue) upload.Note = "Asking VRChat…";
            Ask(blueprintId, upload);
            return upload;
        }

        // Started on the main thread: the answer comes back to it.
        private static async void Ask(string blueprintId, Upload upload)
        {
            try
            {
                upload.Avatar = await VRCApi.GetAvatar(blueprintId);
                upload.Note = null;
            }
            catch (ApiErrorException e) when (e.StatusCode == HttpStatusCode.NotFound || e.StatusCode == HttpStatusCode.Forbidden)
            {
                upload.Avatar = null;
                upload.Note = "Not on VRChat for this account (deleted, or another account's)";
            }
            catch (Exception)
            {
                upload.Note = upload.Avatar.HasValue ? upload.Note : "VRChat did not answer";
            }
            upload.Pending = false;
            upload.Asked = DateTime.UtcNow;
            HierarchyVrchat.NotifyChanged();
        }

        // ---- The performance rank -------------------------------------------------------------------------------------

        private static void RankNext()
        {
            double now = EditorApplication.timeSinceStartup;
            if (waitingForSignIn && now >= nextSignInCheck)
            {
                nextSignInCheck = now + 1.0;
                if (APIUser.IsLoggedIn)
                {
                    waitingForSignIn = false;
                    foreach (var upload in uploads.Values) if (!upload.Avatar.HasValue && !upload.Pending) upload.Asked = DateTime.MinValue;
                    HierarchyVrchat.NotifyChanged();
                }
            }
            if (toRank.Count == 0) return;
            for (int i = 0; i < toRank.Count; i++)
            {
                int id = toRank[i];
                var root = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (root == null) { toRank.RemoveAt(i); ranks.Remove(id); return; }
                ranks.TryGetValue(id, out var rank);
                bool mobile = Mobile;
                if (rank != null && rank.Mobile == mobile && now - rank.When < RankEvery) continue;
                if (AvatarMeshEdits.IsLiveEditing(root.transform)) continue;
                toRank.RemoveAt(i);
                int seen = changes;
                var rating = PerformanceRating.None;
                try
                {
                    var stats = new AvatarPerformanceStats(mobile);
                    AvatarPerformance.CalculatePerformanceStats(root.name, root, stats, mobile);
                    rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                }
                catch (Exception) { }
                ranks[id] = new Rank { Rating = rating, Mobile = mobile, Changes = seen, When = now };
                HierarchyVrchat.NotifyChanged();
                return;
            }
        }

        private static string RankName(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent: return "Excellent";
                case PerformanceRating.Good: return "Good";
                case PerformanceRating.Medium: return "Medium";
                case PerformanceRating.Poor: return "Poor";
                case PerformanceRating.VeryPoor: return "Very Poor";
                default: return null;
            }
        }

        // The SDK's own icons, as its builder panel shows them.
        private static Texture RankIcon(PerformanceRating rating)
        {
            string name;
            switch (rating)
            {
                case PerformanceRating.Excellent: name = "Great"; break;
                case PerformanceRating.Good: name = "Good"; break;
                case PerformanceRating.Medium: name = "Medium"; break;
                case PerformanceRating.Poor: name = "Poor"; break;
                case PerformanceRating.VeryPoor: name = "Horrible"; break;
                default: return null;
            }
            return Resources.Load<Texture2D>("PerformanceIcons/Perf_" + name + "_32");
        }
    }
}
