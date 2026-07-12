using System;
using System.Collections.Generic;
using Framework.HotUpdate;
using UnityEditor;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布模式。区分"只出产物"与"真实部署"，是部署目标失败关闭闸门的判定依据。
    /// </summary>
    public enum ReleaseMode
    {
        /// <summary>只生成候选产物（GitHub Artifact），不对外部署、不切指针。唯一允许无部署目标的模式。</summary>
        BuildOnly = 0,

        /// <summary>构建产物并发布到部署目标，按 payload→回读→current.json 最后切换的顺序提交。</summary>
        Publish = 1,

        /// <summary>同产物跨环境晋级：不重建产物，从源环境读取已验证 release，重签后发布并切指针。</summary>
        Promote = 2,

        /// <summary>回切已存在版本：不重建、不重传，仅重签并切换 current.json 指针。</summary>
        Rollback = 3,

        /// <summary>只校验已发布版本完整性（回读 + 逐文件 SHA-256），不产出、不切指针。</summary>
        VerifyOnly = 4,
    }

    /// <summary>
    /// 发布流水线上下文：装"本次发什么"（计划）、"发到哪"（环境与输出目录）与步骤间的中间产物。
    /// 由入口（Editor 窗口 / 未来的 CI 命令行）组装，供各 <see cref="IReleaseStep"/> 读写。
    /// </summary>
    public class ReleaseContext
    {
        /// <summary>
        /// 本次发布模式。默认 Publish（构建即部署）；CI 经 -releaseMode 注入。
        /// BuildOnly 时 AtomicPublishArtifacts 只保留本地 staging 不部署；
        /// Publish/Promote/Rollback 时部署目标为空由 <see cref="ReleaseArtifactStoreFactory"/> 失败关闭。
        /// </summary>
        public ReleaseMode Mode = ReleaseMode.Publish;

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
        /// <summary>显式目标环境；CI 必须传入，Editor 窗口为空时回退当前活动环境。</summary>
        public string EnvironmentName = string.Empty;
        /// <summary>CI 命令行对 Profile.UploadRoot 的机器级覆盖；不写回团队共享 profile 文件。</summary>
        public string UploadRootOverride = string.Empty;
        /// <summary>本次发布唯一 ID，用于 staging、台账和审计关联。</summary>
        public string ReleaseId = Guid.NewGuid().ToString("N");
        /// <summary>本次发布目标平台，默认使用当前活动 BuildTarget。</summary>
        public BuildTarget BuildTarget = BuildTarget.NoTarget;
        /// <summary>发布环境（由环境校验步骤填充）。</summary>
        public ReleaseProfile Profile;
        /// <summary>ServerData/Updates 本地权威输出目录。</summary>
        public string ServerDataDir;
        /// <summary>version.json 额外输出目录（IIS/联调目录，可空）。</summary>
        public string VersionOutputDir;
        /// <summary>bundle 额外同步目录（旧窗口兼容，可空；正式部署优先使用 Profile.UploadRoot 原子发布）。</summary>
        public string BundleOutputDir;
        /// <summary>发布台账输出路径，由 WriteReleaseLedger 步骤回写。</summary>
        public string ReleaseLedgerPath;
        /// <summary>Git Commit，由发布台账步骤采集。</summary>
        public string GitCommit = string.Empty;

        // ── 产物仓库布局（由环境校验步骤按目标设计填充，各步骤共用同一事实源）──
        /// <summary>发行渠道标识；清单 Channel 字段与产物路径段共用该值，禁止两处各取各的。</summary>
        public string Channel = "default";
        /// <summary>
        /// 发布目标在 UploadRoot 下的作用域相对路径（{env}/{platform}/{channel}）。
        /// 为空时按旧布局直接发布到 UploadRoot 根（单元测试与迁移期兼容）。
        /// </summary>
        public string PublishScopeRelative = string.Empty;
        /// <summary>
        /// 本次发布的不可变版本目录相对路径（releases/{appVersion}/{releaseId}）。
        /// 目录一经发布永不修改；回滚与晋级只移动指针，不重建产物。
        /// </summary>
        public string ReleaseDirRelative = string.Empty;
        /// <summary>实际发布到的渠道根绝对路径，由 AtomicPublishArtifacts 回写；未部署时为空。</summary>
        public string PublishedRootAbsolute = string.Empty;
        /// <summary>指针切换操作者标识（审计用）；CI 入口注入 workflow 信息，缺省为机器用户名。</summary>
        public string SwitchedBy = string.Empty;

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
