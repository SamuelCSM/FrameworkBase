using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Diagnostics;
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
        /// <summary>红点目录提供者（L3 从 ConfigData 构建，延迟到 StartAsync 求值）。</summary>
        private readonly Func<RedDotCatalog> _catalogProvider;
        /// <summary>全局红点 DAG 服务；构造即创建并发布访问点 RedDots.Service。</summary>
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
            RegisterDebugCommands();
            Debug.Log($"[RedDot] 红点模块已启动，节点数={_service.Catalog.Nodes.Length}。");
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// 注册红点查询调试命令（reddot / explain / path）。幂等：已注册则跳过。命令闭包捕获本模块服务，
        /// 无参列出所有非零节点，带 ID/Key 查节点详情，explain 追来源链，path 打印亮起路径。
        /// </summary>
        private void RegisterDebugCommands()
        {
            CommandRegistry registry = GameEntry.Commands;
            if (registry == null || registry.TryGet("reddot", out _)) return;
            RedDotService service = _service;
            registry.Register(
                new CommandInfo("reddot", "查询共享红点 DAG：无参列非零节点，支持 ID/Key、explain 来源链与 path 亮起路径",
                    usage: "reddot [ID|Key] | reddot explain <ID|Key> | reddot path <ID|Key>",
                    requiredAccess: CommandAccessLevel.Privileged),
                args =>
                {
                    if (service == null || !service.IsInitialized)
                        return CommandResult.Ok("红点目录未初始化。");

                    string sub = args.GetStringOrDefault(0);
                    bool explain = string.Equals(sub, "explain", StringComparison.OrdinalIgnoreCase);
                    bool path = string.Equals(sub, "path", StringComparison.OrdinalIgnoreCase);
                    string target = args.GetStringOrDefault(explain || path ? 1 : 0);

                    // path：从入口沿"有值"子边逐层深入，打印到最深亮起来源的确定性路径。
                    if (path)
                    {
                        if (string.IsNullOrEmpty(target))
                            return CommandResult.Fail("用法：reddot path <ID|Key>");
                        int pathId;
                        if (!int.TryParse(target, out pathId) && !service.TryResolveId(target, out pathId))
                            return CommandResult.Fail($"红点 ID/Key 不存在：{target}");

                        var pathNodes = service.GetActivePath(pathId);
                        if (pathNodes.Count == 0)
                            return CommandResult.Ok($"红点 {pathId} 未点亮，无亮起路径。");

                        var pathText = new StringBuilder(256);
                        pathText.Append("亮起路径（入口→最深来源）：");
                        for (int i = 0; i < pathNodes.Count; i++)
                        {
                            RedDotNodeSnapshot step = pathNodes[i];
                            pathText.AppendLine().Append("  ");
                            for (int indent = 0; indent < i; indent++) pathText.Append("  ");
                            pathText.Append(i == 0 ? string.Empty : "└ ")
                                .Append(step.Id).Append(" [").Append(step.Key).Append("] = ").Append(step.FinalCount);
                            if (step.Kind == RedDotNodeKind.Signal) pathText.Append("（Signal）");
                        }
                        return CommandResult.Ok(pathText.ToString());
                    }

                    // 带 ID/Key：打印单个节点详情，explain 时附加使其亮起的底层 Signal 来源链。
                    if (!string.IsNullOrEmpty(target))
                    {
                        int id;
                        if (!int.TryParse(target, out id) && !service.TryResolveId(target, out id))
                            return CommandResult.Fail($"红点 ID/Key 不存在：{target}");

                        RedDotNodeSnapshot info = default;
                        bool found = false;
                        foreach (RedDotNodeSnapshot item in service.Snapshot())
                        {
                            if (item.Id != id) continue;
                            info = item;
                            found = true;
                            break;
                        }
                        if (!found) return CommandResult.Fail($"红点 ID 不存在：{id}");

                        var detail = new StringBuilder(256);
                        detail.Append(info.Id).Append(" [").Append(info.Key).Append("] = ").Append(info.FinalCount)
                            .Append(" kind=").Append(info.Kind)
                            .Append(" aggregation=").Append(info.Aggregation);
                        if (info.Kind == RedDotNodeKind.Signal)
                        {
                            detail.Append(" raw=").Append(info.RawCount)
                                .Append(" effective=").Append(info.EffectiveCount)
                                .Append(" provider=").Append(info.Provider ?? "(direct)")
                                .Append(" ready=").Append(info.Provider == null || info.ProviderReady);
                            if (info.SeenPolicy != null)
                            {
                                detail.Append(" seen=").Append(info.LastSeenVersion).Append('/')
                                    .Append(info.SeenPolicy.Version)
                                    .Append(" trigger=").Append(info.SeenPolicy.Trigger)
                                    .Append(" save=").Append(info.SeenPolicy.SaveMode);
                            }
                        }

                        if (explain)
                        {
                            foreach (RedDotNodeSnapshot source in service.GetActiveSignalSources(id))
                            {
                                detail.AppendLine().Append("  <- ").Append(source.Id).Append(" [")
                                    .Append(source.Key).Append("] raw=").Append(source.RawCount)
                                    .Append(" effective=").Append(source.EffectiveCount)
                                    .Append(" provider=").Append(source.Provider ?? "(direct)")
                                    .Append(" ready=").Append(source.Provider == null || source.ProviderReady);
                            }
                        }
                        return CommandResult.Ok(detail.ToString());
                    }

                    // 无参：列出所有非零节点（DAG 无隐式 TotalCount，逐节点列 FinalCount）。
                    var sb = new StringBuilder(256);
                    sb.Append("红点 DAG（非零节点；DAG 无隐式 TotalCount）：");
                    int shown = 0;
                    foreach (RedDotNodeSnapshot info in service.Snapshot())
                    {
                        if (info.FinalCount == 0)
                            continue;
                        sb.AppendLine().Append("  ").Append(info.Id).Append(" [").Append(info.Key)
                            .Append("] = ").Append(info.FinalCount);
                        if (info.Kind == RedDotNodeKind.Aggregate) sb.Append("（聚合）");
                        shown++;
                    }
                    if (shown == 0)
                        sb.AppendLine().Append("  （全空）");
                    return CommandResult.Ok(sb.ToString());
                });
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
