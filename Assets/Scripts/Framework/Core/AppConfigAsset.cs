using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 应用全局配置（ScriptableObject）。
    /// 在 Project 中创建：右键 → Create → Framework → App Config，
    /// 或菜单 Framework → App Config → Create AppConfig Asset。
    /// 须放在 Resources 下且命名为 AppConfig，以便运行时 Load。
    /// </summary>
    [CreateAssetMenu(fileName = "AppConfig", menuName = "Framework/App Config")]
    public class AppConfigAsset : ScriptableObject
    {
        [Header("环境")]
        [Tooltip("dev / staging / prod")]
        public string AppEnv = "dev";

        [Header("热更 HTTP")]
        [Tooltip("version.json 与热更程序集组根 URL；留空跳过热更检查。AppEnv=prod 时必须为 HTTPS，否则运行时拒绝热更")]
        public string UpdateServerUrl = "http://127.0.0.1:80/Updates";

        [Tooltip("热更清单（version.json）验签公钥，.NET RSA XML 格式（<RSAKeyValue>…）。" +
                 "非空时强制校验 version.json.sig 签名，验签失败拒绝本次热更；留空跳过验签（仅限开发期）。" +
                 "密钥对经菜单 Framework → Hot Update Security → Generate Signing Key Pair 生成，私钥保存在工程外供发布工具使用")]
        [TextArea(3, 8)]
        public string UpdateManifestPublicKey = string.Empty;

        [Header("游戏服 TCP（当前 GS 一体登录）")]
        [Tooltip("勾选后 Login 走 NetworkAuthBackend；未勾选则使用 MockAuthBackend")]
        public bool UseNetworkLogin = true;

        [Tooltip("勾选后启动完成自动以访客身份登录，跳过登录界面；内网测试包用，正式包关闭")]
        public bool AutoGuestLogin = false;

        public string GameServerHost = "127.0.0.1";
        public int GameServerPort = 9000;

        [Header("网络")]
        [Tooltip("登录/HTTP 超时（秒）")]
        public int NetworkTimeoutSeconds = 30;

        [Header("传输加密 TLS（自签名证书 + 指纹固定）")]
        [Tooltip("对 GS 的 TCP 连接启用 TLS；服务端须以 GS_TLS_CERT 指向同一证书启动。本机开发默认关闭，生产必须开启")]
        public bool UseTls = false;

        [Tooltip("TLS 握手目标名（SNI），须与服务端证书 CN 一致；gen_gs_tls_cert.ps1 默认生成 clientbase-gs")]
        public string TlsServerName = "clientbase-gs";

        [Tooltip("服务端证书 SHA-256 指纹（gen_gs_tls_cert.ps1 生成证书时打印，服务端启动日志也会输出）；" +
                 "自签名证书靠此指纹放行，留空则只接受 CA 链校验通过的证书")]
        public string TlsCertSha256 = string.Empty;

        [Header("崩溃回捞")]
        [Tooltip("崩溃/未捕获异常记录的上报端点（HTTP POST，body 为 JSON Lines）；留空仅本地缓存 persistentDataPath/crash_reports.jsonl")]
        public string CrashReportUrl = string.Empty;

        [Header("热更总开关")]
        [Tooltip("关闭后启动流程跳过 AOT 元数据 / HybridCLR 程序集加载 / StartHotfix（LaunchFlow Step 7-9），直接进入登录。" +
                 "无热更业务程序集的项目（纯框架壳 / 单机项目）须关闭，否则 Step 8 找不到热更 DLL 会启动失败并重试卡死。")]
        public bool EnableHotUpdate = true;

        [Header("热更程序集（新项目在此改表，Framework 不写死项目专属程序集名）")]
        [Tooltip("可热更程序集 bytes 文件名，按「依赖在前、被依赖方在后」的加载顺序排列；" +
                 "留空使用 VersionManager 内置默认（本项目：Blokus.Core → GameProtocol → HotUpdate）")]
        public string[] HotUpdateAssemblyFiles = System.Array.Empty<string>();

        /// <summary>供将来 LS 扩展：若不为空则优先于 GameServerHost（当前未使用）。</summary>
        [Header("扩展预留（暂不使用）")]
        public string LoginServerHost = string.Empty;
        public int LoginServerPort;
    }
}
