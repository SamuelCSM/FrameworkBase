using System;
using System.Collections.Generic;
using Framework.HotUpdate;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// version.json 的<b>唯一</b>序列化入口（发布契约，阶段一）。
    ///
    /// 契约对称：发布端序列化与客户端反序列化用同一个类型 <see cref="UpdateInfo"/>、
    /// 同一个序列化器（JsonUtility，见 Framework.Serialization.UnityJsonSerializer）——
    /// 字段增删只改 UpdateInfo 一处，发布端自动跟上，不再出现"运行时有 GrayPercent/UpdateUrl
    /// 字段、发布工具却写不出来"的漂移。任何发布工具禁止再手搓 version.json 字符串。
    /// </summary>
    public static class ReleaseManifestWriter
    {
        /// <summary>
        /// 序列化发布清单。会先做契约校验与规范化（AppVersion 必填；PatchFiles null 归一为空列表，
        /// 不依赖 JsonUtility 对 null 列表的隐式行为）。
        /// </summary>
        /// <param name="manifest">发布清单；PatchFiles 为 null 时会被就地替换为空列表。</param>
        /// <returns>带缩进的 JSON 文本。</returns>
        public static string ToJson(UpdateInfo manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            if (string.IsNullOrWhiteSpace(manifest.AppVersion))
                throw new ArgumentException("发布清单缺少 AppVersion", nameof(manifest));

            if (manifest.PatchFiles == null)
                manifest.PatchFiles = new List<PatchFile>();

            return JsonUtility.ToJson(manifest, prettyPrint: true);
        }
    }
}
