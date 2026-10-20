using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Orbiters.Mixer.Splats;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats.Tests
{
    public sealed class SplatTests
    {
        /// <summary>A splat as Orbiters desktop holds it: Unity world, log scales, opacity logit, encoded sRGB colour.</summary>
        struct Gaussian
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 logScale;
            public float opacityLogit;
            public Color color;
        }

        /// <summary>Writes splats the way Orbiters desktop's ply::write does (Z mirrored), with `extra` float
        /// properties after the colour that a reader must skip.</summary>
        static byte[] Ply(IList<Gaussian> splats, params string[] extra)
        {
            var props = new List<string> { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" };
            props.AddRange(extra);
            props.AddRange(new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" });
            var header = new StringBuilder("ply\nformat binary_little_endian 1.0\ncomment test\nelement vertex " + splats.Count + "\n");
            foreach (string p in props)
                header.Append("property float ").Append(p).Append('\n');
            header.Append("end_header\n");
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(header.ToString()));
            foreach (Gaussian g in splats)
            {
                var v = new List<float> { g.position.x, g.position.y, -g.position.z, 0, 0, 0 };
                for (int c = 0; c < 3; c++)
                    v.Add((g.color[c] - 0.5f) / PlyReader.ShC0);
                foreach (string _ in extra)
                    v.Add(123.0f);
                v.Add(g.opacityLogit);
                v.AddRange(new[] { g.logScale.x, g.logScale.y, g.logScale.z });
                v.AddRange(new[] { g.rotation.w, -g.rotation.x, -g.rotation.y, g.rotation.z });
                foreach (float f in v)
                    bytes.AddRange(BitConverter.GetBytes(f));
            }
            return bytes.ToArray();
        }

        /// <summary>The PLY with <paramref name="find"/> in its header replaced by <paramref name="replace"/>.</summary>
        static byte[] EditHeader(byte[] ply, string find, string replace)
        {
            string text = Encoding.ASCII.GetString(ply, 0, Math.Min(ply.Length, 4096));
            int end = text.IndexOf("\nend_header\n", StringComparison.Ordinal) + "\nend_header\n".Length;
            string header = text.Substring(0, end);
            Assert.That(header.Contains(find), find);
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(header.Replace(find, replace)));
            bytes.AddRange(new ArraySegment<byte>(ply, end, ply.Length - end));
            return bytes.ToArray();
        }

        static readonly Gaussian A = new Gaussian
        {
            position = new Vector3(1.5f, 2f, -3f),
            rotation = Quaternion.Euler(10, 70, -25),
            logScale = new Vector3(Mathf.Log(0.1f), Mathf.Log(0.02f), Mathf.Log(0.3f)),
            opacityLogit = 1.2f,
            color = new Color(0.8f, 0.4f, 0.2f),
        };

        static readonly Gaussian B = new Gaussian
        {
            position = new Vector3(-4f, 0.5f, 7f),
            rotation = Quaternion.Euler(-40, 5, 80),
            logScale = new Vector3(Mathf.Log(1f), Mathf.Log(0.5f), Mathf.Log(0.25f)),
            opacityLogit = -2f,
            color = new Color(0.1f, 0.6f, 0.5f),
        };

        static void AreClose(Quaternion want, Quaternion got)
        {
            // q and −q are the same rotation.
            Assert.That(Mathf.Abs(Quaternion.Dot(want, got)), Is.EqualTo(1f).Within(1e-5f), want + " vs " + got);
        }

        [Test]
        public void ReadsOrbitersDesktopSplatsBackIntoUnitysWorld()
        {
            SplatCloud cloud = PlyReader.Read(Ply(new[] { A, B }));
            try
            {
                Assert.That(cloud.Count, Is.EqualTo(2));
                Assert.That(Vector3.Distance(cloud.positions[0], A.position), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(cloud.positions[1], B.position), Is.LessThan(1e-5f));
                AreClose(A.rotation, cloud.rotations[0]);
                AreClose(B.rotation, cloud.rotations[1]);
                Assert.That(Vector3.Distance(cloud.scales[0], new Vector3(0.1f, 0.02f, 0.3f)), Is.LessThan(1e-5f));
                Assert.That(cloud.colors[0].a, Is.EqualTo((byte)Mathf.RoundToInt(255f / (1f + Mathf.Exp(-1.2f)))));
                Assert.That(cloud.colors[0].r, Is.EqualTo((byte)204));
                Assert.That(cloud.colors[0].g, Is.EqualTo((byte)102));
                Assert.That(cloud.colors[1].g, Is.EqualTo((byte)153));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        [Test]
        public void SkipsPropertiesItDoesNotUse()
        {
            SplatCloud cloud = PlyReader.Read(Ply(new[] { A }, "f_rest_0", "f_rest_1", "f_rest_2"));
            try
            {
                Assert.That(Vector3.Distance(cloud.positions[0], A.position), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(cloud.scales[0], new Vector3(0.1f, 0.02f, 0.3f)), Is.LessThan(1e-5f));
                AreClose(A.rotation, cloud.rotations[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        [Test]
        public void RefusesWhatIsNotASplatPly()
        {
            Assert.Throws<FormatException>(() => PlyReader.Read(Encoding.ASCII.GetBytes("ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nend_header\n0\n")));
            Assert.Throws<FormatException>(() => PlyReader.Read(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement vertex 1\nproperty float x\nend_header\n0000")));
            byte[] cut = Ply(new[] { A, B });
            Assert.Throws<FormatException>(() => PlyReader.Read(new ArraySegment<byte>(cut, 0, cut.Length - 8).ToArray()));
        }

        [Test]
        public void ReadsPastACommentThatMentionsTheHeadersEnd()
        {
            SplatCloud cloud = PlyReader.Read(EditHeader(Ply(new[] { A, B }), "comment test\n", "comment end_header=soon\n"));
            try
            {
                Assert.That(cloud.Count, Is.EqualTo(2));
                Assert.That(Vector3.Distance(cloud.positions[1], B.position), Is.LessThan(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        [Test]
        public void RefusesPlysWithoutAFormatOrWithTooManySplats()
        {
            byte[] ply = Ply(new[] { A });
            Assert.Throws<FormatException>(() => PlyReader.Read(EditHeader(ply, "format binary_little_endian 1.0\n", "")));
            // Refused from the header, before anything is allocated for the splats it claims.
            var e = Assert.Throws<FormatException>(() => PlyReader.Read(EditHeader(ply, "element vertex 1\n", "element vertex " + (SplatCloud.MaxCount + 1) + "\n")));
            StringAssert.Contains("at most", e.Message);
            Assert.Throws<FormatException>(() => PlyReader.Read(EditHeader(ply, "element vertex 1\n", "element vertex -1\n")));
        }

        [Test]
        public void LeavesOutDivergedSplats()
        {
            var huge = B;
            huge.logScale = new Vector3(200f, 0f, 0f);
            var lost = B;
            lost.position = new Vector3(float.NaN, 0f, 0f);
            SplatCloud cloud = PlyReader.Read(Ply(new[] { A, huge, lost, B }));
            try
            {
                Assert.That(cloud.Count, Is.EqualTo(2));
                Assert.That(Vector3.Distance(cloud.positions[0], A.position), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(cloud.positions[1], B.position), Is.LessThan(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        [Test]
        public void TheContentHashFollowsTheSplats()
        {
            var moved = B;
            moved.position += Vector3.up;
            SplatCloud first = PlyReader.Read(Ply(new[] { A, B }));
            SplatCloud same = PlyReader.Read(Ply(new[] { A, B }));
            SplatCloud other = PlyReader.Read(Ply(new[] { A, moved }));
            try
            {
                Assert.That(first.contentHash, Is.Not.Empty);
                Assert.That(same.contentHash, Is.EqualTo(first.contentHash));
                Assert.That(other.contentHash, Is.Not.EqualTo(first.contentHash), "same count, other splats");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(same);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void ABrokenPlyNeverLandsInTheProject()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixer-splats-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string ply = Path.Combine(dir, "splats.ply");
                byte[] cut = Ply(new[] { A, B });
                File.WriteAllBytes(ply, new ArraySegment<byte>(cut, 0, cut.Length - 8).ToArray());
                string title = "Mixer test " + Guid.NewGuid().ToString("N");
                Assert.Throws<FormatException>(() => SplatImport.Import(ply, title));
                Assert.That(File.Exists(SplatImport.Folder + "/" + title + ".splats"), Is.False);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void CovarianceIsRotationTimesScaleSquaredTimesItsTranspose()
        {
            Quaternion q = Quaternion.Euler(30, -60, 15);
            var s = new Vector3(0.5f, 2f, 0.1f);
            SplatRenderer.Covariance(q, s, out Vector3 cov0, out Vector3 cov1);
            Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, q, s);
            Matrix4x4 sigma = m * m.transpose;
            Assert.That(cov0.x, Is.EqualTo(sigma[0, 0]).Within(1e-5f));
            Assert.That(cov0.y, Is.EqualTo(sigma[0, 1]).Within(1e-5f));
            Assert.That(cov0.z, Is.EqualTo(sigma[0, 2]).Within(1e-5f));
            Assert.That(cov1.x, Is.EqualTo(sigma[1, 1]).Within(1e-5f));
            Assert.That(cov1.y, Is.EqualTo(sigma[1, 2]).Within(1e-5f));
            Assert.That(cov1.z, Is.EqualTo(sigma[2, 2]).Within(1e-5f));
        }

        [Test]
        public void TheCoreLeavesTheFarSkyOut()
        {
            var positions = new List<Vector3>();
            var rng = new System.Random(3);
            for (int i = 0; i < 2000; i++)
                positions.Add(new Vector3((float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 2, (float)rng.NextDouble() * 4 - 2));
            // Most splats in a sky shell 400 m out, all around.
            for (int i = 0; i < 6000; i++)
            {
                Vector3 d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() * 0.5f, (float)rng.NextDouble() - 0.5f).normalized;
                positions.Add(d * 400f);
            }
            Bounds core = PlyReader.Core(positions.ToArray());
            Assert.That(core.max.x, Is.LessThan(3f));
            Assert.That(core.min.x, Is.LessThan(-1.5f));
            Assert.That(core.max.y, Is.LessThan(3f));
        }

        [Test]
        public void TheCoreLeavesOutNaNsAndSplatsBillionsOfMetresAway()
        {
            var positions = new List<Vector3>();
            for (int i = 0; i < 100; i++)
                positions.Add(new Vector3(i * 0.01f, 1f, 0f));
            for (int i = 0; i < 1000; i++)
                positions.Add(i % 2 == 0 ? new Vector3(float.NaN, 0f, 0f) : new Vector3(5e9f, -5e9f, 1e12f));
            Bounds core = PlyReader.Core(positions.ToArray());
            Assert.That(core.center.y, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(core.max.x, Is.LessThan(1.1f));
            Assert.That(PlyReader.Core(new[] { new Vector3(float.NaN, 0f, 0f) }).size, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void SplatsShowOnlyInCamerasOfTheirScene()
        {
            var preview = EditorSceneManager.NewPreviewScene();
            var go = new GameObject("Mixer splat test camera") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                var main = default(UnityEngine.SceneManagement.Scene);
                Assert.That(SplatRenderer.Shows(cam, main), Is.True, "a camera kept to no scene shows every scene");
                cam.scene = preview;
                Assert.That(SplatRenderer.Shows(cam, main), Is.False, "Prefab Mode, a preview scene");
                Assert.That(SplatRenderer.Shows(cam, preview), Is.True);
                cam.cameraType = CameraType.Preview;
                Assert.That(SplatRenderer.Shows(cam, preview), Is.False, "previews and thumbnails");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void TakesOnlyImportRequests()
        {
            var r = SplatInbox.Parse("{\"kind\":\"orbiters.mixer.request\",\"action\":\"import-splats\",\"session\":\"C:/Sessions/Canine\",\"title\":\"Canine\"}");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.session, Is.EqualTo("C:/Sessions/Canine"));
            Assert.That(r.title, Is.EqualTo("Canine"));
            Assert.That(SplatInbox.Parse("{\"kind\":\"other\",\"action\":\"import-splats\",\"session\":\"x\"}"), Is.Null);
            Assert.That(SplatInbox.Parse("{\"kind\":\"orbiters.mixer.request\",\"action\":\"import-splats\"}"), Is.Null);
            Assert.That(SplatInbox.Parse("not json"), Is.Null);
        }

        [Test]
        public void TakesSplatsOnlyFromSessionsOnThisComputer()
        {
            string session = Path.Combine(Path.GetTempPath(), "Canine");
            Assert.That(SplatInbox.SessionSplats(session), Is.EqualTo(Path.Combine(Path.GetFullPath(session), "reconstruction", "splats.ply")));
            Assert.Throws<ArgumentException>(() => SplatInbox.SessionSplats(@"\\host\share\Canine"));
            Assert.Throws<ArgumentException>(() => SplatInbox.SessionSplats("//host/share/Canine"));
            Assert.Throws<ArgumentException>(() => SplatInbox.SessionSplats("Sessions/Canine"));
        }

        [Test]
        public void ClearsTheInboxesOfEditorsThatAreGone()
        {
            string inbox = Path.Combine(Path.GetTempPath(), "mixer-inbox-" + Guid.NewGuid().ToString("N"));
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            // No process ever has this number.
            string dead = Path.Combine(inbox, int.MaxValue.ToString()), own = Path.Combine(inbox, self.ToString()), other = Path.Combine(inbox, "notes");
            foreach (string dir in new[] { dead, own, other })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "endpoint.json"), "{}");
            }
            File.WriteAllText(Path.Combine(dead, "request.json"), "{}");
            try
            {
                SplatInbox.SweepDead(inbox, self);
                Assert.That(Directory.Exists(dead), Is.False);
                Assert.That(File.Exists(Path.Combine(own, "endpoint.json")), Is.True);
                Assert.That(File.Exists(Path.Combine(other, "endpoint.json")), Is.True, "not an editor's folder");
            }
            finally
            {
                Directory.Delete(inbox, true);
            }
        }

        [Test]
        public void TitlesComeFromTheSessionFolder()
        {
            string ply = Path.Combine(Path.GetTempPath(), "Canine 2026-10-20 14-27-07", "reconstruction", "splats.ply");
            Assert.That(SplatImport.TitleOf(ply), Is.EqualTo("Canine 2026-10-20 14-27-07"));
            Assert.That(SplatImport.TitleOf(Path.Combine(Path.GetTempPath(), "loose.ply")), Is.EqualTo("loose"));
        }
    }
}
