using System;
using System.Collections.Generic;
using System.Linq;
using Framework;
using Framework.Core;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;
using UnityEngine;

namespace HotUpdate.UI
{
    /// <summary>
    /// 从标准 ConfigData 安装稳定窗口目录。Addressable 窗口由表自动注册；Code 窗口只提供元数据，
    /// 业务仍须按生成的 UIWindowIds 注册 View Factory，但不再手写数字/字符串身份。
    /// </summary>
    public static class UIWindowBootstrap
    {
        private static UIManager _installedManager;

        public static UIWindowCatalog Catalog { get; private set; }

        public static void Install()
        {
            UIManager ui = GameEntry.UI;
            if (ui == null)
            {
                Debug.LogError("[UIWindow] GameEntry.UI 尚未创建，无法安装窗口目录。");
                return;
            }
            if (ReferenceEquals(_installedManager, ui)) return;

            UIWindowCatalog catalog = BuildCatalog();
            for (int i = 0; i < catalog.Windows.Length; i++)
            {
                UIWindowDefinition window = catalog.Windows[i];
                if (window.RegistrationMode == UIWindowRegistrationMode.Code) continue;
                Type logicType = Type.GetType(window.LogicType, throwOnError: false);
                if (logicType == null)
                    throw new InvalidOperationException(
                        $"[UIWindow] WindowId={window.Id} 无法解析 LogicType：{window.LogicType}。");
                ui.RegisterUI(
                    window.Id,
                    logicType,
                    window.Address,
                    window.Layer,
                    window.AllowMultiple,
                    window.StackBehavior,
                    window.BlockerMode);
            }

            Catalog = catalog;
            _installedManager = ui;
            Debug.Log($"[UIWindow] 目录已安装，窗口={catalog.Windows.Length}，Target={catalog.Targets.Length}。");
        }

        private static UIWindowCatalog BuildCatalog()
        {
            List<UiWindowModuleRef> modules = GameEntry.RefData.GetConfig<UiWindowModuleRefTable>().GetAll();
            List<UiWindowRef> windows = GameEntry.RefData.GetConfig<UiWindowRefTable>().GetAll();
            List<UiTargetRef> targets = GameEntry.RefData.GetConfig<UiTargetRefTable>().GetAll();
            List<UiWindowRetiredRef> retiredWindows = GameEntry.RefData.GetConfig<UiWindowRetiredRefTable>().GetAll();
            List<UiTargetRetiredRef> retiredTargets = GameEntry.RefData.GetConfig<UiTargetRetiredRefTable>().GetAll();
            var moduleNames = modules.ToDictionary(value => value.Id, value => value.CodeName);
            return new UIWindowCatalog
            {
                Modules = modules.OrderBy(value => value.Id).Select(value => new UIWindowModuleDefinition
                {
                    Id = value.Id,
                    Key = value.CodeName,
                    Description = value.Description,
                    WindowIdMin = value.WindowIdMin,
                    WindowIdMax = value.WindowIdMax,
                    TargetIdMin = value.TargetIdMin,
                    TargetIdMax = value.TargetIdMax,
                }).ToArray(),
                Windows = windows.OrderBy(value => value.Id).Select(value => new UIWindowDefinition
                {
                    Id = value.Id,
                    ModuleId = value.ModuleId,
                    Key = (moduleNames.TryGetValue(value.ModuleId, out string module) ? module : "MissingModule")
                          + "." + value.CodeName,
                    LogicType = value.LogicType,
                    RegistrationMode = value.RegistrationMode,
                    Address = value.Address,
                    Layer = value.Layer,
                    AllowMultiple = value.AllowMultiple,
                    StackBehavior = value.StackBehavior,
                    BlockerMode = value.BlockerMode,
                    Description = value.Description,
                }).ToArray(),
                Targets = targets.OrderBy(value => value.Id).Select(value => new UITargetDefinition
                {
                    Id = value.Id,
                    ModuleId = value.ModuleId,
                    WindowId = value.WindowId,
                    Key = (moduleNames.TryGetValue(value.ModuleId, out string module) ? module : "MissingModule")
                          + "." + value.CodeName,
                    Description = value.Description,
                }).ToArray(),
                RetiredWindowIds = retiredWindows.OrderBy(value => value.Id).Select(value => new UIStableIdRetiredDefinition
                {
                    Id = value.Id,
                    FormerKey = value.FormerKey,
                    RetiredVersion = value.RetiredVersion,
                    Reason = value.Reason,
                }).ToArray(),
                RetiredTargetIds = retiredTargets.OrderBy(value => value.Id).Select(value => new UIStableIdRetiredDefinition
                {
                    Id = value.Id,
                    FormerKey = value.FormerKey,
                    RetiredVersion = value.RetiredVersion,
                    Reason = value.Reason,
                }).ToArray(),
            };
        }
    }
}
