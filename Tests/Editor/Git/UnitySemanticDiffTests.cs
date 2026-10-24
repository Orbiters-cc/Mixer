using System.Linq;
using NUnit.Framework;
using Orbiters.UnitGit.Editor.Semantic;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitySemanticDiffTests
    {
        private const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        private static string Avatar(string position = "{x: 0, y: 0, z: 0}", string name = "Hips", string pull = "0.2", bool extraCollider = false, string overrideY = "1.5")
        {
            return Header +
                "--- !u!1 &100\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n  - component: {fileID: 101}\n  - component: {fileID: 102}\n" +
                "  m_Layer: 0\n  m_Name: " + name + "\n  m_IsActive: 1\n" +
                "--- !u!4 &101\nTransform:\n  m_GameObject: {fileID: 100}\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: " + position + "\n  m_Children: []\n  m_Father: {fileID: 201}\n" +
                "--- !u!114 &102\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Enabled: 1\n  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5,\n    type: 3}\n" +
                "  m_Name: \n  pull: " + pull + "\n  ignoreTransforms:\n  - {fileID: 101}\n  pullCurve:\n    serializedVersion: 2\n    m_Curve: []\n" +
                "--- !u!1 &200\nGameObject:\n  m_Component:\n  - component: {fileID: 201}\n  m_Name: Armature\n" +
                "--- !u!4 &201\nTransform:\n  m_GameObject: {fileID: 200}\n  m_LocalPosition: {x: 0, y: 0, z: 0}\n  m_Father: {fileID: 0}\n" +
                (extraCollider ? "--- !u!114 &103\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Script: {fileID: 11500000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n  radius: 0.1\n" : "") +
                "--- !u!1001 &300\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 201}\n" +
                "    m_Modifications:\n    - target: {fileID: -867, guid: 1fd59d31e538ede4f9f29daf80fd0687,\n        type: 3}\n      propertyPath: m_Name\n      value: Hoodie\n      objectReference: {fileID: 0}\n" +
                "    - target: {fileID: -867, guid: 1fd59d31e538ede4f9f29daf80fd0687,\n        type: 3}\n      propertyPath: m_LocalPosition.y\n      value: " + overrideY + "\n      objectReference: {fileID: 0}\n" +
                "    m_RemovedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: 1fd59d31e538ede4f9f29daf80fd0687, type: 3}\n";
        }

        [Test]
        public void ParserReadsUnityYaml()
        {
            var file = UnityYaml.Parse(Avatar());
            Assert.That(file.Documents.Count, Is.EqualTo(6));
            var behaviour = file.ById[102];
            Assert.That(behaviour.TypeName, Is.EqualTo("MonoBehaviour"));
            // An inline map wrapped over two lines.
            Assert.That(UnitySemanticDiff.Reference(behaviour.Body["m_Script"]).guid, Is.EqualTo("2a2c05204084d904aa4945ccff20d8e5"));
            Assert.That(((YamlSequence)behaviour.Body["ignoreTransforms"]).Items.Count, Is.EqualTo(1));
            Assert.That(((YamlMap)behaviour.Body["pullCurve"]).Text("serializedVersion"), Is.EqualTo("2"));
            Assert.That(behaviour.Body.Text("m_Name"), Is.EqualTo(string.Empty));
            var modifications = (YamlSequence)((YamlMap)file.ById[300].Body["m_Modification"])["m_Modifications"];
            Assert.That(modifications.Items.Count, Is.EqualTo(2));
            Assert.That(((YamlMap)modifications.Items[1]).Text("value"), Is.EqualTo("1.5"));
            Assert.That(UnitySemanticDiff.HierarchyPath(file, 100), Is.EqualTo("Armature/Hips"));
        }

        [Test]
        public void IdenticalFilesHaveNoChanges()
        {
            Assert.That(UnitySemanticDiff.Compare(Avatar(), Avatar()).Objects, Is.Empty);
        }

        [Test]
        public void ChangesAreGroupedByObjectAndComponent()
        {
            var set = UnitySemanticDiff.Compare(Avatar(), Avatar(position: "{x: 0, y: 1.25, z: 0}", name: "Hips2", pull: "0.4", extraCollider: true, overrideY: "2"));
            var hips = set.Objects.Single(o => o.FileId == 100);
            Assert.That(hips.Path, Is.EqualTo("Armature/Hips2"));
            Assert.That(hips.Kind, Is.EqualTo(SemanticChangeKind.Modified));
            var gameObject = hips.Components.First();
            Assert.That(gameObject.TypeName, Is.EqualTo("GameObject"));
            Assert.That(gameObject.Properties.Single().Label, Is.EqualTo("Name"));
            var transform = hips.Components.Single(c => c.TypeName == "Transform");
            Assert.That(transform.Properties.Single().Label, Is.EqualTo("Local Position"));
            var physBone = hips.Components.Single(c => c.FileId == 102);
            Assert.That(physBone.Properties.Single().Before, Is.EqualTo("0.2"));
            Assert.That(physBone.ScriptGuid, Is.EqualTo("2a2c05204084d904aa4945ccff20d8e5"));
            Assert.That(hips.Components.Single(c => c.FileId == 103).Kind, Is.EqualTo(SemanticChangeKind.Added));

            var hoodie = set.Objects.Single(o => o.PrefabInstance);
            Assert.That(hoodie.Name, Is.EqualTo("Hoodie"));
            Assert.That(hoodie.Path, Is.EqualTo("Armature/Hoodie"));
            var moved = hoodie.Overrides.Single();
            Assert.That(moved.PropertyPath, Is.EqualTo("m_LocalPosition.y"));
            Assert.That(moved.Before, Is.EqualTo("1.5"));
            Assert.That(moved.After, Is.EqualTo("2"));
        }

        [Test]
        public void AddedObjectsListTheirComponentsWithoutProperties()
        {
            string before = Header + "--- !u!1 &200\nGameObject:\n  m_Name: Armature\n--- !u!4 &201\nTransform:\n  m_GameObject: {fileID: 200}\n  m_Father: {fileID: 0}\n";
            string after = before + "--- !u!1 &400\nGameObject:\n  m_Name: Ear\n--- !u!4 &401\nTransform:\n  m_GameObject: {fileID: 400}\n  m_Father: {fileID: 201}\n  m_LocalPosition: {x: 1, y: 0, z: 0}\n";
            var set = UnitySemanticDiff.Compare(before, after);
            var ear = set.Objects.Single();
            Assert.That(ear.Kind, Is.EqualTo(SemanticChangeKind.Added));
            Assert.That(ear.Path, Is.EqualTo("Armature/Ear"));
            Assert.That(ear.Components.Single().Properties, Is.Empty);
        }

        [Test]
        public void LabelsAndValuesReadLikeTheInspector()
        {
            Assert.That(UnitySemanticDiff.Label("m_LocalPosition.x"), Is.EqualTo("Local Position › X"));
            Assert.That(UnitySemanticDiff.Label("m_Materials.Array.data[0]"), Is.EqualTo("Materials [0]"));
            Assert.That(UnitySemanticDiff.Label("rootTransform"), Is.EqualTo("Root Transform"));
            Assert.That(UnitySemanticDiff.TryEuler("{x: 0, y: 0.70710677, z: 0, w: 0.70710677}", out string euler), Is.True);
            Assert.That(euler, Is.EqualTo("(0°, 90°, 0°)"));
            Assert.That(UnitySemanticNames.Friendly("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "VRCPhysBone").label, Is.EqualTo("PhysBone"));
            Assert.That(UnitySemanticNames.Friendly("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature", "ModularAvatarMergeArmature").label, Is.EqualTo("MA Merge Armature"));
            Assert.That(UnitySemanticNames.Friendly("VF.Model.VRCFury", "VRCFury").family, Is.EqualTo(ComponentFamily.VRCFury));
            var names = new UnitySemanticNames();
            var file = UnityYaml.Parse(Avatar());
            Assert.That(names.Value("{x: 0, y: 1.25, z: 0}", "m_LocalPosition", file), Is.EqualTo("(0, 1.25, 0)"));
            Assert.That(names.Value("{fileID: 101}", "rootTransform", file), Is.EqualTo("Hips (Transform)"));
            Assert.That(names.Value("{fileID: 0}", "rootTransform", file), Is.EqualTo("None"));
        }
    }
}
