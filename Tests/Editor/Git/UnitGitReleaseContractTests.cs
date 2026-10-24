using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitReleaseContractTests
    {
        [Test]
        public void ReleaseApiV2ExposesExpectedCapabilities()
        {
            Assert.That(UnitGitReleases.ApiVersion, Is.EqualTo(2));
            Assert.That(UnitGitReleases.GetCapabilities(), Is.EquivalentTo(new[]
            {
                UnitGitReleases.CommitFilesCapability,
                UnitGitReleases.ScopedReleaseCheckpointCapability,
                UnitGitReleases.FullProjectReleaseCheckpointCapability
            }));
        }

        [Test]
        public void ReleaseApiV2KeepsPublicMethodSignatures()
        {
            AssertPublicStaticMethod(
                nameof(UnitGitReleases.CommitFiles),
                typeof(UnitGitReleaseResult),
                typeof(string),
                typeof(string),
                typeof(string[]));
            AssertPublicStaticMethod(
                nameof(UnitGitReleases.PublishRelease),
                typeof(UnitGitReleaseResult),
                typeof(UnitGitReleaseEntry),
                typeof(string),
                typeof(string[]));
            AssertPublicStaticMethod(
                nameof(UnitGitReleases.PublishReleaseAll),
                typeof(UnitGitReleaseResult),
                typeof(UnitGitReleaseEntry),
                typeof(string));
        }

        [Test]
        public void ReleaseApiV2KeepsDtoMembersUsedByExternalPublishers()
        {
            AssertWritableField<UnitGitReleaseField>("key", typeof(string));
            AssertWritableField<UnitGitReleaseField>("value", typeof(string));
            Assert.That(typeof(UnitGitReleaseField).GetConstructor(new[] { typeof(string), typeof(string) }), Is.Not.Null);

            foreach (string fieldName in new[]
                     {
                         "tool",
                         "type",
                         "name",
                         "version",
                         "title",
                         "changelog",
                         "scope",
                         "date",
                         "author"
                     })
            {
                AssertWritableField<UnitGitReleaseEntry>(fieldName, typeof(string));
            }

            FieldInfo fields = AssertWritableField<UnitGitReleaseEntry>("fields", typeof(System.Collections.Generic.List<UnitGitReleaseField>));
            Assert.That(fields.GetValue(new UnitGitReleaseEntry()), Is.Not.Null);

            AssertWritableField<UnitGitReleaseResult>("Success", typeof(bool));
            AssertWritableField<UnitGitReleaseResult>("Message", typeof(string));
            AssertWritableField<UnitGitReleaseResult>("CommitHash", typeof(string));
            AssertWritableField<UnitGitReleaseResult>("ReleaseId", typeof(string));
        }

        private static void AssertPublicStaticMethod(string methodName, Type returnType, params Type[] parameterTypes)
        {
            MethodInfo method = typeof(UnitGitReleases).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                parameterTypes,
                null);

            Assert.That(method, Is.Not.Null, methodName + "(" + string.Join(", ", parameterTypes.Select(type => type.Name).ToArray()) + ")");
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
        }

        private static FieldInfo AssertWritableField<T>(string fieldName, Type fieldType)
        {
            FieldInfo field = typeof(T).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, typeof(T).Name + "." + fieldName);
            Assert.That(field.FieldType, Is.EqualTo(fieldType));
            Assert.That(field.IsInitOnly, Is.False);
            return field;
        }
    }
}
