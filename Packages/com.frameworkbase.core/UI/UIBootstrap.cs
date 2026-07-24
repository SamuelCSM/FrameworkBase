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

        [Header("适配")]
        [Tooltip("开启后每个 UILayer Canvas 下垫一个 SafeArea 容器（GetLayerRoot 返回它），" +
                 "所有经框架打开的 UI 自动避让刘海/挖孔/Home 条；关闭则由各 UI prefab 自行挂 SafeAreaFitter。" +
                 "注意：开启会改变既有 UI 的实际可用区域，存量项目先在真机/Device Simulator 过一遍再开")]
        [SerializeField] private bool _applySafeAreaToLayers = false;

        [Tooltip("开启后在 UIRoot 上自动挂 CanvasScalerAutoMatch：按 屏幕/参考分辨率宽高比 动态设置 " +
                 "CanvasScaler.matchWidthOrHeight（更宽按高缩放、更窄按宽缩放），异形比例设备 UI 不再溢出")]
        [SerializeField] private bool _autoMatchScaler = true;

        /// <summary>UIRoot 根 Canvas</summary>
        public Canvas UIRootCanvas => _uiRootCanvas;

        /// <summary>全局 UI 事件系统。</summary>
        public EventSystem UIEventSystem => _eventSystem;

        /// <summary>全局 UI 输入模块。</summary>
        public BaseInputModule UIInputModule => _inputModule;

        // 各层级 Canvas，由 Awake 动态创建
        private readonly Dictionary<UILayer, Canvas> _layerCanvases = new Dictionary<UILayer, Canvas>();

        // 各层级的 SafeArea 容器（仅 _applySafeAreaToLayers 开启时创建）
        private readonly Dictionary<UILayer, RectTransform> _layerSafeRoots = new Dictionary<UILayer, RectTransform>();

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ValidateEventSystem();

            if (_uiRootCanvas == null)
            {
                GameLog.Error("[UIBootstrap] UIRoot Canvas 未赋值，请在 Inspector 中拖拽赋值");
                return;
            }

            // URP 不再支持 Built-in 那种"多台 Base 相机 + Depth-Only 叠加"的 UI 合成方式。
            // UI 统一改用 Screen Space - Overlay：直接合成在场景相机渲染之上，无需独立 UICamera
            //（UICamera 已从框架与场景移除），既省一台相机与移动端 tile 显存刷写，也免去跨场景维护相机 Stack。
            _uiRootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // 分辨率适配：按屏幕宽高比动态调 CanvasScaler.match，固定值在异形比例设备上必翻车
            if (_autoMatchScaler && _uiRootCanvas.GetComponent<CanvasScalerAutoMatch>() == null)
                _uiRootCanvas.gameObject.AddComponent<CanvasScalerAutoMatch>();

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
                GameLog.Error($"[UIBootstrap] 场景中存在 {eventSystems.Length} 个 EventSystem，请保留全局唯一实例。");
            }

            if (_eventSystem == null && eventSystems.Length > 0)
            {
                _eventSystem = eventSystems[0];
                GameLog.Error("[UIBootstrap] EventSystem 未在 Inspector 显式配置，已临时绑定场景中的实例。请使用 Framework/Template/Setup Launch Scene 重建接线。");
            }
#endif

            if (_eventSystem == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _eventSystem = CreateDevelopmentEventSystem();
#else
                GameLog.Error("[UIBootstrap] 缺少 EventSystem，正式环境不会运行时创建。请在启动场景显式配置 EventSystem。");
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
                GameLog.Error("[UIBootstrap] EventSystem 缺少输入模块，开发环境已临时添加 StandaloneInputModule。请修复场景配置。");
#else
                GameLog.Error("[UIBootstrap] EventSystem 缺少输入模块，UGUI 将无法响应点击。");
                return;
#endif
            }

            _eventSystem.enabled = true;
            _inputModule.enabled = true;

            // EventSystem 按本组件文档结构通常是 UIBootstrap 的子节点（根节点已整体 DontDestroyOnLoad），
            // 对非根节点重复调用只会触发 Unity "only works for root GameObjects" 告警；
            // 仅当它是独立根节点时才需要单独保活，挂在其它非常驻根下则明确告警。
            if (_eventSystem.transform.parent == null)
                DontDestroyOnLoad(_eventSystem.gameObject);
            else if (_eventSystem.transform.root != transform.root)
                GameLog.Warning("[UIBootstrap] EventSystem 挂在其它根节点下，场景切换时可能被销毁；" +
                                 "建议移到 UIBootstrap 下或作为独立根节点。");
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
            GameLog.Error("[UIBootstrap] 缺少显式 EventSystem，开发环境已创建临时兜底节点。生产构建前必须修复启动场景配置。");
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

                // 可选：层内垫 SafeArea 容器，GetLayerRoot 返回它 → 全部经框架打开的 UI 自动避让异形屏
                if (_applySafeAreaToLayers)
                {
                    var safe = new GameObject("SafeArea");
                    safe.layer = uiLayer;
                    safe.transform.SetParent(obj.transform, false);

                    var safeRt = safe.AddComponent<RectTransform>();
                    safeRt.anchorMin        = Vector2.zero;
                    safeRt.anchorMax        = Vector2.one;
                    safeRt.sizeDelta        = Vector2.zero;
                    safeRt.anchoredPosition = Vector2.zero;
                    safe.AddComponent<SafeAreaFitter>();

                    _layerSafeRoots[layer] = safeRt;
                }
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
        /// 开启 Apply Safe Area To Layers 时返回该层的 SafeArea 容器（UI 自动避让异形屏）。
        /// </summary>
        public Transform GetLayerRoot(UILayer layer)
        {
            if (_layerSafeRoots.TryGetValue(layer, out RectTransform safeRoot))
                return safeRoot;
            return GetLayerCanvas(layer).transform;
        }
    }
}
