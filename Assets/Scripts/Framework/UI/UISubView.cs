using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 子视图基类。
    /// <para>
    /// 子视图只负责持有 Inspector 上拖拽的 Button、TMP_Text、Transform、GameObject 等引用；
    /// 业务逻辑应放在 <see cref="UISubModule{TView}"/> 或 <see cref="UISubPanel{TView}"/> 子类中。
    /// </para>
    /// </summary>
    public abstract class UISubView : MonoBehaviour { }
}
