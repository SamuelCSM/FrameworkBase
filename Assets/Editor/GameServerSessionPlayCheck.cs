using System;
using System.Text;
using Framework;
using Framework.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 长连接贯通（切片 H）无人值守 Play 验收：对**真实 ServerBase**（TCP 9000 + HTTP 9080）
    /// 驱动「游客登录 → 连接 → SessionBind 握手 → 心跳校时 → 业务消息往返」全链路。
    ///
    /// 逐项对应 ServerBase 联调验收清单：握手成功 = 4.2；ServerTime 同步 = 3.1；
    /// 真实 RTT = 3.2；Echo 往返 = 绑定后业务请求放行。
    ///
    /// <para>
    /// 观察方式分两种，因为 <c>HotUpdate</c> 与 <c>GameProtocol</c> 都是热更程序集
    /// （见 HybridCLRSettings），编辑器工具不能对其做编译期绑定：校时/RTT 走 Framework 公开 API
    /// 直接读；握手与 Echo 结果只能靠热更侧打出的 ASCII 哨兵日志观察。
    /// </para>
    ///
    /// 前置：ServerBase 已启动；<c>AppConfig.asset</c> 处于联网档（<c>UseNetworkLogin=1</c> +
    /// <c>GameServerHost/Port</c> 指向服务端）；环境变量 <c>GAMESERVER_SELFCHECK=1</c> 开启 Echo 探针。
    /// 配置未就位时输出 <c>_SKIP</c> 而非 <c>_FAIL</c>——配置缺位不是代码缺陷。
    ///
    /// 判定以日志 ASCII 哨兵为准（batchmode 退出码不可靠）：
    /// GAME_SERVER_SESSION_CHECK_OK / _FAIL / _SKIP。
    ///
    /// 用法（不能带 -quit）：
    ///   Unity.exe -batchmode -projectPath ... -executeMethod Game.Editor.GameServerSessionPlayCheck.Run -logFile ...
    /// </summary>
    public static class GameServerSessionPlayCheck
    {
        private const string LaunchScene = "Assets/Scenes/Launch.unity";
        private const float TimeoutSecs = 180f;

        /// <summary>心跳往返采样需要时间，校时不会在连上瞬间就绪，故给出独立的等待窗口。</summary>
        private const float SyncWaitSecs = 45f;

        /// <summary>Echo 探针在握手成功后立即发出，只需覆盖一次往返。</summary>
        private const float EchoWaitSecs = 20f;

        private static double _deadline;
        private static double _stageDeadline;
        private static int _phase;
        private static bool _bindOk;
        private static bool _syncOk;
        private static bool _echoOk;
        private static bool _echoFailed;
        private static long _observedRttMs = -1;
        private static long _observedOffsetMs;
        private static string _boundUserId = string.Empty;
        private static readonly StringBuilder Details = new StringBuilder();

        /// <summary>入口：校验联网档就位后打开启动场景进入 Play；断言在域重载后的轮询里推进。</summary>
        [MenuItem("Template/Run Game Server Session Check (长连接贯通验收)")]
        public static void Run()
        {
            ResetState();

            AppConfig.ClearCache();
            AppConfigAsset config = AppConfig.Load();
            if (config == null || !config.UseNetworkLogin ||
                string.IsNullOrWhiteSpace(config.GameServerHost) || config.GameServerPort <= 0)
            {
                Debug.Log("[GameServerSessionCheck] GAME_SERVER_SESSION_CHECK_SKIP " +
                          "AppConfig 未处于联网档（需 UseNetworkLogin=1 且 GameServerHost/Port 指向 ServerBase）");
                return;
            }

            if (Environment.GetEnvironmentVariable("GAMESERVER_SELFCHECK") != "1")
            {
                Debug.Log("[GameServerSessionCheck] GAME_SERVER_SESSION_CHECK_SKIP " +
                          "未置 GAMESERVER_SELFCHECK=1，热更侧 Echo 探针不会运行");
                return;
            }

            Debug.Log($"[GameServerSessionCheck] 目标服务端 {config.GameServerHost}:{config.GameServerPort}" +
                      $"（Auth={config.AuthServerUrl}），打开 Launch 场景进入 Play...");
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void ResetState()
        {
            _phase = 0;
            _bindOk = false;
            _syncOk = false;
            _echoOk = false;
            _echoFailed = false;
            _observedRttMs = -1;
            _observedOffsetMs = 0;
            _boundUserId = string.Empty;
            Details.Clear();
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceived -= OnLog;
        }

        /// <summary>Play 进入后经域重载重建轮询与日志钩子（静态状态不跨域重载保留）。</summary>
        [InitializeOnLoadMethod]
        private static void HookAfterDomainReload()
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            _deadline = EditorApplication.timeSinceStartup + TimeoutSecs;
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        /// <summary>热更侧只能经日志汇报：在此捕获握手与 Echo 的 ASCII 哨兵。</summary>
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition))
                return;

            if (!_bindOk && condition.Contains("SESSION_BIND_OK") && condition.Contains("reason=initial"))
            {
                _bindOk = true;
                _boundUserId = ExtractField(condition, "userId=");
                Details.AppendLine($"[OK] SessionBind 握手成功 userId={_boundUserId}");
            }

            if (condition.Contains("ECHO_ROUNDTRIP_OK"))
            {
                _echoOk = true;
                Details.AppendLine("[OK] Echo 002_001 往返一致（绑定后业务通道放行）");
            }
            else if (condition.Contains("ECHO_ROUNDTRIP_FAIL"))
            {
                _echoFailed = true;
                Details.AppendLine($"[FAIL] Echo 往返失败：{condition}");
            }
        }

        /// <summary>从哨兵行里取出 <paramref name="key"/> 之后到下一个空白为止的取值。</summary>
        private static string ExtractField(string line, string key)
        {
            int start = line.IndexOf(key, StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;

            start += key.Length;
            int end = line.IndexOf(' ', start);
            return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
        }

        private static void OnUpdate()
        {
            if (!EditorApplication.isPlaying)
                return;

            if (EditorApplication.timeSinceStartup > _deadline)
            {
                Details.AppendLine($"超时：停在 phase={_phase}");
                Finish();
                return;
            }

            switch (_phase)
            {
                case 0:
                    // 握手由 OnLog 置位；到手后开一个校时等待窗口。
                    if (_bindOk)
                    {
                        _stageDeadline = EditorApplication.timeSinceStartup + SyncWaitSecs;
                        _phase = 1;
                    }
                    break;

                case 1:
                    WaitForTimeSync();
                    break;

                case 2:
                    WaitForEcho();
                    break;
            }
        }

        /// <summary>等心跳往返把校时喂进 <see cref="ServerTime"/>（验收 3.1 / 3.2）。</summary>
        private static void WaitForTimeSync()
        {
            if (ServerTime.IsSynchronized)
            {
                _syncOk = true;
                _observedRttMs = ServerTime.RttMs;
                _observedOffsetMs = ServerTime.OffsetMs;
                Details.AppendLine($"[OK] 校时同步完成 offset={_observedOffsetMs}ms rtt={_observedRttMs}ms");
            }
            else if (EditorApplication.timeSinceStartup > _stageDeadline)
            {
                Details.AppendLine($"[FAIL] {SyncWaitSecs:F0}s 内未完成校时（心跳未往返或响应解析器未注入）");
            }
            else
            {
                return;
            }

            _stageDeadline = EditorApplication.timeSinceStartup + EchoWaitSecs;
            _phase = 2;
        }

        /// <summary>等热更侧 Echo 探针的哨兵；超时按失败收尾。</summary>
        private static void WaitForEcho()
        {
            if (_echoOk || _echoFailed)
            {
                Finish();
                return;
            }

            if (EditorApplication.timeSinceStartup > _stageDeadline)
            {
                Details.AppendLine($"[FAIL] {EchoWaitSecs:F0}s 内未见 Echo 哨兵");
                Finish();
            }
        }

        /// <summary>汇总断言、打哨兵行并收尾；batchmode 下直接以退出码退出。</summary>
        private static void Finish()
        {
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceived -= OnLog;

            bool ok = _bindOk && _syncOk && _echoOk;
            string sentinel = ok ? "GAME_SERVER_SESSION_CHECK_OK" : "GAME_SERVER_SESSION_CHECK_FAIL";

            Debug.Log($"[GameServerSessionCheck] {sentinel} " +
                      $"bind={_bindOk} sync={_syncOk} echo={_echoOk} " +
                      $"userId={_boundUserId} offset={_observedOffsetMs}ms rtt={_observedRttMs}ms\n{Details}");

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
                return;
            }

            EditorApplication.ExitPlaymode();
        }
    }
}
