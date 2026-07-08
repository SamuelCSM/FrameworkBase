using System.IO;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布环境配置的文件加载与"当前活动环境"选择。
    ///
    /// 约定：profile 文件位于<b>工程根</b> <c>ReleaseProfiles/{env}.json</c>（在 Assets 之外，不走 Unity 资源导入，无 .meta）；
    /// 当前发布机选中的环境存 EditorPrefs（机器级，不进库）。
    /// </summary>
    public static class ReleaseProfileStore
    {
        /// <summary>"当前活动发布环境"的 EditorPrefs 键（机器级配置）。</summary>
        public const string ActiveEnvPrefsKey = "FrameworkBase.Release.ActiveEnv";

        /// <summary>内置的标准发布环境集合，用于窗口下拉。</summary>
        public static readonly string[] KnownEnvironments = { "dev", "qa", "staging", "prod" };

        /// <summary>工程根下的 ReleaseProfiles 目录（Assets 同级）。</summary>
        public static string ProfilesDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ReleaseProfiles");

        /// <summary>当前发布机选中的环境；缺省 dev。</summary>
        public static string ActiveEnv
        {
            get => EditorPrefs.GetString(ActiveEnvPrefsKey, "dev");
            set => EditorPrefs.SetString(ActiveEnvPrefsKey, string.IsNullOrEmpty(value) ? "dev" : value);
        }

        /// <summary>指定环境 profile 文件的绝对路径。</summary>
        public static string PathFor(string env) => Path.Combine(ProfilesDir, $"{env}.json");

        /// <summary>
        /// 加载指定环境的 profile。文件缺失 / 解析失败返回 null 并输出原因，不抛出。
        /// </summary>
        public static ReleaseProfile TryLoad(string env, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(env))
            {
                error = "环境名为空";
                return null;
            }

            string path = PathFor(env);
            if (!File.Exists(path))
            {
                error = $"未找到发布环境配置：{path}";
                return null;
            }

            return ReleaseProfile.FromJson(File.ReadAllText(path), out error);
        }

        /// <summary>
        /// 加载并校验当前活动环境（发布工具收口入口）：文件缺失、解析失败、环境未达标（prod 明文 /
        /// 要求签名却无私钥）都会在 <paramref name="report"/> 中给出，仅在 <paramref name="profile"/>
        /// 非空且校验通过时返回 true。
        /// </summary>
        public static bool TryResolveActive(out ReleaseProfile profile, out string report)
        {
            profile = TryLoad(ActiveEnv, out string loadError);
            if (profile == null)
            {
                report = loadError;
                return false;
            }

            return ReleaseProfileGate.Validate(profile, UpdateManifestSigner.HasUsablePrivateKey, out report);
        }
    }
}
