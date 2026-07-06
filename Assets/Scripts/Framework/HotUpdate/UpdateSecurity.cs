using System;
using System.Security.Cryptography;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更链路安全策略（集中收口，运行时与 Editor 发布工具共用）。
    ///
    /// 三条防线：
    ///   1. 更新服务器 URL 准入 —— prod 环境强制 HTTPS，杜绝明文链路被中间人替换补丁；
    ///   2. 清单签名校验 —— version.json 用发布方 RSA 私钥签名，客户端用内置公钥验签，
    ///      服务器被入侵 / CDN 被投毒时攻击者无法伪造合法清单；
    ///   3. 补丁强制哈希 —— 代码补丁（DLL）必须携带 MD5，来源是已验签的清单，
    ///      下载完成后逐文件比对，杜绝"跳过校验"的旁路。
    ///
    /// 密钥格式说明：采用 .NET RSA XML 格式（&lt;RSAKeyValue&gt;…），而非 PEM——
    /// 工程 API 兼容级别为 .NET Framework 4.x，PEM 导入 API（ImportSubjectPublicKeyInfo 等）
    /// 不可用；FromXmlString 在 Mono 与 IL2CPP 全平台可用。
    /// </summary>
    public static class UpdateSecurity
    {
        /// <summary>清单签名伴生文件后缀：version.json → version.json.sig（Base64 编码的 RSA-SHA256 签名）。</summary>
        public const string ManifestSignatureSuffix = ".sig";

        /// <summary>视为生产环境的 AppEnv 值（此环境下强制 HTTPS）。</summary>
        public const string ProductionEnv = "prod";

        // ── 1. 更新服务器 URL 准入 ────────────────────────────────────────────

        /// <summary>
        /// 校验更新服务器 URL 是否允许使用。
        /// 规则：prod 环境必须是 HTTPS；非 prod 允许 HTTP（本机联调）但会由调用方告警。
        /// </summary>
        /// <param name="url">更新服务器根 URL。</param>
        /// <param name="appEnv">当前环境（AppConfig.AppEnv：dev / staging / prod）。</param>
        /// <param name="rejectReason">拒绝原因；通过校验时为 null。</param>
        /// <returns>URL 可用返回 true。</returns>
        public static bool ValidateUpdateServerUrl(string url, string appEnv, out string rejectReason)
        {
            rejectReason = null;

            if (string.IsNullOrEmpty(url))
                return true; // 未配置 = 跳过热更，不属于安全违规

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            {
                rejectReason = $"UpdateServerUrl 不是合法的绝对 URL: {url}";
                return false;
            }

            bool isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            bool isHttp  = string.Equals(parsed.Scheme, Uri.UriSchemeHttp,  StringComparison.OrdinalIgnoreCase);

            if (!isHttps && !isHttp)
            {
                rejectReason = $"UpdateServerUrl 协议不受支持（仅允许 http/https）: {url}";
                return false;
            }

            if (isHttp && IsProductionEnv(appEnv))
            {
                rejectReason = $"生产环境（AppEnv={appEnv}）禁止使用明文 HTTP 更新服务器: {url}。" +
                               "热更 DLL 走明文链路等于开放远程代码执行入口，必须切换 HTTPS。";
                return false;
            }

            return true;
        }

        /// <summary>指定环境是否按生产环境安全标准执行。</summary>
        public static bool IsProductionEnv(string appEnv)
        {
            return string.Equals(appEnv?.Trim(), ProductionEnv, StringComparison.OrdinalIgnoreCase);
        }

        // ── 2. 清单签名（RSA-SHA256 / PKCS#1 v1.5）────────────────────────────

        /// <summary>
        /// 用 RSA 公钥验证清单签名。
        /// </summary>
        /// <param name="manifestBytes">version.json 原始字节（下载到什么就验什么，不做重编码）。</param>
        /// <param name="signatureBase64">Base64 编码的签名（version.json.sig 文件内容）。</param>
        /// <param name="publicKeyXml">.NET RSA XML 格式公钥（&lt;RSAKeyValue&gt;，不含私钥参数）。</param>
        /// <returns>签名有效返回 true；任何解析/校验失败一律返回 false（不抛出，调用方按验签失败处理）。</returns>
        public static bool VerifyManifestSignature(byte[] manifestBytes, string signatureBase64, string publicKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyXml))
                return false;

            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64.Trim());

                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(publicKeyXml.Trim());
                    return rsa.VerifyData(manifestBytes, signature,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                GameLog.Error($"[UpdateSecurity] 清单签名校验异常（按验签失败处理）: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 用 RSA 私钥（XML 格式，含私钥参数）对清单签名，返回 Base64 签名。
        /// 发布工具（Editor / CI）使用；运行时不应持有私钥。
        /// </summary>
        /// <param name="manifestBytes">version.json 原始字节。</param>
        /// <param name="privateKeyXml">.NET RSA XML 格式私钥（ToXmlString(true) 导出）。</param>
        public static string SignManifest(byte[] manifestBytes, string privateKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0)
                throw new ArgumentException("清单内容为空", nameof(manifestBytes));
            if (string.IsNullOrWhiteSpace(privateKeyXml))
                throw new ArgumentException("私钥为空", nameof(privateKeyXml));

            using (var rsa = RSA.Create())
            {
                rsa.FromXmlString(privateKeyXml.Trim());
                byte[] signature = rsa.SignData(manifestBytes,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(signature);
            }
        }

        // ── 3. 补丁清单准入 ───────────────────────────────────────────────────

        /// <summary>
        /// 校验单个代码补丁项是否满足安全准入：必须有文件名、下载 URL 与非空 MD5。
        /// MD5 来源是（已验签的）服务端清单，作为下载产物的完整性锚点，任何补丁不得跳过。
        /// </summary>
        public static bool ValidateCodePatchFile(PatchFile patch, out string rejectReason)
        {
            rejectReason = null;

            if (patch == null)
            {
                rejectReason = "补丁项为空";
                return false;
            }

            if (string.IsNullOrEmpty(patch.FileName))
            {
                rejectReason = "补丁项缺少 FileName";
                return false;
            }

            if (string.IsNullOrEmpty(patch.Url))
            {
                rejectReason = $"补丁 {patch.FileName} 缺少下载 URL";
                return false;
            }

            if (string.IsNullOrWhiteSpace(patch.MD5))
            {
                rejectReason = $"补丁 {patch.FileName} 未携带 MD5。代码补丁必须由发布工具生成带哈希的清单，" +
                               "禁止无校验下发（version.json 的 PatchFiles 逐项必填 MD5）。";
                return false;
            }

            return true;
        }
    }
}
