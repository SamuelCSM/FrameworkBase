using System;

namespace Framework
{
    /// <summary>
    /// 配置数据库安装流程的终态分类。
    /// <para>
    /// 历史缺陷：旧实现用单个 bool 同时表示"本次没有配置更新 / 下载失败 / 校验失败 / 替换失败"，
    /// LaunchFlow 无法区分"没有热更配置（正常）"与"配置安装失败（必须阻断）"，
    /// 导致配置失败后启动流程继续提交新 ResourceVersion，形成"新资源 + 旧配置"的不一致组合。
    /// </para>
    /// </summary>
    public enum ConfigInstallStatus
    {
        /// <summary>本次内容发行不包含配置数据库（Addressables 中无该地址）。属于正常情况，不阻断启动。</summary>
        NotIncluded = 0,

        /// <summary>配置数据库存在且安装成功（旧库备份保留至启动确认点）。</summary>
        Installed = 1,

        /// <summary>配置数据库存在但下载/加载失败（网络、Addressables 异常）。必须阻断启动。</summary>
        DownloadFailed = 2,

        /// <summary>配置数据库字节校验失败（SQLite 无法打开/表结构不可读/载荷为空）。必须阻断启动。</summary>
        ValidationFailed = 3,

        /// <summary>备份或替换文件操作失败（磁盘空间、句柄占用等）。必须阻断启动。</summary>
        ReplaceFailed = 4,

        /// <summary>安装成功但重载已缓存配置失败。必须阻断启动（数据库与内存缓存不一致）。</summary>
        LoadFailed = 5,
    }

    /// <summary>
    /// 配置数据库安装结果（不可变值类型）。
    /// <see cref="Succeeded"/> 为 true 仅当 <see cref="ConfigInstallStatus.NotIncluded"/>（无需安装）
    /// 或 <see cref="ConfigInstallStatus.Installed"/>（安装成功）——其余任何失败终态都必须阻断启动，
    /// 禁止继续提交版本状态（失败关闭原则）。
    /// </summary>
    public readonly struct ConfigInstallResult
    {
        /// <summary>安装终态。</summary>
        public ConfigInstallStatus Status { get; }

        /// <summary>可诊断错误信息；成功时为空。</summary>
        public string Message { get; }

        /// <summary>是否成功（NotIncluded 或 Installed）。false 时必须阻断启动。</summary>
        public bool Succeeded => Status == ConfigInstallStatus.NotIncluded || Status == ConfigInstallStatus.Installed;

        /// <summary>本次是否实际安装了新配置数据库。</summary>
        public bool DatabaseChanged => Status == ConfigInstallStatus.Installed;

        private ConfigInstallResult(ConfigInstallStatus status, string message)
        {
            Status = status;
            Message = message ?? string.Empty;
        }

        /// <summary>本次内容发行不包含配置数据库。</summary>
        public static ConfigInstallResult NotIncluded() =>
            new ConfigInstallResult(ConfigInstallStatus.NotIncluded, string.Empty);

        /// <summary>安装成功。</summary>
        public static ConfigInstallResult Installed() =>
            new ConfigInstallResult(ConfigInstallStatus.Installed, string.Empty);

        /// <summary>失败终态工厂：状态必须是失败类，信息不得为空。</summary>
        public static ConfigInstallResult Failed(ConfigInstallStatus status, string message)
        {
            if (status == ConfigInstallStatus.NotIncluded || status == ConfigInstallStatus.Installed)
                throw new ArgumentException("失败工厂不接受成功状态。", nameof(status));
            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("失败结果必须携带诊断信息。", nameof(message));
            return new ConfigInstallResult(status, message);
        }

        public override string ToString() =>
            Succeeded ? $"ConfigInstallResult({Status})" : $"ConfigInstallResult({Status}, {Message})";
    }
}
