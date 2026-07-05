using System;

namespace Framework
{
    /// <summary>
    /// 通用 Tab 组逻辑（单选）：管理"当前选中下标"、绑定各项点击、对外抛选中变更事件。
    /// 领域无关——只认下标，不认识业务模式/页面；调用方拿到下标自行映射。
    /// 可复用于分段开关、底部导航、分类页签等任意单选 Tab 场景。
    /// </summary>
    public sealed class UITabGroup : UISubModule<UITabGroupView>
    {
        /// <summary>当前选中下标，未选中为 -1。</summary>
        public int SelectedIndex { get; private set; } = -1;

        /// <summary>选中项发生变更时回调（仅用户点击或 notify=true 的 Select 触发）。</summary>
        public event Action<int> OnSelectionChanged;

        /// <summary>
        /// 创建 Tab 组并绑定各项点击。
        /// </summary>
        /// <param name="view">Tab 组视图。</param>
        public UITabGroup(UITabGroupView view) : base(view)
        {
        }

        /// <summary>
        /// 初始化时为每个 Tab 单项绑定点击。
        /// </summary>
        protected override void OnInit()
        {
            TabItemView[] items = View.Items;
            if (items == null)
            {
                return;
            }

            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                items[i]?.Button?.AddClick(() => Select(index));
            }
        }

        /// <summary>
        /// 选中指定下标：刷新各项皮肤，并按需抛出变更事件。
        /// </summary>
        /// <param name="index">目标下标。</param>
        /// <param name="notify">是否触发 <see cref="OnSelectionChanged"/>；初始化/外部回灌选中态时传 false 避免误触发。</param>
        public void Select(int index, bool notify = true)
        {
            TabItemView[] items = View?.Items;
            if (items == null || index < 0 || index >= items.Length || index == SelectedIndex)
            {
                return;
            }

            SelectedIndex = index;
            for (int i = 0; i < items.Length; i++)
            {
                items[i]?.SetSelected(i == index);
            }

            if (notify)
            {
                OnSelectionChanged?.Invoke(index);
            }
        }

        /// <summary>
        /// 释放时清空事件订阅。
        /// </summary>
        protected override void OnDispose()
        {
            OnSelectionChanged = null;
        }
    }
}
