using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 加载型子面板宿主。
    /// <para>
    /// 宿主负责协调子面板根对象与父窗口内部容器，
    /// 创建对应的 <see cref="UISubPanel{TView}"/> 逻辑实例，
    /// 并在父窗口关闭或切换内容时统一执行 Hide、Dispose 和资源释放。
    /// 具体加载和释放策略由子类实现。
    /// </para>
    /// </summary>
    /// <typeparam name="TPanel">子面板逻辑类型。</typeparam>
    public abstract class UISubPanelHost<TPanel> : System.IDisposable
        where TPanel : UISubPanelCore, new()
    {
        /// <summary>当前加载的实例 key。</summary>
        private string panelKey;

        /// <summary>子面板挂载的父节点。</summary>
        private Transform parent;

        /// <summary>当前加载出的子面板根对象。</summary>
        private GameObject panelObject;

        /// <summary>当前创建的子面板逻辑实例。</summary>
        private TPanel panel;

        /// <summary>
        /// 创建 UI 加载型子面板宿主。
        /// </summary>
        /// <param name="key">子面板实例 key，具体含义由子类加载策略解释。</param>
        /// <param name="parent">父窗口内部的挂载容器。</param>
        protected UISubPanelHost(string key, Transform parent)
        {
            panelKey = key;
            this.parent = parent;
        }

        /// <summary>当前是否已经加载出面板实例。</summary>
        public bool IsLoaded => panel != null && panelObject != null;

        /// <summary>当前子面板逻辑实例，未加载时为 null。</summary>
        public TPanel Panel => panel;

        /// <summary>当前子面板根对象，未加载时为 null。</summary>
        public GameObject PanelObject => panelObject;

        /// <summary>
        /// 加载子面板但不显示。
        /// </summary>
        /// <returns>加载成功时返回子面板逻辑实例，否则返回 null。</returns>
        public async UniTask<TPanel> LoadAsync()
        {
            if (string.IsNullOrEmpty(panelKey))
            {
                Logger.Error($"[UISubPanelHost] LoadAsync 失败，key 为空: {typeof(TPanel).Name}");
                return null;
            }

            if (parent == null)
            {
                Logger.Error($"[UISubPanelHost] LoadAsync 失败，父节点为空: {typeof(TPanel).Name}");
                return null;
            }

            if (IsLoaded)
            {
                return panel;
            }

            Dispose();

            panelObject = await LoadGameObjectAsync(panelKey, parent);

            if (panelObject == null)
            {
                ResetRuntimeState();
                Logger.Error($"[UISubPanelHost] 加载子面板失败: {typeof(TPanel).Name}, Key={panelKey}");
                return null;
            }

            panel = new TPanel();

            UISubView view = panelObject.GetComponent(panel.ViewType) as UISubView;
            if (view == null)
            {
                Logger.Error($"[UISubPanelHost] 子面板缺少视图组件: {panel.ViewType.Name}, Key={panelKey}");
                panel.Dispose();
                panel = null;
                ReleasePanelObject();
                ResetRuntimeState();
                return null;
            }

            panel.Initialize(view);
            if (!panel.IsInitialized)
            {
                panel.Dispose();
                panel = null;
                ReleasePanelObject();
                ResetRuntimeState();
                return null;
            }

            panelObject.SetActive(false);
            return panel;
        }

        /// <summary>
        /// 加载并显示子面板。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        /// <returns>显示成功时返回子面板逻辑实例，否则返回 null。</returns>
        public async UniTask<TPanel> ShowAsync(object userData = null)
        {
            TPanel loadedPanel = await LoadAsync();
            loadedPanel?.Show(userData);
            return loadedPanel;
        }

        /// <summary>
        /// 隐藏当前子面板。
        /// </summary>
        public void Hide()
        {
            panel?.Hide();
        }

        /// <summary>
        /// 释放当前子面板与资源实例。
        /// </summary>
        public void Dispose()
        {
            panel?.Dispose();
            panel = null;
            ReleasePanelObject();
            ResetRuntimeState();
        }

        /// <summary>
        /// 释放当前加载出的 GameObject 实例。
        /// </summary>
        private void ReleasePanelObject()
        {
            if (panelObject == null)
            {
                return;
            }

            ReleaseGameObject(panelObject);
        }

        /// <summary>
        /// 重置宿主内部状态。
        /// </summary>
        private void ResetRuntimeState()
        {
            panelObject = null;
        }

        /// <summary>
        /// 加载子面板根对象。
        /// </summary>
        /// <param name="key">子面板实例 key，具体含义由子类加载策略解释。</param>
        /// <param name="parent">父窗口内部的挂载容器。</param>
        /// <returns>加载成功时返回子面板根对象，否则返回 null。</returns>
        protected abstract UniTask<GameObject> LoadGameObjectAsync(string key, Transform parent);

        /// <summary>
        /// 释放子面板根对象。
        /// </summary>
        /// <param name="instance">需要释放的子面板根对象。</param>
        protected abstract void ReleaseGameObject(GameObject instance);
    }

    /// <summary>
    /// 基于 <see cref="ResourceManager"/> 的 Addressables UI 子面板宿主。
    /// </summary>
    /// <typeparam name="TPanel">子面板逻辑类型。</typeparam>
    public class AddressableUISubPanelHost<TPanel> : UISubPanelHost<TPanel>
        where TPanel : UISubPanelCore, new()
    {
        /// <summary>
        /// 创建 Addressables UI 子面板宿主。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址。</param>
        /// <param name="parent">父窗口内部的挂载容器。</param>
        public AddressableUISubPanelHost(string key, Transform parent) : base(key, parent)
        {
        }

        /// <summary>
        /// 通过 Addressables 实例化子面板根对象。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址。</param>
        /// <param name="parent">父窗口内部的挂载容器。</param>
        /// <returns>加载成功时返回子面板根对象，否则返回 null。</returns>
        protected override async UniTask<GameObject> LoadGameObjectAsync(string key, Transform parent)
        {
            if (GameEntry.Resource == null)
            {
                Logger.Error($"[AddressableUISubPanelHost] 加载子面板失败，ResourceManager 未就绪: {typeof(TPanel).Name}, Key={key}");
                return null;
            }

            return await GameEntry.Resource.InstantiateAsync(key, parent);
        }

        /// <summary>
        /// 释放 Addressables 创建的子面板根对象。
        /// </summary>
        /// <param name="instance">需要释放的子面板根对象。</param>
        protected override void ReleaseGameObject(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (GameEntry.Resource != null)
            {
                GameEntry.Resource.ReleaseInstance(instance);
                return;
            }

            UnityEngine.Object.Destroy(instance);
        }
    }
}
