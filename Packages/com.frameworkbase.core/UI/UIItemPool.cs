using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 子视图条目缓存池。
    /// <para>
    /// 适用于窗口内部从模板克隆的重复 UI 条目，例如 Toast、排行榜行、聊天气泡、背包格子。
    /// 该池只管理已有模板的实例化与回收，不负责 Addressables 加载，也不接管窗口生命周期。
    /// </para>
    /// </summary>
    /// <typeparam name="TView">条目子视图类型。</typeparam>
    public sealed class UIItemPool<TView> : IDisposable where TView : UISubView
    {
        /// <summary>条目模板，通常来自窗口 Prefab 内的隐藏节点。</summary>
        private readonly TView template;

        /// <summary>条目挂载父节点。</summary>
        private readonly Transform parent;

        /// <summary>缓存池栈。</summary>
        private readonly Stack<TView> pooledItems;

        /// <summary>当前借出的条目集合，用于防止重复回收和误回收。</summary>
        private readonly HashSet<TView> activeItems = new HashSet<TView>();

        /// <summary>最大缓存数量。</summary>
        private readonly int maxSize;

        /// <summary>条目创建后回调，用于补齐非 Inspector 引用或一次性配置。</summary>
        private readonly Action<TView> onCreate;

        /// <summary>条目借出后回调，用于重置显示状态。</summary>
        private readonly Action<TView> onGet;

        /// <summary>条目回收前回调，用于停止动画和清理临时状态。</summary>
        private readonly Action<TView> onRelease;

        /// <summary>是否已经释放。</summary>
        private bool disposed;

        /// <summary>
        /// 创建 UI 子视图条目缓存池。
        /// </summary>
        /// <param name="template">条目模板，通常为窗口 Prefab 内隐藏节点。</param>
        /// <param name="parent">条目挂载父节点。</param>
        /// <param name="maxSize">最大缓存数量。</param>
        /// <param name="defaultCapacity">初始栈容量。</param>
        /// <param name="onCreate">条目创建后回调。</param>
        /// <param name="onGet">条目借出后回调。</param>
        /// <param name="onRelease">条目回收前回调。</param>
        public UIItemPool(
            TView template,
            Transform parent,
            int maxSize = 32,
            int defaultCapacity = 4,
            Action<TView> onCreate = null,
            Action<TView> onGet = null,
            Action<TView> onRelease = null)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            this.template = template;
            this.parent = parent;
            this.maxSize = Mathf.Max(0, maxSize);
            this.onCreate = onCreate;
            this.onGet = onGet;
            this.onRelease = onRelease;
            pooledItems = new Stack<TView>(Mathf.Max(0, defaultCapacity));

            this.template.gameObject.SetActive(false);
        }

        /// <summary>当前池内可复用条目数量。</summary>
        public int CountInPool => pooledItems.Count;

        /// <summary>当前已借出条目数量。</summary>
        public int CountActive => activeItems.Count;

        /// <summary>
        /// 预创建指定数量条目。
        /// </summary>
        /// <param name="count">预创建数量。</param>
        public void Prewarm(int count)
        {
            EnsureNotDisposed();

            int targetCount = Mathf.Min(Mathf.Max(0, count), maxSize);
            while (pooledItems.Count < targetCount)
            {
                TView item = CreateItem();
                item.gameObject.SetActive(false);
                pooledItems.Push(item);
            }
        }

        /// <summary>
        /// 借出一个条目。
        /// </summary>
        /// <returns>可用条目。</returns>
        public TView Get()
        {
            EnsureNotDisposed();

            TView item = null;
            while (pooledItems.Count > 0 && item == null)
            {
                item = pooledItems.Pop();
            }

            if (item == null)
            {
                item = CreateItem();
            }

            item.transform.SetParent(parent, false);
            item.gameObject.SetActive(true);
            activeItems.Add(item);
            onGet?.Invoke(item);
            return item;
        }

        /// <summary>
        /// 回收一个条目。
        /// </summary>
        /// <param name="item">待回收条目。</param>
        public void Release(TView item)
        {
            if (item == null)
            {
                return;
            }

            if (disposed)
            {
                UnityEngine.Object.Destroy(item.gameObject);
                return;
            }

            if (!activeItems.Remove(item))
            {
                GameLog.Warning($"[UIItemPool] 尝试回收未借出的条目: {typeof(TView).Name}");
                return;
            }

            onRelease?.Invoke(item);

            if (pooledItems.Count >= maxSize)
            {
                UnityEngine.Object.Destroy(item.gameObject);
                return;
            }

            item.transform.SetParent(parent, false);
            item.gameObject.SetActive(false);
            pooledItems.Push(item);
        }

        /// <summary>
        /// 收缩池内缓存数量。
        /// </summary>
        /// <param name="targetSize">目标缓存数量。</param>
        public void Shrink(int targetSize)
        {
            EnsureNotDisposed();

            int normalizedTarget = Mathf.Max(0, targetSize);
            while (pooledItems.Count > normalizedTarget)
            {
                TView item = pooledItems.Pop();
                if (item != null)
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
        }

        /// <summary>
        /// 清空池内和借出的所有条目，不销毁模板。
        /// </summary>
        public void Clear()
        {
            if (disposed)
            {
                return;
            }

            while (pooledItems.Count > 0)
            {
                TView item = pooledItems.Pop();
                if (item != null)
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }

            foreach (TView item in activeItems)
            {
                if (item != null)
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }

            activeItems.Clear();
        }

        /// <summary>
        /// 释放缓存池。
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            Clear();
            disposed = true;
        }

        /// <summary>
        /// 创建新条目实例。
        /// </summary>
        /// <returns>新条目实例。</returns>
        private TView CreateItem()
        {
            TView item = UnityEngine.Object.Instantiate(template, parent);
            item.name = template.name.Replace("(Clone)", string.Empty);
            onCreate?.Invoke(item);
            return item;
        }

        /// <summary>
        /// 确认缓存池未释放。
        /// </summary>
        /// <exception cref="ObjectDisposedException">缓存池已释放时抛出。</exception>
        private void EnsureNotDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(UIItemPool<TView>));
            }
        }
    }
}
