using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmPackagerProjectSettings
    {
        internal const int DefaultVpmmUploadChunkSizeMb = 16;
        internal const int MinVpmmUploadChunkSizeMb = 1;
        internal const int MaxVpmmUploadChunkSizeMb = 1024;

        [Serializable]
        internal sealed class Data
        {
            public bool vpmmUploadEnabled = false;
            public string vpmmBaseUrl = "https://vpmm.dev";

            // Currently active key (may or may not be present in keyHistory).
            public string vpmmApiKey = "";

            // Previous keys (stored in project, so be mindful of source control!).
            public List<string> vpmmKeyHistory = new List<string>();

            // Chunk payload size in MB for upload requests.
            public int vpmmUploadChunkSizeMb = DefaultVpmmUploadChunkSizeMb;
        }

        private const string SettingsRelativePath = "ProjectSettings/VpmPackagerSettings.json";
        private static Data _cache;
        private static DateTime _cacheWriteTimeUtc;

        internal static Data Load()
        {
            try
            {
                var fullPath = GetFullPath();
                if (!File.Exists(fullPath))
                    return CreateDefault();

                var writeTime = File.GetLastWriteTimeUtc(fullPath);
                if (_cache != null && writeTime == _cacheWriteTimeUtc)
                    return Clone(_cache);

                var json = File.ReadAllText(fullPath);
                var data = JsonUtility.FromJson<Data>(json) ?? CreateDefault();
                Normalize(data);

                _cache = Clone(data);
                _cacheWriteTimeUtc = writeTime;

                return data;
            }
            catch
            {
                return CreateDefault();
            }
        }

        internal static void Save(Data data)
        {
            if (data == null) data = CreateDefault();
            Normalize(data);

            var fullPath = GetFullPath();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

            var json = JsonUtility.ToJson(data, prettyPrint: true);
            if (!json.EndsWith("\n")) json += "\n";
            File.WriteAllText(fullPath, json);

            _cache = Clone(data);
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);

            // Not an asset, but refresh is harmless and ensures file watchers see it.
            AssetDatabase.Refresh();
        }

        internal static void AddKeyToHistory(Data data, string apiKey)
        {
            if (data == null) return;
            apiKey = (apiKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            if (data.vpmmKeyHistory == null)
                data.vpmmKeyHistory = new List<string>();

            if (!data.vpmmKeyHistory.Contains(apiKey))
                data.vpmmKeyHistory.Add(apiKey);
        }

        internal static string GetMaskedKeyLabel(string apiKey)
        {
            apiKey = (apiKey ?? "").Trim();
            if (apiKey.Length == 0) return "(empty)";
            if (apiKey.Length <= 6) return new string('*', apiKey.Length);

            var last = apiKey.Substring(apiKey.Length - 4, 4);
            return "****" + last;
        }

        private static string GetFullPath()
        {
            return Path.GetFullPath(SettingsRelativePath);
        }

        private static Data CreateDefault()
        {
            var data = new Data();
            Normalize(data);
            return data;
        }

        private static void Normalize(Data data)
        {
            if (data == null) return;

            data.vpmmBaseUrl = (data.vpmmBaseUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(data.vpmmBaseUrl))
                data.vpmmBaseUrl = "https://vpmm.dev";

            data.vpmmApiKey = (data.vpmmApiKey ?? "").Trim();

            if (data.vpmmUploadChunkSizeMb <= 0)
                data.vpmmUploadChunkSizeMb = DefaultVpmmUploadChunkSizeMb;
            data.vpmmUploadChunkSizeMb = Math.Max(MinVpmmUploadChunkSizeMb, Math.Min(MaxVpmmUploadChunkSizeMb, data.vpmmUploadChunkSizeMb));

            if (data.vpmmKeyHistory == null)
                data.vpmmKeyHistory = new List<string>();

            data.vpmmKeyHistory = data.vpmmKeyHistory
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct()
                .ToList();
        }

        private static Data Clone(Data data)
        {
            // JsonUtility clone is simple and avoids missing fields when we add more later.
            return JsonUtility.FromJson<Data>(JsonUtility.ToJson(data)) ?? CreateDefault();
        }
    }
}

