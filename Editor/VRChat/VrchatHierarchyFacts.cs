using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.Core;
using VRC.Dynamics;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Mixer.Editor.VRChat
{
    /// <summary>
    /// Describes VRChat avatars for the Mixer Hierarchy: the upload a blueprint ID points to (its name and whether it is
    /// public, asked to VRChat's API through the SDK's own session, kept ten minutes; an upload that is not there is asked
    /// again after ten minutes, one VRChat did not answer later and later) and the SDK's performance rank of the avatar as
    /// it is in the scene for the build target, worked out one avatar per editor frame after changes under that avatar (at
    /// most every few seconds, never while a tool edits its meshes live).
    /// </summary>
    [InitializeOnLoad]
    internal static class VrchatHierarchyFacts
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SignedOutRetry = TimeSpan.FromSeconds(10);
        private const double RankEvery = 3.0;
        // What VRChat's blueprint IDs look like: anything else (a damaged or crafted prefab) is never sent to its API.
        private static readonly Regex AvatarId = new Regex("^avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private sealed class Upload
        {
            public VRCAvatar? Avatar;
            public string Note;
            public DateTime Asked;
            public TimeSpan Wait;
            public int Failures;
            public bool Pending;
            // Asked past the SDK's own cache: the avatar just changed in the SDK panel.
            public bool Force;
        }

        private sealed class Rank
        {
            public PerformanceRating Rating;
            public bool Failed;
            public bool Mobile;
            public int Changes;
            public double When;
        }

        // Keyed by the signed-in account and the blueprint ID: another account sees other uploads.
        private static readonly Dictionary<string, Upload> uploads = new Dictionary<string, Upload>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Rank> ranks = new Dictionary<int, Rank>();
        private static readonly List<int> toRank = new List<int>();
        // Changes under each ranked avatar (by its root's instance ID), and changes that may touch any (a scene change, an undo).
        private static readonly Dictionary<int, int> avatarChanges = new Dictionary<int, int>();
        private static int everywhere;
        // An upload asked while the SDK was signed out: the rows are told once it has signed in (again after each reload).
        private static bool waitingForSignIn;
        private static double nextSignInCheck;

        static VrchatHierarchyFacts()
        {
            HierarchyVrchat.Describe = Describe;
            Undo.undoRedoPerformed += () => everywhere++;
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorApplication.update += RankNext;
            EditorApplication.update += WatchSdkPanel;
        }

        private static int ChangesOf(int root) => everywhere + (avatarChanges.TryGetValue(root, out int count) ? count : 0);

        // Only what changed under a ranked avatar ranks it again (moving a cube elsewhere does not).
        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties: stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties); Touch(properties.instanceId); break;
                    case ObjectChangeKind.CreateGameObjectHierarchy: stream.GetCreateGameObjectHierarchyEvent(i, out var created); Touch(created.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy: stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy); Touch(hierarchy.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructure: stream.GetChangeGameObjectStructureEvent(i, out var structure); Touch(structure.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectParent: stream.GetChangeGameObjectParentEvent(i, out var parent); Touch(parent.instanceId); Touch(parent.previousParentInstanceId); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy: stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed); Touch(destroyed.parentInstanceId); break;
                    case ObjectChangeKind.UpdatePrefabInstances: stream.GetUpdatePrefabInstancesEvent(i, out var prefabs); foreach (int id in prefabs.instanceIds) Touch(id); break;
                    case ObjectChangeKind.ChangeScene: everywhere++; break;
                }
            }
        }

        private static void Touch(int instanceId)
        {
            if (instanceId == 0 || ranks.Count == 0 && toRank.Count == 0) return;
            var changed = EditorUtility.InstanceIDToObject(instanceId);
            var go = changed as GameObject ?? (changed as Component)?.gameObject;
            if (go == null) return;
            for (var t = go.transform; t != null; t = t.parent)
            {
                int id = t.gameObject.GetInstanceID();
                if (!ranks.ContainsKey(id) && !toRank.Contains(id)) continue;
                avatarChanges[id] = (avatarChanges.TryGetValue(id, out int count) ? count : 0) + 1;
            }
        }

        private static VrchatAvatarFacts Describe(GameObject root)
        {
            var facts = new VrchatAvatarFacts();
            if (root == null) return facts;
            var pipeline = root.GetComponent<PipelineManager>();
            facts.BlueprintId = pipeline != null && !string.IsNullOrWhiteSpace(pipeline.blueprintId) ? pipeline.blueprintId.Trim() : null;
            if (facts.BlueprintId == null) facts.Note = "Not uploaded yet";
            else if (!AvatarId.IsMatch(facts.BlueprintId)) { facts.BlueprintId = null; facts.Note = "Its blueprint ID is not a VRChat avatar ID"; }
            else
            {
                var upload = UploadOf(facts.BlueprintId);
                // A bought base often keeps its seller's blueprint ID, and a public avatar answers for any account.
                string author = upload.Avatar?.AuthorId, me = APIUser.IsLoggedIn ? APIUser.CurrentUser?.id : null;
                if (upload.Avatar.HasValue && me != null && !string.IsNullOrEmpty(author) && author != me)
                    facts.Note = "Its blueprint ID is another account's upload (it came with the avatar?)";
                else if (upload.Avatar.HasValue)
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
                facts.Rank = rank.Failed ? "Could not be ranked" : RankName(rank.Rating);
                facts.RankIcon = rank.Failed ? null : RankIcon(rank.Rating);
            }
            if ((rank == null || rank.Changes != ChangesOf(id) || rank.Mobile != mobile) && !toRank.Contains(id)) toRank.Add(id);
            return facts;
        }

        private static bool Mobile => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

        // ---- The upload on VRChat -------------------------------------------------------------------------------------

        private static Upload UploadOf(string blueprintId)
        {
            var now = DateTime.UtcNow;
            string key = (APIUser.IsLoggedIn ? APIUser.CurrentUser?.id : null) + "|" + blueprintId;
            if (uploads.TryGetValue(key, out var upload) && (upload.Pending || now - upload.Asked < upload.Wait)) return upload;
            if (upload == null) uploads[key] = upload = new Upload();
            upload.Asked = now;
            if (!APIUser.IsLoggedIn)
            {
                upload.Note = "Signed out of the VRChat SDK";
                upload.Wait = SignedOutRetry;
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
                bool force = upload.Force;
                upload.Force = false;
                upload.Avatar = await VRCApi.GetAvatar(blueprintId, forceRefresh: force);
                upload.Note = null;
                upload.Failures = 0;
                upload.Wait = Fresh;
            }
            catch (ApiErrorException e) when (e.StatusCode == HttpStatusCode.NotFound || e.StatusCode == HttpStatusCode.Forbidden)
            {
                // Not there for this account: that stays so, asked again only after a while.
                upload.Avatar = null;
                upload.Note = "Not on VRChat for this account (deleted, or another account's)";
                upload.Wait = Fresh;
            }
            catch (Exception)
            {
                // Offline, an expired session, a server error: asked again later and later (10 s, 20 s... 10 minutes).
                upload.Note = upload.Avatar.HasValue ? upload.Note : "VRChat did not answer";
                upload.Failures++;
                upload.Wait = TimeSpan.FromSeconds(Math.Min(Fresh.TotalSeconds, SignedOutRetry.TotalSeconds * Math.Pow(2, Math.Min(upload.Failures - 1, 10))));
            }
            upload.Pending = false;
            upload.Asked = DateTime.UtcNow;
            HierarchyVrchat.NotifyChanged();
        }

        // ---- Changes made in the SDK panel ---------------------------------------------------------------------------

        // Saving the avatar's details (its visibility, name, description) in the SDK panel and uploading it change what
        // VRChat says about it: the rows ask again a moment after, past the SDK's cache, instead of keeping the old answer
        // ten minutes. The SDK has no event for a save, so its Save Changes button is followed.
        private static Button hookedSave;
        private static IVRCSdkBuilderApi hookedBuilder;
        private static double nextPanelCheck;
        private static readonly List<double> refreshAt = new List<double>();

        private static void WatchSdkPanel()
        {
            double now = EditorApplication.timeSinceStartup;
            for (int i = refreshAt.Count - 1; i >= 0; i--)
            {
                if (now < refreshAt[i]) continue;
                refreshAt.RemoveAt(i);
                RefreshUploads();
            }
            if (now < nextPanelCheck) return;
            nextPanelCheck = now + 1;
            try
            {
                var window = VRCSdkControlPanel.window;
                if (window == null) { hookedSave = null; return; }
                var root = window.rootVisualElement;
                var save = root?.panel?.visualTree?.Q<Button>("save-changes-button") ?? root?.Q<Button>("save-changes-button");
                if (save != null && !ReferenceEquals(save, hookedSave))
                {
                    hookedSave = save;
                    save.clicked += AfterSdkChange;
                }
                if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkBuilderApi>(out var builder) && !ReferenceEquals(builder, hookedBuilder))
                {
                    if (hookedBuilder != null) hookedBuilder.OnSdkUploadSuccess -= OnUploaded;
                    hookedBuilder = builder;
                    builder.OnSdkUploadSuccess += OnUploaded;
                }
            }
            catch (Exception)
            {
                // A panel built differently by another SDK version: the rows keep asking on their own schedule.
            }
        }

        private static void OnUploaded(object sender, string id) => AfterSdkChange();

        // The SDK sends the save after the click: asked again 2 s later, and once more at 8 s in case VRChat was slow.
        private static void AfterSdkChange()
        {
            double now = EditorApplication.timeSinceStartup;
            refreshAt.Add(now + 2);
            refreshAt.Add(now + 8);
        }

        /// <summary>Every upload the rows show is asked again, past the SDK's own cache.</summary>
        internal static void RefreshUploads()
        {
            foreach (var upload in uploads.Values)
            {
                if (upload.Pending) continue;
                upload.Wait = TimeSpan.Zero;
                upload.Force = true;
            }
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
                    foreach (var upload in uploads.Values) if (!upload.Avatar.HasValue && !upload.Pending && upload.Failures == 0) upload.Asked = DateTime.MinValue;
                    HierarchyVrchat.NotifyChanged();
                }
            }
            if (toRank.Count == 0) return;
            for (int i = 0; i < toRank.Count; i++)
            {
                int id = toRank[i];
                var root = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (root == null) { toRank.RemoveAt(i); ranks.Remove(id); avatarChanges.Remove(id); return; }
                ranks.TryGetValue(id, out var rank);
                bool mobile = Mobile;
                if (rank != null && rank.Mobile == mobile && now - rank.When < RankEvery) continue;
                if (AvatarMeshEdits.IsLiveEditing(root.transform)) continue;
                toRank.RemoveAt(i);
                int seen = ChangesOf(id);
                var rating = PerformanceRating.None;
                bool failed = false;
                try
                {
                    // As the SDK's builder panel: constraints' groups first, else unregistered ones count wrong.
                    RefreshConstraintGroups?.Invoke(null, new object[] { root.GetComponentsInChildren<VRCConstraintBase>(true) });
                    var stats = new AvatarPerformanceStats(mobile);
                    AvatarPerformance.CalculatePerformanceStats(root.name, root, stats, mobile);
                    rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                }
                catch (Exception) { failed = true; }
                ranks[id] = new Rank { Rating = rating, Failed = failed, Mobile = mobile, Changes = seen, When = now };
                HierarchyVrchat.NotifyChanged();
                return;
            }
        }

        // The SDK's own step before ranking (internal to it, so looked up; without it the rank skips that step).
        private static readonly System.Reflection.MethodInfo RefreshConstraintGroups =
            typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager")?.GetMethod("Sdk_ManuallyRefreshGroups",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(VRCConstraintBase[]) }, null);

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
