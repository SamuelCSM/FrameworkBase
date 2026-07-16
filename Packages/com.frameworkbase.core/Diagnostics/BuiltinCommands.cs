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
                    GC.Collect();
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
