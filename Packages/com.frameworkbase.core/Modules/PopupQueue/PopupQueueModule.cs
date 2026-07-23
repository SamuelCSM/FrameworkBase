using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Diagnostics;
using UnityEngine;

namespace Framework.Popup
{
    /// <summary>
    /// 一次弹窗/流程展示请求（纯数据）。<see cref="Key"/> 兼作去重键，<see cref="Payload"/> 由业务解释。
    /// </summary>
    public sealed class PopupRequest
    {
        /// <summary>弹窗标识，兼作 <see cref="Unique"/> 去重键。可空（空则不参与去重）。</summary>
        public string Key;

        /// <summary>优先级，越大越先展示；同优先级按入队先后（FIFO）。</summary>
        public int Priority;

        /// <summary>业务负载，队列不解释，原样交回 <see cref="IPopupPresenter.ShowAsync"/>。</summary>
        public object Payload;

        /// <summary>是否按 <see cref="Key"/> 去重：同 Key 已在队列或正在展示时丢弃/择优。默认 true。</summary>
        public bool Unique = true;

        /// <summary>入队序号，供同优先级 FIFO 稳定排序；由 <see cref="PopupQueue"/> 赋值。</summary>
        internal long Sequence;
    }

    /// <summary>
    /// 弹窗/流程序列队列（纯逻辑，不依赖 UnityEngine，可 EditMode 单测）。
    /// 借鉴 ALQueue 的"序列消费"思路——多来源的弹窗请求不并发弹出，而是入队后一次只激活一个、
    /// 关闭后再放下一个，按优先级（同级 FIFO）决定顺序。队列只管"下一个该展示谁"，
    /// 真正的展示/关闭由 <see cref="IPopupPresenter"/> 负责，二者经 <see cref="PopupQueueModule"/> 编排。
    /// <para>
    /// 刻意不做抢占：正在展示的弹窗不被更高优先级打断（打断需保存/恢复现场，属业务策略）；
    /// 更高优先级只会插到待展示队首，等当前关闭后立即轮到。
    /// </para>
    /// </summary>
    public sealed class PopupQueue
    {
        // 待展示请求，始终维持"优先级降序、同级 Sequence 升序"，故队首恒为下一个该展示者。
        private readonly List<PopupRequest> _pending = new List<PopupRequest>();
        private long _seq;

        /// <summary>当前正在展示的请求；无则 null。</summary>
        public PopupRequest Current { get; private set; }

        /// <summary>是否有请求正在展示。</summary>
        public bool IsShowing => Current != null;

        /// <summary>待展示请求数（不含正在展示的）。</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// 入队一个请求。<see cref="PopupRequest.Unique"/> 且 Key 非空时按 Key 去重：
        /// 同 Key 正在展示 → 丢弃；同 Key 已在队列 → 保留优先级更高者（新的更高则替换，否则丢弃新的）。
        /// </summary>
        /// <param name="request">请求（非 null）。</param>
        /// <returns>是否被接纳入队（去重丢弃返回 false）。</returns>
        public bool Enqueue(PopupRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (request.Unique && !string.IsNullOrEmpty(request.Key))
            {
                // 同 Key 正在展示：丢弃，避免同一弹窗叠加
                if (Current != null && Current.Key == request.Key)
                    return false;

                int existing = _pending.FindIndex(p => p.Key == request.Key);
                if (existing >= 0)
                {
                    // 择优保留：新的优先级更高才替换，否则丢弃新的
                    if (request.Priority > _pending[existing].Priority)
                        _pending.RemoveAt(existing);
                    else
                        return false;
                }
            }

            request.Sequence = _seq++;
            InsertSorted(request);
            return true;
        }

        /// <summary>
        /// 若当前无展示且队列非空，激活队首为当前请求并出队。返回是否激活了新请求。
        /// 正在展示（<see cref="IsShowing"/>）时恒返回 false——保证一次只展示一个。
        /// </summary>
        /// <param name="next">被激活的请求；未激活时为 null。</param>
        public bool TryActivateNext(out PopupRequest next)
        {
            next = null;
            if (Current != null || _pending.Count == 0)
                return false;

            next = _pending[0];
            _pending.RemoveAt(0);
            Current = next;
            return true;
        }

        /// <summary>标记当前请求已展示完毕（关闭），清空 <see cref="Current"/>，为下一次激活让路。</summary>
        public void CompleteCurrent()
        {
            Current = null;
        }

        /// <summary>清空待展示队列，不影响正在展示的当前请求（如账号退出时清掉本会话残留弹窗）。</summary>
        public void ClearPending()
        {
            _pending.Clear();
        }

        /// <summary>清空队列与当前展示状态（彻底复位）。</summary>
        public void Reset()
        {
            _pending.Clear();
            Current = null;
        }

        /// <summary>按"优先级降序、同级 Sequence 升序"插入，使队首恒为下一个该展示者。</summary>
        private void InsertSorted(PopupRequest request)
        {
            int i = 0;
            while (i < _pending.Count)
            {
                PopupRequest cur = _pending[i];
                if (request.Priority > cur.Priority)
                    break;
                if (request.Priority == cur.Priority && request.Sequence < cur.Sequence)
                    break;
                i++;
            }
            _pending.Insert(i, request);
        }
    }

    /// <summary>
    /// 弹窗展示者契约：由业务实现真正的 UI 弹出与关闭。
    /// <see cref="ShowAsync"/> 须在弹窗<b>关闭后</b>才返回——队列以其返回为"展示完毕"信号推进下一个。
    /// </summary>
    public interface IPopupPresenter
    {
        /// <summary>
        /// 展示一个弹窗并在其关闭后返回。
        /// </summary>
        /// <param name="request">待展示请求。</param>
        /// <param name="cancellationToken">会话取消令牌；取消时应尽快关闭并抛 <see cref="OperationCanceledException"/>。</param>
        UniTask ShowAsync(PopupRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 弹窗队列对外服务：业务经此入队请求；泵的启动由模块经 onEnqueued 钩子驱动，
    /// 业务无需关心"当前是否空闲"。
    /// </summary>
    public sealed class PopupQueueService
    {
        private readonly PopupQueue _queue;
        private readonly Action _onEnqueued;

        internal PopupQueueService(PopupQueue queue, Action onEnqueued)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _onEnqueued = onEnqueued;
        }

        /// <summary>当前是否有弹窗正在展示。</summary>
        public bool IsShowing => _queue.IsShowing;

        /// <summary>待展示请求数。</summary>
        public int PendingCount => _queue.PendingCount;

        /// <summary>
        /// 入队一个弹窗请求；被接纳则触发泵尝试展示。
        /// </summary>
        /// <param name="key">弹窗标识（兼去重键，可空）。</param>
        /// <param name="priority">优先级，越大越先。</param>
        /// <param name="payload">业务负载，原样交给 Presenter。</param>
        /// <param name="unique">是否按 key 去重（默认 true）。</param>
        /// <returns>是否被接纳入队。</returns>
        public bool Enqueue(string key, int priority = 0, object payload = null, bool unique = true)
        {
            bool accepted = _queue.Enqueue(new PopupRequest
            {
                Key = key,
                Priority = priority,
                Payload = payload,
                Unique = unique,
            });
            if (accepted)
                _onEnqueued?.Invoke();
            return accepted;
        }

        /// <summary>清空待展示队列（不影响正在展示者）。</summary>
        public void ClearPending() => _queue.ClearPending();
    }
}

namespace Framework
{
    using Framework.Popup;

    /// <summary>
    /// 弹窗队列模块访问点（ADR-008）。由 <see cref="PopupQueueModule"/> 在 Phase 1 发布，未安装则为 null。
    /// 业务经 <see cref="Service"/> 入队弹窗请求。
    /// </summary>
    public static class Popups
    {
        /// <summary>弹窗队列服务；模块未安装或已释放时为 null。</summary>
        public static PopupQueueService Service { get; internal set; }
    }

    /// <summary>
    /// 中间层弹窗/流程序列队列模块（ADR-008，沿用既有 L2 模块扩展机制，不改分层/依赖方向/公共契约，故无需新 ADR）。
    /// 持有纯核 <see cref="PopupQueue"/> 与业务注入的 <see cref="IPopupPresenter"/>，以"泵"的方式串行消费：
    /// 空闲时从队列取队首、await 展示至关闭、再取下一个。一次只展示一个，按优先级（同级 FIFO）决定顺序。
    /// <para>
    /// Presenter 单次展示异常被隔离（记录后继续下一个），避免一个坏弹窗把整条队列卡死；
    /// 取消（会话结束/释放）则停止泵。账号退出清待展示队列，避免跨账号残留。
    /// </para>
    /// </summary>
    public sealed class PopupQueueModule : FrameworkModuleBase
    {
        private readonly PopupQueue _queue = new PopupQueue();
        private readonly IPopupPresenter _presenter;
        private readonly PopupQueueService _service;

        // 会话级取消源：Dispose 时取消，中断在途 ShowAsync；泵以其 Token 驱动 Presenter。
        private CancellationTokenSource _cts;
        // 泵是否在运行，避免同一时刻并行两条消费循环（多次入队只需一条泵）。
        private bool _pumping;

        /// <summary>
        /// 构造弹窗队列模块。
        /// </summary>
        /// <param name="presenter">业务实现的弹窗展示者（非 null）。</param>
        public PopupQueueModule(IPopupPresenter presenter)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            // onEnqueued 钩子：入队被接纳即尝试启动泵（已在泵中则无操作）。
            _service = new PopupQueueService(_queue, KickPump);
        }

        /// <summary>Phase 1：发布访问点，早于业务入口，使业务在 StartAsync 后即可入队。</summary>
        public override void RegisterCapabilities()
        {
            _cts = new CancellationTokenSource();
            Popups.Service = _service;
        }

        /// <summary>Phase 2：注册调试命令并就绪。队列本身无需异步初始化。</summary>
        public override UniTask StartAsync()
        {
            RegisterDebugCommands();
            Debug.Log("[Popup] 弹窗队列模块已启动。");
            return UniTask.CompletedTask;
        }

        /// <summary>入队被接纳后尝试启动泵；已在泵中则不重复启动（在途 await 返回后会自然取下一个）。</summary>
        private void KickPump()
        {
            if (_pumping)
                return;
            if (_cts == null || _cts.IsCancellationRequested)
                return; // 已释放/取消，不再消费
            PumpAsync().Forget();
        }

        /// <summary>
        /// 串行消费泵：空闲则激活队首、await 展示至关闭、完成后取下一个，直到队列排空。
        /// 单次展示异常隔离后继续；取消则停止。<see cref="_pumping"/> 保证同时只有一条泵。
        /// </summary>
        private async UniTaskVoid PumpAsync()
        {
            if (_pumping)
                return;
            _pumping = true;
            try
            {
                CancellationToken token = _cts.Token;
                while (!token.IsCancellationRequested && _queue.TryActivateNext(out PopupRequest req))
                {
                    try
                    {
                        await _presenter.ShowAsync(req, token);
                    }
                    catch (OperationCanceledException)
                    {
                        _queue.CompleteCurrent(); // 会话取消：结束当前、停止泵
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 单个弹窗展示异常隔离：记录后继续下一个，不让坏弹窗卡死队列
                        Debug.LogError($"[Popup] 弹窗展示异常（已隔离）Key={req.Key}");
                        Debug.LogException(ex);
                    }

                    _queue.CompleteCurrent();
                }
            }
            finally
            {
                _pumping = false;
            }
        }

        /// <summary>注册弹窗队列状态查询命令（popup）。幂等：已注册则跳过。</summary>
        private void RegisterDebugCommands()
        {
            CommandRegistry registry = GameEntry.Commands;
            if (registry == null || registry.TryGet("popup", out _)) return;
            PopupQueue queue = _queue;
            registry.Register(
                new CommandInfo("popup", "查询弹窗队列状态：当前展示者与待展示数",
                    usage: "popup",
                    requiredAccess: CommandAccessLevel.Privileged),
                _ =>
                {
                    string current = queue.Current != null ? (queue.Current.Key ?? "(无Key)") : "(空闲)";
                    return CommandResult.Ok($"弹窗队列：当前展示={current}，待展示={queue.PendingCount}");
                });
        }

        /// <summary>账号退出：清掉本会话残留的待展示弹窗，避免跨账号带过去（正在展示者让其自然关闭）。</summary>
        public override void OnAccountExit()
        {
            _queue.ClearPending();
        }

        /// <summary>释放：取消在途展示、复位队列、清空访问点。</summary>
        public override void Dispose()
        {
            try { _cts?.Cancel(); }
            catch (Exception ex) { Debug.LogException(ex); }
            _cts?.Dispose();
            _cts = null;

            _queue.Reset();
            if (ReferenceEquals(Popups.Service, _service))
                Popups.Service = null;
        }
    }
}
