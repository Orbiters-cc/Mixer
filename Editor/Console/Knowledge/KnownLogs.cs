using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>One explanation as written in explanations.json and served by the Orbiters server.</summary>
    [Serializable]
    internal sealed class ExplanationData
    {
        public string id;
        public string needle;
        public string pattern;
        public string stackNeedle;
        public string[] levels;
        public int priority;
        public string severity;
        public string source;
        public string title;
        public string summary;
        public string[] fixes;
        public string link;
        public string linkLabel;
        /// <summary>When the entry's text last changed (yyyy-MM-dd); see <see cref="LogExplanation.Updated"/>.</summary>
        public string updated;
    }

    /// <summary>A list of explanations: the Mixer console's file, or the Orbiters server's answer (with its revision).</summary>
    [Serializable]
    internal sealed class ExplanationFile
    {
        public string revision;
        public ExplanationData[] explanations;
    }

    /// <summary>
    /// The Mixer console's own explanations (Editor/Knowledge/explanations.json): the Unity, C# compiler, VRChat SDK, VRCFury
    /// and Orbiters tools' messages creators meet most. The same list lives on the Orbiters server, where it is kept
    /// up to date between releases (see <see cref="RemoteExplanations"/>).
    /// </summary>
    internal static class KnownLogs
    {
        private const string RelativePath = "Editor/Console/Knowledge/explanations.json";

        public static IEnumerable<LogExplanation> Load()
        {
            string path = FilePath;
            if (path == null || !File.Exists(path))
            {
                return Array.Empty<LogExplanation>();
            }

            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return Array.Empty<LogExplanation>();
            }
        }

        private static string FilePath
        {
            get
            {
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(KnownLogs).Assembly);
                    if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                    {
                        return Path.Combine(info.resolvedPath, RelativePath);
                    }
                }
                catch (Exception)
                {
                    // Off the main thread or very early: the package's usual place.
                }

                return Path.GetFullPath(Path.Combine("Packages", "orbiters.mixer", RelativePath));
            }
        }

        /// <summary>Explanations from the JSON of a file or of the server; invalid entries are left out.</summary>
        internal static List<LogExplanation> Parse(string json)
        {
            var list = new List<LogExplanation>();
            var file = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<ExplanationFile>(json);
            if (file?.explanations == null)
            {
                return list;
            }

            foreach (var data in file.explanations)
            {
                var explanation = From(data);
                if (explanation != null)
                {
                    list.Add(explanation);
                }
            }

            return list;
        }

        private static LogExplanation From(ExplanationData data)
        {
            if (data == null || string.IsNullOrEmpty(data.id) || string.IsNullOrEmpty(data.needle) || string.IsNullOrEmpty(data.title))
            {
                return null;
            }

            return new LogExplanation(data.id, data.needle, data.title)
            {
                Pattern = string.IsNullOrEmpty(data.pattern) ? null : data.pattern,
                StackNeedle = string.IsNullOrEmpty(data.stackNeedle) ? null : data.stackNeedle,
                Levels = data.levels != null && data.levels.Length > 0 ? data.levels : null,
                Priority = data.priority,
                Severity = Severity(data.severity),
                Source = data.source ?? string.Empty,
                Summary = data.summary ?? string.Empty,
                Fixes = data.fixes ?? Array.Empty<string>(),
                Link = string.IsNullOrEmpty(data.link) ? null : data.link,
                LinkLabel = string.IsNullOrEmpty(data.linkLabel) ? null : data.linkLabel,
                Updated = data.updated ?? string.Empty
            };
        }

        private static LogExplanationSeverity Severity(string value)
        {
            switch (value)
            {
                case "harmless":
                    return LogExplanationSeverity.Harmless;
                case "warning":
                    return LogExplanationSeverity.Warning;
                case "problem":
                    return LogExplanationSeverity.Problem;
                case "blocking":
                    return LogExplanationSeverity.Blocking;
                default:
                    return LogExplanationSeverity.Info;
            }
        }
    }
}
