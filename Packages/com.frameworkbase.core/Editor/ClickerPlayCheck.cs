using System;
using System.Text;
using Framework.Core.Auth;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Framework.Editor
{
    /// <summary>
    /// Clicker 玩法切片（切片 C）无人值守 Play 验收：
    /// 打开 Launch → 自动游客登录 → 等业务入口把 Clicker 主界面拉起 → 驱动 UI（点击/商店开合）→
    /// 校验热更侧落的自检哨兵（玩法数值 / 存档往返）→ 结算零 Error。
    ///
    /// 本检查器在 Framework.Editor 程序集，无法引用 HotUpdate 里的 Clicker 类型，
    /// 故一律按名字定位 UnityEngine.UI.Button 来驱动（与 slice A 同法）。
    /// 判定以日志 ASCII 哨兵为准（batchmode 退出码不可靠）：CLICKER_PLAY_CHECK_OK / _FAIL。
    ///
    /// 用法（不能带 -quit）：
    ///   Unity.exe -batchmode -projectPath ... -executeMethod Framework.Editor.ClickerPlayCheck.Run -logFile ...
    /// </summary>
    public static class ClickerPlayCheck
    {
        private const string ActiveKey   = "ClickerPlayCheck_Active";
        private const string LaunchScene = "Assets/Scenes/Launch.unity";
        private const float  TimeoutSecs = 200f;

        private static int    _errorCount;
        private static int    _warningCount;
        private static bool   _guestClicked;
        private static bool   _clickerReady;
        private static bool   _gameplaySelfCheckOk;
        private static bool   _saveRoundtripOk;
        private static bool   _uiDriven;
        private static bool   _shopOpened;
        private static double _deadline;
        private static double _driveAt = -1;
        private static double _finishAt = -1;
        private static int    _drivePhase;
        private static readonly StringBuilder Details = new StringBuilder();

        [MenuItem("Framework/Template/Run Clicker Play Check (玩法切片验收)")]
        public static void Run()
        {
            // 清持久化配置库：早期测试运行会在 persistentDataPath 留下旧 schema 的 config.db，
            // 触发 ConfigManager「schema 旧于基线→重装」的迁移告警（真实首装不会有）。
            // 删掉它让本次走干净首装，验收测的是「零噪声」而非测试历史残留。
            CleanPersistentConfig();

            // 清持久化登录会话：否则上次游客会话会被静默恢复、跳过登录界面，导致本检查器
            // 等不到游客登录按钮。清掉让登录界面确定性出现，真实走一遍游客登录。
            try { AuthSessionStore.Clear(); }
            catch (Exception ex) { Debug.Log($"[ClickerPlayCheck] 清会话跳过：{ex.Message}"); }

            // 让热更侧 ClickerBootstrap 执行玩法/存档自检（仅本进程可见）。
            Environment.SetEnvironmentVariable("CLICKER_SELFCHECK", "1");
            SessionState.SetBool(ActiveKey, true);
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            Debug.Log("[ClickerPlayCheck] 打开 Launch 场景，进入 Play 模式...");
            EditorApplication.EnterPlaymode();
        }

        private static void CleanPersistentConfig()
        {
            try
            {
                string refData = System.IO.Path.Combine(Application.persistentDataPath, "RefData");
                if (System.IO.Directory.Exists(refData))
                {
                    System.IO.Directory.Delete(refData, recursive: true);
                    Debug.Log($"[ClickerPlayCheck] 已清持久化配置库以走干净首装：{refData}");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[ClickerPlayCheck] 清持久化配置库跳过：{ex.Message}");
            }
        }

        [InitializeOnLoadMethod]
        private static void HookAfterDomainReload()
        {
            if (!SessionState.GetBool(ActiveKey, false))
                return;
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            _deadline = EditorApplication.timeSinceStartup + TimeoutSecs;
            Application.logMessageReceived += OnLog;
            EditorApplication.update += OnUpdate;
            Debug.Log("[ClickerPlayCheck] 已挂钩日志监听与轮询");
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    _errorCount++;
                    if (_errorCount <= 10) Details.AppendLine($"  [E{_errorCount}] {condition}");
                    break;
                case LogType.Warning:
                    _warningCount++;
                    if (_warningCount <= 10) Details.AppendLine($"  [W{_warningCount}] {condition}");
                    break;
            }

            if (condition.Contains("CLICKER_READY")) _clickerReady = true;
            if (condition.Contains("GAMEPLAY_SELFCHECK_OK")) _gameplaySelfCheckOk = true;
            if (condition.Contains("SAVE_ROUNDTRIP_OK")) _saveRoundtripOk = true;
        }

        private static void OnUpdate()
        {
            if (!Application.isPlaying)
                return;

            // 硬超时兜底放最前：任何阶段卡住都能退出，杜绝 batchmode 无限跑（上一版把它放在
            // 各阶段 return 之后，登录阶段一 return 就永远够不到超时 → 挂了数小时）。
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Finish(true);
                return;
            }

            // 尚未进入游戏：best-effort 点游客登录。会话已清则登录界面必现；即便被自动恢复
            // 跳过登录界面，也只是这步空转，等 CLICKER_READY 即可（不再以登录点击为后续前置）。
            if (!_clickerReady)
            {
                if (!_guestClicked)
                {
                    Button guest = FindButtonByName("GuestLoginButton");
                    if (guest != null && guest.interactable)
                    {
                        _guestClicked = true;
                        Debug.Log("[ClickerPlayCheck] 自动触发游客登录");
                        guest.onClick.Invoke();
                    }
                }
                return;
            }

            // 已进入游戏：分帧驱动 UI（点击 / 商店开合）
            if (!_uiDriven)
            {
                if (_driveAt < 0)
                    _driveAt = EditorApplication.timeSinceStartup + 0.5f;
                if (EditorApplication.timeSinceStartup >= _driveAt)
                    DriveUiStep();
                return;
            }

            // UI 驱动完 + 玩法/存档哨兵到齐 → 观察一小段抓迟发错误后收尾
            if (_gameplaySelfCheckOk && _saveRoundtripOk)
            {
                if (_finishAt < 0)
                    _finishAt = EditorApplication.timeSinceStartup + 1.5f;
                if (EditorApplication.timeSinceStartup >= _finishAt)
                    Finish(false);
            }
        }

        // 分帧驱动：点击主按钮 → 开商店 → 关商店。每步间隔 0.4s。
        private static void DriveUiStep()
        {
            _driveAt = EditorApplication.timeSinceStartup + 0.4f;
            switch (_drivePhase)
            {
                case 0:
                    Button click = FindButtonByName("ClickButton");
                    if (click != null) { click.onClick.Invoke(); click.onClick.Invoke(); }
                    Debug.Log("[ClickerPlayCheck] 已驱动点击按钮");
                    _drivePhase++;
                    break;
                case 1:
                    Button shop = FindButtonByName("ShopButton");
                    if (shop != null) { shop.onClick.Invoke(); _shopOpened = true; }
                    _drivePhase++;
                    break;
                case 2:
                    Button buy = FindButtonByName("BuyButton");
                    if (buy != null) buy.onClick.Invoke();
                    _drivePhase++;
                    break;
                case 3:
                    Button close = FindButtonByName("CloseButton");
                    if (close != null) close.onClick.Invoke();
                    Debug.Log($"[ClickerPlayCheck] 商店开合完成 shopOpened={_shopOpened}");
                    _drivePhase++;
                    break;
                default:
                    _uiDriven = true;
                    _driveAt = -1;
                    break;
            }
        }

        private static Button FindButtonByName(string name)
        {
            foreach (Button b in UnityEngine.Object.FindObjectsOfType<Button>())
                if (b.gameObject.name == name)
                    return b;
            return null;
        }

        private static void Finish(bool timedOut)
        {
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceived -= OnLog;
            SessionState.SetBool(ActiveKey, false);
            Environment.SetEnvironmentVariable("CLICKER_SELFCHECK", null);

            bool ok = !timedOut && _errorCount == 0 &&
                      _clickerReady && _gameplaySelfCheckOk && _saveRoundtripOk && _uiDriven;
            string verdict = ok ? "CLICKER_PLAY_CHECK_OK" : "CLICKER_PLAY_CHECK_FAIL";
            Debug.Log($"[ClickerPlayCheck] {verdict} timedOut={timedOut} errors={_errorCount} warnings={_warningCount} " +
                      $"ready={_clickerReady} gameplay={_gameplaySelfCheckOk} save={_saveRoundtripOk} uiDriven={_uiDriven}\n{Details}");

            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
            else
                EditorApplication.ExitPlaymode();
        }
    }
}
