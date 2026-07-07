using UnityEngine.Networking;

namespace Framework.Http
{
    /// <summary>
    /// URL helpers owned by the HTTP layer so callers do not need to reference UnityWebRequest directly.
    /// </summary>
    public static class HttpUrl
    {
        /// <summary>Escape one query-string value using Unity's platform-compatible URL escaping.</summary>
        public static string EscapeQueryValue(string value)
        {
            return UnityWebRequest.EscapeURL(value ?? string.Empty);
        }
    }
}
