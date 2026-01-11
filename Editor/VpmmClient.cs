using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmmClient
    {
        [Serializable]
        private sealed class JobIdentifier
        {
            public string id;
        }

        internal static void UploadZipAsync(string baseUrl, string apiKey, string zipPath, Action<string> onSuccess, Action<string> onError)
        {
            baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            apiKey = (apiKey ?? "").Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                onError?.Invoke("VPMM base URL is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("API key is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(zipPath))
            {
                onError?.Invoke("zipPath is empty.");
                return;
            }

            if (!File.Exists(zipPath))
            {
                onError?.Invoke("Zip does not exist: " + zipPath);
                return;
            }

            var url = baseUrl + "/upload";

            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerFile(zipPath);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/zip");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);

            var op = request.SendWebRequest();

            void Tick()
            {
                if (op == null) return;
                if (!op.isDone)
                {
                    var p = Mathf.Clamp01(request.uploadProgress);
                    EditorUtility.DisplayProgressBar("Uploading to VPMM", url, p);
                    return;
                }

                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();

                try
                {
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        var body = request.downloadHandler != null ? request.downloadHandler.text : "";
                        onError?.Invoke($"VPMM upload failed ({request.responseCode}): {request.error}\n{body}");
                        return;
                    }

                    var text = request.downloadHandler != null ? request.downloadHandler.text : "";
                    var job = JsonUtility.FromJson<JobIdentifier>(text);
                    var jobId = job != null ? job.id : null;
                    onSuccess?.Invoke(string.IsNullOrWhiteSpace(jobId) ? text : jobId);
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
    }
}

