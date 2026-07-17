using System;

namespace Framework
{
    /// <summary>
    /// 引导流程推进器：驱动一条 <see cref="GuideScript"/> 的步骤推进、断点存档与完成收尾。
    /// <para>
    /// 框架四原语之「步骤流 + 断点」：业务在 <see cref="StepEntered"/> 里按步骤 id 编排表现
    /// （配合 <see cref="GuideMaskOverlay"/> 挖孔高亮），步骤达成时调 <see cref="CompleteStep"/>
    /// 推进——必须带当前步骤 id，乱序完成是接线错误直接抛（fail-loud）。
    /// 每步推进即写档（存的是步骤 <b>id</b>）：崩溃 / 杀进程重进 <see cref="Start"/> 从断点续。
    /// 断点按 id 在当前剧本里重新定位——线上剧本插入 / 重排步骤，玩家仍续在正确的那一步上，
    /// 不会因序号漂移错位；断点步骤被删 / 改名（id 找不到）则从头重播，不把玩家卡死在不存在的步骤上。
    /// </para>
    /// <para>纯 C# 零 Unity 依赖；订阅者异常隔离经 <see cref="ObserverErrorSink"/>。仅主线程访问。</para>
    /// </summary>
    public sealed class GuideFlow
    {
        private readonly GuideScript _script;
        private readonly IGuideProgressStore _store;
        private int _currentIndex = -1;
        private bool _running;

        public GuideFlow(GuideScript script, IGuideProgressStore store)
        {
            _script = script ?? throw new ArgumentNullException(nameof(script));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>订阅者异常的诊断出口；为 null 时静默隔离。</summary>
        public Action<Exception> ObserverErrorSink { get; set; }

        /// <summary>剧本。</summary>
        public GuideScript Script => _script;

        /// <summary>是否运行中（Start 成功后、完成/跳过前）。</summary>
        public bool IsRunning => _running;

        /// <summary>是否已整条完成（含跳过；读存档，未 Start 也能查）。</summary>
        public bool IsCompleted => _store.IsCompleted(_script.Id);

        /// <summary>当前步骤 id；未运行时为 null。</summary>
        public string CurrentStepId => _running ? _script.Steps[_currentIndex] : null;

        /// <summary>当前步骤序号（0 起）；未运行时为 -1。</summary>
        public int CurrentStepIndex => _running ? _currentIndex : -1;

        /// <summary>进入某步骤时触发（含 Start 断点续上的第一步），参数为（步骤 id, 序号）。</summary>
        public event Action<string, int> StepEntered;

        /// <summary>整条完成（走完最后一步或被跳过）时触发。</summary>
        public event Action Completed;

        /// <summary>
        /// 启动（或断点续跑）。已完成返回 false 不做任何事；重复 Start 运行中的流程返回 false。
        /// 断点按存档步骤 id 在当前剧本里重新定位：id 找不到（该步被删 / 改名）则从头重播。
        /// </summary>
        public bool Start()
        {
            if (_running || _store.IsCompleted(_script.Id))
                return false;

            string savedId = _store.GetStepId(_script.Id);
            int saved = 0;
            if (!string.IsNullOrEmpty(savedId))
            {
                saved = IndexOfStep(savedId);
                if (saved < 0)
                {
                    // 断点步骤在当前剧本已不存在（线上删 / 改名了该步）：无法可靠续跑，
                    // 从头重播——不静默跳过后续未看内容，也不把玩家卡死在不存在的步骤上。
                    saved = 0;
                }
            }

            _currentIndex = saved;
            _running = true;
            NotifyStepEntered();
            return true;
        }

        /// <summary>
        /// 完成当前步骤并推进。<paramref name="stepId"/> 必须等于当前步骤——
        /// 乱序 / 迟到的完成是接线错误，抛异常在开发期炸出。走完最后一步即整条完成。
        /// </summary>
        public void CompleteStep(string stepId)
        {
            if (!_running)
                throw new InvalidOperationException($"引导 '{_script.Id}' 未在运行，不能完成步骤 '{stepId}'。");
            string current = _script.Steps[_currentIndex];
            if (!string.Equals(stepId, current, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"引导 '{_script.Id}' 当前步骤是 '{current}'，收到对 '{stepId}' 的完成——乱序或迟到的触发属接线错误。");
            }

            int next = _currentIndex + 1;
            if (next >= _script.Steps.Count)
            {
                FinishInternal();
                return;
            }

            _currentIndex = next;
            _store.SetStepId(_script.Id, _script.Steps[next]); // 每步落档存 id：崩溃重进按 id 续
            NotifyStepEntered();
        }

        /// <summary>整条跳过（玩家点跳过 / GM）。未运行也可调（直接标完成，防重复弹出）。</summary>
        public void Skip()
        {
            if (_store.IsCompleted(_script.Id))
                return;
            FinishInternal();
        }

        /// <summary>清进度回到未开始（调试 / 重玩入口）。运行中调用会先停止。</summary>
        public void Reset()
        {
            _running = false;
            _currentIndex = -1;
            _store.Clear(_script.Id);
        }

        private void FinishInternal()
        {
            _running = false;
            _currentIndex = -1;
            _store.MarkCompleted(_script.Id);
            try { Completed?.Invoke(); }
            catch (Exception ex) { NotifyObserverError(ex); }
        }

        /// <summary>步骤 id 在当前剧本中的序号；不存在返回 -1。</summary>
        private int IndexOfStep(string stepId)
        {
            for (int i = 0; i < _script.Steps.Count; i++)
            {
                if (string.Equals(_script.Steps[i], stepId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private void NotifyStepEntered()
        {
            try { StepEntered?.Invoke(_script.Steps[_currentIndex], _currentIndex); }
            catch (Exception ex) { NotifyObserverError(ex); }
        }

        private void NotifyObserverError(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); }
            catch { /* 诊断出口自身的异常没有更下游的去处，只能吞。 */ }
        }
    }
}
