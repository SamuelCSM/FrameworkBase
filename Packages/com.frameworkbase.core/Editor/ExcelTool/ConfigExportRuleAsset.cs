using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 配表导出目标。未配置的表默认双端导出，只需要维护少数例外。
    /// </summary>
    public enum ConfigExportTarget
    {
        /// <summary>客户端与服务端都导出。</summary>
        Both,

        /// <summary>只导出客户端，不生成服务端 TSV 与服务端配置类。</summary>
        ClientOnly,

        /// <summary>只导出服务端，预留给后续服务端专用表。</summary>
        ServerOnly,

        /// <summary>只作为专用工具的配置源，不进入通用 SQLite/TSV/配置类管线。</summary>
        ToolOnly
    }

    /// <summary>工作表的运行时容器形态；Auto 保留 ExcelReader 的默认识别结果。</summary>
    public enum ConfigTableShape
    {
        /// <summary>沿用默认规则：普通表为 Keyed Table，_general 后缀为 General。</summary>
        Auto,

        /// <summary>首列唯一，生成 ConfigBase&lt;TKey,TValue&gt;。</summary>
        Keyed,

        /// <summary>无主键关系表，生成 ConfigListBase&lt;TValue&gt; 并保留全部重复行。</summary>
        List,
    }

    /// <summary>
    /// 配表导出规则资产。默认双端 Keyed Table，Overrides 维护导出目标与少数 List 形态例外。
    /// </summary>
    [CreateAssetMenu(fileName = "ConfigExportRules", menuName = "ClientBase/Config Export Rules")]
    public sealed class ConfigExportRuleAsset : ScriptableObject
    {
        /// <summary>
        /// 单张表的导出目标覆盖规则。
        /// </summary>
        [Serializable]
        public sealed class Rule
        {
            /// <summary>Excel 工作表名，例如 language、ui_wnd_res。</summary>
            public string TableName;

            /// <summary>该表的导出目标。</summary>
            public ConfigExportTarget Target = ConfigExportTarget.Both;

            /// <summary>可选的表容器形态覆盖；关系表选择 List。</summary>
            public ConfigTableShape Shape = ConfigTableShape.Auto;
        }

        /// <summary>未配置表的默认导出目标，大多数项目保持 Both。</summary>
        public ConfigExportTarget DefaultTarget = ConfigExportTarget.Both;

        /// <summary>少数非默认表的覆盖规则。</summary>
        public List<Rule> Overrides = new List<Rule>();
    }

    /// <summary>
    /// 配表导出规则查询器，负责把工作表名映射为客户端/服务端导出决策。
    /// </summary>
    public sealed class ConfigExportRuleResolver
    {
        /// <summary>默认规则资产路径。</summary>
        private const string ProjectOverrideAssetPath = "Assets/Editor/ExcelTool/ConfigExportRules.asset";
        private const string PackageDefaultAssetPath = "Packages/com.frameworkbase.core/Editor/ExcelTool/ConfigExportRules.asset";

        /// <summary>规则资产引用；为空时表示全部按默认双端导出。</summary>
        private readonly ConfigExportRuleAsset _asset;

        /// <summary>表名到导出目标的快速查询表。</summary>
        private readonly Dictionary<string, ConfigExportTarget> _targets =
            new Dictionary<string, ConfigExportTarget>(StringComparer.OrdinalIgnoreCase);

        /// <summary>显式表形态覆盖；Auto 不进入索引。</summary>
        private readonly Dictionary<string, ConfigTableShape> _shapes =
            new Dictionary<string, ConfigTableShape>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 从默认路径加载规则资产；没有资产时默认全部双端导出。
        /// </summary>
        /// <returns>可用于查询导出目标的规则解析器。</returns>
        public static ConfigExportRuleResolver LoadDefault()
        {
            var asset = AssetDatabase.LoadAssetAtPath<ConfigExportRuleAsset>(ProjectOverrideAssetPath);
            if (asset == null)
                asset = AssetDatabase.LoadAssetAtPath<ConfigExportRuleAsset>(PackageDefaultAssetPath);
            return new ConfigExportRuleResolver(asset);
        }

        /// <summary>
        /// 使用指定规则资产创建查询器。
        /// </summary>
        /// <param name="asset">规则资产，为空时所有表按 Both 处理。</param>
        public ConfigExportRuleResolver(ConfigExportRuleAsset asset)
        {
            _asset = asset;

            if (_asset == null || _asset.Overrides == null)
            {
                return;
            }

            foreach (var rule in _asset.Overrides)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.TableName))
                {
                    continue;
                }

                string tableName = rule.TableName.Trim();
                _targets[tableName] = rule.Target;
                if (rule.Shape != ConfigTableShape.Auto)
                    _shapes[tableName] = rule.Shape;
            }
        }

        /// <summary>
        /// 判断指定表是否应该导出到客户端 SQLite。
        /// </summary>
        /// <param name="tableName">Excel 工作表名。</param>
        /// <returns>需要进入客户端配置库时返回 true。</returns>
        public bool ShouldExportToClient(string tableName)
        {
            var target = GetTarget(tableName);
            return target == ConfigExportTarget.Both || target == ConfigExportTarget.ClientOnly;
        }

        /// <summary>
        /// 判断指定表是否应该导出到服务端 TSV 与服务端配置类。
        /// </summary>
        /// <param name="tableName">Excel 工作表名。</param>
        /// <returns>需要进入服务端配置产物时返回 true。</returns>
        public bool ShouldExportToServer(string tableName)
        {
            var target = GetTarget(tableName);
            return target == ConfigExportTarget.Both || target == ConfigExportTarget.ServerOnly;
        }

        /// <summary>是否应该生成通用客户端配置类；ToolOnly/ServerOnly 由各自专用管线处理。</summary>
        public bool ShouldGenerateClientCode(string tableName) => ShouldExportToClient(tableName);

        /// <summary>把规则资产中的形态覆盖应用到 ExcelReader 已识别的工作表类型。</summary>
        public ExcelReader.ExcelSheetKind ResolveSheetKind(
            string tableName,
            ExcelReader.ExcelSheetKind detectedKind)
        {
            if (string.IsNullOrWhiteSpace(tableName) ||
                !_shapes.TryGetValue(tableName.Trim(), out ConfigTableShape shape))
                return detectedKind;

            switch (shape)
            {
                case ConfigTableShape.Keyed: return ExcelReader.ExcelSheetKind.Table;
                case ConfigTableShape.List: return ExcelReader.ExcelSheetKind.List;
                default: return detectedKind;
            }
        }

        /// <summary>
        /// 获取指定表的导出目标，未配置时使用资产默认值，资产不存在时使用 Both。
        /// </summary>
        /// <param name="tableName">Excel 工作表名。</param>
        /// <returns>该表最终使用的导出目标。</returns>
        public ConfigExportTarget GetTarget(string tableName)
        {
            if (!string.IsNullOrWhiteSpace(tableName) &&
                _targets.TryGetValue(tableName.Trim(), out var target))
            {
                return target;
            }

            return _asset != null ? _asset.DefaultTarget : ConfigExportTarget.Both;
        }
    }
}
