using System;

namespace Framework
{
    /// <summary>
    /// 框架对象池的兼容入口。
    /// 新代码建议优先使用 <see cref="Pooling.ObjectPool{T}"/>；此类型保留用于避免破坏已有调用方。
    /// </summary>
    public class ObjectPool<T> : Pooling.ObjectPool<T> where T : class, new()
    {
        /// <summary>
        /// 创建通用对象池。
        /// </summary>
        /// <param name="createFunc">对象创建工厂，默认使用 <c>new T()</c>。</param>
        /// <param name="onGet">对象取出后的回调。</param>
        /// <param name="onRelease">对象放回池前的回调。</param>
        /// <param name="defaultCapacity">对象池初始容量。</param>
        /// <param name="maxSize">对象池最多保留的对象数量。</param>
        /// <param name="checkDuplicate">是否拒绝重复回收。</param>
        public ObjectPool(
            Func<T> createFunc = null,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            int defaultCapacity = 10,
            int maxSize = 1000,
            bool checkDuplicate = true)
            : base(createFunc ?? (() => new T()), onGet, onRelease, defaultCapacity, maxSize, checkDuplicate)
        {
        }
    }
}
