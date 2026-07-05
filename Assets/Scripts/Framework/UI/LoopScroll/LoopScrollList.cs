using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 循环滚动列表控制器：支持竖/横/网格/变长多模式（布局委托 <see cref="ILoopLayout"/>）。
    /// <para>
    /// 实例化的行视图数量恒定为「可视项数 + 缓冲」，与数据量无关；滚动时把移出视口的项回收进
    /// <see cref="UIItemPool{TView}"/> 再复用并重新绑定，不做 Instantiate/Destroy。
    /// 业务通过 <see cref="ILoopListSource"/> 提供数据（变长模式需 <see cref="ILoopVariableSource"/>），
    /// 通过 <see cref="OnItemClicked"/> 接收行点击。
    /// </para>
    /// </summary>
    public sealed class LoopScrollList : UISubModule<LoopScrollListView>
    {
        /// <summary>当前数据源。</summary>
        private ILoopListSource _source;

        /// <summary>布局策略（按 View 配置在 OnInit 选定）。</summary>
        private ILoopLayout _layout;

        /// <summary>变长模式的逐项尺寸查询委托（定尺寸模式为 null）。</summary>
        private Func<int, float> _mainSizeOf;

        /// <summary>行视图复用池，模板取自 View 的 CellTemplate。</summary>
        private UIItemPool<UISubView> _pool;

        /// <summary>当前激活项：数据下标 → 行视图。</summary>
        private readonly Dictionary<int, UISubView> _active = new Dictionary<int, UISubView>(32);

        /// <summary>行视图 → 当前绑定的数据下标（供点击事件零分配反查）。</summary>
        private readonly Dictionary<UISubView, int> _cellIndex = new Dictionary<UISubView, int>(32);

        /// <summary>回收阶段复用的下标暂存（避免遍历中改字典并规避每帧分配）。</summary>
        private readonly List<int> _recycleScratch = new List<int>(32);

        /// <summary>当前数据条数。</summary>
        private int _count;

        /// <summary>当前激活窗口首下标。</summary>
        private int _firstActive;

        /// <summary>当前激活窗口末下标（空窗口为 -1）。</summary>
        private int _lastActive = -1;

        /// <summary>是否已订阅滚动回调。</summary>
        private bool _scrollHooked;

        /// <summary>程序化滚动 / 增删滑动动画时长（秒，使用非缩放时间）。</summary>
        private const float AnimDuration = 0.18f;

        /// <summary>当前程序化动画取消源（ScrollToIndex / 增删共用，新动画会打断旧动画）。</summary>
        private CancellationTokenSource _opAnimCts;

        /// <summary>行被点击时触发，参数为被点击行的数据下标（行视图需实现 <see cref="ILoopListClickable"/>）。</summary>
        public event Action<int> OnItemClicked;

        /// <summary>
        /// 创建循环列表控制器并绑定视图。
        /// </summary>
        /// <param name="view">Inspector 中显式配置的列表视图。</param>
        public LoopScrollList(LoopScrollListView view) : base(view)
        {
        }

        /// <summary>初始化：选布局策略、建池、订阅滚动回调。</summary>
        protected override void OnInit()
        {
            if (View.CellTemplate == null)
            {
                GameLog.Error("[LoopScrollList] CellTemplate 未配置，列表无法工作");
                return;
            }

            if (View.Content == null || View.Viewport == null || View.ScrollRect == null)
            {
                GameLog.Error("[LoopScrollList] ScrollRect/Viewport/Content 引用缺失");
                return;
            }

            _layout = View.VariableSize ? new VariableLoopLayout() : (ILoopLayout)new GridLoopLayout();

            // 物理实例只在创建时订阅一次点击；闭包捕获稳定的 cell 实例，避免每次绑定产生分配。
            _pool = new UIItemPool<UISubView>(
                template: View.CellTemplate,
                parent: View.Content,
                maxSize: 64,
                defaultCapacity: 8,
                onCreate: cell =>
                {
                    if (cell is ILoopListClickable clickable)
                    {
                        // 行视图只透出按钮引用，点击接线由控制器（本列表）在此统一完成，行视图自身不绑回调。
                        clickable.RowButton.AddClick(() => HandleCellClicked(cell));
                    }
                });

            View.ScrollRect.onValueChanged.AddListener(OnScrollChanged);
            _scrollHooked = true;
        }

        /// <summary>
        /// 设置数据源并从起始端全量重建。
        /// </summary>
        /// <param name="source">数据源，可为空表示清空。</param>
        public void SetSource(ILoopListSource source)
        {
            _source = source;
            ResetScrollToStart();
            Reload();
        }

        /// <summary>
        /// 重新读取数据条数并重建：重新测量、把当前滚动量夹回合法范围、刷新可视项。
        /// <para>条数不变只想改内容用 <see cref="RefreshVisible"/>。</para>
        /// </summary>
        public void Reload()
        {
            if (_pool == null || _layout == null)
            {
                return;
            }

            _count = _source?.Count ?? 0;
            _mainSizeOf = ResolveSizeProvider();

            _layout.Measure(_count, BuildConfig(), _mainSizeOf);
            SetContentSize(_layout.ContentSize);
            ClampScroll();

            if (View.EmptyHint != null)
            {
                View.EmptyHint.SetActive(_count <= 0);
            }

            PrewarmToWindow();
            RebuildVisible(force: true, rebindExisting: true);
        }

        /// <summary>
        /// 重新绑定当前所有可视项（数据内容变化但条数不变时使用，如好友在线状态刷新）。
        /// </summary>
        public void RefreshVisible()
        {
            if (_source == null)
            {
                return;
            }

            foreach (KeyValuePair<int, UISubView> kv in _active)
            {
                _source.BindCell(kv.Value, kv.Key);
            }
        }

        /// <summary>
        /// 刷新单项：若该项当前在可视区内则重新绑定，否则忽略（滚动到时会自然绑定）。
        /// </summary>
        /// <param name="index">数据下标。</param>
        public void RefreshItem(int index)
        {
            if (_source == null)
            {
                return;
            }

            if (_active.TryGetValue(index, out UISubView cell))
            {
                _source.BindCell(cell, index);
            }
        }

        /// <summary>
        /// 滚动到指定项：按对齐方式定位，可选缓出动画。
        /// </summary>
        /// <param name="index">目标项下标（自动夹紧到合法范围）。</param>
        /// <param name="align">对齐方式（起始 / 居中 / 末尾）。</param>
        /// <param name="animated">是否播放滚动动画。</param>
        public void ScrollToIndex(int index, LoopAlign align = LoopAlign.Start, bool animated = true)
        {
            if (_pool == null || _layout == null || _count <= 0)
            {
                return;
            }

            float targetMain = _layout.GetScrollOffset(index, align, ViewportMain());

            CancelOpAnim();
            View.ScrollRect.velocity = Vector2.zero;

            if (animated)
            {
                _opAnimCts = new CancellationTokenSource();
                AnimateScrollAsync(targetMain, _opAnimCts.Token).Forget();
            }
            else
            {
                SetScrollMain(targetMain);
                RebuildVisible(force: false);
            }
        }

        /// <summary>
        /// 在指定下标处插入一项：保持当前可视内容视觉不跳，可选邻项滑动动画。
        /// <para>调用前数据源 <see cref="ILoopListSource.Count"/> 必须已反映插入后的条数。</para>
        /// </summary>
        /// <param name="index">插入位置下标。</param>
        /// <param name="animated">是否播放滑动动画。</param>
        public void InsertItem(int index, bool animated = true)
        {
            if (_pool == null || _layout == null || _source == null)
            {
                return;
            }

            int newCount = _source.Count;
            if (newCount != _count + 1)
            {
                Reload();
                return;
            }

            if (index < 0)
            {
                index = 0;
            }
            else if (index > newCount - 1)
            {
                index = newCount - 1;
            }

            ApplyReflow(newCount, index, removed: false, animated);
        }

        /// <summary>
        /// 移除指定下标项：保持当前可视内容视觉不跳，可选邻项滑动动画。
        /// <para>调用前数据源 <see cref="ILoopListSource.Count"/> 必须已反映移除后的条数。</para>
        /// </summary>
        /// <param name="index">移除位置下标。</param>
        /// <param name="animated">是否播放滑动动画。</param>
        public void RemoveItem(int index, bool animated = true)
        {
            if (_pool == null || _layout == null || _source == null)
            {
                return;
            }

            int newCount = _source.Count;
            if (newCount != _count - 1)
            {
                Reload();
                return;
            }

            ApplyReflow(newCount, index, removed: true, animated);
        }

        /// <summary>释放：取消动画、反订阅滚动回调、清空激活项、销毁池。</summary>
        protected override void OnDispose()
        {
            CancelOpAnim();

            if (_scrollHooked && View != null && View.ScrollRect != null)
            {
                View.ScrollRect.onValueChanged.RemoveListener(OnScrollChanged);
            }
            _scrollHooked = false;

            RecycleAll();
            _pool?.Dispose();
            _pool = null;
            _source = null;
            _layout = null;
            _mainSizeOf = null;
            OnItemClicked = null;
        }

        /// <summary>滚动回调：按当前滚动量重铺（仅窗口跨行变化时实际改动）。</summary>
        /// <param name="normalizedPosition">归一化滚动位置（未使用，直接读内容锚点）。</param>
        private void OnScrollChanged(Vector2 normalizedPosition)
        {
            if (_count <= 0 || _pool == null)
            {
                return;
            }

            RebuildVisible(force: false);
        }

        /// <summary>
        /// 按当前滚动量计算可视窗口并重铺；force 为 true 时即使窗口未变也强制补齐（用于 Reload）。
        /// </summary>
        /// <param name="force">是否强制重铺。</param>
        /// <param name="rebindExisting">是否对仍在窗口内的留存项重定位并重绑（数据变更场景，如 Reload / 增删）。</param>
        private void RebuildVisible(bool force, bool rebindExisting = false)
        {
            _layout.GetVisibleRange(GetScrollMain(), ViewportMain(), out int first, out int last);

            if (!force && first == _firstActive && last == _lastActive)
            {
                return;
            }

            // 回收越界项。
            _recycleScratch.Clear();
            foreach (KeyValuePair<int, UISubView> kv in _active)
            {
                if (kv.Key < first || kv.Key > last)
                {
                    _recycleScratch.Add(kv.Key);
                }
            }

            for (int i = 0; i < _recycleScratch.Count; i++)
            {
                int idx = _recycleScratch[i];
                UISubView cell = _active[idx];
                _active.Remove(idx);
                _cellIndex.Remove(cell);
                RecycleCell(cell);
            }

            // 补齐区间内缺失项；数据变更场景下对留存项重定位并重绑。
            for (int idx = first; idx <= last; idx++)
            {
                if (_active.TryGetValue(idx, out UISubView existing))
                {
                    if (rebindExisting)
                    {
                        PositionCell(existing, idx);
                        _source.BindCell(existing, idx);
                    }

                    continue;
                }

                UISubView cell = _pool.Get();
                _active[idx] = cell;
                _cellIndex[cell] = idx;
                PositionCell(cell, idx);
                _source.BindCell(cell, idx);
            }

            _firstActive = first;
            _lastActive = last;
        }

        /// <summary>增删后的统一重排：测量、按锚点钉定保持滚动位置、快照式重铺，并按需播放滑动动画。</summary>
        /// <param name="newCount">变更后的数据条数。</param>
        /// <param name="editIndex">增删位置下标。</param>
        /// <param name="removed">true=移除，false=插入。</param>
        /// <param name="animated">是否播放邻项滑动动画。</param>
        private void ApplyReflow(int newCount, int editIndex, bool removed, bool animated)
        {
            CancelOpAnim();

            // 钉定锚点：记录首个可视项及其相对视口的主轴偏移，编辑后让同一数据项停在原屏幕位置。
            bool hasAnchor = _count > 0 && _lastActive >= _firstActive && _active.Count > 0;
            int anchorIndex = _firstActive;
            float anchorDelta = hasAnchor ? GetScrollMain() - _layout.GetItemMainStart(anchorIndex) : 0f;

            // 动画前捕获在场物理实例的当前锚点位置，作为滑动起点。
            Dictionary<UISubView, Vector2> fromPos = null;
            if (animated && _active.Count > 0)
            {
                fromPos = new Dictionary<UISubView, Vector2>(_active.Count);
                foreach (KeyValuePair<int, UISubView> kv in _active)
                {
                    fromPos[kv.Value] = ((RectTransform)kv.Value.transform).anchoredPosition;
                }
            }

            _count = newCount;
            _layout.Measure(_count, BuildConfig(), _mainSizeOf);
            SetContentSize(_layout.ContentSize);

            if (hasAnchor && _count > 0)
            {
                // 锚点数据项的新下标（增删点在其之前/之上时整体平移一格）。
                int newAnchor = anchorIndex;
                if (removed)
                {
                    if (editIndex < anchorIndex)
                    {
                        newAnchor = anchorIndex - 1;
                    }
                }
                else if (editIndex <= anchorIndex)
                {
                    newAnchor = anchorIndex + 1;
                }

                newAnchor = Mathf.Clamp(newAnchor, 0, _count - 1);
                float max = MaxScrollMain();
                SetScrollMain(Mathf.Clamp(_layout.GetItemMainStart(newAnchor) + anchorDelta, 0f, max));
            }
            else
            {
                ClampScroll();
            }

            if (View.EmptyHint != null)
            {
                View.EmptyHint.SetActive(_count <= 0);
            }

            RebuildVisible(force: true, rebindExisting: true);

            if (fromPos != null && fromPos.Count > 0)
            {
                _opAnimCts = new CancellationTokenSource();
                AnimateSlideAsync(fromPos, _opAnimCts.Token).Forget();
            }
        }

        /// <summary>把行视图定位到其下标对应的锚点位置。</summary>
        /// <param name="cell">行视图。</param>
        /// <param name="index">数据下标。</param>
        private void PositionCell(UISubView cell, int index)
        {
            var rect = (RectTransform)cell.transform;
            rect.anchoredPosition = _layout.GetAnchoredPosition(index);
        }

        /// <summary>回收单个行视图：先通知数据源解绑、再回调清理，最后还池。</summary>
        /// <param name="cell">行视图。</param>
        private void RecycleCell(UISubView cell)
        {
            if (_source is ILoopListRecycleAware aware)
            {
                aware.OnCellRecycled(cell);
            }

            if (cell is ILoopListCell lifecycle)
            {
                lifecycle.OnRecycled();
            }

            _pool.Release(cell);
        }

        /// <summary>回收所有激活项。</summary>
        private void RecycleAll()
        {
            foreach (KeyValuePair<int, UISubView> kv in _active)
            {
                if (_source is ILoopListRecycleAware aware)
                {
                    aware.OnCellRecycled(kv.Value);
                }

                if (kv.Value is ILoopListCell lifecycle)
                {
                    lifecycle.OnRecycled();
                }

                _pool?.Release(kv.Value);
            }

            _active.Clear();
            _cellIndex.Clear();
            _firstActive = 0;
            _lastActive = -1;
        }

        /// <summary>处理某行视图被点击：反查当前绑定下标并外抛。</summary>
        /// <param name="cell">被点击的行视图。</param>
        private void HandleCellClicked(UISubView cell)
        {
            if (_cellIndex.TryGetValue(cell, out int index))
            {
                OnItemClicked?.Invoke(index);
            }
        }

        /// <summary>缓出动画把内容根滚动到目标主轴偏移，并逐帧重铺可视项。</summary>
        /// <param name="targetMain">目标主轴滚动偏移。</param>
        /// <param name="token">取消令牌。</param>
        private async UniTaskVoid AnimateScrollAsync(float targetMain, CancellationToken token)
        {
            float startMain = GetScrollMain();
            float elapsed = 0f;
            while (elapsed < AnimDuration)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(elapsed / AnimDuration);
                k = 1f - (1f - k) * (1f - k); // 缓出
                SetScrollMain(Mathf.Lerp(startMain, targetMain, k));
                RebuildVisible(force: false);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            if (!token.IsCancellationRequested)
            {
                SetScrollMain(targetMain);
                RebuildVisible(force: false);
            }
        }

        /// <summary>缓出动画把在场旧实例从捕获的旧位置滑到其当前下标对应的新位置。</summary>
        /// <param name="fromPos">物理实例 → 滑动起点锚点位置。</param>
        /// <param name="token">取消令牌。</param>
        private async UniTaskVoid AnimateSlideAsync(Dictionary<UISubView, Vector2> fromPos, CancellationToken token)
        {
            float elapsed = 0f;
            while (elapsed < AnimDuration)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(elapsed / AnimDuration);
                k = 1f - (1f - k) * (1f - k); // 缓出
                foreach (KeyValuePair<int, UISubView> kv in _active)
                {
                    if (!fromPos.TryGetValue(kv.Value, out Vector2 from))
                    {
                        continue; // 新出现的项不参与滑动
                    }

                    Vector2 to = _layout.GetAnchoredPosition(kv.Key);
                    ((RectTransform)kv.Value.transform).anchoredPosition = Vector2.Lerp(from, to, k);
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            if (!token.IsCancellationRequested)
            {
                // 收尾贴到目标，消除插值残差。
                foreach (KeyValuePair<int, UISubView> kv in _active)
                {
                    PositionCell(kv.Value, kv.Key);
                }
            }
        }

        /// <summary>取消并释放当前程序化动画。</summary>
        private void CancelOpAnim()
        {
            if (_opAnimCts == null)
            {
                return;
            }

            _opAnimCts.Cancel();
            _opAnimCts.Dispose();
            _opAnimCts = null;
        }

        /// <summary>按 View 配置与模板尺寸组装布局参数。</summary>
        /// <returns>布局配置。</returns>
        private LoopLayoutConfig BuildConfig()
        {
            var rect = (RectTransform)View.CellTemplate.transform;
            float width = rect.rect.width;
            float height = rect.rect.height;
            bool vertical = View.Axis == LoopAxis.Vertical;
            if ((vertical ? height : width) <= 0f)
            {
                GameLog.Warning("[LoopScrollList] 行模板主轴尺寸为 0，请检查模板 RectTransform 尺寸");
            }

            return new LoopLayoutConfig
            {
                Axis = View.Axis,
                CrossCount = View.VariableSize ? 1 : View.CrossAxisCount,
                CellMain = vertical ? height : width,
                CellCross = vertical ? width : height,
                SpacingMain = View.SpacingMain,
                SpacingCross = View.SpacingCross,
                PadStart = View.PadStart,
                PadEnd = View.PadEnd,
                PadCrossStart = View.PadCrossStart,
                Buffer = View.Buffer,
            };
        }

        /// <summary>解析变长尺寸查询委托：变长模式且数据源支持时返回查询，否则 null。</summary>
        /// <returns>逐项主轴尺寸查询，或 null。</returns>
        private Func<int, float> ResolveSizeProvider()
        {
            if (!View.VariableSize)
            {
                return null;
            }

            if (_source is ILoopVariableSource variableSource)
            {
                return variableSource.GetItemSize;
            }

            if (_source != null)
            {
                GameLog.Error("[LoopScrollList] 配置为变长模式但数据源未实现 ILoopVariableSource，项尺寸按 0 处理");
            }

            return null;
        }

        /// <summary>预热到当前可视窗口大小，避免首次滚动现 new。</summary>
        private void PrewarmToWindow()
        {
            _layout.GetVisibleRange(GetScrollMain(), ViewportMain(), out int first, out int last);
            if (last < first)
            {
                return;
            }

            // 多预留一个交叉轴行，覆盖滚动方向首次进入的下一行。
            _pool.Prewarm(last - first + 1 + View.CrossAxisCount);
        }

        /// <summary>视口主轴尺寸（竖向=高 / 横向=宽）。</summary>
        /// <returns>视口主轴尺寸（像素）。</returns>
        private float ViewportMain()
        {
            Rect rect = View.Viewport.rect;
            return View.Axis == LoopAxis.Vertical ? rect.height : rect.width;
        }

        /// <summary>读取当前主轴滚动偏移（≥0，越大越靠近末尾）。</summary>
        /// <returns>主轴滚动偏移。</returns>
        private float GetScrollMain()
        {
            Vector2 pos = View.Content.anchoredPosition;
            return View.Axis == LoopAxis.Vertical ? pos.y : -pos.x;
        }

        /// <summary>设置主轴滚动偏移。</summary>
        /// <param name="main">主轴滚动偏移。</param>
        private void SetScrollMain(float main)
        {
            Vector2 pos = View.Content.anchoredPosition;
            if (View.Axis == LoopAxis.Vertical)
            {
                pos.y = main;
            }
            else
            {
                pos.x = -main;
            }

            View.Content.anchoredPosition = pos;
        }

        /// <summary>设置内容根主轴尺寸。</summary>
        /// <param name="size">主轴尺寸。</param>
        private void SetContentSize(float size)
        {
            Vector2 sd = View.Content.sizeDelta;
            if (View.Axis == LoopAxis.Vertical)
            {
                sd.y = size;
            }
            else
            {
                sd.x = size;
            }

            View.Content.sizeDelta = sd;
        }

        /// <summary>主轴最大滚动量。</summary>
        /// <returns>最大滚动量（≥0）。</returns>
        private float MaxScrollMain()
        {
            float max = _layout.ContentSize - ViewportMain();
            return max > 0f ? max : 0f;
        }

        /// <summary>把主轴滚动量夹回 [0, 最大]，并停止惯性。</summary>
        private void ClampScroll()
        {
            SetScrollMain(Mathf.Clamp(GetScrollMain(), 0f, MaxScrollMain()));
            View.ScrollRect.velocity = Vector2.zero;
        }

        /// <summary>把内容根复位到起始端并停止惯性。</summary>
        private void ResetScrollToStart()
        {
            SetScrollMain(0f);
            View.ScrollRect.velocity = Vector2.zero;
        }
    }

    /// <summary>
    /// 行视图可选实现的点击上报接口。
    /// <para>行视图只透出整行点击按钮引用，由 <see cref="LoopScrollList"/> 在创建物理实例时统一绑定点击，
    /// 列表据此补当前下标并通过 <see cref="LoopScrollList.OnItemClicked"/> 外抛；行视图自身不绑回调、不持有点击逻辑。
    /// 不实现本接口则列表不产生行点击事件。</para>
    /// </summary>
    public interface ILoopListClickable
    {
        /// <summary>整行点击按钮（Inspector 显式配置，仅透出引用供控制器绑定）。</summary>
        Button RowButton { get; }
    }
}
