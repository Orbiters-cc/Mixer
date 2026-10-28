using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Orbiters.Mixer.Splats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats.Tests
{
    public sealed class SplatTests
    {
        /// <summary>A splat as Orbiters - N4N0 holds it: Unity world, log scales, opacity logit, encoded sRGB colour.</summary>
        struct Gaussian
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 logScale;
            public float opacityLogit;
            public Color color;
        }

        /// <summary>Writes splats the way Orbiters - N4N0's ply::write does (Z mirrored), with `extra` float
        /// properties after the colour that a reader must skip.</summary>
        static byte[] Ply(IList<Gaussian> splats, params string[] extra) => Ply(splats, (splat, property) => 123.0f, extra);

        /// <summary>Same, with each extra property's value for each splat (by its index) from <paramref name="value"/>.</summary>
        static byte[] Ply(IList<Gaussian> splats, Func<int, string, float> value, params string[] extra)
        {
            var props = new List<string> { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" };
            props.AddRange(extra);
            props.AddRange(new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" });
            var header = new StringBuilder("ply\nformat binary_little_endian 1.0\ncomment test\nelement vertex " + splats.Count + "\n");
            foreach (string p in props)
                header.Append("property float ").Append(p).Append('\n');
            header.Append("end_header\n");
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(header.ToString()));
            for (int index = 0; index < splats.Count; index++)
            {
                Gaussian g = splats[index];
                var v = new List<float> { g.position.x, g.position.y, -g.position.z, 0, 0, 0 };
                for (int c = 0; c < 3; c++)
                    v.Add((g.color[c] - 0.5f) / PlyReader.ShC0);
                foreach (string property in extra)
                    v.Add(value(index, property));
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

        /// <summary>Orbiters - N4N0's record of what the keyframes saw of each splat, and its sky flag, come along where
        /// the PLY has them; a PLY without them says nothing.</summary>
        [Test]
        public void ReadsWhatTheKeyframesSawOfEachSplat()
        {
            SplatCloud with = PlyReader.Read(Ply(new[] { A, B }, PlyReader.Seen));
            SplatCloud without = PlyReader.Read(Ply(new[] { A }));
            try
            {
                Assert.That(with.seen, Is.EqualTo(new[] { new Vector4(123f, 123f, 123f, 123f), new Vector4(123f, 123f, 123f, 123f) }));
                Assert.That(with.sky, Is.EqualTo(new[] { true, true }));
                Assert.That(with.beyond, Is.EqualTo(new[] { 123f, 123f }));
                // Every splat on the sky shell: nothing to see the horizon from.
                Assert.That(with.hasHorizon, Is.False);
                Assert.That(without.seen, Is.Empty);
                Assert.That(without.sky, Is.Empty);
                // Uploaded: known where the cloud says, unknown (a negative weight) where it does not.
                Assert.That(SplatRenderer.Pack(with)[1].seen, Is.EqualTo(new Vector4(123f, 123f, 123f, 123f)));
                Assert.That(SplatRenderer.Pack(with)[1].flags, Is.EqualTo(1u));
                Assert.That(SplatRenderer.Pack(with)[1].beyond, Is.EqualTo(123f));
                Assert.That(SplatRenderer.Pack(without)[0].seen.x, Is.LessThan(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(with);
                UnityEngine.Object.DestroyImmediate(without);
            }
        }

        /// <summary>The horizon's colour: the sky shell's splats near the horizon as seen from the middle of the rest, by
        /// opacity; the zenith and the ground's colours stay out of it.</summary>
        [Test]
        public void TheHorizonIsTheSkyNearTheHorizon()
        {
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(400, 10, 0), new Vector3(0, 20, -400), new Vector3(0, 400, 0) };
            var colors = new[] { new Color32(10, 200, 10, 255), new Color32(10, 200, 10, 255), new Color32(100, 150, 250, 255), new Color32(100, 150, 250, 128), new Color32(250, 0, 0, 255) };
            var sky = new[] { false, false, true, true, true };
            Assert.That(PlyReader.Horizon(positions, colors, sky, out Color horizon), Is.True);
            Assert.That(horizon.r, Is.EqualTo(100f / 255f).Within(1e-4f));
            Assert.That(horizon.b, Is.EqualTo(250f / 255f).Within(1e-4f));
            Assert.That(PlyReader.Horizon(positions, colors, new bool[0], out _), Is.False);
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

        /// <summary>A rotation that is not a number (infinite, NaN) is a diverged splat: left out, never uploaded as NaN.</summary>
        [Test]
        public void LeavesOutSplatsWhoseRotationIsNotANumber()
        {
            var infinite = B;
            infinite.rotation = new Quaternion(float.PositiveInfinity, 0f, 0f, 1f);
            var nan = B;
            nan.rotation = new Quaternion(0f, float.NaN, 0f, 1f);
            var none = B;
            none.rotation = new Quaternion(0f, 0f, 0f, 0f);
            SplatCloud cloud = PlyReader.Read(Ply(new[] { A, infinite, nan, none }));
            try
            {
                Assert.That(cloud.Count, Is.EqualTo(2));
                AreClose(A.rotation, cloud.rotations[0]);
                Assert.That(cloud.rotations[1], Is.EqualTo(Quaternion.identity), "a rotation of length zero is none");
                foreach (var packed in SplatRenderer.Pack(cloud))
                    Assert.That(float.IsNaN(packed.cov0.x + packed.cov0.y + packed.cov0.z + packed.cov1.x + packed.cov1.y + packed.cov1.z), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        /// <summary>The sky flag goes to the GPU too: a reimport that only changes which splats are sky uploads again.</summary>
        [Test]
        public void TheContentHashFollowsTheSkyFlags()
        {
            SplatCloud ground = PlyReader.Read(Ply(new[] { A, B }, (splat, property) => property == "sky" ? 0f : 0.5f, PlyReader.Seen));
            SplatCloud sky = PlyReader.Read(Ply(new[] { A, B }, (splat, property) => property == "sky" ? (splat == 1 ? 1f : 0f) : 0.5f, PlyReader.Seen));
            try
            {
                Assert.That(sky.sky, Is.EqualTo(new[] { false, true }));
                Assert.That(sky.contentHash, Is.Not.EqualTo(ground.contentHash));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ground);
                UnityEngine.Object.DestroyImmediate(sky);
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

        const int Size = 64;

        /// <summary>One splat seen head-on 3 m away: white at the given opacity (0 to 255), round, 0.3 m wide.</summary>
        static SplatCloud White(byte opacity, params float[] distances)
        {
            var cloud = ScriptableObject.CreateInstance<SplatCloud>();
            cloud.hideFlags = HideFlags.HideAndDontSave;
            cloud.positions = Array.ConvertAll(distances, z => new Vector3(0f, 0f, z));
            cloud.rotations = Array.ConvertAll(distances, _ => Quaternion.identity);
            cloud.scales = Array.ConvertAll(distances, _ => new Vector3(0.3f, 0.3f, 0.3f));
            cloud.colors = Array.ConvertAll(distances, _ => new Color32(255, 255, 255, opacity));
            cloud.contentHash = opacity + ":" + string.Join(",", distances);
            return cloud;
        }

        static Color32[] Render(SplatCloud cloud, params GameObject[] scene) => Render(cloud, true, true, 1, scene);

        /// <summary>
        /// Renders <paramref name="cloud"/> and <paramref name="scene"/> with a hidden camera at the origin looking along +Z
        /// over black, into an 8-bit sRGB target with <paramref name="targetSamples"/> MSAA samples: the pixels as stored
        /// (bottom row first). Nothing touches the open scene.
        /// </summary>
        static Color32[] Render(SplatCloud cloud, bool hdr, bool msaa, int targetSamples, params GameObject[] scene)
        {
            var target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = targetSamples, hideFlags = HideFlags.HideAndDontSave };
            var read = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            GameObject splats = EditorUtility.CreateGameObjectWithHideFlags("Mixer splat test", HideFlags.HideAndDontSave);
            GameObject eye = EditorUtility.CreateGameObjectWithHideFlags("Mixer splat test camera", HideFlags.HideAndDontSave, typeof(Camera));
            try
            {
                splats.layer = 31;
                foreach (GameObject o in scene)
                    o.layer = 31;
                splats.AddComponent<SplatRenderer>().cloud = cloud;
                var cam = eye.GetComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 1 << 31;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.targetTexture = target;
                cam.allowHDR = hdr;
                cam.allowMSAA = msaa;
                cam.Render();
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                read.Apply();
                RenderTexture.active = active;
                cam.targetTexture = null;
                return read.GetPixels32();
            }
            finally
            {
                if (splats != null)
                    UnityEngine.Object.DestroyImmediate(splats);
                UnityEngine.Object.DestroyImmediate(eye);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(cloud);
            }
        }

        static Color32 At(Color32[] pixels, int x, int y) => pixels[y * Size + x];

        [Test]
        public void SplatsBlendOnTheirEncodedColoursAsTheyWereTrained()
        {
            // Orbiters - N4N0's trainer blends the encoded sRGB colours: white at half opacity over black stores 0.5
            // (128); blending in linear light showed 0.735 (187). Two of them: 0.752 (192, not 226).
            Assert.That(At(Render(White(128, 3f)), Size / 2, Size / 2).r, Is.InRange(126, 130));
            Assert.That(At(Render(White(128, 3.1f, 3f)), Size / 2, Size / 2).r, Is.InRange(190, 194));
            // Opaque colours come out as they went in.
            Assert.That(At(Render(White(255, 3f)), Size / 2, Size / 2).r, Is.InRange(250, 255));
        }

        [Test]
        public void SplatsDrawWhateverTheCameraRendersInto()
        {
            // The splats share the depth buffer of what the camera draws into, so their target must have its MSAA samples
            // (SplatRenderer.Samples); with any other count nothing would draw.
            foreach (bool hdr in new[] { false, true })
            foreach (bool msaa in new[] { false, true })
            foreach (int samples in new[] { 1, 4 })
                Assert.That(At(Render(White(255, 3f), hdr, msaa, samples), Size / 2, Size / 2).r, Is.GreaterThan(240), "HDR " + hdr + ", MSAA " + msaa + ", " + samples + " samples");
        }

        [Test]
        public void TheSceneInFrontHidesSplats()
        {
            var black = new Material(Shader.Find("Unlit/Color")) { color = Color.black, hideFlags = HideFlags.HideAndDontSave };
            GameObject cube = EditorUtility.CreateGameObjectWithHideFlags("Mixer splat test cube", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
            try
            {
                cube.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                cube.GetComponent<MeshRenderer>().sharedMaterial = black;
                // 8 px wide in front of the splat's centre (the splat is about 12 px to 1 σ), drawn by a shader without a
                // shadow-caster pass, so it is missing from the camera's depth texture but not from its depth buffer.
                cube.transform.position = new Vector3(0f, 0f, 1.5f);
                cube.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                Color32[] pixels = Render(White(255, 3f), cube);
                Assert.That(At(pixels, Size / 2, Size / 2).r, Is.LessThan(8), "behind the cube");
                Assert.That(At(pixels, Size / 2 + 7, Size / 2).r, Is.GreaterThan(150), "beside it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(black);
            }
        }

        /// <summary>A sky map <paramref name="width"/> texels across as Orbiters - N4N0 writes it (a PNG, rows from the
        /// top) and Mixer loads it: <paramref name="colour"/> of each texel by column and row from the top.</summary>
        static byte[] SkyPng(int width, Func<int, int, Color32> colour)
        {
            int height = width / 2;
            var pixels = new Color32[width * height];
            for (int row = 0; row < height; row++)
                for (int x = 0; x < width; x++)
                    pixels[(height - 1 - row) * width + x] = colour(x, row);
            var image = new Texture2D(width, height, TextureFormat.RGB24, false, true);
            try
            {
                image.SetPixels32(pixels);
                image.Apply(false, false);
                return image.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        static Texture2D Sky(int width, Func<int, int, Color32> colour)
        {
            Texture2D map = SkyMaps.Load(SkyPng(width, colour), out string problem);
            Assert.That(map, Is.Not.Null, problem);
            map.hideFlags = HideFlags.HideAndDontSave;
            return map;
        }

        static void AreClose(Color32 want, Color32 got, string what)
        {
            Assert.That(got.r, Is.InRange(want.r - 3, want.r + 3), what + ": " + got);
            Assert.That(got.g, Is.InRange(want.g - 3, want.g + 3), what + ": " + got);
            Assert.That(got.b, Is.InRange(want.b - 3, want.b + 3), what + ": " + got);
        }

        [Test]
        public void TheSkyMapShowsWhereNothingNearerWasDrawn()
        {
            Texture2D sky = Sky(64, (x, row) => new Color32(64, 128, 192, 255));
            var black = new Material(Shader.Find("Unlit/Color")) { color = Color.black, hideFlags = HideFlags.HideAndDontSave };
            GameObject cube = EditorUtility.CreateGameObjectWithHideFlags("Mixer splat test cube", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
            try
            {
                cube.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                cube.GetComponent<MeshRenderer>().sharedMaterial = black;
                // Up and to the right, 2 m away: about 12 px wide around (53, 53).
                cube.transform.position = new Vector3(0.35f, 0.35f, 2f);
                cube.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
                foreach (bool hdr in new[] { false, true })
                foreach (bool msaa in new[] { false, true })
                {
                    string variant = "HDR " + hdr + ", MSAA " + msaa;
                    SplatCloud cloud = White(255, 10f);
                    cloud.skyMap = sky;
                    Color32[] pixels = Render(cloud, hdr, msaa, 1, cube);
                    // The map's encoded colour, stored as such in an sRGB target, as Orbiters - N4N0 shows it.
                    AreClose(new Color32(64, 128, 192, 255), At(pixels, 2, 2), variant + ", the sky");
                    Assert.That(At(pixels, Size / 2, Size / 2).r, Is.GreaterThan(240), variant + ", the splat in front of it");
                    Assert.That(At(pixels, 53, 53).b, Is.LessThan(8), variant + ", the scene in front of it");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(black);
                UnityEngine.Object.DestroyImmediate(sky);
            }
        }

        [Test]
        public void TheSkyMapIsLookedUpByDirectionAsTrained()
        {
            // West of straight ahead (+Z, the map's middle column) red, east green; above the horizon bright blue.
            Texture2D sky = Sky(64, (x, row) => new Color32((byte)(x < 32 ? 200 : 0), (byte)(x < 32 ? 0 : 200), (byte)(row < 16 ? 200 : 40), 255));
            try
            {
                SplatCloud cloud = White(255, 10f);
                cloud.skyMap = sky;
                Color32[] pixels = Render(cloud);
                // Stored from the bottom row: (16, 48) is left and up, (48, 16) right and down, 7.4° off the axis each way.
                AreClose(new Color32(200, 0, 200, 255), At(pixels, 16, 48), "left, above the horizon");
                AreClose(new Color32(0, 200, 40, 255), At(pixels, 48, 16), "right, below it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sky);
            }
        }

        [Test]
        public void TheSkyMapComesWithItsSplats()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mixer-splats-" + Guid.NewGuid().ToString("N"), "reconstruction");
            Directory.CreateDirectory(dir);
            string title = "Mixer test " + Guid.NewGuid().ToString("N");
            string asset = SplatImport.Folder + "/" + title + ".splats";
            try
            {
                string ply = Path.Combine(dir, "splats.ply");
                File.WriteAllBytes(ply, Ply(new[] { A, B }));
                // The band from 2° below the horizon to 12° above (rows 14 and 15 of 32) in one colour, the rest dark.
                File.WriteAllBytes(Path.Combine(dir, SkyMaps.FileName), SkyPng(64, (x, row) => row == 14 || row == 15 ? new Color32(90, 120, 150, 255) : new Color32(10, 10, 10, 255)));
                SplatCloud cloud = SplatImport.Bring(ply, title);
                Assert.That(File.Exists(SkyMaps.Beside(asset)), Is.True, "the map beside the splats");
                Assert.That(cloud.skyMap, Is.Not.Null);
                Assert.That(cloud.skyMap.width, Is.EqualTo(64));
                Assert.That(cloud.skyMap.height, Is.EqualTo(32));
                Assert.That(cloud.skyMap.wrapModeU, Is.EqualTo(TextureWrapMode.Repeat));
                Assert.That(cloud.skyMap.wrapModeV, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(cloud.skyMap.isDataSRGB, Is.False, "encoded values filtered as they are");
                Assert.That(AssetDatabase.GetAssetPath(cloud.skyMap), Is.EqualTo(asset), "kept inside the cloud's asset");
                Assert.That(cloud.hasHorizon, Is.True);
                Assert.That(cloud.horizon.r, Is.EqualTo(90f / 255f).Within(1e-4f), "the horizon from the map's band");
                Assert.That(cloud.horizon.b, Is.EqualTo(150f / 255f).Within(1e-4f));
            }
            finally
            {
                AssetDatabase.DeleteAsset(asset);
                AssetDatabase.DeleteAsset(SkyMaps.Beside(asset));
                Directory.Delete(Path.GetDirectoryName(dir), true);
            }
        }

        [Test]
        public void ASkyMapIsTwiceAsWideAsHigh()
        {
            var square = new Texture2D(8, 8, TextureFormat.RGB24, false, true);
            try
            {
                Assert.That(SkyMaps.Load(square.EncodeToPNG(), out string problem), Is.Null);
                StringAssert.Contains("twice as wide as high", problem);
                Assert.That(SkyMaps.Load(Encoding.ASCII.GetBytes("not a picture"), out _), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(square);
            }
        }

        [Test]
        public void SplatsShowOnlyInCamerasOfTheirScene()
        {
            var preview = EditorSceneManager.NewPreviewScene();
            var go = new GameObject("Mixer splat test camera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, preview);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                var main = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
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
