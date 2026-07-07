using Cysharp.Threading.Tasks;

namespace Framework.Http
{
    /// <summary>
    /// Minimal HTTP transport abstraction used by framework modules.
    /// Implementations should convert transport exceptions into <see cref="HttpResponse.Error"/>
    /// so callers can handle network failures without try/catch at every call site.
    /// </summary>
    public interface IHttpClient
    {
        /// <summary>
        /// Sends one request and returns a normalized response. This method should not throw for normal
        /// network/HTTP failures; those are represented by the returned response.
        /// </summary>
        UniTask<HttpResponse> SendAsync(HttpRequest request);
    }
}
