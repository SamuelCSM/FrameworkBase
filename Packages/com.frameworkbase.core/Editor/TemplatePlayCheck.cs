using System.Text;
using Framework.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 模板启动壳 Play 验收检查器（垂直切片 A 验收 / 切片 F CI 集成绿的种子）。
    ///
    /// batchmode（不带 -quit）执行 <see cref="Run"/>：打开 Launch 场景进入 Play，
    /// 全程统计 Error / Warning，登录界面出现后自动触发游客登录，
    /// 等到 GameEntry「登录完成」哨兵后结算：
    ///   - 零 Error 且未超时 → 打印 <c>TEMPLATE_PLAY_CHECK_OK</c>，Exit(0)
    ///   - 有 Error / 超时  → 打印 <c>TEMPLATE_PLAY_CHECK_FAIL</c> + 明细，Exit(1)
    /// CI 按日志哨兵判定（batchmode 退出码不可靠，见 CI 备忘），退出码仅作辅助。
    ///
    /// 用法：Unity.exe -batchmode -projectPath ... -executeMethod Framework.Editor.TemplatePlayCheck.Run -logFile ...
    /// （注意：不能带 -quit，Play 结束由本检查器调用 EditorApplication.Exit 主动退出。）
    /// </summary>
    public static class TemplatePlayCheck
    {
        private const string ActiveKey     = "TemplatePlayCheck_Active";
        private const string LaunchScene   = "Assets/Scenes/Launch.unity";
        private const float  TimeoutSecs   = 180f;
        /// <summary>登录完成后再观察一小段，抓「登录后立刻炸」类错误。</summary>
        private const float  SettleSecs    = 3f;

        private static int    _errorCount;
        private static int    _warningCount;
        private static bool   _loginDone;
        private static bool   _guestClicked;
        private static double _deadline;
        private static double _settleAt = -1;
        private static readonly StringBuilder Details = new StringBuilder();

        /// <summary>batchmode 入口（编辑器内也可从菜单触发，用于本地快速验收）。</summary>
        [MenuItem("Template/Run Launch Play Check (启动壳验收)")]
        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            Debug.Log("[TemplatePlayCheck] 打开 Launch 场景，进入 Play 模式...");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Play 模式域重载后自挂钩（SessionState 跨域存活；普通静态字段会被重载清零）。
        /// </summary>
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
            Debug.Log("[TemplatePlayCheck] 已挂钩日志监听与轮询，开始验收观察");
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    _errorCount++;
                    if (_errorCount <= 10)
                        Details.AppendLine($"  [E{_errorCount}] {condition}");
                    break;
                case LogType.Warning:
                    _warningCount++;
                    if (_warningCount <= 10)
                        Details.AppendLine($"  [W{_warningCount}] {condition}");
                    break;
            }

            // 终点哨兵：登录链路贯通（LaunchFlow 9 步 + LoginFlow 游客登录）
            if (!_loginDone && condition.Contains("[GameEntry] 登录完成"))
            {
                _loginDone = true;
                _settleAt = EditorApplication.timeSinceStartup + SettleSecs;
            }
        }

        private static void OnUpdate()
        {
            if (!Application.isPlaying)
                return;

            // 登录界面出现后自动触发游客登录（AutoGuestLogin=0 时无人值守跑通全链路）
            if (!_guestClicked)
            {
                var loginView = Object.FindObjectOfType<LoginView>();
                if (loginView != null && loginView.guestLoginButton != null &&
                    loginView.guestLoginButton.interactable)
                {
                    _guestClicked = true;
                    Debug.Log("[TemplatePlayCheck] 检测到登录界面，自动触发游客登录");
                    loginView.guestLoginButton.onClick.Invoke();
                }
            }

            if (_loginDone && EditorApplication.timeSinceStartup >= _settleAt)
            {
                Finish(timedOut: false);
                return;
            }

            if (EditorApplication.timeSinceStartup >= _deadline)
                Finish(timedOut: true);
        }

        private static void Finish(bool timedOut)
        {
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceived -= OnLog;
            SessionState.SetBool(ActiveKey, false);

            bool ok = !timedOut && _errorCount == 0;
            string verdict = ok ? "TEMPLATE_PLAY_CHECK_OK" : "TEMPLATE_PLAY_CHECK_FAIL";
            Debug.Log($"[TemplatePlayCheck] {verdict} loginDone={_loginDone} timedOut={timedOut} " +
                      $"errors={_errorCount} warnings={_warningCount}\n{Details}");

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
            else
            {
                EditorApplication.ExitPlaymode();
                Debug.Log("[TemplatePlayCheck] 编辑器模式：已退出 Play，结果见上方日志");
            }
        }
    }
}
