using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor.Processes;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// "Open scene in Blender": starts the user's Blender on the scene's project (<c>.blend</c> under
    /// <c>Assets/Orbiters/Mixer/Blender/&lt;scene&gt;/blend~</c>, a folder Unity never imports) with a small Python
    /// bootstrap that installs or updates the Mixer extension, makes or opens the file, remembers the Unity scene in
    /// it and saves; the extension then connects to the link and builds the scene. The interactive Blender is the
    /// user's, not a process Unity owns.
    /// </summary>
    internal static class BlenderProjectLauncher
    {
        public const string ExtensionId = "orbiters_mixer";
        public const string ExtensionVersion = "0.1.0";
        private const string ReleaseUrlTemplate = "https://github.com/Orbiters-cc/Mixer-Blender-Add-on/releases/download/v{0}/orbiters_mixer-{0}.zip";
        public const string ZipPref = "Orbiters.Mixer.BlenderExtensionZip";
        public const string SourcePref = "Orbiters.Mixer.BlenderExtensionSource";
        public const string UrlPref = "Orbiters.Mixer.BlenderExtensionUrl";
        private const string Repository = "user_default";

        public static string DownloadUrl => EditorPrefs.GetString(UrlPref, string.Format(ReleaseUrlTemplate, ExtensionVersion));

        /// <summary>Where the scene's .blend lives (absolute), made if needed.</summary>
        public static string BlendPathFor(Scene scene)
        {
            string name = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "Orbiters", "Mixer", "Blender", Orbiters.Toolkit.Editor.Meshes.MeshAssets.Clean(name), "blend~"));
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Orbiters.Toolkit.Editor.Meshes.MeshAssets.Clean(name) + ".blend");
        }

        /// <summary>Starts Blender on the active scene's project. Returns an error text, or null.</summary>
        public static string Open()
        {
            string blender = BlenderInstalls.GetSelectedExecutablePath();
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) return "Blender was not found. Pick its executable in the Orbiters settings.";
            MixerLinkService.Enabled = true;
            MixerLinkService.EnsureRunning();
            if (!MixerLinkService.Running) return "The Blender link could not listen; see the Console.";
            var scene = SceneManager.GetActiveScene();
            var settings = MixerLinkService.SettingsFor(scene, create: true);
            string blendPath = BlendPathFor(scene);
            settings.blendPath = blendPath;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            string launchFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "Orbiters", "Mixer", "launch"));
            Directory.CreateDirectory(launchFolder);
            string configPath = Path.Combine(launchFolder, "launch.json");
            string scriptPath = Path.Combine(launchFolder, "bootstrap.py");
            var config = new
            {
                kind = "orbiters.mixer.launch",
                blendPath,
                sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                sceneName = scene.name,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                endpointPath = MixerLinkProtocol.EndpointPath,
                extension = new
                {
                    id = ExtensionId,
                    version = ExtensionVersion,
                    repository = Repository,
                    downloadUrl = DownloadUrl,
                    zipPath = EditorPrefs.GetString(ZipPref, ""),
                    sourceDir = EditorPrefs.GetString(SourcePref, ""),
                },
            };
            File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            File.WriteAllText(scriptPath, "CONFIG_PATH = " + JsonConvert.ToString(configPath) + "\n" + Bootstrap);
            var startInfo = new ProcessStartInfo
            {
                FileName = blender,
                Arguments = "--python " + Quote(scriptPath),
                WorkingDirectory = Path.GetDirectoryName(blendPath),
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            try
            {
                var process = Process.Start(startInfo);
                AppWindows.BringToFrontOnceShown(process);
            }
            catch (Exception e)
            {
                return "Blender could not start: " + e.Message;
            }
            return null;
        }

        private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

        // Runs inside the interactive Blender before its window shows. Keep Python comments out of this literal (the
        // C# excluded-region parser reads line-leading '#').
        private const string Bootstrap = @"import addon_utils
import importlib
import json
import os
import subprocess
import sys
import tempfile
import traceback
import urllib.request

import bpy


def _log(message):
    print('[Mixer launch] ' + str(message))


def _installed_module(extension_id):
    for module in addon_utils.modules(refresh=False):
        if module.__name__.startswith('bl_ext.') and module.__name__.rsplit('.', 1)[-1] == extension_id:
            return module
    return None


def _version_of(module):
    return '.'.join(str(part) for part in addon_utils.module_bl_info(module).get('version', ()))


def _zip_for(extension):
    source = extension.get('sourceDir') or ''
    if source and os.path.isdir(source):
        out = os.path.join(tempfile.gettempdir(), 'orbiters_mixer_build')
        os.makedirs(out, exist_ok=True)
        _log('Building the extension from ' + source)
        completed = subprocess.run([bpy.app.binary_path, '--factory-startup', '--command', 'extension', 'build', '--source-dir', source, '--output-dir', out], capture_output=True, text=True)
        for line in (completed.stdout + completed.stderr).splitlines():
            _log('  ' + line)
        if completed.returncode != 0:
            raise RuntimeError('extension build failed')
        zips = sorted([os.path.join(out, f) for f in os.listdir(out) if f.endswith('.zip')], key=os.path.getmtime)
        if not zips:
            raise RuntimeError('no zip built')
        return zips[-1]
    zip_path = extension.get('zipPath') or ''
    if zip_path and os.path.isfile(zip_path):
        return zip_path
    url = extension.get('downloadUrl') or ''
    if not url:
        raise RuntimeError('No download URL is configured.')
    target = os.path.join(tempfile.gettempdir(), os.path.basename(url.split('?')[0]) or 'extension.zip')
    _log('Downloading ' + url)
    urllib.request.urlretrieve(url, target)
    return target


def _unload(module_name):
    addon_utils.disable(module_name)
    for key in list(sys.modules.keys()):
        if key == module_name or key.startswith(module_name + '.'):
            del sys.modules[key]


def _install(extension, repository):
    zip_path = _zip_for(extension)
    _log('Installing ' + zip_path + ' into the ' + repository + ' extension repository')
    completed = subprocess.run([bpy.app.binary_path, '--command', 'extension', 'install-file', '--repo', repository, '--enable', zip_path], capture_output=True, text=True)
    for line in (completed.stdout + completed.stderr).splitlines():
        _log('  ' + line)
    if completed.returncode != 0:
        raise RuntimeError('extension install-file failed with exit code ' + str(completed.returncode))
    importlib.invalidate_caches()
    addon_utils.modules(refresh=True)


def _ensure(extension, repository):
    extension_id = extension['id']
    expected = extension.get('version') or ''
    module = _installed_module(extension_id)
    force = bool(extension.get('sourceDir') or extension.get('zipPath'))
    if module is not None and _version_of(module) == expected and not force:
        module_name = module.__name__
    else:
        if module is not None:
            _log(extension_id + ' ' + _version_of(module) + ' is installed, ' + expected + ' is wanted')
            _unload(module.__name__)
        _install(extension, repository)
        module_name = 'bl_ext.' + repository + '.' + extension_id
    if module_name not in bpy.context.preferences.addons or not addon_utils.check(module_name)[1]:
        if addon_utils.enable(module_name, default_set=True) is None:
            raise RuntimeError('Could not enable ' + module_name)
        bpy.ops.wm.save_userpref()
    return module_name


def main():
    with open(CONFIG_PATH, 'r', encoding='utf-8') as handle:
        config = json.load(handle)
    extension = config['extension']
    _ensure(extension, extension.get('repository') or 'user_default')
    blend_path = config['blendPath']
    if os.path.exists(blend_path):
        bpy.ops.wm.open_mainfile(filepath=blend_path)
    else:
        bpy.ops.wm.read_homefile(use_empty=True)
    settings = bpy.context.scene.orbiters_mixer
    settings.scene_guid = config.get('sceneGuid') or ''
    settings.scene_name = config.get('sceneName') or ''
    settings.project_path = config.get('projectPath') or ''
    settings.endpoint_path = config.get('endpointPath') or ''
    settings.auto_connect = True
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)
    _log('Ready: ' + blend_path)


try:
    main()
except Exception:
    traceback.print_exc()
";
    }
}
