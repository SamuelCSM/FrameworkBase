using System;
using System.Collections.Generic;

namespace Framework.Editor.BuildSize
{
    /// <summary>包体尺寸快照中的一条分类条目（如某个 bundle、某类资源）。</summary>
    [Serializable]
    public class BuildSizeEntry
    {
        /// <summary>条目名（bundle 名 / 分类名，作为跨版本比对的键）。</summary>
        public string name;

        /// <summary>字节数。</summary>
        public long bytes;

        /// <summary>无参构造（JsonUtility 反序列化需要）。</summary>
        public BuildSizeEntry() { }

        /// <summary>构造条目。</summary>
        public BuildSizeEntry(string name, long bytes)
        {
            this.name = name;
            this.bytes = bytes;
        }
    }

    /// <summary>
    /// 一次构建产物的尺寸快照：总字节 + 分类明细。
    /// 既作"当前构建"输入，也作落盘"基线"，用 JsonUtility 存 <c>build-size-baseline.json</c>。
    /// </summary>
    [Serializable]
    public class BuildSizeSnapshot
    {
        /// <summary>标签（版本号 / 渠道 / 平台，仅记录与展示用）。</summary>
        public string label;

        /// <summary>采集时刻（Unix 秒，UTC）。</summary>
        public long timestampUtc;

        /// <summary>总字节数。</summary>
        public long totalBytes;

        /// <summary>分类明细（按名比对增长）。</summary>
        public List<BuildSizeEntry> entries = new List<BuildSizeEntry>();
    }

    /// <summary>
    /// 包体门禁阈值策略。总量与单类各有一道，单类设最小体积门槛避免小文件百分比抖动误报。
    /// </summary>
    [Serializable]
    public class BuildSizePolicy
    {
        /// <summary>总量增长超此百分比即违规（&lt;=0 关闭此项）。默认 10%。</summary>
        public double maxTotalGrowthPercent = 10.0;

        /// <summary>总量增长超此绝对字节即违规（&lt;=0 关闭此项）。默认关闭。</summary>
        public long maxTotalGrowthBytes = 0;

        /// <summary>单类增长超此百分比即违规（&lt;=0 关闭此项）。默认 25%。</summary>
        public double maxEntryGrowthPercent = 25.0;

        /// <summary>单类当前体积低于此字节时跳过百分比检查（避免小文件抖动）。默认 64KB。</summary>
        public long entryMinBytesToCheck = 64 * 1024;

        /// <summary>出现基线中没有的新条目是否算违规。默认否（新增内容常态）。</summary>
        public bool failOnNewEntry = false;

        /// <summary>true = 只告警不阻断（Warn 而非 Fail）。默认阻断。</summary>
        public bool warnOnly = false;
    }

    /// <summary>门禁裁决状态。</summary>
    public enum BuildSizeStatus
    {
        /// <summary>无违规（或首次建基线）。</summary>
        Pass,

        /// <summary>有违规但策略只告警。</summary>
        Warn,

        /// <summary>有违规且策略阻断。</summary>
        Fail,
    }

    /// <summary>一条尺寸违规明细。</summary>
    [Serializable]
    public class BuildSizeViolation
    {
        /// <summary>违规分类：<c>TOTAL</c>（总量）/ 条目名 / <c>NEW:条目名</c>（新增）。</summary>
        public string category;

        /// <summary>基线字节。</summary>
        public long baselineBytes;

        /// <summary>当前字节。</summary>
        public long currentBytes;

        /// <summary>增长字节（正数）。</summary>
        public long deltaBytes;

        /// <summary>人类可读原因。</summary>
        public string reason;
    }

    /// <summary>门禁裁决：状态 + 违规明细 + 摘要。</summary>
    public class BuildSizeVerdict
    {
        /// <summary>裁决状态。</summary>
        public BuildSizeStatus Status { get; }

        /// <summary>违规明细（Pass 时为空）。</summary>
        public IReadOnlyList<BuildSizeViolation> Violations { get; }

        /// <summary>一行摘要（日志用）。</summary>
        public string Summary { get; }

        /// <summary>构造裁决。</summary>
        public BuildSizeVerdict(BuildSizeStatus status, IReadOnlyList<BuildSizeViolation> violations, string summary)
        {
            Status = status;
            Violations = violations ?? Array.Empty<BuildSizeViolation>();
            Summary = summary ?? string.Empty;
        }

        /// <summary>是否应阻断（Fail）。</summary>
        public bool IsBlocking => Status == BuildSizeStatus.Fail;
    }
}
