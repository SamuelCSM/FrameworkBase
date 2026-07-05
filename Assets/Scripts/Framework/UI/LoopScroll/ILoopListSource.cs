namespace Framework
{
    /// <summary>
    /// 循环滚动列表的数据源（Adapter）。
    /// <para>
    /// 列表本身不认识任何业务类型，只通过本接口拿条数、把第 index 条数据绑定到复用出来的行视图上。
    /// 实现方在 <see cref="BindCell"/> 内把 <see cref="UISubView"/> 向下转型为自己的具体行视图后填充数据。
    /// </para>
    /// </summary>
    public interface ILoopListSource
    {
        /// <summary>当前数据条数。</summary>
        int Count { get; }

        /// <summary>
        /// 把第 index 条数据绑定到给定行视图。
        /// <para>该方法会在重铺与刷新的热路径上被频繁调用，实现内禁止分配、禁止隐式查找组件。</para>
        /// </summary>
        /// <param name="cell">从池中复用出来的行视图（实现方按约定向下转型）。</param>
        /// <param name="index">数据下标（保证落在 [0, Count) 内）。</param>
        void BindCell(UISubView cell, int index);
    }

    /// <summary>
    /// 变长主轴尺寸数据源：在 <see cref="ILoopListSource"/> 基础上提供逐项主轴尺寸。
    /// <para>仅当列表配置为变长模式（<c>LoopScrollListView.VariableSize</c>）时需要；定尺寸模式无需实现。</para>
    /// </summary>
    public interface ILoopVariableSource : ILoopListSource
    {
        /// <summary>
        /// 取第 index 项的主轴尺寸（竖向=高 / 横向=宽，像素）。
        /// <para>会在 <c>Measure</c> 阶段对全部条目调用一次，实现应为 O(1) 且无分配。</para>
        /// </summary>
        /// <param name="index">数据下标。</param>
        /// <returns>主轴尺寸（像素）。</returns>
        float GetItemSize(int index);
    }

    /// <summary>
    /// 行视图可选实现的回收回调接口。
    /// <para>
    /// 行被移出可视区放回池前，列表会调用 <see cref="OnRecycled"/>，
    /// 供行视图停止协程/动画、反订阅事件、断开业务引用，避免复用时串数据。
    /// </para>
    /// </summary>
    public interface ILoopListCell
    {
        /// <summary>行被回收进池之前调用，用于清理展示期状态。</summary>
        void OnRecycled();
    }

    /// <summary>
    /// 数据源可选实现的回收通知接口。
    /// <para>
    /// 行视图被回收进池前，列表会调用 <see cref="OnCellRecycled"/>，供「Presenter 驱动」的数据源
    /// （如 <c>LoopPresenterSource</c>）解绑该行 Presenter——取消异步、反订阅、清当前数据。
    /// 普通展示型数据源无需实现；行内清理走行视图的 <see cref="ILoopListCell"/> 即可。
    /// </para>
    /// </summary>
    public interface ILoopListRecycleAware
    {
        /// <summary>
        /// 行视图被回收前调用。
        /// </summary>
        /// <param name="cell">即将回收的行视图（实现方按约定向下转型）。</param>
        void OnCellRecycled(UISubView cell);
    }
}
