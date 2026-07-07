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
        /// 版本迁移回调。当磁盘封包里的旧版本号低于代码当前版本号时自动触发。
        /// fromVersion = 磁盘中存的版本号（旧版）；迁移完成后 dataVersion 归位到代码当前版本。
        /// </summary>
        protected virtual void OnMigrate(int fromVersion) { }

        /// <summary>
        /// 执行版本迁移。<paramref name="savedVersion"/> 是磁盘封包记录的旧版本（envelope.v），
        /// <paramref name="currentVersion"/> 是"代码里的当前版本"——由 SaveManager 用一个全新实例
        /// （<c>new T().dataVersion</c>，字段初始值不会被反序列化覆盖）取得。
        /// 磁盘版本更旧才回调 <see cref="OnMigrate"/>，随后把内存版本号归位到当前版本，使后续写档以当前版本落盘。
        /// <para>
        /// 之所以不直接用反序列化后的 <c>this.dataVersion</c> 做判断：dataVersion 是可序列化字段，
        /// <c>FromJson</c> 读档时会把它覆盖回磁盘旧值，导致 “savedVersion &lt; this.dataVersion” 恒不成立、
        /// OnMigrate 永不触发。这里以外部传入的当前版本为准，避开该陷阱。
        /// </para>
        /// </summary>
        internal void RunMigrationFrom(int savedVersion, int currentVersion)
        {
            if (savedVersion < currentVersion)
                OnMigrate(savedVersion);

            // 归位：无论是否迁移，内存里的版本号都应是代码当前版本，而非磁盘旧值
            dataVersion = currentVersion;
        }
    }
}
