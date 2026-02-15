using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmmClient
    {
        private const string UploadPath = "/upload";

        internal static void UploadZipAsync(
            string baseUrl,
            string apiKey,
            string zipPath,
            int uploadChunkSizeMb,
            Action<string> onSuccess,
            Action<string> onError
        )
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

            long totalBytes;
            try
            {
                totalBytes = new FileInfo(zipPath).Length;
            }
            catch (Exception ex)
            {
                onError?.Invoke("Could not read zip size: " + ex.Message);
                return;
            }

            if (totalBytes <= 0)
            {
                onError?.Invoke("Zip is empty: " + zipPath);
                return;
            }

            var normalizedChunkSizeMb = NormalizeChunkSizeMb(uploadChunkSizeMb);
            var chunkSizeBytes = ToChunkBytes(normalizedChunkSizeMb);
            var url = baseUrl + UploadPath;

            if (totalBytes > chunkSizeBytes)
            {
                UploadZipChunkedAsync(url, apiKey, zipPath, totalBytes, chunkSizeBytes, onSuccess, onError);
                return;
            }

            UploadZipSingleAsync(url, apiKey, zipPath, onSuccess, onError);
        }

        private static int NormalizeChunkSizeMb(int chunkSizeMb)
        {
            if (chunkSizeMb <= 0)
                chunkSizeMb = VpmPackagerProjectSettings.DefaultVpmmUploadChunkSizeMb;
            return Mathf.Clamp(
                chunkSizeMb,
                VpmPackagerProjectSettings.MinVpmmUploadChunkSizeMb,
                VpmPackagerProjectSettings.MaxVpmmUploadChunkSizeMb
            );
        }

        private static long ToChunkBytes(int chunkSizeMb)
        {
            return Math.Max(1L, (long)chunkSizeMb * 1024L * 1024L);
        }

        private static void UploadZipSingleAsync(string url, string apiKey, string zipPath, Action<string> onSuccess, Action<string> onError)
        {
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
                    if (TryParseJsonObject(text, out var root, out _) && TryGetNonEmptyString(root, "id", out var jobId))
                    {
                        onSuccess?.Invoke(jobId);
                        return;
                    }

                    onSuccess?.Invoke(text);
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

        private static void UploadZipChunkedAsync(
            string url,
            string apiKey,
            string zipPath,
            long totalBytes,
            long chunkSizeBytes,
            Action<string> onSuccess,
            Action<string> onError
        )
        {
            FileStream stream = null;
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation op = null;
            long acknowledgedOffset = 0;
            long currentChunkOffset = 0;
            int currentChunkLength = 0;
            string uploadId = null;
            var finished = false;

            void Cleanup()
            {
                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();
                if (request != null)
                {
                    request.Dispose();
                    request = null;
                }
                op = null;
                if (stream != null)
                {
                    stream.Dispose();
                    stream = null;
                }
            }

            void FinishError(string error)
            {
                if (finished) return;
                finished = true;
                Cleanup();
                onError?.Invoke(error);
            }

            void FinishSuccess(string jobId)
            {
                if (finished) return;
                finished = true;
                Cleanup();
                onSuccess?.Invoke(jobId);
            }

            bool TryStartChunkRequest(long offset, string requestUploadId, out string error)
            {
                error = null;
                if (stream == null)
                {
                    error = "Zip stream is not open.";
                    return false;
                }

                if (offset < 0 || offset >= totalBytes)
                {
                    error = $"Chunk offset {offset} is out of range 0..{totalBytes - 1}.";
                    return false;
                }

                var remaining = totalBytes - offset;
                var nextChunkLenLong = Math.Min(chunkSizeBytes, remaining);
                if (nextChunkLenLong <= 0 || nextChunkLenLong > int.MaxValue)
                {
                    error = $"Invalid chunk size at offset {offset}: {nextChunkLenLong}.";
                    return false;
                }

                currentChunkLength = (int)nextChunkLenLong;
                var payload = new byte[currentChunkLength];

                stream.Seek(offset, SeekOrigin.Begin);
                var readTotal = 0;
                while (readTotal < currentChunkLength)
                {
                    var read = stream.Read(payload, readTotal, currentChunkLength - readTotal);
                    if (read <= 0) break;
                    readTotal += read;
                }
                if (readTotal != currentChunkLength)
                {
                    error = $"Failed to read zip chunk at offset {offset}. Expected {currentChunkLength} bytes, got {readTotal}.";
                    return false;
                }

                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/zip");
                request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                request.SetRequestHeader("Upload-Offset", offset.ToString());
                request.SetRequestHeader("Upload-Length", totalBytes.ToString());
                if (!string.IsNullOrWhiteSpace(requestUploadId))
                    request.SetRequestHeader("Upload-ID", requestUploadId);

                currentChunkOffset = offset;
                op = request.SendWebRequest();
                return true;
            }

            void Tick()
            {
                if (finished) return;
                if (op == null || request == null) return;

                if (!op.isDone)
                {
                    var inFlightBytes = (long)Math.Round(Mathf.Clamp01(request.uploadProgress) * currentChunkLength);
                    var uploadedBytes = currentChunkOffset + inFlightBytes;
                    var progress = totalBytes > 0
                        ? Mathf.Clamp01((float)((double)uploadedBytes / totalBytes))
                        : 1f;
                    EditorUtility.DisplayProgressBar("Uploading to VPMM", url, progress);
                    return;
                }

                var completedRequest = request;
                request = null;
                op = null;

                var body = completedRequest.downloadHandler != null ? completedRequest.downloadHandler.text : "";

                try
                {
                    if (completedRequest.result != UnityWebRequest.Result.Success)
                    {
                        FinishError($"VPMM upload failed ({completedRequest.responseCode}) at offset {currentChunkOffset}: {completedRequest.error}\n{body}");
                        return;
                    }

                    if (!TryParseJsonObject(body, out var root, out var parseError))
                    {
                        FinishError($"VPMM upload returned invalid JSON at offset {currentChunkOffset}: {parseError}\n{body}");
                        return;
                    }

                    if (TryGetNonEmptyString(root, "id", out var jobId))
                    {
                        FinishSuccess(jobId);
                        return;
                    }

                    if (!TryGetNonEmptyString(root, "uploadId", out var responseUploadId))
                    {
                        FinishError($"VPMM upload response missing uploadId at offset {currentChunkOffset}.\n{body}");
                        return;
                    }

                    if (!TryGetInt64(root, "uploadOffset", out var nextOffset))
                    {
                        FinishError($"VPMM upload response missing uploadOffset at offset {currentChunkOffset}.\n{body}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(uploadId))
                        uploadId = responseUploadId;
                    else if (!string.Equals(uploadId, responseUploadId, StringComparison.Ordinal))
                    {
                        FinishError($"VPMM uploadId changed during upload. Expected '{uploadId}', got '{responseUploadId}'.");
                        return;
                    }

                    if (nextOffset < 0 || nextOffset > totalBytes)
                    {
                        FinishError($"VPMM uploadOffset {nextOffset} is out of range 0..{totalBytes}.");
                        return;
                    }

                    if (nextOffset <= acknowledgedOffset)
                    {
                        FinishError($"VPMM uploadOffset did not advance. Previous offset {acknowledgedOffset}, next offset {nextOffset}.");
                        return;
                    }

                    acknowledgedOffset = nextOffset;
                    if (acknowledgedOffset >= totalBytes)
                    {
                        FinishError("VPMM upload finished sending all chunks but final response did not include job id.");
                        return;
                    }

                    if (!TryStartChunkRequest(acknowledgedOffset, uploadId, out var startError))
                    {
                        FinishError(startError);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    FinishError(ex.Message);
                }
                finally
                {
                    completedRequest.Dispose();
                }
            }

            try
            {
                stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                onError?.Invoke("Failed to open zip for chunked upload: " + ex.Message);
                return;
            }

            if (!TryStartChunkRequest(acknowledgedOffset, requestUploadId: null, out var firstChunkError))
            {
                stream.Dispose();
                onError?.Invoke(firstChunkError);
                return;
            }

            EditorApplication.update += Tick;
        }

        private static bool TryParseJsonObject(string text, out Dictionary<string, object> root, out string error)
        {
            root = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Response body is empty.";
                return false;
            }

            try
            {
                root = VpmJsonIO.Deserialize(text) as Dictionary<string, object>;
                if (root == null)
                {
                    error = "Response JSON is not an object.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryGetNonEmptyString(Dictionary<string, object> root, string key, out string value)
        {
            value = null;
            if (root == null || string.IsNullOrWhiteSpace(key)) return false;
            if (!root.TryGetValue(key, out var raw) || raw == null) return false;

            value = (raw as string ?? raw.ToString() ?? "").Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetInt64(Dictionary<string, object> root, string key, out long value)
        {
            value = 0;
            if (root == null || string.IsNullOrWhiteSpace(key)) return false;
            if (!root.TryGetValue(key, out var raw) || raw == null) return false;

            switch (raw)
            {
                case long l:
                    value = l;
                    return true;
                case int i:
                    value = i;
                    return true;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                    var rounded = Math.Round(d);
                    if (Math.Abs(d - rounded) > 1e-6) return false;
                    value = (long)rounded;
                    return true;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                    var roundedF = Math.Round((double)f);
                    if (Math.Abs(f - roundedF) > 1e-6) return false;
                    value = (long)roundedF;
                    return true;
                case string s:
                    return long.TryParse(s.Trim(), out value);
                default:
                    try
                    {
                        value = Convert.ToInt64(raw);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
            }
        }
    }
}

