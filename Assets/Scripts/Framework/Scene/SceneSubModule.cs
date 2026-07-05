namespace Framework
{
    /// <summary>
    /// 场景内嵌子对象控制类，和 <see cref="SceneSubView"/> 一一对应。
    /// </summary>
    /// <typeparam name="TView">绑定的场景子 View 类型。</typeparam>
    public abstract class SceneSubModule<TView> : SceneControllerBase<TView> where TView : SceneSubView
    {
        /// <summary>
        /// 创建场景内嵌子对象控制类。
        /// </summary>
        /// <param name="view">场景内嵌子 View。</param>
        protected SceneSubModule(TView view) : base(view)
        {
        }
    }
}
