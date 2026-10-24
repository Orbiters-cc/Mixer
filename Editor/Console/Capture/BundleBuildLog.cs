using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// What Unity wrote to Editor.log while it built an asset bundle, with the durations it measured itself: asset
    /// database refreshes (and how much of them went to managed callbacks), the script compilation done for the build,
    /// how many shader passes were compiled and the size of the bundle. Editor.log has no timestamps, so only these
    /// self-timed lines can split the build.
    /// </summary>
    internal sealed class BundleBuildLog
    {
        private const long MaxBytes = 64L * 1024L * 1024L;

        private static readonly Regex RefreshLine = new Regex(@"^Asset Pipeline Refresh \(id=[0-9a-f]+\): Total: (?<s>[\d.]+) seconds", RegexOptions.CultureInvariant);
        private static readonly Regex ImportsLine = new Regex(@"^\s+Imports: total=(?<total>\d+) \(actual=(?<actual>\d+), local cache=(?<cache>\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex CallbackLine = new Regex(@"^\s+Asset DB Callback time: managed=(?<ms>\d+) ms", RegexOptions.CultureInvariant);
        private static readonly Regex BeeLine = new Regex(@"^ExitCode: \d+ Duration: (?:(?<m>\d+)m)?(?:(?<s>\d+)s)?(?<ms>\d+)ms", RegexOptions.CultureInvariant);
        private static readonly Regex SizeLine = new Regex(@"^Total compressed size (?<packed>[\d.]+ \w+)\. Total uncompressed size (?<raw>[\d.]+ \w+)\.", RegexOptions.CultureInvariant);

        public int Refreshes;
        public double RefreshMilliseconds;
        /// <summary>Time the refreshes spent in managed callbacks (asset postprocessors' OnPostprocessAllAssets above all).</summary>
        public double CallbackMilliseconds;
        public int Imports;
        public int ActualImports;
        public int CachedImports;
        /// <summary>Script compilation runs (the build compiles the scripts for the target platform).</summary>
        public double ScriptMilliseconds;
        public int ShaderPasses;
        /// <summary>"106.0 MB" and "266.1 MB" for the first bundle written, empty when none was reported.</summary>
        public string CompressedSize = string.Empty;
        public string UncompressedSize = string.Empty;

        public bool Found => Refreshes > 0 || ScriptMilliseconds > 0d || ShaderPasses > 0 || CompressedSize.Length > 0;

        /// <summary>Reads Editor.log between two lengths it had; null when it can't be read.</summary>
        public static BundleBuildLog Read(string path, long from, long to)
        {
            if (string.IsNullOrEmpty(path) || from < 0L || to <= from)
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < to)
                    {
                        // A new Editor.log (the editor restarted it): the build's lines are gone.
                        return null;
                    }

                    long start = Math.Max(from, to - MaxBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[to - start];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    return Parse(Encoding.UTF8.GetString(buffer, 0, read).Split('\n'));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static BundleBuildLog Parse(IEnumerable<string> lines)
        {
            var log = new BundleBuildLog();
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                Match match;
                if (line[0] == '\t' || line[0] == ' ')
                {
                    if ((match = ImportsLine.Match(line)).Success)
                    {
                        log.Imports += Int(match, "total");
                        log.ActualImports += Int(match, "actual");
                        log.CachedImports += Int(match, "cache");
                    }
                    else if ((match = CallbackLine.Match(line)).Success)
                    {
                        log.CallbackMilliseconds += Int(match, "ms");
                    }

                    continue;
                }

                if (line.StartsWith("Compiling shader \"", StringComparison.Ordinal))
                {
                    log.ShaderPasses++;
                }
                else if ((match = RefreshLine.Match(line)).Success)
                {
                    log.Refreshes++;
                    log.RefreshMilliseconds += double.Parse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * 1000d;
                }
                else if ((match = BeeLine.Match(line)).Success)
                {
                    log.ScriptMilliseconds += Int(match, "m") * 60000d + Int(match, "s") * 1000d + Int(match, "ms");
                }
                else if (log.CompressedSize.Length == 0 && (match = SizeLine.Match(line)).Success)
                {
                    log.CompressedSize = match.Groups["packed"].Value;
                    log.UncompressedSize = match.Groups["raw"].Value;
                }
            }

            return log;
        }

        private static int Int(Match match, string group)
        {
            var value = match.Groups[group];
            return value.Success && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0;
        }
    }
}
