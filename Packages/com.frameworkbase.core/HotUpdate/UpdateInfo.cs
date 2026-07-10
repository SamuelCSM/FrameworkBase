using System;
using System.Collections.Generic;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新版本清单的数据契约。
    /// <para>
    /// 服务端发布的 <c>version.json</c> 必须与其签名文件作为同一个不可变版本单元发布。
    /// 客户端应先对收到的原始 JSON 字节执行 RSA-SHA256 验签，再反序列化并执行字段级安全准入；
    /// 任何字段都不能在验签前被当作可信输入。
    /// </para>
    /// <para>
    /// <see cref="AppVersion"/> 表示整包兼容边界；<see cref="ResourceVersion"/> 与 <see cref="CodeVersion"/>
    /// 只允许在相同 AppVersion 内严格递增。跨 AppVersion 的变更必须通过整包更新处理，避免旧原生代码加载不兼容补丁。
    /// </para>
    /// </summary>
    [Serializable]
    public class UpdateInfo
    {
        /// <summary>
        /// 清单协议版本，用于拒绝客户端无法理解的新格式或已被淘汰的旧安全语义。
        /// </summary>
        public int ManifestVersion = 2;

        /// <summary>
        /// 单次发布清单的全局唯一标识，建议使用 GUID；用于审计、发布台账关联和重放问题定位。
        /// </summary>
        public string ManifestId = string.Empty;

        /// <summary>
        /// 清单签发时间，采用 Unix 秒 UTC；客户端据此拒绝明显来自未来或过旧的清单。
        /// </summary>
        public long IssuedAtUnixSeconds;

        /// <summary>
        /// 清单失效时间，采用 Unix 秒 UTC；超过该时间后即使签名正确也不得继续安装。
        /// </summary>
        public long ExpiresAtUnixSeconds;

        /// <summary>
        /// 签名公钥标识。客户端通过该值从受信任公钥环选择密钥，以支持可审计的密钥轮换。
        /// </summary>
        public string KeyId = string.Empty;

        /// <summary>
        /// 清单目标平台，例如 android、ios、windows；必须与当前运行平台一致，禁止跨平台误投。
        /// </summary>
        public string Platform = string.Empty;

        /// <summary>
        /// 清单目标发行渠道；用于隔离官包、渠道包、审核包及其他具有不同资源或 SDK 契约的产物。
        /// </summary>
        public string Channel = string.Empty;

        /// <summary>
        /// 安装该补丁所要求的最低 FrameworkBase 运行时版本。
        /// </summary>
        public string MinFrameworkVersion = string.Empty;

        /// <summary>
        /// 整包版本，例如 1.2.0；必须与当前 <c>Application.version</c> 一致才能应用热更新。
        /// </summary>
        public string AppVersion;

        /// <summary>
        /// 资源版本号，在同一 AppVersion 内只能严格递增，禁止降级或重复提交。
        /// </summary>
        public int ResourceVersion;

        /// <summary>
        /// 代码版本号，在同一 AppVersion 内只能严格递增，禁止降级或重复提交。
        /// </summary>
        public int CodeVersion;

        /// <summary>
        /// 是否要求用户立即更新；具体交互策略由上层启动模板决定，底层仅传递不可绕过的更新语义。
        /// </summary>
        public bool ForceUpdate;

        /// <summary>
        /// 服务端允许继续运行的最低整包版本，低于该版本时客户端必须进入整包更新流程。
        /// </summary>
        public string MinCompatibleVersion;

        /// <summary>
        /// 代码补丁文件集合。CodeVersion 增长时必须提供完整程序集集合及每个文件的 Size、SHA-256。
        /// </summary>
        public List<PatchFile> PatchFiles = new List<PatchFile>();

        /// <summary>
        /// 面向用户或运营后台的版本说明；不得参与安全决策。
        /// </summary>
        public string Description;

        /// <summary>
        /// 发布端给出的更新类型提示。客户端仍需基于版本字段自行复核，不能只信任该枚举值。
        /// </summary>
        public UpdateType Type;

        /// <summary>
        /// 整包更新地址，仅在判定为 <see cref="UpdateType.FullUpdate"/> 时使用。
        /// </summary>
        public string UpdateUrl;

        /// <summary>
        /// 灰度百分比：0 表示不灰度，100 表示全量，1～99 表示按稳定设备桶进行投放。
        /// </summary>
        public int GrayPercent;
    }

    /// <summary>
    /// 单个热更新补丁文件的数据契约。
    /// <para>
    /// <see cref="FileName"/> 必须是无目录分隔符和 <c>..</c> 的安全叶子文件名；
    /// <see cref="Size"/> 与 <see cref="SHA256"/> 必须由已签名清单覆盖，下载完成后再次校验。
    /// </para>
    /// </summary>
    [Serializable]
    public class PatchFile
    {
        /// <summary>
        /// 安装槽内的目标文件名，例如 HotUpdate.dll.bytes；不得包含相对路径或绝对路径。
        /// </summary>
        public string FileName;

        /// <summary>
        /// 文件下载地址。正式环境必须满足 HTTPS 和发布域名策略，不得由本地路径拼接覆盖安装根目录。
        /// </summary>
        public string Url;

        /// <summary>
        /// 期望文件长度（字节），用于发现截断、错误响应体及断点续传拼接错误。
        /// </summary>
        public long Size;

        /// <summary>
        /// 文件 SHA-256 摘要，使用十六进制编码；这是新版清单必需的内容完整性字段。
        /// </summary>
        public string SHA256;

        /// <summary>
        /// 旧清单兼容字段。新发布流程不得只生成 MD5，正式安全准入也不得以 MD5 代替 SHA-256。
        /// </summary>
        public string MD5;
    }

    /// <summary>
    /// 客户端经过版本比较和安全准入后得到的更新决策类型。
    /// </summary>
    public enum UpdateType
    {
        /// <summary>
        /// 当前客户端无需安装任何更新。
        /// </summary>
        None,

        /// <summary>
        /// AppVersion 相同，可在事务槽内安装资源或代码热更新。
        /// </summary>
        HotUpdate,

        /// <summary>
        /// AppVersion 不兼容或低于最低兼容版本，必须跳转整包更新。
        /// </summary>
        FullUpdate
    }
}
