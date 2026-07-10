using System;
using System.Collections.Generic;
using Framework.HotUpdate;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布流水线上下文：装"本次发什么"（计划）、"发到哪"（环境与输出目录）与步骤间的中间产物。
    /// 由入口（Editor 窗口 / 未来的 CI 命令行）组装，供各 <see cref="IReleaseStep"/> 读写。
    /// </summary>
    public class ReleaseContext
    {
        // ── 计划：本次发布什么 ────────────────────────────────────────────────
        public bool PublishResource;
        public bool PublishCode;
        public bool ForceUpdate;
        public string AppVersion;
        /// <summary>本次发布的目标资源版本（已按 <see cref="VersionPolicy"/> 计算）。</summary>
        public int ResourceVersion;
        /// <summary>本次发布的目标代码版本（已按 <see cref="VersionPolicy"/> 计算）。</summary>
        public int CodeVersion;
        public string MinCompatibleVersion;
        public string Description = string.Empty;
        public int GrayPercent;
        public string UpdateUrl = string.Empty;

        // ── 环境与输出 ────────────────────────────────────────────────────────
        /// <summary>发布环境（由环境校验步骤填充）。</summary>
        public ReleaseProfile Profile;
        /// <summary>ServerData/Updates 本地权威输出目录。</summary>
        public string ServerDataDir;
        /// <summary>version.json 额外输出目录（IIS/联调目录，可空）。</summary>
        public string VersionOutputDir;
        /// <summary>bundle 额外同步目录（可空）。</summary>
        public string BundleOutputDir;

        // ── 中间产物（步骤间传递）─────────────────────────────────────────────
        /// <summary>代码补丁清单（复制热更 DLL 步骤产出）。</summary>
        public List<PatchFile> PatchFiles = new List<PatchFile>();
        /// <summary>最终清单 JSON（生成清单步骤产出）。</summary>
        public string ManifestJson;

        /// <summary>步骤日志回调（默认丢弃，入口注入窗口/控制台日志）。</summary>
        public Action<string> Log = _ => { };
    }

    /// <summary>
    /// 版本递增规则（阶段一契约）：把"整包归 1、热更 +1"从人脑记忆变成工具自动判断。
    /// </summary>
    public static class VersionPolicy
    {
        /// <summary>
        /// 计算本次发布的目标版本号。
        /// 整包更新（AppVersion 已变更）：Resource/Code 归 1 重算——新大版本安装包内置的就是 1/1；
        /// 热更：勾选项 +1，未勾选项不动。
        /// </summary>
        public static (int resource, int code) Next(
            bool forceUpdate, bool publishResource, bool publishCode,
            int currentResource, int currentCode)
        {
            if (forceUpdate)
                return (1, 1);

            return (publishResource ? currentResource + 1 : currentResource,
                    publishCode ? currentCode + 1 : currentCode);
        }
    }
}
