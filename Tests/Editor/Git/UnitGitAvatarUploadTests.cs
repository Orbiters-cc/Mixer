using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitAvatarUploadTests
    {
        private const string AvatarId = "avtr_b11054d2-80c6-47c2-a3a9-9514f9084587";

        private static UnitGitReleaseEntry Upload(string platform, string date, string started = null, string succeeded = null, string avatarId = AvatarId)
        {
            var fields = new List<UnitGitReleaseField>
            {
                new UnitGitReleaseField(UnitGitAvatarUpload.AvatarIdKey, avatarId),
                new UnitGitReleaseField(UnitGitAvatarUpload.PlatformKey, platform),
                new UnitGitReleaseField(UnitGitAvatarUpload.AvatarsSdkKey, "3.10.3")
            };
            if (started != null) fields.Add(new UnitGitReleaseField(UnitGitAvatarUpload.UploadStartedKey, started));
            if (succeeded != null) fields.Add(new UnitGitReleaseField(UnitGitAvatarUpload.UploadSucceededKey, succeeded));
            return new UnitGitReleaseEntry
            {
                id = "upload-" + platform + date, tool = UnitGitAvatarUpload.Tool, type = UnitGitAvatarUpload.Type,
                name = "Rexouium", scope = platform, date = date, fields = fields
            };
        }

        [Test]
        public void ReadsTheUploadFieldsTimesAndPage()
        {
            var upload = UnitGitAvatarUpload.From(Upload("PC", "2026-10-07T23:39:55Z", "2026-10-07T23:39:20.0838637Z", "2026-10-07T23:39:54.6335897Z"));

            Assert.IsNotNull(upload);
            Assert.AreEqual(AvatarId, upload.AvatarId);
            Assert.AreEqual("3.10.3", upload.AvatarsSdk);
            Assert.IsTrue(upload.IsPc);
            Assert.IsFalse(upload.IsAndroid);
            Assert.AreEqual(new DateTime(2026, 10, 7, 23, 39, 20, DateTimeKind.Utc).AddTicks(838637), upload.StartedUtc);
            Assert.AreEqual(34.549726, upload.Duration.Value.TotalSeconds, 1e-6);
            Assert.AreEqual("https://vrchat.com/home/avatar/" + AvatarId, upload.PageUrl);
        }

        [Test]
        public void OtherReleasesAreNotUploads()
        {
            Assert.IsNull(UnitGitAvatarUpload.From(new UnitGitReleaseEntry { tool = "MCB", type = "mcb-version" }));
            Assert.IsNull(UnitGitAvatarUpload.From(null));
        }

        [Test]
        public void WithoutAValidIdThereIsNoPageAndNoDurationWithoutBothTimes()
        {
            var upload = UnitGitAvatarUpload.From(Upload("PC", "2026-10-07T23:39:55Z", "2026-10-07T23:39:20Z", null, "not-an-id"));
            Assert.IsNull(upload.PageUrl);
            Assert.IsNull(upload.Duration);
        }

        // VRChat keeps one upload per platform: the card shows every platform uploaded so far, not later ones.
        [Test]
        public void PlatformsSoFarCombinesEarlierUploadsOfTheSameAvatar()
        {
            var android = Upload("Android/Quest", "2026-10-01T10:00:00Z");
            var pc = Upload("PC", "2026-10-02T10:00:00Z");
            var laterOther = Upload("Android/Quest", "2026-10-01T09:00:00Z", avatarId: "avtr_00000000-0000-0000-0000-000000000000");
            var file = new UnitGitReleaseFile { releases = new List<UnitGitReleaseEntry> { android, pc, laterOther } };

            UnitGitAvatarUpload.From(pc).PlatformsSoFar(file, out bool pcSoFar, out bool androidSoFar);
            Assert.IsTrue(pcSoFar);
            Assert.IsTrue(androidSoFar);

            UnitGitAvatarUpload.From(android).PlatformsSoFar(file, out pcSoFar, out androidSoFar);
            Assert.IsFalse(pcSoFar, "The PC upload came later.");
            Assert.IsTrue(androidSoFar);
        }

        [Test]
        public void DurationsAndTimesReadNaturally()
        {
            Assert.AreEqual("850 ms", UnitGitTime.Duration(TimeSpan.FromMilliseconds(850)));
            Assert.AreEqual("34.5 s", UnitGitTime.Duration(TimeSpan.FromSeconds(34.549726)));
            Assert.AreEqual("2 min 05 s", UnitGitTime.Duration(TimeSpan.FromSeconds(125)));
            Assert.AreEqual("1 h 02 min", UnitGitTime.Duration(TimeSpan.FromMinutes(62)));

            Assert.IsTrue(UnitGitTime.TryParseUtc("2026-10-07T23:39:20.0838637Z", out DateTime utc));
            Assert.AreEqual(DateTimeKind.Utc, utc.Kind);
            Assert.AreEqual(23, utc.Hour);
            Assert.IsFalse(UnitGitTime.TryParseUtc("soon", out _));
            Assert.AreEqual("soon", UnitGitTime.LocalDate("soon"));

            var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Local);
            Assert.AreEqual("just now", UnitGitTime.Relative(now.AddSeconds(-20), now));
            Assert.AreEqual("5 min ago", UnitGitTime.Relative(now.AddMinutes(-5), now));
            Assert.AreEqual("3 h ago", UnitGitTime.Relative(now.AddHours(-3), now));
            Assert.AreEqual("yesterday", UnitGitTime.Relative(now.AddHours(-30), now));
        }
    }
}
