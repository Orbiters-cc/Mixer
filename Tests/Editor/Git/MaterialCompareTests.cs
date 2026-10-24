using System.IO;
using NUnit.Framework;
using Orbiters.UnitGit.Editor;
using UnityEditor;
using UnityEngine;

public class MaterialCompareTests
{
    [TestCase("Previous name", "Previous name")]
    [TestCase("\"Old/Body: \\\"fur\\\"\"", "Old/Body: \"fur\"")]
    [TestCase("A very long\n    material name", "A very long material name")]
    public void RenamedVersionsImportWithoutNameWarnings(string serializedName, string expectedName)
    {
        var folder = "Assets/__UnitGitMaterialTest-" + System.Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder);
        var path = folder + "/fixture.mat";
        Material preview = null;
        var warnings = new System.Collections.Generic.List<string>();
        Application.LogCallback onLog = (message, stack, type) =>
        {
            if (message.Contains("does not match filename")) warnings.Add(message);
        };
        try
        {
            var source = new Material(Shader.Find("Standard")) { color = Color.red };
            AssetDatabase.CreateAsset(source, path); AssetDatabase.SaveAssets();
            var original = File.ReadAllText(path);
            var historical = original.Replace("  m_Name: fixture", "  m_Name: " + serializedName);
            Assert.AreNotEqual(original, historical);
            Application.logMessageReceived += onLog;
            preview = MaterialCompareView.ReadMaterial(historical, path);
            Assert.AreEqual(expectedName, preview.name);
            Assert.AreEqual(Color.red, preview.color);
            Assert.IsEmpty(warnings);
            Assert.AreEqual(original, File.ReadAllText(path));
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            if (preview != null) Object.DestroyImmediate(preview);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    [Test]
    public void ImportsIndependentVersionsAndLeavesSourceAndSceneUntouched()
    {
        var folder = "Assets/__UnitGitMaterialTest-" + System.Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder);
        var path = folder + "/fixture.mat";
        Material before = null, after = null;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.rootCount;
        try
        {
            var source = new Material(Shader.Find("Standard"));
            source.color = Color.red;
            AssetDatabase.CreateAsset(source, path); AssetDatabase.SaveAssets();
            var red = File.ReadAllText(path);
            source.color = Color.blue; EditorUtility.SetDirty(source); AssetDatabase.SaveAssets();
            var blue = File.ReadAllText(path);
            before = MaterialCompareView.ReadMaterial(red, path);
            after = MaterialCompareView.ReadMaterial(blue, path);
            Assert.AreEqual(Color.red, before.color);
            Assert.AreEqual(Color.blue, after.color);
            Assert.AreEqual(blue, File.ReadAllText(path));
            Assert.AreEqual(roots, scene.rootCount);
            Assert.IsNull(MaterialCompareView.ReadMaterial("", path));
            Assert.AreEqual(0, Directory.GetFiles("Assets/__UnitGit Compare", "*.mat", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (before != null) Object.DestroyImmediate(before);
            if (after != null) Object.DestroyImmediate(after);
            AssetDatabase.DeleteAsset(folder);
        }
    }
}
