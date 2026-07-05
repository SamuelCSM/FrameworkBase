using System.Threading;

namespace Framework
{
    /// <summary>
    /// 循环列表行 Presenter 基类：一个 Presenter 配一张被复用的行视图，承载该行的交互 / 异步 / 单行订阅等业务逻辑。
    /// <para>
    /// 由 <see cref="LoopPresenterSource{TPresenter,TView,TData}"/> 驱动：Presenter 与物理行视图 1:1 终身绑定，
    /// 滚动时只 <c>Bind</c>/<c>Unbind</c>、不销毁也不重建，稳态零分配。
    /// 行仅有「展示 + 整行点击」时无需用它，普通 <see cref="ILoopListSource"/> + <see cref="ILoopListClickable"/> 即可。
    /// </para>
    /// </summary>
    /// <typeparam name="TView">行视图类型。</typeparam>
    /// <typeparam name="TData">行数据类型。</typeparam>
    public abstract class LoopCellPresenter<TView, TData> where TView : UISubView
    {
        /// <summary>绑定的行视图（首次绑定后终身不变）。</summary>
        protected TView View { get; private set; }

        /// <summary>当前绑定的数据：一次性接线的回调读它取「当前行」，是复用安全的关键（切勿在闭包里捕获 data）。</summary>
        protected TData Data { get; private set; }

        /// <summary>当前绑定的数据下标（未绑定为 -1）。</summary>
        protected int Index { get; private set; } = -1;

        /// <summary>当前是否绑定着某行数据；回收后为 false，点击回调据此忽略迟到点击。</summary>
        public bool IsBound => Index >= 0;

        /// <summary>本次绑定的取消令牌：解绑（回收）时自动取消，供行内异步（头像加载等）挂靠。</summary>
        protected CancellationToken BoundToken => _cts?.Token ?? default;

        /// <summary>本次绑定的取消源（每次 Bind 新建、Unbind 取消并释放）。</summary>
        private CancellationTokenSource _cts;

        /// <summary>是否已对所属物理视图完成一次性接线。</summary>
        private bool _initialized;

        /// <summary>
        /// 绑定到行视图并渲染一条数据（每次重绑都会调用）。首次绑定时先做一次性接线。
        /// </summary>
        /// <param name="view">所属物理行视图（与本 Presenter 终身 1:1）。</param>
        /// <param name="data">本行数据。</param>
        /// <param name="index">数据下标。</param>
        internal void Bind(TView view, TData data, int index)
        {
            if (!_initialized)
            {
                View = view;
                OnInit();
                _initialized = true;
            }

            Data = data;
            Index = index;
            _cts = new CancellationTokenSource();
            OnBind(data);
        }

        /// <summary>解绑：取消本行异步、反订阅、清当前数据，等待下次复用（保留视图与接线）。</summary>
        internal void Unbind()
        {
            if (!IsBound)
            {
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            OnUnbind();
            Data = default;
            Index = -1;
        }

        /// <summary>
        /// 一次性接线：物理视图首次绑定时仅调一次，用于绑定行内子控件点击。
        /// <para>回调内读 <see cref="Data"/> 取当前行，<b>不要</b>在闭包里捕获 data（行会复用给别的数据，捕获即串数据）。</para>
        /// </summary>
        protected virtual void OnInit()
        {
        }

        /// <summary>每次重绑：渲染数据、挂靠 <see cref="BoundToken"/> 的异步。</summary>
        /// <param name="data">本行数据。</param>
        protected abstract void OnBind(TData data);

        /// <summary>每次回收：反订阅等清理（行内异步已随 <see cref="BoundToken"/> 自动取消）。默认空。</summary>
        protected virtual void OnUnbind()
        {
        }
    }
}
