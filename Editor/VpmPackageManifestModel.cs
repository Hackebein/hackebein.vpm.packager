using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using hackebein.vpm.packager.editor.semver;

namespace hackebein.vpm.packager.editor
{
    internal sealed class VpmPackageManifestModel
    {
        internal sealed class AuthorModel
        {
            public string name = "";
            public string email = "";
            public string url = "";
        }

        public string name = "";
        public string displayName = "";
        public string version = "";
        public string description = "";
        public string unity = "";
        public string changelogUrl = "";
        public string license = "";

        public AuthorModel author = new AuthorModel();

        public Dictionary<string, string> vpmDependencies = new Dictionary<string, string>();
        public Dictionary<string, string> legacyFolders = new Dictionary<string, string>();
        public Dictionary<string, string> legacyFiles = new Dictionary<string, string>();
        public List<string> legacyPackages = new List<string>();

        /// <summary>
        /// Raw JSON root object from file load. We keep it so we can preserve unknown fields on write.
        /// </summary>
        public Dictionary<string, object> rawRoot = new Dictionary<string, object>();

        internal readonly struct ValidationIssue
        {
            public enum Severity
            {
                Info,
                Warning,
                Error,
            }

            public readonly Severity severity;
            public readonly string message;

            public ValidationIssue(Severity severity, string message)
            {
                this.severity = severity;
                this.message = message ?? "";
            }
        }

        private static readonly Regex SemVerRegex =
            new Regex(
                @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
                RegexOptions.Compiled
            );

        internal IEnumerable<ValidationIssue> ValidateForVpm()
        {
            if (string.IsNullOrWhiteSpace(name))
                yield return new ValidationIssue(ValidationIssue.Severity.Error, "`name` is required.");

            if (string.IsNullOrWhiteSpace(displayName))
                yield return new ValidationIssue(ValidationIssue.Severity.Error, "`displayName` is required.");

            if (string.IsNullOrWhiteSpace(version))
            {
                yield return new ValidationIssue(ValidationIssue.Severity.Error, "`version` is required.");
            }
            else if (!SemVerRegex.IsMatch(version.Trim()))
            {
                yield return new ValidationIssue(ValidationIssue.Severity.Warning, "`version` does not look like SemVer (e.g. 1.2.3).");
            }

            if (!string.IsNullOrWhiteSpace(author.email) && !author.email.Contains("@"))
            {
                yield return new ValidationIssue(ValidationIssue.Severity.Info, "`author.email` does not look like an email address.");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                // UPM-style constraints are stricter, but VPM packages in the wild can vary.
                // We provide a warning only.
                var trimmed = name.Trim();
                if (!trimmed.Contains("."))
                    yield return new ValidationIssue(ValidationIssue.Severity.Info, "`name` usually contains at least one '.' (e.g. com.example.package).");
            }

            foreach (var dep in vpmDependencies)
            {
                if (string.IsNullOrWhiteSpace(dep.Key))
                    yield return new ValidationIssue(ValidationIssue.Severity.Warning, "A `vpmDependencies` entry has an empty package name.");
                if (string.IsNullOrWhiteSpace(dep.Value))
                {
                    yield return new ValidationIssue(ValidationIssue.Severity.Warning, $"`vpmDependencies.{dep.Key}` has an empty version/range.");
                }
                else if (!SemverRange.IsValid(dep.Value))
                {
                    var shown = dep.Value.Trim();
                    yield return new ValidationIssue(ValidationIssue.Severity.Warning, $"`vpmDependencies.{dep.Key}` does not look like a valid SemVer range: \"{shown}\".");
                }
            }
        }

        internal static VpmPackageManifestModel FromRawRoot(Dictionary<string, object> root)
        {
            var model = new VpmPackageManifestModel();
            model.rawRoot = root ?? new Dictionary<string, object>();

            model.name = GetString(root, "name");
            model.displayName = GetString(root, "displayName");
            model.version = GetString(root, "version");
            model.description = GetString(root, "description");
            model.unity = GetString(root, "unity");
            model.changelogUrl = GetString(root, "changelogUrl");
            model.license = GetString(root, "license");

            if (TryGetDict(root, "author", out var authorDict))
            {
                model.author.name = GetString(authorDict, "name");
                model.author.email = GetString(authorDict, "email");
                model.author.url = GetString(authorDict, "url");
            }

            model.vpmDependencies = GetStringMap(root, "vpmDependencies");
            model.legacyFolders = GetStringMap(root, "legacyFolders");
            model.legacyFiles = GetStringMap(root, "legacyFiles");
            model.legacyPackages = GetStringList(root, "legacyPackages");

            return model;
        }

        internal Dictionary<string, object> ToRawRootPreservingUnknown()
        {
            var root = rawRoot != null
                ? new Dictionary<string, object>(rawRoot)
                : new Dictionary<string, object>();

            root["name"] = name ?? "";
            root["displayName"] = displayName ?? "";
            root["version"] = version ?? "";

            SetOrRemove(root, "description", description);
            SetOrRemove(root, "unity", unity);
            SetOrRemove(root, "license", license);
            SetOrRemove(root, "changelogUrl", changelogUrl);

            // For VPM package.json, we intentionally do not write `url`.
            // (If present in raw JSON, it will be removed on Apply.)
            root.Remove("url");

            var authorDict = TryGetDict(root, "author", out var existingAuthor)
                ? new Dictionary<string, object>(existingAuthor)
                : new Dictionary<string, object>();
            SetOrRemove(authorDict, "name", author.name);
            SetOrRemove(authorDict, "email", author.email);
            SetOrRemove(authorDict, "url", author.url);

            if (authorDict.Count == 0)
                root.Remove("author");
            else
                root["author"] = authorDict;

            SetOrRemoveMap(root, "vpmDependencies", vpmDependencies);
            SetOrRemoveMap(root, "legacyFolders", legacyFolders);
            SetOrRemoveMap(root, "legacyFiles", legacyFiles);
            SetOrRemoveList(root, "legacyPackages", legacyPackages);

            return root;
        }

        private static void SetOrRemove(Dictionary<string, object> root, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                root.Remove(key);
            }
            else
            {
                root[key] = value.Trim();
            }
        }

        private static void SetOrRemoveMap(Dictionary<string, object> root, string key, Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0)
            {
                root.Remove(key);
                return;
            }

            // Ensure stable output ordering by sorting keys (best-effort; JSON objects are unordered anyway).
            var sorted = map
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key.Trim(), kv => (object)(kv.Value ?? "").Trim());

            root[key] = sorted;
        }

        private static void SetOrRemoveList(Dictionary<string, object> root, string key, List<string> list)
        {
            if (list == null || list.Count == 0)
            {
                root.Remove(key);
                return;
            }

            root[key] = list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => (object)x.Trim()).ToList();
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            if (root == null) return "";
            if (!root.TryGetValue(key, out var obj) || obj == null) return "";
            return obj.ToString() ?? "";
        }

        private static bool TryGetDict(Dictionary<string, object> root, string key, out Dictionary<string, object> dict)
        {
            dict = null;
            if (root == null) return false;
            if (!root.TryGetValue(key, out var obj)) return false;
            dict = obj as Dictionary<string, object>;
            return dict != null;
        }

        private static Dictionary<string, string> GetStringMap(Dictionary<string, object> root, string key)
        {
            if (!TryGetDict(root, key, out var dict)) return new Dictionary<string, string>();
            var result = new Dictionary<string, string>();
            foreach (var kv in dict)
            {
                if (kv.Value == null) continue;
                result[kv.Key] = kv.Value.ToString();
            }

            return result;
        }

        private static List<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (root == null) return new List<string>();
            if (!root.TryGetValue(key, out var obj) || obj == null) return new List<string>();
            if (obj is List<object> listObj)
                return listObj.Where(x => x != null).Select(x => x.ToString()).ToList();
            if (obj is List<string> listStr)
                return listStr.ToList();

            return new List<string>();
        }
    }
}

