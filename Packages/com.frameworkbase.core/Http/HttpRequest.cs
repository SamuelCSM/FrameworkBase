using System;
using System.Collections.Generic;

namespace Framework.Http
{
    /// <summary>
    /// Transport-agnostic HTTP request model shared by framework services.
    /// It intentionally contains only common request data so it can be backed by UnityWebRequest today
    /// and another transport later without leaking transport-specific APIs into business modules.
    /// </summary>
    public sealed class HttpRequest
    {
        /// <summary>Create a request with the specified verb and absolute or platform-supported URL.</summary>
        public HttpRequest(HttpMethod method, string url)
        {
            Method = method;
            Url = url;
        }

        /// <summary>HTTP verb to use for the request.</summary>
        public HttpMethod Method { get; }

        /// <summary>Target URL. Unity transports may also accept file/jar URLs for packaged assets.</summary>
        public string Url { get; }

        /// <summary>Optional raw request body. Null means no upload handler/body is sent.</summary>
        public byte[] Body { get; set; }

        /// <summary>Optional content type for <see cref="Body"/>, for example application/json.</summary>
        public string ContentType { get; set; }

        /// <summary>Timeout in seconds. Values below 1 are clamped by helper methods.</summary>
        public int TimeoutSeconds { get; set; } = 10;

        /// <summary>Case-insensitive request headers.</summary>
        public Dictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 响应体字节上限；0 表示不限。超限时传输会被中止并按失败返回，响应体不交给调用方。
        /// <para>
        /// 面向不可全信的远端（配置服务、遥测端点）应显式设置：默认缓冲式下载会把整个响应读进托管堆，
        /// 恶意或异常的服务端可借此耗尽内存。下载大文件请改用流式落盘的下载器，而不是调高这个上限。
        /// </para>
        /// </summary>
        public int MaxResponseBytes { get; set; }

        /// <summary>
        /// 取消令牌。触发后中止在途传输并按失败返回，用于随场景/流程退出干净收尾，
        /// 而不是把请求弃置在后台空转。
        /// </summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        /// <summary>Create a GET request.</summary>
        public static HttpRequest Get(string url)
        {
            return new HttpRequest(HttpMethod.Get, url);
        }

        /// <summary>Create a POST request with a raw body and content type.</summary>
        public static HttpRequest Post(string url, byte[] body, string contentType)
        {
            return new HttpRequest(HttpMethod.Post, url)
            {
                Body = body,
                ContentType = contentType
            };
        }

        /// <summary>Set timeout in seconds and return the same request for fluent construction.</summary>
        public HttpRequest WithTimeout(int timeoutSeconds)
        {
            TimeoutSeconds = Math.Max(1, timeoutSeconds);
            return this;
        }

        /// <summary>Add or replace a request header and return the same request for fluent construction.</summary>
        public HttpRequest WithHeader(string name, string value)
        {
            if (!string.IsNullOrEmpty(name))
                Headers[name] = value ?? string.Empty;
            return this;
        }

        /// <summary>设置响应体字节上限并返回自身，便于链式构造。负值按不限处理。</summary>
        /// <param name="maxBytes">上限字节数；0 或负值表示不限。</param>
        /// <returns>同一请求实例。</returns>
        public HttpRequest WithMaxResponseBytes(int maxBytes)
        {
            MaxResponseBytes = Math.Max(0, maxBytes);
            return this;
        }

        /// <summary>设置取消令牌并返回自身，便于链式构造。</summary>
        /// <param name="token">取消令牌。</param>
        /// <returns>同一请求实例。</returns>
        public HttpRequest WithCancellation(System.Threading.CancellationToken token)
        {
            CancellationToken = token;
            return this;
        }
    }
}
