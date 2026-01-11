using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmDependencyCollector
    {
        internal static HashSet<string> CollectAssets(
            IEnumerable<string> seedAssetPaths,
            bool includeDependencies
        )
        {
            var seeds = NormalizeAssetPaths(seedAssetPaths)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var expanded = ExpandFolders(seeds).ToList();

            var result = new HashSet<string>(expanded, StringComparer.Ordinal);

            if (includeDependencies && expanded.Count > 0)
            {
                var deps = AssetDatabase.GetDependencies(expanded.ToArray(), recursive: true);
                foreach (var d in deps)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    result.Add(NormalizeAssetPath(d));
                }
            }

            // Filter out non-assets and meta files.
            result.RemoveWhere(p =>
                string.IsNullOrWhiteSpace(p) ||
                p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                IsBuiltinResourcePath(p) ||
                !AssetPathExistsOnDisk(p)
            );

            return result;
        }

        internal static IEnumerable<string> ExpandFolders(IEnumerable<string> assetPaths)
        {
            foreach (var p in NormalizeAssetPaths(assetPaths))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;

                if (AssetDatabase.IsValidFolder(p))
                {
                    var guids = AssetDatabase.FindAssets("", new[] { p });
                    foreach (var guid in guids)
                    {
                        var ap = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrWhiteSpace(ap)) continue;
                        ap = NormalizeAssetPath(ap);
                        if (AssetDatabase.IsValidFolder(ap)) continue;
                        yield return ap;
                    }
                }
                else
                {
                    yield return p;
                }
            }
        }

        internal static string NormalizeAssetPath(string assetPath)
        {
            return (assetPath ?? "").Replace('\\', '/');
        }

        internal static IEnumerable<string> NormalizeAssetPaths(IEnumerable<string> assetPaths)
        {
            if (assetPaths == null) yield break;
            foreach (var p in assetPaths)
                yield return NormalizeAssetPath(p);
        }

        private static bool IsBuiltinResourcePath(string assetPath)
        {
            // Unity may return these pseudo-paths from GetDependencies.
            if (assetPath.StartsWith("Resources/unity_builtin_extra", StringComparison.OrdinalIgnoreCase)) return true;
            if (assetPath.StartsWith("Library/unity default resources", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool AssetPathExistsOnDisk(string assetPath)
        {
            try
            {
                var fullPath = Path.GetFullPath(assetPath);
                return File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }
    }
}

