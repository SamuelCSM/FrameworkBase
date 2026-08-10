using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace Framework.Http
{
    /// <summary>
    /// UnityWebRequest-backed implementation of <see cref="IHttpClient"/>.
    /// This is the default runtime transport for Unity player/editor code.
    /// </summary>
    public sealed class UnityHttpClient : IHttpClient
    {
        /// <inheritdoc />
        public async UniTask<HttpResponse> SendAsync(HttpRequest request)
        {
            if (request == null)
                return HttpResponse.Failed("Request is null.");
            if (string.IsNullOrWhiteSpace(request.Url))
                return HttpResponse.Failed("Request url is empty.");
            // 已取消就不必开工：小响应可能在一帧内读完，靠轮询判断会漏掉这种情况。
            if (request.CancellationToken.IsCancellationRequested)
                return HttpResponse.Failed("Request was canceled.");

            try
            {
                using (var webRequest = new UnityWebRequest(request.Url, ToUnityMethod(request.Method)))
                {
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    webRequest.timeout = Math.Max(1, request.TimeoutSeconds);

                    if (request.Body != null)
                    {
                        webRequest.uploadHandler = new UploadHandlerRaw(request.Body);
                        if (!string.IsNullOrEmpty(request.ContentType))
                            webRequest.SetRequestHeader("Content-Type", request.ContentType);
                    }

                    foreach (KeyValuePair<string, string> header in request.Headers)
                    {
                        if (!string.IsNullOrEmpty(header.Key))
                            webRequest.SetRequestHeader(header.Key, header.Value ?? string.Empty);
                    }

                    // 逐帧轮询而不是直接 await：取消与响应体上限都要在传输过程中生效，
                    // 等整个响应读完再判断，内存已经被占掉了，防的就是这一段。
                    UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (request.CancellationToken.IsCancellationRequested)
                        {
                            webRequest.Abort();
                            return HttpResponse.Failed("Request was canceled.");
                        }

                        if (ExceedsLimit(request.MaxResponseBytes, webRequest.downloadedBytes))
                        {
                            webRequest.Abort();
                            return HttpResponse.Failed(
                                $"Response exceeded the {request.MaxResponseBytes}-byte limit.");
                        }

                        await UniTask.Yield();
                    }

                    // 完成后再判一次：小响应可能在一帧内读完，循环里根本来不及看到超限。
                    if (ExceedsLimit(request.MaxResponseBytes, webRequest.downloadedBytes))
                    {
                        return HttpResponse.Failed(
                            $"Response exceeded the {request.MaxResponseBytes}-byte limit.");
                    }

                    string error = webRequest.result == UnityWebRequest.Result.Success
                        ? null
                        : webRequest.error;

                    Dictionary<string, string> headers = webRequest.GetResponseHeaders();
                    return new HttpResponse(
                        webRequest.responseCode,
                        error,
                        webRequest.downloadHandler != null ? webRequest.downloadHandler.data : null,
                        headers);
                }
            }
            catch (Exception ex)
            {
                return HttpResponse.Failed(ex.Message);
            }
        }

        /// <summary>已下载字节是否超出上限。上限为 0 表示不限，恒返回 false。</summary>
        /// <param name="maxResponseBytes">配置的上限字节数。</param>
        /// <param name="downloadedBytes">当前已下载字节数。</param>
        /// <returns>超限返回 true。</returns>
        private static bool ExceedsLimit(int maxResponseBytes, ulong downloadedBytes)
            => maxResponseBytes > 0 && downloadedBytes > (ulong)maxResponseBytes;

        private static string ToUnityMethod(HttpMethod method)
        {
            switch (method)
            {
                case HttpMethod.Get: return UnityWebRequest.kHttpVerbGET;
                case HttpMethod.Post: return UnityWebRequest.kHttpVerbPOST;
                case HttpMethod.Put: return UnityWebRequest.kHttpVerbPUT;
                case HttpMethod.Delete: return UnityWebRequest.kHttpVerbDELETE;
                case HttpMethod.Head: return UnityWebRequest.kHttpVerbHEAD;
                case HttpMethod.Patch: return "PATCH";
                default: return UnityWebRequest.kHttpVerbGET;
            }
        }
    }
}
