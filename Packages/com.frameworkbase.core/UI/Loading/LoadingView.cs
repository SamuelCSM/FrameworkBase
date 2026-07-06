using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Framework.UI
{
    /// <summary>
    /// 加载界面视图层。
    /// 持有所有 UI 组件引用，不包含任何业务逻辑。
    ///
    /// 使用方式：
    ///   制作 LoadingScreen Prefab（不含 Canvas 组件），挂载此脚本并在 Inspector 中拖拽赋值。
    ///   GameEntry.Start() 将此 Prefab 实例化到 Canvas_System 层（UIBootstrap 创建的 SortOrder=400 层），
    ///   再通过 LoadingWindow 驱动显示内容。
    ///
    /// Prefab 层级结构（LoadingScreen.prefab，根节点 RectTransform + CanvasGroup）：
    ///   LoadingScreen (挂载本脚本 + CanvasGroup，RectTransform 全屏拉伸)
    ///   ├── Background          全屏背景图
    ///   ├── Logo                游戏 Logo
    ///   ├── ProgressGroup       进度信息组
    ///   │   ├── StatusText      状态说明文字
    ///   │   ├── ProgressBar     Slider 进度条
    ///   │   ├── ProgressText    "42%"
    ///   │   └── DownloadText    "5.2 MB / 10.4 MB"（下载时显示）
    ///   ├── VersionText         左下角版本号
    ///   ├── ErrorPanel          错误面板（默认隐藏）
    ///   │   ├── ErrorText       错误描述
    ///   │   ├── RetryButton     重试按钮
    ///   │   └── ExitButton      退出按钮（移动端）
    ///   └── ForceUpdatePanel    强制更新面板（默认隐藏）
    ///       ├── UpdateDescText  更新说明
    ///       └── UpdateButton    前往更新按钮
    /// </summary>
    public class LoadingView : MonoBehaviour
    {
        [Header("进度区域")]
        public TextMeshProUGUI statusText;
        public Slider          progressBar;
        public TextMeshProUGUI progressText;
        public TextMeshProUGUI downloadText;

        [Header("版本号")]
        public TextMeshProUGUI versionText;

        [Header("错误面板")]
        public GameObject      errorPanel;
        public TextMeshProUGUI errorMessageText;
        public Button          retryButton;
        public Button          exitButton;

        [Header("强制更新面板")]
        public GameObject      forceUpdatePanel;
        public TextMeshProUGUI updateDescText;
        public Button          updateButton;

        [Header("整体淡入淡出")]
        [Tooltip("根节点上的 CanvasGroup，用于整体淡入淡出；缺省时 Awake 从本节点回退获取")]
        public CanvasGroup canvasGroup;

        private void Awake()
        {
            // 稳定引用优先走 Inspector 显式赋值；未配置时仅从本节点（Prefab 根节点已挂 CanvasGroup）回退获取，
            // 不再使用运行时 GetComponentInParent 隐式向上查找，避免层级变化时拿错对象。
            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();

            // 初始状态：错误面板和强制更新面板隐藏
            if (errorPanel)       errorPanel.SetActive(false);
            if (forceUpdatePanel) forceUpdatePanel.SetActive(false);
            if (downloadText)     downloadText.gameObject.SetActive(false);

            // 进度条初始为 0
            if (progressBar) progressBar.value = 0f;
        }
    }
}
