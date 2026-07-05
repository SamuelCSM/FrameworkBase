using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 运行时访问 AppConfigAsset 的入口。
    /// </summary>
    public static class AppConfig
    {
        private const string ResourcesPath = "AppConfig";
        private static AppConfigAsset _cached;

        /// <summary>加载 Resources/AppConfig.asset；缺失时使用内存默认值。</summary>
        public static AppConfigAsset Load()
        {
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<AppConfigAsset>(ResourcesPath);
            if (_cached != null)
            {
                Logger.Log($"[AppConfig] 已加载 ScriptableObject Env={_cached.AppEnv} " +
                           $"UpdateServerUrl={_cached.UpdateServerUrl} " +
                           $"GS={_cached.GameServerHost}:{_cached.GameServerPort} " +
                           $"UseNetworkLogin={_cached.UseNetworkLogin}");
                return _cached;
            }

            Logger.Warning("[AppConfig] 未找到 Resources/AppConfig.asset，使用运行时默认配置");
            _cached = ScriptableObject.CreateInstance<AppConfigAsset>();
            return _cached;
        }

        /// <summary>清除缓存（Editor 改配置后测试用）。</summary>
        public static void ClearCache()
        {
            _cached = null;
        }
    }
}
