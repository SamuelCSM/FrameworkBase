using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Framework.HotUpdate;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 热更清单签名工具（Editor / CI 侧）。
    ///
    /// 职责：
    ///   1. 生成 RSA 密钥对 —— 私钥（XML）保存在<b>工程目录外</b>（发布机本地），公钥（XML）粘贴进
    ///      AppConfig.UpdateManifestPublicKey 随包分发；
    ///   2. 发布时对 version.json 签名，生成伴生 version.json.sig（Base64 RSA-SHA256）；
    ///   3. 私钥路径存 EditorPrefs（按机器隔离，不进版本库）。
    ///
    /// 客户端验签逻辑见 <see cref="UpdateSecurity.VerifyManifestSignature"/>。
    /// 密钥采用 .NET RSA XML 格式（工程 API 级别为 .NET Framework 4.x，PEM 导入 API 不可用）。
    /// </summary>
    public static class UpdateManifestSigner
    {
        /// <summary>私钥文件路径的 EditorPrefs 键（机器级配置，不进仓库）。</summary>
        private const string PrivateKeyPathPrefsKey = "FrameworkBase.HotUpdate.ManifestPrivateKeyPath";

        /// <summary>当前配置的私钥路径；未配置返回空字符串。</summary>
        public static string PrivateKeyPath
        {
            get => EditorPrefs.GetString(PrivateKeyPathPrefsKey, string.Empty);
            set => EditorPrefs.SetString(PrivateKeyPathPrefsKey, value ?? string.Empty);
        }

        /// <summary>是否已配置可用的私钥（路径非空且文件存在）。</summary>
        public static bool HasUsablePrivateKey =>
            !string.IsNullOrEmpty(PrivateKeyPath) && File.Exists(PrivateKeyPath);

        // ── 菜单 ──────────────────────────────────────────────────────────────

        [MenuItem("Framework/Hot Update Security/Generate Signing Key Pair...")]
        public static void GenerateKeyPairMenu()
        {
            string privateKeyPath = EditorUtility.SaveFilePanel(
                "保存清单签名私钥（务必放在工程目录外，且勿提交版本库）",
                Directory.GetParent(Application.dataPath).Parent?.FullName ?? "",
                "hotupdate_manifest_private", "xml");

            if (string.IsNullOrEmpty(privateKeyPath))
                return;

            if (IsInsideProject(privateKeyPath) &&
                !EditorUtility.DisplayDialog("私钥位置警告",
                    "所选路径在 Unity 工程目录内，私钥可能被误提交进版本库或打进包体。\n仍要继续吗？",
                    "仍然保存", "重新选择"))
            {
                GenerateKeyPairMenu();
                return;
            }

            GenerateKeyPair(privateKeyPath, out string publicKeyXml);
            PrivateKeyPath = privateKeyPath;

            EditorGUIUtility.systemCopyBuffer = publicKeyXml;
            Debug.Log($"[UpdateManifestSigner] 密钥对已生成。\n私钥: {privateKeyPath}（已登记到 EditorPrefs，发布时自动签名）\n" +
                      $"公钥已复制到剪贴板，请粘贴到 Resources/AppConfig.asset 的 UpdateManifestPublicKey 字段:\n{publicKeyXml}");
            EditorUtility.DisplayDialog("密钥对已生成",
                "私钥已保存并登记到本机 EditorPrefs。\n\n公钥（XML）已复制到剪贴板，" +
                "请粘贴到 AppConfig.asset 的 UpdateManifestPublicKey 字段。", "好");
        }

        [MenuItem("Framework/Hot Update Security/Set Private Key Path...")]
        public static void SetPrivateKeyPathMenu()
        {
            string path = EditorUtility.OpenFilePanel("选择清单签名私钥（RSA XML）",
                Path.GetDirectoryName(PrivateKeyPath), "xml");
            if (string.IsNullOrEmpty(path))
                return;

            PrivateKeyPath = path;
            Debug.Log($"[UpdateManifestSigner] 私钥路径已更新: {path}");
        }

        [MenuItem("Framework/Hot Update Security/Sign version.json...")]
        public static void SignManifestMenu()
        {
            string manifestPath = EditorUtility.OpenFilePanel("选择要签名的 version.json",
                Directory.GetParent(Application.dataPath).FullName, "json");
            if (string.IsNullOrEmpty(manifestPath))
                return;

            if (TrySignManifest(manifestPath, out string error))
                Debug.Log($"[UpdateManifestSigner] 签名完成: {manifestPath}{UpdateSecurity.ManifestSignatureSuffix}");
            else
                Debug.LogError($"[UpdateManifestSigner] 签名失败: {error}");
        }

        // ── 核心 API（发布工具调用）───────────────────────────────────────────

        /// <summary>
        /// 生成 RSA-2048 密钥对：私钥（XML，含私钥参数）写入指定路径，公钥（XML）经出参返回。
        /// </summary>
        public static void GenerateKeyPair(string privateKeyPath, out string publicKeyXml)
        {
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                string privateKeyXml = rsa.ToXmlString(includePrivateParameters: true);
                publicKeyXml = rsa.ToXmlString(includePrivateParameters: false);
                File.WriteAllText(privateKeyPath, privateKeyXml, new UTF8Encoding(false));
            }
        }

        /// <summary>
        /// 对指定 version.json 生成伴生签名文件（同目录 version.json.sig）。
        /// 私钥取自 EditorPrefs 登记路径；未配置私钥时返回 false（由调用方决定告警级别）。
        /// </summary>
        /// <param name="manifestPath">version.json 完整路径。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        public static bool TrySignManifest(string manifestPath, out string error)
        {
            error = null;

            if (!File.Exists(manifestPath))
            {
                error = $"清单文件不存在: {manifestPath}";
                return false;
            }

            if (!HasUsablePrivateKey)
            {
                error = "未配置签名私钥（菜单 Framework → Hot Update Security → Generate Signing Key Pair / Set Private Key Path）";
                return false;
            }

            try
            {
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                string privateKeyXml = File.ReadAllText(PrivateKeyPath);
                string signature = UpdateSecurity.SignManifest(manifestBytes, privateKeyXml);
                File.WriteAllText(manifestPath + UpdateSecurity.ManifestSignatureSuffix, signature, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 发布工具收口：签名成功记日志，失败按"是否强制"告警。
        /// 未配置私钥时开发期只警告不阻断；正式发布流程应把 required 置 true。
        /// </summary>
        public static bool SignManifestForPublish(string manifestPath, Action<string> log, bool required = false)
        {
            if (TrySignManifest(manifestPath, out string error))
            {
                log?.Invoke($"清单已签名: {Path.GetFileName(manifestPath)}{UpdateSecurity.ManifestSignatureSuffix}");
                return true;
            }

            string message = $"清单未签名（{error}）。客户端 AppConfig 配置了验签公钥时将拒绝此清单。";
            if (required)
            {
                log?.Invoke($"[错误] {message}");
                return false;
            }

            log?.Invoke($"[警告] {message}");
            return true;
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

        /// <summary>路径是否位于当前 Unity 工程目录内。</summary>
        private static bool IsInsideProject(string path)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName
                .Replace('\\', '/').TrimEnd('/');
            return Path.GetFullPath(path).Replace('\\', '/')
                .StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
