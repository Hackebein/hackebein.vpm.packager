using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace hackebein.vpm.packager.editor
{
    [CustomEditor(typeof(TextAsset))]
    internal sealed class VpmPackageJsonEditor : Editor
    {
        [Serializable]
        private sealed class StringPair
        {
            public string key = "";
            public string value = "";
        }

        private string _assetPath;
        private bool _isPackageJson;
        private string _loadError;

        private VpmPackageManifestModel _model;

        private List<StringPair> _vpmDeps = new List<StringPair>();
        private List<StringPair> _legacyFolders = new List<StringPair>();
        private List<StringPair> _legacyFiles = new List<StringPair>();
        private List<string> _legacyPackages = new List<string>();

        private Vector2 _scroll;
        private bool _showRawJson;
        private string _rawJson;
        private bool _dirty;

        // Cached VPMM/index state to keep the inspector responsive.
        private VpmPackagerProjectSettings.Data _vpmmSettings;
        private bool _autoDownloadTriggered;
        private VpmmIndexCache.CacheData _indexCache;
        private bool _indexCacheAvailable;
        private DateTime _indexCacheLastUpdatedUtc;
        private DateTime _indexCacheFileWriteTimeUtc;
        private double _nextCachePollTime;

        private bool _depWarningsDirty = true;
        private List<string> _depWarningsCache = new List<string>();

        private bool _versionInfoDirty = true;
        private bool _versionExistsCache;

        private void OnEnable()
        {
            Reload();
            _vpmmSettings = VpmPackagerProjectSettings.Load();
            ForceRefreshIndexCache();

            if (!_indexCacheAvailable && !_autoDownloadTriggered)
            {
                _autoDownloadTriggered = true;
                VpmmIndexDownloader.EnsureCacheAsync(_vpmmSettings);
            }
        }

        public override void OnInspectorGUI()
        {
            var prevGuiEnabled = GUI.enabled;
            // Unity can disable GUI for some text assets/package manifests; we still want editable controls.
            GUI.enabled = true;
            try
            {
                PollIndexCacheThrottled();

                _assetPath = AssetDatabase.GetAssetPath(target);
                _isPackageJson = string.Equals(Path.GetFileName(_assetPath), "package.json", StringComparison.OrdinalIgnoreCase);

                if (!_isPackageJson)
                {
                    DrawDefaultInspector();
                    return;
                }

                var canWrite = CanWriteToAssetPath(_assetPath, out var cannotWriteReason);
                if (!canWrite && !string.IsNullOrWhiteSpace(cannotWriteReason))
                    EditorGUILayout.HelpBox(cannotWriteReason, MessageType.Info);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("VPM package.json", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Reload", GUILayout.Width(80)))
                        Reload();
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Path", _assetPath);
                }

                // VPMM section near the top (base URL is stored in ProjectSettings and can be edited there if needed).
                DrawVpmmSettings();
                EditorGUILayout.Space(8);

                if (!string.IsNullOrEmpty(_loadError))
                {
                    EditorGUILayout.HelpBox(_loadError, MessageType.Error);
                    if (GUILayout.Button("Open Raw JSON"))
                        _showRawJson = true;
                }

                if (_model == null)
                {
                    DrawRawJson();
                    return;
                }

                DrawValidation();

                EditorGUI.BeginChangeCheck();

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                DrawCoreFields();
                EditorGUILayout.Space(8);
                DrawAuthorFields();
                EditorGUILayout.Space(8);
                DrawStringMap("VPM Dependencies", _vpmDeps, help: "Package -> version/range. Example: com.vrchat.avatars -> 3.10.x");
                DrawVpmDependencyIndexWarnings();
                EditorGUILayout.Space(8);
                DrawStringMap("Legacy Folders", _legacyFolders, help: "Assets\\\\OldFolderPath -> optional GUID");
                EditorGUILayout.Space(8);
                DrawStringMap("Legacy Files", _legacyFiles, help: "Assets\\\\OldFilePath -> optional GUID");
                EditorGUILayout.Space(8);
                DrawStringList("Legacy Packages", _legacyPackages, help: "Package names to remove when installing this package.");
                EditorGUILayout.EndScrollView();

                if (EditorGUI.EndChangeCheck())
                {
                    _dirty = true;
                    _depWarningsDirty = true;
                    _versionInfoDirty = true;
                }

                EditorGUILayout.Space(8);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!_dirty || !canWrite))
                    {
                        if (GUILayout.Button("Apply", GUILayout.Height(28)))
                            Apply();
                    }

                    if (GUILayout.Button("Open File", GUILayout.Height(28)))
                        EditorUtility.RevealInFinder(Path.GetFullPath(_assetPath));

                    if (GUILayout.Button("Export…", GUILayout.Height(28)))
                        ExportZip();
                }

                EditorGUILayout.Space(6);
                DrawRawJson();
            }
            finally
            {
                GUI.enabled = prevGuiEnabled;
            }
        }

        private void DrawCoreFields()
        {
            EditorGUILayout.LabelField("Core", EditorStyles.boldLabel);

            _model.name = EditorGUILayout.TextField("name", _model.name);
            _model.displayName = EditorGUILayout.TextField("displayName", _model.displayName);
            DrawVersionFieldWithFillButton();
            _model.description = EditorGUILayout.TextField("description", _model.description);
            _model.unity = EditorGUILayout.TextField("unity", _model.unity);
            _model.license = EditorGUILayout.TextField("license", _model.license);

            EditorGUILayout.Space(4);
            _model.changelogUrl = EditorGUILayout.TextField("changelogUrl", _model.changelogUrl);
        }

        private void DrawVersionFieldWithFillButton()
        {
            var haveCache = _indexCacheAvailable;
            var cache = _indexCache;
            var packageName = (_model.name ?? "").Trim();
            var version = (_model.version ?? "").Trim();

            var hasPkg = haveCache && !string.IsNullOrWhiteSpace(packageName) && cache.packages.ContainsKey(packageName);
            var versionExists = hasPkg && !string.IsNullOrWhiteSpace(version) && cache.packages[packageName].ContainsKey(version);
            var canFill = hasPkg && (string.IsNullOrWhiteSpace(version) || versionExists);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _model.version = EditorGUILayout.TextField("version", _model.version);
                if (EditorGUI.EndChangeCheck())
                    _versionInfoDirty = true;

                using (new EditorGUI.DisabledScope(!canFill))
                {
                    var content = EditorGUIUtility.IconContent("CloudDownload");
                    if (content == null || content.image == null)
                        content = new GUIContent("↓", "Fill fields from released version");
                    else
                        content.tooltip = "Fill fields from released version";

                    if (GUILayout.Button(content, GUILayout.Width(28), GUILayout.Height(18)))
                    {
                        var targetVersion = version;
                        if (string.IsNullOrWhiteSpace(targetVersion))
                            targetVersion = PickLatestVersion(cache.packages[packageName].Keys);

                        if (!string.IsNullOrWhiteSpace(targetVersion) && cache.packages[packageName].TryGetValue(targetVersion, out var manifest))
                            ApplyManifestToEditor(manifest);
                    }
                }
            }

            if (_versionInfoDirty)
            {
                _versionExistsCache = versionExists;
                _versionInfoDirty = false;
            }

            if (_versionExistsCache)
            {
                EditorGUILayout.HelpBox("This version already exists in the index (already released).", MessageType.Warning);
            }
        }

        private static string PickLatestVersion(IEnumerable<string> versions)
        {
            var list = (versions ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            if (list.Count == 0) return null;
            list.Sort((a, b) => hackebein.vpm.packager.editor.semver.SemverComparable.Compare(b, a));
            return list[0];
        }

        private void ApplyManifestToEditor(Dictionary<string, object> manifest)
        {
            if (manifest == null) return;
            var loaded = VpmPackageManifestModel.FromRawRoot(manifest);

            _model.name = loaded.name;
            _model.displayName = loaded.displayName;
            _model.version = loaded.version;
            _model.description = loaded.description;
            _model.unity = loaded.unity;
            _model.license = loaded.license;
            _model.changelogUrl = loaded.changelogUrl;
            _model.author.name = loaded.author.name;
            _model.author.email = loaded.author.email;
            _model.author.url = loaded.author.url;

            _model.vpmDependencies = loaded.vpmDependencies;
            _model.legacyFolders = loaded.legacyFolders;
            _model.legacyFiles = loaded.legacyFiles;
            _model.legacyPackages = loaded.legacyPackages;

            _vpmDeps = ToList(_model.vpmDependencies);
            _legacyFolders = ToList(_model.legacyFolders);
            _legacyFiles = ToList(_model.legacyFiles);
            _legacyPackages = _model.legacyPackages != null ? _model.legacyPackages.ToList() : new List<string>();

            _dirty = true;
        }

        private void DrawAuthorFields()
        {
            EditorGUILayout.LabelField("Author", EditorStyles.boldLabel);

            _model.author.name = EditorGUILayout.TextField("author.name", _model.author.name);
            _model.author.email = EditorGUILayout.TextField("author.email", _model.author.email);
            _model.author.url = EditorGUILayout.TextField("author.url", _model.author.url);
        }

        private void DrawVpmDependencyIndexWarnings()
        {
            if (!_indexCacheAvailable) return;
            if (_indexCache.packages == null || _indexCache.packages.Count == 0) return;

            if (_depWarningsDirty)
            {
                _depWarningsCache = new List<string>();

                foreach (var row in _vpmDeps)
                {
                    if (row == null) continue;
                    var depName = (row.key ?? "").Trim();
                    var depRange = (row.value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(depName)) continue;

                    if (!_indexCache.packages.TryGetValue(depName, out var versionsDict) || versionsDict == null || versionsDict.Count == 0)
                    {
                        _depWarningsCache.Add($"Dependency package not found in index: {depName}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(depRange) || depRange == "*" || depRange.Equals("x", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var versions = versionsDict.Keys;
                    if (!hackebein.vpm.packager.editor.semver.SemverRange.AnySatisfies(depRange, versions))
                        _depWarningsCache.Add($"No versions match range for {depName}: \"{depRange}\"");
                }

                _depWarningsDirty = false;
            }

            foreach (var w in _depWarningsCache)
                EditorGUILayout.HelpBox(w, MessageType.Warning);
        }

        private void DrawValidation()
        {
            var issues = _model.ValidateForVpm().ToList();
            if (issues.Count == 0) return;

            foreach (var issue in issues)
            {
                var type = MessageType.None;
                switch (issue.severity)
                {
                    case VpmPackageManifestModel.ValidationIssue.Severity.Info:
                        type = MessageType.Info;
                        break;
                    case VpmPackageManifestModel.ValidationIssue.Severity.Warning:
                        type = MessageType.Warning;
                        break;
                    case VpmPackageManifestModel.ValidationIssue.Severity.Error:
                        type = MessageType.Error;
                        break;
                }

                EditorGUILayout.HelpBox(issue.message, type);
            }
        }

        private static void DrawStringMap(string title, List<StringPair> list, string help)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(help))
                EditorGUILayout.HelpBox(help, MessageType.None);

            if (list.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }

            var removeAt = -1;

            for (var i = 0; i < list.Count; i++)
            {
                var row = list[i] ?? (list[i] = new StringPair());

                using (new EditorGUILayout.HorizontalScope())
                {
                    row.key = EditorGUILayout.TextField(row.key, GUILayout.MinWidth(120));
                    row.value = EditorGUILayout.TextField(row.value, GUILayout.MinWidth(80));

                    if (GUILayout.Button("X", GUILayout.Width(22)))
                        removeAt = i;
                }
            }

            if (removeAt >= 0 && removeAt < list.Count)
                list.RemoveAt(removeAt);

            if (GUILayout.Button("Add", GUILayout.Width(80)))
                list.Add(new StringPair());
        }

        private static void DrawStringList(string title, List<string> list, string help)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(help))
                EditorGUILayout.HelpBox(help, MessageType.None);

            if (list.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }

            var removeAt = -1;
            for (var i = 0; i < list.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    list[i] = EditorGUILayout.TextField(list[i] ?? "");
                    if (GUILayout.Button("X", GUILayout.Width(22)))
                        removeAt = i;
                }
            }

            if (removeAt >= 0 && removeAt < list.Count)
                list.RemoveAt(removeAt);

            if (GUILayout.Button("Add", GUILayout.Width(80)))
                list.Add("");
        }

        private void DrawRawJson()
        {
            _showRawJson = EditorGUILayout.Foldout(_showRawJson, "Raw JSON", true);
            if (!_showRawJson) return;

            using (new EditorGUI.DisabledScope(true))
            {
                if (_rawJson == null)
                    _rawJson = SafeReadText(_assetPath);

                EditorGUILayout.TextArea(_rawJson, GUILayout.MinHeight(160));
            }
        }

        private void Reload()
        {
            _dirty = false;
            _loadError = null;
            _model = null;
            _rawJson = null;

            _assetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(_assetPath))
            {
                _loadError = "Could not resolve asset path.";
                return;
            }

            if (!VpmJsonIO.TryReadJsonObjectAtAssetPath(_assetPath, out var root, out var error))
            {
                _loadError = error;
                return;
            }

            _model = VpmPackageManifestModel.FromRawRoot(root);
            _vpmDeps = ToList(_model.vpmDependencies);
            _legacyFolders = ToList(_model.legacyFolders);
            _legacyFiles = ToList(_model.legacyFiles);
            _legacyPackages = _model.legacyPackages != null ? _model.legacyPackages.ToList() : new List<string>();
        }

        private void Apply()
        {
            _model.vpmDependencies = FromList(_vpmDeps);
            _model.legacyFolders = FromList(_legacyFolders);
            _model.legacyFiles = FromList(_legacyFiles);
            _model.legacyPackages = _legacyPackages != null ? _legacyPackages.ToList() : new List<string>();

            var root = _model.ToRawRootPreservingUnknown();
            if (!VpmJsonIO.TryWriteJsonObjectToAssetPath(_assetPath, root, out var error))
            {
                _loadError = error;
                return;
            }

            _dirty = false;
            _rawJson = null;
            Reload();
        }

        private void ExportZip()
        {
            if (_dirty)
            {
                var choice = EditorUtility.DisplayDialogComplex(
                    "Export VPM Zip",
                    "You have unapplied changes.\n\nApply changes before exporting?",
                    "Apply & Export",
                    "Cancel",
                    "Export Without Applying"
                );

                // 0 = ok, 1 = cancel, 2 = alt
                if (choice == 1) return;
                if (choice == 0) Apply();
            }

            var seeds = GetSeedAssetPathsForExport(_assetPath);
            VpmZipExportWindow.Open(_assetPath, seeds);
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

        private static string GetParentFolderAssetPath(string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath).TrimEnd('/');
            var i = assetPath.LastIndexOf('/');
            if (i <= 0) return "";
            return assetPath.Substring(0, i);
        }

        private static List<StringPair> ToList(Dictionary<string, string> dict)
        {
            if (dict == null) return new List<StringPair>();
            return dict
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new StringPair { key = kv.Key ?? "", value = kv.Value ?? "" })
                .ToList();
        }

        private static Dictionary<string, string> FromList(List<StringPair> list)
        {
            var dict = new Dictionary<string, string>();
            if (list == null) return dict;

            foreach (var row in list)
            {
                if (row == null) continue;
                var k = (row.key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(k)) continue;
                dict[k] = (row.value ?? "").Trim();
            }

            return dict;
        }

        private static string SafeReadText(string assetPath)
        {
            try
            {
                var fullPath = Path.GetFullPath(assetPath);
                return File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void DrawVpmmSettings()
        {
            EditorGUILayout.LabelField("VPMM", EditorStyles.boldLabel);

            var settings = VpmPackagerProjectSettings.Load();

            EditorGUI.BeginChangeCheck();

            var keys = (settings.vpmmKeyHistory ?? new List<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct()
                .ToList();

            var currentKey = (settings.vpmmApiKey ?? "").Trim();

            var options = new List<string> { "<Custom / Unsaved>" };
            options.AddRange(keys.Select(VpmPackagerProjectSettings.GetMaskedKeyLabel));

            var selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentKey))
            {
                var match = keys.FindIndex(k => string.Equals(k, currentKey, StringComparison.Ordinal));
                if (match >= 0) selectedIndex = match + 1;
            }

            // Saved keys dropdown with remove button ("-") after it.
            var newIndex = selectedIndex;
            using (new EditorGUILayout.HorizontalScope())
            {
                newIndex = EditorGUILayout.Popup("Saved keys", selectedIndex, options.ToArray());
                using (new EditorGUI.DisabledScope(newIndex == 0))
                {
                    if (GUILayout.Button("-", GUILayout.Width(24)))
                    {
                        if (newIndex > 0 && newIndex - 1 < keys.Count)
                        {
                            var toRemove = keys[newIndex - 1];
                            settings.vpmmKeyHistory.Remove(toRemove);
                            if (string.Equals(settings.vpmmApiKey, toRemove, StringComparison.Ordinal))
                                settings.vpmmApiKey = "";
                            EditorGUI.EndChangeCheck();
                            VpmPackagerProjectSettings.Save(settings);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }

            if (newIndex != selectedIndex && newIndex > 0 && newIndex - 1 < keys.Count)
            {
                settings.vpmmApiKey = keys[newIndex - 1];
                currentKey = settings.vpmmApiKey;
            }

            // API key input with save button ("+") after it.
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.vpmmApiKey = EditorGUILayout.PasswordField("API key", settings.vpmmApiKey ?? "");
                if (GUILayout.Button("+", GUILayout.Width(24)))
                {
                    VpmPackagerProjectSettings.AddKeyToHistory(settings, settings.vpmmApiKey);
                    EditorGUI.EndChangeCheck(); // avoid double-save logic below; we'll save explicitly
                    VpmPackagerProjectSettings.Save(settings);
                    GUIUtility.ExitGUI();
                }
            }

            // Index cache status (uses cached in-memory state; disk is polled at most once per second)
            if (_indexCacheAvailable)
                EditorGUILayout.LabelField("Index cache", $"Last updated: {_indexCacheLastUpdatedUtc:u}", EditorStyles.miniLabel);
            else
                EditorGUILayout.LabelField("Index cache", VpmmIndexDownloader.IsDownloading ? "Downloading..." : "Not downloaded yet", EditorStyles.miniLabel);

            // Reload button below API key input
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reload cache", GUILayout.Width(110)))
                {
                    VpmmIndexDownloader.DownloadAsync(settings, force: true);
                    _nextCachePollTime = 0; // poll immediately
                }
            }

            if (!string.IsNullOrWhiteSpace(VpmmIndexDownloader.LastError))
            {
                EditorGUILayout.HelpBox("VPMM index download error: " + VpmmIndexDownloader.LastError, MessageType.Warning);
            }

            // Upload toggle at the bottom.
            settings.vpmmUploadEnabled = EditorGUILayout.ToggleLeft("Upload export to VPMM after zip build", settings.vpmmUploadEnabled);

            if (settings.vpmmUploadEnabled && string.IsNullOrWhiteSpace(settings.vpmmApiKey))
            {
                EditorGUILayout.HelpBox("Upload is enabled, but no API key is set.", MessageType.Warning);
            }

            if (EditorGUI.EndChangeCheck())
            {
                // Keep history tidy: if user typed a key that matches a saved one, no need to add.
                VpmPackagerProjectSettings.Save(settings);
                _vpmmSettings = settings;
            }
        }

        private void ForceRefreshIndexCache()
        {
            _indexCacheFileWriteTimeUtc = VpmmIndexCache.CacheWriteTimeUtcOrMin();
            _indexCacheAvailable = VpmmIndexCache.TryLoad(out _indexCache);
            _indexCacheLastUpdatedUtc = _indexCacheAvailable ? _indexCache.lastUpdatedUtc : DateTime.MinValue;
            _depWarningsDirty = true;
            _versionInfoDirty = true;
        }

        private void PollIndexCacheThrottled()
        {
            // Disk poll no more than once per second.
            if (EditorApplication.timeSinceStartup < _nextCachePollTime) return;
            _nextCachePollTime = EditorApplication.timeSinceStartup + 1.0;

            var writeTime = VpmmIndexCache.CacheWriteTimeUtcOrMin();
            if (writeTime != _indexCacheFileWriteTimeUtc)
                ForceRefreshIndexCache();
        }

        private static bool CanWriteToAssetPath(string assetPath, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                reason = "Could not determine file path.";
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    reason = "File does not exist on disk.";
                    return false;
                }

                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    reason = "File is read-only on disk. Remove the read-only attribute (or check it out from version control) to Apply changes.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }
}

