using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 中间层「框架自带业务模块」契约（ADR-008）。宿主生命周期由 <see cref="GameEntry"/> 驱动，分两阶段：
    /// <list type="number">
    /// <item><see cref="RegisterCapabilities"/>：把 Evaluator/Binder/Executor 等能力处理器注册进 L1 编排服务；
    /// 此阶段编排 Catalog 尚未冻结，<b>不得依赖其它模块已就绪</b>。</item>
    /// <item>宿主/组合根冻结编排 Catalog（此后只读）。</item>
    /// <item><see cref="StartAsync"/>：new 运行器、Initialize 模块私有 Catalog、接线、开始监听。</item>
    /// </list>
    /// 账号级生命周期（登录加载 / 登出回推）<b>不进本契约</b>——模块在 <see cref="StartAsync"/> 内自行订阅
    /// 框架账号进入/退出事件。
    /// </summary>
    public interface IFrameworkModule : IDisposable
    {
        /// <summary>Phase 1：注册能力处理器。编排 Catalog 尚未冻结，不得依赖其它模块。</summary>
        void RegisterCapabilities();

        /// <summary>Phase 2：编排 Catalog 冻结后启动模块自身运行时。</summary>
        UniTask StartAsync();

        /// <summary>系统低内存回调，释放可重建缓存；无则空实现。</summary>
        void OnLowMemory();

        /// <summary>
        /// 账号进入回调（ADR-008）：登录成功、玩家身份贯通之后、业务入口之前，由宿主<b>有序 await</b> 驱动。
        /// 适合做“必须在业务读数据前完成”的账号级加载（如红点已看版本），避免先亮后灭。无需求则空实现。
        /// </summary>
        /// <param name="cancellationToken">业务会话取消令牌；登出/退出会触发取消。</param>
        UniTask OnAccountEnterAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 账号退出回调（ADR-008）：在框架清除玩家身份（切回 guest 目录）<b>之前</b>同步调用。
        /// 适合“需在旧身份仍有效时完成”的收尾（如红点已看快照捕获/回推）。须快速返回，异常被宿主隔离。
        /// </summary>
        void OnAccountExit();

        /// <summary>每帧 LateUpdate 回调（ADR-008）：适合帧末统一结算（如红点合并刷新）。无需求则空实现。</summary>
        /// <param name="deltaTime">本帧时长（秒）。</param>
        void OnLateUpdate(float deltaTime);
    }

    /// <summary>模块默认基类：按需覆盖，未覆盖的阶段为空操作。引导等纯事件驱动模块只覆盖前两阶段即可。</summary>
    public abstract class FrameworkModuleBase : IFrameworkModule
    {
        /// <summary>Phase 1：注册能力处理器。默认空实现。</summary>
        public virtual void RegisterCapabilities() { }

        /// <summary>Phase 2：编排冻结后启动。默认空实现（立即完成）。</summary>
        public virtual UniTask StartAsync() => UniTask.CompletedTask;

        /// <summary>低内存回调。默认空实现。</summary>
        public virtual void OnLowMemory() { }

        /// <summary>账号进入。默认空实现（模块无账号级加载需求时无需覆盖）。</summary>
        public virtual UniTask OnAccountEnterAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        /// <summary>账号退出。默认空实现（模块无账号级收尾需求时无需覆盖）。</summary>
        public virtual void OnAccountExit() { }

        /// <summary>每帧结算。默认空实现（模块无逐帧需求时无需覆盖）。</summary>
        public virtual void OnLateUpdate(float deltaTime) { }

        /// <summary>释放模块资源。默认空实现。</summary>
        public virtual void Dispose() { }
    }

    /// <summary>
    /// 中间层模块宿主（ADR-008）。L3 经 <see cref="Use"/> 按启动顺序显式登记模块；<see cref="GameEntry"/>
    /// 在配置库就绪后驱动两阶段（<see cref="RegisterCapabilities"/> → 冻结编排 → <see cref="StartAsync"/>），
    /// 关闭时逆序 <see cref="DisposeAll"/>。
    /// <para>
    /// 异常策略分两段（ADR-008 修订第 9 点）：<b>启动阶段 fail-fast</b>——Phase 1/2 抛异常即标记该模块
    /// <see cref="IsFaulted"/> 并原样抛出，让根因直接暴露在登录链路上，避免被吞掉后在冻结校验处引发
    /// "Executor 未注册" 这类误导性二级错误；<b>运行阶段隔离</b>——低内存/账号/帧回调单模块异常不影响其它模块。
    /// 已标记 Faulted 的模块不再接收任何后续回调（Dispose 除外，它可能已持有待释放资源）。
    /// </para>
    /// </summary>
    public sealed class FrameworkModuleHost
    {
        /// <summary>按登记顺序保存的模块；两阶段/账号/帧回调正序驱动，Dispose 逆序。</summary>
        private readonly List<IFrameworkModule> _modules = new List<IFrameworkModule>();
        /// <summary>启动阶段失败的模块；后续回调一律跳过，避免驱动半死状态的模块（Dispose 除外）。</summary>
        private readonly HashSet<IFrameworkModule> _faulted = new HashSet<IFrameworkModule>();
        /// <summary>Phase 1 是否已执行；用于幂等，并禁止 RegisterCapabilities 之后再 Use 新模块。</summary>
        private bool _capabilitiesRegistered;
        /// <summary>Phase 2 是否已执行；用于幂等（重复登录不重复启动）。</summary>
        private bool _started;
        /// <summary>宿主是否已释放（DisposeAll 后拒绝再登记/驱动）。</summary>
        private bool _disposed;

        /// <summary>模块回调异常出口（默认落 Debug，可由组合根替换为诊断上报）。</summary>
        public Action<string, IFrameworkModule, Exception> ModuleErrorSink { get; set; }

        public IReadOnlyList<IFrameworkModule> Modules => _modules;
        public bool CapabilitiesRegistered => _capabilitiesRegistered;
        public bool Started => _started;

        /// <summary>该模块是否已在启动阶段失败（失败后不再接收后续回调）。</summary>
        public bool IsFaulted(IFrameworkModule module) => module != null && _faulted.Contains(module);

        /// <summary>启动阶段失败的模块数；供诊断命令与自检展示。</summary>
        public int FaultedCount => _faulted.Count;

        /// <summary>登记模块（顺序即启动顺序）。必须在 <see cref="RegisterCapabilities"/> 之前；同一实例重复登记被忽略。</summary>
        public FrameworkModuleHost Use(IFrameworkModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (_disposed) throw new ObjectDisposedException(nameof(FrameworkModuleHost));
            if (_capabilitiesRegistered)
                throw new InvalidOperationException("模块已进入启动阶段，不能再 Use 新模块。");
            if (!_modules.Contains(module)) _modules.Add(module);
            return this;
        }

        /// <summary>
        /// Phase 1：按登记顺序注册各模块能力。幂等。
        /// 启动阶段 fail-fast：某模块抛异常即标记 Faulted 后原样抛出——能力注册失败若被吞掉，
        /// 随后的编排冻结只会报 "TypeId 未注册 Executor"，把根因埋进日志。
        /// </summary>
        public void RegisterCapabilities()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FrameworkModuleHost));
            if (_capabilitiesRegistered) return;
            _capabilitiesRegistered = true;
            for (int i = 0; i < _modules.Count; i++)
            {
                try { _modules[i].RegisterCapabilities(); }
                catch (Exception ex)
                {
                    MarkFaulted("RegisterCapabilities", _modules[i], ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Phase 2：按登记顺序启动各模块。幂等；须在 <see cref="RegisterCapabilities"/> 与编排冻结之后。
        /// 同样 fail-fast；已在 Phase 1 失败的模块被跳过（登录重试时 Phase 1 因幂等不会重跑）。
        /// </summary>
        public async UniTask StartAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FrameworkModuleHost));
            if (!_capabilitiesRegistered)
                throw new InvalidOperationException("必须先 RegisterCapabilities 再 StartAsync。");
            if (_started) return;
            _started = true;
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_faulted.Contains(_modules[i])) continue;
                try { await _modules[i].StartAsync(); }
                catch (Exception ex)
                {
                    MarkFaulted("StartAsync", _modules[i], ex);
                    throw;
                }
            }
        }

        /// <summary>广播低内存到各模块（运行阶段：异常隔离，跳过 Faulted 模块）。</summary>
        public void BroadcastLowMemory()
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_faulted.Contains(_modules[i])) continue;
                try { _modules[i].OnLowMemory(); }
                catch (Exception ex) { Report("OnLowMemory", _modules[i], ex); }
            }
        }

        /// <summary>
        /// 账号进入：按登记顺序<b>有序 await</b> 各模块的账号加载（前一个完成再启动后一个）。
        /// 单个模块异常被隔离并继续——账号级加载（如红点已看版本）失败不应卡住登录进入业务。
        /// </summary>
        public async UniTask OnAccountEnterAsync(CancellationToken cancellationToken)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_faulted.Contains(_modules[i])) continue;
                try { await _modules[i].OnAccountEnterAsync(cancellationToken); }
                catch (Exception ex) { Report("OnAccountEnterAsync", _modules[i], ex); }
            }
        }

        /// <summary>账号退出：按登记顺序驱动各模块收尾（异常隔离，跳过 Faulted）。须在框架清除身份之前调用。</summary>
        public void OnAccountExit()
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_faulted.Contains(_modules[i])) continue;
                try { _modules[i].OnAccountExit(); }
                catch (Exception ex) { Report("OnAccountExit", _modules[i], ex); }
            }
        }

        /// <summary>
        /// 每帧把 LateUpdate 广播给各模块（异常隔离，跳过 Faulted）。属逐帧热路径，模块清单为空时零开销；
        /// Faulted 集合通常为空，HashSet 查询在此可忽略。
        /// </summary>
        public void BroadcastLateUpdate(float deltaTime)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_faulted.Count > 0 && _faulted.Contains(_modules[i])) continue;
                try { _modules[i].OnLateUpdate(deltaTime); }
                catch (Exception ex) { Report("OnLateUpdate", _modules[i], ex); }
            }
        }

        /// <summary>
        /// 逆序 Dispose 所有模块（异常隔离）。幂等。
        /// 与其它回调不同，<b>Faulted 模块也要 Dispose</b>——它可能在失败前已分配需释放的资源。
        /// </summary>
        public void DisposeAll()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _modules.Count - 1; i >= 0; i--)
            {
                try { _modules[i].Dispose(); }
                catch (Exception ex) { Report("Dispose", _modules[i], ex); }
            }
            _modules.Clear();
            _faulted.Clear();
        }

        /// <summary>记录启动阶段失败并摘除该模块的后续回调；调用方随后原样抛出保持 fail-fast。</summary>
        private void MarkFaulted(string phase, IFrameworkModule module, Exception ex)
        {
            if (module != null) _faulted.Add(module);
            Report(phase, module, ex, isolated: false);
        }

        /// <param name="isolated">
        /// 该异常是否已被吞掉：运行阶段回调为 true；启动阶段为 false（调用方随后原样抛出），
        /// 两者的日志措辞必须不同，否则排障时会误以为启动失败也被兜住了。
        /// </param>
        private void Report(string phase, IFrameworkModule module, Exception ex, bool isolated = true)
        {
            Action<string, IFrameworkModule, Exception> sink = ModuleErrorSink;
            if (sink != null)
            {
                try { sink(phase, module, ex); return; }
                catch { /* 出口自身异常不得再抛 */ }
            }
            Debug.LogError($"[FrameworkModuleHost] 模块 {module?.GetType().Name} 在 {phase} 抛异常"
                + (isolated ? "（已隔离）" : "（启动阶段，模块已标记失败并向上抛出）"));
            Debug.LogException(ex);
        }
    }
}
