using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private void RunRemoteProviderLogin(UnitGitRemoteProvider provider)
        {
            RunAction(GetRemoteProviderCommandLabel(provider) + " login", () =>
            {
                return provider == UnitGitRemoteProvider.GitLab
                    ? gitService.GitLabLogin()
                    : gitService.GitHubLogin();
            });
        }

        private void CreateRemoteRepository(UnitGitRemoteProvider provider, string repositoryName, string remoteName, bool isPrivate)
        {
            remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
            string repository = string.IsNullOrWhiteSpace(repositoryName) ? string.Empty : repositoryName.Trim();
            string command = provider == UnitGitRemoteProvider.GitLab
                ? RemoteCliCommand(provider, "repo", "create", repository, "--remoteName", remoteName, isPrivate ? "--private" : "--public")
                : RemoteCliCommand(provider, "repo", "create", repository, "--source", gitService.ProjectRoot, "--remote", remoteName, isPrivate ? "--private" : "--public");
            if (!ConfirmGitOperation(
                    "Create Remote Repository",
                    "Create",
                    command,
                    "Create a " + GetRemoteProviderCommandLabel(provider) + " remote repository and register the local remote.",
                    "This does not push commits.",
                    0))
            {
                return;
            }

            RunAction(GetRemoteProviderCommandLabel(provider) + " create remote", () =>
            {
                return provider == UnitGitRemoteProvider.GitLab
                    ? gitService.CreateGitLabRemoteRepository(repositoryName, remoteName, isPrivate)
                    : gitService.CreateGitHubRemoteRepository(repositoryName, remoteName, isPrivate);
            });
        }

        private static string GetRemoteProviderCommandLabel(UnitGitRemoteProvider provider)
        {
            return provider == UnitGitRemoteProvider.GitLab ? "gitlab" : "github";
        }

        private string GetDefaultRemoteRepositoryName()
        {
            return UnitGitOverview.DefaultRepositoryName;
        }
    }
}
