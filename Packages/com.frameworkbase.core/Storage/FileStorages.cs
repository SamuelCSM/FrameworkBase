namespace Framework.Storage
{
    /// <summary>
    /// Global file storage registry for framework modules.
    /// Tests and host applications can replace <see cref="Shared"/> to redirect IO or add diagnostics.
    /// </summary>
    public static class FileStorages
    {
        private static IFileStorage _shared;

        /// <summary>Default shared storage. Lazily creates <see cref="LocalFileStorage"/>.</summary>
        public static IFileStorage Shared
        {
            get
            {
                if (_shared == null)
                    _shared = new LocalFileStorage();
                return _shared;
            }
            set => _shared = value ?? new LocalFileStorage();
        }
    }
}
