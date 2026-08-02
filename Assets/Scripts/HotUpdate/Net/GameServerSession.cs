using System;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Core.Auth;
using Framework.Network;
using Game.Protocol;
using UnityEngine;

namespace HotUpdate.Net
{
    /// <summary>
    /// 游戏服 TCP 长连接会话驱动（L3 壳工程接线）。
    /// <para>
    /// 框架网络层刻意不认识任何具体协议类型：心跳消息工厂、心跳响应解析、重连后的重新鉴权
    /// 都是留给业务组合根注入的缝。本类把这些缝绑到本工程 <c>GameProtocol</c> 的 001 段系统协议，
    /// 并负责「连接 → SessionBind 握手」这条链路。
    /// </para>
    /// <para>
    /// 握手分两条路径，刻意不合并：首连由 <see cref="ConnectAndBindAsync"/> 显式驱动并把结果交回
    /// 调用方；重连由 <see cref="ReauthenticateAsync"/> 驱动——框架在传输层重连成功后、
    /// <b>冲刷离线待发队列之前</b>调用重鉴权钩子，只有在此处补发 SessionBind，被补发的业务请求
    /// 才落在已绑定的连接上；放到 <c>OnReconnectSucceeded</c> 里就晚了，队列已经冲刷到匿名连接。
    /// </para>
    /// </summary>
    public static class GameServerSession
    {
        /// <summary>协议版本号，与服务端 <c>Gateway.ProtocolVersion</c> 对齐；不匹配时握手回 426。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>会话失效的服务端结果码，与服务端 <c>FrameworkResultCodes.SessionInvalid</c> 对齐。</summary>
        private const int ResultCodeSessionInvalid = 401;

        /// <summary>已完成协议绑定的 NetworkManager 实例。用实例而非 bool 判定，
        /// 使关闭 Domain Reload 的编辑器在重进 Play 换了新实例后仍会重新绑定。</summary>
        private static NetworkManager _installedOn;

        /// <summary>当前连接是否已完成 SessionBind 握手。</summary>
        public static bool IsBound { get; private set; }

        /// <summary>服务端在握手中确认的用户 ID；未绑定时为空串。</summary>
        public static string BoundUserId { get; private set; } = string.Empty;

        /// <summary>绑定状态变化事件（参数为变化后的绑定状态），供 UI 与验收器观察。</summary>
        public static event Action<bool> OnBindStateChanged;

        /// <summary>
        /// 安装系统协议绑定：心跳工厂、心跳响应解析（注入即开启校时）、重连重鉴权钩子。
        /// 对同一 <see cref="NetworkManager"/> 实例幂等，可在离线整包与热更两条启动路径重复调用。
        /// </summary>
        public static void Install()
        {
            NetworkManager network = GameEntry.Network;
            if (network == null)
            {
                GameLog.Error("[GameServerSession] NetworkManager 未就绪，系统协议绑定未安装。");
                return;
            }

            if (ReferenceEquals(_installedOn, network))
                return;
            _installedOn = network;

            // 心跳走 001_001：ClientTime 供服务端回显以估算 RTT，SequenceId 由框架递增。
            network.SetHeartbeatProvider((clientTimeMs, sequenceId) => new GC2GS_001_001_HeartbeatRequest
            {
                ClientTime = clientTimeMs,
                SequenceId = sequenceId,
            });

            // 注入解析器即开启校时：框架按 (发出时刻, 服务端时刻, 收到时刻) 估算偏移与 RTT。
            network.SetHeartbeatResponseParser(payload =>
            {
                try
                {
                    return GS2GC_001_001_HeartbeatResponse.Parser.ParseFrom(payload).ServerTime;
                }
                catch (Exception)
                {
                    return 0; // 畸形心跳响应不参与校时采样
                }
            });

            network.SetReauthenticationProvider(ReauthenticateAsync);
            network.OnDisconnected += HandleDisconnected;

            GameLog.Log("[GameServerSession] 系统协议绑定已安装（心跳 001_001 / 握手 001_002）");
        }

        /// <summary>
        /// 连接游戏服并完成首次 SessionBind 握手。
        /// 未配置服务器地址时按单机模式静默跳过——示例工程允许不起服务端也能跑玩法。
        /// </summary>
        /// <returns>握手成功返回 true；未配置、未登录、连接失败或握手被拒均返回 false。</returns>
        public static async UniTask<bool> ConnectAndBindAsync()
        {
            Install();

            NetworkManager network = GameEntry.Network;
            if (network == null)
                return false;

            AppConfigAsset config = AppConfig.Load();
            if (config == null || string.IsNullOrWhiteSpace(config.GameServerHost) || config.GameServerPort <= 0)
            {
                GameLog.Log("[GameServerSession] 未配置游戏服地址，跳过长连接（单机模式）。");
                return false;
            }

            // SessionBind 拿会话令牌换服务端身份，登录未完成时握手必然被拒，不做无谓往返。
            if (!AuthSession.IsLoggedIn)
            {
                GameLog.Warning("[GameServerSession] 尚未登录，跳过握手：SessionBind 需要会话令牌。");
                return false;
            }

            try
            {
                await network.ConnectAsync(config.GameServerHost, config.GameServerPort);
            }
            catch (Exception ex)
            {
                GameLog.Warning(
                    $"[GameServerSession] 连接游戏服失败 {config.GameServerHost}:{config.GameServerPort} — {ex.Message}");
                return false;
            }

            if (!network.IsConnected)
            {
                GameLog.Warning("[GameServerSession] 连接未建立，跳过握手。");
                return false;
            }

            return await BindAsync("initial");
        }

        /// <summary>
        /// 查询服务端权威档案（020_001，由服务端 L3 示例业务模块提供）。
        /// 请求体不带任何身份字段——服务端只按会话绑定回答「你是谁」，客户端自报不作数。
        /// 这是「服务端权威」的最小形态，日后金币/等级上收时沿本条链路扩展。
        /// </summary>
        /// <returns>未连接或未完成握手时返回 null。</returns>
        public static async UniTask<GS2GC_020_001_GetClickerProfileResponse> QueryServerProfileAsync()
        {
            NetworkManager network = GameEntry.Network;
            if (network == null || !network.IsConnected || !IsBound)
                return null;

            return await network
                .RequestAsync<GC2GS_020_001_GetClickerProfileRequest, GS2GC_020_001_GetClickerProfileResponse>(
                    new GC2GS_020_001_GetClickerProfileRequest());
        }

        /// <summary>
        /// 业务会话退出时断开长连接（登出 / 切号 / 重复登录顶替）。幂等。
        /// </summary>
        /// <param name="reason">断开原因，进日志便于回溯。</param>
        public static void Shutdown(string reason)
        {
            SetBound(false, string.Empty);

            NetworkManager network = GameEntry.Network;
            if (network == null || !network.IsConnected)
                return;

            network.Disconnect();
            GameLog.Log($"[GameServerSession] 长连接已断开 reason={reason}");
        }

        /// <summary>
        /// 在当前连接上发送 SessionBind 握手（001_002）。首连与重连共用同一实现——
        /// 服务端把「重绑」定义为重放同一握手，客户端因此不需要第二套会话恢复协议。
        /// </summary>
        /// <param name="reason">触发原因（initial / reconnect），进哨兵日志便于验收区分两条路径。</param>
        /// <returns>握手成功且服务端回 ResultCode=0 时返回 true。</returns>
        private static async UniTask<bool> BindAsync(string reason)
        {
            NetworkManager network = GameEntry.Network;
            if (network == null)
                return false;

            var request = new GC2GS_001_002_SessionBindRequest
            {
                SessionToken = AuthSession.SessionToken ?? string.Empty,
                // 与 HTTP 登录同源的设备标识：游客身份锚在 DeviceId，两端必须一致。
                DeviceId = SystemInfo.deviceUniqueIdentifier,
                ProtocolVersion = ProtocolVersion,
            };

            GS2GC_001_002_SessionBindResponse response = await network
                .RequestAsync<GC2GS_001_002_SessionBindRequest, GS2GC_001_002_SessionBindResponse>(request);

            if (response == null)
            {
                SetBound(false, string.Empty);
                GameLog.Warning($"[GameServerSession] SESSION_BIND_FAIL reason={reason} 无响应（超时或连接已断）");
                return false;
            }

            if (response.ResultCode != 0)
            {
                SetBound(false, string.Empty);
                GameLog.Warning($"[GameServerSession] SESSION_BIND_FAIL reason={reason} code={response.ResultCode}");
                return false;
            }

            SetBound(true, response.UserId);
            GameLog.Log($"[GameServerSession] SESSION_BIND_OK reason={reason} userId={response.UserId}");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 自检：仅当外部（CI / GameServerSessionPlayCheck）置环境变量时运行。
            // 业务通道探针必须落在热更侧——GameProtocol 与 HotUpdate 同属热更程序集，
            // 编辑器验收器不能对其做编译期绑定，只能靠这里的 ASCII 哨兵观察。
            if (Environment.GetEnvironmentVariable("GAMESERVER_SELFCHECK") == "1")
            {
                await RunEchoSelfCheck();
            }
#endif

            return true;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>绑定后发一条 Echo（002_001），证明业务号段消息在握手后真被服务端放行。</summary>
        private static async UniTask RunEchoSelfCheck()
        {
            const string probeText = "framework-base-transport-probe";
            try
            {
                GS2GC_002_001_EchoResponse response = await GameEntry.Network
                    .RequestAsync<GC2GS_002_001_EchoRequest, GS2GC_002_001_EchoResponse>(
                        new GC2GS_002_001_EchoRequest { Text = probeText });

                bool ok = response != null && response.ResultCode == 0 &&
                          string.Equals(response.Text, probeText, StringComparison.Ordinal);
                GameLog.Log(ok
                    ? "[GameServerSession] ECHO_ROUNDTRIP_OK 业务通道在绑定后放行"
                    : $"[GameServerSession] ECHO_ROUNDTRIP_FAIL code={response?.ResultCode} text={response?.Text}");
            }
            catch (Exception ex)
            {
                GameLog.Log($"[GameServerSession] ECHO_ROUNDTRIP_FAIL 异常 {ex.Message}");
            }

            await RunServerProfileSelfCheck();
        }

        /// <summary>
        /// 查一次服务端权威档案，证明 L3 示例业务模块（samples/，非框架项目）经模块缝接入后
        /// 真能被客户端消费。断言服务端回的 userId 与本地登录身份一致——它来自会话绑定，
        /// 而请求体是空的。
        /// </summary>
        private static async UniTask RunServerProfileSelfCheck()
        {
            try
            {
                GS2GC_020_001_GetClickerProfileResponse profile = await QueryServerProfileAsync();
                bool ok = profile != null && profile.ResultCode == 0 &&
                          string.Equals(profile.UserId, AuthSession.UserId, StringComparison.Ordinal) &&
                          profile.ServerTimeMs > 0;

                GameLog.Log(ok
                    ? $"[GameServerSession] SERVER_PROFILE_OK userId={profile.UserId} " +
                      $"serverTime={profile.ServerTimeMs} boundAt={profile.BoundAtMs}"
                    : $"[GameServerSession] SERVER_PROFILE_FAIL code={profile?.ResultCode} userId={profile?.UserId}");
            }
            catch (Exception ex)
            {
                GameLog.Log($"[GameServerSession] SERVER_PROFILE_FAIL 异常 {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// 重连后的会话恢复钩子。先经鉴权层续/校验令牌，再在新连接上重放握手，两步都成功才算恢复。
        /// <para>
        /// 只做 HTTP 续令牌是不够的：那只刷新了凭据，新建立的 TCP 连接在服务端仍是匿名的，
        /// 随后被冲刷的离线请求会因未绑定而被回 <see cref="ResultCodeSessionInvalid"/>。
        /// </para>
        /// </summary>
        /// <returns>恢复结果；令牌永久失效时透传 SessionExpired，由框架停止重试并引导重新登录。</returns>
        private static async UniTask<NetworkReauthenticationResult> ReauthenticateAsync()
        {
            NetworkReauthenticationResult credentialResult = GameEntry.Auth != null
                ? await GameEntry.Auth.ReauthenticateWithResultAsync()
                : NetworkReauthenticationResult.Succeeded;

            if (credentialResult != NetworkReauthenticationResult.Succeeded)
            {
                SetBound(false, string.Empty);
                return credentialResult;
            }

            // 凭据已就绪，把新连接绑回同一身份。此处失败按可重试处理：令牌真正失效的情况
            // 上一步已返回 SessionExpired，走到这里的失败多为握手往返超时。
            bool bound = await BindAsync("reconnect");
            return bound
                ? NetworkReauthenticationResult.Succeeded
                : NetworkReauthenticationResult.RetryableFailure;
        }

        /// <summary>连接断开即失去绑定：服务端会话与连接同生命周期。</summary>
        private static void HandleDisconnected() => SetBound(false, string.Empty);

        /// <summary>更新绑定状态并在真正发生变化时广播，避免重复事件打扰订阅方。</summary>
        private static void SetBound(bool bound, string userId)
        {
            BoundUserId = bound ? userId ?? string.Empty : string.Empty;

            if (IsBound == bound)
                return;

            IsBound = bound;
            OnBindStateChanged?.Invoke(bound);
        }
    }
}
