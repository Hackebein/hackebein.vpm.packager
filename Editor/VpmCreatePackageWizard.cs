using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace hackebein.vpm.packager.editor
{
    internal sealed class VpmCreatePackageWizard : EditorWindow
    {
        [Serializable]
        private sealed class StringPair
        {
            public string key = "";
            public string value = "";
        }

        private DefaultAsset _parentFolder;
        private string _folderName = "New VPM Package";

        private string _packageName = "com.example.my-package";
        private string _displayName = "My Package";
        private string _version = "1.0.0";
        private string _description = "";
        private string _authorName = "";
        private string _authorEmail = "";

        private List<StringPair> _vpmDeps = new List<StringPair>();

        private bool _createRuntime = true;
        private bool _createEditor = true;
        private bool _createDocs = false;
        private bool _createReadme = true;

        private Vector2 _scroll;

        [MenuItem("Tools/VPM Packager/Create VPM Package…", false, 100)]
        private static void Open()
        {
            var wnd = GetWindow<VpmCreatePackageWizard>(utility: false, title: "Create VPM Package");
            wnd.minSize = new Vector2(560, 560);
            wnd.EnsureDefaults();
            wnd.Show();
        }

        private void EnsureDefaults()
        {
            if (_parentFolder == null)
            {
                var vpmFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/VPM");
                _parentFolder = vpmFolder != null ? vpmFolder : AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets");
            }
        }

        private void OnGUI()
        {
            EnsureDefaults();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Location", EditorStyles.boldLabel);
            _parentFolder = (DefaultAsset)EditorGUILayout.ObjectField("Parent folder", _parentFolder, typeof(DefaultAsset), allowSceneObjects: false);
            _folderName = EditorGUILayout.TextField("Package folder name", _folderName);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("package.json", EditorStyles.boldLabel);

            _packageName = EditorGUILayout.TextField("name", _packageName);
            _displayName = EditorGUILayout.TextField("displayName", _displayName);
            _version = EditorGUILayout.TextField("version", _version);
            _description = EditorGUILayout.TextField("description", _description);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Author", EditorStyles.boldLabel);
            _authorName = EditorGUILayout.TextField("author.name", _authorName);
            _authorEmail = EditorGUILayout.TextField("author.email", _authorEmail);

            EditorGUILayout.Space(10);
            DrawStringMap("VPM Dependencies", _vpmDeps, help: "Optional. Example: com.vrchat.avatars -> 3.10.x");

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Optional folders/files", EditorStyles.boldLabel);
            _createRuntime = EditorGUILayout.ToggleLeft("Create Runtime/ folder", _createRuntime);
            _createEditor = EditorGUILayout.ToggleLeft("Create Editor/ folder", _createEditor);
            _createDocs = EditorGUILayout.ToggleLeft("Create Documentation~/ folder", _createDocs);
            _createReadme = EditorGUILayout.ToggleLeft("Create README.md", _createReadme);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(10);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create", GUILayout.Height(32), GUILayout.Width(160)))
                    CreatePackage();
            }
        }

        private void CreatePackage()
        {
            var parentAssetPath = _parentFolder != null ? AssetDatabase.GetAssetPath(_parentFolder) : "Assets";
            parentAssetPath = VpmDependencyCollector.NormalizeAssetPath(parentAssetPath);

            if (string.IsNullOrWhiteSpace(parentAssetPath) || !AssetDatabase.IsValidFolder(parentAssetPath))
            {
                EditorUtility.DisplayDialog("Create VPM Package", "Parent folder is not a valid project folder.", "OK");
                return;
            }

            if (!parentAssetPath.StartsWith("Assets", StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("Create VPM Package", "Parent folder must be under Assets/.", "OK");
                return;
            }

            var folderName = (_folderName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "New VPM Package";
            folderName = SanitizeFolderName(folderName);

            var packageRoot = CombineAssetPath(parentAssetPath, folderName);
            EnsureFolderRecursive(packageRoot);

            if (_createRuntime) EnsureFolderRecursive(CombineAssetPath(packageRoot, "Runtime"));
            if (_createEditor) EnsureFolderRecursive(CombineAssetPath(packageRoot, "Editor"));
            if (_createDocs) EnsureFolderRecursive(CombineAssetPath(packageRoot, "Documentation~"));

            var packageJsonAssetPath = CombineAssetPath(packageRoot, "package.json");
            if (File.Exists(Path.GetFullPath(packageJsonAssetPath)))
            {
                EditorUtility.DisplayDialog("Create VPM Package", "package.json already exists in that folder.", "OK");
                return;
            }

            var root = BuildPackageJsonObject();
            if (!VpmJsonIO.TryWriteJsonObjectToAssetPath(packageJsonAssetPath, root, out var error))
            {
                EditorUtility.DisplayDialog("Create VPM Package", error ?? "Failed to write package.json.", "OK");
                return;
            }

            if (_createReadme)
            {
                TryWriteReadme(packageRoot);
            }

            AssetDatabase.Refresh();

            var created = AssetDatabase.LoadAssetAtPath<TextAsset>(packageJsonAssetPath);
            if (created != null)
            {
                Selection.activeObject = created;
                EditorGUIUtility.PingObject(created);
            }
        }

        private Dictionary<string, object> BuildPackageJsonObject()
        {
            var root = new Dictionary<string, object>();

            root["name"] = (_packageName ?? "").Trim();
            root["displayName"] = (_displayName ?? "").Trim();
            root["version"] = (_version ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(_description))
                root["description"] = _description.Trim();

            var author = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(_authorName))
                author["name"] = _authorName.Trim();
            if (!string.IsNullOrWhiteSpace(_authorEmail))
                author["email"] = _authorEmail.Trim();
            if (author.Count > 0)
                root["author"] = author;

            var deps = new Dictionary<string, object>();
            foreach (var row in _vpmDeps ?? new List<StringPair>())
            {
                if (row == null) continue;
                var k = (row.key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(k)) continue;
                deps[k] = (row.value ?? "").Trim();
            }
            if (deps.Count > 0)
                root["vpmDependencies"] = deps;

            return root;
        }

        private void TryWriteReadme(string packageRoot)
        {
            try
            {
                var fullRoot = Path.GetFullPath(packageRoot);
                var readmePath = Path.Combine(fullRoot, "README.md");
                if (File.Exists(readmePath)) return;

                var content =
                    "# " + (string.IsNullOrWhiteSpace(_displayName) ? "VPM Package" : _displayName.Trim()) + "\n\n" +
                    "- name: " + (_packageName ?? "").Trim() + "\n" +
                    "- version: " + (_version ?? "").Trim() + "\n";

                File.WriteAllText(readmePath, content);
            }
            catch
            {
                // best-effort
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
                    row.key = EditorGUILayout.TextField(row.key, GUILayout.MinWidth(140));
                    row.value = EditorGUILayout.TextField(row.value, GUILayout.MinWidth(100));

                    if (GUILayout.Button("X", GUILayout.Width(22)))
                        removeAt = i;
                }
            }

            if (removeAt >= 0 && removeAt < list.Count)
                list.RemoveAt(removeAt);

            if (GUILayout.Button("Add", GUILayout.Width(80)))
                list.Add(new StringPair());
        }

        private static void EnsureFolderRecursive(string assetPath)
        {
            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath).TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;

            var parts = assetPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var current = parts[0]; // e.g. Assets
            if (!AssetDatabase.IsValidFolder(current))
                throw new InvalidOperationException($"Root folder '{current}' does not exist.");

            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static string CombineAssetPath(string a, string b)
        {
            a = VpmDependencyCollector.NormalizeAssetPath(a).TrimEnd('/');
            b = VpmDependencyCollector.NormalizeAssetPath(b).TrimStart('/');
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a + "/" + b;
        }

        private static string SanitizeFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return "New VPM Package";

            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                folderName = folderName.Replace(c, '_');

            // Avoid creating hidden folders on unix.
            folderName = folderName.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "New VPM Package";

            return folderName;
        }
    }
}

