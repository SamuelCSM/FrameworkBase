using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Framework.Editor
{
    /// <summary>
    /// 从一个（已子集化的）源字体文件生成 <b>动态 SDF</b> TMP 字体资产，供挂进 TMP Settings 全局 fallback 覆盖 CJK。
    /// <para>
    /// 动态模式（<see cref="AtlasPopulationMode.Dynamic"/>）：图集按需从源字体补字形，故资产本体只需带一张初始空图集，
    /// 入库体积由<b>源字体</b>决定（已子集到常用字集，几 MB），而非预烘全字集的大图集（CJK 静态图集会到数十 MB）。
    /// CI 字体门禁（<see cref="FontCoverageChecker"/>）对动态字体按源字体文件的字形覆盖判定，与运行时一致。
    /// </para>
    /// <para>
    /// 源字体子集化在仓库外用 fonttools 完成（见 Docs / 提交说明）；本工具只做「源 OTF → 动态 SDF 资产」这步，
    /// 因 TMP 的 SDF 生成依赖编辑器 FontEngine，无法纯 headless 手搓 .asset。菜单或 batchmode 均可触发。
    /// </para>
    /// </summary>
    public static class CjkFallbackFontBuilder
    {
        private const string SourceOtfPath = "Assets/FrameworkTemplate/Fonts/Committed/SourceHanSansSC-Subset.otf";
        private const string OutputAssetPath = "Assets/FrameworkTemplate/Fonts/Committed/SourceHanSansSC SDF.asset";

        // 动态字体图集参数：SDFAA + 1024²初始图集 + 9px padding，与工程既有 TMP 资产同档。
        private const int SamplingPointSize = 90;
        private const int AtlasPadding = 9;
        private const int AtlasDimension = 1024;

        [MenuItem("Framework/Localization/Build CJK Fallback SDF (思源子集)")]
        public static void BuildFromMenu()
        {
            if (Build(out string message))
                EditorUtility.DisplayDialog("CJK Fallback 字体生成", message, "确定");
            else
                EditorUtility.DisplayDialog("CJK Fallback 字体生成失败", message, "确定");
        }

        /// <summary>
        /// batchmode 入口：<c>-executeMethod Framework.Editor.CjkFallbackFontBuilder.BuildForBatch</c>。
        /// 失败不抛异常（规避 batchmode 退出码不可靠坑），以 ASCII 哨兵日志收口，调用方按日志判定。
        /// </summary>
        public static void BuildForBatch()
        {
            bool ok = Build(out string message);
            Debug.Log(ok
                ? $"CJK_FALLBACK_BUILD_OK {message}"
                : $"CJK_FALLBACK_BUILD_FAIL {message}");
        }

        private static bool Build(out string message)
        {
            Font source = AssetDatabase.LoadAssetAtPath<Font>(SourceOtfPath);
            if (source == null)
            {
                // 新拷入的 OTF 可能尚未导入，先强制导入再取一次。
                AssetDatabase.ImportAsset(SourceOtfPath, ImportAssetOptions.ForceUpdate);
                source = AssetDatabase.LoadAssetAtPath<Font>(SourceOtfPath);
            }
            if (source == null)
            {
                message = $"源字体未找到 / 未导入为 Font：{SourceOtfPath}";
                return false;
            }

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                source, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasDimension, AtlasDimension, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            if (fontAsset == null)
            {
                message = "TMP_FontAsset.CreateFontAsset 返回 null（源字体无法加载字形表？）";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutputAssetPath));
            AssetDatabase.DeleteAsset(OutputAssetPath); // 重建幂等
            AssetDatabase.CreateAsset(fontAsset, OutputAssetPath);

            // 图集贴图与材质随资产存为子对象，否则重载后引用丢失。
            if (fontAsset.atlasTextures != null && fontAsset.atlasTextures.Length > 0)
            {
                fontAsset.atlasTextures[0].name = fontAsset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
            }
            if (fontAsset.material != null)
            {
                fontAsset.material.name = fontAsset.name + " Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(OutputAssetPath, ImportAssetOptions.ForceUpdate);

            string guid = AssetDatabase.AssetPathToGUID(OutputAssetPath);
            message = $"已生成动态 SDF 字体资产：{OutputAssetPath}（GUID {guid}）。请挂进 TMP Settings 全局 fallback。";
            return true;
        }
    }
}
