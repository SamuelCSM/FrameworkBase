using System;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布环境配置（dev / qa / staging / prod）。发布机据此决定"发到哪、走不走 HTTPS、要不要签名"。
    ///
    /// 存放约定（工程根 <c>ReleaseProfiles/{name}.json</c>，JsonUtility PascalCase 字段）：
    ///   - 非敏感的环境定义（BaseUrl / RequireHttps / 签名策略）<b>进 Git</b>，团队共享同一套；
    ///   - 机器相关值（<see cref="UploadRoot"/> 部署根路径）留空或占位，由发布机本地覆盖；
    ///   - 私钥<b>绝不进库</b>：这里只放引用名 <see cref="SigningKeyRef"/>，真正的私钥路径存本机
    ///     EditorPrefs（见 <see cref="UpdateManifestSigner"/>）。
    /// </summary>
    [Serializable]
    public class ReleaseProfile
    {
        /// <summary>环境名：dev / qa / staging / prod。须与文件名一致。</summary>
        public string Name;

        /// <summary>该环境客户端使用的更新服务器根 URL（对应运行时 AppConfig.UpdateServerUrl）。</summary>
        public string BaseUrl;

        /// <summary>
        /// 部署产物上传/写入的目标根（本地目录 / 静态服务器目录 / CDN staging 目录）。
        /// 机器相关，通常留空或占位，由发布机本地填写；不作为团队共享的权威值。
        /// </summary>
        public string UploadRoot;

        /// <summary>是否强制该环境的 BaseUrl 走 HTTPS。prod 恒为 true（明文链路等于开放 RCE 入口）。</summary>
        public bool RequireHttps;

        /// <summary>是否要求本次发布对 version.json 签名；true 时未配置可用私钥将<b>阻断发布</b>。</summary>
        public bool RequireManifestSignature;

        /// <summary>签名私钥的<b>引用名</b>（非私钥本体），仅供人工核对当前发布机登记的是否为该环境密钥。</summary>
        public string SigningKeyRef;

        /// <summary>是否允许该环境用 EditorPrefs / 本地覆盖发布参数（正式环境应关闭以防误发）。</summary>
        public bool AllowPlayerPrefsOverride;

        /// <summary>
        /// 解析 profile JSON（JsonUtility，PascalCase 字段）。空串或非法 JSON 返回 null 并输出原因，不抛出。
        /// </summary>
        public static ReleaseProfile FromJson(string json, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "profile JSON 为空";
                return null;
            }

            try
            {
                var profile = JsonUtility.FromJson<ReleaseProfile>(json);
                if (profile == null)
                    error = "profile JSON 解析结果为空";
                return profile;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }
}
