using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace Framework.Analytics
{
    /// <summary>
    /// 通用 HTTP JSON 埋点后端：把一批事件按 JSON 数组 POST 到自建采集端点。
    /// 2xx 视为成功；任何网络错误/非 2xx 折算为失败（由管理器退避重试），不抛异常。
    /// </summary>
    public class HttpJsonAnalyticsBackend : IAnalyticsBackend
    {
        private readonly string _endpointUrl;
        private readonly int _timeoutSeconds;

        public string Name => $"http_json({_endpointUrl})";

        /// <param name="endpointUrl">采集端点（AppConfig.AnalyticsUrl）。</param>
        /// <param name="timeoutSeconds">单次请求超时。</param>
        public HttpJsonAnalyticsBackend(string endpointUrl, int timeoutSeconds = 10)
        {
            if (string.IsNullOrEmpty(endpointUrl))
                throw new ArgumentException("采集端点为空", nameof(endpointUrl));
            _endpointUrl = endpointUrl;
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public async UniTask<bool> SendAsync(IReadOnlyList<string> eventJsonBatch)
        {
            if (eventJsonBatch == null || eventJsonBatch.Count == 0)
                return true;

            // 事件已是 JSON 对象文本，拼数组即可，不重复序列化
            var body = new StringBuilder(eventJsonBatch.Count * 128);
            body.Append('[');
            for (int i = 0; i < eventJsonBatch.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append(eventJsonBatch[i]);
            }
            body.Append(']');

            try
            {
                using (var request = new UnityWebRequest(_endpointUrl, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString()));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = _timeoutSeconds;

                    await request.SendWebRequest();

                    bool ok = request.result == UnityWebRequest.Result.Success;
                    if (!ok)
                        GameLog.Warning($"[HttpJsonAnalyticsBackend] 上报失败 code={request.responseCode} err={request.error}");
                    return ok;
                }
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[HttpJsonAnalyticsBackend] 上报异常: {ex.Message}");
                return false;
            }
        }
    }
}
