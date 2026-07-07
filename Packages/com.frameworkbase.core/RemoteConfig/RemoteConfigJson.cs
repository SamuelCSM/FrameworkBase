using System.Collections.Generic;
using Framework.Serialization;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// Remote config JSON compatibility entry. The shared parser lives in <see cref="JsonObjectParser"/>.
    /// </summary>
    public static class RemoteConfigJson
    {
        /// <summary>Parse a top-level JSON object. Invalid input returns false and does not throw.</summary>
        public static bool TryParseObject(string json, out Dictionary<string, object> result)
        {
            return JsonObjectParser.TryParseObject(json, out result);
        }
    }
}
