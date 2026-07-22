using System;
using System.Text;
using UnityEngine;

namespace Framework.Diagnostics
{
    /// <summary>
    /// 框架内置调试命令集。由组合根 GameEntry 在 Manager 初始化完成后注册一次；
    /// 业务命令自行经 <see cref="Core.GameEntry.Commands"/> 注册，不进本类。
    /// <para>
    /// 收录原则：只进「安全、无账号态副作用」的框架自带项；改配置 / 发协议 / 触发热更等
    /// 有真实业务后果的命令由业务侧按自身风险评估注册（并自行决定是否降到 Privileged 白名单级）。
    /// </para>
    /// </summary>
    public static class BuiltinCommands
    {
        /// <summary>注册全部内置命令。重复调用会因同名注册抛异常（装配错误，按设计炸出）。</summary>
        public static void RegisterAll(CommandRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            registry.Register(
                new CommandInfo("help", "列出可用命令；help <命令名> 查看单条用法",
                    usage: "help [命令名]",
                    requiredAccess: CommandAccessLevel.Privileged),
                args => Help(registry, args));

            registry.Register(
                new CommandInfo("version", "显示本地版本（App / 资源 / 代码）",
                    requiredAccess: CommandAccessLevel.Privileged),
                _ => CommandResult.Ok(Core.VersionDisplayHelper.FormatLocal()));

            registry.Register(
                new CommandInfo("loglevel", "设置日志级别",
                    usage: "loglevel <debug|log|warning|error|none>"),
                args =>
                {
                    string token = args.GetString(0);
                    if (!TryParseLogLevel(token, out LogLevel level))
                        throw new CommandArgumentException($"'{token}' 不是合法级别。");
                    GameLog.SetLogLevel(level);
                    return CommandResult.Ok($"日志级别已设为 {level}。");
                });

            registry.Register(
                new CommandInfo("perfhud", "性能 HUD 显隐", usage: "perfhud <on|off>"),
                args =>
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    PerfHud.Visible = args.GetBool(0);
                    return CommandResult.Ok($"PerfHud {(PerfHud.Visible ? "已显示" : "已隐藏")}。");
#else
                    return CommandResult.Fail("正式包无 PerfHud。");
#endif
                });

            registry.Register(
                new CommandInfo("fps", "设置目标帧率（0 恢复平台默认）", usage: "fps <帧率>"),
                args =>
                {
                    int target = args.GetInt(0);
                    if (target < 0 || target > 240)
                        throw new CommandArgumentException($"帧率 {target} 超出 0~240。");
                    if (target > 0)
                    {
                        QualitySettings.vSyncCount = 0;
                        Application.targetFrameRate = target;
                        return CommandResult.Ok($"目标帧率已设为 {target}。");
                    }
                    Application.targetFrameRate = -1;
                    return CommandResult.Ok("目标帧率已恢复平台默认。");
                });

            registry.Register(
                new CommandInfo("timescale", "设置时间缩放（卡顿/慢动作排查用）", usage: "timescale <0~10>"),
                args =>
                {
                    float scale = args.GetFloat(0);
                    if (scale < 0f || scale > 10f)
                        throw new CommandArgumentException($"时间缩放 {scale} 超出 0~10。");
                    Time.timeScale = scale;
                    return CommandResult.Ok($"Time.timeScale = {scale}。");
                });

            registry.Register(
                new CommandInfo("gc", "强制 GC 并卸载未引用资产（会卡一下，仅排查内存用）"),
                _ =>
                {
                    long before = GC.GetTotalMemory(false) / (1024 * 1024);
                    GC.Collect(); // banned-api-allow: gc-collect 调试命令显式触发
                    Resources.UnloadUnusedAssets();
                    long after = GC.GetTotalMemory(true) / (1024 * 1024);
                    return CommandResult.Ok($"托管内存 {before}MB → {after}MB（未引用资产卸载已异步发起）。");
                });

            registry.Register(
                new CommandInfo("net", "网络连接状态与 RTT",
                    requiredAccess: CommandAccessLevel.Privileged),
                _ =>
                {
                    var network = Core.GameEntry.Network;
                    if (network == null)
                        return CommandResult.Ok("NetworkManager 未初始化。");
                    return CommandResult.Ok(network.IsConnected
                        ? $"已连接，RTT {(ServerTime.RttMs > 0 ? ServerTime.RttMs + "ms" : "采样中")}。"
                        : "未连接。");
                });

            // 白名单级：正式包 GM 白名单账号可用——日志回捞正是给「真机才复现」的问题准备的。
            registry.Register(
                new CommandInfo("logdump", "打包日志目录并尝试上报（未配置通道时留存本地）",
                    requiredAccess: CommandAccessLevel.Privileged),
                _ => LogDump.DumpAsync());

            registry.Register(
                new CommandInfo("reddot", "查询共享红点 DAG：无参列非零节点，支持 ID/Key、explain 来源链与 path 亮起路径",
                    usage: "reddot [ID|Key] | reddot explain <ID|Key> | reddot path <ID|Key>",
                    requiredAccess: CommandAccessLevel.Privileged),
                args =>
                {
                    // 红点服务由中间层 RedDotModule 发布（ADR-008）；ADR-008 步骤3b 搬 asmdef 后本命令将随红点下沉到模块。
                    var service = Framework.RedDots.Service;
                    if (service == null || !service.IsInitialized)
                        return CommandResult.Ok("红点目录未初始化。");

                    string sub = args.GetStringOrDefault(0);
                    bool explain = string.Equals(sub, "explain", StringComparison.OrdinalIgnoreCase);
                    bool path = string.Equals(sub, "path", StringComparison.OrdinalIgnoreCase);
                    string target = args.GetStringOrDefault(explain || path ? 1 : 0);

                    if (path)
                    {
                        if (string.IsNullOrEmpty(target))
                            return CommandResult.Fail("用法：reddot path <ID|Key>");
                        int pathId;
                        if (!int.TryParse(target, out pathId) && !service.TryResolveId(target, out pathId))
                            return CommandResult.Fail($"红点 ID/Key 不存在：{target}");

                        var pathNodes = service.GetActivePath(pathId);
                        if (pathNodes.Count == 0)
                            return CommandResult.Ok($"红点 {pathId} 未点亮，无亮起路径。");

                        var pathText = new StringBuilder(256);
                        pathText.Append("亮起路径（入口→最深来源）：");
                        for (int i = 0; i < pathNodes.Count; i++)
                        {
                            Framework.Foundation.RedDotNodeSnapshot step = pathNodes[i];
                            pathText.AppendLine().Append("  ");
                            for (int indent = 0; indent < i; indent++) pathText.Append("  ");
                            pathText.Append(i == 0 ? string.Empty : "└ ")
                                .Append(step.Id).Append(" [").Append(step.Key).Append("] = ").Append(step.FinalCount);
                            if (step.Kind == Framework.Foundation.RedDotNodeKind.Signal) pathText.Append("（Signal）");
                        }
                        return CommandResult.Ok(pathText.ToString());
                    }

                    if (!string.IsNullOrEmpty(target))
                    {
                        int id;
                        if (!int.TryParse(target, out id) && !service.TryResolveId(target, out id))
                            return CommandResult.Fail($"红点 ID/Key 不存在：{target}");

                        Framework.Foundation.RedDotNodeSnapshot info = default;
                        bool found = false;
                        foreach (Framework.Foundation.RedDotNodeSnapshot item in service.Snapshot())
                        {
                            if (item.Id != id) continue;
                            info = item;
                            found = true;
                            break;
                        }
                        if (!found) return CommandResult.Fail($"红点 ID 不存在：{id}");

                        var detail = new StringBuilder(256);
                        detail.Append(info.Id).Append(" [").Append(info.Key).Append("] = ").Append(info.FinalCount)
                            .Append(" kind=").Append(info.Kind)
                            .Append(" aggregation=").Append(info.Aggregation);
                        if (info.Kind == Framework.Foundation.RedDotNodeKind.Signal)
                        {
                            detail.Append(" raw=").Append(info.RawCount)
                                .Append(" effective=").Append(info.EffectiveCount)
                                .Append(" provider=").Append(info.Provider ?? "(direct)")
                                .Append(" ready=").Append(info.Provider == null || info.ProviderReady);
                            if (info.SeenPolicy != null)
                            {
                                detail.Append(" seen=").Append(info.LastSeenVersion).Append('/')
                                    .Append(info.SeenPolicy.Version)
                                    .Append(" trigger=").Append(info.SeenPolicy.Trigger)
                                    .Append(" save=").Append(info.SeenPolicy.SaveMode);
                            }
                        }

                        if (explain)
                        {
                            foreach (Framework.Foundation.RedDotNodeSnapshot source in service.GetActiveSignalSources(id))
                            {
                                detail.AppendLine().Append("  <- ").Append(source.Id).Append(" [")
                                    .Append(source.Key).Append("] raw=").Append(source.RawCount)
                                    .Append(" effective=").Append(source.EffectiveCount)
                                    .Append(" provider=").Append(source.Provider ?? "(direct)")
                                    .Append(" ready=").Append(source.Provider == null || source.ProviderReady);
                            }
                        }
                        return CommandResult.Ok(detail.ToString());
                    }

                    var sb = new StringBuilder(256);
                    sb.Append("红点 DAG（非零节点；DAG 无隐式 TotalCount）：");
                    int shown = 0;
                    foreach (Framework.Foundation.RedDotNodeSnapshot info in service.Snapshot())
                    {
                        if (info.FinalCount == 0)
                            continue;
                        sb.AppendLine().Append("  ").Append(info.Id).Append(" [").Append(info.Key)
                            .Append("] = ").Append(info.FinalCount);
                        if (info.Kind == Framework.Foundation.RedDotNodeKind.Aggregate) sb.Append("（聚合）");
                        shown++;
                    }
                    if (shown == 0)
                        sb.AppendLine().Append("  （全空）");
                    return CommandResult.Ok(sb.ToString());
                });

            registry.Register(
                new CommandInfo("lang", "查看/切换当前语言：无参看当前，带语言代码切换（如 lang en_us）",
                    usage: "lang [语言代码]",
                    requiredAccess: CommandAccessLevel.Privileged),
                args =>
                {
                    string code = args.GetStringOrDefault(0);
                    if (string.IsNullOrEmpty(code))
                        return CommandResult.Ok($"当前语言：{Language.CurrentLanguage}");

                    // SetLanguage 归一化并广播 LanguageChanged：TextMeshProEx / LocalizedImage 自动重载。
                    Language.SetLanguage(code);
                    return CommandResult.Ok($"语言已切至 {Language.CurrentLanguage}。已订阅的文案/图片自动刷新。");
                });

            // 引导断点调试命令（guide status/reset/skip）已随引导下沉到 GuideModule 注册（ADR-008）。

            registry.Register(
                new CommandInfo("sysinfo", "设备与运行环境信息",
                    requiredAccess: CommandAccessLevel.Privileged),
                _ =>
                {
                    var sb = new StringBuilder(256);
                    sb.Append(SystemInfo.deviceModel).Append(" / ").Append(SystemInfo.operatingSystem).AppendLine()
                      .Append("内存 ").Append(SystemInfo.systemMemorySize).Append("MB，显存 ")
                      .Append(SystemInfo.graphicsMemorySize).Append("MB，").Append(SystemInfo.graphicsDeviceName).AppendLine()
                      .Append("分辨率 ").Append(Screen.width).Append('x').Append(Screen.height)
                      .Append(" @").Append(Screen.currentResolution.refreshRateRatio.value.ToString("0")).Append("Hz，语言 ")
                      .Append(Application.systemLanguage);
                    return CommandResult.Ok(sb.ToString());
                });
        }

        private static CommandResult Help(CommandRegistry registry, CommandArgs args)
        {
            string target = args.GetStringOrDefault(0);
            if (!string.IsNullOrEmpty(target))
            {
                if (!registry.TryGet(target, out CommandInfo info))
                    return CommandResult.Fail($"未知命令 '{target}'。");
                string usage = string.IsNullOrEmpty(info.Usage) ? info.Name : info.Usage;
                return CommandResult.Ok($"{info.Name} — {info.Description}\n用法：{usage}（{info.RequiredAccess}）");
            }

            var sb = new StringBuilder(512);
            var available = registry.ListAvailable();
            sb.Append("可用命令 ").Append(available.Count).Append(" 条：");
            foreach (CommandInfo info in available)
                sb.AppendLine().Append("  ").Append(info.Name).Append(" — ").Append(info.Description);
            return CommandResult.Ok(sb.ToString());
        }

        private static bool TryParseLogLevel(string token, out LogLevel level)
        {
            switch (token.ToLowerInvariant())
            {
                case "debug":   level = LogLevel.Debug;   return true;
                case "log":     level = LogLevel.Log;     return true;
                case "warning": level = LogLevel.Warning; return true;
                case "error":   level = LogLevel.Error;   return true;
                case "none":    level = LogLevel.None;    return true;
                default:        level = LogLevel.Debug;   return false;
            }
        }
    }
}
