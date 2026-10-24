using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Orbiters.Logger.Editor
{
    /// <summary>What a project report can hold, one card each in the report window.</summary>
    internal enum ReportPart
    {
        Diagnosis,
        Logs,
        Git,
        Scene,
        SceneExport
    }

    /// <summary>A file or folder another package adds to the Git part of a report (Mixer Git's release notes).</summary>
    internal readonly struct ReportExtra
    {
        public readonly string Path;
        public readonly string Name;
        public readonly string Label;
        public readonly bool Binary;

        /// <param name="path">The file or folder on disk.</param>
        /// <param name="name">Where it goes inside the report ("unitgit/releases.json").</param>
        /// <param name="label">What it is, for the report's contents.</param>
        /// <param name="binary">Pictures or other files copied as they are: left out when anonymizing.</param>
        public ReportExtra(string path, string name, string label, bool binary = false)
        {
            Path = path;
            Name = name;
            Label = label;
            Binary = binary;
        }
    }

    /// <summary>
    /// The report's remembered choices (the parts the person keeps on or off, where reports go) and what other
    /// assemblies add to it.
    /// </summary>
    internal static class ReportOptions
    {
        private const string PartPref = "Orbiters.Logger.Report.";
        private const string FolderPref = "Orbiters.Logger.Report.Folder";
        private const string AnonymizePref = "Orbiters.Logger.Report.Anonymize";

        internal static readonly ReportPart[] Parts = (ReportPart[])Enum.GetValues(typeof(ReportPart));

        /// <summary>Files Git-aware packages add to the Git part (called on the main thread with the project folder).</summary>
        internal static readonly List<Func<string, IEnumerable<ReportExtra>>> GitExtras = new List<Func<string, IEnumerable<ReportExtra>>>();

        // The AI diagnosis sends the report to a third-party AI provider: off until the person turns it on.
        internal static bool IsOn(ReportPart part) => EditorPrefs.GetBool(PartPref + part, part != ReportPart.Diagnosis);

        internal static void Set(ReportPart part, bool on) => EditorPrefs.SetBool(PartPref + part, on);

        /// <summary>Whether reports mask names, ids, emails, remotes and outside paths too (off by default).</summary>
        internal static bool Anonymize
        {
            get => EditorPrefs.GetBool(AnonymizePref, false);
            set => EditorPrefs.SetBool(AnonymizePref, value);
        }

        /// <summary>
        /// What Git-aware packages add for <paramref name="root"/>, each read whole inside its own guard (a lazy list
        /// failing half-way leaves that package out, not the report); main thread.
        /// </summary>
        internal static List<ReportExtra> CollectGitExtras(string root)
        {
            var all = new List<ReportExtra>();
            foreach (var extra in GitExtras)
            {
                try
                {
                    all.AddRange((extra(root) ?? Enumerable.Empty<ReportExtra>()).ToList());
                }
                catch (Exception)
                {
                    // That package's records are left out.
                }
            }

            return all;
        }

        /// <summary>Where reports are saved: the Desktop unless the person picked another folder that still exists.</summary>
        internal static string Folder
        {
            get
            {
                string chosen = EditorPrefs.GetString(FolderPref, string.Empty);
                return !string.IsNullOrEmpty(chosen) && Directory.Exists(chosen) ? chosen : DefaultFolder;
            }
            set => EditorPrefs.SetString(FolderPref, value == DefaultFolder ? string.Empty : value ?? string.Empty);
        }

        internal static string DefaultFolder
        {
            get
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return string.IsNullOrEmpty(desktop) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : desktop;
            }
        }

        /// <summary>The project's folder name ("MCB Test").</summary>
        internal static string ProjectName
        {
            get
            {
                string root = GitCli.ProjectRoot;
                string name = string.IsNullOrEmpty(root) ? null : new DirectoryInfo(root).Name;
                return string.IsNullOrWhiteSpace(name) ? "Unity project" : name;
            }
        }

        /// <summary>"MCB Test report 2026-10-08 14-32.zip": readable, sortable, safe on every system.</summary>
        internal static string FileName(DateTime local)
        {
            string name = ProjectName;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '-');
            }

            return name + " report " + local.ToString("yyyy-MM-dd HH-mm", System.Globalization.CultureInfo.InvariantCulture) + ".zip";
        }

        /// <summary>A path in <paramref name="folder"/> no other report uses yet (nor one being written).</summary>
        internal static string FreePath(string folder, DateTime local)
        {
            string file = FileName(local);
            string path = Path.Combine(folder, file);
            for (int i = 2; File.Exists(path) || File.Exists(ReportZip.PartialOf(path)); i++)
            {
                path = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + " (" + i + ").zip");
            }

            return path;
        }
    }
}
