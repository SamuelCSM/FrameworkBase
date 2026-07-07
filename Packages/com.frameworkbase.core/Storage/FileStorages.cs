namespace Framework.Storage
{
    /// <summary>
    /// 框架模块使用的全局文件存储入口。
    /// 测试或宿主应用可以替换 <see cref="Shared"/>，用于重定向 IO 或增加诊断能力。
    /// </summary>
    public static class FileStorages
    {
        private static IFileStorage _shared;

        /// <summary>默认共享存储实例，按需创建 <see cref="LocalFileStorage"/>。</summary>
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
