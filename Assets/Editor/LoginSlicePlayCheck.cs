using System;
using System.Text;
using Framework;
using Framework.Core;
using Framework.Core.Auth;
using Framework.Editor;
using Framework.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// 真实登录切片（切片 E）无人值守 Play 验收：
    /// 进程内起 <see cref="LocalAuthServer"/>（真 HTTP、中性契约）并注入 <see cref="HttpAuthBackend"/>，
    /// 驱动登录界面完成 A（攒金币）→ 登出 → B（金币独立）→ 切回 A（存档恢复）→ 互踢回登录页，
    /// 全程断言：UID 标签随账号切换（身份贯通）、存档按 uid 隔离、登出/互踢后持久化凭据已清、
    /// 三次登录都真实命中本地 HTTP 认证服务（非 Mock）。
    ///
    /// 归属：Clicker 样例专属驱动（按名定位 UI），落游戏侧 Game.Editor；框架 internal 一律经
    /// 公开入口（DevAuthTools / AuthManager.SetBackend）触达。
    /// 判定以日志 ASCII 哨兵为准（batchmode 退出码不可靠）：LOGIN_SLICE_CHECK_OK / _FAIL。
    ///
    /// 用法（不能带 -quit）：
    ///   Unity.exe -batchmode -projectPath ... -executeMethod Game.Editor.LoginSlicePlayCheck.Run -logFile ...
    /// </summary>
    public static class LoginSlicePlayCheck
    {
        private const string ActiveKey   = "LoginSlicePlayCheck_Active";
        private const string LaunchScene = "Assets/Scenes/Launch.unity";
        private const float  TimeoutSecs = 240f;
        private const int    AliceClicks = 15;

        private static LocalAuthServer _server;
        private static int    _errorCount;
        private static int    _warningCount;
        private static int    _readyCount;
        private static int    _phase;
        private static bool   _backendInjected;
        private static bool   _uidAliceOk;
        private static bool   _uidBobOk;
        private static bool   _isolationOk;
        private static bool   _restoreOk;
        private static bool   _credsClearedAfterLogout;
        private static bool   _credsClearedAfterKick;
        private static bool   _scenarioFailed;
        private static long   _coinsAlice1 = -1;
        private static long   _coinsBob = -1;
        private static long   _coinsAlice2 = -1;
        private static double _deadline;
        private static readonly StringBuilder Details = new StringBuilder();

        [MenuItem("Template/Run Login Slice Check (真实登录切片验收)")]
        public static void Run()
        {
            ResetState();

            // 清测试账号的历史存档：上次运行残留的金币会破坏「B 是新档 / A 恢复到本次数值」断言。
            CleanUserSaves("acc_alice");
            CleanUserSaves("acc_bob");

            // 清持久化配置库残留（旧 schema 触发迁移告警，会污染零告警门禁）。
            CleanPersistentConfig();

            // 清持久化登录会话：让首个登录界面确定性出现（否则上次会话静默恢复跳过 UI）。
            try { DevAuthTools.ClearPersistedSession(); }
            catch (Exception ex) { Debug.Log($"[LoginSliceCheck] 清会话跳过：{ex.Message}"); }

            SessionState.SetBool(ActiveKey, true);
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            Debug.Log("[LoginSliceCheck] 打开 Launch 场景，进入 Play 模式...");
            EditorApplication.EnterPlaymode();
        }

        private static void ResetState()
        {
            Application.logMessageReceived -= OnLog;
            EditorApplication.update -= OnUpdate;
            _server?.Dispose();
            _server = null;
            _errorCount = 0;
            _warningCount = 0;
            _readyCount = 0;
            _phase = 0;
            _backendInjected = false;
            _uidAliceOk = _uidBobOk = _isolationOk = _restoreOk = false;
            _credsClearedAfterLogout = _credsClearedAfterKick = false;
            _scenarioFailed = false;
            _coinsAlice1 = _coinsBob = _coinsAlice2 = -1;
            Details.Clear();
        }

        private static void CleanUserSaves(string userId)
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "saves", $"u_{userId}");
                if (System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                    Debug.Log($"[LoginSliceCheck] 已清测试账号存档：{dir}");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[LoginSliceCheck] 清存档跳过：{ex.Message}");
            }
        }

        private static void CleanPersistentConfig()
        {
            // 持久化配置库真实落点是 {persistentDataPath}/config.db（ConfigManager.DefaultDatabaseFileName），
            // 残留旧库会触发「包内基线更新→重装」的一次性迁移告警，污染零告警门禁。
            try
            {
                // language.db 是 ADR-006 分片库（与 config.db 同目录同生命周期），一并清理走干净首装。
                foreach (string relative in new[] { "config.db", "config.db.bak", "language.db", "language.db.bak" })
                {
                    string path = System.IO.Path.Combine(Application.persistentDataPath, relative);
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                        Debug.Log($"[LoginSliceCheck] 已清持久化配置库以走干净首装：{path}");
                    }
                }
                string refData = System.IO.Path.Combine(Application.persistentDataPath, "RefData");
                if (System.IO.Directory.Exists(refData))
                {
                    System.IO.Directory.Delete(refData, recursive: true);
                    Debug.Log($"[LoginSliceCheck] 已清热更配置残留：{refData}");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[LoginSliceCheck] 清持久化配置库跳过：{ex.Message}");
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
            _server = LocalAuthServer.Start();
            Application.logMessageReceived += OnLog;
            EditorApplication.update += OnUpdate;
            Debug.Log("[LoginSliceCheck] 已挂钩日志监听、轮询与本地认证服务");
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

            if (condition.Contains("CLICKER_READY"))
                _readyCount++;
        }

        private static void OnUpdate()
        {
            if (!Application.isPlaying)
                return;

            // 硬超时兜底放最前：任何阶段卡住都能退出（血泪教训：放后面会被阶段 return 挡住）。
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Finish(true);
                return;
            }

            _server?.Pump();

            // 尽早把登录后端切到本地真实 HTTP 服务（Auth 在 Awake 就绪，登录发生在 LaunchFlow 之后）。
            if (!_backendInjected && GameEntry.Auth != null && _server != null)
            {
                GameEntry.Auth.SetBackend(new HttpAuthBackend(_server.LoginUrl));
                _backendInjected = true;
                Debug.Log($"[LoginSliceCheck] 已注入 HttpAuthBackend → {_server.LoginUrl}");
            }

            switch (_phase)
            {
                case 0: // 首个登录界面：以 alice 账号登录
                    if (!TrySubmitAccountLogin("alice", "pw-alice")) return;
                    _phase = 1;
                    return;

                case 1: // alice 会话就绪：验 UID → 攒金币 → 主动登出
                {
                    if (_readyCount < 1) return;
                    TextMeshProUGUI uid = FindTextByName("UserLabel");
                    if (uid == null) return;
                    if (!uid.text.Contains("acc_alice"))
                    {
                        FailScenario($"UID 标签未显示 acc_alice：{uid.text}");
                        return;
                    }
                    _uidAliceOk = true;

                    Button click = FindButtonByName("ClickButton");
                    if (click == null || !click.interactable)
                    {
                        FailScenario("alice 会话缺少可交互 ClickButton");
                        return;
                    }
                    for (int i = 0; i < AliceClicks; i++)
                        click.onClick.Invoke();
                    _coinsAlice1 = ParseCoins();
                    if (_coinsAlice1 < AliceClicks)
                    {
                        FailScenario($"点击 {AliceClicks} 次后金币应≥{AliceClicks}，实际 {_coinsAlice1}");
                        return;
                    }
                    Debug.Log($"[LoginSliceCheck] alice 攒金币完成 coins={_coinsAlice1}，发起主动登出");
                    GameEntry.Event.Publish(GameMessage.PlayerLogout);
                    _phase = 2;
                    return;
                }

                case 2: // 登出拆卸完成回登录页：验主界面已销毁 + 凭据已清 → bob 登录
                {
                    if (FindButtonByName("ClickButton") != null) return; // 等旧主界面销毁完成
                    LoginView view = UnityEngine.Object.FindObjectOfType<LoginView>();
                    if (view == null) return;
                    _credsClearedAfterLogout = !DevAuthTools.HasPersistedSession();
                    if (!_credsClearedAfterLogout)
                    {
                        FailScenario("主动登出后持久化凭据未清除");
                        return;
                    }
                    if (!TrySubmitAccountLogin("bob", "pw-bob")) return;
                    _phase = 3;
                    return;
                }

                case 3: // bob 会话就绪：验 UID 切换 + 存档隔离 → 登出
                {
                    if (_readyCount < 2) return;
                    TextMeshProUGUI uid = FindTextByName("UserLabel");
                    if (uid == null) return;
                    if (!uid.text.Contains("acc_bob"))
                    {
                        FailScenario($"切号后 UID 标签未显示 acc_bob：{uid.text}");
                        return;
                    }
                    _uidBobOk = true;

                    _coinsBob = ParseCoins();
                    if (_coinsBob < 0) return;
                    if (_coinsBob >= _coinsAlice1)
                    {
                        FailScenario($"存档未按账号隔离：bob 初始金币 {_coinsBob} ≥ alice {_coinsAlice1}");
                        return;
                    }
                    _isolationOk = true;
                    Debug.Log($"[LoginSliceCheck] bob 新档独立 coins={_coinsBob}，发起登出切回 alice");
                    GameEntry.Event.Publish(GameMessage.PlayerLogout);
                    _phase = 4;
                    return;
                }

                case 4: // 回登录页：alice 再次登录
                    if (FindButtonByName("ClickButton") != null) return;
                    if (!TrySubmitAccountLogin("alice", "pw-alice")) return;
                    _phase = 5;
                    return;

                case 5: // alice 二进：验存档恢复 → 模拟服务端互踢
                {
                    if (_readyCount < 3) return;
                    TextMeshProUGUI uid = FindTextByName("UserLabel");
                    if (uid == null) return;
                    if (!uid.text.Contains("acc_alice"))
                    {
                        FailScenario($"切回 A 后 UID 标签未显示 acc_alice：{uid.text}");
                        return;
                    }

                    _coinsAlice2 = ParseCoins();
                    if (_coinsAlice2 < 0) return;
                    // 挂机收益只增不减：恢复后的金币应不小于登出时的数值。
                    if (_coinsAlice2 < _coinsAlice1)
                    {
                        FailScenario($"切回 A 存档未恢复：coins={_coinsAlice2}，登出时 {_coinsAlice1}");
                        return;
                    }
                    _restoreOk = true;
                    Debug.Log($"[LoginSliceCheck] alice 存档恢复 coins={_coinsAlice2}，模拟服务端互踢");
                    GameEntry.Event.Publish(GameMessage.ServerForceLogout, 401);
                    _phase = 6;
                    return;
                }

                case 6: // 互踢后回登录页且凭据已清 → 收尾
                {
                    if (FindButtonByName("ClickButton") != null) return;
                    LoginView view = UnityEngine.Object.FindObjectOfType<LoginView>();
                    if (view == null) return;
                    _credsClearedAfterKick = !DevAuthTools.HasPersistedSession();
                    if (!_credsClearedAfterKick)
                    {
                        FailScenario("互踢后持久化凭据未清除");
                        return;
                    }
                    Finish(false);
                    return;
                }
            }
        }

        /// <summary>登录界面就绪时填账号密码并点击账号登录；未就绪返回 false 由调用方下帧重试。</summary>
        private static bool TrySubmitAccountLogin(string account, string password)
        {
            LoginView view = UnityEngine.Object.FindObjectOfType<LoginView>();
            if (view == null || view.accountInput == null || view.passwordInput == null ||
                view.accountLoginButton == null || !view.accountLoginButton.interactable)
                return false;

            view.accountInput.text = account;
            view.passwordInput.text = password;
            Debug.Log($"[LoginSliceCheck] 提交账号登录 account={account}");
            view.accountLoginButton.onClick.Invoke();
            return true;
        }

        /// <summary>解析主界面 CoinLabel（格式「金币 {n}」）；未就绪返回 -1。</summary>
        private static long ParseCoins()
        {
            TextMeshProUGUI label = FindTextByName("CoinLabel");
            if (label == null) return -1;
            var digits = new StringBuilder();
            foreach (char c in label.text)
                if (char.IsDigit(c)) digits.Append(c);
            return digits.Length > 0 && long.TryParse(digits.ToString(), out long value) ? value : -1;
        }

        private static Button FindButtonByName(string name)
        {
            foreach (Button b in UnityEngine.Object.FindObjectsOfType<Button>())
                if (b.gameObject.name == name)
                    return b;
            return null;
        }

        private static TextMeshProUGUI FindTextByName(string name)
        {
            foreach (TextMeshProUGUI text in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
                if (text.gameObject.name == name)
                    return text;
            return null;
        }

        private static void FailScenario(string reason)
        {
            _scenarioFailed = true;
            Details.AppendLine($"  [S] {reason}");
            Finish(false);
        }

        private static void Finish(bool timedOut)
        {
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceived -= OnLog;
            SessionState.SetBool(ActiveKey, false);
            int httpHits = _server?.HandledRequests ?? 0;
            _server?.Dispose();
            _server = null;

            // 三次账号登录必须都真实命中本地 HTTP 认证服务（证明走的是 HttpAuthBackend 而非 Mock）。
            bool httpOk = httpHits >= 3;
            bool ok = !timedOut && _errorCount == 0 && _warningCount == 0 && !_scenarioFailed &&
                      _uidAliceOk && _uidBobOk && _isolationOk && _restoreOk &&
                      _credsClearedAfterLogout && _credsClearedAfterKick && httpOk;
            string verdict = ok ? "LOGIN_SLICE_CHECK_OK" : "LOGIN_SLICE_CHECK_FAIL";
            Debug.Log($"[LoginSliceCheck] {verdict} timedOut={timedOut} errors={_errorCount} warnings={_warningCount} " +
                      $"uidA={_uidAliceOk} uidB={_uidBobOk} isolation={_isolationOk} restore={_restoreOk} " +
                      $"credsClearLogout={_credsClearedAfterLogout} credsClearKick={_credsClearedAfterKick} " +
                      $"httpHits={httpHits} coinsA1={_coinsAlice1} coinsB={_coinsBob} coinsA2={_coinsAlice2}\n{Details}");

            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
            else
                EditorApplication.ExitPlaymode();
        }
    }
}
