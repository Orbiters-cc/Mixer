using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    /// <summary>
    /// The report's logs.txt, with its masking memoised (LogReport.LineMemo), must be byte for byte what master's masker
    /// (ReferencePrivacy, a verbatim copy) gives when every line goes through it in order.
    /// </summary>
    public class ReportMaskingReferenceTests
    {
        private const string Home = @"C:\Users\Jane";
        private const string Secret = "s3cret-session-value";

        private static readonly string[] Ordinary =
        {
            "Player spawned", "Loading scene Main", "Import finished", "Done", "Compiling shaders: 12 of 40", "Finished",
            "Saved to C:\\Users\\Jane\\Documents\\x.txt", "Token refreshed for user", "abcdefgh", "QUJDREVGR0g="
        };

        private static readonly string[][] Secrets =
        {
            new[] { "-----BEGIN RSA PRIVATE KEY-----", "MIIEowIBAAKCAQEAsecretbody1", "Q2FmZWJhYmVzZWNyZXRib2R5Mg==", "-----END RSA PRIVATE KEY-----" },
            new[] { "-----BEGIN PRIVATE KEY-----", "short", "MIIE+/xyz", "body and footer\n-----END PRIVATE KEY-----" },
            new[] { "-----BEGIN PRIVATE KEY-----\nMIIEowIBAAKCAQEAone\n-----END PRIVATE KEY-----" },
            new[] { "-----BEGIN OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXktdjEAAAAA" },
            new[] { "-----BEGIN PGP PRIVATE KEY BLOCK-----", "lQOYBF0abc", "-----END PGP PRIVATE KEY BLOCK-----" },
            new[] { "-accessToken", "tokvalue-9876" },
            new[] { "Unity.exe -projectPath D:\\P --password \"hunter2\" -batchmode" },
            new[] { "Signed in with token " + Secret },
            new[] { "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc" }
        };

        [Test]
        public void MemoisedLogsMatchMastersMaskingExactly()
        {
            for (int seed = 1; seed <= 40; seed++)
            {
                var random = new Random(seed);
                var entries = new List<string>();
                var pending = new List<Queue<string>>();
                for (int i = 0; i < 400; i++)
                {
                    int pick = random.Next(10);
                    if (pick < 2)
                    {
                        pending.Add(new Queue<string>(Secrets[random.Next(Secrets.Length)]));
                    }

                    // Secrets advance a part at a time, interleaved with ordinary and repeated logs.
                    if (pending.Count > 0 && random.Next(3) == 0)
                    {
                        var secret = pending[random.Next(pending.Count)];
                        entries.Add(secret.Dequeue());
                        if (secret.Count == 0)
                        {
                            pending.Remove(secret);
                        }
                    }
                    else if (entries.Count > 0 && random.Next(3) == 0)
                    {
                        entries.Add(entries[random.Next(entries.Count)]);
                    }
                    else
                    {
                        entries.Add(Ordinary[random.Next(Ordinary.Length)]);
                    }
                }

                Compare(seed, entries, random);
            }
        }

        private static void Compare(int seed, List<string> entries, Random random)
        {
            var store = new LogStore();
            long time = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
            foreach (string entry in entries)
            {
                time += random.Next(1, 5000) * TimeSpan.TicksPerMillisecond;
                var variant = random.Next(4) == 0 ? LogVariant.Warning : LogVariant.Log;
                var flags = random.Next(5) == 0 ? OccurrenceFlags.ApproximateTime | OccurrenceFlags.FromUnityConsole : OccurrenceFlags.None;
                store.Add(time, entry, string.Empty, variant, flags);
            }

            var snapshot = LogReport.Take(store);
            snapshot.EditorLog = null;
            string staging = Path.Combine(Path.GetTempPath(), "LoggerReportReference-" + Guid.NewGuid().ToString("N"));
            try
            {
                LogReport.Write(new ReportContext(staging, new ReportPrivacy(Home, Secret), null, new[] { ReportPart.Logs }), snapshot, null);
                string actual = File.ReadAllText(Path.Combine(staging, "logs", "logs.txt"));
                Assert.AreEqual(Reference(store, snapshot), actual, "seed " + seed);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }

        // master's LogReport.Write for logs.txt, line by line through master's masker.
        private static string Reference(LogStore store, LogReport.Snapshot snapshot)
        {
            var view = snapshot.View;
            var masker = new ReferencePrivacy(Home, Secret).Lines();
            var text = new StringBuilder();
            void Line(string line) => text.Append(masker.Mask(line)).Append('\n');
            Line("# " + LoggerUi.Plural(view.Count, "log") + " from " + LogReport.Stamp(view.TimeAt(view.First)) + " to " + LogReport.Stamp(view.TimeAt(view.End - 1)) +
                 ". ~ marks a time read back from Unity's console (to the second).");
            Line(string.Empty);
            var row = new StringBuilder();
            for (int i = view.First; i < view.End; i++)
            {
                int id = view.OwnerAt(i);
                ref var message = ref view.Messages[id];
                row.Clear();
                row.Append(LogReport.Stamp(view.TimeAt(i))).Append(((OccurrenceFlags)view.FlagsAt(i) & OccurrenceFlags.ApproximateTime) != 0 ? "~ " : "  ");
                row.Append(LogReport.LevelTag(message.Variant)).Append("  ");
                int source = snapshot.SourceOf[id];
                row.Append(source >= 0 && source < snapshot.Sources.Length ? snapshot.Sources[source] : "Unity").Append("  ");
                string plain = view.Texts.Plain(message.Text);
                if (!string.IsNullOrEmpty(plain))
                {
                    row.Append(plain.TrimEnd().Replace("\n", "\n    "));
                }

                Line(row.ToString());
            }

            return text.ToString();
        }
    }
}
