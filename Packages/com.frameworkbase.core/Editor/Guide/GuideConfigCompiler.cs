using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Editor.ExcelTool;
using Framework.Foundation;
using Framework.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.Guide
{
    public sealed class GuideConfigCompilation
    {
        public RuleCatalog Rules;
        public TriggerCatalog Triggers;
        public ActionCatalog Actions;
        public GuideCatalog Guides;
        public int[] RetiredGuideIds = Array.Empty<int>();
        public GuideStepIdentity[] RetiredSteps = Array.Empty<GuideStepIdentity>();
        public string[] Warnings = Array.Empty<string>();
    }

    public readonly struct GuideStepIdentity
    {
        public GuideStepIdentity(int guideId, int stepId)
        {
            GuideId = guideId;
            StepId = stepId;
        }
        public int GuideId { get; }
        public int StepId { get; }
    }

    /// <summary>业务自定义 TypeId 的编辑器占位；运行时由业务注册的强类型 Payload Factory 替换。</summary>
    public sealed class OrchestrationPayloadReference
    {
        public OrchestrationPayloadReference(int id) => Id = id;
        public int Id { get; }
    }

    /// <summary>
    /// Guide.xlsx 编译器：校验 Rule/Trigger/Action/Guide 跨表引用，复用运行时服务完成树与步骤拓扑校验，
    /// 并生成 GuideId/StepId 以及编排实例 ID 常量。标准 ConfigPipeline 仍负责 Ref/Table 与 config.db。
    /// </summary>
    public static class GuideConfigCompiler
    {
        public const string WorkbookPath = "Assets/RefData_Excel/Guide.xlsx";
        public const string GeneratedIdsPath = "Assets/Scripts/HotUpdate/Generated/GuideIds.g.cs";

        private const string RuleSheet = "rule_ref";
        private const string RuleNodeSheet = "rule_node_ref";
        private const string RuleEdgeSheet = "rule_edge_ref";
        private const string RuleWindowPayloadSheet = "rule_ui_window_payload_ref";
        private const string RuleTargetPayloadSheet = "rule_ui_target_payload_ref";
        private const string TriggerSheet = "trigger_ref";
        private const string TriggerWindowPayloadSheet = "trigger_ui_window_payload_ref";
        private const string TriggerTargetPayloadSheet = "trigger_ui_target_click_payload_ref";
        private const string TriggerDelayPayloadSheet = "trigger_delay_payload_ref";
        private const string ActionSheet = "action_ref";
        private const string ActionOpenWindowPayloadSheet = "action_ui_open_window_payload_ref";
        private const string ActionCloseWindowPayloadSheet = "action_ui_close_window_payload_ref";
        private const string ActionDelayPayloadSheet = "action_delay_payload_ref";
        private const string ActionFocusPayloadSheet = "action_guide_focus_payload_ref";
        private const string ActionClearFocusPayloadSheet = "action_guide_clear_focus_payload_ref";
        private const string GuideSheet = "guide_ref";
        private const string StepSheet = "guide_step_ref";
        private const string StepActionSheet = "guide_step_action_ref";
        private const string RetiredGuideSheet = "guide_retired_ref";
        private const string RetiredStepSheet = "guide_step_retired_ref";

        [MenuItem("Tools/Framework/Guide/Import Configuration")]
        public static void ImportMenu() => ImportConfiguration(interactive: true);

        internal static bool ImportConfiguration(bool interactive)
        {
            if (!TryCompile(out GuideConfigCompilation compilation, out string report))
            {
                Debug.LogError("[GuideConfig] 导入失败：\n" + report);
                if (interactive) EditorUtility.DisplayDialog("引导配置导入失败", report, "确定");
                return false;
            }
            try
            {
                WriteArtifacts(compilation);
                AssetDatabase.Refresh();
                Debug.Log("[GuideConfig] 导入完成。\n" + report);
                if (interactive) EditorUtility.DisplayDialog("引导配置导入完成", report, "确定");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (interactive) EditorUtility.DisplayDialog("引导配置产物写入失败", ex.Message, "确定");
                return false;
            }
        }

        public static bool TryCompile(out GuideConfigCompilation compilation, out string report)
        {
            compilation = null;
            if (!File.Exists(WorkbookPath))
            {
                report = $"缺少配置文件：{WorkbookPath}";
                return false;
            }
            try
            {
                Dictionary<string, ExcelReader.ExcelSheetData> sheets = new ExcelReader()
                    .ReadExcel(WorkbookPath)
                    .ToDictionary(sheet => sheet.SheetName, StringComparer.OrdinalIgnoreCase);
                EnsureSheets(sheets);
                ValidateSchemas(sheets);

                if (!UIWindowConfigCompiler.TryCompile(out UIWindowCatalog uiCatalog, out string uiReport))
                    throw new InvalidDataException("关联窗口配置无效：" + uiReport);
                var windowIds = new HashSet<int>(uiCatalog.Windows.Select(value => value.Id));
                var targetIds = new HashSet<int>(uiCatalog.Targets.Select(value => value.Id));
                var warnings = new List<string>();

                Dictionary<int, object> rulePayloads = BuildRulePayloads(sheets, windowIds, targetIds);
                Dictionary<int, object> triggerPayloads = BuildTriggerPayloads(sheets, windowIds, targetIds);
                Dictionary<int, object> actionPayloads = BuildActionPayloads(sheets, windowIds, targetIds);

                RuleCatalog rules = ParseRules(sheets, rulePayloads, warnings);
                TriggerCatalog triggers = ParseTriggers(sheets, triggerPayloads, warnings);
                ActionCatalog actions = ParseActions(sheets, actionPayloads, warnings);
                GuideCatalog guides = ParseGuides(sheets);
                ValidateUsingRuntime(rules, triggers, actions, guides);

                int[] retiredGuides = sheets[RetiredGuideSheet].DataRows
                    .Select((row, index) => Int(row, "Id", sheets[RetiredGuideSheet], index)).ToArray();
                GuideStepIdentity[] retiredSteps = sheets[RetiredStepSheet].DataRows
                    .Select((row, index) => new GuideStepIdentity(
                        Int(row, "GuideId", sheets[RetiredStepSheet], index),
                        Int(row, "StepId", sheets[RetiredStepSheet], index))).ToArray();
                ValidateRetired(guides, retiredGuides, retiredSteps);

                compilation = new GuideConfigCompilation
                {
                    Rules = rules,
                    Triggers = triggers,
                    Actions = actions,
                    Guides = guides,
                    RetiredGuideIds = retiredGuides,
                    RetiredSteps = retiredSteps,
                    Warnings = warnings.ToArray(),
                };
                var builder = new StringBuilder()
                    .Append("Rule ").Append(rules.Rules.Length)
                    .Append("，RuleNode ").Append(rules.Nodes.Length)
                    .Append("，Trigger ").Append(triggers.Triggers.Length)
                    .Append("，Action ").Append(actions.Actions.Length)
                    .Append("，Guide ").Append(guides.Guides.Length)
                    .Append("，Step ").Append(guides.Steps.Length).Append('。');
                if (warnings.Count > 0)
                {
                    builder.AppendLine().AppendLine("警告：");
                    for (int i = 0; i < warnings.Count; i++) builder.Append("- ").AppendLine(warnings[i]);
                }
                report = builder.ToString().TrimEnd();
                return true;
            }
            catch (Exception ex)
            {
                compilation = null;
                report = ex.Message;
                return false;
            }
        }

        public static void WriteArtifacts(GuideConfigCompilation compilation)
        {
            if (compilation == null) throw new ArgumentNullException(nameof(compilation));
            Directory.CreateDirectory(Path.GetDirectoryName(GeneratedIdsPath));
            ConfigExportFileWriter.WriteAllTextIfChanged(
                GeneratedIdsPath,
                GenerateIdsCode(compilation),
                new UTF8Encoding(false));
        }

        public static string GenerateIdsCode(GuideConfigCompilation compilation)
        {
            if (compilation == null) throw new ArgumentNullException(nameof(compilation));
            var builder = new StringBuilder(4096);
            builder.AppendLine("// <auto-generated>")
                .AppendLine("// 来源：Assets/RefData_Excel/Guide.xlsx。请勿手改；相同输入生成完全相同内容。")
                .AppendLine("// </auto-generated>")
                .AppendLine()
                .AppendLine("namespace HotUpdate.Guide.Generated")
                .AppendLine("{");
            AppendFlatConstants(builder, "GuideIds", compilation.Guides.Guides
                .Select(value => new IdKey(value.Id, value.Key)));
            builder.AppendLine();
            builder.AppendLine("    public static class GuideStepIds")
                .AppendLine("    {");
            foreach (GuideDefinition guide in compilation.Guides.Guides.OrderBy(value => value.Id))
            {
                string guideName = Identifier(guide.Key);
                builder.Append("        public static class ").Append(guideName).AppendLine()
                    .AppendLine("        {");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (GuideStepDefinition step in compilation.Guides.Steps
                             .Where(value => value.GuideId == guide.Id)
                             .OrderBy(value => value.StepId))
                {
                    string name = Identifier(step.Key);
                    if (!names.Add(name)) throw new InvalidOperationException($"Guide {guide.Id} Step 常量名冲突：{name}。");
                    builder.Append("            public const int ").Append(name).Append(" = ")
                        .Append(step.StepId).AppendLine(";");
                }
                builder.AppendLine("        }");
            }
            builder.AppendLine("    }").AppendLine();
            AppendFlatConstants(builder, "RuleIds", compilation.Rules.Rules
                .Select(value => new IdKey(value.Id, value.Key)));
            builder.AppendLine();
            AppendFlatConstants(builder, "TriggerIds", compilation.Triggers.Triggers
                .Select(value => new IdKey(value.Id, value.Key)));
            builder.AppendLine();
            AppendFlatConstants(builder, "ActionIds", compilation.Actions.Actions
                .Select(value => new IdKey(value.Id, value.Key)));
            builder.AppendLine("}");
            return builder.ToString().Replace("\r\n", "\n");
        }

        private static void AppendFlatConstants(StringBuilder builder, string className, IEnumerable<IdKey> values)
        {
            builder.Append("    public static class ").Append(className).AppendLine()
                .AppendLine("    {");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (IdKey value in values.OrderBy(item => item.Id))
            {
                string name = Identifier(value.Key);
                if (!names.Add(name)) throw new InvalidOperationException($"{className} 常量名冲突：{name}。");
                builder.Append("        public const int ").Append(name).Append(" = ")
                    .Append(value.Id).AppendLine(";");
            }
            builder.AppendLine("    }");
        }

        private readonly struct IdKey
        {
            public IdKey(int id, string key) { Id = id; Key = key; }
            public int Id { get; }
            public string Key { get; }
        }

        private static Dictionary<int, object> BuildRulePayloads(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            ISet<int> windows,
            ISet<int> targets)
        {
            var result = new Dictionary<int, object>();
            AddPayloads(result, sheets[RuleWindowPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int windowId = Int(row, "WindowId", sheet, index);
                RequireId(windows, windowId, sheet, index, "WindowId");
                return new KeyValuePair<int, object>(id, new UIWindowRulePayload { WindowId = windowId });
            });
            AddPayloads(result, sheets[RuleTargetPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int targetId = Int(row, "TargetId", sheet, index);
                RequireId(targets, targetId, sheet, index, "TargetId");
                return new KeyValuePair<int, object>(id, new UITargetRulePayload { TargetId = targetId });
            });
            return result;
        }

        private static Dictionary<int, object> BuildTriggerPayloads(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            ISet<int> windows,
            ISet<int> targets)
        {
            var result = new Dictionary<int, object>();
            AddPayloads(result, sheets[TriggerWindowPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int windowId = Int(row, "WindowId", sheet, index);
                RequireId(windows, windowId, sheet, index, "WindowId");
                return new KeyValuePair<int, object>(id, new UIWindowTriggerPayload
                {
                    WindowId = windowId,
                    Phase = EnumValue<UIWindowPhase>(row, "Phase", sheet, index),
                });
            });
            AddPayloads(result, sheets[TriggerTargetPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int targetId = Int(row, "TargetId", sheet, index);
                RequireId(targets, targetId, sheet, index, "TargetId");
                return new KeyValuePair<int, object>(id, new UITargetClickTriggerPayload { TargetId = targetId });
            });
            AddPayloads(result, sheets[TriggerDelayPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                return new KeyValuePair<int, object>(id, new DelayPayload
                {
                    Milliseconds = Int(row, "Milliseconds", sheet, index),
                    IgnoreTimeScale = Bool(row, "IgnoreTimeScale"),
                });
            });
            return result;
        }

        private static Dictionary<int, object> BuildActionPayloads(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            ISet<int> windows,
            ISet<int> targets)
        {
            var result = new Dictionary<int, object>();
            AddPayloads(result, sheets[ActionOpenWindowPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int windowId = Int(row, "WindowId", sheet, index);
                RequireId(windows, windowId, sheet, index, "WindowId");
                return new KeyValuePair<int, object>(id, new UIOpenWindowActionPayload
                {
                    WindowId = windowId, UsePool = Bool(row, "UsePool"),
                });
            });
            AddPayloads(result, sheets[ActionCloseWindowPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int windowId = Int(row, "WindowId", sheet, index);
                RequireId(windows, windowId, sheet, index, "WindowId");
                return new KeyValuePair<int, object>(id, new UICloseWindowActionPayload
                {
                    WindowId = windowId, Destroy = Bool(row, "Destroy"),
                });
            });
            AddPayloads(result, sheets[ActionDelayPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                return new KeyValuePair<int, object>(id, new DelayPayload
                {
                    Milliseconds = Int(row, "Milliseconds", sheet, index),
                    IgnoreTimeScale = Bool(row, "IgnoreTimeScale"),
                });
            });
            AddPayloads(result, sheets[ActionFocusPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                int targetId = Int(row, "TargetId", sheet, index);
                RequireId(targets, targetId, sheet, index, "TargetId");
                float dimAlpha = Float(row, "DimAlpha", sheet, index);
                if (dimAlpha < 0f || dimAlpha > 1f) throw RowError(sheet, index, "DimAlpha 必须位于 0~1。");
                return new KeyValuePair<int, object>(id, new GuideFocusTargetActionPayload
                {
                    TargetId = targetId,
                    Padding = Float(row, "Padding", sheet, index),
                    DimAlpha = dimAlpha,
                });
            });
            AddPayloads(result, sheets[ActionClearFocusPayloadSheet], (row, sheet, index) =>
            {
                int id = Int(row, "Id", sheet, index);
                return new KeyValuePair<int, object>(id, new GuideClearFocusActionPayload());
            });
            return result;
        }

        private static void AddPayloads(
            IDictionary<int, object> destination,
            ExcelReader.ExcelSheetData sheet,
            Func<Dictionary<string, object>, ExcelReader.ExcelSheetData, int, KeyValuePair<int, object>> parser)
        {
            for (int i = 0; i < sheet.DataRows.Count; i++)
            {
                KeyValuePair<int, object> value = parser(sheet.DataRows[i], sheet, i);
                if (value.Key <= 0) throw RowError(sheet, i, "PayloadId 必须大于 0。");
                if (!destination.TryAdd(value.Key, value.Value))
                    throw RowError(sheet, i, $"PayloadId 跨同类 Payload 表重复：{value.Key}。");
            }
        }

        private static RuleCatalog ParseRules(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            IReadOnlyDictionary<int, object> payloads,
            ICollection<string> warnings)
        {
            ExcelReader.ExcelSheetData ruleSheet = sheets[RuleSheet];
            ExcelReader.ExcelSheetData nodeSheet = sheets[RuleNodeSheet];
            var knownTypes = new HashSet<int>
            {
                BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen,
                BuiltinOrchestrationTypeIds.Rules.UITargetExists,
            };
            return new RuleCatalog
            {
                Rules = ruleSheet.DataRows.Select((row, index) => new RuleDefinition
                {
                    Id = Int(row, "Id", ruleSheet, index),
                    Key = CodeName(row, "CodeName", ruleSheet, index),
                    RootNodeId = Int(row, "RootNodeId", ruleSheet, index),
                    Description = String(row, "Description"),
                }).ToArray(),
                Nodes = nodeSheet.DataRows.Select((row, index) =>
                {
                    RuleNodeKind kind = EnumValue<RuleNodeKind>(row, "Kind", nodeSheet, index);
                    int typeId = Int(row, "TypeId", nodeSheet, index);
                    int payloadId = Int(row, "PayloadId", nodeSheet, index);
                    object payload = null;
                    if (kind == RuleNodeKind.Predicate)
                    {
                        if (knownTypes.Contains(typeId)) payload = RequirePayload(payloads, payloadId, nodeSheet, index);
                        else
                        {
                            if (payloadId <= 0) throw RowError(nodeSheet, index, "业务扩展 Rule PayloadId 必须大于 0。");
                            payload = new OrchestrationPayloadReference(payloadId);
                            warnings.Add($"Rule TypeId={typeId} 为业务扩展，编辑器只校验引用结构；运行时须注册强类型 Factory/Evaluator。");
                        }
                    }
                    return new RuleNodeDefinition
                    {
                        Id = Int(row, "Id", nodeSheet, index),
                        RuleId = Int(row, "RuleId", nodeSheet, index),
                        Kind = kind,
                        TypeId = typeId,
                        Payload = payload,
                        Description = String(row, "Description"),
                    };
                }).ToArray(),
                Edges = sheets[RuleEdgeSheet].DataRows.Select((row, index) => new RuleEdgeDefinition
                {
                    ParentNodeId = Int(row, "ParentNodeId", sheets[RuleEdgeSheet], index),
                    ChildNodeId = Int(row, "ChildNodeId", sheets[RuleEdgeSheet], index),
                    Order = Int(row, "Order", sheets[RuleEdgeSheet], index),
                }).ToArray(),
            };
        }

        private static TriggerCatalog ParseTriggers(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            IReadOnlyDictionary<int, object> payloads,
            ICollection<string> warnings)
        {
            ExcelReader.ExcelSheetData sheet = sheets[TriggerSheet];
            var known = new HashSet<int>
            {
                BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle,
                BuiltinOrchestrationTypeIds.Triggers.UITargetClicked,
                BuiltinOrchestrationTypeIds.Triggers.Delay,
            };
            return new TriggerCatalog
            {
                Triggers = sheet.DataRows.Select((row, index) =>
                {
                    int typeId = Int(row, "TypeId", sheet, index);
                    int payloadId = Int(row, "PayloadId", sheet, index);
                    object payload;
                    if (known.Contains(typeId)) payload = RequirePayload(payloads, payloadId, sheet, index);
                    else
                    {
                        if (payloadId <= 0) throw RowError(sheet, index, "业务扩展 Trigger PayloadId 必须大于 0。");
                        payload = new OrchestrationPayloadReference(payloadId);
                        warnings.Add($"Trigger TypeId={typeId} 为业务扩展，编辑器只校验引用结构；运行时须注册强类型 Factory/Binder。");
                    }
                    return new TriggerDefinition
                    {
                        Id = Int(row, "Id", sheet, index),
                        Key = CodeName(row, "CodeName", sheet, index),
                        TypeId = typeId,
                        Payload = payload,
                        Description = String(row, "Description"),
                    };
                }).ToArray(),
            };
        }

        private static ActionCatalog ParseActions(
            IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets,
            IReadOnlyDictionary<int, object> payloads,
            ICollection<string> warnings)
        {
            ExcelReader.ExcelSheetData sheet = sheets[ActionSheet];
            var known = new HashSet<int>
            {
                BuiltinOrchestrationTypeIds.Actions.UIOpenWindow,
                BuiltinOrchestrationTypeIds.Actions.UICloseWindow,
                BuiltinOrchestrationTypeIds.Actions.Delay,
                BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget,
                BuiltinOrchestrationTypeIds.Actions.GuideClearFocus,
            };
            return new ActionCatalog
            {
                Actions = sheet.DataRows.Select((row, index) =>
                {
                    int typeId = Int(row, "TypeId", sheet, index);
                    int payloadId = Int(row, "PayloadId", sheet, index);
                    object payload;
                    if (known.Contains(typeId)) payload = RequirePayload(payloads, payloadId, sheet, index);
                    else
                    {
                        if (payloadId <= 0) throw RowError(sheet, index, "业务扩展 Action PayloadId 必须大于 0。");
                        payload = new OrchestrationPayloadReference(payloadId);
                        warnings.Add($"Action TypeId={typeId} 为业务扩展，编辑器只校验引用结构；运行时须注册强类型 Factory/Executor。");
                    }
                    return new ActionDefinition
                    {
                        Id = Int(row, "Id", sheet, index),
                        Key = CodeName(row, "CodeName", sheet, index),
                        TypeId = typeId,
                        Payload = payload,
                        Description = String(row, "Description"),
                    };
                }).ToArray(),
            };
        }

        private static GuideCatalog ParseGuides(IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets)
        {
            ExcelReader.ExcelSheetData guideSheet = sheets[GuideSheet];
            ExcelReader.ExcelSheetData stepSheet = sheets[StepSheet];
            ExcelReader.ExcelSheetData actionSheet = sheets[StepActionSheet];
            return new GuideCatalog
            {
                Guides = guideSheet.DataRows.Select((row, index) => new GuideDefinition
                {
                    Id = Int(row, "Id", guideSheet, index),
                    Key = CodeName(row, "CodeName", guideSheet, index),
                    StartRuleId = Int(row, "StartRuleId", guideSheet, index),
                    StartTriggerId = Int(row, "StartTriggerId", guideSheet, index),
                    Priority = Int(row, "Priority", guideSheet, index),
                    RepeatMode = EnumValue<GuideRepeatMode>(row, "RepeatMode", guideSheet, index),
                    Description = String(row, "Description"),
                }).ToArray(),
                Steps = stepSheet.DataRows.Select((row, index) => new GuideStepDefinition
                {
                    GuideId = Int(row, "GuideId", stepSheet, index),
                    StepId = Int(row, "StepId", stepSheet, index),
                    Order = Int(row, "Order", stepSheet, index),
                    CompleteTriggerId = Int(row, "CompleteTriggerId", stepSheet, index),
                    Key = CodeName(row, "CodeName", stepSheet, index),
                    Description = String(row, "Description"),
                }).ToArray(),
                StepActions = actionSheet.DataRows.Select((row, index) => new GuideStepActionDefinition
                {
                    GuideId = Int(row, "GuideId", actionSheet, index),
                    StepId = Int(row, "StepId", actionSheet, index),
                    Phase = EnumValue<GuideActionPhase>(row, "Phase", actionSheet, index),
                    ActionId = Int(row, "ActionId", actionSheet, index),
                    Order = Int(row, "Order", actionSheet, index),
                    FailurePolicy = EnumValue<GuideActionFailurePolicy>(row, "FailurePolicy", actionSheet, index),
                    Description = String(row, "Description"),
                }).ToArray(),
            };
        }

        private static void ValidateUsingRuntime(
            RuleCatalog rules,
            TriggerCatalog triggers,
            ActionCatalog actions,
            GuideCatalog guides)
        {
            var ruleService = new RuleService();
            RegisterRuleTypes(ruleService, rules);
            ruleService.Initialize(rules);
            var triggerService = new TriggerService();
            RegisterTriggerTypes(triggerService, triggers);
            triggerService.Initialize(triggers);
            var actionService = new ActionService();
            RegisterActionTypes(actionService, actions);
            actionService.Initialize(actions);
            var runner = new GuideRunner(
                ruleService, triggerService, actionService, new ValidationProgressStore());
            runner.Initialize(guides);
            runner.Dispose();
        }

        private static void RegisterRuleTypes(RuleService service, RuleCatalog catalog)
        {
            service.Register(BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen, new PassRule<UIWindowRulePayload>());
            service.Register(BuiltinOrchestrationTypeIds.Rules.UITargetExists, new PassRule<UITargetRulePayload>());
            foreach (int typeId in catalog.Nodes.Where(node => node.Kind == RuleNodeKind.Predicate)
                         .Select(node => node.TypeId).Distinct())
                if (typeId != BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen
                    && typeId != BuiltinOrchestrationTypeIds.Rules.UITargetExists)
                    service.Register(typeId, new PassRule<OrchestrationPayloadReference>());
        }

        private static void RegisterTriggerTypes(TriggerService service, TriggerCatalog catalog)
        {
            service.Register(BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle, new EmptyTrigger<UIWindowTriggerPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Triggers.UITargetClicked, new EmptyTrigger<UITargetClickTriggerPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Triggers.Delay, new EmptyTrigger<DelayPayload>());
            foreach (int typeId in catalog.Triggers.Select(value => value.TypeId).Distinct())
                if (typeId != BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle
                    && typeId != BuiltinOrchestrationTypeIds.Triggers.UITargetClicked
                    && typeId != BuiltinOrchestrationTypeIds.Triggers.Delay)
                    service.Register(typeId, new EmptyTrigger<OrchestrationPayloadReference>());
        }

        private static void RegisterActionTypes(ActionService service, ActionCatalog catalog)
        {
            service.Register(BuiltinOrchestrationTypeIds.Actions.UIOpenWindow, new EmptyAction<UIOpenWindowActionPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Actions.UICloseWindow, new EmptyAction<UICloseWindowActionPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Actions.Delay, new EmptyAction<DelayPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget, new EmptyAction<GuideFocusTargetActionPayload>());
            service.Register(BuiltinOrchestrationTypeIds.Actions.GuideClearFocus, new EmptyAction<GuideClearFocusActionPayload>());
            foreach (int typeId in catalog.Actions.Select(value => value.TypeId).Distinct())
                if (typeId != BuiltinOrchestrationTypeIds.Actions.UIOpenWindow
                    && typeId != BuiltinOrchestrationTypeIds.Actions.UICloseWindow
                    && typeId != BuiltinOrchestrationTypeIds.Actions.Delay
                    && typeId != BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget
                    && typeId != BuiltinOrchestrationTypeIds.Actions.GuideClearFocus)
                    service.Register(typeId, new EmptyAction<OrchestrationPayloadReference>());
        }

        private sealed class PassRule<T> : IRuleEvaluator<T>
        {
            public RuleResult Evaluate(T payload, RuleContext context) => RuleResult.Passed();
        }

        private sealed class EmptyTrigger<T> : ITriggerBinder<T>
        {
            public IDisposable Bind(T payload, TriggerContext context, Action<object> onTriggered)
                => EmptyDisposable.Instance;
        }

        private sealed class EmptyAction<T> : IActionExecutor<T>
        {
            public UniTask<ActionExecutionResult> ExecuteAsync(
                T payload, ActionContext context, CancellationToken cancellationToken)
                => UniTask.FromResult(ActionExecutionResult.Succeeded());
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new EmptyDisposable();
            public void Dispose() { }
        }

        private sealed class ValidationProgressStore : IGuideRuntimeProgressStore
        {
            public GuideProgress Get(int guideId) => default;
            public void SetCurrentStep(int guideId, int stepId) { }
            public void MarkCompleted(int guideId) { }
            public void Clear(int guideId) { }
        }

        private static void ValidateRetired(
            GuideCatalog catalog,
            IEnumerable<int> retiredGuides,
            IEnumerable<GuideStepIdentity> retiredSteps)
        {
            var activeGuides = new HashSet<int>(catalog.Guides.Select(value => value.Id));
            var retiredGuideSet = new HashSet<int>();
            foreach (int id in retiredGuides)
            {
                if (!retiredGuideSet.Add(id)) throw new InvalidDataException($"退休 GuideId 重复：{id}。");
                if (activeGuides.Contains(id)) throw new InvalidDataException($"GuideId {id} 已退休，不得复用。");
            }
            var activeSteps = new HashSet<long>(catalog.Steps.Select(value => Identity(value.GuideId, value.StepId)));
            var retiredStepSet = new HashSet<long>();
            foreach (GuideStepIdentity step in retiredSteps)
            {
                long identity = Identity(step.GuideId, step.StepId);
                if (!retiredStepSet.Add(identity))
                    throw new InvalidDataException($"退休 StepId 重复：Guide={step.GuideId}, Step={step.StepId}。");
                if (activeSteps.Contains(identity))
                    throw new InvalidDataException($"StepId 已退休，不得复用：Guide={step.GuideId}, Step={step.StepId}。");
            }
        }

        private static long Identity(int guideId, int stepId) => ((long)guideId << 32) | (uint)stepId;

        private static object RequirePayload(
            IReadOnlyDictionary<int, object> payloads,
            int payloadId,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex)
        {
            if (payloads.TryGetValue(payloadId, out object payload)) return payload;
            throw RowError(sheet, rowIndex, $"PayloadId 不存在或不属于当前 TypeId：{payloadId}。");
        }

        private static void RequireId(
            ISet<int> ids,
            int id,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex,
            string field)
        {
            if (!ids.Contains(id)) throw RowError(sheet, rowIndex, $"{field} 引用不存在：{id}。");
        }

        private static void EnsureSheets(IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets)
        {
            string[] required =
            {
                RuleSheet, RuleNodeSheet, RuleEdgeSheet, RuleWindowPayloadSheet, RuleTargetPayloadSheet,
                TriggerSheet, TriggerWindowPayloadSheet, TriggerTargetPayloadSheet, TriggerDelayPayloadSheet,
                ActionSheet, ActionOpenWindowPayloadSheet, ActionCloseWindowPayloadSheet, ActionDelayPayloadSheet,
                ActionFocusPayloadSheet, ActionClearFocusPayloadSheet, GuideSheet, StepSheet, StepActionSheet,
                RetiredGuideSheet, RetiredStepSheet,
            };
            string[] missing = required.Where(name => !sheets.ContainsKey(name)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException("缺少工作表：" + string.Join(", ", missing));
        }

        private static void ValidateSchemas(IReadOnlyDictionary<string, ExcelReader.ExcelSheetData> sheets)
        {
            Schema(sheets[RuleSheet], ("Id", "int"), ("CodeName", "string"), ("RootNodeId", "int"), ("Description", "string"));
            Schema(sheets[RuleNodeSheet], ("Id", "int"), ("RuleId", "int"), ("Kind", nameof(RuleNodeKind)),
                ("TypeId", "int"), ("PayloadId", "int"), ("Description", "string"));
            Schema(sheets[RuleEdgeSheet], ("ParentNodeId", "int"), ("ChildNodeId", "int"), ("Order", "int"), ("Description", "string"));
            Schema(sheets[RuleWindowPayloadSheet], ("Id", "int"), ("WindowId", "int"));
            Schema(sheets[RuleTargetPayloadSheet], ("Id", "int"), ("TargetId", "int"));
            Schema(sheets[TriggerSheet], ("Id", "int"), ("CodeName", "string"), ("TypeId", "int"), ("PayloadId", "int"), ("Description", "string"));
            Schema(sheets[TriggerWindowPayloadSheet], ("Id", "int"), ("WindowId", "int"), ("Phase", nameof(UIWindowPhase)));
            Schema(sheets[TriggerTargetPayloadSheet], ("Id", "int"), ("TargetId", "int"));
            Schema(sheets[TriggerDelayPayloadSheet], ("Id", "int"), ("Milliseconds", "int"), ("IgnoreTimeScale", "bool"));
            Schema(sheets[ActionSheet], ("Id", "int"), ("CodeName", "string"), ("TypeId", "int"), ("PayloadId", "int"), ("Description", "string"));
            Schema(sheets[ActionOpenWindowPayloadSheet], ("Id", "int"), ("WindowId", "int"), ("UsePool", "bool"));
            Schema(sheets[ActionCloseWindowPayloadSheet], ("Id", "int"), ("WindowId", "int"), ("Destroy", "bool"));
            Schema(sheets[ActionDelayPayloadSheet], ("Id", "int"), ("Milliseconds", "int"), ("IgnoreTimeScale", "bool"));
            Schema(sheets[ActionFocusPayloadSheet], ("Id", "int"), ("TargetId", "int"), ("Padding", "float"), ("DimAlpha", "float"));
            Schema(sheets[ActionClearFocusPayloadSheet], ("Id", "int"));
            Schema(sheets[GuideSheet], ("Id", "int"), ("CodeName", "string"), ("StartRuleId", "int"),
                ("StartTriggerId", "int"), ("Priority", "int"), ("RepeatMode", nameof(GuideRepeatMode)), ("Description", "string"));
            Schema(sheets[StepSheet], ("GuideId", "int"), ("StepId", "int"), ("Order", "int"),
                ("CompleteTriggerId", "int"), ("CodeName", "string"), ("Description", "string"));
            Schema(sheets[StepActionSheet], ("GuideId", "int"), ("StepId", "int"), ("Phase", nameof(GuideActionPhase)),
                ("ActionId", "int"), ("Order", "int"), ("FailurePolicy", nameof(GuideActionFailurePolicy)), ("Description", "string"));
            Schema(sheets[RetiredGuideSheet], ("Id", "int"), ("FormerKey", "string"), ("RetiredVersion", "string"), ("Reason", "string"));
            Schema(sheets[RetiredStepSheet], ("GuideId", "int"), ("StepId", "int"), ("FormerKey", "string"),
                ("RetiredVersion", "string"), ("Reason", "string"));
        }

        private static void Schema(ExcelReader.ExcelSheetData sheet, params (string Field, string Type)[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                int index = sheet.FieldNames.FindIndex(value => string.Equals(value, fields[i].Field, StringComparison.Ordinal));
                if (index < 0) throw new InvalidDataException($"{sheet.SheetName} 缺少字段 {fields[i].Field}。");
                string actual = index < sheet.TypeDefinitions.Count ? sheet.TypeDefinitions[index]?.Trim() : string.Empty;
                if (!string.Equals(actual, fields[i].Type, StringComparison.Ordinal))
                    throw new InvalidDataException($"{sheet.SheetName}.{fields[i].Field} 类型应为 {fields[i].Type}，当前为 '{actual}'。");
            }
        }

        private static int Int(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int index)
        {
            string value = String(row, field);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw RowError(sheet, index, $"字段 {field} 不是有效整数：'{value}'。");
            return parsed;
        }

        private static float Float(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int index)
        {
            string value = String(row, field);
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                throw RowError(sheet, index, $"字段 {field} 不是有效浮点数：'{value}'。");
            return parsed;
        }

        private static bool Bool(Dictionary<string, object> row, string field)
            => (bool)ExcelReader.ParseCellValue(row.TryGetValue(field, out object value) ? value : null, typeof(bool));

        private static T EnumValue<T>(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int index)
            where T : struct
        {
            string value = String(row, field);
            if (!Enum.TryParse(value, true, out T parsed) || !Enum.IsDefined(typeof(T), parsed))
                throw RowError(sheet, index, $"字段 {field} 不是有效 {typeof(T).Name}：'{value}'。");
            return parsed;
        }

        private static string CodeName(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int index)
        {
            string value = String(row, field);
            if (string.IsNullOrWhiteSpace(value)) throw RowError(sheet, index, $"字段 {field} 不能为空。");
            if (!char.IsLetter(value[0]) && value[0] != '_')
                throw RowError(sheet, index, $"字段 {field} 必须以字母或下划线开头：'{value}'。");
            for (int i = 1; i < value.Length; i++)
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '_')
                    throw RowError(sheet, index, $"字段 {field} 只能包含字母、数字和下划线：'{value}'。");
            return value;
        }

        private static string String(Dictionary<string, object> row, string field)
            => row.TryGetValue(field, out object value) && value != null && value != DBNull.Value
                ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                : string.Empty;

        private static Exception RowError(ExcelReader.ExcelSheetData sheet, int index, string message)
            => new InvalidDataException($"{sheet.SheetName} 第 {index + 4} 行：{message}");

        private static string Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            var builder = new StringBuilder(value.Length);
            bool upper = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_') { upper = true; continue; }
                if (builder.Length == 0 && char.IsDigit(c)) builder.Append('_');
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return builder.Length == 0 ? "Unnamed" : builder.ToString();
        }
    }

    internal sealed class GuideWorkbookPostprocessor : AssetPostprocessor
    {
        private static bool _queued;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_queued || importedAssets == null || !importedAssets.Any(path => string.Equals(
                    path, GuideConfigCompiler.WorkbookPath, StringComparison.OrdinalIgnoreCase)))
                return;
            _queued = true;
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                GuideConfigCompiler.ImportConfiguration(interactive: false);
            };
        }
    }
}
