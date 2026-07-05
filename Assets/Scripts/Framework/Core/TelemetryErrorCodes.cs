namespace Framework.Core
{
    /// <summary>
    /// 客户端统一错误码常量。
    /// 先稳定客户端码，再把后续服务端错误码映射到同一聚合码。
    /// </summary>
    public static class TelemetryErrorCodes
    {
        public static class Launch
        {
            public const string Ok = "LF_OK";
            public const string FullUpdateGateBlocked = "LF_FULL_UPDATE_GATE_BLOCKED";
            public const string ResourceDownloadFailed = "LF_RESOURCE_DOWNLOAD_FAILED";
            public const string CodeDownloadFailed = "LF_CODE_DOWNLOAD_FAILED";
            public const string MetadataLoadFailed = "LF_METADATA_LOAD_FAILED";
            public const string HotUpdateAssemblyLoadFailed = "LF_HOTUPDATE_ASSEMBLY_LOAD_FAILED";
            public const string UnhandledException = "LF_UNHANDLED_EXCEPTION";
        }

        public static class Auth
        {
            public const string LoginCancelled = "AUTH_LOGIN_CANCELLED";
            public const string LoginTimeout = "AUTH_LOGIN_TIMEOUT";
            public const string TokenExpired = "AUTH_TOKEN_EXPIRED";
            public const string NetworkOffline = "AUTH_NETWORK_OFFLINE";
            public const string InvalidCredential = "AUTH_INVALID_CREDENTIAL";
            public const string LoginInProgress = "AUTH_LOGIN_IN_PROGRESS";
            public const string Unknown = "AUTH_UNKNOWN";
        }

        public static class UI
        {
            public const string DuplicateOpenBlocked = "UI_DUPLICATE_OPEN_BLOCKED";
            public const string AsyncCancelled = "UI_ASYNC_CANCELLED";
            public const string RaceConditionGuarded = "UI_RACE_CONDITION_GUARDED";
        }
    }
}
