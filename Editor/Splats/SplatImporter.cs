using System.IO;
using UnityEditor.AssetImporters;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Imports <c>.splats</c> files (a Gaussian splat PLY under Mixer's own extension, so other PLY importers never
    /// see it) as a <see cref="Orbiters.Mixer.Splats.SplatCloud"/>.
    /// </summary>
    [ScriptedImporter(3, "splats")]
    public sealed class SplatImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var cloud = PlyReader.ReadFile(ctx.assetPath);
            cloud.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("splats", cloud);
            ctx.SetMainObject(cloud);
        }
    }
}
