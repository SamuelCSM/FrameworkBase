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
        ServerOnly
    }

    /// <summary>
    /// 配表导出规则资产。默认双端导出，Overrides 只维护 ClientOnly 或 ServerOnly 例外表。
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
        private const string DefaultAssetPath = "Assets/Editor/ExcelTool/ConfigExportRules.asset";

        /// <summary>规则资产引用；为空时表示全部按默认双端导出。</summary>
        private readonly ConfigExportRuleAsset _asset;

        /// <summary>表名到导出目标的快速查询表。</summary>
        private readonly Dictionary<string, ConfigExportTarget> _targets =
            new Dictionary<string, ConfigExportTarget>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 从默认路径加载规则资产；没有资产时默认全部双端导出。
        /// </summary>
        /// <returns>可用于查询导出目标的规则解析器。</returns>
        public static ConfigExportRuleResolver LoadDefault()
        {
            var asset = AssetDatabase.LoadAssetAtPath<ConfigExportRuleAsset>(DefaultAssetPath);
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

                _targets[rule.TableName.Trim()] = rule.Target;
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
