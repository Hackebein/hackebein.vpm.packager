using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmmIndexCache
    {
        internal sealed class CacheData
        {
            public DateTime lastUpdatedUtc;
            public Dictionary<string, Dictionary<string, Dictionary<string, object>>> packages =
                new Dictionary<string, Dictionary<string, Dictionary<string, object>>>(StringComparer.Ordinal);
        }

        private const string CachePath = "Library/VpmPackagerVpmmIndexCache.json";

        internal static string CacheFullPath => Path.GetFullPath(CachePath);

        internal static DateTime CacheWriteTimeUtcOrMin()
        {
            try
            {
                var p = CacheFullPath;
                return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        internal static bool TryLoad(out CacheData cache)
        {
            cache = null;
            try
            {
                if (!File.Exists(Path.GetFullPath(CachePath)))
                    return false;

                if (!VpmJsonIO.TryReadJsonObjectAtPath(CachePath, out var root, out _))
                    return false;

                cache = FromRoot(root);
                return cache != null;
            }
            catch
            {
                return false;
            }
        }

        internal static CacheData LoadOrDefault()
        {
            return TryLoad(out var cache) ? cache : new CacheData();
        }

        internal static bool HasCache()
        {
            return File.Exists(Path.GetFullPath(CachePath));
        }

        internal static void Save(CacheData cache)
        {
            if (cache == null) cache = new CacheData();
            var root = ToRoot(cache);
            VpmJsonIO.TryWriteJsonObjectToPath(CachePath, root, out _);
        }

        internal static CacheData BuildFromIndexJsons(IEnumerable<Dictionary<string, object>> indices)
        {
            var cache = new CacheData
            {
                lastUpdatedUtc = DateTime.UtcNow
            };

            foreach (var index in indices ?? Array.Empty<Dictionary<string, object>>())
            {
                MergeIndexInto(cache, index);
            }

            return cache;
        }

        internal static bool TryGetPackageVersions(CacheData cache, string packageName, out List<string> versions)
        {
            versions = null;
            if (cache == null) return false;
            packageName = (packageName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(packageName)) return false;

            if (!cache.packages.TryGetValue(packageName, out var byVersion))
                return false;

            versions = byVersion.Keys.ToList();
            return versions.Count > 0;
        }

        internal static bool TryGetManifest(CacheData cache, string packageName, string version, out Dictionary<string, object> manifest)
        {
            manifest = null;
            if (cache == null) return false;
            packageName = (packageName ?? "").Trim();
            version = (version ?? "").Trim();
            if (packageName.Length == 0 || version.Length == 0) return false;

            if (!cache.packages.TryGetValue(packageName, out var byVersion))
                return false;

            if (!byVersion.TryGetValue(version, out var m))
                return false;

            manifest = m;
            return manifest != null;
        }

        private static void MergeIndexInto(CacheData cache, Dictionary<string, object> indexRoot)
        {
            if (cache == null || indexRoot == null) return;

            // VCC/VPM index format: { "packages": { "<name>": { "versions": { "<ver>": { ...manifest... }}}}}
            if (!TryGetDict(indexRoot, "packages", out var packagesObj))
                return;

            foreach (var pkgKv in packagesObj)
            {
                var pkgName = (pkgKv.Key ?? "").Trim();
                if (pkgName.Length == 0) continue;

                if (!(pkgKv.Value is Dictionary<string, object> pkgInfo))
                    continue;

                if (!TryGetDict(pkgInfo, "versions", out var versionsObj))
                    continue;

                foreach (var verKv in versionsObj)
                {
                    var ver = (verKv.Key ?? "").Trim();
                    if (ver.Length == 0) continue;

                    if (!(verKv.Value is Dictionary<string, object> manifest))
                        continue;

                    if (!cache.packages.TryGetValue(pkgName, out var byVersion))
                    {
                        byVersion = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                        cache.packages[pkgName] = byVersion;
                    }

                    byVersion[ver] = manifest;
                }
            }
        }

        private static CacheData FromRoot(Dictionary<string, object> root)
        {
            var cache = new CacheData();
            try
            {
                if (root.TryGetValue("lastUpdatedUtc", out var tObj) && tObj != null &&
                    DateTime.TryParse(tObj.ToString(), out var dt))
                {
                    cache.lastUpdatedUtc = dt.ToUniversalTime();
                }

                if (TryGetDict(root, "packages", out var pkgs))
                {
                    foreach (var pkg in pkgs)
                    {
                        var name = (pkg.Key ?? "").Trim();
                        if (name.Length == 0) continue;
                        if (!(pkg.Value is Dictionary<string, object> versDictObj)) continue;

                        var byVersion = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                        foreach (var v in versDictObj)
                        {
                            var ver = (v.Key ?? "").Trim();
                            if (ver.Length == 0) continue;
                            if (v.Value is Dictionary<string, object> manifest)
                                byVersion[ver] = manifest;
                        }

                        cache.packages[name] = byVersion;
                    }
                }

                return cache;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> ToRoot(CacheData cache)
        {
            var root = new Dictionary<string, object>();
            root["lastUpdatedUtc"] = cache.lastUpdatedUtc.ToString("o");

            var pkgs = new Dictionary<string, object>();
            foreach (var pkg in cache.packages)
            {
                var vers = new Dictionary<string, object>();
                foreach (var v in pkg.Value)
                {
                    vers[v.Key] = v.Value;
                }
                pkgs[pkg.Key] = vers;
            }
            root["packages"] = pkgs;
            return root;
        }

        private static bool TryGetDict(Dictionary<string, object> root, string key, out Dictionary<string, object> dict)
        {
            dict = null;
            if (root == null) return false;
            if (!root.TryGetValue(key, out var obj)) return false;
            dict = obj as Dictionary<string, object>;
            return dict != null;
        }
    }
}

