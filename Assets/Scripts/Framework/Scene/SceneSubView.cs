using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景子对象 View 基类，只负责持有 Inspector 序列化引用。
    /// </summary>
    /// <remarks>
    /// 适用于嵌在场景预制内部的子控件，或运行时加载到场景节点下的子预制。
    /// </remarks>
    public abstract class SceneSubView : MonoBehaviour
    {
    }
}
