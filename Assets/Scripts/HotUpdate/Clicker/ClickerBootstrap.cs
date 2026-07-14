using System;
using Cysharp.Threading.Tasks;
using Framework.Analytics;
using Framework.Core;
using Framework.Core.Auth;
using Framework.Save;
using UnityEngine;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 业务入口装配：把玩法接管逻辑注册到框架的登录后钩子
    /// <see cref="GameEntry.OnBusinessEntryAsync"/>（身份贯通后调用，读账号存档安全）。
    ///
    /// 注册时机分模式：
    ///   - 离线整包（编辑器 / 未热更）：HotUpdate 程序集随工程编入，
    ///     <see cref="RuntimeInitializeOnLoadMethod"/> 自动触发 <see cref="Install"/>；
    ///   - 热更包（HybridCLR 加载）：不执行 RuntimeInitializeOnLoad，
    ///     须由 <c>HotfixEntry.Start()</c> 显式调 <see cref="Install"/>（切片 D 接线）。
    /// <see cref="Install"/> 幂等，两条路径不会重复注册。
    /// </summary>
    public static class ClickerBootstrap
    {
        private static bool _installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstallForOfflineDev() => Install();

        /// <summary>注册登录后业务入口 + 业务埋点事件字典。幂等。</summary>
        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            RegisterAnalyticsSchemas();
            GameEntry.OnBusinessEntryAsync = EnterGameAsync;
            Debug.Log("[Clicker] 业务入口已注册（GameEntry.OnBusinessEntryAsync）");
        }

        /// <summary>
        /// 注册 Clicker 业务埋点 schema（框架治理：Track 未注册事件在 Editor/Dev 报 Error）。
        /// 组合根职责——业务事件契约代码化，避免事件名/属性打错采集到脏数据。
        /// </summary>
        private static void RegisterAnalyticsSchemas()
        {
            AnalyticsSchemaRegistry.Shared.Register(new AnalyticsEventSchema("clicker_click")
                .Require("level", AnalyticsPropType.Integer)
                .Require("gain", AnalyticsPropType.Integer)
                .Require("coins", AnalyticsPropType.Integer));

            AnalyticsSchemaRegistry.Shared.Register(new AnalyticsEventSchema("clicker_upgrade")
                .Require("new_level", AnalyticsPropType.Integer)
                .Require("cost", AnalyticsPropType.Integer));
        }

        private static async UniTask EnterGameAsync(LoginResult loginResult)
        {
            var model = new ClickerModel();
            await model.InitAsync();
            ClickerMainView.Create(model);
            Debug.Log($"[Clicker] CLICKER_READY userId={loginResult.UserId} coins={model.Coins} level={model.Level} double={model.DoubleGain}");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 自检：仅当外部（CI / ClickerPlayCheck）置环境变量时运行，避免污染日常手动 Play。
            // 直接在带类型访问的热更侧验玩法数值与账号级存档，落 ASCII 哨兵供 CI 判定。
            if (Environment.GetEnvironmentVariable("CLICKER_SELFCHECK") == "1")
            {
                RunGameplaySelfCheck(model);
                await RunSaveRoundtripSelfCheck(model);
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 数值取自配表：点击加 ClickGain；升级消耗 UpgradeCost 并升 1 级。验证配表→运行时消费闭环。
        private static void RunGameplaySelfCheck(ClickerModel model)
        {
            long coinsBefore = model.Coins;
            int expectedGain = model.ClickGain;
            model.Click();
            bool clickOk = model.Coins == coinsBefore + expectedGain;

            bool upgradeOk = true;
            if (!model.IsMaxLevel)
            {
                int levelBefore = model.Level;
                model.GrantCoins(model.UpgradeCost); // 补足金币再升级
                upgradeOk = model.TryUpgrade() && model.Level == levelBefore + 1;
            }

            Debug.Log(clickOk && upgradeOk
                ? $"[Clicker] GAMEPLAY_SELFCHECK_OK click(+{expectedGain}) upgrade level={model.Level}"
                : $"[Clicker] GAMEPLAY_SELFCHECK_FAIL clickOk={clickOk} upgradeOk={upgradeOk}");
        }

        private static async UniTask RunSaveRoundtripSelfCheck(ClickerModel model)
        {
            long marker = model.Coins + 123456;
            SaveManager.Instance.Save(new ClickerSave { coins = marker, level = model.Level });
            ClickerSave reloaded = await SaveManager.Instance.LoadAsync<ClickerSave>();
            bool ok = reloaded != null && reloaded.coins == marker && reloaded.level == model.Level;
            Debug.Log(ok
                ? "[Clicker] SAVE_ROUNDTRIP_OK 账号级存档存/读一致"
                : $"[Clicker] SAVE_ROUNDTRIP_FAIL expected={marker} got={reloaded?.coins}");
            model.SaveNow(); // 恢复真实状态
        }
#endif
    }
}
