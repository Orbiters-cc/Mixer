using System;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // A throwaway Unity-shaped Git repository in the temp folder, for tests of features that read or write a real repo.
    internal sealed class UnitGitTestRepo : IDisposable
    {
        // Every folder a test makes is named so, directly in the system temp folder: nothing else is ever deleted.
        private const string Prefix = "unitgit-tests-";

        public readonly string Root;
        public readonly UnitGitService Git;

        private UnitGitTestRepo(string root)
        {
            Root = root;
            Git = new UnitGitService(root);
        }

        public static UnitGitTestRepo Create()
        {
            if (!new UnitGitService(Path.GetTempPath()).IsGitAvailable()) Assert.Ignore("Git is not available on PATH.");
            string root = TempFolder();
            var repo = new UnitGitTestRepo(root);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Assets"));
                Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
                Directory.CreateDirectory(Path.Combine(root, "Packages"));
                File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{}\n");
                repo.Run("init", "-b", "main");
                repo.Run("config", "user.email", "unitgit@example.test");
                repo.Run("config", "user.name", "Mixer Git Tests");
                repo.Run("config", "core.autocrlf", "false");
                repo.Run("config", "commit.gpgsign", "false");
                repo.Run("config", "core.hooksPath", repo.Hooks);
                Directory.CreateDirectory(repo.Hooks);
                return repo;
            }
            catch
            {
                // Nothing returns to dispose it: a setup that failed cleans up its own folder.
                repo.Dispose();
                throw;
            }
        }

        // The repository's own hooks folder, empty unless a test adds one.
        public string Hooks => Path.Combine(Root, ".git", "test-empty-hooks");

        /// <summary>A new, empty "unitgit-tests-…" folder in the system temp folder. Delete it with <see cref="DeleteTempFolder"/>.</summary>
        public static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Deletes a folder a test made, and nothing else: only a "unitgit-tests-…" folder directly in the system temp folder.
        /// Any other path fails the test before anything is touched.
        /// </summary>
        public static void DeleteTempFolder(string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal))
                Assert.Fail("Refusing to delete " + full + ": tests only delete their own " + Prefix + "* folders in " + temp + ".");
            if (!Directory.Exists(full)) return;
            // Git makes its objects read-only; a process a test stopped may let go of its files a moment late.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
                    Directory.Delete(full, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException) when (attempt < 10)
                {
                    Thread.Sleep(200);
                }
            }
        }

        public string Full(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative, string text)
        {
            string path = Full(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public string Read(string relative) => File.ReadAllText(Full(relative));

        public string Run(params string[] args)
        {
            GitCommandResult result = Git.RunGit(60000, args);
            Assert.That(result.Success, Is.True, "git " + string.Join(" ", args) + ": " + result.Message);
            return result.StandardOutput;
        }

        public string Commit(string message)
        {
            Run("add", "-A");
            Run("commit", "-q", "-m", message);
            return Run("rev-parse", "HEAD").Trim();
        }

        public void Dispose() => DeleteTempFolder(Root);
    }
}
