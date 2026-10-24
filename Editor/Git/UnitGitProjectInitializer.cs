using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitProjectInitializer
    {
        private const string InitialCommitMessage = "Initial commit";
        private const string ExcludeRootPackagePattern = "/[Pp]ackage/";

        private static readonly string[] VrchatUnityGitIgnorePatterns =
        {
            "",
            "# Mixer Git VRChat Unity project rules",
            ".utmp/",
            "/[Ll]ibrary/",
            "/[Tt]emp/",
            "/[Oo]bj/",
            "/[Bb]uild/",
            "/[Bb]uilds/",
            "/[Ll]ogs/",
            "/[Uu]ser[Ss]ettings/",
            "/[Mm]emoryCaptures/",
            "/[Rr]ecordings/",
            ".vs/",
            ".idea/",
            ".gradle/",
            "ExportedObj/",
            "*.csproj",
            "*.unityproj",
            "*.sln",
            "*.suo",
            "*.tmp",
            "*.user",
            "*.userprefs",
            "*.pidb",
            "*.booproj",
            "*.svd",
            "*.pdb",
            "*.mdb",
            "*.opendb",
            "*.VC.db",
            "*.pidb.meta",
            "*.pdb.meta",
            "*.mdb.meta",
            "sysinfo.txt",
            "*.apk",
            "*.aab",
            "*.unitypackage",
            "*.unitypackage.meta",
            "*.app",
            "crashlytics-build.properties",
            "/[Aa]ssets/[Aa]ddressable[Aa]ssets[Dd]ata/*/*.bin*",
            "/[Aa]ssets/[Ss]treamingAssets/aa.meta",
            "/[Aa]ssets/[Ss]treamingAssets/aa/*",
            "Packages/com.vrchat.*",
            "!Packages/com.vrchat.core.vpm-resolver/",
            "!Packages/com.vrchat.core.vpm-resolver/**",
            "Packages/com.vrcfury.*",
            "Packages/com.poiyomi.*",
            "!Packages/manifest.json",
            "!Packages/packages-lock.json",
            "!Packages/vpm-manifest.json"
        };

        // MCB's downloaded version patches: large, and MCB downloads them again when a version is switched to. The rest of
        // a version folder (avatar definitions, logic prefabs, textures) can be referenced by the avatar and stays tracked.
        internal const string McbDownloadsFolder = "Assets/MCB/assets";
        internal static readonly string[] McbDownloadPatterns =
        {
            "",
            "# Mixer Git: MCB version downloads (MCB downloads them again when needed)",
            "/[Aa]ssets/MCB/assets/*/versions/**/*.bin",
            "/[Aa]ssets/MCB/assets/*/versions/**/*.bin.meta"
        };

        private static readonly HashSet<string> EnsuredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string EnsuredSessionKey = "Orbiters.UnitGit.DefaultRulesEnsured";

        /// <summary>
        /// Adds the default rules that came after a repository was created, once per project and editor session: today the
        /// MCB download rules, for projects that use MCB. Never while HEAD is a commit of its own (a visit, a rebase): the
        /// .gitignore is that commit's then, and the rules would show as a change made while visiting.
        /// </summary>
        internal static void EnsureDefaultRules(string projectRoot)
        {
            lock (EnsuredRoots)
            {
                if (EnsuredRoots.Contains(projectRoot)) return;
            }
            bool mcb = Directory.Exists(Path.Combine(projectRoot, "Assets", "MCB"));
            // Checked again once a branch is back.
            if (mcb && new UnitGitService(projectRoot).IsHeadDetached()) return;
            lock (EnsuredRoots)
            {
                if (!EnsuredRoots.Add(projectRoot)) return;
            }
            if (mcb) AppendMissing(Path.Combine(projectRoot, ".gitignore"), McbDownloadPatterns);
        }

        // The set empties on every domain reload: the editor's own project is carried over them in SessionState, so "once
        // per editor session" holds (SessionState is main-thread only, the set is read by background refreshes).
        [InitializeOnLoadMethod]
        private static void KeepEnsuredAcrossReloads()
        {
            string project = UnitGitService.GetUnityProjectRoot();
            if (SessionState.GetBool(EnsuredSessionKey, false))
            {
                lock (EnsuredRoots) EnsuredRoots.Add(project);
            }
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                lock (EnsuredRoots) SessionState.SetBool(EnsuredSessionKey, EnsuredRoots.Contains(project));
            };
        }

        private static bool AppendMissing(string path, string[] patterns)
        {
            string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var builder = new StringBuilder(existing.TrimEnd());
            bool changed = AppendPatterns(builder, existing, patterns);
            if (changed) File.WriteAllText(path, builder.ToString().TrimStart() + Environment.NewLine, Encoding.UTF8);
            return changed;
        }

        // Appends the patterns `existing` lacks, after a blank line. A heading (a "#" line) goes in only with a missing rule
        // under it: a file that already has the rules, under any heading, gets nothing.
        private static bool AppendPatterns(StringBuilder builder, string existing, string[] patterns)
        {
            bool changed = false;
            string heading = null;
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                bool present = existing.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                if (pattern.StartsWith("#", StringComparison.Ordinal))
                {
                    heading = present ? null : pattern;
                    continue;
                }

                if (present) continue;
                if (!changed)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    changed = true;
                }

                if (heading != null)
                {
                    builder.AppendLine(heading);
                    heading = null;
                }

                builder.AppendLine(pattern);
            }

            return changed;
        }

        public GitCommandResult Initialize(UnitGitService gitService, bool excludeRootPackageFolder)
        {
            if (gitService == null)
            {
                return Failure("Git service is not available.");
            }

            if (!UnitGitService.IsUnityProject(gitService.ProjectRoot))
            {
                return Failure("Cannot initialize Git because this folder is not a Unity project.");
            }

            EnsureVrchatGitIgnore(gitService.ProjectRoot, excludeRootPackageFolder);
            AppendMissing(Path.Combine(gitService.ProjectRoot, ".gitignore"), McbDownloadPatterns);

            var initResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "init");
            if (!initResult.Success)
            {
                return initResult;
            }

            var addResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "add", "-A", "--", ".");
            if (!addResult.Success)
            {
                return addResult;
            }

            var commitResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "commit", "-m", InitialCommitMessage);
            if (!commitResult.Success)
            {
                return commitResult;
            }

            return new GitCommandResult
            {
                ExitCode = 0,
                StandardOutput = "Initialized project repository and created the first commit."
            };
        }

        private static void EnsureVrchatGitIgnore(string projectRoot, bool excludeRootPackageFolder)
        {
            string path = Path.Combine(projectRoot, ".gitignore");
            string existing = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : string.Empty;

            var builder = new StringBuilder(existing.TrimEnd());
            bool changed = AppendPatterns(builder, existing, VrchatUnityGitIgnorePatterns);

            if (excludeRootPackageFolder &&
                existing.IndexOf(ExcludeRootPackagePattern, StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (!changed)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    changed = true;
                }

                builder.AppendLine("# Mixer Git optional first-commit exclusion");
                builder.AppendLine(ExcludeRootPackagePattern);
            }

            if (!File.Exists(path) || changed)
            {
                File.WriteAllText(path, builder.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult
            {
                ExitCode = 1,
                StandardError = message
            };
        }
    }
}
