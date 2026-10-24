using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.Logger.Editor.Tests
{
    public class ProjectReportTests
    {
        private const string Home = @"C:\Users\Jane Doe";
        private const string Token = "orbit-0123abcd-4567ef01-89abcdef-08-10-2026";

        private static ReportPrivacy Privacy() => new ReportPrivacy(Home, "s3cret-session-value");

        [Test]
        public void MasksTheUserFolderInEverySpelling()
        {
            var privacy = Privacy();
            Assert.AreEqual(@"%USERPROFILE%\AppData\Local\Unity\Editor.log", privacy.Mask(@"C:\Users\Jane Doe\AppData\Local\Unity\Editor.log"));
            Assert.AreEqual("%USERPROFILE%/Desktop/a.png", privacy.Mask("C:/Users/Jane Doe/Desktop/a.png"));
            Assert.AreEqual("%USERPROFILE%/x", privacy.Mask("c:/users/jane doe/x"));
            Assert.AreEqual("{\"path\":\"%USERPROFILE%\\\\Temp\"}", privacy.Mask("{\"path\":\"C:\\\\Users\\\\Jane Doe\\\\Temp\"}"));
            Assert.AreEqual(@"D:\Projects\Avatar", privacy.Mask(@"D:\Projects\Avatar"));
        }

        [Test]
        public void KnownTitlesAreMaskedLikeTheirMessage()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.known-secret"", ""needle"": ""ZZ-known"", ""pattern"": ""ZZ-known token=(?<value>\\S+) for (?<path>.+)"", ""severity"": ""problem"",
                  ""source"": ""Tests"", ""title"": ""Rejected {value} for {path}"" } ] }");
            var store = new LogStore();
            store.Add(1000, @"ZZ-known token=abcdef123456 for C:\Users\Jane Doe\Desktop\a.png", string.Empty, LogVariant.Error);
            try
            {
                LogExplanations.SetRemote(entries);
                var group = LogReport.Digest(store, Privacy()).Groups.Single();
                Assert.AreEqual(@"Rejected [masked] for %USERPROFILE%\Desktop\a.png", group.Known, "the placeholders come from the masked text");
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void MasksTokensAndPasswords()
        {
            var privacy = Privacy();
            Assert.AreEqual("token [masked] used", privacy.Mask("token " + Token + " used"));
            Assert.AreEqual("Authorization: Bearer [masked]", privacy.Mask("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123"));
            Assert.AreEqual("origin https://[masked]@github.com/me/repo.git (fetch)", privacy.Mask("origin https://me:ghp_secret@github.com/me/repo.git (fetch)"));
            Assert.AreEqual("value [masked]!", privacy.Mask("value s3cret-session-value!"));
            Assert.AreEqual("git@github.com:me/repo.git", privacy.Mask("git@github.com:me/repo.git"));
            Assert.AreEqual(string.Empty, privacy.Mask(null));
        }

        [Test]
        public void MasksSecretsInEveryForm()
        {
            var privacy = Privacy();
            Assert.AreEqual("Authorization: Basic [masked]", privacy.Mask("Authorization: Basic dXNlcjpwYXNzd29yZA=="));
            Assert.AreEqual("auth with token [masked]", privacy.Mask("auth with token ghp_0123456789abcdefghijABCDEFGHIJ012345"));
            Assert.AreEqual("GET https://api.x.com/v1?access_token=[masked]&page=2", privacy.Mask("GET https://api.x.com/v1?access_token=abc123&page=2"));
            Assert.AreEqual("https://maps.x.com/api?key=[masked]&apiKey=[masked]", privacy.Mask("https://maps.x.com/api?key=AbCdEf123&apiKey=zzz999"));
            Assert.AreEqual("{\"token\":\"[masked]\",\"name\":\"x\"}", privacy.Mask("{\"token\":\"abc.def\",\"name\":\"x\"}"));
            Assert.AreEqual("{\"password\": \"[masked]\", \"tokenCount\": \"5\"}", privacy.Mask("{\"password\": \"hunter2\", \"tokenCount\": \"5\"}"));
            Assert.AreEqual("body {\\\"token\\\":\\\"[masked]\\\"}", privacy.Mask("body {\\\"token\\\":\\\"abc\\\"}"));
            Assert.AreEqual("Cookie: [masked]", privacy.Mask("Cookie: auth=authcookie_0123abcd-4567; twoFactorAuth=xyz"));
            Assert.AreEqual("set authcookie_[masked]", privacy.Mask("set authcookie_0123abcd-4567-89ab"));
            Assert.AreEqual("post to https://discord.com/api/webhooks/[masked]", privacy.Mask("post to https://discord.com/api/webhooks/123456789/AbC-dEf_123"));
            Assert.AreEqual("jwt [masked] end", privacy.Mask("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3In0.abcdefghijk end"));
            Assert.AreEqual("{\"url\":\"https:\\/\\/[masked]@host.com\\/x\"}", privacy.Mask("{\"url\":\"https:\\/\\/jane:pw@host.com\\/x\"}"));
            Assert.AreEqual("https://[masked]@github.com/repo", privacy.Mask("https://jane:p@ss@github.com/repo"));
            Assert.AreEqual("license [masked] ok", privacy.Mask("license 0A1B2C3D-4E5F6A7B-8C9D0E1F-2A3B4C5D ok"));
            Assert.AreEqual("password: [masked]", privacy.Mask("password: hunter22"));
            Assert.AreEqual("password: [masked]", privacy.Mask("password: my secret phrase"));
            Assert.AreEqual("password = \"[masked]\" ok", privacy.Mask("password = \"correct horse battery\" ok"));
            Assert.AreEqual("{\"password\":\"[masked]\"}", privacy.Mask("{\"password\":\"ab\\\"cdXYZ\"}"), "an escaped quote inside the value");
            Assert.AreEqual("x {\\\"password\\\":\\\"[masked]\\\",\\\"n\\\":1}", privacy.Mask("x {\\\"password\\\":\\\"ab\\\\\\\"cd\\\",\\\"n\\\":1}"), "JSON inside a string");
            Assert.AreEqual("https://[masked]@host.com/x", privacy.Mask("https://user:pa/ss@host.com/x"));
            Assert.AreEqual("token=[masked] in a cookie", privacy.Mask("token=abc123 in a cookie"));
            Assert.AreEqual("Token: [masked]", privacy.Mask("Token: abcdefghijklmnopqrstuvwxyz012345"));
            Assert.AreEqual("-accessToken\n[masked]\n-projectPath\nH:\\x", privacy.Mask("-accessToken\nabc.def-123\n-projectPath\nH:\\x"), "a flag's value on the next line");
            Assert.AreEqual("a\n-----BEGIN RSA PRIVATE KEY-----\n[masked]\n-----END RSA PRIVATE KEY-----\nb",
                privacy.Mask("a\n-----BEGIN RSA PRIVATE KEY-----\nMIIEow\nxyz\n-----END RSA PRIVATE KEY-----\nb"));
            // Ordinary log text stays as it is.
            Assert.AreEqual("OAuth: success", privacy.Mask("OAuth: success"));
            Assert.AreEqual("Unexpected token: 'foreach'", privacy.Mask("Unexpected token: 'foreach'"));
            Assert.AreEqual("https://example.com/?email=jane@example.com&x=1", privacy.Mask("https://example.com/?email=jane@example.com&x=1"));
            const string compile = "Assets/Foo.cs(3,1): error CS0246: The type or namespace name 'Bar' could not be found";
            Assert.AreEqual(compile, privacy.Mask(compile));
            Assert.AreEqual("Unexpected token: '}' and maxTokens: 500", privacy.Mask("Unexpected token: '}' and maxTokens: 500"));
        }

        [Test]
        public void MasksShortAndEncodedUserFolders()
        {
            var privacy = Privacy();
            Assert.AreEqual(@"%USERPROFILE%\AppData\Local\Temp", privacy.Mask(@"C:\Users\JANEDO~1\AppData\Local\Temp"));
            Assert.AreEqual("%USERPROFILE%%5Cx", privacy.Mask("C%3A%5CUsers%5CJANEDO~1%5Cx"));
            Assert.AreEqual("\"%USERPROFILE%\\\\\\\\x\"", privacy.Mask("\"C:\\\\\\\\Users\\\\\\\\Jane Doe\\\\\\\\x\""), "JSON inside a JSON string");
            Assert.AreEqual(@"Saved to %USERPROFILE%.", privacy.Mask(@"Saved to C:\Users\Jane Doe."));
            Assert.AreEqual(@"C:\Users\Jane Doe.old\x", privacy.Mask(@"C:\Users\Jane Doe.old\x"), "another folder");
            Assert.AreEqual("file:///%USERPROFILE%/Desktop/x.png", privacy.Mask("file:///C:/Users/Jane%20Doe/Desktop/x.png"));
            Assert.AreEqual("q=%USERPROFILE%%5Cx", privacy.Mask("q=C%3A%5CUsers%5CJane%20Doe%5Cx"));
            Assert.AreEqual(@"C:\Users\Jane Doette\x", privacy.Mask(@"C:\Users\Jane Doette\x"), "only the whole folder");
        }

        private static ReportPrivacy Anonymous() => new ReportPrivacy(new ReportPrivacy.Identity
        {
            UserFolder = Home, UserName = "Jane Doe", ComputerName = "DESKTOP-JD42", ProjectFolder = @"D:\Work\Client X\Avatar",
            UnityFolder = @"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor"
        }, true);

        [Test]
        public void AnonymizingMasksNamesIdsAddressesAndOutsidePaths()
        {
            var privacy = Anonymous();
            Assert.AreEqual("channel: \"LicenseClient-%USERNAME%\" at", privacy.Mask("channel: \"LicenseClient-Jane Doe\" at"));
            Assert.AreEqual("  Machine Id:              [masked]", privacy.Mask("  Machine Id:              pcNTv4t3q9vbrn1z+pvOB5UsnsQ="));
            Assert.AreEqual("  Session Id:              [masked]", privacy.Mask("  Session Id:              baf1243c006049a99e15055c93f2b38e"));
            Assert.AreEqual("Serial number assigned to: \"[masked]\"", privacy.Mask("Serial number assigned to: \"F4-QGE8-GBG2-9Z3P-8FEK-XXXX\""));
            Assert.AreEqual("host %COMPUTERNAME% up", privacy.Mask("host DESKTOP-JD42 up"));
            Assert.AreEqual(@"[path]\x.txt and %USERNAME%", privacy.Mask(@"C:\Users\JANEDO~1\x.txt and JANEDO~1"));
            // Under the user folder too, and up to the end of the line: folder names have spaces.
            Assert.AreEqual(@"Loading [path]\a.cs", privacy.Mask(@"Loading C:\Users\Jane Doe\Documents\Unity Projects\Secret Client NDA\Assets\a.cs"));
            Assert.AreEqual("Loading [path]/a.cs", privacy.Mask("Loading C:/Users/Jane Doe/Documents/Unity Projects/Secret Client NDA/Assets/a.cs"));
            Assert.AreEqual("changed project path to: [path]", privacy.Mask(@"changed project path to: H:\metaverse\unity projects\MCB Test"));
            Assert.AreEqual(@"[path]\Unity Hub.exe", privacy.Mask(@"C:\Program Files\Unity Hub\Unity Hub.exe"));
            Assert.AreEqual(@"[path]\x.cs", privacy.Mask(@"D:\Work\Client X\Avatar-Backup\Assets\x.cs"), "a sibling of the project is outside it");
            Assert.AreEqual(@"x [path]\b.cs(12,5): error CS1 and [path]\d.txt", privacy.Mask(@"x C:\a\b.cs(12,5): error CS1 and D:\c\d.txt"));
            Assert.AreEqual("[email]", privacy.Mask("jane.doe%40example.com"));
            Assert.AreEqual("-licensingIpc [masked]", privacy.Mask("-licensingIpc LicenseClient-Jane Doe"));
            Assert.AreEqual("-hubSessionId\n[masked]", privacy.Mask("-hubSessionId\n1234-abcd"));
            Assert.AreEqual("%USERNAME% and %USERNAME%", privacy.Mask("JaneDoe and jane_doe"));
            Assert.AreEqual("user %USERNAME% in url", privacy.Mask("user Jane%20Doe in url"));
            Assert.AreEqual("mail [email] now", privacy.Mask("mail jane.doe@example.com now"));
            Assert.AreEqual("origin\t[remote] (fetch)", privacy.Mask("origin\tgit@github.com:janedoe/avatar.git (fetch)"));
            Assert.AreEqual("origin\t[remote] (push)", privacy.Mask("origin\thttps://github.com/janedoe/avatar.git (push)"));
            Assert.AreEqual("prefab-id-v1_avtr_[masked]_27.prefab", privacy.Mask("prefab-id-v1_avtr_0bf7ea0b-0849-47d5-8a2b-b0a217aa676c_27.prefab"));
            Assert.AreEqual(@"Loading %PROJECT%\Assets\a.fbx", privacy.Mask(@"Loading D:\Work\Client X\Avatar\Assets\a.fbx"));
            Assert.AreEqual(@"%UNITY%\Data\Mono.dll", privacy.Mask(@"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\Mono.dll"));
            Assert.AreEqual(@"Read [path]\Rexouium.unitypackage done", privacy.Mask(@"Read E:\Downloads\Rexouium 1.6\Rexouium.unitypackage done"));
            Assert.AreEqual("[path]", privacy.Mask(@"C:\Users\Bob"), "another user's folder is a name");
            Assert.AreEqual("[path]/a.log", privacy.Mask("/Users/bob/Library/Logs/a.log"));
            Assert.AreEqual("{\"author\": \"[masked]\", \"version\": \"1.0\"}", privacy.Mask("{\"author\": \"Jane\", \"version\": \"1.0\"}"));
            Assert.AreEqual("Co-authored-by: [masked]", privacy.Mask("Co-authored-by: Claude <noreply@anthropic.com>"));
            Assert.AreEqual("-hubSessionId [masked] -accessToken [masked]", privacy.Mask("-hubSessionId 1234-abcd -accessToken zzz"));
            // What helps reading the report stays.
            Assert.AreEqual("at 12:30:45 Error: x", privacy.Mask("at 12:30:45 Error: x"));
            Assert.AreEqual("see https://api.orbiters.cc/logger/explanations", privacy.Mask("see https://api.orbiters.cc/logger/explanations"));
            Assert.AreEqual("com.vrchat.avatars@3.7.0", privacy.Mask("com.vrchat.avatars@3.7.0"));
        }

        [Test]
        public void WithoutAnonymizingNamesStay()
        {
            var privacy = new ReportPrivacy(new ReportPrivacy.Identity { UserFolder = Home, UserName = "Jane Doe", ComputerName = "DESKTOP-JD42" }, false);
            Assert.AreEqual("LicenseClient-Jane Doe on DESKTOP-JD42", privacy.Mask("LicenseClient-Jane Doe on DESKTOP-JD42"));
            Assert.IsFalse(privacy.Anonymize);
            StringAssert.Contains("Not anonymized", privacy.Describe());
            StringAssert.Contains("Anonymized", Anonymous().Describe());
        }

        [Test]
        public void LogsReadLineByLineMaskWhatTheirFlagsAnnounce()
        {
            // Editor.log under "COMMAND LINE ARGUMENTS:" prints each argument on its own line, a flag's value on the next.
            var lines = Privacy().Lines();
            Assert.AreEqual(new[] { "-accessToken", "  [masked]", "-projectPath", @"H:\p" },
                new[] { "-accessToken", "  eyJsecretvalue", "-projectPath", @"H:\p" }.Select(lines.Mask).ToArray());
            var anonymous = Anonymous().Lines();
            Assert.AreEqual(new[] { "-hubSessionId", "[masked]", "-token", "-cloudEnvironment", "[masked]" },
                new[] { "-hubSessionId", "1234", "-token", "-cloudEnvironment", "production" }.Select(anonymous.Mask).ToArray());
            var key = Privacy().Lines();
            Assert.AreEqual(new[] { "-----BEGIN PRIVATE KEY-----", "[masked]", "[masked]", "-----END PRIVATE KEY-----", "after" },
                new[] { "-----BEGIN PRIVATE KEY-----", "MIIEow", "xyz", "-----END PRIVATE KEY-----", "after" }.Select(key.Mask).ToArray());
            Assert.AreEqual(new[] { "-hubSessionId", "1234" }, new[] { "-hubSessionId", "1234" }.Select(Privacy().Lines().Mask).ToArray(), "names only when anonymizing");

            // The report's files written line by line (the copied Editor.log) go through it.
            string staging = Path.Combine(Path.GetTempPath(), "LoggerReportLines-" + Guid.NewGuid().ToString("N"));
            try
            {
                var context = new ReportContext(staging, Privacy(), null, new[] { ReportPart.Logs });
                using (var writer = context.OpenText("logs/Editor.log", "test"))
                {
                    foreach (string line in new[] { "COMMAND LINE ARGUMENTS:", "C:\\Unity.exe", "-accessToken", "secret-value-123", "-projectPath", "D:\\Project" })
                    {
                        writer.Line(line);
                    }
                }

                Assert.AreEqual("COMMAND LINE ARGUMENTS:\nC:\\Unity.exe\n-accessToken\n[masked]\n-projectPath\nD:\\Project\n",
                    File.ReadAllText(Path.Combine(staging, "logs", "Editor.log")));
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }

        [Test]
        public void TextsAreMaskedBeforeTheyAreCut()
        {
            // A token across the cut would otherwise leave its first half in the report.
            string text = new string('x', 1490) + " " + Token;
            StringAssert.EndsWith(" [masked]", Privacy().MaskAndClip(text, 1500));
            Assert.AreEqual(1500, Privacy().MaskAndClip(new string('y', 3000), 1500).Length);
        }

        [Test]
        public void ShortValuesAreNeverMasked()
        {
            // A user folder like "C:\" or a two-letter secret would mask half of every file.
            var privacy = new ReportPrivacy(@"C:\", "ab");
            Assert.AreEqual(@"C:\Program Files ab", privacy.Mask(@"C:\Program Files ab"));
        }

        [Test]
        public void ReportFileNamesAreSafeAndUnique()
        {
            var when = new DateTime(2026, 10, 8, 14, 32, 0);
            string name = ReportOptions.FileName(when);
            StringAssert.EndsWith(" report 2026-10-08 14-32.zip", name);
            Assert.AreEqual(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));

            string folder = Path.Combine(Path.GetTempPath(), "LoggerReportTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, name), string.Empty);
                StringAssert.EndsWith(" report 2026-10-08 14-32 (2).zip", ReportOptions.FreePath(folder, when));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void StackTopsSkipUnityLoggingFrames()
        {
            string stack = "UnityEngine.Debug:LogError (object)\nUnityEngine.Logger:Log ()\nMy.Tool:Run () (at Assets/Tool.cs:12)\n\nMy.Tool:Main ()\n";
            Assert.AreEqual("My.Tool:Run () (at Assets/Tool.cs:12)\nMy.Tool:Main ()", LogReport.TopOfStack(stack));
            Assert.AreEqual(string.Empty, LogReport.TopOfStack("  "));
            var many = string.Join("\n", Enumerable.Range(0, 40).Select(i => "Frame" + i + " ()"));
            Assert.AreEqual(14, LogReport.TopOfStack(many).Split('\n').Length);
        }

        [Test]
        public void SizesReadNaturally()
        {
            Assert.AreEqual("512 B", SceneReport.Size(512));
            Assert.AreEqual("1.5 KB", SceneReport.Size(1536));
            Assert.AreEqual("48 MB", SceneReport.Size(48L * 1024 * 1024));
            Assert.AreEqual("1.0 GB", SceneReport.Size(1024L * 1024 * 1024));
        }

        private static ReportContext Context(string note = "It broke in C:\\Users\\Jane Doe\\Desktop")
        {
            var context = new ReportContext(Path.GetTempPath(), Privacy(), note, new[] { ReportPart.Diagnosis, ReportPart.Logs });
            context.Project = new ProjectFacts { Name = "Avatar", Unity = "2022.3.22f1", Platform = "StandaloneWindows64", OperatingSystem = "Windows 11", RenderPipeline = "Built-in" };
            context.Project.Packages.Add(new System.Collections.Generic.KeyValuePair<string, string>("com.vrchat.avatars", "3.10.3"));
            context.Logs = new LogDigest { Total = 10, Errors = 2, Warnings = 1 };
            context.Logs.Groups.Add(new LogDigestGroup
            {
                Id = "g0", Level = LogLevel.Error, Count = 2, Source = "Compiler", Text = new string('x', 3000), Stack = "at " + Home + "\\A.cs",
                FirstSeen = DateTime.UtcNow.Ticks, LastSeen = DateTime.UtcNow.Ticks
            });
            return context;
        }

        [Test]
        public void DiagnosisRequestsAreMaskedAndFitTheServer()
        {
            var request = DiagnosisClient.BuildRequest(Context());
            Assert.AreEqual("It broke in %USERPROFILE%\\Desktop", request.note);
            Assert.AreEqual(1500, request.logs.groups[0].text.Length);
            StringAssert.Contains("%USERPROFILE%", request.logs.groups[0].stack);
            Assert.AreEqual("error", request.logs.groups[0].level);
            Assert.AreEqual("(not included)", request.git.branch);
            Assert.AreEqual("(not included)", request.scene.name);
            string json = JsonUtility.ToJson(request);
            StringAssert.Contains("\"packages\":[{\"name\":\"com.vrchat.avatars\",\"version\":\"3.10.3\"}]", json);
            StringAssert.DoesNotContain("Jane Doe", json);
        }

        [Test]
        public void PartsLeftOutNeverReachTheAiOrTheReadme()
        {
            // Only the AI and the scene export chosen: the logs digest and the scene's names stay out.
            var context = new ReportContext(Path.GetTempPath(), Privacy(), "help", new[] { ReportPart.Diagnosis, ReportPart.SceneExport });
            context.Project = Context().Project;
            context.Logs = Context().Logs;
            context.Git = new GitDigest { Repository = true, Branch = "main" };
            context.Scene = new SceneDigest();
            context.Scene.Names.Add("Secret scene");
            context.Scene.Roots.Add("Client avatar");
            var request = DiagnosisClient.BuildRequest(context);
            Assert.AreEqual(0, request.logs.groups.Length);
            Assert.AreEqual(0, request.logs.errors);
            Assert.AreEqual("(not included)", request.git.branch);
            Assert.AreEqual("(not included)", request.scene.name);
            StringAssert.DoesNotContain("Client avatar", JsonUtility.ToJson(request));
            string readme = ReportReadme.Write(context, Array.Empty<ReportStep>());
            StringAssert.DoesNotContain("Secret scene", readme);
            StringAssert.DoesNotContain("The messages that matter most", readme);
        }

        [Test]
        public void AiWordsCantBecomeLinksOrMentions()
        {
            var diagnosis = new Diagnosis
            {
                headline = "[Click here](https://evil.example) <b>now</b>",
                summary = "# Heading\n<script>x</script> and **bold**",
                causes = new[] { new DiagnosisCause { title = "![img](x)", likelihood = "Medium", explanation = "_x_", steps = new[] { "[a](b)" } } }
            };
            string markdown = DiagnosisClient.Markdown(diagnosis, null);
            StringAssert.Contains("**\\[Click here\\](https://evil.example) \\<b\\>now\\</b\\>**", markdown);
            StringAssert.Contains("\\# Heading\n\\<script\\>x\\</script\\> and \\*\\*bold\\*\\*", markdown);
            StringAssert.Contains("## 1. !\\[img\\](x)  (medium likelihood)", markdown);
            StringAssert.Contains("1. \\[a\\](b)\n", markdown);
            Assert.AreEqual(@"%USERPROFILE%\Desktop", MarkdownText.Escape(@"%USERPROFILE%\Desktop"), "a backslash before a letter stays");
            Assert.AreEqual("@\u200Beveryone look \\<@\u200B123456789\\>", MarkdownText.ForChat("@everyone look <@123456789>"));
            Assert.AreEqual("medium", DiagnosisClient.Likelihood("Medium"));
            Assert.AreEqual("high", DiagnosisClient.Likelihood(" HIGH "));
        }

        [Test]
        public void RequestsHoldExactlyTheFieldsTheServerAccepts()
        {
            // The server's schema is strict (backend/src/services/logger/loggerDiagnosis.js): one field more is a 400.
            string Fields(Type type) => string.Join(",", type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(f => f.Name));
            Assert.AreEqual("note,project,logs,git,scene", Fields(typeof(DiagnosisClient.Request)));
            Assert.AreEqual("name,unity,platform,os,renderPipeline,packages", Fields(typeof(DiagnosisClient.ProjectPart)));
            Assert.AreEqual("name,version", Fields(typeof(DiagnosisClient.PackagePart)));
            Assert.AreEqual("total,errors,warnings,groups", Fields(typeof(DiagnosisClient.LogsPart)));
            Assert.AreEqual("id,level,count,source,text,stack,firstSeen,lastSeen,known", Fields(typeof(DiagnosisClient.GroupPart)));
            Assert.AreEqual("branch,changedFiles,recent", Fields(typeof(DiagnosisClient.GitPart)));
            Assert.AreEqual("name,objects,missingScripts,inactive,roots", Fields(typeof(DiagnosisClient.ScenePart)));
        }

        [Test]
        public void SignInErrorsAndServerReasonsReadRight()
        {
            StringAssert.Contains("sign-in expired", DiagnosisClient.ErrorOf(401, "{\"error\":\"Invalid token\"}", null));
            StringAssert.Contains("sign-in expired", DiagnosisClient.ErrorOf(403, string.Empty, null));
            Assert.AreEqual("AI features are disabled in your account overview.", DiagnosisClient.ErrorOf(403, "{\"error\":\"AI features are disabled in your account overview.\"}", null));
            StringAssert.Contains("Too many", DiagnosisClient.ErrorOf(429, "{\"error\":\"x\"}", null));
            Assert.AreEqual("Your previous diagnosis is still running. Please wait.", DiagnosisClient.ErrorOf(409, "{\"error\":\"Your previous diagnosis is still running. Please wait.\"}", null));
        }

        [Test]
        public void DiagnosesReadAsMarkdownWithTheirLogs()
        {
            var context = Context();
            var diagnosis = JsonUtility.FromJson<Diagnosis>("{\"headline\":\"A script does not compile\",\"summary\":\"Fix it.\",\"causes\":[" +
                                                           "{\"title\":\"Foo.cs\",\"likelihood\":\"high\",\"explanation\":\"CS0246\",\"steps\":[\"Open Foo.cs\",\"Add the using\"],\"related\":[\"g0\",\"g7\"]}]}");
            string markdown = DiagnosisClient.Markdown(diagnosis, context.Logs.Groups, 2);
            StringAssert.StartsWith("## AI diagnosis\n\n**A script does not compile**", markdown);
            StringAssert.Contains("### 1. Foo.cs  (high likelihood)", markdown);
            StringAssert.Contains("1. Open Foo.cs\n2. Add the using\n", markdown);
            Assert.AreEqual(1, markdown.Split(new[] { "\n- `" }, StringSplitOptions.None).Length - 1, "only the log this report sent");

            context.Diagnosis = diagnosis;
            string readme = ReportReadme.Write(context, new[] { new ReportStep("Git history", LoggerGlyph.Branch) { State = ReportStepState.Failed, Detail = "Git is not installed" } });
            StringAssert.Contains("## What's going wrong\n\n> It broke in %USERPROFILE%\\Desktop", readme);
            StringAssert.Contains("## AI diagnosis", readme);
            StringAssert.Contains("## The messages that matter most", readme);
            StringAssert.Contains("- Git history: Git is not installed", readme);
        }
    }
}
