using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmPackageJsonAssetMenu
    {
        [MenuItem("Assets/VPM Packager/Export VPM Zip...", false, 2000)]
        private static void ExportVpmZip()
        {
            var packageJson = GetSelectedPackageJsonAssetPath();
            if (string.IsNullOrWhiteSpace(packageJson))
            {
                EditorUtility.DisplayDialog("Export VPM Zip", "Select a package.json first.", "OK");
                return;
            }

            var seeds = GetSeedAssetPathsForExport(packageJson);
            VpmZipExportWindow.Open(packageJson, seeds);
        }

        [MenuItem("Assets/VPM Packager/Export VPM Zip...", true)]
        private static bool ExportVpmZipValidate()
        {
            return !string.IsNullOrWhiteSpace(GetSelectedPackageJsonAssetPath());
        }

        private static string GetSelectedPackageJsonAssetPath()
        {
            foreach (var guid in Selection.assetGUIDs ?? Array.Empty<string>())
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsPackageJson(path))
                    return VpmDependencyCollector.NormalizeAssetPath(path);
            }

            return null;
        }

        private static List<string> GetSeedAssetPathsForExport(string packageJsonAssetPath)
        {
            packageJsonAssetPath = VpmDependencyCollector.NormalizeAssetPath(packageJsonAssetPath);

            var selected = new List<string>();
            foreach (var guid in Selection.assetGUIDs ?? Array.Empty<string>())
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                p = VpmDependencyCollector.NormalizeAssetPath(p);
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (string.Equals(p, packageJsonAssetPath, StringComparison.Ordinal)) continue;
                selected.Add(p);
            }

            selected = selected.Distinct(StringComparer.Ordinal).ToList();
            if (selected.Count > 0) return selected;

            var root = GetParentFolderAssetPath(packageJsonAssetPath);
            if (string.IsNullOrWhiteSpace(root)) return new List<string>();
            return new List<string> { root };
        }

        private static bool IsPackageJson(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return false;
            return string.Equals(Path.GetFileName(assetPath), "package.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetParentFolderAssetPath(string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath).TrimEnd('/');
            var i = assetPath.LastIndexOf('/');
            if (i <= 0) return "";
            return assetPath.Substring(0, i);
        }
    }
}

