using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Logger.Editor
{
    /// <summary>The project at a glance, always part of a report: Unity, platform, system and the packages installed.</summary>
    internal sealed class ProjectFacts
    {
        public string Name;
        public string Unity;
        public string Platform;
        public string OperatingSystem;
        public string RenderPipeline;
        public string Graphics;
        public string LoggerVersion = string.Empty;
        public readonly List<KeyValuePair<string, string>> Packages = new List<KeyValuePair<string, string>>();
        public readonly List<string> PackageLines = new List<string>();

        /// <summary>Read on the main thread (Unity's settings and package list).</summary>
        internal static ProjectFacts Gather()
        {
            var facts = new ProjectFacts
            {
                Name = ReportOptions.ProjectName,
                Unity = Application.unityVersion,
                Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                OperatingSystem = SystemInfo.operatingSystem,
                RenderPipeline = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in",
                Graphics = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"
            };
            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().OrderBy(p => p.name, StringComparer.Ordinal))
                {
                    facts.Packages.Add(new KeyValuePair<string, string>(package.name, package.version));
                    if (package.name == LoggerInfo.PackageName)
                    {
                        facts.LoggerVersion = package.version;
                    }

                    facts.PackageLines.Add(package.name + " " + package.version + "  (" + package.source.ToString().ToLowerInvariant() +
                                           (string.IsNullOrEmpty(package.displayName) ? string.Empty : ", " + package.displayName) + ")");
                }
            }
            catch (Exception)
            {
                // Package Manager not ready: the manifest copies still tell.
            }

            return facts;
        }

        /// <summary>Writes the facts and Unity's project files that describe the setup (manifests, version).</summary>
        internal void Write(ReportContext context)
        {
            var text = new StringBuilder();
            text.Append("Project   ").Append(Name).Append('\n');
            text.Append("Unity     ").Append(Unity).Append('\n');
            text.Append("Platform  ").Append(Platform).Append('\n');
            text.Append("Rendering ").Append(RenderPipeline).Append('\n');
            text.Append("System    ").Append(OperatingSystem).Append('\n');
            text.Append("Graphics  ").Append(Graphics).Append('\n');
            text.Append("Created   ").Append(context.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            text.Append("\nPackages (").Append(Packages.Count).Append(")\n");
            foreach (string line in PackageLines)
            {
                text.Append("  ").Append(line).Append('\n');
            }

            context.WriteText("project/project.txt", text.ToString(), "Unity version, platform, system and installed packages");
            Copy(context, "Packages/manifest.json", "project/manifest.json", "Unity's package manifest");
            Copy(context, "Packages/packages-lock.json", "project/packages-lock.json", "Resolved package versions");
            Copy(context, "Packages/vpm-manifest.json", "project/vpm-manifest.json", "VRChat Creator Companion packages");
            Copy(context, "ProjectSettings/ProjectVersion.txt", "project/ProjectVersion.txt", "The Unity version the project was saved with");
        }

        private static void Copy(ReportContext context, string from, string name, string label)
        {
            try
            {
                if (File.Exists(from))
                {
                    context.WriteText(name, File.ReadAllText(from), label);
                }
            }
            catch (Exception)
            {
                // A file that can't be read is left out.
            }
        }
    }
}
