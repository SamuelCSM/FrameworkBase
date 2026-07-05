using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 通用 Tab 组视图：按显示顺序持有所有 Tab 单项引用，只做引用容器。
    /// </summary>
    public sealed class UITabGroupView : UISubView
    {
        [Header("Tab 单项（按显示顺序，索引即对外选中下标）")]
        [SerializeField] private TabItemView[] items;

        /// <summary>Tab 单项数组，供 <see cref="UITabGroup"/> 绑定与换皮。</summary>
        public TabItemView[] Items => items;
    }
}
