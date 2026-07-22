using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Foundation;
using Framework.RedDot;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 红点模块访问点（ADR-008）。由 <see cref="RedDotModule"/> 构造时赋值，未安装红点模块时为 null。
    /// 替代旧的 <c>GameEntry.RedDots</c>：UI 徽标与业务经 <see cref="Service"/> 用稳定 ID 绑定/写入。
    /// </summary>
    public static class RedDots
    {
        /// <summary>配置驱动的全局红点 DAG 服务；模块未安装或已释放时为 null。</summary>
        public static RedDotService Service { get; internal set; }
    }

    /// <summary>
    /// 中间层红点模块（ADR-008）：持有全局红点 <see cref="RedDotService"/>，负责目录初始化、账号已看版本的
    /// 加载/回推与帧末合并结算。红点目录 <see cref="RedDotCatalog"/> 由 L3 从 ConfigData 构建后经构造注入；
    /// 账号会话委托静态 <see cref="RedDotAccountSession"/>。红点不依赖 Rule/Trigger/Action 编排，故无需参与
    /// 编排冻结。构造即创建服务并发布 <see cref="RedDots.Service"/>，使 UI 徽标可在目录初始化前先行订阅。
    /// </summary>
    public sealed class RedDotModule : FrameworkModuleBase
    {
        private readonly Func<RedDotCatalog> _catalogProvider;
        private readonly RedDotService _service;
        // 帧末合并只需开启一次；目录就绪后的首个 LateUpdate 打开，之后每帧只 FlushPending。
        private bool _frameCoalescingEnabled;

        /// <summary>
        /// 构造即创建空的 <see cref="RedDotService"/> 并发布访问点，让 UI 能在目录初始化前先订阅
        /// （RedDotService 允许未初始化订阅，初始化后保持绑定）。
        /// </summary>
        /// <param name="catalogProvider">红点目录提供者（L3 从 ConfigData 构建；延迟到 StartAsync 求值）。</param>
        public RedDotModule(Func<RedDotCatalog> catalogProvider)
        {
            _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
            _service = new RedDotService
            {
                // 红点订阅者（UI 徽标）回调异常隔离：单个徽标异常不影响其它订阅者与聚合结算。
                ObserverErrorSink = ex =>
                {
                    Debug.LogError("[RedDot] 红点订阅者异常（已隔离）");
                    if (ex != null) Debug.LogException(ex);
                },
            };
            RedDots.Service = _service;
        }

        /// <summary>Phase 2：目录就绪后初始化红点 DAG（幂等）。红点独立于编排服务，只需初始化自身目录。</summary>
        public override UniTask StartAsync()
        {
            if (!_service.IsInitialized)
                _service.Initialize(_catalogProvider());
            Debug.Log($"[RedDot] 红点模块已启动，节点数={_service.Catalog.Nodes.Length}。");
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// 账号进入：加载当前账号 LocalAccount 已看版本（并按需拉取/合并 ServerAccount）。宿主有序 await，
        /// 保证业务读数据前已看版本已就绪，避免红点先亮后灭。
        /// </summary>
        public override UniTask OnAccountEnterAsync(CancellationToken cancellationToken)
            => RedDotAccountSession.BeginAsync(_service);

        /// <summary>账号退出：在身份清除前落盘已看版本、回推 ServerAccount 快照，并清空运行态。</summary>
        public override void OnAccountExit()
            => RedDotAccountSession.End(_service);

        /// <summary>
        /// 帧末统一结算：把本帧内多个来源对同一子树的写入合并为一次聚合与 UI 通知，避免重复计算/刷新。
        /// 目录就绪后首帧开启合并模式；读接口仍按需即时结算，保证"读到自己的写入"。
        /// </summary>
        public override void OnLateUpdate(float deltaTime)
        {
            if (!_service.IsInitialized) return;
            if (!_frameCoalescingEnabled)
            {
                _service.SetFrameCoalescing(true);
                _frameCoalescingEnabled = true;
            }
            _service.FlushPending();
        }

        /// <summary>释放：清空访问点（进程退出时）。RedDotService 为纯逻辑无需显式释放。</summary>
        public override void Dispose()
        {
            if (ReferenceEquals(RedDots.Service, _service)) RedDots.Service = null;
        }
    }
}
