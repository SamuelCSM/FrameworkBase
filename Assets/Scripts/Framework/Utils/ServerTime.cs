using System;

namespace Framework
{
    /// <summary>
    /// 服务器校时服务：基于心跳往返（RTT）估算服务端时钟，供倒计时、每日重置、活动开关等
    /// 一切"以服务端时间为准"的显示逻辑读取，避免直接用本地时间被用户改表钟带偏。
    /// <para>
    /// 采样来源：NetworkManager 在收到心跳响应时调用 <see cref="AddSample"/>（需业务注入
    /// <c>SetHeartbeatResponseParser</c>），偏移 = 服务端时间 + RTT/2 - 本地接收时刻。
    /// RTT 明显劣化的样本（半程不对称误差大）被忽略，只信最优网络状况下的估算。
    /// </para>
    /// <para>断线不清除偏移——时钟偏移不因连接断开而失效，重连后继续用旧值直至新样本到达。</para>
    /// </summary>
    public static class ServerTime
    {
        /// <summary>单次往返超过该值的样本直接丢弃（网络极端拥塞时的估算不可信）。</summary>
        private const long MaxAcceptableRttMs = 10_000;

        /// <summary>服务端时钟 - 本地时钟 的毫秒偏移（仅 _synchronized 为 true 时有效）。</summary>
        private static long _offsetMs;

        /// <summary>历史最优（最小）RTT，毫秒；用于判定新样本质量，随样本缓慢跟随网络变化。</summary>
        private static long _bestRttMs = long.MaxValue;

        /// <summary>是否已完成至少一次有效校时。</summary>
        private static bool _synchronized;

        /// <summary>是否已完成至少一次有效校时。未同步时 <see cref="NowMs"/> 回退为本地 UTC 时间。</summary>
        public static bool IsSynchronized => _synchronized;

        /// <summary>服务端时钟相对本地时钟的毫秒偏移（服务端 - 本地）。未同步时为 0。</summary>
        public static long OffsetMs => _offsetMs;

        /// <summary>当前采信的往返延迟（毫秒）。未同步时为 0。</summary>
        public static long RttMs => _synchronized ? _bestRttMs : 0;

        /// <summary>估算的服务端当前 Unix 毫秒时间戳。未同步时回退本地 UTC。</summary>
        public static long NowMs => LocalNowMs + (_synchronized ? _offsetMs : 0);

        /// <summary>估算的服务端当前时间（UTC）。未同步时回退本地 UTC。</summary>
        public static DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);

        /// <summary>本地 UTC 当前 Unix 毫秒时间戳。</summary>
        private static long LocalNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// 喂入一次校时样本。由框架心跳路径（NetworkManager）调用，业务层无需手动调用。
        /// </summary>
        /// <param name="serverTimeMs">心跳响应携带的服务端 Unix 毫秒时间戳。</param>
        /// <param name="sentLocalMs">心跳请求发出时的本地 Unix 毫秒时间戳。</param>
        /// <param name="receivedLocalMs">心跳响应收到时的本地 Unix 毫秒时间戳。</param>
        public static void AddSample(long serverTimeMs, long sentLocalMs, long receivedLocalMs)
        {
            long rtt = receivedLocalMs - sentLocalMs;
            if (serverTimeMs <= 0 || rtt < 0 || rtt > MaxAcceptableRttMs)
            {
                return; // 时钟回拨 / 极端延迟等异常样本，忽略
            }

            // RTT 不超过历史最优 1.5 倍即采纳（半程对称假设仍基本成立）；明显劣化的样本只用于跟随统计
            if (!_synchronized || rtt <= _bestRttMs + (_bestRttMs >> 1))
            {
                _offsetMs = serverTimeMs + rtt / 2 - receivedLocalMs;
                _synchronized = true;
            }

            if (rtt < _bestRttMs)
            {
                _bestRttMs = rtt;
            }
            else
            {
                // 最优 RTT 向当前样本缓慢上浮（1/8 步进），网络长期变差后仍能重新接受新样本
                _bestRttMs += (rtt - _bestRttMs) >> 3;
            }
        }

        /// <summary>
        /// 清除校时状态（回退为本地时间）。切换服务器 / 登出到不同环境时调用；普通断线重连无需调用。
        /// </summary>
        public static void Reset()
        {
            _offsetMs = 0;
            _bestRttMs = long.MaxValue;
            _synchronized = false;
        }
    }
}
