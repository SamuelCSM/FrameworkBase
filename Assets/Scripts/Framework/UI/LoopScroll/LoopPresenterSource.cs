using System.Collections.Generic;

namespace Framework
{
    /// <summary>
    /// 由 Presenter 驱动的循环列表数据源：为每个物理行视图常驻一个 <typeparamref name="TPresenter"/>
    /// （跟随复用，不随回收销毁），回收时仅解绑数据 / 异步 / 订阅。
    /// <para>
    /// 适用于行内含多按钮 / 异步 / 单行订阅等较重交互的列表；行仅「展示 + 整行点击」时用普通
    /// <see cref="ILoopListSource"/> 即可，不必引入本类。子类通常只需声明三个类型参数、无需再写绑定逻辑。
    /// </para>
    /// </summary>
    /// <typeparam name="TPresenter">行 Presenter 类型。</typeparam>
    /// <typeparam name="TView">行视图类型。</typeparam>
    /// <typeparam name="TData">行数据类型。</typeparam>
    public abstract class LoopPresenterSource<TPresenter, TView, TData>
        : ILoopListSource, ILoopListRecycleAware
        where TPresenter : LoopCellPresenter<TView, TData>, new()
        where TView : UISubView
    {
        /// <summary>当前数据列表（外部提供，可为空）。</summary>
        private IReadOnlyList<TData> _items;

        /// <summary>物理行视图 → 其常驻 Presenter（视图一生一个，总数 = 可视行 + 缓冲）。</summary>
        private readonly Dictionary<TView, TPresenter> _presenters = new Dictionary<TView, TPresenter>(16);

        /// <summary>当前数据条数。</summary>
        public int Count => _items?.Count ?? 0;

        /// <summary>
        /// 替换数据源；调用方随后需触发列表重铺（<c>SetSource</c>/<c>Reload</c>）。
        /// </summary>
        /// <param name="items">数据列表，可为空表示清空。</param>
        public void SetItems(IReadOnlyList<TData> items)
        {
            _items = items;
        }

        /// <summary>
        /// 按下标取数据（供页面在收到行点击下标后回查）。
        /// </summary>
        /// <param name="index">数据下标。</param>
        /// <returns>命中返回数据，越界返回默认值。</returns>
        public TData GetItem(int index)
        {
            if (_items == null || index < 0 || index >= _items.Count)
            {
                return default;
            }

            return _items[index];
        }

        /// <summary>
        /// 把第 index 条数据绑定到行视图：取 / 建该物理视图的常驻 Presenter 后绑定。
        /// </summary>
        /// <param name="cell">复用出来的行视图。</param>
        /// <param name="index">数据下标。</param>
        public void BindCell(UISubView cell, int index)
        {
            var view = (TView)cell;
            if (!_presenters.TryGetValue(view, out TPresenter presenter))
            {
                presenter = new TPresenter();
                _presenters[view] = presenter;
            }

            presenter.Bind(view, _items[index], index);
        }

        /// <summary>
        /// 行被回收：解绑对应 Presenter（保留 视图↔Presenter 配对供下次复用）。
        /// </summary>
        /// <param name="cell">即将回收的行视图。</param>
        void ILoopListRecycleAware.OnCellRecycled(UISubView cell)
        {
            if (_presenters.TryGetValue((TView)cell, out TPresenter presenter))
            {
                presenter.Unbind();
            }
        }
    }
}
