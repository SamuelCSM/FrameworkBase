using System.Collections.Generic;
using Framework.HotUpdate;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布前环境校验：把传输协议、全环境强制签名和私钥可用性从人脑记忆变成自动化阻断规则。
    /// 纯逻辑（不读文件、不碰 EditorPrefs），便于单测；文件加载与私钥探测由 <see cref="ReleaseProfileStore"/> 收口后传入。
    /// </summary>
    public static class ReleaseProfileGate
    {
        /// <summary>
        /// 校验发布环境是否满足放行条件。所有不达标项汇总进 <paramref name="report"/>，全部通过才返回 true。
        /// </summary>
        /// <param name="profile">目标发布环境配置。</param>
        /// <param name="hasUsablePrivateKey">发布机当前是否已登记可用的签名私钥（<see cref="UpdateManifestSigner.HasUsablePrivateKey"/>）。</param>
        /// <param name="report">校验报告（多行），无论通过与否都会填充。</param>
        /// <returns>无阻断项返回 true。</returns>
        public static bool Validate(ReleaseProfile profile, bool hasUsablePrivateKey, out string report)
        {
            var issues = new List<string>();
            var notes = new List<string>();

            if (profile == null)
            {
                report = "发布环境配置为空（未找到对应 ReleaseProfiles/{env}.json 或解析失败）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
                issues.Add("profile 缺少 Name");

            if (string.IsNullOrWhiteSpace(profile.BaseUrl))
            {
                issues.Add("profile 缺少 BaseUrl（该环境客户端更新服务器根 URL）");
            }
            else
            {
                // 复用运行时同一套 URL 准入（prod 强制 HTTPS、非法 URL / 非 http(s) 协议拒绝）。
                if (!UpdateSecurity.ValidateUpdateServerUrl(profile.BaseUrl, profile.Name, out string urlReason))
                    issues.Add(urlReason);

                // profile 显式要求 HTTPS 时（如 staging），即便不是 prod 也强制。
                if (profile.RequireHttps &&
                    !profile.BaseUrl.TrimStart().StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
                    issues.Add($"环境 {profile.Name} 要求 HTTPS，但 BaseUrl 非 https：{profile.BaseUrl}");
            }

            if (!profile.AllowPlayerPrefsOverride && string.IsNullOrWhiteSpace(profile.UploadRoot))
                issues.Add($"环境 {profile.Name} 必须通过发布机配置或 -uploadRoot 提供部署目标根目录。");
            if (!profile.AllowPlayerPrefsOverride &&
                !string.IsNullOrWhiteSpace(profile.BaseUrl) &&
                profile.BaseUrl.IndexOf("example.com", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                issues.Add($"环境 {profile.Name} 的 BaseUrl 仍是 example.com 占位地址，禁止正式发布。");
            }

            // 运行时对所有远程清单强制验签，因此任一发布环境关闭签名都会产出客户端必然拒绝的无效清单。
            if (!profile.RequireManifestSignature)
                issues.Add($"环境 {profile.Name} 必须启用 RequireManifestSignature；开发环境也应使用独立开发密钥。");
            if (!UpdateSecurity.IsSafeManifestIdentifier(profile.SigningKeyRef))
                issues.Add($"环境 {profile.Name} 的 SigningKeyRef 为空或包含非法字符，无法作为稳定 KeyId。");
            if (!hasUsablePrivateKey)
            {
                issues.Add($"环境 {profile.Name} 未配置可用签名私钥。" +
                           "CI 请注入 FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 或 FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_PATH。");
            }
            else if (!string.IsNullOrWhiteSpace(profile.SigningKeyRef))
            {
                notes.Add($"签名私钥引用：{profile.SigningKeyRef}（请确认注入密钥与该 KeyId 对应）");
            }

            report = BuildReport(profile, issues, notes);
            return issues.Count == 0;
        }

        private static string BuildReport(ReleaseProfile profile, List<string> issues, List<string> notes)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"发布环境 [{profile.Name}] BaseUrl={profile.BaseUrl} " +
                      $"RequireHttps={profile.RequireHttps} RequireSignature={profile.RequireManifestSignature}");

            if (issues.Count > 0)
            {
                sb.Append("\n阻断项：");
                foreach (string issue in issues)
                    sb.Append("\n  - ").Append(issue);
            }

            foreach (string note in notes)
                sb.Append("\n  · ").Append(note);

            return sb.ToString();
        }
    }
}
