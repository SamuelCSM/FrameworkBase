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
    }
}
