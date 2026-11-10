using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.UnitGit.Editor.Semantic;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The scene compare fingerprints every object first and parses only the ones that differ.
    public sealed class UnityYamlLazyTests
    {
        private const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        private static string Scene(int objects, int changed = -1, string newline = "\n")
        {
            var text = new StringBuilder(Header);
            for (int i = 0; i < objects; i++)
            {
                long go = 1000 + i * 2, transform = go + 1;
                text.Append("--- !u!1 &").Append(go).Append("\nGameObject:\n  m_ObjectHideFlags: 0\n  m_Component:\n  - component: {fileID: ")
                    .Append(transform).Append("}\n  m_Name: Object ").Append(i).Append("\n  m_IsActive: 1\n");
                text.Append("--- !u!4 &").Append(transform).Append("\nTransform:\n  m_GameObject: {fileID: ").Append(go)
                    .Append("}\n  m_LocalPosition: {x: ").Append(i == changed ? "9" : "0").Append(", y: 0, z: 0}\n  m_Father: {fileID: 0}\n");
            }
            text.Append("--- !u!1 &9\nGameObject:\n");
            return text.ToString().Replace("\n", newline);
        }

        [Test]
        public void HeadersTypesAndLinesComeFromOneScanAndFieldsWhenRead()
        {
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                var file = UnityYaml.Parse(Scene(3, newline: newline));
                Assert.That(file.Documents.Count, Is.EqualTo(7));
                Assert.That(file.Documents.All(document => !document.IsParsed), Is.True, "Nothing is parsed before it is read.");
                UnityYamlDocument transform = file.ById[1003];
                Assert.That(transform.ClassId + " " + transform.TypeName + " " + transform.Line, Is.EqualTo("4 Transform 22"));
                Assert.That(transform.TopLevelValue("m_GameObject"), Is.EqualTo("{fileID: 1002}"));
                Assert.That(transform.TopLevelValue("m_Missing"), Is.Null);
                Assert.That(transform.Body.Text("m_LocalPosition"), Is.Null, "A flow map is no scalar.");
                Assert.That(((YamlMap)transform.Body["m_LocalPosition"]).Text("x"), Is.EqualTo("0"));
                Assert.That(file.ById[1000].Body.Text("m_Name"), Is.EqualTo("Object 0"));
                Assert.That(file.ById[9].TypeName, Is.EqualTo("GameObject"));
                Assert.That(file.ById[9].Body.Entries, Is.Empty);
            }
        }

        [Test]
        public void OnlyTheObjectsThatDifferAreParsed()
        {
            var changes = UnitySemanticDiff.Compare(Scene(200), Scene(200, changed: 150));
            var changed = changes.Objects.Single();
            Assert.That(changed.Name, Is.EqualTo("Object 150"));
            Assert.That(changed.Components.Single().Properties.Single().Path, Is.EqualTo("m_LocalPosition"));
            int parsed = changes.After.Documents.Count(document => document.IsParsed);
            Assert.That(parsed, Is.LessThanOrEqualTo(3), "The changed Transform, its GameObject's name and nothing else.");
        }

        [Test]
        public void LineBreaksAloneAreNoChange()
        {
            Assert.That(UnitySemanticDiff.Compare(Scene(20), Scene(20, newline: "\r\n")).Objects, Is.Empty);
            Assert.That(UnitySemanticDiff.Compare(Scene(20), Scene(20, changed: 3, newline: "\r\n")).Objects.Single().Name, Is.EqualTo("Object 3"));
        }

        [Test]
        public void InlineValuesParseWithoutAWholeDocument()
        {
            var map = (YamlMap)UnityYaml.ParseInline("{fileID: 100100000, guid: 1fd59d31e538ede4f9f29daf80fd0687, type: 3}");
            Assert.That(map.Text("fileID") + " " + map.Text("guid") + " " + map.Text("type"), Is.EqualTo("100100000 1fd59d31e538ede4f9f29daf80fd0687 3"));
            Assert.That(((YamlScalar)UnityYaml.ParseInline("plain")).Value, Is.EqualTo("plain"));
            Assert.That(((YamlSequence)UnityYaml.ParseInline("[1, 2]")).Items.Count, Is.EqualTo(2));
        }
    }
}
