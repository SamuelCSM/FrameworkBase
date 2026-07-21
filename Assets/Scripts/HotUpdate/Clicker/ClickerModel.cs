using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Save;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;
using UnityEngine;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 玩法状态与规则。数值全部来自配表（clicker_level，切片 B 导出），
    /// 是「运行时真正消费 config.db」的兑现点：收益/挂机/升级花费无一硬编码。
    ///
    /// 触达框架子系统：ConfigData（GetConfig）、Save（账号级持久化）、
    /// RemoteConfig（双倍开关）、Analytics（点击/升级埋点）、Timer（挂机+自动存档）、Event（状态广播）。
    /// </summary>
    public class ClickerModel
    {
        /// <summary>远程配置：双倍收益开关键。默认关闭；后端/缓存置 true 时收益翻倍。</summary>
        private const string DoubleGainRemoteKey = "clicker_double_gain";
        private const float AutosaveIntervalSec = 5f;

        private ClickerLevelTable _levels;
        private int _maxLevelId = 1;
        private int _idleTimerId = -1;
        private int _autosaveTimerId = -1;

        public long Coins { get; private set; }
        public int Level { get; private set; } = 1;
        public bool DoubleGain { get; private set; }

        // 当前等级配表行；Level 恒在 [1, _maxLevelId] 内，GetByKey 必命中（不触发缺键告警）。
        private ClickerLevel CurrentRow => _levels?.GetByKey(Level);
        private int Multiplier => DoubleGain ? 2 : 1;

        public int ClickGain => (CurrentRow?.ClickGain ?? 1) * Multiplier;
        public int IdleGainPerSec => (CurrentRow?.IdleGainPerSec ?? 0) * Multiplier;
        public int UpgradeCost => CurrentRow?.UpgradeCost ?? 0;
        public bool IsMaxLevel => CurrentRow == null || CurrentRow.UpgradeCost <= 0;
        /// <summary>当前业务状态是否满足升级条件；UI 与红点投影共享同一事实判断。</summary>
        public bool CanUpgrade => !IsMaxLevel && Coins >= UpgradeCost;
        public string LevelName => CurrentRow?.Name ?? "?";

        /// <summary>加载配表 + 远配开关 + 账号存档，并启动挂机/自动存档定时器。须在登录身份贯通后调用。</summary>
        public async UniTask InitAsync()
        {
            _levels = GameEntry.RefData.GetConfig<ClickerLevelTable>();
            _maxLevelId = ComputeMaxLevelId();
            DoubleGain = GameEntry.RemoteConfig != null &&
                         GameEntry.RemoteConfig.GetBool(DoubleGainRemoteKey, false);

            ClickerSave save = await SaveManager.Instance.LoadAsync<ClickerSave>();
            Coins = save?.coins ?? 0;
            Level = Mathf.Clamp(save?.level ?? 1, 1, _maxLevelId);

            _idleTimerId = GameEntry.Timer.AddLoopTimer(OnIdleTick, 1f);
            _autosaveTimerId = GameEntry.Timer.AddLoopTimer(SaveNow, AutosaveIntervalSec);
            PublishStateChanged();
        }

        private int ComputeMaxLevelId()
        {
            int max = 1;
            foreach (ClickerLevel row in _levels.GetAll())
                if (row.Id > max) max = row.Id;
            return max;
        }

        /// <summary>点击一次：按当前等级配表收益加金币并埋点。</summary>
        public void Click()
        {
            bool couldUpgrade = CanUpgrade;
            Coins += ClickGain;
            GameEntry.Analytics?.Track("clicker_click", new Dictionary<string, object>
            {
                { "level", Level }, { "gain", ClickGain }, { "coins", Coins },
            });
            PublishUpgradeAvailabilityIfChanged(couldUpgrade);
            PublishStateChanged();
        }

        /// <summary>尝试升级：满级或金币不足返回 false（调用方据此禁用按钮，不产生失败态）。</summary>
        public bool TryUpgrade()
        {
            if (IsMaxLevel)
                return false;
            int cost = UpgradeCost;
            if (Coins < cost)
                return false;

            bool couldUpgrade = CanUpgrade;
            Coins -= cost;
            Level += 1;
            GameEntry.Analytics?.Track("clicker_upgrade", new Dictionary<string, object>
            {
                { "new_level", Level }, { "cost", cost },
            });
            SaveNow();
            PublishUpgradeAvailabilityIfChanged(couldUpgrade);
            PublishStateChanged();
            return true;
        }

        /// <summary>商店演示：直接发放金币（验证二级窗口回调打通主状态）。</summary>
        public void GrantCoins(long amount)
        {
            if (amount <= 0)
                return;
            bool couldUpgrade = CanUpgrade;
            Coins += amount;
            PublishUpgradeAvailabilityIfChanged(couldUpgrade);
            PublishStateChanged();
        }

        private void OnIdleTick()
        {
            int gain = IdleGainPerSec;
            if (gain <= 0)
                return;
            bool couldUpgrade = CanUpgrade;
            Coins += gain;
            PublishUpgradeAvailabilityIfChanged(couldUpgrade);
            PublishStateChanged();
        }

        /// <summary>立即落盘（账号级）。定时器与升级/退出各处复用。</summary>
        public void SaveNow()
        {
            SaveManager.Instance.Save(new ClickerSave { coins = Coins, level = Level });
        }

        /// <summary>退出玩法：取消定时器并存档。</summary>
        public void Dispose()
        {
            if (_idleTimerId >= 0) GameEntry.Timer.CancelTimer(_idleTimerId);
            if (_autosaveTimerId >= 0) GameEntry.Timer.CancelTimer(_autosaveTimerId);
            _idleTimerId = _autosaveTimerId = -1;
            SaveNow();
        }

        private void PublishUpgradeAvailabilityIfChanged(bool oldValue)
        {
            if (oldValue != CanUpgrade)
                GameEntry.Event?.Publish(ClickerEvents.UpgradeAvailabilityChanged);
        }

        private void PublishStateChanged() => GameEntry.Event?.Publish(ClickerEvents.StateChanged);
    }
}
