using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmmIndexDownloader
    {
        internal static bool IsDownloading => _activeRequestCount > 0;
        internal static string LastError => _lastError;

        private static int _activeRequestCount;
        private static string _lastError;

        internal static void EnsureCacheAsync(VpmPackagerProjectSettings.Data settings)
        {
            if (VpmmIndexCache.HasCache()) return;
            DownloadAsync(settings, force: false);
        }

        internal static void DownloadAsync(VpmPackagerProjectSettings.Data settings, bool force)
        {
            if (settings == null) settings = VpmPackagerProjectSettings.Load();

            // If already downloading, ignore extra triggers.
            if (IsDownloading) return;

            _lastError = null;

            var baseUrl = (settings.vpmmBaseUrl ?? "https://vpmm.dev").Trim().TrimEnd('/');

            var indices = new List<Dictionary<string, object>>();

            // 1) Public index (no auth)
            GetJsonAsync(
                url: baseUrl + "/index.json",
                authBearer: null,
                title: "Downloading VPMM public index",
                onSuccess: root =>
                {
                    if (root != null) indices.Add(root);

                    // 2) Private index (auth + needs account_id)
                    var apiKey = (settings.vpmmApiKey ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        SaveMerged(indices);
                        return;
                    }

                    GetJsonAsync(
                        url: baseUrl + "/api/auth",
                        authBearer: apiKey,
                        title: "Downloading VPMM auth",
                        onSuccess: authRoot =>
                        {
                            var accountId = ExtractAccountId(authRoot);
                            if (string.IsNullOrWhiteSpace(accountId))
                            {
                                _lastError = "Could not determine account_id from /api/auth response.";
                                SaveMerged(indices);
                                return;
                            }

                            GetJsonAsync(
                                url: baseUrl + "/index-" + accountId + ".json",
                                authBearer: apiKey,
                                title: "Downloading VPMM private index",
                                onSuccess: privateRoot =>
                                {
                                    if (privateRoot != null) indices.Add(privateRoot);
                                    SaveMerged(indices);
                                },
                                onError: err =>
                                {
                                    _lastError = err;
                                    SaveMerged(indices);
                                }
                            );
                        },
                        onError: err =>
                        {
                            _lastError = err;
                            SaveMerged(indices);
                        }
                    );
                },
                onError: err =>
                {
                    _lastError = err;
                    SaveMerged(indices);
                }
            );
        }

        private static void SaveMerged(List<Dictionary<string, object>> indices)
        {
            try
            {
                var cache = VpmmIndexCache.BuildFromIndexJsons(indices);
                VpmmIndexCache.Save(cache);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }

        private static void GetJsonAsync(string url, string authBearer, string title, Action<Dictionary<string, object>> onSuccess, Action<string> onError)
        {
            _activeRequestCount++;

            var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            if (!string.IsNullOrWhiteSpace(authBearer))
                request.SetRequestHeader("Authorization", "Bearer " + authBearer);

            var op = request.SendWebRequest();

            void Tick()
            {
                if (op == null) return;
                if (!op.isDone)
                {
                    var p = Mathf.Clamp01(request.downloadProgress);
                    EditorUtility.DisplayProgressBar(title, url, p);
                    return;
                }

                EditorApplication.update -= Tick;
                _activeRequestCount = Math.Max(0, _activeRequestCount - 1);
                if (!IsDownloading) EditorUtility.ClearProgressBar();

                try
                {
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        var body = request.downloadHandler != null ? request.downloadHandler.text : "";
                        onError?.Invoke($"GET failed ({request.responseCode}): {request.error}\n{body}");
                        return;
                    }

                    var text = request.downloadHandler != null ? request.downloadHandler.text : "";
                    var parsed = VpmJsonIO.Deserialize(text) as Dictionary<string, object>;
                    if (parsed == null)
                    {
                        onError?.Invoke("Response JSON is not an object.");
                        return;
                    }

                    onSuccess?.Invoke(parsed);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex.Message);
                }
                finally
                {
                    request.Dispose();
                }
            }

            EditorApplication.update += Tick;
        }

        private static string ExtractAccountId(Dictionary<string, object> authRoot)
        {
            // Best-effort: handle a few possible shapes.
            if (authRoot == null) return null;

            if (authRoot.TryGetValue("account_id", out var a) && a != null)
                return a.ToString();

            if (authRoot.TryGetValue("accountId", out var b) && b != null)
                return b.ToString();

            // Sometimes wrapped.
            if (authRoot.TryGetValue("data", out var d) && d is Dictionary<string, object> dd)
            {
                if (dd.TryGetValue("account_id", out var a2) && a2 != null) return a2.ToString();
                if (dd.TryGetValue("accountId", out var b2) && b2 != null) return b2.ToString();
                if (dd.TryGetValue("id", out var id) && id != null) return id.ToString();
            }

            if (authRoot.TryGetValue("id", out var i) && i != null)
                return i.ToString();

            return null;
        }
    }
}

