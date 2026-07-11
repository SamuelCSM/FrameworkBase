using System;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 热更新清单验签公钥条目。
    /// <para>
    /// 公钥可以安全地随客户端发布，用于验证发布系统持有的私钥所生成的签名。
    /// <see cref="KeyId"/> 必须与清单中的 KeyId 精确匹配，从而支持新旧公钥并存、分阶段轮换和审计定位。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class UpdateManifestPublicKeyEntry
    {
        /// <summary>
        /// 公钥稳定标识，例如 prod_manifest_2026_q3；不得复用同一标识承载不同密钥材料。
        /// </summary>
        public string KeyId = string.Empty;

        /// <summary>
        /// .NET RSA XML 格式公钥，只允许包含公开参数，严禁把私钥内容序列化到 Unity 资源。
        /// </summary>
        [TextArea(3, 8)]
        public string PublicKeyXml = string.Empty;
    }

    /// <summary>
    /// 框架运行时应用配置。
    /// <para>
    /// 默认从 <c>Resources/AppConfig.asset</c> 加载，在各 Manager 初始化和 LaunchFlow 启动阶段提供
    /// 环境、热更新、网络、观测及远程配置等底层参数。它定义的是基础设施接入点，不应承载具体游戏业务配置。
    /// </para>
    /// <para>
    /// 该资产会进入客户端包体，因此只能保存公开配置和公钥，不能保存签名私钥、上传密钥、服务端 Token 等 Secret。
    /// 敏感值必须由 CI 密钥系统、平台安全存储或服务端短期凭证注入。
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "AppConfig", menuName = "Framework/App Config")]
    public class AppConfigAsset : ScriptableObject
    {
        [Header("运行环境")]
        [Tooltip("dev / qa / staging / prod。prod 环境会启用更严格的热更新与传输安全门禁。")]
        public string AppEnv = "dev";

        [Tooltip("发行渠道稳定标识，用于隔离渠道包、审核包和灰度清单；必须与发布清单 Channel 一致。")]
        public string AppChannel = "default";

        [Header("热更新")]
        [Tooltip("更新服务渠道根 URL，指向产物仓库的 {BaseUrl}/{env}/{platform}/{channel}：version.json 别名与" +
                 " releases/ 不可变版本目录都在其下，补丁 URL 必须位于该路径前缀内。prod 环境必须 HTTPS。")]
        public string UpdateServerUrl = "http://127.0.0.1:80/Updates/dev/windows/default";

        [Tooltip("旧版单公钥兼容字段。新项目应使用带 KeyId 的公钥环；保留该字段仅用于平滑迁移。")]
        [TextArea(3, 8)]
        public string UpdateManifestPublicKey = string.Empty;

        [Tooltip("热更新 RSA 验签公钥环。通过 KeyId 选择密钥，prod 构建至少配置一个有效公钥。")]
        public UpdateManifestPublicKeyEntry[] UpdateManifestPublicKeys = Array.Empty<UpdateManifestPublicKeyEntry>();

        [Tooltip("是否启用 AOT 元数据补充、HybridCLR 程序集加载与 HotfixEntry 启动流程。")]
        public bool EnableHotUpdate = true;

        [Tooltip("更新清单下载、验签或准入失败时是否仍允许使用本地版本启动。强联网生产环境必须关闭。")]
        public bool AllowLaunchWhenUpdateCheckFails;

        [Tooltip("热更新程序集完整文件名白名单。安装槽只允许出现这里声明的程序集，禁止清单写入任意文件。")]
        public string[] HotUpdateAssemblyFiles = Array.Empty<string>();

        [Header("登录与连接")]
        [Tooltip("是否使用真实网络登录链路；关闭时仅允许开发或自动化测试环境使用 Mock 实现。")]
        public bool UseNetworkLogin = true;

        [Tooltip("是否在无持久账号凭据时自动执行游客登录；账号语义和游客合并策略由上层模板实现。")]
        public bool AutoGuestLogin;

        /// <summary>
        /// 游戏长连接服务器域名或地址。生产环境建议使用域名，以便 DNS 同时下发 IPv6 和 IPv4 地址并支持迁移。
        /// </summary>
        public string GameServerHost = "127.0.0.1";

        /// <summary>
        /// 游戏长连接 TCP 端口。
        /// </summary>
        public int GameServerPort = 9000;

        [Tooltip("DNS、TCP 连接及 TLS 握手的统一超时上限（秒）；必须为正数。")]
        public int NetworkTimeoutSeconds = 30;

        [Header("网络 TLS")]
        [Tooltip("是否为游戏长连接启用 TLS。正式强联网项目应启用，并结合证书 Pin 与轮换策略。")]
        public bool UseTls;

        [Tooltip("TLS SNI 与证书主机名校验所使用的服务端名称，不应直接填写临时 IP。")]
        public string TlsServerName = "clientbase-gs";

        [Tooltip("旧版单证书 SHA-256 Pin 兼容字段；新项目应使用下方 Pin 集合完成新旧证书并行轮换。")]
        public string TlsCertSha256 = string.Empty;

        [Tooltip("服务端证书 SHA-256 Pin 集合。证书轮换时先同时配置新旧 Pin，服务端切换完成后再移除旧 Pin。")]
        public string[] TlsCertSha256Pins = Array.Empty<string>();

        [Tooltip("仅允许开发环境对自签名证书启用；开启后 Pin 匹配可绕过系统证书链和主机名错误，生产构建会强制失败。")]
        public bool AllowPinnedCertificateWithoutSystemTrust;

        [Header("可观测性")]
        [Tooltip("崩溃报告上传地址。正式项目通常由平台 Crash SDK 或统一观测扩展包接管。")]
        public string CrashReportUrl = string.Empty;

        [Tooltip("通用 HTTP 埋点上传地址；也可由 Analytics Backend 扩展包替换为厂商 SDK。")]
        public string AnalyticsUrl = string.Empty;

        [Tooltip("是否必须取得隐私授权后才允许启动埋点和设备标识采集。")]
        public bool RequirePrivacyConsentForAnalytics;

        [Tooltip("当前客户端认可的隐私政策版本；版本升级后上层应重新触发授权流程。")]
        public int PrivacyPolicyVersion = 1;

        [Header("远程配置")]
        [Tooltip("远程配置服务地址。运行时应使用本地 Last-Known-Good 快照应对网络或服务端异常。")]
        public string RemoteConfigUrl = string.Empty;

        [Header("登录服务")]
        /// <summary>
        /// 登录服务地址；留空时由项目模板决定是否复用游戏服务器地址或通过服务发现获得。
        /// </summary>
        public string LoginServerHost = string.Empty;

        /// <summary>
        /// 登录服务端口；为 0 时表示尚未配置独立登录服务。
        /// </summary>
        public int LoginServerPort;
    }
}
