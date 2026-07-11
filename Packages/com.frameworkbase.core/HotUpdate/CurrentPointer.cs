using System;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 渠道根下唯一可变对象 <c>current.json</c> 的数据契约（目标设计 §3）。
    /// <para>
    /// 指针本身与热更清单同一密钥体系签名：客户端先对指针原始字节验签，再按
    /// <see cref="ManifestPath"/> 跳转到不可变版本目录内的正本清单并再次验签——两层都失败关闭。
    /// 回滚与晋级只改写该指针，产物目录永不修改。
    /// </para>
    /// </summary>
    [Serializable]
    public class CurrentPointer
    {
        /// <summary>指针协议版本；客户端拒绝无法理解的新格式。</summary>
        public int SchemaVersion = 1;

        /// <summary>签名公钥标识，与清单共用客户端公钥环，支持轮换。</summary>
        public string KeyId = string.Empty;

        /// <summary>目标环境标识（dev/qa/prod），用于审计与误投防护。</summary>
        public string Env = string.Empty;

        /// <summary>目标平台标识，必须与客户端 <see cref="UpdateSecurity.GetRuntimePlatformId"/> 一致。</summary>
        public string Platform = string.Empty;

        /// <summary>目标发行渠道，必须与客户端 AppConfig.AppChannel 一致。</summary>
        public string Channel = string.Empty;

        /// <summary>当前激活 release 的整包版本。</summary>
        public string AppVersion = string.Empty;

        /// <summary>当前激活的 releaseId。</summary>
        public string ReleaseId = string.Empty;

        /// <summary>
        /// 正本清单相对渠道根的路径（releases/{appVersion}/{releaseId}/version.json）。
        /// 只允许 releases/ 前缀下的安全相对路径，禁止绝对路径与目录穿越。
        /// </summary>
        public string ManifestPath = string.Empty;

        /// <summary>上一个激活的 releaseId，构成可回溯历史链；回滚时指向被回滚者。</summary>
        public string PreviousReleaseId = string.Empty;

        /// <summary>指针切换时间（Unix 秒 UTC）。</summary>
        public long SwitchedAtUnixSeconds;

        /// <summary>切换操作者（workflow run / 操作人标识），仅用于审计。</summary>
        public string SwitchedBy = string.Empty;
    }
}
