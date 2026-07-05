using Framework.HotUpdate;

namespace Framework.Core
{
    /// <summary>
    /// 版本号展示格式统一入口，避免 Loading / Login 两处文案不一致。
    /// </summary>
    public static class VersionDisplayHelper
    {
        /// <summary>格式化为左下角版本行文案。</summary>
        public static string Format(UpdateInfo version)
        {
            if (version == null)
                return string.Empty;

            return $"v{version.AppVersion}  |  Res.{version.ResourceVersion}  |  Code.{version.CodeVersion}";
        }

        /// <summary>读取本地版本并格式化。</summary>
        public static string FormatLocal()
        {
            return Format(VersionManager.GetLocalVersion());
        }
    }
}
