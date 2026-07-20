using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using Framework.Save;

namespace Framework.RedDot
{
    /// <summary>账号级本地弱提示存档；只保存 ID + 已看版本，不保存红点计数或聚合结果。</summary>
    [Serializable]
    public sealed class RedDotSeenSave : SaveData
    {
        /// <summary>当前账号需要持久化的 LocalAccount 已看版本列表。</summary>
        public List<RedDotSeenSaveRecord> records = new List<RedDotSeenSaveRecord>();
    }

    /// <summary>SaveManager 序列化用的已看记录 DTO。</summary>
    [Serializable]
    public sealed class RedDotSeenSaveRecord
    {
        /// <summary>弱提示 Signal 的稳定 ID。</summary>
        public int signalId;

        /// <summary>当前账号已确认的最高内容版本。</summary>
        public int lastSeenVersion;
    }

    /// <summary>
    /// 把 RedDotService 的 LocalAccount 已看版本接到现有 SaveManager 账号隔离目录。
    /// GameEntry 在身份绑定后加载、身份清除前保存。
    /// </summary>
    public static class RedDotAccountSession
    {
        /// <summary>当前身份是否成功加载过红点账号存档；只有活跃会话退出时才写回。</summary>
        private static bool _active;

        /// <summary>重置旧运行态并加载当前 SaveManager 账号目录中的 LocalAccount 已看版本。</summary>
        public static async UniTask BeginAsync(RedDotService service)
        {
            if (service == null || !service.IsInitialized) return;
            service.ResetAccountState();

            RedDotSeenSave save = await SaveManager.Instance.LoadAsync<RedDotSeenSave>();
            var records = new List<RedDotSeenRecord>();
            if (save?.records != null)
            {
                for (int i = 0; i < save.records.Count; i++)
                {
                    RedDotSeenSaveRecord item = save.records[i];
                    if (item == null) continue;
                    records.Add(new RedDotSeenRecord(item.signalId, item.lastSeenVersion));
                }
            }
            service.ImportSeen(RedDotSeenSaveMode.LocalAccount, records);
            _active = true;
        }

        /// <summary>异步触发当前账号已看版本落盘，并同步清空运行态；SaveManager 会捕获当前账号路径。</summary>
        public static void End(RedDotService service)
        {
            if (service == null || !service.IsInitialized)
            {
                _active = false;
                return;
            }

            if (_active)
            {
                IReadOnlyList<RedDotSeenRecord> exported = service.ExportSeen(RedDotSeenSaveMode.LocalAccount);
                var save = new RedDotSeenSave();
                for (int i = 0; i < exported.Count; i++)
                {
                    save.records.Add(new RedDotSeenSaveRecord
                    {
                        signalId = exported[i].SignalId,
                        lastSeenVersion = exported[i].LastSeenVersion,
                    });
                }
                SaveManager.Instance.Save(save);
            }

            service.ResetAccountState();
            _active = false;
        }
    }
}
