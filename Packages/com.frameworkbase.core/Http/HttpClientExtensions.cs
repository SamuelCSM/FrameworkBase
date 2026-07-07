using System.Text;
using Cysharp.Threading.Tasks;

namespace Framework.Http
{
    /// <summary>
    /// Convenience helpers for common text and byte request patterns.
    /// Lower-level callers can use <see cref="IHttpClient.SendAsync"/> when they need status codes or headers.
    /// </summary>
    public static class HttpClientExtensions
    {
        /// <summary>GET a UTF-8 text body. Returns null when the response is not successful.</summary>
        public static async UniTask<string> GetTextAsync(
            this IHttpClient client,
            string url,
            int timeoutSeconds = 10)
        {
            HttpResponse response = await client.SendAsync(HttpRequest.Get(url).WithTimeout(timeoutSeconds));
            return response.Succeeded ? response.Text : null;
        }

        /// <summary>GET a raw byte body. Returns null when the response is not successful.</summary>
        public static async UniTask<byte[]> GetBytesAsync(
            this IHttpClient client,
            string url,
            int timeoutSeconds = 10)
        {
            HttpResponse response = await client.SendAsync(HttpRequest.Get(url).WithTimeout(timeoutSeconds));
            return response.Succeeded ? response.Data : null;
        }

        /// <summary>POST a UTF-8 text body and return the full normalized response.</summary>
        public static UniTask<HttpResponse> PostTextAsync(
            this IHttpClient client,
            string url,
            string body,
            string contentType,
            int timeoutSeconds = 10)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            HttpRequest request = HttpRequest.Post(url, bytes, contentType).WithTimeout(timeoutSeconds);
            return client.SendAsync(request);
        }
    }
}
