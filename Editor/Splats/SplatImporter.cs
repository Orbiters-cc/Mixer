using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Imports <c>.splats</c> files (a Gaussian splat PLY under Mixer's own extension, so other PLY importers never
    /// see it) as a <see cref="Orbiters.Mixer.Splats.SplatCloud"/>, with the sky map trained with them when a
    /// <c>.splatsky</c> lies beside (<see cref="SkyMaps"/>).
    /// </summary>
    [ScriptedImporter(3, "splats")]
    public sealed class SplatImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var cloud = PlyReader.ReadFile(ctx.assetPath);
            cloud.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            // The sky drawn behind the splats; its band around the horizon is what fog and haze turn to (instead of the
            // sky shell's splats, which give way to the map where it is trained).
            string sky = SkyMaps.Beside(ctx.assetPath);
            if (File.Exists(sky))
            {
                ctx.DependsOnSourceAsset(sky);
                Texture2D map = SkyMaps.Load(File.ReadAllBytes(sky), out string problem);
                if (map == null)
                {
                    ctx.LogImportWarning("Mixer: the sky map beside " + ctx.assetPath + " was left out: " + problem + ".");
                }
                else
                {
                    map.name = cloud.name + " sky";
                    ctx.AddObjectToAsset("sky", map);
                    cloud.skyMap = map;
                    cloud.horizon = SkyMaps.Horizon(map);
                    cloud.hasHorizon = true;
                }
            }
            ctx.AddObjectToAsset("splats", cloud);
            ctx.SetMainObject(cloud);
        }
    }
}
