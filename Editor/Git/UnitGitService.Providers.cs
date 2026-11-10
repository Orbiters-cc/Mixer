using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor
{
    // GitHub and GitLab: their command-line tools found, signed in, and remote repositories created.
    internal sealed partial class UnitGitService
    {
        public GitCommandResult GitHubLogin()
        {
            if (!IsGitHubCliAvailable())
            {
                return Failure("GitHub CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitHub CLI, then reopen Unity.");
            }

            return RunGitHubCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "github.com", "--web", "--clipboard", "--git-protocol", "https", "--skip-ssh-key");
        }

        public GitCommandResult CreateGitHubRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            // One operation: it holds the repository's lease from here to its end (see UnitGitLease).
            return UnitGitLease.Hold(() =>
            {
                if (!IsGitHubCliAvailable())
                {
                    return Failure("GitHub CLI was not found on PATH. Install GitHub CLI, then reopen Unity.");
                }

                if (string.IsNullOrWhiteSpace(repositoryName))
                {
                    return Failure("GitHub repository name is required.");
                }

                remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
                if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
                {
                    return Failure("Remote '" + remoteName + "' already exists.");
                }

                return RunGitHubCli(
                    LongTimeoutMilliseconds,
                    "repo",
                    "create",
                    repositoryName.Trim(),
                    "--source",
                    ProjectRoot,
                    "--remote",
                    remoteName,
                    isPrivate ? "--private" : "--public");
        
            });
        }

        private GitCommandResult RunGitHubCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitHubCliExecutable(), "GitHub CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitHubCliAvailable()
        {
            return RunGitHubCli(10000, "--version").Success;
        }

        internal bool IsGitHubCliSignedIn()
        {
            return RunGitHubCli(10000, "auth", "status", "--hostname", "github.com").Success;
        }

        internal bool IsGitLabCliSignedIn()
        {
            return RunGitLabCli(10000, "auth", "status", "--hostname", "gitlab.com").Success;
        }

        public GitCommandResult GitLabLogin()
        {
            if (!IsGitLabCliAvailable())
            {
                return Failure("GitLab CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
            }

            return RunGitLabCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "gitlab.com", "--web", "--git-protocol", "https");
        }

        public GitCommandResult CreateGitLabRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            // One operation: it holds the repository's lease from here to its end (see UnitGitLease).
            return UnitGitLease.Hold(() =>
            {
                if (!IsGitLabCliAvailable())
                {
                    return Failure("GitLab CLI was not found. Mixer Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
                }

                if (string.IsNullOrWhiteSpace(repositoryName))
                {
                    return Failure("GitLab repository path is required.");
                }

                remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
                if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
                {
                    return Failure("Remote '" + remoteName + "' already exists.");
                }

                return RunGitLabCli(
                    LongTimeoutMilliseconds,
                    "repo",
                    "create",
                    repositoryName.Trim(),
                    "--remoteName",
                    remoteName,
                    isPrivate ? "--private" : "--public");
        
            });
        }

        private GitCommandResult RunGitLabCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitLabCliExecutable(), "GitLab CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitLabCliAvailable()
        {
            return RunGitLabCli(10000, "--version").Success;
        }

        private static string GetGitHubCliExecutable()
        {
            const string executableName = "gh.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "GitHub CLI", executableName),
                Path.Combine(localAppData, "GitHub CLI", executableName),
                Path.Combine(localAppData, "Programs", "GitHub CLI", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "gh";
        }

        private static string GetGitLabCliExecutable()
        {
            const string executableName = "glab.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "glab", "bin", executableName),
                Path.Combine(localAppData, "Programs", "glab", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "glab";
        }

        private static string FindExecutableOnPath(string executableName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }
    }
}
