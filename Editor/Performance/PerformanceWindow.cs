using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Contracts;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// Mixer's performance import: pick a session Orbiters - N4N0 recorded (avatar parameters with face tracking,
    /// voice, VRChat's sound, SteamVR tracking) and an avatar in the scene, and bake what the avatar did into a clip,
    /// with the audio lined up and a Timeline to play them together. Same look as the Mixer and Capture windows.
    /// </summary>
    internal sealed class PerformanceWindow : EditorWindow, IHasCustomMenu
    {
        internal const string MenuPath = "Tools/Orbiters/Mixer Performance";
        const string MixerSheetPath = MixerInfo.BasePath + "/Editor/UI/mixer.uss";
        const string SheetPath = MixerInfo.BasePath + "/Editor/Performance/performance.uss";
        const string LibraryPref = "Orbiters.Mixer.Performance.Library";

        sealed class SessionInfo
        {
            public string Folder, Name;
            public DateTime When;
            public double Seconds;
            public bool Face, Voice, Sound, Body, Kit;
            /// <summary>VRChat sent its head and wrist poses.</summary>
            public bool VrChatBody;
            public int Calibrations;
        }

        [SerializeField] string selectedFolder;
        [SerializeField] VRCAvatarDescriptor avatar;
        [SerializeField] bool face = true, visemes = true, body = true, locomotion = true, audio = true, timeline = true;
        [SerializeField] int viewer, rate = 1, kitReach;

        static readonly float[] Rates = { 30f, 60f };

        VisualElement root, list, resultBox, resultBody;
        Label avatarInfo, footerText, footerRight, empty, kitInfo;
        ObjectField avatarField;
        Button bakeButton, kitInstall, kitRemove;
        SegmentedControl kitReachControl;
        // The avatar whose installed kit the Drawn switch was last set from.
        [NonSerialized] VRCAvatarDescriptor kitReachOf;
        MixerToasts toasts;
        readonly List<SessionInfo> sessions = new List<SessionInfo>();
        BakeResult last;
        // The bake under way (the avatar's build first): a reload drops it with the build.
        [NonSerialized] BakeJob baking;

        [MenuItem(MenuPath, priority = 2002)]
        public static void Open()
        {
            var window = GetWindow<PerformanceWindow>();
            window.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Performance", MixerLogo.TabIcon(), "Bake a recorded VRChat performance into a clip");
            minSize = new Vector2(480f, 420f);
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Open the sessions folder"), false, () => EditorUtility.RevealInFinder(Library));
            menu.AddItem(new GUIContent("Change the sessions folder…"), false, ChooseLibrary);
            menu.AddItem(new GUIContent("Open Mixer"), false, () => EditorApplication.ExecuteMenuItem(MixerInfo.MenuPath));
        }

        static string Library
        {
            get
            {
                string custom = EditorPrefs.GetString(LibraryPref, "");
                return string.IsNullOrWhiteSpace(custom) ? PerformanceSession.DefaultLibrary : custom;
            }
        }

        public void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-performance");
            foreach (var path in new[] { MixerSheetPath, SheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
                Orbiters.Mixer.Editor.MixerAccent.Apply(root);
            }
            BuildTopBar();
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("mx-body");
            root.Add(scroll);
            BuildSessions(scroll);
            BuildAvatar(scroll);
            BuildKit(scroll);
            BuildOptions(scroll);
            BuildResult(scroll);
            BuildFooter();
            toasts = new MixerToasts();
            root.Add(toasts);
            if (avatar == null) avatar = FindObjectsOfType<VRCAvatarDescriptor>().FirstOrDefault(a => a.gameObject.activeInHierarchy);
            avatarField.SetValueWithoutNotify(avatar);
            ReloadSessions();
            Refresh();
        }

        void BuildTopBar()
        {
            var bar = MixerUi.Box("mx-topbar");
            root.Add(bar);
            var brand = MixerUi.Box("mx-brand");
            var mark = MixerUi.Box("mx-brand__mark");
            var logo = new MixerLogo.Element();
            logo.AddToClassList("mx-brand__logo");
            mark.Add(logo);
            brand.Add(mark);
            brand.Add(MixerUi.Text("Performance", "mx-brand__name"));
            bar.Add(brand);
            bar.Add(MixerUi.Spacer());
            bar.Add(MixerUi.Icon(IconGlyph.Refresh, "Look for new sessions", () => { ReloadSessions(); Refresh(); }));
            bar.Add(MixerUi.Icon(IconGlyph.External, "Open the sessions folder", () => EditorUtility.RevealInFinder(Library)));
            bar.Add(MixerUi.Icon(IconGlyph.Settings, "Orbiters settings", OrbitersSettingsWindow.Open));
        }

        void BuildSessions(VisualElement parent)
        {
            var card = MixerUi.Card("SESSION", out var bodyBox);
            parent.Add(card);
            list = MixerUi.Box("mx-perf-sessions");
            bodyBox.Add(list);
            empty = MixerUi.Text("No session with a performance yet. Record one with Orbiters - N4N0: avatar parameters and face tracking, your voice, SteamVR tracking.", "mx-perf-empty");
            bodyBox.Add(empty);
            var actions = MixerUi.Row("mx-perf-actions");
            actions.Add(MixerUi.Pill("Choose a folder…", ChooseSession, "ghost", IconGlyph.External, "Bake a session from anywhere"));
            actions.Add(MixerUi.Spacer());
            bodyBox.Add(actions);
        }

        void BuildAvatar(VisualElement parent)
        {
            var card = MixerUi.Card("AVATAR", out var bodyBox);
            parent.Add(card);
            avatarField = new ObjectField { objectType = typeof(VRCAvatarDescriptor), allowSceneObjects = true };
            avatarField.AddToClassList("mx-objectfield");
            avatarField.RegisterValueChangedCallback(e =>
            {
                avatar = e.newValue as VRCAvatarDescriptor;
                Refresh();
            });
            bodyBox.Add(MixerUi.Setting("Avatar", "The avatar the performance was recorded with, in the open scene", avatarField, first: true));
            avatarInfo = MixerUi.Text("", "mx-caption mx-perf-info");
            bodyBox.Add(avatarInfo);
        }

        void BuildKit(VisualElement parent)
        {
            var card = MixerUi.Card("CAPTURE KIT", out var bodyBox);
            parent.Add(card);
            var buttons = MixerUi.Row("mx-perf-kit__buttons");
            kitRemove = MixerUi.Pill("Remove", RemoveKit, "ghost", IconGlyph.Trash, "Take the capture kit off the avatar (its assets stay)", destructive: true);
            kitInstall = MixerUi.Pill("Install", InstallKit, "primary", IconGlyph.Plus, "Add the capture kit to the avatar, then upload it");
            buttons.Add(kitRemove);
            buttons.Add(kitInstall);
            bodyBox.Add(MixerUi.Setting("On this avatar", "Draws your pose and the camera's into the frames VRChat's camera streams, only while you record", buttons, first: true));
            kitReachControl = new SegmentedControl(new[] { "Near you", "Everywhere" }, i => { kitReach = i; Refresh(); });
            kitReachControl.SetIndex(kitReach);
            bodyBox.Add(MixerUi.Setting("Drawn", "Near you: when the camera sees you or is within 2 m. Everywhere: always, but VRChat ranks the bounds Very Poor", kitReachControl));
            kitInfo = MixerUi.Text("", "mx-caption mx-perf-info");
            bodyBox.Add(kitInfo);
        }

        void BuildOptions(VisualElement parent)
        {
            var card = MixerUi.Card("BAKE", out var bodyBox);
            parent.Add(card);
            bodyBox.Add(MixerUi.Setting("Face and toggles", "Everything the avatar's animators showed, with what VRCFury and Modular Avatar add at upload: face tracking, gestures, toggles", new ToggleSwitch(face, v => { face = v; Refresh(); }), first: true));
            bodyBox.Add(MixerUi.Setting("Lip sync", "The visemes VRChat showed from your voice", new ToggleSwitch(visemes, v => { visemes = v; Refresh(); })));
            bodyBox.Add(MixerUi.Setting("Body", "VRChat's own pose with the capture kit; otherwise from the headset, controllers and trackers calibrated by your T-pose, or from VRChat's head and hand poses", new ToggleSwitch(body, v => { body = v; Refresh(); })));
            bodyBox.Add(MixerUi.Setting("Joystick movement", "Where VRChat moved you, estimated from its velocity", new ToggleSwitch(locomotion, v => { locomotion = v; Refresh(); })));
            bodyBox.Add(MixerUi.Setting("Audio", "Voice and VRChat's sound, lined up with the clip", new ToggleSwitch(audio, v => { audio = v; Refresh(); })));
            bodyBox.Add(MixerUi.Setting("Timeline", "A Timeline with the clip and the audio, ready to play", new ToggleSwitch(timeline, v => { timeline = v; Refresh(); })));
            var viewers = new SegmentedControl(new[] { "Others", "You" }, i => { viewer = i; Refresh(); });
            viewers.SetIndex(viewer);
            bodyBox.Add(MixerUi.Setting("As seen by", "Some avatar logic runs only for the wearer", viewers));
            var rates = new SegmentedControl(Rates.Select(r => r.ToString("0") + " fps"), i => { rate = i; Refresh(); });
            rates.SetIndex(rate);
            bodyBox.Add(MixerUi.Setting("Frame rate", "Samples per second of the clip", rates));
        }

        void BuildResult(VisualElement parent)
        {
            resultBox = MixerUi.Card("RESULT", out resultBody, "mx-perf-result");
            parent.Add(resultBox);
            MixerUi.Show(resultBox, false);
        }

        void BuildFooter()
        {
            var footer = MixerUi.Box("mx-footer");
            root.Add(footer);
            footerText = MixerUi.Text("", "mx-footer__text");
            footer.Add(footerText);
            footer.Add(MixerUi.Spacer());
            footerRight = MixerUi.Text("", "mx-footer__text mx-footer__text--right");
            footer.Add(footerRight);
            bakeButton = MixerUi.Pill("Bake", Bake, "primary", IconGlyph.Sparkle, "Bake the performance into a clip for the avatar");
            bakeButton.AddToClassList("mx-footer__button");
            footer.Add(bakeButton);
        }

        void ReloadSessions()
        {
            sessions.Clear();
            foreach (string folder in PerformanceSession.FindSessions(Library)) sessions.Add(Describe(folder));
            if (!string.IsNullOrEmpty(selectedFolder) && sessions.All(s => s.Folder != selectedFolder) && PerformanceSession.HasPerformance(selectedFolder))
                sessions.Insert(0, Describe(selectedFolder));
            if (sessions.All(s => s.Folder != selectedFolder)) selectedFolder = sessions.FirstOrDefault()?.Folder;
            RebuildList();
        }

        static SessionInfo Describe(string folder)
        {
            var info = new SessionInfo { Folder = folder, Name = Path.GetFileName(folder), When = Directory.GetLastWriteTime(folder) };
            try
            {
                string manifest = Path.Combine(folder, "manifest.json");
                if (File.Exists(manifest))
                {
                    var m = JsonConvert.DeserializeObject<SessionManifest>(File.ReadAllText(manifest));
                    info.Seconds = m?.Stats?.DurationS ?? 0;
                    info.Calibrations = m?.Performance?.Calibrations ?? 0;
                    info.Voice = m?.Performance?.Audio?.Any(a => a.Contains("mic")) ?? false;
                    info.Sound = m?.Performance?.Audio?.Any(a => a.Contains("game")) ?? false;
                }
            }
            catch (Exception)
            {
                // A session still being written: list it with what the folder shows.
            }
            info.Face = File.Exists(Path.Combine(folder, PerformanceFiles.Parameters));
            try
            {
                string table = Path.Combine(folder, PerformanceFiles.ParameterTable);
                info.VrChatBody = File.Exists(table) && File.ReadAllText(table).Contains(VrChatPoses.Address(VrChatPoses.Head));
            }
            catch (IOException)
            {
                // Being written.
            }
            info.Body = File.Exists(Path.Combine(folder, PerformanceFiles.Tracking));
            info.Kit = File.Exists(Path.Combine(folder, PerformanceFiles.Skeleton));
            return info;
        }

        void RebuildList()
        {
            if (list == null) return;
            list.Clear();
            MixerUi.Show(empty, sessions.Count == 0);
            foreach (var s in sessions.Take(40))
            {
                var row = MixerUi.Row("mx-perf-session");
                row.EnableInClassList("mx-perf-session--selected", s.Folder == selectedFolder);
                var texts = MixerUi.Box("mx-perf-session__texts", PickingMode.Ignore);
                texts.Add(MixerUi.Text(s.Name, "mx-perf-session__name"));
                texts.Add(MixerUi.Text(s.When.ToString("d MMM yyyy, HH:mm") + (s.Seconds > 0 ? " · " + Duration(s.Seconds) : ""), "mx-perf-session__detail"));
                row.Add(texts);
                var chips = MixerUi.Row("mx-perf-session__chips");
                chips.pickingMode = PickingMode.Ignore;
                if (s.Face) chips.Add(MixerUi.Chip("Face", accent: true));
                if (s.Voice) chips.Add(MixerUi.Chip("Voice"));
                if (s.Sound) chips.Add(MixerUi.Chip("Sound"));
                if (s.Kit) chips.Add(MixerUi.Chip("Body · exact", accent: true));
                else if (s.Body && s.Calibrations > 0) chips.Add(MixerUi.Chip("Body"));
                else if (s.VrChatBody) chips.Add(MixerUi.Chip("Body · VRChat"));
                else if (s.Body) chips.Add(MixerUi.Chip("Body · no T-pose"));
                row.Add(chips);
                string folder = s.Folder;
                MixerUi.Press(row, () =>
                {
                    selectedFolder = folder;
                    RebuildList();
                    Refresh();
                });
                list.Add(row);
            }
        }

        static string Duration(double seconds) => seconds >= 60 ? (int)(seconds / 60) + " min " + ((int)seconds % 60).ToString("00") + " s" : seconds.ToString("0.0") + " s";

        void Refresh()
        {
            if (root == null || avatarInfo == null) return;
            var s = sessions.FirstOrDefault(x => x.Folder == selectedFolder);
            if (avatar == null)
            {
                avatarInfo.text = "Pick the avatar (its VRChat Avatar Descriptor) in the scene.";
            }
            else
            {
                var animator = avatar.GetComponent<Animator>();
                bool human = animator != null && animator.avatar != null && animator.avatar.isHuman;
                string lip = avatar.lipSync switch
                {
                    VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape => "viseme blend shapes",
                    VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape => "jaw flap blend shape",
                    VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone => "jaw bone",
                    VRC_AvatarDescriptor.LipSyncStyle.VisemeParameterOnly => "Viseme parameter (animator)",
                    _ => "no lip sync",
                };
                avatarInfo.text = (human ? "Humanoid" : "Not humanoid (no body)") + " · lip sync: " + lip + " · eyes at " + MixerUi.Metres(avatar.ViewPosition.y * avatar.transform.lossyScale.y);
            }
            RefreshKit();
            footerText.text = s == null ? "Pick a session" : s.Name + (s.Seconds > 0 ? " · " + Duration(s.Seconds) : "") + " · " + Rates[rate].ToString("0") + " fps";
            MixerUi.SetPillText(bakeButton, baking != null ? "Baking…" : "Bake");
            bakeButton.SetEnabled(s != null && avatar != null && baking == null);
        }

        void RefreshKit()
        {
            var animator = avatar != null ? avatar.GetComponent<Animator>() : null;
            bool human = animator != null && animator.isHuman;
            var kit = avatar != null ? CaptureKit.Inspect(avatar) : null;
            // Picked up with a kit, the avatar's switch shows how that kit is drawn, so Install again keeps it.
            if (avatar != kitReachOf)
            {
                kitReachOf = avatar;
                if (kit != null && kit.Installed)
                {
                    kitReach = kit.Reach == CaptureKit.Reach.Everywhere ? 1 : 0;
                    kitReachControl.SetIndex(kitReach);
                }
            }
            MixerUi.SetPillText(kitInstall, kit != null && kit.Installed ? "Install again" : "Install");
            kitInstall.SetEnabled(human && kit.VrcFury);
            MixerUi.Show(kitRemove, kit != null && kit.Installed);
            if (avatar == null) kitInfo.text = "Pick the avatar first.";
            else if (!human) kitInfo.text = "The kit draws a humanoid's bones: this avatar is not humanoid.";
            else if (!kit.VrcFury) kitInfo.text = "The kit's Action Menu toggles are merged by VRCFury: add VRCFury to the project.";
            else if (!kit.Installed) kitInfo.text = "Not installed. With it, VRChat's own pose of your body goes into the recording, exactly as others saw it.";
            else if (kit.Stale) kitInfo.text = "The armature or the avatar's name changed since the kit was made: install it again, then upload.";
            else
                kitInfo.text = "Installed · " + kit.Bones + " bones · drawn " + (kit.Reach == CaptureKit.Reach.Everywhere ? "everywhere" : "near you") +
                               ". In VRChat, Action Menu › Orbiters › Record shows it and starts Orbiters - N4N0's recording.";
        }

        void InstallKit()
        {
            if (avatar == null) return;
            try
            {
                CaptureKit.Install(avatar, kitReach == 1 ? CaptureKit.Reach.Everywhere : CaptureKit.Reach.NearYou);
                toasts.Show("Capture kit installed on " + avatar.name + ". Upload the avatar to use it in VRChat.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                toasts.Show("The capture kit could not be installed: " + e.Message, true);
            }
            Refresh();
        }

        void RemoveKit()
        {
            if (avatar == null) return;
            try
            {
                CaptureKit.Remove(avatar);
                toasts.Show("Capture kit removed from " + avatar.name + ".");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                toasts.Show("The capture kit could not be removed: " + e.Message, true);
            }
            Refresh();
        }

        BakeOptions Options() => new BakeOptions
        {
            FrameRate = Rates[Mathf.Clamp(rate, 0, Rates.Length - 1)],
            Face = face,
            Visemes = visemes,
            Body = body,
            Locomotion = locomotion,
            Audio = audio,
            Timeline = timeline,
            Viewer = viewer == 1 ? Viewer.Local : Viewer.Remote,
        };

        void Bake()
        {
            if (avatar == null || string.IsNullOrEmpty(selectedFolder) || baking != null) return;
            try
            {
                var session = PerformanceSession.Load(selectedFolder);
                // The avatar is built as an upload builds it on the next frame (with its own progress), then baked.
                var job = PerformanceBaker.Start(session, avatar, Options(), (k, label) => !EditorUtility.DisplayCancelableProgressBar("Baking the performance", label, k));
                baking = job;
                job.Completed += () => Baked(job);
                if (job.Done) Baked(job);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                toasts.Show("Bake failed: " + e.Message, true);
            }
            Refresh();
        }

        void Baked(BakeJob job)
        {
            EditorUtility.ClearProgressBar();
            // Closed meanwhile: the clip is made all the same.
            if (this == null) return;
            if (baking == job) baking = null;
            if (job.Error is OperationCanceledException)
            {
                toasts.Show("Bake cancelled.");
            }
            else if (job.Error != null)
            {
                Debug.LogException(job.Error);
                toasts.Show("Bake failed: " + job.Error.Message, true);
            }
            else
            {
                last = job.Result;
                ShowResult(last);
                toasts.Show("Baked " + Duration(last.Seconds) + " into " + Path.GetFileName(last.ClipPath) + (last.Warnings.Count > 0 ? " (see the notes)" : "."));
                EditorGUIUtility.PingObject(last.Clip);
            }
            Refresh();
        }

        void ShowResult(BakeResult r)
        {
            resultBody.Clear();
            MixerUi.Show(resultBox, true);
            resultBody.Add(MixerUi.Setting("Clip", r.Frames + " frames · " + r.Curves + " curves · " + MixerUi.Count(r.Keys) + " keys · " + r.Elapsed.TotalSeconds.ToString("0.0") + " s",
                MixerUi.Pill("Select", () => { Selection.activeObject = r.Clip; EditorGUIUtility.PingObject(r.Clip); }, "ghost"), first: true));
            string bodyFrom = r.BodyFromSkeleton ? "Exactly as VRChat showed it (capture kit)"
                : r.BodyFromTracking ? "From SteamVR tracking, calibrated by the T-pose"
                : r.BodyFromVrChat ? "From VRChat's head and hand poses"
                : "From the animation (no tracking or calibration)";
            resultBody.Add(MixerUi.Setting("Body", bodyFrom, null));
            resultBody.Add(MixerUi.Setting("Animators", r.Built ? "As an upload builds them (VRCFury, Modular Avatar)" : "The scene avatar's own, not built (see the notes)", null));
            foreach (string a in r.AudioPaths) resultBody.Add(MixerUi.Setting("Audio", Path.GetFileName(a), MixerUi.Pill("Select", () => Selection.activeObject = AssetDatabase.LoadAssetAtPath<AudioClip>(a), "ghost")));
            if (!string.IsNullOrEmpty(r.TimelinePath))
                resultBody.Add(MixerUi.Setting("Timeline", Path.GetFileName(r.TimelinePath), MixerUi.Pill("Select", () => Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(r.TimelinePath), "ghost")));
            foreach (string w in r.Warnings) resultBody.Add(MixerUi.Text(w, "mx-perf-warning"));
            footerRight.text = r.Elapsed.TotalSeconds.ToString("0.0") + " s";
        }

        void ChooseSession()
        {
            string folder = EditorUtility.OpenFolderPanel("Choose a session folder", Library, "");
            if (string.IsNullOrEmpty(folder)) return;
            if (!PerformanceSession.HasPerformance(folder))
            {
                toasts.Show("That folder has no performance (parameters.bin, tracking.bin or audio.json).", true);
                return;
            }
            selectedFolder = folder;
            ReloadSessions();
            Refresh();
        }

        void ChooseLibrary()
        {
            string folder = EditorUtility.OpenFolderPanel("Choose the sessions folder", Library, "");
            if (string.IsNullOrEmpty(folder)) return;
            EditorPrefs.SetString(LibraryPref, folder);
            ReloadSessions();
            Refresh();
        }
    }
}
