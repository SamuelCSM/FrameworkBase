using System;
using System.Collections.Generic;

namespace Framework.Network
{
    /// <summary>
    /// 断线待发队列（纯逻辑，可单测）：断线/重连期间 opt-in 的请求先挂在这里，
    /// 重连 + 重鉴权成功后按 FIFO 补发；入队项有 TTL，等太久的请求按失败收尾
    /// （玩家早就离开那个界面了，迟到的补发只会造成脏数据）。
    ///
    /// 设计约束：
    ///   · 只服务显式 opt-in（<see cref="NetworkRequestConfig.QueueWhileDisconnected"/>）的
    ///     幂等请求——框架绝不默认全量补发，非幂等请求重发的一致性只有业务自己能判断；
    ///   · 队列有上限，超限入队直接拒绝（断网期间的请求洪水不能无界积压）；
    ///   · 补发/失败回调逐项异常隔离，单个业务回调炸了不影响其余项。
    /// </summary>
    internal sealed class OfflineRequestQueue
    {
        private sealed class Item
        {
            /// <summary>补发动作（重连成功后调用，内部走完整 RequestAsync 流程）。</summary>
            public Action Send;

            /// <summary>失败收尾动作（TTL 到期 / 放弃重连 / 主动断开时调用，完成器置 null）。</summary>
            public Action Fail;

            /// <summary>过期时刻（队列时钟，秒）。</summary>
            public double ExpireAt;
        }

        private readonly List<Item> _items = new List<Item>();

        /// <summary>队列上限：超限入队直接拒绝。</summary>
        public int MaxItems = 64;

        /// <summary>当前排队项数（诊断/测试用）。</summary>
        public int Count => _items.Count;

        /// <summary>
        /// 入队一项待补发请求。队列已满返回 false（调用方按发送失败收尾）。
        /// </summary>
        /// <param name="send">补发动作。</param>
        /// <param name="fail">失败收尾动作。</param>
        /// <param name="ttlSeconds">最长等待时长（秒），到期未能补发则失败收尾。</param>
        /// <param name="now">当前队列时钟（秒）。</param>
        /// <param name="isReplaySafe">调用方已明确证明请求为只读或具备服务端幂等去重。</param>
        public bool TryEnqueue(Action send, Action fail, double ttlSeconds, double now, bool isReplaySafe)
        {
            if (send == null || fail == null)
                return false;
            if (!isReplaySafe)
                return false;
            if (_items.Count >= MaxItems)
                return false;

            _items.Add(new Item
            {
                Send = send,
                Fail = fail,
                ExpireAt = now + Math.Max(0, ttlSeconds),
            });
            return true;
        }

        /// <summary>时钟驱动：把已过期的项按失败收尾并移除（保持其余项的 FIFO 次序）。</summary>
        public void Update(double now)
        {
            if (_items.Count == 0)
                return;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (now < _items[i].ExpireAt)
                    continue;

                Item expired = _items[i];
                _items.RemoveAt(i);
                Invoke(expired.Fail, "TTL 过期收尾");
            }
        }

        /// <summary>重连成功：按入队顺序逐项补发并清空。</summary>
        public void FlushAll()
        {
            if (_items.Count == 0)
                return;

            // 先摘下再执行：补发动作内部可能再次入队（补发瞬间又断线），不能撞上正在遍历的列表
            List<Item> toSend = new List<Item>(_items);
            _items.Clear();

            GameLog.Log($"[OfflineRequestQueue] 重连恢复，补发排队请求 {toSend.Count} 条");
            foreach (Item item in toSend)
                Invoke(item.Send, "补发");
        }

        /// <summary>放弃重连 / 主动断开：全部按失败收尾并清空。</summary>
        public void FailAll()
        {
            if (_items.Count == 0)
                return;

            List<Item> toFail = new List<Item>(_items);
            _items.Clear();

            GameLog.Warning($"[OfflineRequestQueue] 连接不再恢复，排队请求 {toFail.Count} 条按失败收尾");
            foreach (Item item in toFail)
                Invoke(item.Fail, "失败收尾");
        }

        /// <summary>逐项异常隔离：单个业务回调炸了不影响其余项。</summary>
        private static void Invoke(Action action, string phase)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[OfflineRequestQueue] {phase}回调异常（已隔离）: {ex.Message}");
            }
        }
    }
}
