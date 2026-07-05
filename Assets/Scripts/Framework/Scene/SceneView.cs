using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景对象 View 基类，只负责持有 Inspector 序列化引用。
    /// </summary>
    /// <remarks>
    /// 场景中稳定存在的业务预制根节点应继承该类型，并把业务逻辑放到对应的 <see cref="SceneObject{TView}"/> 子类中。
    /// </remarks>
    public abstract class SceneView : MonoBehaviour
    {
    }
}
