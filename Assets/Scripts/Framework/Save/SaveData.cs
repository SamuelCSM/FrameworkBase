using System;

namespace Framework.Save
{
    /// <summary>
    /// 所有存档数据的基类。
    ///
    /// 使用方式：
    ///   1. 继承此类并添加 [Serializable]
    ///   2. 在子类中定义需要持久化的字段
    ///   3. 如果修改了子类字段结构，将 dataVersion +1 并重写 OnMigrate 处理迁移
    ///
    /// 示例：
    ///   [Serializable]
    ///   public class PlayerData : SaveData
    ///   {
    ///       public string  nickname = "";
    ///       public int     level    = 1;
    ///       public float   coins    = 0f;
    ///   }
    ///
    ///   // 当增加了 lastLoginTime 字段后：
    ///   [Serializable]
    ///   public class PlayerData : SaveData
    ///   {
    ///       public int dataVersion = 2;   // 升 1 → 2
    ///       ...
    ///       public long lastLoginTime = 0;
    ///
    ///       protected override void OnMigrate(int fromVersion)
    ///       {
    ///           if (fromVersion < 2) lastLoginTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    ///       }
    ///   }
    /// </summary>
    [Serializable]
    public abstract class SaveData
    {
        /// <summary>
        /// 存档数据版本号。
        /// 修改了子类字段结构时，将此值 +1，并在 OnMigrate 中处理旧版本兼容。
        /// </summary>
        public int dataVersion = 1;

        /// <summary>
        /// 版本迁移回调。当磁盘上的版本号低于当前 dataVersion 时自动触发。
        /// fromVersion = 磁盘中存的版本号（旧版），this.dataVersion = 当前最新版本号。
        /// </summary>
        protected virtual void OnMigrate(int fromVersion) { }

        internal void TryMigrate(int savedVersion)
        {
            if (savedVersion < dataVersion)
                OnMigrate(savedVersion);
        }
    }
}
