using System;
using System.IO;
using UnityEditor;
using UnityEditorInternal;

namespace Orbiters.Logger.Editor
{
    /// <summary>Opens a log's source file at its line (moved out of LoggerWindow, MXL-3).</summary>
    internal static class SourceFiles
    {
        internal static bool Open(string path, int line, int column = 0)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset != null && AssetDatabase.OpenAsset(asset, Math.Max(1, line), Math.Max(0, column)))
                    {
                        return true;
                    }
                }

                string full = Path.GetFullPath(path);
                return File.Exists(full) && InternalEditorUtility.OpenFileAtLineExternal(full, Math.Max(1, line), Math.Max(0, column));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
