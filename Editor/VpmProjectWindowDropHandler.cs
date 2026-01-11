using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace hackebein.vpm.packager.editor
{
    [InitializeOnLoad]
    internal static class VpmProjectWindowDropHandler
    {
        static VpmProjectWindowDropHandler()
        {
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemOnGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemOnGUI;
        }

        private static void OnProjectWindowItemOnGUI(string guid, Rect selectionRect)
        {
            var evt = Event.current;
            if (evt == null) return;

            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;

            if (!selectionRect.Contains(evt.mousePosition))
                return;

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!IsPackageJson(assetPath))
                return;

            assetPath = VpmDependencyCollector.NormalizeAssetPath(assetPath);

            var dragged = GetDraggedAssetPaths();
            dragged.RemoveAll(p => string.Equals(p, assetPath, StringComparison.Ordinal));
            if (dragged.Count == 0)
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                evt.Use();
                VpmZipExportWindow.Open(assetPath, dragged);
            }
            else
            {
                evt.Use();
            }
        }

        private static bool IsPackageJson(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return false;
            return string.Equals(Path.GetFileName(assetPath), "package.json", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetDraggedAssetPaths()
        {
            var result = new List<string>();

            if (DragAndDrop.objectReferences != null)
            {
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj == null) continue;
                    var p = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    result.Add(VpmDependencyCollector.NormalizeAssetPath(p));
                }
            }

            if (DragAndDrop.paths != null)
            {
                foreach (var p in DragAndDrop.paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var norm = p.Replace('\\', '/');
                    if (norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(norm);
                    }
                }
            }

            return result.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}

