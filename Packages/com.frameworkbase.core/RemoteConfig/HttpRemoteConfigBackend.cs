using System;
using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Http;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 通用 HTTP 远程配置后端：GET 拉取一份 JSON 对象文本。
    /// 端点可以是配置服务，也可以是 CDN 静态文件（此时查询参数被忽略，定向逻辑走客户端开关字段）。
    /// 任何网络错误/非 2xx 折算为 null（由管理器保留现值），不抛异常。
    /// </summary>
    public class HttpRemoteConfigBackend : IRemoteConfigBackend
    {
        private readonly string _endpointUrl;
        private readonly int _timeoutSeconds;

        public string Name => $"http({_endpointUrl})";

        /// <param name="endpointUrl">配置端点（AppConfig.RemoteConfigUrl）。</param>
        /// <param name="timeoutSeconds">单次请求超时。</param>
        public HttpRemoteConfigBackend(string endpointUrl, int timeoutSeconds = 10)
        {
            if (string.IsNullOrEmpty(endpointUrl))
                throw new ArgumentException("配置端点为空", nameof(endpointUrl));
            _endpointUrl = endpointUrl;
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public async UniTask<string> FetchAsync(RemoteConfigRequest request)
        {
            string url = BuildUrl(request);
            HttpResponse response = await HttpClients.Shared.SendAsync(
                HttpRequest.Get(url).WithTimeout(_timeoutSeconds));

            if (!response.Succeeded)
            {
                GameLog.Warning($"[HttpRemoteConfigBackend] 拉取失败 code={response.StatusCode} err={response.Error}");
                return null;
            }

            return response.Text;
        }

        /// <summary>把客户端属性拼为查询参数（供服务端条件定向；空属性省略）。</summary>
        private string BuildUrl(RemoteConfigRequest request)
        {
            var sb = new StringBuilder(_endpointUrl);
            bool hasQuery = _endpointUrl.IndexOf('?') >= 0;

            AppendParam(sb, ref hasQuery, "device_id", request.DeviceId);
            AppendParam(sb, ref hasQuery, "user_id", request.UserId);
            AppendParam(sb, ref hasQuery, "app_version", request.AppVersion);
            AppendParam(sb, ref hasQuery, "channel", request.Channel);
            AppendParam(sb, ref hasQuery, "env", request.Env);
            return sb.ToString();
        }

        private static void AppendParam(StringBuilder sb, ref bool hasQuery, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            sb.Append(hasQuery ? '&' : '?');
            hasQuery = true;
            sb.Append(key).Append('=').Append(HttpUrl.EscapeQueryValue(value));
        }
    }
}
