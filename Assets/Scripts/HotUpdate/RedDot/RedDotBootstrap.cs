using System.Collections.Generic;
using System.Linq;
using Framework.Core;
using Framework.Foundation;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;
using UnityEngine;

namespace HotUpdate.RedDot
{
    /// <summary>
    /// 热更侧红点组合根：从标准 ConfigData 表组装目录并安装到框架服务。
    /// <para>
    /// HybridCLR 路径由 HotfixEntry 在配置数据库就绪后显式调用；离线整包/Editor 路径先注册
    /// GameEntry.OnBeforeBusinessEntry，首次业务会话进入时再安装，避免早于配置库准备阶段读取旧数据。
    /// <see cref="Install"/> 幂等，并兼容 Editor 关闭 Domain Reload 后 GameEntry 实例被重建。
    /// </para>
    /// </summary>
    public static class RedDotBootstrap
    {
        /// <summary>最近一次完成目录安装的服务实例，用于识别同一进程内的重复装配。</summary>
        private static RedDotService _installedService;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRegisterForOfflineDev() => RegisterPreEntryHook();

        /// <summary>注册离线整包的登录前装配钩子；重复调用只会重挂当前静态入口。</summary>
        public static void RegisterPreEntryHook()
        {
            GameEntry.OnBeforeBusinessEntry = Install;
        }

        /// <summary>从 ConfigData 读取五张红点表并初始化当前 GameEntry.RedDots；同一服务只执行一次。</summary>
        public static void Install()
        {
            RedDotService service = GameEntry.RedDots;
            if (service == null)
            {
                Debug.LogError("[RedDot] GameEntry.RedDots 尚未创建，无法安装热更红点目录。");
                return;
            }

            if (object.ReferenceEquals(_installedService, service) && service.IsInitialized)
                return;

            if (!service.IsInitialized)
                service.Initialize(BuildCatalog());

            _installedService = service;
            Debug.Log($"[RedDot] 热更目录已安装，节点数={service.Catalog.Nodes.Length}。");
        }

        private static RedDotCatalog BuildCatalog()
        {
            List<RedDotModuleRef> moduleRows = GameEntry.RefData
                .GetConfig<RedDotModuleRefTable>().GetAll();
            List<RedDotNodeRef> nodeRows = GameEntry.RefData
                .GetConfig<RedDotNodeRefTable>().GetAll();
            List<RedDotEdgeRef> edgeRows = GameEntry.RefData
                .GetConfig<RedDotEdgeRefTable>().GetAll();
            List<RedDotSeenPolicyRef> seenRows = GameEntry.RefData
                .GetConfig<RedDotSeenPolicyRefTable>().GetAll();
            List<RedDotRetiredRef> retiredRows = GameEntry.RefData
                .GetConfig<RedDotRetiredRefTable>().GetAll();

            var moduleNames = new Dictionary<int, string>();
            for (int i = 0; i < moduleRows.Count; i++)
                if (!moduleNames.ContainsKey(moduleRows[i].Id))
                    moduleNames.Add(moduleRows[i].Id, moduleRows[i].CodeName);

            return new RedDotCatalog
            {
                SchemaVersion = 1,
                Modules = moduleRows.OrderBy(row => row.Id).Select(row => new RedDotModuleDefinition
                {
                    Id = row.Id,
                    Key = row.CodeName,
                    Description = row.Description,
                    IdMin = row.IdMin,
                    IdMax = row.IdMax,
                }).ToArray(),
                Nodes = nodeRows.OrderBy(row => row.Id).Select(row => new RedDotNodeDefinition
                {
                    Id = row.Id,
                    Key = (moduleNames.TryGetValue(row.ModuleId, out string moduleName)
                        ? moduleName
                        : "MissingModule" + row.ModuleId) + "." + row.CodeName,
                    ModuleId = row.ModuleId,
                    Kind = row.Type,
                    Aggregation = row.Aggregation,
                    Description = row.Description,
                }).ToArray(),
                Edges = edgeRows.OrderBy(row => row.ParentId).ThenBy(row => row.ChildId)
                    .Select(row => new RedDotEdgeDefinition
                    {
                        ParentId = row.ParentId,
                        ChildId = row.ChildId,
                        Description = row.Description,
                    }).ToArray(),
                SeenPolicies = seenRows.OrderBy(row => row.SignalId)
                    .Select(row => new RedDotSeenPolicyDefinition
                    {
                        SignalId = row.SignalId,
                        Trigger = row.Trigger,
                        SaveMode = row.SaveMode,
                        Version = row.Version,
                    }).ToArray(),
                RetiredIds = retiredRows.OrderBy(row => row.Id)
                    .Select(row => new RedDotRetiredIdDefinition
                    {
                        Id = row.Id,
                        FormerKey = row.FormerKey,
                        RetiredVersion = row.RetiredVersion,
                        Reason = row.Reason,
                    }).ToArray(),
            };
        }
    }
}
