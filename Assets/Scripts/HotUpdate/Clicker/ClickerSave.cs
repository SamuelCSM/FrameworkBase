using System;
using Framework.Save;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 玩法存档（账号级隔离，随 SaveManager.SetCurrentUser 切换目录）。
    /// 明文非机密数据走 SaveManager（非 SecureStorage）——金币/等级不是密钥。
    /// </summary>
    [Serializable]
    public class ClickerSave : SaveData
    {
        /// <summary>累计金币。</summary>
        public long coins;

        /// <summary>当前等级（对应 clicker_level 表主键，最小 1）。</summary>
        public int level = 1;
    }
}
