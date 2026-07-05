namespace Framework
{
    /// <summary>
    /// 对象池对象接口
    /// 实现此接口的对象可以被对象池管理，并在回收时自动重置状态
    /// </summary>
    public interface IPoolable
    {
        /// <summary>
        /// 对象从对象池中取出时调用
        /// 用于初始化对象状态
        /// </summary>
        void OnSpawn();

        /// <summary>
        /// 对象回收到对象池时调用
        /// 用于重置对象状态，清理引用
        /// </summary>
        void OnRecycle();
    }
}
