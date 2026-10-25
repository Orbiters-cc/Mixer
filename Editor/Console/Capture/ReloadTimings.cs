using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Measures every script reload. Unity writes a "Domain Reload Profiling" tree to Editor.log after each reload;
    /// with its <c>EnableDomainReloadTimings</c> diagnostic switch on (the Mixer console turns it on only for the reload
    /// <see cref="ByPackage"/> asks for) the tree lists each type and method that runs on load, each before/after-reload
    /// callback and each window restored. The tree is read back after the reload, every entry is given to the package
    /// its code belongs to, and the result is logged as a timing log (or handed to the Play Mode timing when the reload
    /// was part of entering Play Mode). In Unity 2022.3 that switch leaks Unity's temporary memory at every reload, and
    /// Unity then writes every leaked block to Editor.log again and again, so it is on for one reload at a time.
    /// </summary>
    [InitializeOnLoad]
    internal static class ReloadTimings
    {
        internal const string EnabledPref = "Orbiters.Logger.MeasureReloads";
        // Session state, not a preference: a request left on would turn the leaking switch on again at every start.
        private const string ByPackageKey = "Orbiters.Logger.MeasureNextReloadByPackage";
        // Set when a reload starts with the switch on: that reload is the one measured by package.
        private const string MeasuringKey = "Orbiters.Logger.ReloadTimings.MeasuringByPackage";
        private const string OwnedSwitchPref = "Orbiters.Logger.OwnsReloadTimingsSwitch";
        private const string SwitchName = "EnableDomainReloadTimings";
        private const string OffsetKey = "Orbiters.Logger.ReloadTimings.Offset";
        private const string StartKey = "Orbiters.Logger.ReloadTimings.Start";
        private const string Header = "Domain Reload Profiling: ";
        private const double WaitSeconds = 60d;
        private const int MaxScanBytes = 32 * 1024 * 1024;

        private static readonly Regex LinePattern = new Regex(
            @"^(?<name>.*?)(?:: (?<ms>\d+(?:\.\d+)?)ms(?: in (?<count>\d+) occurrences)?| \((?<ms2>\d+(?:\.\d+)?)ms\))$",
            RegexOptions.CultureInvariant);

        private static readonly Regex ScriptObject = new Regex(@"^[A-Za-z]+\((?<type>[^,()]+)(?:, (?<assembly>[^,()]+))?", RegexOptions.CultureInvariant);

        private static double deadline;
        private static double nextPoll;
        private static int busy;
        private static string foundBlock;
        private static long foundEnd;
        private static TimingProfile measured;

        /// <summary>
        /// Takes a measured reload instead of logging it on its own (entering Play Mode includes its reload). Returns
        /// true when it took it.
        /// </summary>
        internal static Func<TimingProfile, long, bool> Claim;

        static ReloadTimings()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                SessionState.SetString(StartKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                if (Enabled && ByPackage)
                {
                    SessionState.SetBool(MeasuringKey, true);
                }
            };
            if (!Enabled)
            {
                return;
            }

            ApplySwitch();
            // This reload's tree is written once it ends, so after the log's current end. Never from where the last tree
            // ended: a tree left unread would pass for this one, and after more than a scan of log (a flood of messages)
            // every later reload went unmeasured while the same part of the log was read again and again.
            SessionState.SetString(OffsetKey, LogLength().ToString(CultureInfo.InvariantCulture));

            deadline = EditorApplication.timeSinceStartup + WaitSeconds;
            EditorApplication.update += Poll;
            if (ByPackage)
            {
                EditorApplication.update += HideUnityNotice;
            }
        }

        private const string NoticeStart = "Diagnostic switches are active";

        /// <summary>
        /// Unity's sticky "Diagnostic switches are active" error when the only switch on is the one reloads are measured
        /// with: expected while measuring, so neither Unity's console nor the Mixer console shows it.
        /// </summary>
        internal static bool IsOwnNotice(string condition)
        {
            if (string.IsNullOrEmpty(condition) || !condition.StartsWith(NoticeStart, StringComparison.Ordinal) || !ByPackage)
            {
                return false;
            }

            string[] lines = condition.Split('\n');
            bool listed = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.StartsWith(SwitchName + ":", StringComparison.Ordinal))
                {
                    return false;
                }

                listed = true;
            }

            return listed;
        }

        // Unity logs the notice again when its notice object wakes up after each reload: removed once the reload is over.
        private static void HideUnityNotice()
        {
            EditorApplication.update -= HideUnityNotice;
            try
            {
                var notice = typeof(EditorWindow).Assembly.GetType("UnityEditor.DiagnosticSwitchesConsoleMessage") ??
                             Type.GetType("UnityEditor.DiagnosticSwitchesConsoleMessage, UnityEditor.DiagnosticsModule");
                var instance = notice?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as UnityEngine.Object;
                var switches = typeof(Debug).GetProperty("diagnosticSwitches", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Array;
                var remove = typeof(Debug).GetMethod("RemoveLogEntriesByIdentifier", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (instance == null || switches == null || remove == null)
                {
                    return;
                }

                foreach (var entry in switches)
                {
                    var type = entry.GetType();
                    bool isDefault = type.GetProperty("isSetToDefault", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry) as bool? ?? true;
                    string name = type.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry) as string;
                    if (!isDefault && name != SwitchName)
                    {
                        // Another switch is on too: Unity's notice is about it as well, so it stays.
                        return;
                    }
                }

                remove.Invoke(null, new object[] { instance.GetInstanceID() });
            }
            catch (Exception)
            {
                // The notice stays in Unity's console.
            }
        }

        /// <summary>Whether reloads are measured. On by default.</summary>
        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                {
                    SessionState.SetString(OffsetKey, LogLength().ToString(CultureInfo.InvariantCulture));
                }

                ApplySwitch();
            }
        }

        /// <summary>
        /// Whether each package's part of the next reload is read too, which needs Unity's diagnostic switch on. Turns
        /// itself off once that reload is read, and lasts this editor session at most: the switch leaks Unity's
        /// temporary memory at every reload.
        /// </summary>
        internal static bool ByPackage
        {
            get => SessionState.GetBool(ByPackageKey, false);
            set
            {
                SessionState.SetBool(ByPackageKey, value);
                // Asked again while a measured reload is still being read: that reading doesn't end the new request.
                SessionState.EraseBool(MeasuringKey);
                ApplySwitch();
            }
        }

        // The switch is on exactly while a reload is to be measured by package; a switch the Mixer console turned on is turned back off.
        private static void ApplySwitch()
        {
            if (Enabled && ByPackage)
            {
                SetUnitySwitch(true);
            }
            else if (EditorPrefs.GetBool(OwnedSwitchPref, false))
            {
                SetUnitySwitch(false);
                EditorPrefs.DeleteKey(OwnedSwitchPref);
            }
        }

        /// <summary>Whether Unity reports each package's part (its diagnostic switch is on).</summary>
        internal static bool Detailed => GetUnitySwitch() == true;

        internal static string LogPath
        {
            get
            {
                try
                {
                    return Application.consoleLogPath;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        internal static long LogLength()
        {
            try
            {
                string path = LogPath;
                return string.IsNullOrEmpty(path) || !File.Exists(path) ? 0L : new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        private static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll)
            {
                return;
            }

            nextPoll = now + 0.4d;
            try
            {
                if (measured != null)
                {
                    Publish(measured);
                    measured = null;
                    Stop();
                    return;
                }

                if (foundBlock != null)
                {
                    // Sources come from the compilation pipeline and the type cache: main thread.
                    string block = foundBlock;
                    foundBlock = null;
                    SessionState.SetString(OffsetKey, foundEnd.ToString(CultureInfo.InvariantCulture));
                    var sources = AssemblySources.Take();
                    Run(() =>
                    {
                        var profile = Parse(block, sources);
                        if (profile != null)
                        {
                            measured = profile;
                        }
                    });
                    return;
                }

                if (now > deadline)
                {
                    Stop();
                    return;
                }

                if (Volatile.Read(ref busy) != 0)
                {
                    return;
                }

                string path = LogPath;
                if (!long.TryParse(SessionState.GetString(OffsetKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
                {
                    offset = 0L;
                }

                long length = LogLength();
                if (string.IsNullOrEmpty(path) || length <= offset + Header.Length)
                {
                    if (length < offset)
                    {
                        // A new Editor.log (the editor restarted it): start over at its end.
                        SessionState.SetString(OffsetKey, length.ToString(CultureInfo.InvariantCulture));
                    }

                    return;
                }

                Run(() =>
                {
                    if (TryFindBlock(path, offset, length, out string block, out long end))
                    {
                        foundEnd = end;
                        foundBlock = block;
                    }
                });
            }
            catch (Exception)
            {
                Stop();
            }
        }

        private static void Stop()
        {
            EditorApplication.update -= Poll;
            if (SessionState.GetBool(MeasuringKey, false))
            {
                // The reload measured by package is read (or never came): Unity's switch goes back off.
                ByPackage = false;
            }
        }

        private static void Run(Action work)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception)
                {
                    // A log that can't be read leaves this reload unmeasured.
                }
                finally
                {
                    Volatile.Write(ref busy, 0);
                }
            });
        }

        private static void Publish(TimingProfile profile)
        {
            long start = long.TryParse(SessionState.GetString(StartKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ? ticks : 0L;
            if (Claim != null && Claim(profile, start))
            {
                return;
            }

            LogCapture.Inject(profile.Message(), profile.Format(), LogType.Log);
        }

        /// <summary>The first complete profiling tree written after <paramref name="offset"/>, and where it ends.</summary>
        internal static bool TryFindBlock(string path, long offset, long length, out string block, out long end)
        {
            block = null;
            end = offset;
            int count = (int)Math.Min(MaxScanBytes, length - offset);
            var buffer = new byte[count];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                int read = 0;
                while (read < count)
                {
                    int n = stream.Read(buffer, read, count - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                count = read;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, count);
            int start = text.IndexOf(Header, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            // The tree ends at the first line that is not indented; until that line is written, it may be partial.
            int lineEnd = text.IndexOf('\n', start);
            while (lineEnd >= 0 && lineEnd + 1 < text.Length)
            {
                char next = text[lineEnd + 1];
                if (next != '\t' && next != ' ')
                {
                    block = text.Substring(start, lineEnd - start);
                    end = offset + Encoding.UTF8.GetByteCount(text.Substring(0, lineEnd));
                    return true;
                }

                lineEnd = text.IndexOf('\n', lineEnd + 1);
            }

            return false;
        }

        private sealed class Node
        {
            public string Name;
            public double Milliseconds;
            public int Count;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public double Claimed;
            public bool Attributed;
        }

        /// <summary>Reads a "Domain Reload Profiling" tree into a profile, each entry given to its package.</summary>
        internal static TimingProfile Parse(string block, AssemblySources sources)
        {
            if (string.IsNullOrEmpty(block) || !block.StartsWith(Header, StringComparison.Ordinal))
            {
                return null;
            }

            string[] lines = block.Split('\n');
            string totalText = lines[0].Substring(Header.Length).Trim();
            if (totalText.EndsWith("ms", StringComparison.Ordinal))
            {
                totalText = totalText.Substring(0, totalText.Length - 2);
            }

            if (!double.TryParse(totalText, NumberStyles.Float, CultureInfo.InvariantCulture, out double total))
            {
                return null;
            }

            var root = new Node { Name = string.Empty };
            var path = new List<Node> { root };
            for (int i = 1; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd('\r');
                int depth = 0;
                while (depth < raw.Length && raw[depth] == '\t')
                {
                    depth++;
                }

                if (depth == 0)
                {
                    continue;
                }

                var match = LinePattern.Match(raw.Substring(depth));
                if (!match.Success)
                {
                    continue;
                }

                string ms = match.Groups["ms"].Success ? match.Groups["ms"].Value : match.Groups["ms2"].Value;
                var node = new Node
                {
                    Name = match.Groups["name"].Value.Trim(),
                    Milliseconds = double.Parse(ms, CultureInfo.InvariantCulture),
                    Count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 1
                };
                while (path.Count > depth)
                {
                    path.RemoveAt(path.Count - 1);
                }

                node.Parent = path[path.Count - 1];
                node.Parent.Children.Add(node);
                path.Add(node);
            }

            var profile = new TimingProfile { Kind = TimingKind.ScriptReload, TotalMilliseconds = total };
            sources?.Index();
            Attribute(root, profile, sources);
            AddEngineParts(root, profile);
            return profile;
        }

        // Entries that belong to a package (startup code, callbacks, restored windows) become parts; their children
        // are already in their time.
        private static void Attribute(Node node, TimingProfile profile, AssemblySources sources)
        {
            foreach (var child in node.Children)
            {
                var part = PartFor(child, node, sources);
                if (part.HasValue)
                {
                    child.Attributed = true;
                    if (part.Value.Milliseconds >= 1d)
                    {
                        profile.Parts.Add(part.Value);
                    }

                    for (var up = child.Parent; up != null; up = up.Parent)
                    {
                        up.Claimed += child.Milliseconds;
                    }

                    continue;
                }

                Attribute(child, profile, sources);
            }
        }

        private static TimingPart? PartFor(Node node, Node parent, AssemblySources sources)
        {
            string name = node.Name;
            const string startup = "InitializeOnLoad ";
            if (name.StartsWith(startup, StringComparison.Ordinal))
            {
                string what = name.Substring(startup.Length).Trim();
                if (parent.Name == "ProcessInitializeOnLoadMethodAttributes")
                {
                    // Its child names the method in full: ProcessInitializeOnLoadMethodAttribute(VF.Hooks.VFInitHook::Init).
                    string full = what;
                    foreach (var child in node.Children)
                    {
                        int open = child.Name.IndexOf('(');
                        int close = child.Name.LastIndexOf(')');
                        if (child.Name.StartsWith("ProcessInitializeOnLoadMethodAttribute(", StringComparison.Ordinal) && close > open)
                        {
                            full = child.Name.Substring(open + 1, close - open - 1);
                            break;
                        }
                    }

                    int method = full.IndexOf("::", StringComparison.Ordinal);
                    string type = method > 0 ? full.Substring(0, method) : full;
                    return new TimingPart(Source(sources, s => s.ForTypeName(type), type), "Startup methods", what, node.Milliseconds);
                }

                return new TimingPart(Source(sources, s => s.ForStartupType(what), what), "Startup code", what, node.Milliseconds);
            }

            foreach (var (prefix, phase) in new[]
                     {
                         ("AssemblyReloadEvents.beforeAssemblyReload: ", "Before the reload"),
                         ("AssemblyReloadEvents.afterAssemblyReload: ", "After the reload")
                     })
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string handler = name.Substring(prefix.Length).Trim();
                const string callback = "callback in ";
                string type = handler.StartsWith(callback, StringComparison.Ordinal) ? handler.Substring(callback.Length) : handler;
                string label = handler.StartsWith(callback, StringComparison.Ordinal) ? Short(type) + " (callback)" : ShortMethod(handler);
                return new TimingPart(Source(sources, s => s.ForTypeName(type), type), phase, Times(label, node.Count), node.Milliseconds);
            }

            foreach (var (prefix, phase) in new[]
                     {
                         ("AwakeInstanceAfterBackupRestoration(", "Windows restored"),
                         ("RestoringBackedupData(", "Editor state restored"),
                         ("RebuildManagedInstance(", "Editor state restored")
                     })
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var match = ScriptObject.Match(name);
                string type = match.Success ? match.Groups["type"].Value.Trim() : string.Empty;
                string assembly = match.Success && match.Groups["assembly"].Success ? match.Groups["assembly"].Value.Trim() : string.Empty;
                if (type.Length == 0 || type == "<none>")
                {
                    return new TimingPart(TimingProfile.UnitySource, phase, "Unnamed objects", node.Milliseconds);
                }

                string source = sources != null ? sources.ForType(type, assembly) : SourceCatalog.NameForNamespace(type) ?? TimingProfile.UnitySource;
                return new TimingPart(source, phase, Times(Short(type), node.Count), node.Milliseconds);
            }

            return null;
        }

        // What no package claimed, by the reload's own steps: its top steps, and the second level of the two big ones.
        private static void AddEngineParts(Node root, TimingProfile profile)
        {
            foreach (var step in root.Children)
            {
                bool split = step.Name == "BeginReloadAssembly" || step.Name == "FinalizeReload";
                if (!split || step.Children.Count == 0)
                {
                    AddEngine(profile, step.Name, step.Milliseconds - step.Claimed);
                    continue;
                }

                double inChildren = 0d;
                foreach (var child in step.Children)
                {
                    inChildren += child.Milliseconds;
                    if (!child.Attributed)
                    {
                        AddEngine(profile, child.Name, child.Milliseconds - child.Claimed);
                    }
                }

                AddEngine(profile, step.Name, step.Milliseconds - inChildren);
            }
        }

        private static void AddEngine(TimingProfile profile, string step, double milliseconds)
        {
            if (milliseconds >= 1d)
            {
                profile.Parts.Add(new TimingPart(TimingProfile.UnitySource, "Unity", FriendlyStep(step), milliseconds));
            }
        }

        private static string FriendlyStep(string step)
        {
            switch (step)
            {
                case "BeginReloadAssembly":
                    return "Unloading scripts";
                case "CreateAndSetChildDomain":
                    return "Creating the script domain";
                case "DisableScriptedObjects":
                    return "Disabling scripted objects";
                case "BackupScriptedObjects":
                case "BackupInstance":
                    return "Saving editor state";
                case "LoadAllAssembliesAndSetupDomain":
                case "LoadAssemblies":
                    return "Loading assemblies";
                case "FinalizeReload":
                    return "Finishing the reload";
                case "SetupLoadedEditorAssemblies":
                    return "Setting up editor assemblies";
                case "AwakeInstancesAfterBackupRestoration":
                    return "Restoring windows";
                case "RestoreBackups":
                    return "Restoring editor state";
                case "MonoScript.Renew":
                    return "Refreshing script references";
                case "onBeforeAssemblyReload.Invoke":
                    return "Unity's before-reload callbacks";
                case "didReloadMonoDomain.Invoke":
                    return "Unity's after-reload callbacks";
                default:
                    return step;
            }
        }

        private static string Source(AssemblySources sources, Func<AssemblySources, string> lookup, string typeName)
        {
            string source = sources != null ? lookup(sources) : null;
            return string.IsNullOrEmpty(source) ? SourceCatalog.NameForNamespace(typeName) ?? TimingProfile.UnitySource : source;
        }

        private static string Times(string label, int count) => count > 1 ? label + " ×" + count.ToString(CultureInfo.InvariantCulture) : label;

        private static string Short(string type)
        {
            int dot = type.LastIndexOf('.');
            return dot >= 0 && dot < type.Length - 1 ? type.Substring(dot + 1) : type;
        }

        // "Orbiters.Logger.Editor.LogCapture.BeforeReload" → "LogCapture.BeforeReload".
        private static string ShortMethod(string handler)
        {
            int generated = handler.IndexOf('<');
            string clean = generated > 0 ? handler.Substring(0, generated).TrimEnd('.') : handler;
            int last = clean.LastIndexOf('.');
            if (last <= 0)
            {
                return clean;
            }

            int previous = clean.LastIndexOf('.', last - 1);
            return previous >= 0 ? clean.Substring(previous + 1) : clean;
        }

        // ---- Unity's diagnostic switch -----------------------------------------------------------------------------

        private static object Switch()
        {
            var get = typeof(Debug).GetMethod("GetDiagnosticSwitch", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return get?.Invoke(null, new object[] { SwitchName });
        }

        private static bool? GetUnitySwitch()
        {
            try
            {
                var value = Switch()?.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var state = Switch();
                return state != null && value != null ? value.GetValue(state) as bool? : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetUnitySwitch(bool on)
        {
            try
            {
                var state = Switch();
                var value = state?.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (state == null || value == null || !(value.GetValue(state) is bool current) || current == on)
                {
                    return;
                }

                value.SetValue(state, on);
                if (on)
                {
                    EditorPrefs.SetBool(OwnedSwitchPref, true);
                }
            }
            catch (Exception)
            {
                // Without the switch, reloads are still measured step by step, without each package's part.
            }
        }
    }
}
