using System;

namespace Framework
{
    /// <summary>
    /// Backward-compatible entry for the framework object pool.
    /// New code should prefer <see cref="Pooling.ObjectPool{T}"/>; this type remains to avoid breaking existing callers.
    /// </summary>
    public class ObjectPool<T> : Pooling.ObjectPool<T> where T : class, new()
    {
        /// <summary>
        /// Create a general-purpose object pool.
        /// </summary>
        /// <param name="createFunc">Object factory. Defaults to <c>new T()</c>.</param>
        /// <param name="onGet">Callback invoked after checkout.</param>
        /// <param name="onRelease">Callback invoked before storing the object back in the pool.</param>
        /// <param name="defaultCapacity">Initial stack capacity.</param>
        /// <param name="maxSize">Maximum number of objects retained in the pool.</param>
        /// <param name="checkDuplicate">Whether duplicate releases are rejected.</param>
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
