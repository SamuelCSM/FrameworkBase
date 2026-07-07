namespace Framework.Serialization
{
    /// <summary>
    /// Global JSON serializer registry for framework modules.
    /// Tests or host applications can replace <see cref="Shared"/> when a different serializer is required.
    /// </summary>
    public static class JsonSerializers
    {
        private static IJsonSerializer _shared;

        /// <summary>Default strongly typed serializer. Lazily creates <see cref="UnityJsonSerializer"/>.</summary>
        public static IJsonSerializer Shared
        {
            get
            {
                if (_shared == null)
                    _shared = new UnityJsonSerializer();
                return _shared;
            }
            set => _shared = value ?? new UnityJsonSerializer();
        }
    }
}
