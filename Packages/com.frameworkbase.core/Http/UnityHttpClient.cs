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

                    // UnityWebRequest reports network/protocol failures through result/error;
                    // keep that detail here and return the framework response model to callers.
                    await webRequest.SendWebRequest();

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
