using System.Collections.Generic;
using System.IO;
using Orbiters.UnitGit.Editor;
using UnityEditor;

namespace Orbiters.Logger.Editor.UnitGit
{
    /// <summary>
    /// With Mixer Git in the project, a report's Git part also holds Mixer Git's own records: the releases (what was
    /// uploaded or published, when, from which commit) and the pictures kept with them.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnitGitReport
    {
        private const string ReleaseAssetsFolder = ".unitgit-release-assets";

        static UnitGitReport()
        {
            ReportOptions.GitExtras.Add(Extras);
        }

        private static IEnumerable<ReportExtra> Extras(string root)
        {
            string releases = UnitGitReleases.GetReleasesFilePath(root);
            if (File.Exists(releases))
            {
                yield return new ReportExtra(releases, "unitgit/" + Path.GetFileName(releases), "Mixer Git releases");
            }

            string assets = Path.Combine(root, ReleaseAssetsFolder);
            if (Directory.Exists(assets))
            {
                yield return new ReportExtra(assets, "unitgit/release-assets", "Mixer Git release pictures", binary: true);
            }
        }
    }
}
