#if UNITY_EDITOR || DEVELOPMENT_BUILD
using TMPro;
using UnityEngine;

namespace Framework.UI
{
    /// <summary>
    /// 开发期 CJK 回退字体挂载钩子（Editor / Development Build 专用，正式包整类剥离，零开销）。
    ///
    /// 背景：TMP 默认字体 LiberationSans 无中文字形，框架启动链路的中文文案会显示为方框。
    /// 模板脚手架（Framework → Template → Setup Launch Scene）会在本机生成
    /// <c>Resources/CjkDevFallback SDF</c> 动态字体资产（gitignore，不入库——系统字体授权不可再分发，
    /// 且本地资产 GUID 每机不同，故不能写进已提交的 TMP Settings）。
    /// 本钩子在场景加载前按 Resources 路径（与 GUID 无关）把它挂入 TMP 全局回退链，跨机器行为一致。
    ///
    /// 正式包体请为项目配置自有授权字体并做子集化（见评审差距项"字体子集"），不要依赖本钩子。
    /// </summary>
    internal static class DevCjkFontFallback
    {
        /// <summary>脚手架生成的开发期回退字体在 Resources 下的路径（不含扩展名）。</summary>
        private const string ResourcePath = "CjkDevFallback SDF";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            var fontAsset = Resources.Load<TMP_FontAsset>(ResourcePath);
            if (fontAsset == null)
            {
                // 未跑脚手架或非 Windows 环境（如 CI 容器）：中文显示为方框但不影响功能。
                // 用 Log 而非 Warning：这是可选的开发期体验增强，不应污染"零 Warning"验收与 CI 日志。
                GameLog.Log("[DevCjkFontFallback] 未找到开发期 CJK 回退字体，中文将显示为方框。" +
                          "跑一次 Framework → Template → Setup Launch Scene 可修复。");
                return;
            }

            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks != null && !fallbacks.Contains(fontAsset))
            {
                fallbacks.Add(fontAsset);
                GameLog.Log($"[DevCjkFontFallback] 已挂载开发期 CJK 回退字体：{fontAsset.name}");
            }
        }
    }
}
#endif
