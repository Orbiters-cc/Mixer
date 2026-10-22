using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Capture
{
    internal sealed partial class CaptureWindow
    {
        private static readonly (string label, int w, int h)[] Sizes = { ("720p", 1280, 720), ("1080p", 1920, 1080), ("4K", 3840, 2160) };
        private static readonly int[] Rates = { 24, 30, 60 };
        private static readonly int[] DepthChoices = { 0, 2, 3, 4, 8 };
        private static readonly float[] LapChoices = { 0.5f, 1f, 2f };
        private static readonly CapturePost[] Posts = { CapturePost.None, CapturePost.Scene, CapturePost.Typical, CapturePost.Bloom, CapturePost.HeavyBloom, CapturePost.Grading, CapturePost.ChromaticAberration };
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        private Label pathTitle, recordInfo, postCaption, checkCaption;
        private readonly VisualElement[] pathPages = new VisualElement[3];
        private VisualElement pointList, lengthRow, checkRows;
        private Button recordButton, clearRecordingButton;
        private ToggleSwitch loopSwitch;
        private ObjectField targetField;
        private Slider radiusSlider, heightSlider, lengthSlider, fovSlider;
        private Label radiusValue, heightValue, lengthValue, fovValue;
        private SegmentedControl lapsControl, sizeControl, rateControl, depthControl, postControl;
        private bool recordingNow, pathDirty = true;
        private double recordStart, pathBuiltAt;
        private Vector3[] pathPoints = Array.Empty<Vector3>();
        private CapturePose[] cachedPath;
        private string cachedProblem;

        // ---- Path card ------------------------------------------------------------------------------------------

        private void BuildPathCard(VisualElement parent)
        {
            var card = MixerUi.Card("CAMERA PATH", out var body);
            pathTitle = card.Q<Label>(className: "mx-card__title");
            parent.Add(card);
            for (int i = 0; i < pathPages.Length; i++)
            {
                pathPages[i] = MixerUi.Box("mx-capture-page");
                body.Add(pathPages[i]);
            }
            BuildRecordingPage(pathPages[0]);
            BuildWaypointsPage(pathPages[1]);
            BuildOrbitPage(pathPages[2]);
        }

        private void BuildRecordingPage(VisualElement page)
        {
            page.Add(MixerUi.Text("Press Record, then fly in the Scene view (hold the right mouse button and use WASD). Every editor update samples the view; the capture replays it at the frame rate.", "mx-caption"));
            var row = MixerUi.Row("mx-capture-actions");
            recordButton = MixerUi.Pill("Record", ToggleRecording, "accent", IconGlyph.Camera, "Record the Scene view's camera while you fly");
            row.Add(recordButton);
            recordInfo = MixerUi.Text("", "mx-chip mx-chip__text mx-capture-value");
            row.Add(recordInfo);
            row.Add(MixerUi.Spacer());
            clearRecordingButton = MixerUi.Pill("Clear", () => { recording.Clear(); MarkPathDirty(); }, "ghost", IconGlyph.Trash, "Forget the recording");
            row.Add(clearRecordingButton);
            page.Add(row);
        }

        private void BuildWaypointsPage(VisualElement page)
        {
            page.Add(MixerUi.Text("The camera passes through each waypoint in order, at constant speed, turning smoothly between their rotations. A waypoint is an object of the scene (it may move) or a view kept from the Scene view.", "mx-caption"));
            pointList = MixerUi.Box("mx-capture-points");
            page.Add(pointList);
            var row = MixerUi.Row("mx-capture-actions");
            row.Add(MixerUi.Pill("Add selection", AddSelection, "ghost", IconGlyph.Plus, "Add the selected objects, in selection order"));
            row.Add(MixerUi.Pill("Add Scene view", AddSceneView, "ghost", IconGlyph.Camera, "Keep the Scene view's current camera as a waypoint"));
            row.Add(MixerUi.Spacer());
            row.Add(MixerUi.Pill("Clear", () => { waypoints.Clear(); RebuildPoints(); MarkPathDirty(); }, "ghost", IconGlyph.Trash, "Remove every waypoint"));
            page.Add(row);
            loopSwitch = new ToggleSwitch(loopWaypoints, on => { loopWaypoints = on; MarkPathDirty(); });
            page.Add(MixerUi.Setting("Loop", "Come back to the first waypoint at the end", loopSwitch));
            RebuildPoints();
        }

        private void BuildOrbitPage(VisualElement page)
        {
            targetField = new ObjectField { objectType = typeof(Transform), allowSceneObjects = true, value = orbit.Target };
            targetField.AddToClassList("mx-objectfield");
            targetField.RegisterValueChangedCallback(evt =>
            {
                orbit.Target = evt.newValue as Transform;
                FitOrbit(true);
            });
            var fit = MixerUi.Icon(IconGlyph.Refresh, "Fit the radius and height to the target", () => FitOrbit(true));
            var targetRow = MixerUi.Row("mx-pair");
            targetRow.Add(targetField);
            targetRow.Add(fit);
            page.Add(MixerUi.Setting("Target", "What the camera circles and looks at; empty: the whole scene", targetRow, first: true));
            radiusSlider = Slider(0.2f, 40f, orbit.Radius, v => { orbit.Radius = v; MarkPathDirty(); }, out radiusValue);
            page.Add(MixerUi.Setting("Radius", "Distance from the target's centre", Pair(radiusSlider, radiusValue)));
            heightSlider = Slider(-10f, 20f, orbit.Height, v => { orbit.Height = v; MarkPathDirty(); }, out heightValue);
            page.Add(MixerUi.Setting("Height", "Above the target's centre", Pair(heightSlider, heightValue)));
            lapsControl = new SegmentedControl(new[] { "½", "1", "2" }, i => { orbit.Laps = LapChoices[i]; MarkPathDirty(); });
            lapsControl.AddToClassList("orb-segmented--compact");
            page.Add(MixerUi.Setting("Laps", "Turns around the target over the length", lapsControl));
        }

        internal void SelectPath(int index, bool animate)
        {
            if (recordingNow && index != (int)CapturePathKind.SceneView) StopRecording();
            int previous = (int)settings.Path;
            settings.Path = (CapturePathKind)index;
            pathControl?.SetIndex(index);
            for (int i = 0; i < pathPages.Length; i++)
            {
                if (pathPages[i] == null) continue;
                MixerUi.Show(pathPages[i], i == index);
                if (i == index && animate && previous != index) MixerUi.Enter(pathPages[i], previous < index ? "mx-page--from-right" : "mx-page--from-left", 20);
            }
            if (pathTitle != null) pathTitle.text = index == 0 ? "SCENE VIEW FLIGHT" : index == 1 ? "WAYPOINTS" : "ORBIT";
            MarkPathDirty();
        }

        private void RefreshPathCard()
        {
            MixerUi.SetPillText(recordButton, recordingNow ? "Stop" : "Record");
            recordButton.EnableInClassList("mx-button--accent", !recordingNow);
            recordButton.EnableInClassList("mx-button--danger", recordingNow);
            recordInfo.text = recording.Count == 0 ? "Nothing recorded" : recording.Duration.ToString("0.0", Culture) + " s · " + recording.Count + " samples";
            clearRecordingButton.SetEnabled(recording.Count > 0 && !recordingNow);
            radiusSlider.SetValueWithoutNotify(orbit.Radius);
            radiusValue.text = MixerUi.Metres(orbit.Radius);
            heightSlider.SetValueWithoutNotify(orbit.Height);
            heightValue.text = (orbit.Height < 0f ? "−" : "") + MixerUi.Metres(Mathf.Abs(orbit.Height));
            lapsControl.SetIndex(Array.IndexOf(LapChoices, orbit.Laps));
            if (targetField.value != orbit.Target) targetField.SetValueWithoutNotify(orbit.Target);
            if (pointList.childCount != Math.Max(1, waypoints.Count) || waypoints.Any(w => w.Target == null && !w.IsView)) RebuildPoints();
        }

        private void RebuildPoints()
        {
            if (pointList == null) return;
            waypoints.RemoveAll(w => w == null || (w.Target == null && !w.IsView));
            pointList.Clear();
            if (waypoints.Count == 0)
            {
                pointList.Add(MixerUi.Text("No waypoints yet: select objects and press Add selection, or frame a view and press Add Scene view.", "mx-capture-empty"));
                return;
            }
            for (int i = 0; i < waypoints.Count; i++)
            {
                var point = waypoints[i];
                int index = i;
                var row = MixerUi.Row("mx-capture-point");
                var dot = MixerUi.Box("mx-dot " + (point.IsView ? "mx-dot--violet" : "mx-dot--green"), PickingMode.Ignore);
                row.Add(dot);
                row.Add(MixerUi.Text((i + 1) + ". " + (point.Target != null ? point.Target.name : point.Label), "mx-capture-point__name"));
                row.Add(MixerUi.Text(point.IsView ? "View" : "Object", "mx-capture-point__kind"));
                row.Add(MixerUi.Icon(IconGlyph.Close, "Remove this waypoint", () => { waypoints.RemoveAt(index); RebuildPoints(); MarkPathDirty(); }));
                pointList.Add(row);
            }
        }

        private void AddSelection()
        {
            var picked = Selection.transforms.Where(t => t.gameObject.scene.IsValid() && waypoints.All(w => w.Target != t)).ToList();
            if (picked.Count == 0)
            {
                toasts.Show("Select objects of the scene first (cameras, empties, anything with a transform).", true);
                return;
            }
            foreach (var t in picked) waypoints.Add(CaptureWaypoint.Of(t));
            RebuildPoints();
            MarkPathDirty();
        }

        private void AddSceneView()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
            {
                toasts.Show("Open a Scene view first.", true);
                return;
            }
            var t = view.camera.transform;
            waypoints.Add(CaptureWaypoint.View("View " + (waypoints.Count(w => w.IsView) + 1), t.position, t.rotation));
            RebuildPoints();
            MarkPathDirty();
        }

        private void FitOrbit(bool notify)
        {
            if (!CaptureScene.TryGetBounds(orbit.Target, SceneManager.GetActiveScene(), out var bounds)) return;
            CapturePaths.OrbitFor(bounds, settings.FieldOfView, out orbit.Radius, out orbit.Height);
            orbit.Radius = Mathf.Clamp(orbit.Radius, 0.2f, 40f);
            orbit.Height = Mathf.Clamp(orbit.Height, -10f, 20f);
            orbitFitted = true;
            MarkPathDirty();
            if (notify && toasts != null) toasts.Show("Orbit fitted to " + (orbit.Target != null ? orbit.Target.name : "the scene") + ": " + MixerUi.Metres(orbit.Radius) + " away.");
        }

        // ---- Scene view recording -------------------------------------------------------------------------------

        private void ToggleRecording()
        {
            if (recordingNow)
            {
                StopRecording();
                toasts.Show(recording.Count > 1 ? "Recorded " + recording.Duration.ToString("0.0", Culture) + " s of flight." : "Nothing recorded: fly in the Scene view while recording.", recording.Count < 2);
                return;
            }
            if (SceneView.lastActiveSceneView == null)
            {
                toasts.Show("Open a Scene view first.", true);
                return;
            }
            recording.Clear();
            recordingNow = true;
            recordStart = EditorApplication.timeSinceStartup;
            EditorApplication.update += RecordTick;
            RecordTick();
            toasts.Show("Recording: fly in the Scene view, then press Stop.");
            dirty = true;
        }

        private void RecordTick()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return;
            var t = view.camera.transform;
            recording.Add((float)(EditorApplication.timeSinceStartup - recordStart), t.position, t.rotation);
            pathDirty = true;
        }

        private void StopRecording()
        {
            if (!recordingNow) return;
            EditorApplication.update -= RecordTick;
            recordingNow = false;
            MarkPathDirty();
        }

        // ---- Frames, post-processing and self-check cards ------------------------------------------------------

        private void BuildFramesCard(VisualElement parent)
        {
            var card = MixerUi.Card("FRAMES", out var body);
            parent.Add(card);
            sizeControl = new SegmentedControl(Sizes.Select(s => s.label), i => { settings.Width = Sizes[i].w; settings.Height = Sizes[i].h; MarkPathDirty(); });
            sizeControl.AddToClassList("orb-segmented--compact");
            body.Add(MixerUi.Setting("Size", "Pixels of each frame, as VRChat's Stream camera sends them", sizeControl, first: true));
            rateControl = new SegmentedControl(Rates.Select(r => r + " fps"), i => { settings.Fps = Rates[i]; MarkPathDirty(); });
            rateControl.AddToClassList("orb-segmented--compact");
            body.Add(MixerUi.Setting("Frame rate", "Frames per second of game time", rateControl));
            lengthSlider = Slider(1f, 60f, settings.Duration, v => { settings.Duration = Mathf.Round(v * 2f) / 2f; MarkPathDirty(); }, out lengthValue);
            lengthRow = MixerUi.Setting("Length", "Seconds of camera path (a recorded flight keeps its own)", Pair(lengthSlider, lengthValue));
            body.Add(lengthRow);
            fovSlider = Slider(20f, 110f, settings.FieldOfView, v => { settings.FieldOfView = Mathf.Round(v); MarkPathDirty(); }, out fovValue);
            body.Add(MixerUi.Setting("Field of view", "Vertical, in degrees", Pair(fovSlider, fovValue)));
            depthControl = new SegmentedControl(DepthChoices.Select(d => d == 0 ? "Off" : "1 in " + d), i => { settings.DepthEvery = DepthChoices[i]; MarkPathDirty(); });
            depthControl.AddToClassList("orb-segmented--compact");
            body.Add(MixerUi.Setting("Depth frames", "Every Nth frame carries exact depth as grey symbols instead of the picture", depthControl));
        }

        private void RefreshFramesCard()
        {
            sizeControl.SetIndex(Array.FindIndex(Sizes, s => s.w == settings.Width && s.h == settings.Height));
            rateControl.SetIndex(Array.IndexOf(Rates, Mathf.RoundToInt(settings.Fps)));
            depthControl.SetIndex(Array.IndexOf(DepthChoices, settings.DepthEvery));
            lengthSlider.SetValueWithoutNotify(settings.Duration);
            lengthValue.text = settings.Duration.ToString("0.#", Culture) + " s";
            fovSlider.SetValueWithoutNotify(settings.FieldOfView);
            fovValue.text = settings.FieldOfView.ToString("0", Culture) + "°";
            MixerUi.Show(lengthRow, settings.Path != CapturePathKind.SceneView);
        }

        private void BuildPostCard(VisualElement parent)
        {
            var card = MixerUi.Card("POST-PROCESSING", out var body);
            parent.Add(card);
            postControl = new SegmentedControl(Posts.Select(CaptureSettings.PostLabel), i => { settings.Post = Posts[i]; dirty = true; });
            postControl.AddToClassList("orb-segmented--compact");
            postControl.AddToClassList("mx-capture-post");
            body.Add(postControl);
            postCaption = MixerUi.Text("", "mx-caption");
            body.Add(postCaption);
            if (!CapturePostRig.Available)
                for (int i = 1; i < Posts.Length; i++) postControl.SetOptionEnabled(i, false);
        }

        private void RefreshPostCard()
        {
            postControl.SetIndex(Array.IndexOf(Posts, settings.Post));
            string caption = CapturePostRig.Describe(settings.Post);
            if (settings.Post == CapturePost.Scene)
            {
                string owner = CapturePostRig.SceneLayerOwner(SceneManager.GetActiveScene());
                caption += owner != null ? " Here: the layer on " + owner + "." : " This scene's camera has no Post-process Layer, so frames get none.";
            }
            if (settings.Post != CapturePost.None) caption += " It applies to the strip and depth symbols too, as on a capture avatar.";
            postCaption.text = CapturePostRig.Available ? caption : "Post Processing Stack v2 (com.unity.postprocessing) is not installed: frames have no post-processing.";
        }

        private void BuildCheckCard(VisualElement parent)
        {
            var card = MixerUi.Card("SELF-CHECK", out var body);
            parent.Add(card);
            checkCaption = MixerUi.Text("", "mx-caption");
            body.Add(checkCaption);
            checkRows = MixerUi.Box("mx-capture-checks");
            body.Add(checkRows);
        }

        private void RefreshCheckCard()
        {
            var run = watched;
            var check = run?.Check;
            checkRows.Clear();
            if (check == null)
            {
                checkCaption.text = run != null && run.State == CaptureRun.Phase.Checking
                    ? "Decoding " + run.CheckTotal + " frames as Orbiters desktop will…"
                    : "After each capture, up to " + CaptureSelfCheck.SamplePerKind + " colour and " + CaptureSelfCheck.SamplePerKind +
                      " depth frames are read back from disk, decoded like Orbiters desktop does and compared with the ground truth.";
                MixerUi.Show(checkCaption, true);
                return;
            }
            checkCaption.text = run.FrameCount + " frames of " + run.SceneName + " · " + run.Settings.Width + " × " + run.Settings.Height + " · " + run.PostNote;
            checkRows.Add(CheckRow("Capture strip", check.StripRate,
                check.StripExact + " of " + check.StripChecked + " sampled frames decode to their exact pose" +
                (check.StripUndecoded > 0 ? ", " + check.StripUndecoded + " not at all" : "") + (check.CorrectedBits > 0 ? ", " + check.CorrectedBits + " bits repaired by the vote" : "")));
            if (check.DepthSamples > 0)
                checkRows.Add(CheckRow("Depth frames", check.DepthRate,
                    MixerUi.Count(check.DepthWithinOne) + " of " + MixerUi.Count(check.DepthSamples) + " samples within ±1 of the true depth (" +
                    MixerUi.Percent(check.DepthExactRate) + " exact) in " + check.DepthFramesChecked + " frames" + (check.TilesRejected > 0 ? ", " + check.TilesRejected + " tiles rejected" : "")));
            var folderRow = MixerUi.Setting("Folder", run.Folder, MixerUi.Pill("Open", OpenLastFolder, "ghost", IconGlyph.External, "Show the capture in the file browser"));
            checkRows.Add(folderRow);
        }

        private static VisualElement CheckRow(string title, float rate, string detail)
        {
            var row = MixerUi.Setting(title, detail, MixerUi.Text((Mathf.FloorToInt(rate * 1000f) / 10f).ToString("0.#", Culture) + " %", "mx-chip mx-chip__text mx-capture-value" + (rate >= 1f ? " mx-capture-value--ok" : rate >= 0.99f ? " mx-capture-value--warn" : " mx-capture-value--bad")));
            var dot = MixerUi.Box("mx-dot " + (rate >= 1f ? "mx-dot--green" : rate >= 0.99f ? "mx-dot--amber" : "mx-dot--red"), PickingMode.Ignore);
            row.Insert(0, dot);
            return row;
        }

        // ---- Shared pieces -------------------------------------------------------------------------------------

        private static Slider Slider(float min, float max, float value, Action<float> changed, out Label valueLabel)
        {
            var slider = new Slider(min, max) { value = value };
            slider.AddToClassList("mx-slider");
            var label = MixerUi.Text("", "mx-chip mx-chip__text");
            slider.RegisterValueChangedCallback(evt => changed(evt.newValue));
            valueLabel = label;
            return slider;
        }

        private static VisualElement Pair(VisualElement a, VisualElement b)
        {
            var row = MixerUi.Row("mx-pair");
            row.Add(a);
            row.Add(b);
            return row;
        }

        private void MarkPathDirty()
        {
            pathDirty = true;
            dirty = true;
        }

        /// <summary>The path as one pose per frame, or why there is none. Cached; rebuilt when something changed.</summary>
        private bool TryBuildPath(out CapturePose[] poses, out string problem, bool repaint = true)
        {
            // Objects used as waypoints or orbit targets may move: rebuild those paths a few times a second.
            bool live = settings.Path == CapturePathKind.Waypoints && waypoints.Any(w => w.Target != null) ||
                        settings.Path == CapturePathKind.Orbit && orbit.Target != null;
            if (pathDirty || cachedPath == null && cachedProblem == null || live && EditorApplication.timeSinceStartup - pathBuiltAt > 0.25)
            {
                bool changed = pathDirty;
                pathDirty = false;
                pathBuiltAt = EditorApplication.timeSinceStartup;
                cachedPath = BuildPath(out cachedProblem);
                pathPoints = cachedPath == null ? Array.Empty<Vector3>() : cachedPath.Select(p => p.Position).ToArray();
                if (changed && repaint) SceneView.RepaintAll();
            }
            poses = cachedPath;
            problem = cachedProblem;
            return poses != null && poses.Length > 0;
        }

        private CapturePose[] BuildPath(out string problem)
        {
            problem = null;
            int frames = CapturePaths.FrameCount(settings.Duration, settings.Fps);
            switch (settings.Path)
            {
                case CapturePathKind.SceneView:
                    if (recording.Count < 2)
                    {
                        problem = recordingNow ? "Recording… fly in the Scene view." : "No flight recorded yet: press Record and fly in the Scene view.";
                        return null;
                    }
                    return CapturePaths.Resample(recording, settings.Fps);
                case CapturePathKind.Waypoints:
                    var points = new List<CapturePose>();
                    foreach (var w in waypoints)
                        if (w != null && w.TryGetPose(out var pose)) points.Add(pose);
                    if (points.Count == 0)
                    {
                        problem = "No waypoints yet.";
                        return null;
                    }
                    return CapturePaths.Spline(points, loopWaypoints, frames);
                default:
                    if (!CaptureScene.TryGetBounds(orbit.Target, SceneManager.GetActiveScene(), out var bounds))
                    {
                        problem = "Nothing to orbit: the scene has no visible renderers.";
                        return null;
                    }
                    return CapturePaths.Orbit(bounds.center, orbit.Radius, orbit.Height, orbit.Laps, frames);
            }
        }

        // ---- The path in the Scene view ------------------------------------------------------------------------

        private void OnSceneGUI(SceneView view)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!TryBuildPath(out var poses, out _, false) || poses.Length == 0) return;
            var accent = new Color(0f, 0.855f, 0.427f, 0.9f);
            using (new Handles.DrawingScope(accent))
            {
                if (pathPoints.Length > 1) Handles.DrawAAPolyLine(3f, pathPoints);
                DrawCamera(poses[0], settings.FieldOfView, settings.Width / (float)settings.Height, 0.35f * HandleUtility.GetHandleSize(poses[0].Position));
            }
            var run = watched;
            if (run != null && run.Busy && run.State == CaptureRun.Phase.Rendering && run.Rendered > 0 && run.Rendered <= poses.Length)
            {
                var at = poses[run.Rendered - 1];
                using (new Handles.DrawingScope(Color.white))
                    DrawCamera(at, settings.FieldOfView, settings.Width / (float)settings.Height, 0.35f * HandleUtility.GetHandleSize(at.Position));
            }
            if (settings.Path == CapturePathKind.Waypoints)
            {
                using (new Handles.DrawingScope(new Color(0.54f, 0.49f, 1f, 0.95f)))
                    foreach (var w in waypoints)
                        if (w != null && w.TryGetPose(out var pose))
                            Handles.SphereHandleCap(0, pose.Position, Quaternion.identity, 0.12f * HandleUtility.GetHandleSize(pose.Position), EventType.Repaint);
            }
        }

        private static void DrawCamera(CapturePose pose, float fov, float aspect, float size)
        {
            float h = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * size, w = h * aspect;
            var m = Matrix4x4.TRS(pose.Position, pose.Rotation, Vector3.one);
            var c = new[] { m.MultiplyPoint3x4(new Vector3(-w, -h, size)), m.MultiplyPoint3x4(new Vector3(w, -h, size)), m.MultiplyPoint3x4(new Vector3(w, h, size)), m.MultiplyPoint3x4(new Vector3(-w, h, size)) };
            for (int i = 0; i < 4; i++)
            {
                Handles.DrawAAPolyLine(2f, pose.Position, c[i]);
                Handles.DrawAAPolyLine(2f, c[i], c[(i + 1) % 4]);
            }
            Handles.DrawAAPolyLine(2f, c[2] + (c[3] - c[2]) * 0.3f, (c[2] + c[3]) * 0.5f + (c[2] - c[1]) * 0.35f, c[3] + (c[2] - c[3]) * 0.3f);
        }
    }
}
