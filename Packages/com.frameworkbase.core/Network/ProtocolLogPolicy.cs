using System;
using System.Collections.Generic;

namespace Framework.Network
{
    /// <summary>
    /// 协议收发日志的准入策略（L1 网络层纯类，无 UnityEngine / 线程依赖）。
    /// <para>
    /// 从 <see cref="NetworkManager"/> 抽出「是否该打印某条协议的 SEND/RECV 日志」这一判定：
    /// 受总开关、心跳独立开关、按协议号屏蔽表三者约束。抽成纯类后该策略可在 EditMode 独立单测，
    /// 不必拉起真实连接。本类只做<b>判定</b>，不产生任何副作用——实际落地打印仍由
    /// <see cref="NetworkManager"/> 用 <c>NetworkProtocolLogger</c> 执行。
    /// </para>
    /// </summary>
    internal sealed class ProtocolLogPolicy
    {
        // 按 (mainId,subId) 合成的 16 位协议号屏蔽表：命中即不打印，用于压掉高频刷屏协议。
        private readonly HashSet<ushort> _ignored = new HashSet<ushort>();

        // 心跳判定注入自协调器：心跳身份（System 通道 + 约定 subId）是心跳簇的概念，
        // 本策略不复制该常量，只消费谓词，避免与心跳实现耦合（拆分计划：心跳后续独立成类）。
        private readonly Func<byte, byte, bool> _isHeartbeat;

        // 协议日志总开关。关闭时任何协议都不打印。
        private bool _enabled = true;

        // 心跳协议日志独立开关。默认关闭，避免心跳请求/响应刷屏；不影响其它协议。
        private bool _enableHeartbeat = false;

        /// <summary>
        /// 构造协议日志策略。
        /// </summary>
        /// <param name="isHeartbeat">判定 (mainId,subId) 是否为心跳协议的谓词；传 null 时视所有协议非心跳。</param>
        public ProtocolLogPolicy(Func<byte, byte, bool> isHeartbeat)
        {
            _isHeartbeat = isHeartbeat ?? ((_, __) => false);
        }

        /// <summary>启用或关闭协议日志总开关。关闭后 <see cref="ShouldLog"/> 恒为 false。</summary>
        public void SetEnabled(bool enable) => _enabled = enable;

        /// <summary>启用或关闭心跳协议日志。默认关闭；仅影响心跳协议，不影响屏蔽表与总开关。</summary>
        public void SetHeartbeatEnabled(bool enable) => _enableHeartbeat = enable;

        /// <summary>屏蔽指定协议号的收发日志。</summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        public void Ignore(byte mainId, byte subId) => _ignored.Add(MessagePacket.CombineMessageId(mainId, subId));

        /// <summary>恢复指定协议号的收发日志。</summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        public void Unignore(byte mainId, byte subId) => _ignored.Remove(MessagePacket.CombineMessageId(mainId, subId));

        /// <summary>清空协议日志屏蔽表；不影响心跳日志独立开关与总开关。</summary>
        public void ClearIgnored() => _ignored.Clear();

        /// <summary>
        /// 判断指定协议是否应打印收发日志。判定顺序：总开关 → 心跳独立开关 → 屏蔽表。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>需要打印时返回 <see langword="true"/>。</returns>
        public bool ShouldLog(byte mainId, byte subId)
        {
            if (!_enabled)
                return false;

            if (!_enableHeartbeat && _isHeartbeat(mainId, subId))
                return false;

            return !_ignored.Contains(MessagePacket.CombineMessageId(mainId, subId));
        }
    }
}
