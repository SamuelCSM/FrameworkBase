using System;

namespace Framework
{
    /// <summary>
    /// UI 子模块生命周期接口。
    /// <para>
    /// 子模块不进入全局 UI 栈，也不由 <see cref="UIManager"/> 统一打开或关闭；
    /// 它的生命周期由所属窗口、父子模块或 <see cref="UISubPanelHost{TPanel}"/> 管理。
    /// </para>
    /// </summary>
    public interface IUISubModule : IDisposable
    {
        /// <summary>是否已经完成初始化，初始化后才能显示或隐藏。</summary>
        bool IsInitialized { get; }

        /// <summary>当前是否处于显示状态。</summary>
        bool IsShowing { get; }

        /// <summary>是否已经释放，释放后不应继续复用该逻辑实例。</summary>
        bool IsDisposed { get; }

        /// <summary>
        /// 显示子模块并刷新展示数据。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        void Show(object userData = null);

        /// <summary>
        /// 隐藏子模块并停止展示期行为。
        /// </summary>
        void Hide();
    }
}
