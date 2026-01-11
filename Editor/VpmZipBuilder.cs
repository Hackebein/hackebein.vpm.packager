using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmZipBuilder
    {
        internal sealed class BuildResult
        {
            public string zipPath;
            public int fileCount;
            public List<string> skipped = new List<string>();
        }

        internal static BuildResult BuildZip(
            string packageJsonAssetPath,
            string packageRootAssetPath,
            IEnumerable<string> includedAssetPaths,
            string outputZipPath
        )
        {
            if (string.IsNullOrWhiteSpace(packageJsonAssetPath))
                throw new ArgumentException("packageJsonAssetPath is empty.");
            if (string.IsNullOrWhiteSpace(packageRootAssetPath))
                throw new ArgumentException("packageRootAssetPath is empty.");
            if (string.IsNullOrWhiteSpace(outputZipPath))
                throw new ArgumentException("outputZipPath is empty.");

            packageJsonAssetPath = VpmDependencyCollector.NormalizeAssetPath(packageJsonAssetPath);
            packageRootAssetPath = VpmDependencyCollector.NormalizeAssetPath(packageRootAssetPath).TrimEnd('/');

            var assetSet = new HashSet<string>(
                VpmDependencyCollector.NormalizeAssetPaths(includedAssetPaths ?? Array.Empty<string>()),
                StringComparer.Ordinal
            );

            assetSet.Add(packageJsonAssetPath);

            var result = new BuildResult { zipPath = outputZipPath };

            var outDir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);

            // Ensure the file isn't open.
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);

            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var fs = new FileStream(outputZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var paths = assetSet.ToList();
                paths.Sort(StringComparer.Ordinal);

                var total = paths.Count;
                for (var i = 0; i < total; i++)
                {
                    var ap = paths[i];
                    EditorUtility.DisplayProgressBar("VPM Zip Export", ap, total == 0 ? 1f : (float)i / total);

                    AddAssetAndMeta(zip, entryNames, packageRootAssetPath, ap, result);
                }
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();

            return result;
        }

        private static void AddAssetAndMeta(
            ZipArchive zip,
            HashSet<string> entryNames,
            string packageRootAssetPath,
            string assetPath,
            BuildResult result
        )
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);

            var fullPath = SafeFullPath(assetPath);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                result.skipped.Add(assetPath);
                return;
            }

            var entryName = ToZipEntryName(packageRootAssetPath, assetPath);
            AddFile(zip, entryNames, fullPath, entryName, result);

            var metaAssetPath = assetPath + ".meta";
            var metaFullPath = SafeFullPath(metaAssetPath);
            if (!string.IsNullOrWhiteSpace(metaFullPath) && File.Exists(metaFullPath))
            {
                var metaEntryName = ToZipEntryName(packageRootAssetPath, metaAssetPath);
                AddFile(zip, entryNames, metaFullPath, metaEntryName, result);
            }
        }

        private static void AddFile(
            ZipArchive zip,
            HashSet<string> entryNames,
            string fullPath,
            string entryName,
            BuildResult result
        )
        {
            entryName = (entryName ?? "").Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(entryName)) return;
            if (!entryNames.Add(entryName)) return;

            zip.CreateEntryFromFile(fullPath, entryName, System.IO.Compression.CompressionLevel.Optimal);
            result.fileCount++;
        }

        private static string ToZipEntryName(string packageRootAssetPath, string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);
            packageRootAssetPath = VpmDependencyCollector.NormalizeAssetPath(packageRootAssetPath).TrimEnd('/');

            if (IsUnderRoot(packageRootAssetPath, assetPath))
            {
                var rel = assetPath.Substring(packageRootAssetPath.Length).TrimStart('/');
                return rel;
            }

            return "_External/" + assetPath;
        }

        private static bool IsUnderRoot(string rootAssetPath, string assetPath)
        {
            rootAssetPath = (rootAssetPath ?? "").TrimEnd('/');
            assetPath = assetPath ?? "";

            if (assetPath.Equals(rootAssetPath, StringComparison.Ordinal)) return true;
            return assetPath.StartsWith(rootAssetPath + "/", StringComparison.Ordinal);
        }

        private static string SafeFullPath(string assetPath)
        {
            try
            {
                return Path.GetFullPath(assetPath);
            }
            catch
            {
                return null;
            }
        }
    }
}

