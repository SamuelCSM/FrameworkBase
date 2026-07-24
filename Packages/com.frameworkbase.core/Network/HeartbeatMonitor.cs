namespace Framework.Network
{
    /// <summary>
    /// 一次 <see cref="HeartbeatMonitor.Advance"/> 推进后协调器应执行的动作。
    /// 判定与副作用分离：本枚举只表达"该做什么"，真正的发包 / 断开由 <see cref="NetworkManager"/> 执行。
    /// </summary>
    internal enum HeartbeatAction
    {
        /// <summary>本帧无需动作。</summary>
        None,

        /// <summary>心跳间隔到点，应发送一次保活心跳。</summary>
        Send,

        /// <summary>超过超时阈值仍未收到任何数据，应断开传输以触发重连。</summary>
        TimedOut,
    }

    /// <summary>
    /// 心跳时序状态机（L1 网络层纯类，无 UnityEngine / 线程 / IO 依赖）。
    /// <para>
    /// 从 <see cref="NetworkManager"/> 抽出心跳的<b>定时与超时判定</b>：间隔计时、超时累积、序号自增。
    /// 只做判定不做 IO——发包、断开、服务器校时仍由 <see cref="NetworkManager"/> 持 TcpClient 执行。
    /// 这样心跳时序可在 EditMode 手动 <c>Advance</c> 驱动、无需真实连接即可完整覆盖。
    /// </para>
    /// <para>
    /// 线程约定：与 <see cref="NetworkManager.OnUpdate"/> 同在主线程调用，自身不加锁。
    /// "收到数据"这一跨线程信号由协调器在主线程消费后经 <see cref="OnDataReceived"/> 转达。
    /// </para>
    /// </summary>
    internal sealed class HeartbeatMonitor
    {
        // 心跳发送间隔（秒）。
        private float _interval = 30f;
        // 距上次发送心跳的累计秒；到达 _interval 即发一次。
        private float _timer = 0f;
        // 是否定时发送心跳（保活）。
        private bool _sendEnabled = true;

        // 超时阈值（秒）。默认 = 间隔 × 2.5，即连续 2.5 个心跳周期无任何回包即判超时。
        private float _timeoutSeconds = 75f;
        // 是否启用心跳超时检测。
        private bool _timeoutEnabled = true;
        // 距上次收到任意数据的累计秒；超过 _timeoutSeconds 即判超时。
        private float _timeSinceLastData = 0f;

        // 心跳协议体内自增序号，供服务端回显定位请求/响应配对。
        private int _sequenceId = 0;

        /// <summary>当前超时阈值（秒），供协调器打印超时日志。</summary>
        public float TimeoutSeconds => _timeoutSeconds;

        /// <summary>
        /// 设置心跳间隔（秒）。同时把超时阈值联动为间隔 × 2.5。非正值忽略。
        /// </summary>
        public void SetInterval(float interval)
        {
            if (interval <= 0) return;
            _interval = interval;
            _timeoutSeconds = interval * 2.5f;
        }

        /// <summary>启用或关闭定时心跳发送。</summary>
        public void SetSendEnabled(bool enable) => _sendEnabled = enable;

        /// <summary>启用或关闭心跳超时检测。</summary>
        public void SetTimeoutEnabled(bool enable) => _timeoutEnabled = enable;

        /// <summary>
        /// 传输层 (重)连成功时重置两把计时器，避免刚连上就误判超时或立刻抢发心跳。
        /// 前台探活开始前也复用此语义清零计时。
        /// </summary>
        public void OnConnected()
        {
            _timer = 0f;
            _timeSinceLastData = 0f;
        }

        /// <summary>收到任意数据时清零超时累积（服务端任何回包都算存活信号）。</summary>
        public void OnDataReceived() => _timeSinceLastData = 0f;

        /// <summary>
        /// 推进一帧心跳时序，返回协调器应执行的动作。
        /// <para>
        /// 判定顺序与原 <see cref="NetworkManager.OnUpdate"/> 逐字节一致：先超时检测（前台探活期间冻结，
        /// 探活自有超时），命中即返回 <see cref="HeartbeatAction.TimedOut"/> 且不再发送；否则推进发送计时，
        /// 到点返回 <see cref="HeartbeatAction.Send"/>。发送不受探活约束——探活期间仍需保活。
        /// </para>
        /// </summary>
        /// <param name="deltaTime">本帧时长（秒）。</param>
        /// <param name="foregroundProbePending">是否正处于前台探活中；为 true 时冻结超时累积。</param>
        public HeartbeatAction Advance(float deltaTime, bool foregroundProbePending)
        {
            if (_timeoutEnabled && !foregroundProbePending)
            {
                _timeSinceLastData += deltaTime;
                if (_timeSinceLastData > _timeoutSeconds)
                {
                    _timeSinceLastData = 0f;
                    return HeartbeatAction.TimedOut;
                }
            }

            if (_sendEnabled)
            {
                _timer += deltaTime;
                if (_timer >= _interval)
                {
                    _timer = 0f;
                    return HeartbeatAction.Send;
                }
            }

            return HeartbeatAction.None;
        }

        /// <summary>
        /// 取下一个心跳序号（发送前调用）。到达 <see cref="int.MaxValue"/> 时回绕到 0 再自增，
        /// 与原实现的预自增语义一致。
        /// </summary>
        public int NextSequenceId()
        {
            if (_sequenceId == int.MaxValue)
                _sequenceId = 0;
            return ++_sequenceId;
        }
    }
}
