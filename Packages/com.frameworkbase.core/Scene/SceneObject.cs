namespace Framework
{
    /// <summary>
    /// 场景预制根对象控制类，和 <see cref="SceneView"/> 一一对应。
    /// </summary>
    /// <typeparam name="TView">绑定的场景 View 类型。</typeparam>
    public abstract class SceneObject<TView> : SceneControllerBase<TView> where TView : SceneView
    {
        /// <summary>
        /// 创建场景预制根对象控制类。
        /// </summary>
        /// <param name="view">场景预制根 View。</param>
        protected SceneObject(TView view) : base(view)
        {
        }
    }
}
