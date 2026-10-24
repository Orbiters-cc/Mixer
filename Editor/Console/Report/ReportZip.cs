using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Writes report zips safely: into "&lt;name&gt;.zip.partial" next to the final file, renamed only once complete, so a
    /// cancel, a failure or a script reload never leaves a cut zip under the report's name. Files already compressed
    /// (the scene's .unitypackage, pictures) are asked for no compression, as compressing them again only costs time
    /// (Unity's Mono may deflate them anyway: the request is a hint). Safe on a worker thread.
    /// </summary>
    internal static class ReportZip
    {
        private static readonly HashSet<string> AlreadyCompressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unitypackage", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".zip", ".gz", ".7z", ".mp4", ".webm", ".ogg", ".mp3"
        };

        // Room the file system needs beside the file itself.
        private const long SpaceMargin = 64L * 1024 * 1024;

        /// <summary>Where <paramref name="path"/> is written until it is complete.</summary>
        internal static string PartialOf(string path) => path + ".partial";

        /// <summary>
        /// Packs every file of <paramref name="folder"/> into <paramref name="destination"/>. Between files it stops when
        /// <paramref name="cancelled"/> turns true; at the end <paramref name="commit"/> says whether the file may still
        /// take its name. False (and no file left) when stopped.
        /// </summary>
        internal static bool Pack(string folder, string destination, Func<bool> cancelled, Func<bool> commit, Action<float> progress = null)
        {
            string partial = PartialOf(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            bool done = false;
            try
            {
                var files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal).ToList();
                long total = Math.Max(1L, files.Sum(f => f.Length)), written = 0;
                string root = new DirectoryInfo(folder).FullName;
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                    {
                        if (cancelled())
                        {
                            return false;
                        }

                        string name = file.FullName.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                        zip.CreateEntryFromFile(file.FullName, name, LevelOf(name));
                        written += file.Length;
                        progress?.Invoke((float)written / total);
                    }
                }

                if (!commit())
                {
                    return false;
                }

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(partial, destination);
                done = true;
                return true;
            }
            finally
            {
                if (!done)
                {
                    TryDelete(partial);
                }
            }
        }

        /// <summary>
        /// Writes <paramref name="zip"/> again with <paramref name="texts"/> (name → content) replaced or added, copying
        /// every other file as it streams (the archive is never held in memory), then swaps the new file in. The old one
        /// stays as it was if anything fails.
        /// </summary>
        internal static void Rewrite(string zip, IReadOnlyDictionary<string, string> texts)
        {
            string partial = PartialOf(zip);
            bool done = false;
            try
            {
                using (var source = ZipFile.OpenRead(zip))
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var target = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var entry in source.Entries)
                    {
                        if (texts.ContainsKey(entry.FullName))
                        {
                            continue;
                        }

                        var copy = target.CreateEntry(entry.FullName, LevelOf(entry.FullName));
                        copy.LastWriteTime = entry.LastWriteTime;
                        using (var from = entry.Open())
                        using (var to = copy.Open())
                        {
                            from.CopyTo(to, 1 << 16);
                        }
                    }

                    foreach (var pair in texts)
                    {
                        using (var writer = new StreamWriter(target.CreateEntry(pair.Key, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                        {
                            writer.Write(pair.Value);
                        }
                    }
                }

                try
                {
                    // One step where the system can: the report is never missing in between.
                    File.Replace(partial, zip, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(zip);
                    File.Move(partial, zip);
                }

                done = true;
            }
            finally
            {
                if (!done)
                {
                    TryDelete(partial);
                }
            }
        }

        /// <summary>
        /// Throws when the drive of <paramref name="folder"/> has less than <paramref name="bytes"/> free (with a margin),
        /// in words a person can act on. Unknown drives (network shares) pass.
        /// </summary>
        internal static void EnsureSpace(string folder, long bytes, string what)
        {
            long free;
            string drive;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    return;
                }

                var info = new DriveInfo(root);
                if (!info.IsReady)
                {
                    return;
                }

                free = info.AvailableFreeSpace;
                drive = root.TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return;
            }

            if (free < bytes + SpaceMargin)
            {
                throw new IOException("Not enough free space on " + (string.IsNullOrEmpty(drive) ? "the disk" : drive) + " for " + what + ": it needs about " +
                                      SceneReport.Size(bytes + SpaceMargin) + ", " + SceneReport.Size(Math.Max(0, free)) + " is free.");
            }
        }

        /// <summary>The size of every file in <paramref name="folder"/>.</summary>
        internal static long SizeOf(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        internal static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Still open (a reload stopped its writer): removed after the reload.
            }
        }

        private static CompressionLevel LevelOf(string name) =>
            AlreadyCompressed.Contains(Path.GetExtension(name)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
    }
}
