using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Framework.Core;
using UnityEngine;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新供应链安全规则集合，供 Runtime、Editor 构建门禁与 CI 发布工具复用。
    /// <para>
    /// 该类只实现可确定、可自动化的准入规则，不依赖 UI：更新服务 URL 策略、RSA-SHA256 原始字节验签、
    /// 清单版本与时效、平台与渠道隔离、框架最低版本、防降级、文件路径白名单以及 Size + SHA-256 完整性声明。
    /// Runtime 和发布工具必须复用同一套规则，避免“发布端认为合法、客户端拒绝”或安全标准漂移。
    /// </para>
    /// </summary>
    public static class UpdateSecurity
    {
        /// <summary>
        /// 清单签名文件后缀。签名文件应与 version.json 同版本发布，并在切换清单可见性之前完成上传。
        /// </summary>
        public const string ManifestSignatureSuffix = ".sig";

        /// <summary>
        /// 生产环境稳定标识。环境比较忽略大小写和首尾空白。
        /// </summary>
        public const string ProductionEnv = "prod";

        /// <summary>
        /// 允许客户端时钟与签发服务器之间存在的最大正向偏差，单位为秒。
        /// </summary>
        private const long MaxClockSkewSeconds = 300;

        /// <summary>
        /// 单份清单允许的最长有效期。限制长期有效清单可缩短密钥泄露或 CDN 旧对象重放的影响窗口。
        /// </summary>
        private const long MaxManifestLifetimeSeconds = 90L * 24 * 60 * 60;

        /// <summary>
        /// 校验更新服务根 URL。生产环境强制 HTTPS；非生产环境允许 HTTP 以支持本地联调。
        /// </summary>
        /// <param name="url">待校验的绝对更新服务 URL；空值表示该环境未启用远程更新。</param>
        /// <param name="appEnv">应用环境标识。</param>
        /// <param name="rejectReason">失败时返回可定位的拒绝原因。</param>
        /// <returns>URL 为空或满足当前环境传输策略时返回 <see langword="true"/>。</returns>
        public static bool ValidateUpdateServerUrl(string url, string appEnv, out string rejectReason)
        {
            rejectReason = null;
            if (string.IsNullOrEmpty(url)) return true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            {
                rejectReason = $"UpdateServerUrl 不是有效的绝对 URL：{url}";
                return false;
            }

            bool https = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            bool http = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
            if (!https && !http)
            {
                rejectReason = $"更新服务 URL 使用了不允许的协议：{parsed.Scheme}";
                return false;
            }
            if (http && IsProductionEnv(appEnv))
            {
                rejectReason = $"生产环境更新服务 URL 必须使用 HTTPS：{url}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 判断环境标识是否为生产环境；比较忽略大小写与首尾空白。
        /// </summary>
        public static bool IsProductionEnv(string appEnv) =>
            string.Equals(appEnv?.Trim(), ProductionEnv, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 使用 RSA XML 公钥对收到的 version.json 原始字节执行 RSA-SHA256/PKCS#1 v1.5 验签。
        /// <para>
        /// 必须传入网络收到的原始字节，不能对反序列化后重新生成的 JSON 验签，否则字段顺序、空白和编码变化会破坏签名边界。
        /// 所有格式错误和密码学异常都按验签失败处理，不向上层抛出实现细节异常。
        /// </para>
        /// </summary>
        public static bool VerifyManifestSignature(
            byte[] manifestBytes,
            string signatureBase64,
            string publicKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0 ||
                string.IsNullOrWhiteSpace(signatureBase64) ||
                string.IsNullOrWhiteSpace(publicKeyXml))
                return false;

            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64.Trim());
                using (RSA rsa = RSA.Create())
                {
                    rsa.FromXmlString(publicKeyXml.Trim());
                    return rsa.VerifyData(
                        manifestBytes,
                        signature,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                GameLog.Error($"[UpdateSecurity] 清单签名校验异常：{ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 使用 RSA XML 私钥对清单原始字节生成 RSA-SHA256/PKCS#1 v1.5 签名，并返回 Base64 文本。
        /// <para>
        /// 该方法只供 Editor 或 CI 发布进程使用。私钥不得写入 AppConfig、Unity 资源、源码仓库或 Player 包体。
        /// </para>
        /// </summary>
        public static string SignManifest(byte[] manifestBytes, string privateKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0)
                throw new ArgumentException("清单内容不能为空。", nameof(manifestBytes));
            if (string.IsNullOrWhiteSpace(privateKeyXml))
                throw new ArgumentException("签名私钥不能为空。", nameof(privateKeyXml));

            using (RSA rsa = RSA.Create())
            {
                rsa.FromXmlString(privateKeyXml.Trim());
                byte[] signature = rsa.SignData(
                    manifestBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(signature);
            }
        }

        /// <summary>
        /// 对已经通过原始字节验签的清单执行字段级安全准入。
        /// <para>
        /// 本方法不能代替签名验证；它只验证可信清单内部是否符合当前客户端的协议、时效、平台、渠道和版本边界。
        /// 调用顺序必须是：下载原始字节 → 按 KeyId 选择可信公钥 → 验签 → 反序列化 → 调用本方法。
        /// </para>
        /// </summary>
        /// <param name="server">服务端清单。</param>
        /// <param name="local">当前客户端已确认的本地版本事实。</param>
        /// <param name="appEnv">当前应用环境。</param>
        /// <param name="expectedChannel">当前客户端发行渠道。</param>
        /// <param name="rejectReason">失败时返回明确拒绝原因。</param>
        /// <param name="nowUnixSeconds">测试可注入的当前 Unix 秒；运行时为空则使用 UTC 系统时间。</param>
        /// <returns>清单满足全部准入规则时返回 <see langword="true"/>。</returns>
        public static bool ValidateManifest(
            UpdateInfo server,
            UpdateInfo local,
            string appEnv,
            string expectedChannel,
            out string rejectReason,
            long? nowUnixSeconds = null)
        {
            rejectReason = null;
            if (server == null)
            {
                rejectReason = "服务端清单为空。";
                return false;
            }
            if (local == null)
            {
                rejectReason = "本地版本基线为空。";
                return false;
            }
            if (server.ManifestVersion != FrameworkRuntimeInfo.UpdateManifestVersion)
            {
                rejectReason = $"清单协议版本 {server.ManifestVersion} 与客户端支持版本 {FrameworkRuntimeInfo.UpdateManifestVersion} 不一致。";
                return false;
            }
            if (!Guid.TryParse(server.ManifestId, out _))
            {
                rejectReason = "清单 ManifestId 不是有效 GUID。";
                return false;
            }
            if (!IsSafeIdentifier(server.KeyId, 64))
            {
                rejectReason = "清单 KeyId 为空或包含不允许的字符。";
                return false;
            }
            if (!VersionManager.TryCompareVersion(server.AppVersion, Application.version, out int appCompare) ||
                server.ResourceVersion < 1 || server.CodeVersion < 1)
            {
                rejectReason = "清单 AppVersion 格式无效，或资源/代码版本号小于 1。";
                return false;
            }
            if (server.GrayPercent < 0 || server.GrayPercent > 100)
            {
                rejectReason = "清单 GrayPercent 必须位于 0～100。";
                return false;
            }

            long now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (server.IssuedAtUnixSeconds <= 0 || server.ExpiresAtUnixSeconds <= 0)
            {
                rejectReason = "清单缺少有效的签发时间或失效时间。";
                return false;
            }
            if (server.IssuedAtUnixSeconds > now + MaxClockSkewSeconds)
            {
                rejectReason = "清单签发时间超出允许的客户端时钟偏差。";
                return false;
            }
            if (server.ExpiresAtUnixSeconds <= server.IssuedAtUnixSeconds)
            {
                rejectReason = "清单失效时间必须晚于签发时间。";
                return false;
            }
            if (server.ExpiresAtUnixSeconds <= now)
            {
                rejectReason = "清单已经失效。";
                return false;
            }
            if (server.ExpiresAtUnixSeconds - server.IssuedAtUnixSeconds > MaxManifestLifetimeSeconds)
            {
                rejectReason = "清单有效期超过允许的 90 天上限。";
                return false;
            }

            string expectedPlatform = GetRuntimePlatformId();
            if (string.IsNullOrWhiteSpace(server.Platform) ||
                !string.Equals(server.Platform, expectedPlatform, StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = $"清单平台不匹配：expected={expectedPlatform}, actual={server.Platform}";
                return false;
            }

            string normalizedChannel = string.IsNullOrWhiteSpace(expectedChannel) ? "default" : expectedChannel.Trim();
            if (!IsSafeIdentifier(server.Channel, 64) ||
                !string.Equals(server.Channel.Trim(), normalizedChannel, StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = $"清单渠道不匹配：expected={normalizedChannel}, actual={server.Channel}";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(server.MinFrameworkVersion))
            {
                if (!VersionManager.TryCompareVersion(
                        FrameworkRuntimeInfo.Version,
                        server.MinFrameworkVersion,
                        out int frameworkCompare))
                {
                    rejectReason = $"清单 MinFrameworkVersion 格式无效：{server.MinFrameworkVersion}";
                    return false;
                }
                if (frameworkCompare < 0)
                {
                    rejectReason = $"当前框架版本 {FrameworkRuntimeInfo.Version} 低于清单要求 {server.MinFrameworkVersion}。";
                    return false;
                }
            }
            if (appCompare < 0)
            {
                rejectReason = $"拒绝整包版本降级清单：installed={Application.version}, manifest={server.AppVersion}";
                return false;
            }

            if (appCompare == 0)
            {
                if (server.ResourceVersion < local.ResourceVersion)
                {
                    rejectReason = $"拒绝资源版本降级：local={local.ResourceVersion}, manifest={server.ResourceVersion}";
                    return false;
                }
                if (server.CodeVersion < local.CodeVersion)
                {
                    rejectReason = $"拒绝代码版本降级：local={local.CodeVersion}, manifest={server.CodeVersion}";
                    return false;
                }
            }

            if (server.PatchFiles == null)
                server.PatchFiles = new System.Collections.Generic.List<PatchFile>();

            if (appCompare == 0 && server.CodeVersion > local.CodeVersion)
            {
                if (server.PatchFiles.Count == 0)
                {
                    rejectReason = "CodeVersion 已增加，但 PatchFiles 为空。";
                    return false;
                }
                if (!ValidateCompleteCodePatchSet(server.PatchFiles, appEnv, out rejectReason))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 校验代码补丁文件描述：只允许受配置白名单约束的安全 .dll.bytes 叶子文件名，并要求 URL、Size 与 SHA-256 完整。
        /// </summary>
        /// <param name="patch">待校验文件描述。</param>
        /// <param name="rejectReason">失败时返回拒绝原因。</param>
        /// <returns>文件描述可进入下载阶段时返回 <see langword="true"/>。</returns>
        public static bool ValidateCodePatchFile(PatchFile patch, out string rejectReason)
        {
            return ValidateCodePatchFile(patch, appEnv: null, out rejectReason);
        }

        /// <summary>
        /// 按指定环境校验单个代码补丁描述。生产环境除完整字段外还强制补丁 URL 使用 HTTPS。
        /// </summary>
        public static bool ValidateCodePatchFile(PatchFile patch, string appEnv, out string rejectReason)
        {
            rejectReason = null;
            if (patch == null)
            {
                rejectReason = "补丁文件描述为空。";
                return false;
            }
            if (!IsSafeLeafFileName(patch.FileName))
            {
                rejectReason = $"补丁文件名不安全：{patch.FileName}";
                return false;
            }
            if (!VersionManager.IsAllowedHotUpdateAssemblyFile(patch.FileName))
            {
                rejectReason = $"补丁程序集不在客户端白名单中：{patch.FileName}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(patch.Url) ||
                !Uri.TryCreate(patch.Url, UriKind.Absolute, out Uri patchUri) ||
                (patchUri.Scheme != Uri.UriSchemeHttp && patchUri.Scheme != Uri.UriSchemeHttps))
            {
                rejectReason = $"补丁文件下载 URL 不是有效的 HTTP(S) 绝对地址：{patch.FileName}";
                return false;
            }
            if (IsProductionEnv(appEnv) &&
                !string.Equals(patchUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = $"生产环境补丁 URL 必须使用 HTTPS：{patch.FileName}";
                return false;
            }
            if (patch.Size <= 0)
            {
                rejectReason = $"补丁 Size 必须大于 0：{patch.FileName}";
                return false;
            }
            if (!IsHexDigest(patch.SHA256, 64))
            {
                rejectReason = $"补丁 SHA-256 不是 64 位十六进制摘要：{patch.FileName}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 校验代码补丁集合是否与客户端配置的热更新程序集白名单完全一致。
        /// <para>
        /// 代码槽必须是完整不可变快照，不能只下发部分 DLL 后再逐文件回退整包基线；混用不同代码版本的程序集会造成 ABI 漂移，
        /// 并使事务提交失去原子性。因此集合必须无重复、无缺失、无额外文件，且每个文件都通过单项安全准入。
        /// </para>
        /// </summary>
        public static bool ValidateCompleteCodePatchSet(
            System.Collections.Generic.IReadOnlyList<PatchFile> patches,
            string appEnv,
            out string rejectReason)
        {
            rejectReason = null;
            if (patches == null || patches.Count == 0)
            {
                rejectReason = "代码补丁集合为空。";
                return false;
            }

            string[] expected = VersionManager.HotUpdateAssemblyFileNames;
            if (expected == null || expected.Length == 0)
            {
                rejectReason = "客户端未配置热更新程序集白名单。";
                return false;
            }

            var expectedSet = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string fileName in expected)
            {
                if (!IsSafeLeafFileName(fileName) || !expectedSet.Add(fileName))
                {
                    rejectReason = $"客户端热更新程序集白名单无效或存在重复：{fileName}";
                    return false;
                }
            }

            var actualSet = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (PatchFile patch in patches)
            {
                if (!ValidateCodePatchFile(patch, appEnv, out rejectReason))
                    return false;
                if (!actualSet.Add(patch.FileName))
                {
                    rejectReason = $"代码补丁集合存在重复文件：{patch.FileName}";
                    return false;
                }
            }

            if (!actualSet.SetEquals(expectedSet))
            {
                string missing = string.Join(",", expectedSet.Where(name => !actualSet.Contains(name)));
                string extra = string.Join(",", actualSet.Where(name => !expectedSet.Contains(name)));
                rejectReason = $"代码补丁集合与客户端白名单不一致：missing=[{missing}] extra=[{extra}]";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 判断文件名是否为可安全拼接到受控安装目录下的程序集叶子文件名。
        /// <para>
        /// 拒绝目录分隔符、盘符、<c>..</c>、平台非法字符和非 .dll.bytes 后缀，防止目录穿越与任意文件覆盖。
        /// </para>
        /// </summary>
        public static bool IsSafeLeafFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)) return false;
            if (fileName.Contains("..") || fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return fileName.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 校验客户端内置清单验签公钥配置：KeyId 必须安全且唯一，RSA XML 必须可解析并且只包含公开参数。
        /// </summary>
        public static bool ValidatePublicKeyConfiguration(
            string legacyPublicKey,
            UpdateManifestPublicKeyEntry[] keyRing,
            out string rejectReason)
        {
            rejectReason = null;
            bool hasRing = keyRing != null && keyRing.Length > 0;
            if (hasRing)
            {
                var keyIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (UpdateManifestPublicKeyEntry entry in keyRing)
                {
                    if (entry == null || !IsSafeIdentifier(entry.KeyId, 64))
                    {
                        rejectReason = "清单验签公钥环包含空条目或非法 KeyId。";
                        return false;
                    }
                    if (!keyIds.Add(entry.KeyId))
                    {
                        rejectReason = $"清单验签公钥环存在重复 KeyId：{entry.KeyId}";
                        return false;
                    }
                    if (!ValidatePublicKeyXml(entry.PublicKeyXml, out rejectReason))
                    {
                        rejectReason = $"KeyId={entry.KeyId} 的公钥无效：{rejectReason}";
                        return false;
                    }
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(legacyPublicKey))
            {
                rejectReason = "未配置任何清单验签公钥。";
                return false;
            }
            return ValidatePublicKeyXml(legacyPublicKey, out rejectReason);
        }

        /// <summary>
        /// 验证 RSA XML 可导入且不含私钥参数，防止发布私钥被误写入客户端资源和源码仓库。
        /// </summary>
        private static bool ValidatePublicKeyXml(string publicKeyXml, out string rejectReason)
        {
            rejectReason = null;
            if (string.IsNullOrWhiteSpace(publicKeyXml))
            {
                rejectReason = "RSA XML 为空。";
                return false;
            }
            try
            {
                using (RSA rsa = RSA.Create())
                {
                    string xml = publicKeyXml.Trim();
                    if (xml.IndexOf("<D>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        xml.IndexOf("<P>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        xml.IndexOf("<Q>", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        rejectReason = "配置中包含 RSA 私钥参数，严禁进入客户端包体。";
                        return false;
                    }
                    rsa.FromXmlString(xml);
                    RSAParameters parameters = rsa.ExportParameters(false);
                    if (parameters.Modulus == null || parameters.Exponent == null)
                    {
                        rejectReason = "RSA 公钥缺少 Modulus 或 Exponent。";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                rejectReason = $"RSA XML 无法解析：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 判断配置中是否至少存在一把可用于清单验签的公钥；兼容旧单公钥字段和新 KeyId 公钥环。
        /// </summary>
        public static bool HasUsablePublicKey(string legacyPublicKey, UpdateManifestPublicKeyEntry[] keyRing)
        {
            if (!string.IsNullOrWhiteSpace(legacyPublicKey)) return true;
            return keyRing != null && keyRing.Any(entry =>
                entry != null &&
                !string.IsNullOrWhiteSpace(entry.KeyId) &&
                !string.IsNullOrWhiteSpace(entry.PublicKeyXml));
        }

        /// <summary>
        /// 按清单 KeyId 从公钥环精确选择公钥；未命中时仅为旧项目迁移回退到旧单公钥字段。
        /// </summary>
        /// <param name="keyId">清单声明的签名密钥标识。</param>
        /// <param name="legacyPublicKey">旧版无 KeyId 单公钥。</param>
        /// <param name="keyRing">支持轮换的公钥环。</param>
        /// <returns>可用 RSA XML 公钥；不存在时返回 <see langword="null"/>。</returns>
        public static string ResolvePublicKey(
            string keyId,
            string legacyPublicKey,
            UpdateManifestPublicKeyEntry[] keyRing)
        {
            bool hasKeyRing = keyRing != null && keyRing.Any(entry =>
                entry != null && !string.IsNullOrWhiteSpace(entry.KeyId) && !string.IsNullOrWhiteSpace(entry.PublicKeyXml));
            if (hasKeyRing)
            {
                if (string.IsNullOrWhiteSpace(keyId)) return null;
                UpdateManifestPublicKeyEntry match = keyRing.FirstOrDefault(entry =>
                    entry != null && string.Equals(entry.KeyId, keyId, StringComparison.Ordinal));
                return match != null && !string.IsNullOrWhiteSpace(match.PublicKeyXml)
                    ? match.PublicKeyXml
                    : null;
            }

            return string.IsNullOrWhiteSpace(legacyPublicKey) ? null : legacyPublicKey;
        }

        /// <summary>
        /// 将 Unity RuntimePlatform 映射为发布清单使用的稳定小写平台标识。
        /// </summary>
        public static string GetRuntimePlatformId()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android: return "android";
                case RuntimePlatform.IPhonePlayer: return "ios";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor: return "windows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor: return "macos";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor: return "linux";
                case RuntimePlatform.WebGLPlayer: return "webgl";
                default: return Application.platform.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// 校验 KeyId、Channel 等清单路由标识，仅允许字母、数字、点、下划线和连字符。
        /// </summary>
        public static bool IsSafeManifestIdentifier(string value, int maxLength = 64) =>
            IsSafeIdentifier(value, maxLength);

        /// <summary>
        /// 校验用于 KeyId、Channel 等路由字段的稳定标识，仅允许字母、数字、点、下划线和连字符。
        /// </summary>
        private static bool IsSafeIdentifier(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
                return false;
            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 校验字符串是否为指定长度的纯十六进制摘要，不接受分隔符或算法前缀。
        /// </summary>
        private static bool IsHexDigest(string value, int expectedLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != expectedLength) return false;
            foreach (char c in value.Trim())
            {
                bool hex = c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
                if (!hex) return false;
            }
            return true;
        }
    }
}
