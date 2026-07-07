using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// CanvasScaler match 自动适配：按 当前屏幕宽高比 vs 参考分辨率宽高比 动态设置
    /// <c>matchWidthOrHeight</c>——固定 match 值在异形比例设备上必翻车：
    ///   屏幕比参考更宽（平板→带鱼屏）→ 按高度缩放（match=1），UI 不超出上下边；
    ///   屏幕比参考更窄（超长竖屏）  → 按宽度缩放（match=0），UI 不超出左右边。
    /// 效果 = 参考分辨率画布始终完整可见（信封式适配），多余空间由背景出血填充。
    ///
    /// 挂在带 CanvasScaler 的 UIRoot 上（UIBootstrap 开启 Auto Match 时自动挂载）；
    /// 要求 CanvasScaler 为 Scale With Screen Size 模式。分辨率/转屏变化自动跟随。
    /// </summary>
    [RequireComponent(typeof(CanvasScaler))]
    [DisallowMultipleComponent]
    public sealed class CanvasScalerAutoMatch : MonoBehaviour
    {
        private CanvasScaler _scaler;
        private Vector2Int _appliedScreen;

        private void Awake()
        {
            _scaler = GetComponent<CanvasScaler>();
            if (_scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                GameLog.Warning("[CanvasScalerAutoMatch] CanvasScaler 不是 Scale With Screen Size 模式，" +
                                "match 自动适配不生效");
            }
        }

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            if (Screen.width != _appliedScreen.x || Screen.height != _appliedScreen.y)
                Apply();
        }

        private void Apply()
        {
            if (Screen.width <= 0 || Screen.height <= 0)
                return;

            _scaler.matchWidthOrHeight = CalculateMatch(
                Screen.width, Screen.height,
                _scaler.referenceResolution.x, _scaler.referenceResolution.y);
            _appliedScreen = new Vector2Int(Screen.width, Screen.height);
        }

        /// <summary>
        /// 计算 match 值（纯计算，可单测）：
        /// 屏幕宽高比 ≥ 参考宽高比（更宽）→ 1（按高度缩放）；更窄 → 0（按宽度缩放）。
        /// 非法输入回退 1（横屏游戏更常见的安全默认）。
        /// </summary>
        public static float CalculateMatch(float screenWidth, float screenHeight, float refWidth, float refHeight)
        {
            if (screenWidth <= 0 || screenHeight <= 0 || refWidth <= 0 || refHeight <= 0)
                return 1f;

            float screenAspect = screenWidth / screenHeight;
            float refAspect = refWidth / refHeight;
            return screenAspect >= refAspect ? 1f : 0f;
        }
    }
}
