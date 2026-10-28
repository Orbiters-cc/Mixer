using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// A layout made in another project (the one a new project opens with) names Mixer's windows by that project's script
    /// GUIDs: they are put right, by the dock area's name or the window's fields, before Unity drops them.
    /// </summary>
    public sealed class MixerLayoutRepairTests
    {
        private const string HierarchyGuid = "41b2a60f35f33504ba041f86e49ef360";
        private const string ConsoleGuid = "749aff27394998d4cbc9d18b89143ee1";
        private const string GitGuid = "4375beea054e69241b368a244f9cdfd0";
        private const string OtherToolGuid = "9f0c6e1d3e4d5e6f7a8b9c0d1e2f3a4d";

        // Trimmed from a layout Unity wrote (Preferences/Layouts/current/default-2022.dwlt): the Mixer Hierarchy alone in
        // its dock area, the Mixer console shown in another beside Mixer Git, another tool's window and Unity's Scene view.
        private static readonly string Layout = string.Join("\n", new[]
        {
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!114 &8",
            "MonoBehaviour:",
            "  m_ObjectHideFlags: 52",
            "  m_Script: {fileID: 12006, guid: 0000000000000000e000000000000000, type: 0}",
            "  m_Name: MixerHierarchyWindow",
            "  m_EditorClassIdentifier: ",
            "  m_Children: []",
            "  m_ActualView: {fileID: 17}",
            "  m_Panes:",
            "  - {fileID: 17}",
            "  m_Selected: 0",
            "--- !u!114 &13",
            "MonoBehaviour:",
            "  m_Script: {fileID: 12006, guid: 0000000000000000e000000000000000, type: 0}",
            "  m_Name: LoggerWindow",
            "  m_ActualView: {fileID: 19}",
            "  m_Panes:",
            "  - {fileID: 19}",
            "  - {fileID: 20}",
            "  - {fileID: 21}",
            "  m_Selected: 0",
            "--- !u!114 &17",
            "MonoBehaviour:",
            "  m_ObjectHideFlags: 52",
            "  m_Script: {fileID: 11500000, guid: " + HierarchyGuid + ", type: 3}",
            "  m_Name: ",
            "  m_EditorClassIdentifier: ",
            "  m_MinSize: {x: 240, y: 160}",
            "  m_TitleContent:",
            "    m_Text: Hierarchy",
            "  m_OverlayCanvas:",
            "    m_OverlaysVisible: 1",
            "  search: ",
            "  filterMeshes: 0",
            "  filterSkinned: 0",
            "  filterLights: 0",
            "  filterCameras: 0",
            "  filterPrefabs: 0",
            "  filterMissing: 0",
            "  filterInactive: 0",
            "  persistedExpanded: []",
            "--- !u!114 &19",
            "MonoBehaviour:",
            "  m_Script: {fileID: 11500000, guid: " + ConsoleGuid + ", type: 3}",
            "  m_Name: ",
            "  m_TitleContent:",
            "    m_Text: Console",
            "  levelMask: 7",
            "  search: ",
            "  useRegex: 0",
            "  grouped: 1",
            "  hiddenSources: []",
            "  detailsPaneHeight: 320",
            "--- !u!114 &20",
            "MonoBehaviour:",
            "  m_Script: {fileID: 11500000, guid: " + OtherToolGuid + ", type: 3}",
            "  m_Name: ",
            "  m_MinSize: {x: 500, y: 340}",
            "--- !u!114 &21",
            "MonoBehaviour:",
            "  m_Script: {fileID: 11500000, guid: " + GitGuid + ", type: 3}",
            "  m_Name: ",
            "  m_TitleContent:",
            "    m_Text: Git",
            "  commitMessage: ",
            "  commitAmend: 0",
            "--- !u!114 &26",
            "MonoBehaviour:",
            "  m_Script: {fileID: 12013, guid: 0000000000000000e000000000000000, type: 0}",
            "  m_Name: ",
            "",
        });

        private static Type TypeNamed(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        // This project's scripts for the windows, under other GUIDs than the layout's (as in another project).
        private static List<ForeignLayoutRepair.WindowScript> Scripts()
        {
            var scripts = new List<ForeignLayoutRepair.WindowScript>();
            foreach (var (name, guid) in new[]
                     {
                         ("Orbiters.Mixer.Editor.MixerHierarchyWindow", "11111111111111111111111111111111"),
                         ("Orbiters.Mixer.Editor.MixerProjectWindow", "22222222222222222222222222222222"),
                         ("Orbiters.Logger.Editor.LoggerWindow", "33333333333333333333333333333333"),
                         ("Orbiters.UnitGit.Editor.UnitGitWindow", "44444444444444444444444444444444"),
                     })
            {
                var type = TypeNamed(name);
                Assert.That(type, Is.Not.Null, name);
                scripts.Add(new ForeignLayoutRepair.WindowScript { TypeName = type.Name, Guid = guid, Fields = ForeignLayoutRepair.FieldsOf(type) });
            }
            return scripts;
        }

        [Test]
        public void MixersWindowsFromAnotherProjectGetThisProjectsScripts()
        {
            string repaired = ForeignLayoutRepair.Remap(Layout, guid => false, Scripts, out var remapped);
            Assert.That(remapped, Is.EquivalentTo(new[] { "MixerHierarchyWindow", "LoggerWindow", "UnitGitWindow" }));
            StringAssert.Contains("  m_Script: {fileID: 11500000, guid: 11111111111111111111111111111111, type: 3}", repaired);
            StringAssert.Contains("  m_Script: {fileID: 11500000, guid: 33333333333333333333333333333333, type: 3}", repaired);
            StringAssert.Contains("  m_Script: {fileID: 11500000, guid: 44444444444444444444444444444444, type: 3}", repaired, "a tab behind another, by its fields");
            StringAssert.Contains("guid: " + OtherToolGuid, repaired, "another tool's window without fields of a Mixer one stays as it is");
            StringAssert.DoesNotContain(HierarchyGuid, repaired);
            Assert.That(repaired.Length, Is.EqualTo(Layout.Length), "only the GUIDs change");
            Assert.That(repaired.Replace("11111111111111111111111111111111", HierarchyGuid).Replace("33333333333333333333333333333333", ConsoleGuid)
                .Replace("44444444444444444444444444444444", GitGuid), Is.EqualTo(Layout));
        }

        [Test]
        public void ALayoutWhoseScriptsAreKnownIsLeftAsItIs()
        {
            bool askedForTypes = false;
            string repaired = ForeignLayoutRepair.Remap(Layout, guid => true, () => { askedForTypes = true; return Scripts(); }, out var remapped);
            Assert.That(remapped, Is.Empty);
            Assert.That(repaired, Is.SameAs(Layout));
            Assert.That(askedForTypes, Is.False, "the window types are only looked up when a script is unknown");
        }

        [Test]
        public void AWindowTwoTypesCouldBeIsLeftAlone()
        {
            // Only "search": the Mixer Hierarchy and the Mixer console both have it, and no dock area names it.
            string layout = "--- !u!114 &5\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: " + HierarchyGuid + ", type: 3}\n  search: \n";
            string repaired = ForeignLayoutRepair.Remap(layout, guid => false, Scripts, out var remapped);
            Assert.That(remapped, Is.Empty);
            Assert.That(repaired, Is.EqualTo(layout));
        }

        [Test]
        public void TheLayoutUnityWroteHereHoldsTheMixerWindowsByTheirScripts()
        {
            // The fields of this project's window types hold every field a layout writes for them (a renamed field would
            // stop the match).
            foreach (var name in new[] { "Orbiters.Mixer.Editor.MixerHierarchyWindow", "Orbiters.Logger.Editor.LoggerWindow" })
            {
                var fields = ForeignLayoutRepair.FieldsOf(TypeNamed(name));
                Assert.That(fields, Has.Member("search"), name);
            }
            Assert.That(ForeignLayoutRepair.FieldsOf(TypeNamed("Orbiters.Mixer.Editor.MixerHierarchyWindow")), Is.SupersetOf(new[] { "filterMeshes", "filterInactive", "persistedExpanded" }));
            Assert.That(ForeignLayoutRepair.FieldsOf(TypeNamed("Orbiters.Logger.Editor.LoggerWindow")), Is.SupersetOf(new[] { "levelMask", "useRegex", "grouped", "hiddenSources", "detailsPaneHeight" }));
            Assert.That(typeof(EditorWindow).IsAssignableFrom(TypeNamed("Orbiters.UnitGit.Editor.UnitGitWindow")), Is.True);
        }
    }
}
