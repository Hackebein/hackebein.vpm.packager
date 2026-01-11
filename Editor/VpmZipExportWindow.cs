using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace hackebein.vpm.packager.editor
{
    internal sealed class VpmZipExportWindow : EditorWindow
    {
        private enum FileTypeFilter
        {
            All = 0,
            Animations = 1,
            Controllers = 2,
            Prefabs = 3,
            Materials = 4,
            Textures = 5,
            Audio = 6,
            Other = 7,
        }

        private sealed class Entry
        {
            public string assetPath;
            public bool isExternal;
        }

        private string _packageJsonAssetPath;
        private string _packageRootAssetPath;
        private string _outputZipPath;

        private bool _includeDependencies = true;

        private List<string> _seedAssetPaths = new List<string>();
        private List<Entry> _entries = new List<Entry>();
        private Dictionary<string, bool> _selected = new Dictionary<string, bool>(StringComparer.Ordinal);
        private Dictionary<string, HashSet<string>> _dependencyClosureCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        private Vector2 _scroll;

        private TreeViewState _treeState;
        private VpmExportTreeView _treeView;
        private FileTypeFilter _filter = FileTypeFilter.All;
        private static GUIContent[] _filterContents;
        private bool _treeDidInitialExpand;
        private HashSet<string> _vpmDepPackagesDirect = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _vpmDepPackagesClosure = new HashSet<string>(StringComparer.Ordinal);

        internal static void Open(string packageJsonAssetPath, IEnumerable<string> seedAssetPaths)
        {
            if (string.IsNullOrWhiteSpace(packageJsonAssetPath))
            {
                EditorUtility.DisplayDialog("VPM Zip Export", "packageJsonAssetPath is empty.", "OK");
                return;
            }

            var wnd = GetWindow<VpmZipExportWindow>(utility: true, title: "Export VPM Zip");
            wnd.minSize = new Vector2(560, 520);
            wnd._packageJsonAssetPath = VpmDependencyCollector.NormalizeAssetPath(packageJsonAssetPath);
            wnd._packageRootAssetPath = GetParentFolderAssetPath(wnd._packageJsonAssetPath);
            wnd._seedAssetPaths = VpmDependencyCollector.NormalizeAssetPaths(seedAssetPaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            wnd._outputZipPath = wnd.SuggestDefaultZipPath();
            wnd.RebuildEntries();
            wnd.Show();
        }

        private void OnEnable()
        {
            if (_treeState == null) _treeState = new TreeViewState();
        }

        private void OnGUI()
        {
            if (string.IsNullOrWhiteSpace(_packageJsonAssetPath))
            {
                EditorGUILayout.HelpBox("No package.json selected.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("Package", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("package.json", _packageJsonAssetPath);
                EditorGUILayout.TextField("package root", _packageRootAssetPath);
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                var newIncludeDeps = EditorGUILayout.ToggleLeft("Include dependencies", _includeDependencies, GUILayout.Width(180));
                if (newIncludeDeps != _includeDependencies)
                {
                    _includeDependencies = newIncludeDeps;
                    RebuildEntries();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Select All", GUILayout.Width(90)))
                    SetAllSelected(true);
                if (GUILayout.Button("Select None", GUILayout.Width(90)))
                    SetAllSelected(false);
            }

            EditorGUILayout.Space(4);
            DrawFilterToolbar();

            EditorGUILayout.Space(6);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Contents", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("`.meta` files are automatically included for every selected asset.", MessageType.None);

            DrawTree();

            EditorGUILayout.Space(8);

            DrawOutputPath();

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_outputZipPath)))
                {
                    if (GUILayout.Button("Export Zip", GUILayout.Height(30), GUILayout.Width(160)))
                        Export();
                }
            }
        }

        private static GUIContent[] GetFilterContents()
        {
            if (_filterContents != null) return _filterContents;

            // Use Unity's built-in type icons where possible.
            _filterContents = new[]
            {
                new GUIContent("All", EditorGUIUtility.IconContent("FilterByType").image, "Show all files"),
                new GUIContent("Anim", EditorGUIUtility.IconContent("AnimationClip Icon").image, "Show .anim"),
                new GUIContent("Ctrl", EditorGUIUtility.IconContent("AnimatorController Icon").image, "Show .controller"),
                new GUIContent("Prefab", EditorGUIUtility.IconContent("Prefab Icon").image, "Show .prefab"),
                new GUIContent("Mat", EditorGUIUtility.IconContent("Material Icon").image, "Show .mat"),
                new GUIContent("Tex", EditorGUIUtility.IconContent("Texture2D Icon").image, "Show textures"),
                new GUIContent("Audio", EditorGUIUtility.IconContent("AudioClip Icon").image, "Show audio"),
                new GUIContent("Other", EditorGUIUtility.IconContent("DefaultAsset Icon").image, "Show other file types"),
            };

            return _filterContents;
        }

        private void DrawFilterToolbar()
        {
            var rect = EditorGUILayout.GetControlRect(false, 22);
            var labelRect = rect;
            labelRect.width = 40;
            var toolbarRect = rect;
            toolbarRect.x += labelRect.width;
            toolbarRect.width -= labelRect.width;

            EditorGUI.LabelField(labelRect, "Filter");
            var newFilter = (FileTypeFilter)GUI.Toolbar(toolbarRect, (int)_filter, GetFilterContents());
            if (newFilter != _filter)
            {
                _filter = newFilter;
                if (_treeView != null)
                {
                    _treeView.SetFilter(_filter);
                    _treeView.Reload();
                }
            }
        }

        private void DrawOutputPath()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputZipPath = EditorGUILayout.TextField("zip path", _outputZipPath);
                if (GUILayout.Button("Browse...", GUILayout.Width(90)))
                {
                    var folder = "";
                    var defaultName = "package.zip";
                    try
                    {
                        folder = string.IsNullOrWhiteSpace(_outputZipPath) ? "" : Path.GetDirectoryName(_outputZipPath);
                        defaultName = string.IsNullOrWhiteSpace(_outputZipPath) ? defaultName : Path.GetFileName(_outputZipPath);
                    }
                    catch { /* ignored */ }

                    var chosen = EditorUtility.SaveFilePanel("Export VPM Zip", folder, defaultName, "zip");
                    if (!string.IsNullOrWhiteSpace(chosen))
                        _outputZipPath = chosen;
                }
            }
        }

        private void DrawTree()
        {
            EnsureTree();

            var rect = GUILayoutUtility.GetRect(0, 100000, 0, 100000, GUILayout.ExpandHeight(true));
            _treeView.OnGUI(rect);
        }

        private void EnsureTree()
        {
            if (_treeState == null) _treeState = new TreeViewState();
            if (_treeView == null)
            {
                _treeView = new VpmExportTreeView(
                    _treeState,
                    () => _selected,
                    assetPath => SetSelected(assetPath, true),
                    assetPath => SetSelected(assetPath, false),
                    (assetPath, v) => SetSelected(assetPath, v)
                );
                _treeView.SetFilter(_filter);
            }

            _treeView.SetData(_entries.Select(e => e.assetPath).ToList(), _packageJsonAssetPath);

            // Start fully expanded on first open, but allow users to fold afterwards.
            if (!_treeDidInitialExpand)
            {
                _treeView.ExpandAll();
                _treeView.CollapseVpmDependencyPackages(_vpmDepPackagesClosure);
                _treeDidInitialExpand = true;
            }
        }

        private void Export()
        {
            try
            {
                var includedAssets = _selected
                    .Where(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList();

                if (!includedAssets.Contains(_packageJsonAssetPath))
                    includedAssets.Add(_packageJsonAssetPath);

                var result = VpmZipBuilder.BuildZip(
                    packageJsonAssetPath: _packageJsonAssetPath,
                    packageRootAssetPath: _packageRootAssetPath,
                    includedAssetPaths: includedAssets,
                    outputZipPath: _outputZipPath
                );

                var settings = VpmPackagerProjectSettings.Load();
                if (settings.vpmmUploadEnabled)
                {
                    if (string.IsNullOrWhiteSpace(settings.vpmmApiKey))
                    {
                        EditorUtility.DisplayDialog(
                            "VPM Zip Export",
                            $"Exported zip:\n{result.zipPath}\n\nFiles: {result.fileCount}\nSkipped: {result.skipped.Count}\n\nVPMM upload is enabled, but API key is empty.",
                            "OK"
                        );
                        EditorUtility.RevealInFinder(result.zipPath);
                        return;
                    }

                    // Make sure the used key is available in history for next time.
                    VpmPackagerProjectSettings.AddKeyToHistory(settings, settings.vpmmApiKey);
                    VpmPackagerProjectSettings.Save(settings);

                    VpmmClient.UploadZipAsync(
                        baseUrl: settings.vpmmBaseUrl,
                        apiKey: settings.vpmmApiKey,
                        zipPath: result.zipPath,
                        onSuccess: jobId =>
                        {
                            EditorUtility.DisplayDialog(
                                "VPM Zip Export",
                                $"Exported zip:\n{result.zipPath}\n\nFiles: {result.fileCount}\nSkipped: {result.skipped.Count}\n\nUploaded to VPMM.\nJob ID: {jobId}",
                                "OK"
                            );
                            EditorUtility.RevealInFinder(result.zipPath);
                        },
                        onError: err =>
                        {
                            EditorUtility.DisplayDialog(
                                "VPM Zip Export",
                                $"Exported zip:\n{result.zipPath}\n\nFiles: {result.fileCount}\nSkipped: {result.skipped.Count}\n\nVPMM upload failed:\n{err}",
                                "OK"
                            );
                            EditorUtility.RevealInFinder(result.zipPath);
                        }
                    );
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "VPM Zip Export",
                        $"Exported zip:\n{result.zipPath}\n\nFiles: {result.fileCount}\nSkipped: {result.skipped.Count}",
                        "OK"
                    );

                    EditorUtility.RevealInFinder(result.zipPath);
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("VPM Zip Export", ex.Message, "OK");
            }
        }

        private void RebuildEntries()
        {
            _dependencyClosureCache.Clear();

            // Always include the package.json in seeds.
            var seeds = new List<string>(_seedAssetPaths) { _packageJsonAssetPath };
            var collected = VpmDependencyCollector.CollectAssets(
                seedAssetPaths: seeds,
                includeDependencies: _includeDependencies
            );

            // Ensure package.json is always present even if parsing/filtering changes.
            collected.Add(_packageJsonAssetPath);

            // Auto-uncheck anything that belongs to packages listed in vpmDependencies.
            // (We still include them in the list so users can override manually.)
            _vpmDepPackagesDirect = GetVpmDependencyPackageNames(_packageJsonAssetPath);
            _vpmDepPackagesClosure = ExpandTransitiveUnityPackageDeps(_vpmDepPackagesDirect);

            // Preserve prior selections.
            foreach (var ap in collected)
            {
                if (!_selected.ContainsKey(ap))
                    _selected[ap] = ShouldDefaultSelect(ap, _vpmDepPackagesDirect);
            }

            // Remove selections that no longer exist.
            var toRemove = _selected.Keys.Where(k => !collected.Contains(k)).ToList();
            foreach (var k in toRemove)
                _selected.Remove(k);

            _selected[_packageJsonAssetPath] = true;

            _entries = collected
                .Select(ap => new Entry
                {
                    assetPath = ap,
                    isExternal = !IsUnderRoot(_packageRootAssetPath, ap)
                })
                .OrderBy(e => e.isExternal ? 1 : 0)
                .ThenBy(e => e.assetPath, StringComparer.Ordinal)
                .ToList();

            if (_treeView != null)
                _treeView.SetData(_entries.Select(e => e.assetPath).ToList(), _packageJsonAssetPath);
        }

        private bool GetSelected(string assetPath)
        {
            if (string.Equals(assetPath, _packageJsonAssetPath, StringComparison.Ordinal))
                return true;
            return _selected.TryGetValue(assetPath, out var b) && b;
        }

        private void SetSelected(string assetPath, bool selected)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            if (string.Equals(assetPath, _packageJsonAssetPath, StringComparison.Ordinal))
            {
                _selected[assetPath] = true;
                return;
            }

            _selected[assetPath] = selected;
            if (selected)
                CascadeSelectDependencies(assetPath);
            else
                CascadeDeselectDependencies(assetPath);

            Repaint();
        }

        private void CascadeSelectDependencies(string rootAssetPath)
        {
            rootAssetPath = VpmDependencyCollector.NormalizeAssetPath(rootAssetPath);
            if (string.IsNullOrWhiteSpace(rootAssetPath)) return;

            // Only leaf assets have meaningful dependencies; folders are expanded into leaf assets earlier.
            if (AssetDatabase.IsValidFolder(rootAssetPath)) return;

            var deps = GetDependencyClosure(rootAssetPath);
            if (deps == null || deps.Count == 0) return;

            foreach (var dep in deps)
            {
                if (string.IsNullOrWhiteSpace(dep)) continue;
                if (string.Equals(dep, _packageJsonAssetPath, StringComparison.Ordinal)) continue;

                // Only select items that are part of the current export list/tree.
                // (If "Include dependencies" is off, dependencies are not present in `_selected`.)
                if (_selected.ContainsKey(dep))
                    _selected[dep] = true;
            }
        }

        private void CascadeDeselectDependencies(string rootAssetPath)
        {
            rootAssetPath = VpmDependencyCollector.NormalizeAssetPath(rootAssetPath);
            if (string.IsNullOrWhiteSpace(rootAssetPath)) return;

            // Only leaf assets have meaningful dependencies; folders are expanded into leaf assets earlier.
            if (AssetDatabase.IsValidFolder(rootAssetPath)) return;

            var deps = GetDependencyClosure(rootAssetPath);
            if (deps == null || deps.Count == 0) return;

            // Two-phase deselect:
            // - Determine candidate dependencies to potentially deselect (transitive closure minus root/package.json).
            // - Only keep dependencies that are required by some *other* selected asset outside this candidate set.
            //
            // This avoids a one-pass ordering issue where a dependency can temporarily "protect" its own dependencies.
            var candidate = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dep in deps)
            {
                if (string.IsNullOrWhiteSpace(dep)) continue;
                if (string.Equals(dep, rootAssetPath, StringComparison.Ordinal)) continue;
                if (string.Equals(dep, _packageJsonAssetPath, StringComparison.Ordinal)) continue;
                candidate.Add(dep);
            }

            if (candidate.Count == 0) return;

            foreach (var dep in candidate)
            {
                if (!_selected.TryGetValue(dep, out var isSel) || !isSel) continue;

                // Only deselect dependencies that no other currently-selected asset still needs.
                // Note: assets inside `candidate` are expected to be deselected as part of this cascade,
                // so they must not count as "other selected assets".
                if (!IsDependencyRequiredByOtherSelectedAssets(dep, excludingAssetPath: rootAssetPath, excludingCandidates: candidate))
                    _selected[dep] = false;
            }
        }

        private bool IsDependencyRequiredByOtherSelectedAssets(string dependencyAssetPath, string excludingAssetPath, HashSet<string> excludingCandidates)
        {
            dependencyAssetPath = VpmDependencyCollector.NormalizeAssetPath(dependencyAssetPath);
            excludingAssetPath = VpmDependencyCollector.NormalizeAssetPath(excludingAssetPath);

            foreach (var kv in _selected)
            {
                if (!kv.Value) continue;
                var ap = kv.Key;
                if (string.IsNullOrWhiteSpace(ap)) continue;
                if (string.Equals(ap, excludingAssetPath, StringComparison.Ordinal)) continue;
                // Do not let the dependency "protect itself" (AssetDatabase.GetDependencies includes the asset itself).
                // If this dependency is only selected because it was pulled in by other assets, we still want to be able to
                // cascade-deselect it when its roots are deselected.
                if (string.Equals(ap, dependencyAssetPath, StringComparison.Ordinal)) continue;
                if (excludingCandidates != null && excludingCandidates.Contains(ap)) continue;
                if (string.Equals(ap, _packageJsonAssetPath, StringComparison.Ordinal)) continue;
                if (AssetDatabase.IsValidFolder(ap)) continue;

                var closure = GetDependencyClosure(ap);
                if (closure != null && closure.Contains(dependencyAssetPath))
                    return true;
            }

            return false;
        }

        private HashSet<string> GetDependencyClosure(string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(assetPath)) return null;

            if (_dependencyClosureCache.TryGetValue(assetPath, out var cached))
                return cached;

            HashSet<string> set;
            try
            {
                var deps = AssetDatabase.GetDependencies(assetPath, recursive: true) ?? Array.Empty<string>();
                set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in deps)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var n = VpmDependencyCollector.NormalizeAssetPath(d);
                    if (n.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    set.Add(n);
                }
            }
            catch
            {
                set = new HashSet<string>(StringComparer.Ordinal);
            }

            _dependencyClosureCache[assetPath] = set;
            return set;
        }

        private void SetAllSelected(bool selected)
        {
            var keys = _selected.Keys.ToList();
            foreach (var k in keys)
            {
                if (string.Equals(k, _packageJsonAssetPath, StringComparison.Ordinal))
                    _selected[k] = true;
                else
                    _selected[k] = selected;
            }

            if (_treeView != null)
                _treeView.Reload();
        }

        private bool ShouldDefaultSelect(string assetPath, HashSet<string> vpmDepPackages)
        {
            if (string.Equals(assetPath, _packageJsonAssetPath, StringComparison.Ordinal))
                return true;

            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                return true;

            // Use Unity package resolution to identify which package owns this file.
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info != null && vpmDepPackages.Contains(info.name))
                    return false;
            }
            catch
            {
                // ignore
            }

            return true;
        }

        private static HashSet<string> GetVpmDependencyPackageNames(string packageJsonAssetPath)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!VpmJsonIO.TryReadJsonObjectAtAssetPath(packageJsonAssetPath, out var root, out _))
                    return set;
                var model = VpmPackageManifestModel.FromRawRoot(root);
                foreach (var k in model.vpmDependencies.Keys)
                {
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    set.Add(k.Trim());
                }
            }
            catch
            {
                // ignore
            }

            return set;
        }

        private static HashSet<string> ExpandTransitiveUnityPackageDeps(HashSet<string> direct)
        {
            var result = new HashSet<string>(direct ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            if (direct == null || direct.Count == 0) return result;

            try
            {
                var all = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                var byName = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>(StringComparer.Ordinal);
                foreach (var p in all)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                    byName[p.name] = p;
                }

                var q = new Queue<string>(direct);
                while (q.Count > 0)
                {
                    var name = q.Dequeue();
                    if (!byName.TryGetValue(name, out var info) || info == null) continue;

                    var depsProp = info.GetType().GetProperty("dependencies");
                    var depsObj = depsProp != null ? depsProp.GetValue(info) : null;
                    if (depsObj is Array depsArr)
                    {
                        foreach (var dep in depsArr)
                        {
                            if (dep == null) continue;
                            var nProp = dep.GetType().GetProperty("name");
                            var depName = nProp != null ? nProp.GetValue(dep) as string : null;
                            depName = (depName ?? "").Trim();
                            if (depName.Length == 0) continue;
                            if (result.Add(depName))
                                q.Enqueue(depName);
                        }
                    }
                }
            }
            catch
            {
                // best-effort; if PackageManager API changes, we just collapse direct deps
                return new HashSet<string>(direct, StringComparer.Ordinal);
            }

            return result;
        }

        private string SuggestDefaultZipPath()
        {
            var fileName = "package.zip";
            try
            {
                if (VpmJsonIO.TryReadJsonObjectAtAssetPath(_packageJsonAssetPath, out var root, out _))
                {
                    var model = VpmPackageManifestModel.FromRawRoot(root);
                    var n = SanitizeFileName(string.IsNullOrWhiteSpace(model.name) ? "package" : model.name.Trim());
                    var v = SanitizeFileName(string.IsNullOrWhiteSpace(model.version) ? "0.0.0" : model.version.Trim());
                    fileName = $"{n}-{v}.zip";
                }
            }
            catch { /* ignore */ }

            var folder = "";
            try
            {
                folder = Path.GetFullPath(_packageRootAssetPath);
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            return Path.Combine(folder, fileName);
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "package";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private static string GetParentFolderAssetPath(string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath).TrimEnd('/');
            var i = assetPath.LastIndexOf('/');
            if (i <= 0) return "";
            return assetPath.Substring(0, i);
        }

        private static bool IsUnderRoot(string rootAssetPath, string assetPath)
        {
            rootAssetPath = VpmDependencyCollector.NormalizeAssetPath(rootAssetPath).TrimEnd('/');
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);

            if (assetPath.Equals(rootAssetPath, StringComparison.Ordinal)) return true;
            return assetPath.StartsWith(rootAssetPath + "/", StringComparison.Ordinal);
        }

        private sealed class VpmExportTreeView : TreeView
        {
            private sealed class Item : TreeViewItem
            {
                public string assetPath;
                public bool isFolder;
                public string fullPathKey;
            }

            private readonly Func<Dictionary<string, bool>> _getSelected;
            private readonly Action<string> _forceSelect;
            private readonly Action<string> _forceDeselect;
            private readonly Action<string, bool> _setSelected;

            private List<string> _assetPaths = new List<string>();
            private string _packageJsonPath;
            private FileTypeFilter _filter = FileTypeFilter.All;

            public VpmExportTreeView(
                TreeViewState state,
                Func<Dictionary<string, bool>> getSelected,
                Action<string> forceSelect,
                Action<string> forceDeselect,
                Action<string, bool> setSelected
            ) : base(state)
            {
                _getSelected = getSelected;
                _forceSelect = forceSelect;
                _forceDeselect = forceDeselect;
                _setSelected = setSelected;
                showBorder = true;
                showAlternatingRowBackgrounds = true;
                Reload();
            }

            public void SetFilter(FileTypeFilter filter)
            {
                _filter = filter;
            }

            public void SetData(List<string> assetPaths, string packageJsonPath)
            {
                _assetPaths = assetPaths ?? new List<string>();
                _packageJsonPath = packageJsonPath;
                Reload();
            }

            public void CollapseVpmDependencyPackages(HashSet<string> packageNames)
            {
                if (packageNames == null || packageNames.Count == 0) return;

                // Collapse Packages/<name> roots for packages listed in vpmDependencies.
                foreach (var pkg in packageNames)
                {
                    if (string.IsNullOrWhiteSpace(pkg)) continue;
                    var key = "Packages/" + pkg.Trim();
                    SetExpanded(MakeStableId(key), false);
                }
            }

            protected override TreeViewItem BuildRoot()
            {
                // TreeView requires rootItem.children to be non-null, even if empty.
                var root = new Item { id = 0, depth = -1, displayName = "Root", isFolder = true, fullPathKey = "" };
                root.children = new List<TreeViewItem>();

                var nodeByKey = new Dictionary<string, Item>(StringComparer.Ordinal);
                nodeByKey[""] = root;

                foreach (var ap in _assetPaths
                             .Where(ShouldIncludeForFilter)
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    var path = VpmDependencyCollector.NormalizeAssetPath(ap).Trim('/');
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var segments = path.Split('/');
                    var parentKey = "";
                    for (var i = 0; i < segments.Length; i++)
                    {
                        var seg = segments[i];
                        var isLeaf = i == segments.Length - 1;
                        var key = parentKey.Length == 0 ? seg : parentKey + "/" + seg;

                        if (!nodeByKey.TryGetValue(key, out var node))
                        {
                            node = new Item
                            {
                                id = MakeStableId(key),
                                depth = i,
                                displayName = seg,
                                isFolder = !isLeaf,
                                fullPathKey = key,
                                assetPath = isLeaf ? path : null,
                            };
                            nodeByKey[key] = node;

                            var parent = nodeByKey[parentKey];
                            if (parent.children == null) parent.children = new List<TreeViewItem>();
                            parent.children.Add(node);
                        }

                        // If a previously-created folder becomes a leaf (shouldn't happen) keep folder.
                        if (isLeaf)
                        {
                            node.assetPath = path;
                            node.isFolder = false;
                        }

                        parentKey = key;
                    }
                }

                SetupDepthsFromParentsAndChildren(root);
                return root;
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var item = (Item)args.item;
                var rowRect = args.rowRect;

                var indent = GetContentIndent(item);
                var toggleRect = rowRect;
                toggleRect.x += indent;
                toggleRect.width = 18;

                var labelRect = rowRect;
                labelRect.x += indent + 18;
                labelRect.width -= indent + 18;

                var (all, none, mixed) = GetAggregateState(item);
                // For mixed folders, `all` is false (some items not selected).
                // We intentionally use `all` as the toggle value so clicking a mixed item turns it ON,
                // matching the UX expectation that clicking the checkbox selects everything.
                var current = all;

                var prevMixed = EditorGUI.showMixedValue;
                EditorGUI.showMixedValue = mixed;

                var isPackageJson = !item.isFolder && string.Equals(item.assetPath, _packageJsonPath, StringComparison.Ordinal);
                bool newVal;
                using (new EditorGUI.DisabledScope(isPackageJson))
                {
                    EditorGUI.BeginChangeCheck();
                    newVal = EditorGUI.Toggle(toggleRect, current);
                    if (EditorGUI.EndChangeCheck())
                        ApplySelection(item, newVal);
                }
                EditorGUI.showMixedValue = prevMixed;

                DrawIconAndLabel(item, labelRect);
            }

            protected override void DoubleClickedItem(int id)
            {
                var item = FindItem(id, rootItem) as Item;
                if (item == null) return;
                if (item.isFolder) return;
                if (string.IsNullOrWhiteSpace(item.assetPath)) return;

                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                }
            }

            private (bool all, bool none, bool mixed) GetAggregateState(Item item)
            {
                if (!item.isFolder)
                {
                    if (string.Equals(item.assetPath, _packageJsonPath, StringComparison.Ordinal))
                        return (all: true, none: false, mixed: false);

                    var sel = _getSelected();
                    var isSel = sel != null && item.assetPath != null && sel.TryGetValue(item.assetPath, out var b) && b;
                    return (all: isSel, none: !isSel, mixed: false);
                }

                if (item.children == null || item.children.Count == 0)
                    return (all: false, none: true, mixed: false);

                var anyAll = false;
                var anyNone = false;
                foreach (var c in item.children.OfType<Item>())
                {
                    var (all, none, mixed) = GetAggregateState(c);
                    if (mixed) return (all: false, none: false, mixed: true);
                    if (all) anyAll = true;
                    if (none) anyNone = true;
                    if (anyAll && anyNone) return (all: false, none: false, mixed: true);
                }

                if (anyAll && !anyNone) return (all: true, none: false, mixed: false);
                if (!anyAll && anyNone) return (all: false, none: true, mixed: false);
                return (all: false, none: false, mixed: true);
            }

            private void ApplySelection(Item item, bool selected)
            {
                if (!item.isFolder)
                {
                    if (string.Equals(item.assetPath, _packageJsonPath, StringComparison.Ordinal))
                    {
                        _forceSelect?.Invoke(item.assetPath);
                        return;
                    }

                    _setSelected?.Invoke(item.assetPath, selected);
                    return;
                }

                if (item.children == null) return;
                foreach (var c in item.children.OfType<Item>())
                {
                    ApplySelection(c, selected);
                }
            }

            private bool ShouldIncludeForFilter(string assetPath)
            {
                assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);
                if (_filter == FileTypeFilter.All)
                    return true;

                if (AssetDatabase.IsValidFolder(assetPath))
                    return false;

                var ext = Path.GetExtension(assetPath)?.ToLowerInvariant() ?? "";
                switch (_filter)
                {
                    case FileTypeFilter.Animations:
                        return ext == ".anim";
                    case FileTypeFilter.Controllers:
                        return ext == ".controller";
                    case FileTypeFilter.Prefabs:
                        return ext == ".prefab";
                    case FileTypeFilter.Materials:
                        return ext == ".mat";
                    case FileTypeFilter.Textures:
                        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".psd" || ext == ".gif" || ext == ".webp";
                    case FileTypeFilter.Audio:
                        return ext == ".wav" || ext == ".mp3" || ext == ".ogg";
                    case FileTypeFilter.Other:
                        return ext != ".anim" &&
                               ext != ".controller" &&
                               ext != ".prefab" &&
                               ext != ".mat" &&
                               ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga" && ext != ".psd" && ext != ".gif" && ext != ".webp" &&
                               ext != ".wav" && ext != ".mp3" && ext != ".ogg";
                    default:
                        return true;
                }
            }

            private void DrawIconAndLabel(Item item, Rect labelRect)
            {
                const float iconSize = 16f;
                var iconRect = labelRect;
                iconRect.width = iconSize;
                iconRect.height = iconSize;
                iconRect.y += (labelRect.height - iconSize) * 0.5f;

                var textRect = labelRect;
                textRect.x += iconSize + 4f;
                textRect.width -= iconSize + 4f;

                Texture icon = null;
                if (item.isFolder)
                {
                    var content = EditorGUIUtility.IconContent("Folder Icon");
                    icon = content != null ? content.image as Texture : null;
                }
                else if (!string.IsNullOrWhiteSpace(item.assetPath))
                {
                    icon = AssetDatabase.GetCachedIcon(item.assetPath);
                }

                if (icon != null)
                    GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, alphaBlend: true);

                EditorGUI.LabelField(textRect, item.displayName);
            }

            private static int MakeStableId(string fullPathKey)
            {
                // Stable across reloads so expand/collapse state persists.
                // Animator.StringToHash is deterministic across sessions.
                var id = Animator.StringToHash(fullPathKey ?? "");
                if (id == 0) id = 1;
                return id;
            }
        }
    }
}

