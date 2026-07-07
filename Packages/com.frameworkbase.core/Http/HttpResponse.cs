using System;
using System.Collections.Generic;
using System.Text;

namespace Framework.Http
{
    /// <summary>
    /// Normalized HTTP response returned by <see cref="IHttpClient"/>.
    /// Network failures and transport exceptions are represented as non-success responses instead of thrown exceptions.
    /// </summary>
    public sealed class HttpResponse
    {
        /// <summary>Create a response from transport data.</summary>
        public HttpResponse(
            long statusCode,
            string error,
            byte[] data,
            Dictionary<string, string> headers = null)
        {
            StatusCode = statusCode;
            Error = error;
            Data = data ?? Array.Empty<byte>();
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>HTTP status code. Local file/jar transports may report 0 even when the operation succeeded.</summary>
        public long StatusCode { get; }

        /// <summary>Transport error text. Null or empty means no transport-level error was reported.</summary>
        public string Error { get; }

        /// <summary>Raw response body. Empty array when there is no body.</summary>
        public byte[] Data { get; }

        /// <summary>Response headers, case-insensitive where provided by the transport.</summary>
        public Dictionary<string, string> Headers { get; }

        /// <summary>True for standard HTTP 2xx status codes.</summary>
        public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode < 300;

        /// <summary>
        /// True when the request had no transport error and either returned HTTP 2xx or a local transport status code of 0.
        /// </summary>
        public bool Succeeded => string.IsNullOrEmpty(Error) && (StatusCode == 0 || IsSuccessStatusCode);

        /// <summary>Response body decoded as UTF-8 text.</summary>
        public string Text
        {
            get
            {
                if (Data == null || Data.Length == 0)
                    return string.Empty;
                return Encoding.UTF8.GetString(Data);
            }
        }

        /// <summary>Create a failed response for validation errors or caught transport exceptions.</summary>
        public static HttpResponse Failed(string error)
        {
            return new HttpResponse(0, error, Array.Empty<byte>());
        }
    }
}
