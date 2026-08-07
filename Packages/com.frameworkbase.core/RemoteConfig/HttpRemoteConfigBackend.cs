using System;
using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Http;
using Framework.Serialization;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 客户端属性随请求下发的方式。决定 <c>device_id</c> / <c>user_id</c> 这类标识出现在哪里。
    /// </summary>
    public enum RemoteConfigIdentityTransport
    {
        /// <summary>
        /// 查询参数（默认）。CDN、反向代理、WAF 与服务端访问日志通常整条 URL 落盘，
        /// 标识会散布到这些日志里且保留期不由客户端控制（CWE-598）。
        /// 端点是静态 CDN 文件、或服务端只会读 query 时用它。
        /// </summary>
        QueryString = 0,

        /// <summary>
        /// POST JSON body。标识不进 URL，因而不落进上述访问日志。
        /// 需要配置服务支持 POST 并从 body 读取定向属性。
        /// </summary>
        PostBody = 1,
    }

    /// <summary>
    /// 通用 HTTP 远程配置后端：拉取一份 JSON 对象文本。
    /// 端点可以是配置服务，也可以是 CDN 静态文件（此时客户端属性被忽略，定向逻辑走客户端开关字段）。
    /// 任何网络错误/非 2xx 折算为 null（由管理器保留现值），不抛异常。
    /// </summary>
    public class HttpRemoteConfigBackend : IRemoteConfigBackend
    {
        /// <summary>
        /// 配置载荷字节上限。远端配置是不可全信的输入，缓冲式下载会把整个响应读进托管堆；
        /// 1 MiB 远超正常配置体量（通常几 KB），超过即判为异常并沿用磁盘缓存与代码默认值。
        /// </summary>
        private const int MaxPayloadBytes = 1024 * 1024;

        private readonly string _endpointUrl;
        private readonly int _timeoutSeconds;
        private readonly RemoteConfigIdentityTransport _identityTransport;

        public string Name => $"http({_endpointUrl})";

        /// <param name="endpointUrl">配置端点（AppConfig.RemoteConfigUrl）。</param>
        /// <param name="timeoutSeconds">单次请求超时。</param>
        /// <param name="identityTransport">
        /// 客户端属性的下发方式。默认沿用查询参数以兼容既有端点与静态 CDN；
        /// 配置服务支持 POST 时应改用 <see cref="RemoteConfigIdentityTransport.PostBody"/>，
        /// 使设备与用户标识不再进入 URL 与各级访问日志。
        /// </param>
        public HttpRemoteConfigBackend(
            string endpointUrl,
            int timeoutSeconds = 10,
            RemoteConfigIdentityTransport identityTransport = RemoteConfigIdentityTransport.QueryString)
        {
            if (string.IsNullOrEmpty(endpointUrl))
                throw new ArgumentException("配置端点为空", nameof(endpointUrl));
            _endpointUrl = endpointUrl;
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
            _identityTransport = identityTransport;
        }

        public async UniTask<string> FetchAsync(RemoteConfigRequest request)
        {
            HttpRequest httpRequest = _identityTransport == RemoteConfigIdentityTransport.PostBody
                ? HttpRequest.Post(_endpointUrl, Encoding.UTF8.GetBytes(BuildBody(request)), "application/json")
                : HttpRequest.Get(BuildUrl(request));

            HttpResponse response = await HttpClients.Shared.SendAsync(
                httpRequest
                    .WithTimeout(_timeoutSeconds)
                    .WithMaxResponseBytes(MaxPayloadBytes));

            if (!response.Succeeded)
            {
                GameLog.Warning($"[HttpRemoteConfigBackend] 拉取失败 code={response.StatusCode} err={response.Error}");
                return null;
            }

            return response.Text;
        }

        /// <summary>把客户端属性拼为 JSON body（空属性省略，与查询参数口径一致）。</summary>
        /// <param name="request">本次拉取的客户端属性。</param>
        /// <returns>请求体 JSON 文本。</returns>
        internal static string BuildBody(RemoteConfigRequest request)
        {
            var fields = new System.Collections.Generic.Dictionary<string, object>();
            AddField(fields, "device_id", request.DeviceId);
            AddField(fields, "user_id", request.UserId);
            AddField(fields, "app_version", request.AppVersion);
            AddField(fields, "channel", request.Channel);
            AddField(fields, "env", request.Env);
            return JsonWriter.SerializeObject(fields);
        }

        private static void AddField(
            System.Collections.Generic.Dictionary<string, object> fields,
            string key,
            string value)
        {
            if (!string.IsNullOrEmpty(value))
                fields[key] = value;
        }

        /// <summary>把客户端属性拼为查询参数（供服务端条件定向；空属性省略）。</summary>
        /// <param name="request">本次拉取的客户端属性。</param>
        /// <returns>带查询参数的完整 URL。</returns>
        internal string BuildUrl(RemoteConfigRequest request)
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
