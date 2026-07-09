using System;
using System.Collections.Generic;
using UnityEngine;
using Framework.Core;

namespace Framework
{
    /// <summary>
    /// 定时器管理器（单 Update 驱动的 tick 模型）。
    /// 所有定时器由 <see cref="OnUpdate"/> 统一按帧推进，不再为每个定时器分配
    /// CancellationTokenSource 与异步闭包；定时器条目通过对象池复用，频繁增删近似零 GC。
    /// 支持一次性 / 有限循环 / 无限循环、暂停/恢复/取消，以及缩放时间或真实时间两种时基。
    ///
    /// 设计说明：本游戏同时存活的定时器数量很小（个位到几十），采用 O(n) 顺序推进
    /// 而非最小堆——后者在「缩放时间 + 真实时间双时基 + 暂停」下需要双堆与惰性删除，
    /// 复杂度与 bug 面都更大却无可测收益。若未来定时器数量级显著上升，可在不改动
    /// 公开 API 的前提下替换内部存储为按时基分桶的最小堆。
    /// </summary>
    public class TimerManager : FrameworkComponent<TimerManager>
    {
        /// <summary>定时器条目，记录单个定时器的运行状态。</summary>
        private sealed class TimerEntry
        {
            /// <summary>定时器ID。</summary>
            public int Id;

            /// <summary>周期/时长（秒）。</summary>
            public float Interval;

            /// <summary>距下次触发的剩余时间（秒，本定时器时基）。</summary>
            public float Remaining;

            /// <summary>触发回调。</summary>
            public Action Callback;

            /// <summary>是否使用真实时间（不受 Time.timeScale 影响）。</summary>
            public bool UseRealTime;

            /// <summary>是否循环。</summary>
            public bool IsLoop;

            /// <summary>循环次数（-1 表示无限循环）。</summary>
            public int LoopCount;

            /// <summary>已触发次数。</summary>
            public int FiredCount;

            /// <summary>是否暂停。</summary>
            public bool IsPaused;

            /// <summary>是否已标记移除（延迟删除，避免遍历中改集合）。</summary>
            public bool IsDead;

            /// <summary>重置为可复用的初始状态。</summary>
            public void Reset()
            {
                Id = 0;
                Interval = 0f;
                Remaining = 0f;
                Callback = null;
                UseRealTime = false;
                IsLoop = false;
                LoopCount = 0;
                FiredCount = 0;
                IsPaused = false;
                IsDead = false;
            }
        }

        // 全部存活定时器（含已暂停），按 Id 索引，用于查询与取消
        private readonly Dictionary<int, TimerEntry> _timers = new Dictionary<int, TimerEntry>();

        // 顺序推进列表，与 _timers 内容一致（IsDead 标记的条目延迟到帧末清理）
        private readonly List<TimerEntry> _active = new List<TimerEntry>();

        // 回调内新增的定时器先暂存，帧末并入 _active，避免遍历中扩容/越界
        private readonly List<TimerEntry> _pendingAdd = new List<TimerEntry>();

        // 条目对象池，复用 TimerEntry 降低增删 GC
        private readonly Stack<TimerEntry> _entryPool = new Stack<TimerEntry>();

        // 下一个定时器ID
        private int _nextTimerId = 1;

        // 是否正在遍历推进（用于把回调内的新增/清理推迟到帧末）
        private bool _iterating;

        // 单帧内单个循环定时器的最大补偿触发次数，防止 interval 过小或卡帧导致死循环
        private const int MaxCatchUpPerFrame = 1000;

        /// <summary>
        /// 初始化
        /// </summary>
        public override void OnInit()
        {
            base.OnInit();
            GameLog.Log("[TimerManager] 定时器管理器初始化完成");
        }

        /// <summary>
        /// 关闭清理
        /// </summary>
        public override void OnShutdown()
        {
            CancelAllTimers();
            _entryPool.Clear();
            GameLog.Log("[TimerManager] 定时器管理器已关闭");
            base.OnShutdown();
        }

        /// <summary>
        /// 每帧推进所有定时器。缩放时间定时器使用 deltaTime，真实时间定时器使用 unscaledDeltaTime。
        /// </summary>
        /// <param name="deltaTime">缩放后的帧间隔（来自 GameEntry）。</param>
        public override void OnUpdate(float deltaTime)
        {
            if (_active.Count == 0)
            {
                FlushPending();
                return;
            }

            float realDelta = Time.unscaledDeltaTime;

            _iterating = true;
            for (int i = 0; i < _active.Count; i++)
            {
                TimerEntry t = _active[i];
                if (t.IsDead || t.IsPaused)
                {
                    continue;
                }

                t.Remaining -= t.UseRealTime ? realDelta : deltaTime;

                // 一帧可能跨过多个周期，循环补偿触发
                int catchUp = 0;
                while (!t.IsDead && !t.IsPaused && t.Remaining <= 0f)
                {
                    InvokeSafe(t);
                    t.FiredCount++;

                    // 一次性，或有限循环已达次数 → 结束
                    if (!t.IsLoop || (t.LoopCount > 0 && t.FiredCount >= t.LoopCount))
                    {
                        MarkDead(t);
                        break;
                    }

                    t.Remaining += t.Interval;

                    if (++catchUp >= MaxCatchUpPerFrame)
                    {
                        // 防御：interval 过小或本帧间隔异常巨大，丢弃积压，下帧重新计时
                        t.Remaining = t.Interval;
                        break;
                    }
                }
            }
            _iterating = false;

            FlushPending();
        }

        #region 创建定时器

        /// <summary>
        /// 添加一次性定时器
        /// </summary>
        /// <param name="onComplete">完成回调</param>
        /// <param name="duration">持续时间（秒）</param>
        /// <param name="useRealTime">是否使用真实时间（不受 Time.timeScale 影响）</param>
        /// <returns>定时器ID，参数非法返回 -1</returns>
        public int AddTimer(Action onComplete, float duration, bool useRealTime = false)
        {
            if (duration <= 0)
            {
                GameLog.Warning($"[TimerManager] 定时器持续时间必须大于0，当前值: {duration}");
                return -1;
            }

            if (onComplete == null)
            {
                GameLog.Warning("[TimerManager] 定时器回调不能为 null");
                return -1;
            }

            TimerEntry t = RentEntry();
            t.Id = _nextTimerId++;
            t.Interval = duration;
            t.Remaining = duration;
            t.Callback = onComplete;
            t.UseRealTime = useRealTime;
            t.IsLoop = false;
            t.LoopCount = 1;

            Register(t);
            GameLog.Debug($"[TimerManager] 添加一次性定时器，ID: {t.Id}, 持续时间: {duration}秒");
            return t.Id;
        }

        /// <summary>
        /// 添加循环定时器
        /// </summary>
        /// <param name="onTick">每次触发的回调</param>
        /// <param name="interval">间隔时间（秒）</param>
        /// <param name="loopCount">循环次数（-1 表示无限循环）</param>
        /// <param name="useRealTime">是否使用真实时间（不受 Time.timeScale 影响）</param>
        /// <returns>定时器ID，参数非法返回 -1</returns>
        public int AddLoopTimer(Action onTick, float interval, int loopCount = -1, bool useRealTime = false)
        {
            if (interval <= 0)
            {
                GameLog.Warning($"[TimerManager] 定时器间隔时间必须大于0，当前值: {interval}");
                return -1;
            }

            if (onTick == null)
            {
                GameLog.Warning("[TimerManager] 定时器回调不能为 null");
                return -1;
            }

            TimerEntry t = RentEntry();
            t.Id = _nextTimerId++;
            t.Interval = interval;
            t.Remaining = interval;
            t.Callback = onTick;
            t.UseRealTime = useRealTime;
            t.IsLoop = true;
            t.LoopCount = loopCount;

            Register(t);
            string loopInfo = loopCount < 0 ? "无限" : loopCount.ToString();
            GameLog.Debug($"[TimerManager] 添加循环定时器，ID: {t.Id}, 间隔: {interval}秒, 循环次数: {loopInfo}");
            return t.Id;
        }

        #endregion

        #region 控制定时器

        /// <summary>
        /// 暂停定时器（保留剩余时间，恢复后继续）
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        public void PauseTimer(int timerId)
        {
            if (_timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead)
            {
                if (!t.IsPaused)
                {
                    t.IsPaused = true;
                    GameLog.Debug($"[TimerManager] 暂停定时器，ID: {timerId}, 剩余: {t.Remaining}秒");
                }
            }
            else
            {
                GameLog.Warning($"[TimerManager] 暂停定时器失败，定时器不存在，ID: {timerId}");
            }
        }

        /// <summary>
        /// 恢复定时器
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        public void ResumeTimer(int timerId)
        {
            if (_timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead)
            {
                if (t.IsPaused)
                {
                    t.IsPaused = false;
                    GameLog.Debug($"[TimerManager] 恢复定时器，ID: {timerId}, 剩余: {t.Remaining}秒");
                }
            }
            else
            {
                GameLog.Warning($"[TimerManager] 恢复定时器失败，定时器不存在，ID: {timerId}");
            }
        }

        /// <summary>
        /// 取消定时器
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        public void CancelTimer(int timerId)
        {
            if (_timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead)
            {
                MarkDead(t);
                GameLog.Debug($"[TimerManager] 取消定时器，ID: {timerId}");
            }
            else
            {
                GameLog.Warning($"[TimerManager] 取消定时器失败，定时器不存在，ID: {timerId}");
            }
        }

        /// <summary>
        /// 取消所有定时器
        /// </summary>
        public void CancelAllTimers()
        {
            int count = _timers.Count;

            // 标记全部死亡并从索引中移除
            foreach (TimerEntry t in _timers.Values)
            {
                t.IsDead = true;
            }
            _timers.Clear();

            // 非遍历期立即清理 _active 并回收条目；遍历期交由帧末 FlushPending 处理
            if (!_iterating)
            {
                for (int i = 0; i < _active.Count; i++)
                {
                    ReturnEntry(_active[i]);
                }
                _active.Clear();
            }

            GameLog.Debug($"[TimerManager] 取消所有定时器，共 {count} 个");
        }

        #endregion

        #region 查询定时器

        /// <summary>
        /// 获取定时器剩余时间
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        /// <returns>剩余时间（秒），定时器不存在返回 -1</returns>
        public float GetRemainingTime(int timerId)
        {
            if (_timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead)
            {
                return Mathf.Max(0f, t.Remaining);
            }
            return -1f;
        }

        /// <summary>
        /// 获取定时器进度（0-1，距本次周期触发的完成度）
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        /// <returns>进度值（0-1），定时器不存在返回 -1</returns>
        public float GetProgress(int timerId)
        {
            if (_timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead)
            {
                if (t.Interval <= 0f)
                {
                    return 1f;
                }
                return Mathf.Clamp01((t.Interval - t.Remaining) / t.Interval);
            }
            return -1f;
        }

        /// <summary>
        /// 检查定时器是否正在运行（存在且未暂停）
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        /// <returns>是否正在运行</returns>
        public bool IsTimerRunning(int timerId)
        {
            return _timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead && !t.IsPaused;
        }

        /// <summary>
        /// 检查定时器是否存在
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        /// <returns>是否存在</returns>
        public bool HasTimer(int timerId)
        {
            return _timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead;
        }

        /// <summary>
        /// 检查定时器是否暂停
        /// </summary>
        /// <param name="timerId">定时器ID</param>
        /// <returns>是否暂停</returns>
        public bool IsTimerPaused(int timerId)
        {
            return _timers.TryGetValue(timerId, out TimerEntry t) && !t.IsDead && t.IsPaused;
        }

        /// <summary>
        /// 获取当前定时器数量
        /// </summary>
        /// <returns>定时器数量</returns>
        public int GetTimerCount()
        {
            return _timers.Count;
        }

        #endregion

        #region 内部实现

        /// <summary>
        /// 安全触发定时器回调，捕获并记录回调异常，避免一个定时器异常影响整体推进。
        /// </summary>
        private void InvokeSafe(TimerEntry t)
        {
            try
            {
                t.Callback?.Invoke();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[TimerManager] 定时器回调异常，ID: {t.Id}, 错误: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 注册定时器到索引与推进列表（遍历期间推迟加入推进列表）。
        /// </summary>
        private void Register(TimerEntry t)
        {
            _timers[t.Id] = t;
            if (_iterating)
            {
                _pendingAdd.Add(t);
            }
            else
            {
                _active.Add(t);
            }
        }

        /// <summary>
        /// 标记定时器为待移除，并从索引中移除；条目延迟到帧末从推进列表清理。
        /// </summary>
        private void MarkDead(TimerEntry t)
        {
            if (t.IsDead)
            {
                return;
            }
            t.IsDead = true;
            _timers.Remove(t.Id);
        }

        /// <summary>
        /// 帧末统一清理已死亡条目并并入挂起的新定时器。
        /// </summary>
        private void FlushPending()
        {
            // 原地压缩，移除已死亡条目并回收
            int write = 0;
            for (int read = 0; read < _active.Count; read++)
            {
                TimerEntry t = _active[read];
                if (t.IsDead)
                {
                    ReturnEntry(t);
                }
                else
                {
                    _active[write++] = t;
                }
            }

            if (write < _active.Count)
            {
                _active.RemoveRange(write, _active.Count - write);
            }

            // 并入回调内新增的定时器
            if (_pendingAdd.Count > 0)
            {
                _active.AddRange(_pendingAdd);
                _pendingAdd.Clear();
            }
        }

        /// <summary>
        /// 从对象池租用一个已重置的定时器条目。
        /// </summary>
        private TimerEntry RentEntry()
        {
            TimerEntry t = _entryPool.Count > 0 ? _entryPool.Pop() : new TimerEntry();
            t.Reset();
            return t;
        }

        /// <summary>
        /// 归还定时器条目以供复用。
        /// </summary>
        private void ReturnEntry(TimerEntry t)
        {
            if (t == null)
            {
                return;
            }
            t.Callback = null; // 及时断开回调引用，避免意外延长闭包/目标对象生命周期
            _entryPool.Push(t);
        }

        #endregion
    }
}
