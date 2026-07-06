namespace Framework
{
    /// <summary>
    /// 事件ID定义
    /// 统一管理所有事件ID，避免硬编码和ID冲突
    /// </summary>
    public static class EventDefine
    {
        // ==================== 网络事件 (1000-1999) ====================
        
        /// <summary>
        /// 网络连接成功
        /// </summary>
        public const int NetworkConnected = 1001;

        /// <summary>
        /// 网络断开连接
        /// </summary>
        public const int NetworkDisconnected = 1002;

        /// <summary>
        /// 网络连接错误
        /// 参数：string errorMessage
        /// </summary>
        public const int NetworkError = 1003;

        /// <summary>
        /// 网络重连开始
        /// </summary>
        public const int NetworkReconnecting = 1004;

        /// <summary>
        /// 网络重连成功
        /// </summary>
        public const int NetworkReconnected = 1005;

        /// <summary>
        /// 网络重连失败
        /// </summary>
        public const int NetworkReconnectFailed = 1006;

        /// <summary>
        /// 收到服务器消息
        /// 参数：ushort msgId, byte[] data
        /// </summary>
        public const int NetworkMessageReceived = 1007;

        // ==================== UI事件 (2000-2999) ====================

        /// <summary>
        /// UI打开
        /// 参数：string uiName
        /// </summary>
        public const int UIOpened = 2001;

        /// <summary>
        /// UI关闭
        /// 参数：string uiName
        /// </summary>
        public const int UIClosed = 2002;

        /// <summary>
        /// UI显示
        /// 参数：string uiName
        /// </summary>
        public const int UIShown = 2003;

        /// <summary>
        /// UI隐藏
        /// 参数：string uiName
        /// </summary>
        public const int UIHidden = 2004;

        /// <summary>
        /// UI加载开始
        /// 参数：string uiName
        /// </summary>
        public const int UILoadStart = 2005;

        /// <summary>
        /// UI加载完成
        /// 参数：string uiName
        /// </summary>
        public const int UILoadComplete = 2006;

        /// <summary>
        /// UI加载失败
        /// 参数：string uiName, string error
        /// </summary>
        public const int UILoadFailed = 2007;

        // ==================== 场景事件 (3000-3999) ====================

        /// <summary>
        /// 场景加载开始
        /// 参数：string sceneName
        /// </summary>
        public const int SceneLoadStart = 3001;

        /// <summary>
        /// 场景加载进度
        /// 参数：string sceneName, float progress
        /// </summary>
        public const int SceneLoadProgress = 3002;

        /// <summary>
        /// 场景加载完成
        /// 参数：string sceneName
        /// </summary>
        public const int SceneLoadComplete = 3003;

        /// <summary>
        /// 场景卸载开始
        /// 参数：string sceneName
        /// </summary>
        public const int SceneUnloadStart = 3004;

        /// <summary>
        /// 场景卸载完成
        /// 参数：string sceneName
        /// </summary>
        public const int SceneUnloadComplete = 3005;

        // ==================== 资源事件 (4000-4999) ====================

        /// <summary>
        /// 资源加载开始
        /// 参数：string address
        /// </summary>
        public const int ResourceLoadStart = 4001;

        /// <summary>
        /// 资源加载完成
        /// 参数：string address
        /// </summary>
        public const int ResourceLoadComplete = 4002;

        /// <summary>
        /// 资源加载失败
        /// 参数：string address, string error
        /// </summary>
        public const int ResourceLoadFailed = 4003;

        /// <summary>
        /// 资源释放
        /// 参数：string address
        /// </summary>
        public const int ResourceReleased = 4004;

        /// <summary>
        /// 资源下载开始
        /// 参数：string label
        /// </summary>
        public const int ResourceDownloadStart = 4005;

        /// <summary>
        /// 资源下载进度
        /// 参数：string label, float progress
        /// </summary>
        public const int ResourceDownloadProgress = 4006;

        /// <summary>
        /// 资源下载完成
        /// 参数：string label
        /// </summary>
        public const int ResourceDownloadComplete = 4007;

        // ==================== 热更新事件 (5000-5999) ====================

        /// <summary>
        /// 检查更新开始
        /// </summary>
        public const int HotUpdateCheckStart = 5001;

        /// <summary>
        /// 发现新版本
        /// 参数：string version
        /// </summary>
        public const int HotUpdateNewVersionFound = 5002;

        /// <summary>
        /// 已是最新版本
        /// </summary>
        public const int HotUpdateAlreadyLatest = 5003;

        /// <summary>
        /// 下载补丁开始
        /// </summary>
        public const int HotUpdateDownloadStart = 5004;

        /// <summary>
        /// 下载补丁进度
        /// 参数：float progress
        /// </summary>
        public const int HotUpdateDownloadProgress = 5005;

        /// <summary>
        /// 下载补丁完成
        /// </summary>
        public const int HotUpdateDownloadComplete = 5006;

        /// <summary>
        /// 更新失败
        /// 参数：string error
        /// </summary>
        public const int HotUpdateFailed = 5007;

        /// <summary>
        /// 更新完成
        /// </summary>
        public const int HotUpdateComplete = 5008;

        // ==================== 游戏逻辑事件 (10000+) ====================

        /// <summary>
        /// 玩家登录成功
        /// </summary>
        public const int PlayerLoginSuccess = 10001;

        /// <summary>
        /// 玩家登录失败
        /// 参数：string error
        /// </summary>
        public const int PlayerLoginFailed = 10002;

        /// <summary>
        /// 玩家登出
        /// </summary>
        public const int PlayerLogout = 10003;

        /// <summary>
        /// 玩家等级提升
        /// 参数：int oldLevel, int newLevel
        /// </summary>
        public const int PlayerLevelUp = 10004;

        /// <summary>
        /// 玩家经验变化
        /// 参数：long exp
        /// </summary>
        public const int PlayerExpChanged = 10005;

        /// <summary>
        /// 玩家金币变化
        /// 参数：int gold
        /// </summary>
        public const int PlayerGoldChanged = 10006;

        /// <summary>
        /// 玩家钻石变化
        /// 参数：int diamond
        /// </summary>
        public const int PlayerDiamondChanged = 10007;

        /// <summary>
        /// 获得物品
        /// 参数：int itemId, int count
        /// </summary>
        public const int ItemObtained = 10008;

        /// <summary>
        /// 使用物品
        /// 参数：int itemId, int count
        /// </summary>
        public const int ItemUsed = 10009;

        /// <summary>
        /// 物品数量变化
        /// 参数：int itemId, int count
        /// </summary>
        public const int ItemCountChanged = 10010;

        // ==================== 音频事件 (6000-6999) ====================

        /// <summary>
        /// 背景音乐开始播放
        /// 参数：string musicName
        /// </summary>
        public const int MusicStarted = 6001;

        /// <summary>
        /// 背景音乐停止
        /// </summary>
        public const int MusicStopped = 6002;

        /// <summary>
        /// 音效播放
        /// 参数：string soundName
        /// </summary>
        public const int SoundPlayed = 6003;

        /// <summary>
        /// 音量变化
        /// 参数：float volume
        /// </summary>
        public const int VolumeChanged = 6004;

        // ==================== 系统事件 (9000-9999) ====================

        /// <summary>
        /// 应用暂停
        /// 参数：bool paused
        /// </summary>
        public const int ApplicationPause = 9001;

        /// <summary>
        /// 应用获得焦点
        /// 参数：bool focused
        /// </summary>
        public const int ApplicationFocus = 9002;

        /// <summary>
        /// 应用退出
        /// </summary>
        public const int ApplicationQuit = 9003;

        /// <summary>
        /// 内存警告
        /// </summary>
        public const int LowMemoryWarning = 9004;

        /// <summary>
        /// 语言切换
        /// 参数：string language
        /// </summary>
        public const int LanguageChanged = 9005;

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 获取事件名称（用于调试）
        /// </summary>
        /// <param name="eventId">事件ID</param>
        /// <returns>事件名称</returns>
        public static string GetEventName(int eventId)
        {
            // 网络事件
            if (eventId == NetworkConnected) return "NetworkConnected";
            if (eventId == NetworkDisconnected) return "NetworkDisconnected";
            if (eventId == NetworkError) return "NetworkError";
            if (eventId == NetworkReconnecting) return "NetworkReconnecting";
            if (eventId == NetworkReconnected) return "NetworkReconnected";
            if (eventId == NetworkReconnectFailed) return "NetworkReconnectFailed";
            if (eventId == NetworkMessageReceived) return "NetworkMessageReceived";

            // UI事件
            if (eventId == UIOpened) return "UIOpened";
            if (eventId == UIClosed) return "UIClosed";
            if (eventId == UIShown) return "UIShown";
            if (eventId == UIHidden) return "UIHidden";
            if (eventId == UILoadStart) return "UILoadStart";
            if (eventId == UILoadComplete) return "UILoadComplete";
            if (eventId == UILoadFailed) return "UILoadFailed";

            // 场景事件
            if (eventId == SceneLoadStart) return "SceneLoadStart";
            if (eventId == SceneLoadProgress) return "SceneLoadProgress";
            if (eventId == SceneLoadComplete) return "SceneLoadComplete";
            if (eventId == SceneUnloadStart) return "SceneUnloadStart";
            if (eventId == SceneUnloadComplete) return "SceneUnloadComplete";

            // 资源事件
            if (eventId == ResourceLoadStart) return "ResourceLoadStart";
            if (eventId == ResourceLoadComplete) return "ResourceLoadComplete";
            if (eventId == ResourceLoadFailed) return "ResourceLoadFailed";
            if (eventId == ResourceReleased) return "ResourceReleased";
            if (eventId == ResourceDownloadStart) return "ResourceDownloadStart";
            if (eventId == ResourceDownloadProgress) return "ResourceDownloadProgress";
            if (eventId == ResourceDownloadComplete) return "ResourceDownloadComplete";

            // 热更新事件
            if (eventId == HotUpdateCheckStart) return "HotUpdateCheckStart";
            if (eventId == HotUpdateNewVersionFound) return "HotUpdateNewVersionFound";
            if (eventId == HotUpdateAlreadyLatest) return "HotUpdateAlreadyLatest";
            if (eventId == HotUpdateDownloadStart) return "HotUpdateDownloadStart";
            if (eventId == HotUpdateDownloadProgress) return "HotUpdateDownloadProgress";
            if (eventId == HotUpdateDownloadComplete) return "HotUpdateDownloadComplete";
            if (eventId == HotUpdateFailed) return "HotUpdateFailed";
            if (eventId == HotUpdateComplete) return "HotUpdateComplete";

            // 游戏逻辑事件
            if (eventId == PlayerLoginSuccess) return "PlayerLoginSuccess";
            if (eventId == PlayerLoginFailed) return "PlayerLoginFailed";
            if (eventId == PlayerLogout) return "PlayerLogout";
            if (eventId == PlayerLevelUp) return "PlayerLevelUp";
            if (eventId == PlayerExpChanged) return "PlayerExpChanged";
            if (eventId == PlayerGoldChanged) return "PlayerGoldChanged";
            if (eventId == PlayerDiamondChanged) return "PlayerDiamondChanged";
            if (eventId == ItemObtained) return "ItemObtained";
            if (eventId == ItemUsed) return "ItemUsed";
            if (eventId == ItemCountChanged) return "ItemCountChanged";

            // 音频事件
            if (eventId == MusicStarted) return "MusicStarted";
            if (eventId == MusicStopped) return "MusicStopped";
            if (eventId == SoundPlayed) return "SoundPlayed";
            if (eventId == VolumeChanged) return "VolumeChanged";

            // 系统事件
            if (eventId == ApplicationPause) return "ApplicationPause";
            if (eventId == ApplicationFocus) return "ApplicationFocus";
            if (eventId == ApplicationQuit) return "ApplicationQuit";
            if (eventId == LowMemoryWarning) return "LowMemoryWarning";
            if (eventId == LanguageChanged) return "LanguageChanged";

            return $"UnknownEvent({eventId})";
        }
    }
}
