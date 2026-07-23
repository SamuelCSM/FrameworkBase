using System;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Analytics;
using Framework.Core;
using Framework.Core.Auth;
using Framework.Foundation;
using Framework.Save;
using HotUpdate.RedDot;
using HotUpdate.Entry;
using HotUpdate.UI.Generated;
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
        private static ClickerMainView _mainView;
        private static RedDotCoordinator _redDotCoordinator;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstallForOfflineDev()
        {
            Install();
        }

        /// <summary>注册登录后业务入口 + 业务埋点事件字典。幂等。</summary>
        public static void Install()
        {
            // 离线整包在首次登录前安装 ConfigData 红点目录；HybridCLR 路径在 HotfixEntry 已提前安装。
            RuntimeCatalogBootstrap.RegisterPreEntryHook();

            if (!_installed)
            {
                _installed = true;
                RegisterAnalyticsSchemas();
            }

            // 即使 Editor 关闭了 Domain Reload，也重新挂接当前 GameEntry 的业务钩子。
            RegisterUIs();
            GameEntry.OnBusinessEntryAsync = EnterGameAsync;
            GameEntry.OnBusinessExit = ExitGame;
            Debug.Log("[Clicker] 业务会话钩子已注册（Enter + Exit）");
        }

        /// <summary>把纯代码商店接入 UIManager；同一 GameEntry 生命周期内幂等。</summary>
        private static void RegisterUIs()
        {
            if (GameEntry.UI == null || GameEntry.UI.IsUIRegistered<ClickerShopWindow>())
                return;

            GameEntry.UI.RegisterCodeUI<ClickerShopWindow>(
                UIWindowIds.Clicker.Shop,
                ClickerShopViewFactory.Create,
                UILayer.Popup,
                allowMultiple: false,
                stackBehavior: UIStackBehavior.NoStack,
                blockerMode: UIBlockerMode.DimBlack);
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
            // 防御重复登录/切号：旧会话必须先在旧身份仍有效时保存并释放。
            ExitGame("replace_session");

            RegisterUIs();
            await ClickerGameDataManager.InitializeAsync(loginResult.UserId);
            ClickerModel model = ClickerGameDataManager.Model;

            // 红点服务改由中间层 RedDotModule 发布（ADR-008），经 Framework.RedDots.Service 访问。
            var redDots = Framework.RedDots.Service;
            if (redDots != null && redDots.IsInitialized)
            {
                _redDotCoordinator = new RedDotCoordinator(redDots);
                _redDotCoordinator.Register(new ClickerRedDotProvider());
                _redDotCoordinator.RebuildAll();
            }
            else
            {
                Debug.LogError("[Clicker] 红点目录未初始化，ClickerRedDotProvider 未注册。");
            }

            _mainView = ClickerMainView.Create();
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

        /// <summary>
        /// 在框架清空账号身份前同步退出当前业务会话：先保存/停 Timer，再销毁所有 Clicker UI。
        /// 幂等；重复登出与 ApplicationQuit 不会重复写入或残留对象。
        /// </summary>
        private static void ExitGame(string reason)
        {
            ClickerMainView mainView = _mainView;
            RedDotCoordinator redDotCoordinator = _redDotCoordinator;
            bool hadData = ClickerGameDataManager.IsInitialized;
            _mainView = null;
            _redDotCoordinator = null;

            try
            {
                try
                {
                    redDotCoordinator?.Dispose();
                }
                finally
                {
                    // 红点解绑异常也不能阻止玩法 Timer 和账号存档释放。
                    ClickerGameDataManager.Shutdown();
                }
            }
            finally
            {
                GameEntry.UI?.CloseAllUI<ClickerShopWindow>(destroy: true);
                if (mainView != null)
                    UnityEngine.Object.Destroy(mainView.gameObject);
            }

            if (mainView != null || redDotCoordinator != null || hadData)
                Debug.Log($"[Clicker] 业务会话已退出 reason={reason}");
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
