using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What keeps sessions through everything around them:
    /// <list type="bullet">
    /// <item>script reloads: the working meshes survive (HideAndDontSave), the documents are written at once before the
    /// reload and rebuilt from the originals and the autosave after it;</item>
    /// <item>scene saves and uploads get the originals: the meshes, the renderer's own weights, the bone list without
    /// added leaf bones, bones placed for the original bind poses (<see cref="RestPosePlacement"/>), and the bones
    /// Weight Paint posed as they were (<see cref="BoneSubSelection"/>);</item>
    /// <item>Play, closing a scene, deleting an edited object, a crash or quitting: the session waits as a record and
    /// its autosave, and comes back when Play stops, the scene is reopened, Undo brings the object back or the editor
    /// starts again;</item>
    /// <item>a copy of an edited object (Ctrl+D, paste) gets a session of its own with the same edits;</item>
    /// <item>a prefab written from an instance being edited gets the original mesh, never the working copy.</item>
    /// </list>
    /// </summary>
    internal static partial class MixerSession
    {
        private const string StateKey = "Orbiters.Mixer.Session";
        private const string WaitingKey = "Orbiters.Mixer.Waiting";
        private const string BootedKey = "Orbiters.Mixer.Booted";
        private static readonly TimeSpan RecordLife = TimeSpan.FromDays(14);

        private static readonly HashSet<string> pendingRestore = new HashSet<string>();
        private static readonly List<Record> waiting = new List<Record>();
        // Sessions an undo moved their renderer off (by record id): kept whole, history included, for the redo.
        private static readonly Dictionary<string, EditTarget> parkedTargets = new Dictionary<string, EditTarget>();
        private static readonly List<int> createdObjects = new List<int>();
        private static readonly Dictionary<string, double> watchedPrefabs = new Dictionary<string, double>();
        private static readonly Dictionary<int, SaveStash> stashes = new Dictionary<int, SaveStash>();
        private static double lastAutosave;
        private static bool dirtySinceAutosave;
        private static bool restoring;
        private static bool recoverDue;
        private static bool repairAfterRestore;

        /// <summary>A session waiting to come back.</summary>
        private sealed class Record
        {
            /// <summary>'p' parked (its object was deleted; Undo may bring it back), 's' suspended (Play, its scene closed), 'f' from the recovery file (a crash, a restart).</summary>
            public char Kind;
            public string Id, Original, Layer = "", Held = "", Path = "";
            public int Renderer;
            /// <summary>A parked session's working mesh, kept alive (instance id; 0 for none): the renderer showing it again brings the session back.</summary>
            public int Working;
            public long Time;

            public string Line => string.Join("\t", Kind.ToString(), Clean(Id), Clean(Original), Clean(Layer), Clean(Held), Renderer.ToString(CultureInfo.InvariantCulture), Time.ToString(CultureInfo.InvariantCulture), Clean(Path), Working.ToString(CultureInfo.InvariantCulture));

            public static Record Parse(string line)
            {
                var p = line.Split('\t');
                if (p.Length < 7 || p[0].Length != 1) return null;
                return new Record
                {
                    Kind = p[0][0], Id = p[1], Original = p[2], Layer = p[3], Held = p[4],
                    Renderer = int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0,
                    Time = long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0,
                    Path = p.Length > 7 ? p[7] : "",
                    Working = p.Length > 8 && int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : 0,
                };
            }

            private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private sealed class SaveStash
        {
            public readonly List<EditTarget> Shown = new List<EditTarget>();
            public readonly List<(SkinnedMeshRenderer renderer, Transform[] bones)> Bones = new List<(SkinnedMeshRenderer, Transform[])>();
            public List<RestPosePlacement.Pose> Poses;
            /// <summary>Bones Weight Paint posed, as posed (put back after the save).</summary>
            public List<RestPosePlacement.Pose> Posed;
        }

        private static string RecoveryFile => System.IO.Path.Combine(MixerAutosave.Folder, "sessions.tsv");

        private static void HookLifecycle()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += scene => RestoreStash(scene.handle);
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.sceneOpened += (scene, mode) => recoverDue = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeQuit;
            Undo.undoRedoPerformed += OnUndoRedo;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            LoadState();
        }

        // ---- The editor loop -----------------------------------------------------------------------------------------

        /// <summary>Once a frame: restores after a reload, brings waiting sessions back, flushes edits, autosaves.</summary>
        internal static void Tick()
        {
            if (restoring) return;
            // Nothing in Play: the sessions wait for edit mode (see OnPlayModeChanged).
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (pendingRestore.Count > 0) RestorePending();
            if (repairAfterRestore)
            {
                // After the restore, never before it: a working mesh it is about to show again is not an orphan.
                repairAfterRestore = false;
                MixerMeshGuard.Repair(everywhere: true);
            }
            if (recoverDue) { recoverDue = false; if (waiting.Count > 0) TryRecover(); }
            if (createdObjects.Count > 0)
            {
                var created = createdObjects.ToList();
                createdObjects.Clear();
                foreach (int id in created) if (EditorUtility.InstanceIDToObject(id) is GameObject go) MixerMeshGuard.RepairUnder(go);
            }
            if (watchedPrefabs.Count > 0) CheckPrefabs();
            bool any = false;
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                var t = targets[i];
                if (!t.Alive) { Gone(t); continue; }
                // Another tool gave the renderer another mesh: Mixer must not put its own back over it (a scene save, a
                // commit would). The edits wait; an Undo that gives the mesh back brings them back.
                if (stashes.Count == 0 && t.Shown != t.Working)
                {
                    Say("Mixer stopped editing " + t.Name + ": it was given another mesh. Its edits come back if Undo gives the mesh back.", false);
                    Gone(t);
                    continue;
                }
                any |= t.Flush();
            }
            if (any) { StrokeTimings.Uploaded(); SceneView.RepaintAll(); }
            double now = EditorApplication.timeSinceStartup;
            // Every 8 s at most: a snapshot copies megabytes on the main thread, garbage that brings the next collection
            // (half a second on a large project) nearer, often to the press of the next stroke.
            if (dirtySinceAutosave && now - lastAutosave > 8.0 && !MixerSceneTool.Stroking)
            {
                lastAutosave = now;
                dirtySinceAutosave = false;
                // One with nothing left to keep (a mask cleared, every edit undone) is deleted: a reload must not bring
                // the old one back.
                foreach (var t in targets)
                {
                    if (!t.Alive) continue;
                    if (NeedsAutosave(t)) MixerAutosave.Write(t);
                    else MixerAutosave.Delete(t.Id);
                }
            }
        }

        private static bool NeedsAutosave(EditTarget t) => t.Document.Changed || t.Document.HasMask;

        /// <summary>Writes every session's autosave before returning (a reload, Play, quitting; tests).</summary>
        internal static void FlushAutosavesNow()
        {
            foreach (var t in targets)
            {
                if (!t.Alive) continue;
                try
                {
                    if (NeedsAutosave(t)) MixerAutosave.WriteNow(t);
                    else MixerAutosave.Delete(t.Id);
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            dirtySinceAutosave = false;
            lastAutosave = EditorApplication.timeSinceStartup;
        }

        // The renderer or its working mesh is gone. A deleted object can come back with Undo: its session waits for it.
        // An undo past the session's start, or another tool's mesh, moved the renderer off its working mesh: the same.
        // The session is parked with its working mesh kept alive; the renderer showing it again (Undo of the delete, Redo)
        // brings the session back as it was.
        private static void Gone(EditTarget t)
        {
            bool keep = NeedsAutosave(t);
            if (keep)
            {
                try { MixerAutosave.WriteNow(t); }
                catch (Exception e) { Debug.LogException(e); keep = false; }
            }
            bool workingAlive = t.Working != null;
            // Undone back to the mesh it started from, a session whose steps can still be redone waits for the redo too.
            bool redo = t.Renderer != null && MixerHistory.HasRedo(t);
            if (!workingAlive || (!keep && !redo))
            {
                if (keep) AddWaiting(RecordOf(t, 'p'));
                EndSession(t, t.Original, deleteAutosave: !keep);
                // Only the working mesh went (another tool destroyed it): the renderer gets the original and the session
                // comes back on a new working copy at once.
                if (keep && t.Renderer != null) { MixerMeshGuard.Repair(); recoverDue = true; }
                return;
            }
            var record = RecordOf(t, 'p');
            record.Working = t.Working.GetInstanceID();
            AddWaiting(record);
            MixerLinkService.ReleaseForUnity(t);
            if (t.Renderer != null)
            {
                // Its renderer is still there: the session itself waits, with its history, for the redo.
                parkedTargets[t.Id] = t;
            }
            else
            {
                MixerHistory.Forget(t);
                ArmatureEdit.Forget(t);
            }
            targets.Remove(t);
            SaveState();
            Changed?.Invoke();
        }

        // A parked session goes on where it was: its working mesh, layer, history.
        private static void Resume(EditTarget kept, Renderer renderer)
        {
            targets.Add(kept);
            MixerMeshGuard.Shown(kept.Working, kept.Original, renderer);
            ArmatureEdit.SyncFromHolder(kept);
            kept.HoldLayerWeight();
            kept.Document.MarkAllDirty();
            kept.Flush();
            MixerLinkService.ClaimForUnity(kept);
            NotePreviewed(renderer);
        }

        /// <summary>
        /// A session an undo past its start set aside while its renderer shows the mesh it started from again: editing the
        /// renderer again takes it up, edits and history (Unity's redo of a step recorded before the session gives the
        /// renderer what that step left, never Mixer's working mesh, so the redo alone cannot bring it back).
        /// </summary>
        private static EditTarget ResumeParked(Renderer renderer)
        {
            var shown = MeshOf(renderer);
            foreach (var pair in parkedTargets.ToList())
            {
                var kept = pair.Value;
                if (kept.Renderer != renderer || kept.Working == null || shown != kept.Original) continue;
                parkedTargets.Remove(pair.Key);
                waiting.RemoveAll(r => r.Id == pair.Key);
                kept.Show(kept.Working);
                Resume(kept, renderer);
                SaveState();
                Changed?.Invoke();
                return kept;
            }
            // After a script reload only its record and working mesh are left: the session comes back from its autosave
            // (its history is gone with the reload), with the bind poses Unity's Undo left (it undid the session's start).
            string id = null;
            int rendererId = renderer.GetInstanceID();
            foreach (var r in waiting.Where(w => w.Kind == 'p' && w.Working != 0 && !parkedTargets.ContainsKey(w.Id)).ToList())
            {
                if (r.Renderer != rendererId && r.Id != (id ?? (id = SceneGraphExporter.IdOf(renderer.gameObject)))) continue;
                var original = SceneGraphExporter.Resolve(r.Original) as Mesh;
                var working = EditorUtility.InstanceIDToObject(r.Working) as Mesh;
                if (original == null || working == null || shown != original || !MixerAutosave.Exists(r.Id)) continue;
                waiting.Remove(r);
                var opened = Open(renderer, original, working, r.Layer.Length > 0 ? r.Layer : null, r.Held, boneCorrection: false, out bool restored);
                // The autosave does not fit the mesh any more: a new session starts instead.
                if (!restored) { Revert(opened); return null; }
                ArmatureEdit.SyncFromHolder(opened);
                SaveState();
                Changed?.Invoke();
                return opened;
            }
            return null;
        }

        /// <summary>A session kept whole while it waits (its renderer is shown another mesh by an undo).</summary>
        internal static bool IsParked(EditTarget t) => t != null && parkedTargets.TryGetValue(t.Id, out var p) && p == t;

        // A working mesh some session shows or will show again: one being edited, one set aside by an undo or a delete
        // (its renderer showing it again brings it back), or one a script reload is about to restore. Any other one on
        // its renderer is left from a session that could not come back, and the guard gives the renderer its original.
        private static bool OwnsWorking(Mesh working)
        {
            if (working == null) return false;
            foreach (var t in targets) if (t.Working == working) return true;
            foreach (var kept in parkedTargets.Values) if (kept.Working == working) return true;
            int id = working.GetInstanceID();
            foreach (var r in waiting) if (r.Working == id) return true;
            foreach (string line in pendingRestore)
            {
                var parts = line.Split('\t');
                if (parts.Length > 1 && int.TryParse(parts[1], out int pending) && pending == id) return true;
            }
            return false;
        }

        // A waiting session goes for good: its autosave, and a parked working mesh (what showed it gets the original).
        private static void Drop(Record r, bool deleteAutosave = true)
        {
            waiting.Remove(r);
            // Kept, the autosave is a new session's of the same object: so is the bind-pose holder kept for it.
            if (deleteAutosave) { MixerAutosave.Delete(r.Id); ArmatureEdit.ForgetWaiting(r.Id); }
            if (parkedTargets.TryGetValue(r.Id, out var kept))
            {
                parkedTargets.Remove(r.Id);
                MixerHistory.Forget(kept);
                ArmatureEdit.Forget(kept);
            }
            if (r.Working == 0) return;
            var working = EditorUtility.InstanceIDToObject(r.Working) as Mesh;
            if (working == null || targets.Any(t => t.Working == working)) return;
            MixerMeshGuard.Released(working, SceneGraphExporter.Resolve(r.Original) as Mesh);
            Object.DestroyImmediate(working);
        }

        // ---- State across reloads ------------------------------------------------------------------------------------

        private static void SaveState()
        {
            var lines = new List<string>();
            foreach (var t in targets) if (t.Alive) lines.Add(StateLine(t));
            SessionState.SetString(StateKey, string.Join("\n", lines));
            SessionState.SetString(WaitingKey, string.Join("\n", waiting.Select(r => r.Line)));
            WriteRecoveryFile();
        }

        // What a script reload restores a session from: its id, working mesh, original, layer, held weights, which version
        // of the original it started from (its fingerprint), and the renderer's instance id (instance ids hold
        // across reloads; an object of an unsaved scene may have no id of its own).
        private static string StateLine(EditTarget t) =>
            t.Id + "\t" + t.Working.GetInstanceID() + "\t" + t.OriginalId + "\t" + (t.Document.Layer ?? "") + "\t" + HeldText(t.HeldWeights)
            + "\t" + t.Document.OriginalFingerprint.ToString(CultureInfo.InvariantCulture) + "\t" + t.RendererId.ToString(CultureInfo.InvariantCulture);

        // What a restarted editor can bring back: every session, and those waiting for Play or a closed scene (not the
        // deleted objects', which only Undo in this editor can bring back).
        private static void WriteRecoveryFile()
        {
            try
            {
                var lines = new List<string>();
                long now = DateTime.UtcNow.Ticks;
                foreach (var t in targets) if (t.Alive) { var r = RecordOf(t, 'f'); r.Time = now; lines.Add(r.Line); }
                foreach (var r in waiting) if (r.Kind != 'p') lines.Add(r.Line);
                if (lines.Count == 0 && !File.Exists(RecoveryFile)) return;
                Orbiters.Toolkit.Editor.LiveLink.LiveLinkServer.WriteAtomically(RecoveryFile, string.Join("\n", lines));
            }
            catch (Exception) { }
        }

        private static Record RecordOf(EditTarget t, char kind) => new Record
        {
            Kind = kind, Id = t.Id, Original = t.OriginalId, Layer = t.Document.Layer ?? "", Held = HeldText(t.HeldWeights),
            Renderer = t.RendererId, Time = DateTime.UtcNow.Ticks, Path = PathOf(t.Renderer),
        };

        private static void AddWaiting(Record record)
        {
            foreach (var old in waiting.Where(r => r.Id == record.Id).ToList())
            {
                if (old.Working != 0 && old.Working != record.Working) Drop(old, deleteAutosave: false);
                else waiting.Remove(old);
            }
            waiting.Add(record);
            // Deleted objects' sessions are kept for a while only: the newest few.
            var parked = waiting.Where(r => r.Kind == 'p').OrderByDescending(r => r.Time).Skip(32).ToList();
            foreach (var r in parked) Drop(r);
        }

        // Held weights as "index:weight" pairs (only the held ones), invariant culture.
        private static string HeldText(float[] held)
        {
            if (held == null) return "";
            var pairs = new List<string>();
            for (int i = 0; i < held.Length; i++)
                if (!float.IsNaN(held[i])) pairs.Add(i + ":" + held[i].ToString("R", CultureInfo.InvariantCulture));
            return string.Join(",", pairs);
        }

        private static float[] ParseHeld(string text, int count)
        {
            if (string.IsNullOrEmpty(text) || count <= 0) return null;
            var held = new float[count];
            for (int i = 0; i < count; i++) held[i] = float.NaN;
            foreach (string pair in text.Split(','))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0 && int.TryParse(pair.Substring(0, colon), out int index) && index >= 0 && index < count
                    && float.TryParse(pair.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float weight))
                    held[index] = weight;
            }
            return held;
        }

        private static void LoadState()
        {
            foreach (string line in SessionState.GetString(StateKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                pendingRestore.Add(line);
            foreach (string line in SessionState.GetString(WaitingKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var r = Record.Parse(line);
                if (r != null) waiting.Add(r);
            }
            if (!SessionState.GetBool(BootedKey, false))
            {
                // A new editor: what the last one was editing comes back from the recovery file.
                SessionState.SetBool(BootedKey, true);
                LoadRecoveryFile();
            }
            // After a reload (or at start) every renderer is checked once, after the restore.
            repairAfterRestore = true;
            if (waiting.Count > 0) recoverDue = true;
        }

        private static void LoadRecoveryFile()
        {
            try
            {
                long oldest = (DateTime.UtcNow - RecordLife).Ticks;
                if (File.Exists(RecoveryFile))
                    foreach (string line in File.ReadAllLines(RecoveryFile))
                    {
                        var r = Record.Parse(line);
                        if (r == null) continue;
                        // Instance ids belong to the editor that wrote them: an object found by one now would be another.
                        if (r.Time < oldest || IsInstanceId(r.Id) || IsInstanceId(r.Original)) { MixerAutosave.Delete(r.Id); continue; }
                        r.Kind = 'f';
                        r.Renderer = 0;
                        if (!waiting.Any(w => w.Id == r.Id)) waiting.Add(r);
                    }
                MixerAutosave.CleanUp(waiting.Select(r => r.Id).ToList(), TimeSpan.FromDays(1));
            }
            catch (Exception e) { Debug.LogWarning("[Mixer] The sessions to recover could not be read: " + e.Message); }
        }

        // After a script reload the working meshes still show on their renderers (HideAndDontSave objects survive);
        // the documents are rebuilt from the originals and the autosave, and the working meshes refreshed from them.
        private static void RestorePending()
        {
            restoring = true;
            try
            {
                foreach (string line in pendingRestore)
                {
                    try { RestoreLine(line); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
            finally
            {
                pendingRestore.Clear();
                restoring = false;
                SaveState();
                Changed?.Invoke();
            }
        }

        private static bool IsInstanceId(string id) => id != null && id.StartsWith("InstanceId-", StringComparison.Ordinal);

        private static void RestoreLine(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) return;
            int rendererId = parts.Length > 6 && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instance) ? instance : 0;
            var renderer = RendererOf(parts[0], rendererId, null);
            if (renderer == null || TargetOf(renderer) != null) return;
            int.TryParse(parts[1], out int workingId);
            var working = workingId != 0 ? EditorUtility.InstanceIDToObject(workingId) as Mesh : null;
            var shown = MeshOf(renderer);
            var original = SceneGraphExporter.Resolve(parts[2]) as Mesh;
            if (original == null)
            {
                // Nothing to rebuild the session from. Its working mesh stays shown (no scene saves it); editing the
                // renderer again starts a session on that copy, which a commit then saves.
                if (working != null && shown == working)
                    Debug.LogWarning("[Mixer] The mesh '" + renderer.name + "' was being edited on is gone (an FBX reimported with the mesh renamed or removed?): its uncommitted edits cannot come back. It shows Mixer's working copy, which scenes do not save: edit and commit it in Mixer to keep that, or give the renderer a mesh.");
                return;
            }
            string layer = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            string held = parts.Length > 4 ? parts[4] : "";
            if (shown != working && shown != original)
            {
                // The renderer moved on to another mesh meanwhile: nothing of the session applies to it.
                MixerAutosave.Delete(parts[0]);
                if (working != null) { MixerMeshGuard.Released(working, original); Object.DestroyImmediate(working); }
                return;
            }
            // The working mesh is reused only when it was made from this version of the original (a reimport before the
            // reload, even with as many vertices, is another: its shapes, UVs, triangles and bind poses would mix with the
            // new import's), and only with an autosave it can take (or none): it shows what that autosave brings back.
            if (working != null)
            {
                long fingerprint = MixerAutosave.FingerprintOf(original);
                bool sameVersion = parts.Length > 5 && long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long started) && started == fingerprint
                    && working.vertexCount == original.vertexCount && working.blendShapeCount == original.blendShapeCount;
                if (!sameVersion || (MixerAutosave.Exists(parts[0]) && !MixerAutosave.Fits(parts[0], original.vertexCount, fingerprint)))
                {
                    MixerMeshGuard.Released(working, original);
                    Object.DestroyImmediate(working);
                    working = null;
                }
            }
            Open(renderer, original, working, layer, held, boneCorrection: false, out _);
        }

        /// <summary>A session for the renderer from its autosave: the mesh it had, its working copy (or a new one), the edits, the held weights.</summary>
        private static EditTarget Open(Renderer renderer, Mesh original, Mesh working, string layer, string held, bool boneCorrection, out bool restored)
        {
            // A working mesh kept from before (a reload, a parked session) is reused only when it still fits the original.
            if (working != null && (working.vertexCount != original.vertexCount || working.blendShapeCount != original.blendShapeCount))
            {
                MixerMeshGuard.Released(working, original);
                Object.DestroyImmediate(working);
                working = null;
            }
            if (working == null) working = NewWorking(original);
            var target = new EditTarget(renderer, original, working);
            target.Show(working);
            MixerMeshGuard.Shown(working, original, renderer);
            // The groups beside the mesh first: the autosave, when there is one, holds the session's own.
            MixerSidecar.Load(original, target.Document);
            targets.Add(target);
            restored = MixerAutosave.Restore(target, extendBones: boneCorrection);
            if (restored) target.Document.Layer = layer != null && target.Document.HasShape(layer) ? layer : null;
            target.FlushBindposes();
            target.FlushWeights(force: true);
            // The renderer shows 0 for the shape being edited: its own weight comes from before.
            target.HoldLayerWeight(ParseHeld(held, working.blendShapeCount));
            target.AfterLayerChange();
            target.Flush();
            MixerLinkService.ClaimForUnity(target);
            NotePreviewed(renderer);
            return target;
        }

        // ---- Sessions that wait --------------------------------------------------------------------------------------

        // Brings back the waiting sessions whose renderer is there again and shows the mesh the session started from.
        private static bool recovering;

        private static void TryRecover()
        {
            if (recovering) return;
            recovering = true;
            try { Recover(); }
            finally { recovering = false; }
        }

        private static void Recover()
        {
            var back = new List<(EditTarget target, bool correct)>();
            // Sessions a redo gave their renderer back: nothing to say, they just go on.
            var resumed = new List<EditTarget>();
            long oldest = (DateTime.UtcNow - RecordLife).Ticks;
            restoring = true;
            try
            {
                foreach (var r in waiting.ToList())
                {
                    try
                    {
                        var renderer = RendererOf(r.Id, r.Renderer, r.Path);
                        if (renderer == null || !CanEdit(renderer, out _))
                        {
                            if (r.Time < oldest) Drop(r);
                            continue;
                        }
                        var original = SceneGraphExporter.Resolve(r.Original) as Mesh;
                        var other = TargetOf(renderer);
                        // Edited again meanwhile, or nothing left to bring back: the record goes.
                        if (other != null || original == null || !MixerAutosave.Exists(r.Id))
                        {
                            Drop(r, deleteAutosave: other == null || other.Id != r.Id);
                            continue;
                        }
                        // A parked session comes back when its renderer shows its working mesh again (Undo of the delete,
                        // Redo past its start); any other comes back on the mesh it started from (Play, a scene file).
                        var parked = r.Working != 0 ? EditorUtility.InstanceIDToObject(r.Working) as Mesh : null;
                        var shown = MeshOf(renderer);
                        if (parked != null ? shown != parked : shown != original)
                        {
                            if (r.Time < oldest) Drop(r);
                            continue;
                        }
                        waiting.Remove(r);
                        if (parkedTargets.TryGetValue(r.Id, out var kept) && kept.Renderer == renderer && kept.Working == parked && parked != null)
                        {
                            // The same session, history and all.
                            parkedTargets.Remove(r.Id);
                            Resume(kept, renderer);
                            resumed.Add(kept);
                            continue;
                        }
                        if (parkedTargets.TryGetValue(r.Id, out var stale)) { parkedTargets.Remove(r.Id); MixerHistory.Forget(stale); ArmatureEdit.Forget(stale); }
                        // Only Play, a scene file and a crash leave the bones placed for the original bind poses.
                        bool correct = r.Kind != 'p';
                        var opened = Open(renderer, original, parked, r.Layer.Length > 0 ? r.Layer : null, r.Held, correct, out bool restored);
                        // The autosave does not fit the mesh any more (reimported): there is nothing to bring back.
                        if (!restored) { Revert(opened); continue; }
                        back.Add((opened, correct));
                    }
                    catch (Exception e) { Debug.LogException(e); }
                }
                // Bones the sessions' rest-pose edits moved go back where their bind poses want them, each bone once.
                var place = RestPosePlacement.AsEdited(RestPosePlacement.RigsOf(back.Where(b => b.correct).Select(b => b.target)));
                if (place.Count > 0)
                {
                    RestPosePlacement.Apply(place);
                    foreach (var (t, _) in back) t.RefreshWorld();
                }
            }
            finally
            {
                restoring = false;
                SaveState();
            }
            if (resumed.Count > 0 && back.Count == 0) Changed?.Invoke();
            if (back.Count == 0) return;
            Say("Mixer brought back the edits of " + string.Join(", ", back.Select(b => b.target.Name)) + " that were not committed (commit or revert them).", false);
            Changed?.Invoke();
        }

        // A renderer by the session's object id, else its instance id (an undone delete), else its place in the hierarchy.
        private static Renderer RendererOf(string id, int instanceId, string path)
        {
            var resolved = SceneGraphExporter.Resolve(id);
            var renderer = resolved is GameObject go ? go.GetComponent<Renderer>() : resolved as Renderer;
            if (renderer == null && instanceId != 0) renderer = EditorUtility.InstanceIDToObject(instanceId) as Renderer;
            if (renderer == null && !string.IsNullOrEmpty(path)) renderer = FindByPath(path);
            return renderer;
        }

        private static string PathOf(Renderer renderer)
        {
            if (renderer == null) return "";
            var names = new List<string>();
            for (var t = renderer.transform; t != null; t = t.parent) names.Insert(0, t.name);
            return renderer.gameObject.scene.path + "|" + string.Join("/", names);
        }

        private static Renderer FindByPath(string path)
        {
            int bar = path.IndexOf('|');
            if (bar < 0) return null;
            string scenePath = path.Substring(0, bar);
            var names = path.Substring(bar + 1).Split('/');
            if (names.Length == 0 || names[0].Length == 0) return null;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || scene.path != scenePath) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != names[0]) continue;
                    var t = root.transform;
                    for (int i = 1; i < names.Length && t != null; i++) t = t.Find(names[i]);
                    if (t != null && t.GetComponent<Renderer>() is Renderer r) return r;
                }
            }
            return null;
        }

        // Every session of the scene (or all) waits with its autosave; with prepareScene the objects are left as a scene
        // save leaves them (Play copies the scene as it is).
        private static void Suspend(List<EditTarget> list, bool prepareScene)
        {
            if (list.Count == 0) return;
            if (prepareScene)
            {
                try { RestPosePlacement.Apply(RestPosePlacement.AsOriginal(RestPosePlacement.RigsOf(list))); }
                catch (Exception e) { Debug.LogException(e); }
            }
            foreach (var t in list)
            {
                try
                {
                    bool keep = NeedsAutosave(t);
                    if (keep) MixerAutosave.WriteNow(t);
                    if (prepareScene && t.Alive)
                    {
                        t.ShowChannel(WeightChannel.None);
                        t.RestoreWeights();
                        t.Show(t.Original);
                        TrimAddedBones(t);
                        NotePreviewed(t.Renderer);
                    }
                    if (keep) AddWaiting(RecordOf(t, 's'));
                    // Unity's undo goes on across Play (a bone move from before can be undone after it), not across closing
                    // a scene: only Play's sessions keep their bind-pose holder.
                    EndSession(t, t.Original, deleteAutosave: !keep, notify: false, keepHolder: keep && prepareScene);
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            Changed?.Invoke();
        }

        private static void TrimAddedBones(EditTarget t)
        {
            var skinned = t.Skinned;
            int n = t.Document.OriginalBoneCount;
            if (skinned != null && n > 0 && skinned.bones.Length > n) skinned.bones = skinned.bones.Take(n).ToArray();
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                // Play never gets Weight Paint's pose (nor a rest-pose placement worked out from posed bones below).
                BoneSubSelection.PutBackPosed();
                if (targets.Count == 0) return;
                var list = targets.Where(t => t.Alive).ToList();
                Suspend(list, prepareScene: true);
                Say("Play shows the meshes as committed; Mixer's edits come back when Play stops.", false);
            }
            else if (change == PlayModeStateChange.EnteredEditMode) recoverDue = true;
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (targets.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode) return;
            Suspend(targets.Where(t => t.Alive && t.Renderer.gameObject.scene == scene).ToList(), prepareScene: false);
        }

        private static void BeforeReload()
        {
            // Everything since the last autosave, whatever the 3 s throttle or a stroke in progress said.
            FlushAutosavesNow();
            SaveState();
        }

        private static void BeforeQuit()
        {
            FlushAutosavesNow();
            WriteRecoveryFile();
        }

        private static void OnUndoRedo()
        {
            // With a deleted object's session waiting, the object may be back under an undo: every renderer is checked.
            MixerMeshGuard.Repair(everywhere: waiting.Any(r => r.Kind == 'p'));
            // An undo went past the start of a session (its renderer shows the mesh it had before): the session waits,
            // and a redo brings it back.
            foreach (var t in targets.ToList()) if (t.Alive && stashes.Count == 0 && t.Shown != t.Working) Gone(t);
            // A parked session's renderer may show its working mesh again (a redo): it comes back.
            // An undone delete, or a redo, may have brought back what a waiting session shows: it comes back now.
            if (waiting.Count > 0) TryRecover();
        }

        private static void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (targets.Count == 0) return;
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.CreateGameObjectHierarchy) continue;
                stream.GetCreateGameObjectHierarchyEvent(i, out var data);
                createdObjects.Add(data.instanceId);
            }
        }

        // ---- Copies of edited objects ---------------------------------------------------------------------------------

        // A renderer that shows another session's working mesh is a copy of an edited object (Ctrl+D, paste): it gets
        // a working mesh and a session of its own with the same edits, so neither changes the other and both save.
        private static bool AdoptCopy(Renderer renderer, Mesh working)
        {
            if (renderer == null || working == null) return false;
            if (TargetOf(renderer) != null) return true;
            var source = targets.FirstOrDefault(t => t.Working == working);
            // A waiting session's working mesh: its own renderer showing it (Undo of the delete, a redo) brings that session
            // back (Recover), as it is. Another object showing it (pasted after a Cut) is not the session's: the guard
            // gives it the original rather than a mesh no scene can save.
            if (source == null)
            {
                int id = working.GetInstanceID(), rendererId = renderer.GetInstanceID();
                string path = PathOf(renderer);
                return waiting.Any(r => r.Working == id && (r.Renderer == rendererId || (!string.IsNullOrEmpty(r.Path) && r.Path == path)));
            }
            if (!source.Alive || source.Renderer == renderer) return false;
            if (EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetPrefabStage(renderer.gameObject) != null) return false;
            var copy = NewWorking(source.Working);
            var target = new EditTarget(renderer, source.Original, copy);
            target.Show(copy);
            MixerMeshGuard.Shown(copy, source.Original, renderer);
            MixerSidecar.Load(source.Original, target.Document);
            if (!MixerAutosave.Copy(source, target)) Debug.LogWarning("[Mixer] The edits of '" + source.Name + "' could not be carried to its copy '" + renderer.name + "'.");
            target.FlushBindposes();
            target.FlushWeights(force: true);
            target.HoldLayerWeight(source.HeldWeights);
            target.AfterLayerChange();
            target.Flush();
            targets.Add(target);
            dirtySinceAutosave = true;
            SaveState();
            MixerLinkService.ClaimForUnity(target);
            NotePreviewed(renderer);
            Say("The copy " + renderer.name + " keeps " + source.Name + "'s Mixer edits in a session of its own.", false);
            Changed?.Invoke();
            return true;
        }

        // ---- Scene saves -------------------------------------------------------------------------------------------

        // The scene file gets the originals: meshes, the renderers' own weights of the shapes being edited (held at 0
        // while shown), bone lists without added leaf bones, and bones where the original bind poses want them.
        private static void OnSceneSaving(Scene scene, string path)
        {
            MixerMeshGuard.Repair(everywhere: true);
            // A renderer that shows a parked session's working mesh again (a redo this frame) is a session again first.
            if (waiting.Count > 0) TryRecover();
            // Only renderers that show their working mesh: one another tool gave another mesh keeps it.
            var list = targets.Where(t => t.Alive && t.Renderer.gameObject.scene == scene && t.Shown == t.Working).ToList();
            var unposed = BoneSubSelection.Unposed(bone => bone.gameObject.scene == scene);
            if (list.Count == 0 && unposed.Count == 0) return;
            RestoreStash(scene.handle);
            stashes[scene.handle] = Stash(list, unposed);
            // A save that fails never sends sceneSaved: the scene shows the edits again on the next frame anyway.
            int handle = scene.handle;
            EditorApplication.CallbackFunction once = null;
            once = () => { EditorApplication.update -= once; RestoreStash(handle); };
            EditorApplication.update += once;
        }

        // The targets as a scene file must hold them, and the bones Weight Paint posed as they were before, with what puts
        // them back (RestoreStash).
        private static SaveStash Stash(List<EditTarget> list, List<RestPosePlacement.Pose> unposed)
        {
            var stash = new SaveStash();
            try
            {
                // The pose first: the rest-pose placement is worked out from the bones as they rest.
                if (unposed.Count > 0)
                {
                    stash.Posed = unposed.Select(p => new RestPosePlacement.Pose { Bone = p.Bone, Position = p.Bone.localPosition, Rotation = p.Bone.localRotation, Scale = p.Bone.localScale }).ToList();
                    RestPosePlacement.Restore(unposed);
                }
                stash.Poses = RestPosePlacement.Apply(RestPosePlacement.AsOriginal(RestPosePlacement.RigsOf(list)));
                foreach (var t in list)
                {
                    t.Show(t.Original);
                    t.ReleaseLayerWeight();
                    stash.Shown.Add(t);
                    var skinned = t.Skinned;
                    int n = t.Document.OriginalBoneCount;
                    if (skinned != null && n > 0 && skinned.bones.Length > n) { stash.Bones.Add((skinned, skinned.bones)); skinned.bones = skinned.bones.Take(n).ToArray(); }
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            return stash;
        }

        private static void RestoreStash(int sceneHandle)
        {
            if (!stashes.TryGetValue(sceneHandle, out var stash)) return;
            stashes.Remove(sceneHandle);
            try
            {
                foreach (var (renderer, bones) in stash.Bones) if (renderer != null) renderer.bones = bones;
                RestPosePlacement.Restore(stash.Poses);
                RestPosePlacement.Restore(stash.Posed);
                foreach (var t in stash.Shown) if (t.Alive && targets.Contains(t)) { t.Show(t.Working); t.HoldLayerWeight(); }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>For tests: what a save of <paramref name="scene"/> does before and after writing the file.</summary>
        internal static void SimulateSceneSave(Scene scene, Action whileSaving)
        {
            OnSceneSaving(scene, scene.path);
            try { whileSaving?.Invoke(); }
            finally { RestoreStash(scene.handle); }
        }

        /// <summary>For tests: what Play starting (the sessions of <paramref name="scene"/> wait) and stopping (they come back) does.</summary>
        internal static void SimulatePlay(Scene scene, Action whilePlaying)
        {
            Suspend(targets.Where(t => t.Alive && t.Renderer.gameObject.scene == scene).ToList(), prepareScene: true);
            try { whilePlaying?.Invoke(); }
            finally { OnPlayModeChanged(PlayModeStateChange.EnteredEditMode); TryRecover(); }
        }

        /// <summary>For tests: brings back the waiting sessions now (what the next frame does after an Undo or a scene opening).</summary>
        internal static void RecoverNow() => TryRecover();

        /// <summary>
        /// For tests: what a script reload does to the sessions of <paramref name="scene"/>. They are written as before a
        /// reload, then dropped from memory with their history (their working meshes and bind-pose holders stay, as
        /// HideAndDontSave objects do, and so do the records kept in SessionState), and rebuilt as after a reload;
        /// <paramref name="whileReloading"/> runs in between (a reimport).
        /// </summary>
        internal static void SimulateReload(Scene scene, Action whileReloading = null)
        {
            BeforeReload();
            bool InScene(EditTarget t) => t.Renderer != null && t.Renderer.gameObject.scene == scene;
            var lines = new List<string>();
            foreach (var t in targets.Where(InScene).ToList())
            {
                if (t.Alive) lines.Add(StateLine(t));
                targets.Remove(t);
                ArmatureEdit.DropForReload(t);
                MixerHistory.Forget(t);
                MixerLinkService.ReleaseForUnity(t);
            }
            foreach (var pair in parkedTargets.Where(p => InScene(p.Value)).ToList())
            {
                parkedTargets.Remove(pair.Key);
                MixerHistory.Forget(pair.Value);
                ArmatureEdit.DropForReload(pair.Value);
            }
            whileReloading?.Invoke();
            foreach (string line in lines) pendingRestore.Add(line);
            repairAfterRestore = true;
            if (waiting.Count > 0) recoverDue = true;
            Tick();
        }

        // ---- Prefabs -------------------------------------------------------------------------------------------------

        /// <summary>A prefab is being written: once imported, the renderers of edited instances in it get their original meshes.</summary>
        internal static void WatchPrefab(string path)
        {
            if (targets.Count == 0 || string.IsNullOrEmpty(path)) return;
            watchedPrefabs[path] = EditorApplication.timeSinceStartup;
        }

        // Apply Overrides (or a new prefab dragged from an edited object) writes the prefab from the instance, which shows
        // a working mesh that is never saved: the prefab would hold no mesh. Its renderer gets the original instead.
        private static void CheckPrefabs()
        {
            double now = EditorApplication.timeSinceStartup;
            foreach (var path in watchedPrefabs.Where(p => now - p.Value > 0.2).Select(p => p.Key).ToList())
            {
                watchedPrefabs.Remove(path);
                try
                {
                    var roots = new HashSet<GameObject>();
                    foreach (var t in targets)
                    {
                        if (!t.Alive || !PrefabUtility.IsPartOfPrefabInstance(t.Renderer)) continue;
                        Object holder = t.Filter != null ? (Object)t.Filter : t.Renderer;
                        var source = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(holder, path);
                        if (source == null) continue;
                        var mesh = source is MeshFilter f ? f.sharedMesh : source is SkinnedMeshRenderer s ? s.sharedMesh : null;
                        if (mesh == t.Original || (mesh != null && !MixerMeshGuard.IsWorking(mesh))) continue;
                        if (source is MeshFilter sf) sf.sharedMesh = t.Original; else if (source is SkinnedMeshRenderer ss) ss.sharedMesh = t.Original;
                        EditorUtility.SetDirty(source);
                        var go = source is Component c ? c.transform.root.gameObject : null;
                        if (go != null) roots.Add(go);
                        Debug.LogWarning("[Mixer] '" + path + "' was written from '" + t.Name + "' while Mixer edits it: the prefab keeps '" + t.Original.name + "' (commit in Mixer, then apply, to put the edits in the prefab).");
                    }
                    foreach (var root in roots) PrefabUtility.SavePrefabAsset(root);
                }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // ---- Builds and exports -------------------------------------------------------------------------------------

        /// <summary>
        /// Makes a copy of an avatar (an upload's build copy, an export's) carry nothing of Mixer: original meshes with
        /// the renderers' own weights, bone lists without the leaf bones a session added, bones where the original
        /// bind poses want them, and the bones Weight Paint posed as they were. The scene's own objects are not touched.
        /// </summary>
        internal static void PrepareCopyForExport(GameObject root)
        {
            if (root == null) return;
            if (waiting.Count > 0) TryRecover();
            // Not a copy: the scene's own avatar is being built. It gets what a scene save gets, and the edits show again
            // on the next frame, so the sessions are not broken by it.
            var own = targets.Where(t => t.Alive && t.Shown == t.Working && t.Renderer.transform.IsChildOf(root.transform)).ToList();
            var ownPosed = BoneSubSelection.Unposed(bone => bone.IsChildOf(root.transform));
            if (own.Count > 0 || ownPosed.Count > 0)
            {
                int key = int.MinValue + (root.GetInstanceID() & 0x3fffffff);
                RestoreStash(key);
                stashes[key] = Stash(own, ownPosed);
                EditorApplication.CallbackFunction once = null;
                once = () => { EditorApplication.update -= once; RestoreStash(key); };
                EditorApplication.update += once;
            }
            // A copy of bones Weight Paint posed gets them as they were, before the placement below is worked out.
            foreach (var pose in BoneSubSelection.Unposed(bone => !bone.IsChildOf(root.transform)))
            {
                var copy = CopyOf(pose.Bone, root.transform);
                if (copy == null) continue;
                copy.localPosition = pose.Position;
                copy.localRotation = pose.Rotation;
                copy.localScale = pose.Scale;
            }
            var rigs = new List<RestPosePlacement.Rig>();
            var trims = new List<(SkinnedMeshRenderer renderer, int count)>();
            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var working = skinned.sharedMesh;
                var original = MixerMeshGuard.OriginalOf(working);
                if (original == null) continue;
                var target = targets.FirstOrDefault(t => t.Working == working);
                // The shape being edited shows at 0 on the renderer (its edits are in the working vertices): the copy
                // gets the renderer's own weight for it.
                var held = target?.HeldWeights;
                if (held != null)
                    for (int i = 0; i < held.Length && i < original.blendShapeCount; i++)
                        if (!float.IsNaN(held[i])) skinned.SetBlendShapeWeight(i, held[i]);
                if (target != null && target.Document.HasSkin)
                {
                    var doc = target.Document;
                    if (doc.BindPosesEdited) rigs.Add(new RestPosePlacement.Rig { Bones = skinned.bones, Edited = doc.BindPoses, Original = doc.OriginalBindPoses });
                    if (doc.OriginalBoneCount > 0 && skinned.bones.Length > doc.OriginalBoneCount) trims.Add((skinned, doc.OriginalBoneCount));
                }
                skinned.sharedMesh = original;
            }
            RestPosePlacement.Apply(RestPosePlacement.AsOriginal(rigs));
            foreach (var (renderer, count) in trims) renderer.bones = renderer.bones.Take(count).ToArray();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var original = MixerMeshGuard.OriginalOf(filter.sharedMesh);
                if (original != null) filter.sharedMesh = original;
            }
        }

        // The object of a copy (under copyRoot) that stands for a scene bone: at the bone's path under one of its parents,
        // and posed exactly as the bone is (a copy made just now).
        private static Transform CopyOf(Transform bone, Transform copyRoot)
        {
            string path = bone.name;
            for (var above = bone.parent; above != null; above = above.parent)
            {
                var found = copyRoot.Find(path);
                if (found != null && found != bone && found.localPosition == bone.localPosition && found.localRotation == bone.localRotation && found.localScale == bone.localScale) return found;
                path = above.name + "/" + path;
            }
            return null;
        }
    }
}
