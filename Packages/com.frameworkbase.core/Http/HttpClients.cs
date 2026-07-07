namespace Framework.Http
{
    /// <summary>
    /// Global HTTP client registry for framework modules.
    /// Tests or host applications can replace <see cref="Shared"/> to inject mocks, tracing, auth, or platform-specific transports.
    /// </summary>
    public static class HttpClients
    {
        private static IHttpClient _shared;

        /// <summary>Default shared client. Lazily creates a <see cref="UnityHttpClient"/> when not overridden.</summary>
        public static IHttpClient Shared
        {
            get
            {
                if (_shared == null)
                    _shared = new UnityHttpClient();
                return _shared;
            }
            set => _shared = value ?? new UnityHttpClient();
        }
    }
}
