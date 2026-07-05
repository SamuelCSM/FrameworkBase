using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Framework
{
    /// <summary>
    /// UI 基础设施引导组件（场景放置，跨场景存活）。
    ///
    /// 职责：
    ///   1. 在 Inspector 中持有 UIRoot Canvas 与 EventSystem，参数对设计师可见可调
    ///   2. Awake 时为每个 UILayer 创建一个子 Canvas，并调用 DontDestroyOnLoad
    ///   3. 向 UIManager 暴露各层级 Canvas，UIManager 不再自行创建 UI 节点
    ///
    /// 搭建方式（在 Launch 场景中创建 UIBootstrap Prefab）：
    ///   UIBootstrap  (挂载本脚本)
    ///   ├── EventSystem   — EventSystem + StandaloneInputModule，负责 UGUI 点击与滚动输入
    ///   └── UIRoot        — Canvas 组件，Render Mode = Screen Space - Overlay（Awake 中强制）
    ///                       CanvasScaler 在此 GameObject 上配置参考分辨率等参数
    ///
    /// Awake 后自动生成（无需手动创建）：
    ///   UIRoot
    ///   ├── Canvas_Background  (SortOrder   0)
    ///   ├── Canvas_Normal      (SortOrder 100)
    ///   ├── Canvas_Popup       (SortOrder 200)
    ///   ├── Canvas_Top         (SortOrder 300)
    ///   └── Canvas_System      (SortOrder 400)  ← LoadingScreen / ReconnectPanel 挂载于此
    /// </summary>
    [DisallowMultipleComponent]
    public class UIBootstrap : MonoBehaviour
    {
        [Tooltip("UIRoot Canvas（渲染模式在 Awake 中强制为 Screen Space - Overlay）；CanvasScaler 在此 GameObject 上配置")]
        [SerializeField] private Canvas _uiRootCanvas;

        [Header("UI 输入")]
        [Tooltip("全局唯一 EventSystem，负责分发 UGUI 点击、拖拽、滚动等事件")]
        [SerializeField] private EventSystem _eventSystem;

        [Tooltip("EventSystem 上的输入模块，当前项目使用旧输入系统 StandaloneInputModule")]
        [SerializeField] private BaseInputModule _inputModule;

        /// <summary>UIRoot 根 Canvas</summary>
        public Canvas UIRootCanvas => _uiRootCanvas;

        /// <summary>全局 UI 事件系统。</summary>
        public EventSystem UIEventSystem => _eventSystem;

        /// <summary>全局 UI 输入模块。</summary>
        public BaseInputModule UIInputModule => _inputModule;

        // 各层级 Canvas，由 Awake 动态创建
        private readonly Dictionary<UILayer, Canvas> _layerCanvases = new Dictionary<UILayer, Canvas>();

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ValidateEventSystem();

            if (_uiRootCanvas == null)
            {
                Debug.LogError("[UIBootstrap] UIRoot Canvas 未赋值，请在 Inspector 中拖拽赋值");
                return;
            }

            // URP 不再支持 Built-in 那种"多台 Base 相机 + Depth-Only 叠加"的 UI 合成方式。
            // UI 统一改用 Screen Space - Overlay：直接合成在场景相机渲染之上，无需独立 UICamera
            //（UICamera 已从框架与场景移除），既省一台相机与移动端 tile 显存刷写，也免去跨场景维护相机 Stack。
            _uiRootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CreateLayerCanvases();
        }

        /// <summary>
        /// 校验 UI 输入基础设施，正式环境要求 EventSystem 在场景中显式配置，开发环境允许兜底创建并输出错误日志。
        /// </summary>
        private void ValidateEventSystem()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 仅编辑器 / 开发构建做全场景扫描，用于排查重复实例并对漏配场景兜底绑定；
            // 正式运行时禁止隐式查找（FindObjectsOfType），完全依赖 Inspector 显式配置的 _eventSystem。
            EventSystem[] eventSystems = FindObjectsOfType<EventSystem>(true);
            if (eventSystems.Length > 1)
            {
                Debug.LogError($"[UIBootstrap] 场景中存在 {eventSystems.Length} 个 EventSystem，请保留全局唯一实例。");
            }

            if (_eventSystem == null && eventSystems.Length > 0)
            {
                _eventSystem = eventSystems[0];
                Debug.LogError("[UIBootstrap] EventSystem 未在 Inspector 显式配置，已临时绑定场景中的实例。请使用 Tools/ClientBase/Setup UIBootstrap in Scene 修复配置。");
            }
#endif

            if (_eventSystem == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _eventSystem = CreateDevelopmentEventSystem();
#else
                Debug.LogError("[UIBootstrap] 缺少 EventSystem，正式环境不会运行时创建。请在启动场景显式配置 EventSystem。");
                return;
#endif
            }

            if (_inputModule == null && _eventSystem != null)
            {
                _inputModule = _eventSystem.GetComponent<BaseInputModule>();
            }

            if (_inputModule == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _inputModule = _eventSystem.gameObject.AddComponent<StandaloneInputModule>();
                Debug.LogError("[UIBootstrap] EventSystem 缺少输入模块，开发环境已临时添加 StandaloneInputModule。请修复场景配置。");
#else
                Debug.LogError("[UIBootstrap] EventSystem 缺少输入模块，UGUI 将无法响应点击。");
                return;
#endif
            }

            _eventSystem.enabled = true;
            _inputModule.enabled = true;
            DontDestroyOnLoad(_eventSystem.gameObject);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// 创建开发环境兜底 EventSystem，避免编辑器联调时因旧场景漏配导致 UI 全部不可点击。
        /// </summary>
        /// <returns>临时创建的 EventSystem。</returns>
        private static EventSystem CreateDevelopmentEventSystem()
        {
            GameObject eventSystemObject = new GameObject("EventSystem_RuntimeFallback");
            EventSystem eventSystem = eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<StandaloneInputModule>();
            Debug.LogError("[UIBootstrap] 缺少显式 EventSystem，开发环境已创建临时兜底节点。生产构建前必须修复启动场景配置。");
            return eventSystem;
        }
#endif

        /// <summary>
        /// 为每个 UILayer 在 UIRoot 下创建一个独立子 Canvas，SortOrder 按层级递增。
        /// 子 Canvas 仅控制渲染顺序，渲染模式由根 Canvas 决定。
        /// Canvas GameObject 统一归入 "UI" 层，保持 UI 节点层级归类一致。
        /// </summary>
        private void CreateLayerCanvases()
        {
            int uiLayer = LayerMask.NameToLayer("UI");

            // 根 Canvas 也需要在 UI 层（如在 Inspector 手动创建忘记设置时的保险）
            if (_uiRootCanvas != null)
                _uiRootCanvas.gameObject.layer = uiLayer;

            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var obj = new GameObject($"Canvas_{layer}");
                obj.layer = uiLayer;   // 统一归入 UI 层
                obj.transform.SetParent(_uiRootCanvas.transform, false);

                var canvas = obj.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder    = (int)layer * 100;

                obj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                var rt = obj.GetComponent<RectTransform>();
                rt.anchorMin        = Vector2.zero;
                rt.anchorMax        = Vector2.one;
                rt.sizeDelta        = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;

                _layerCanvases[layer] = canvas;
            }
        }

        /// <summary>
        /// 获取指定层级的 Canvas（UIManager 内部使用）。
        /// 若层级不存在（未正常初始化），返回根 Canvas 作为兜底。
        /// </summary>
        public Canvas GetLayerCanvas(UILayer layer)
            => _layerCanvases.TryGetValue(layer, out var c) ? c : _uiRootCanvas;

        /// <summary>
        /// 获取指定层级 Canvas 的 Transform，作为 UI Prefab 实例化时的父节点。
        /// </summary>
        public Transform GetLayerRoot(UILayer layer)
            => GetLayerCanvas(layer).transform;
    }
}
